using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 通过左右履带的接地点向动态底盘施力，模拟简化差速行驶。
/// 地面支撑由原有碰撞体承担；本组件不修改位置，也不模拟逐节履带。
/// </summary>
[DefaultExecutionOrder(-50)]
[DisallowMultipleComponent]
public class Zx200TrackDrive : MonoBehaviour
{
    [Header("底盘引用")]
    public Zx200KeyboardController keyboard;
    public ArticulationBody rootBody;

    [Header("牵引与制动")]
    [Min(0.01f)] public float responseTime = 0.4f;
    [Min(0f)] public float maxAcceleration = 3f;
    [Tooltip("左右履带指令速度的变化上限；起步与反向先渐变，而非瞬间给满牵引。")]
    [Min(0.01f)] public float trackSpeedSlewRate = 1f;
    [Min(0f)] public float lateralGrip = 2f;
    [Tooltip("无转向输入时，以有限左右履带牵引差纠正坡面造成的偏航；不写入底盘旋转。")]
    [Min(0f)] public float headingHoldGain = 2f;
    [Min(0f)] public float maximumHeadingCorrectionSpeed = .5f;
    [Tooltip("转向输入拉满时使用完整履带速度进行原地差速转向；关闭后采用 turnSpeed 限制。")]
    public bool fullSpeedPivotTurn = true;
    [Range(0f, 1f)] public float tractionCoefficient = 0.8f;
    [Min(0.01f)] public float commandTimeout = 0.25f;
    [Range(0f, 1f)] public float parkingFriction = 0.9f;

    [Header("接地检测")]
    [Min(0.01f)] public float probeHeight = 0.35f;
    [Min(0.001f)] public float contactTolerance = 0.18f;
    [Range(0f, 80f)] public float maxGroundAngle = 45f;
    [Tooltip("接地时增加角阻尼以削弱土面小起伏引起的弹跳；不锁定底盘姿态。")]
    [Min(0f)] public float groundedAngularDamping = 6f;

    /// <summary>获取当前物理步实际检测到的有效履带接地点数量。</summary>
    public int GroundContactCount { get; private set; }

    private readonly List<Collider> trackColliders = new List<Collider>();
    private readonly List<PhysicsMaterial> originalMaterials = new List<PhysicsMaterial>();
    private ArticulationBody[] bodies;
    private Bounds localBounds;
    private PhysicsMaterial trackMaterial;
    private PhysicsMaterial parkingMaterial;
    private bool initialized;
    private float forwardInput;
    /// <summary>Most recent normalized forward command, exposed for control handoff checks.</summary>
    public float ForwardInput => forwardInput;
    private float steeringInput;
    private float commandTime = float.NegativeInfinity;
    private readonly Vector3[] filteredNormals = new Vector3[6];
    private readonly bool[] hasFilteredContacts = new bool[6];
    private readonly RaycastHit[] contacts = new RaycastHit[6];
    private readonly float[] contactTargetSpeeds = new float[6];
    private readonly float[] trackTargetSpeeds = new float[2];
    private readonly int[] trackContactCounts = new int[2];
    private readonly int[] contactTracks = new int[6];
    private float originalAngularDamping;
    private Vector3 driveHeading;
    private bool hasDriveHeading;

    private void Awake()
    {
        Initialize();
    }

    /// <summary>提交归一化行驶指令，由后续物理步执行；无效数值按停止处理。</summary>
    /// <param name="forward">前后输入，范围为 -1 到 1。</param>
    /// <param name="steering">左右输入，正数右转，范围为 -1 到 1。</param>
    public void SetInput(float forward, float steering)
    {
        forwardInput = SanitizeInput(forward);
        steeringInput = SanitizeInput(steering);
        commandTime = Time.time;
    }

    /// <summary>清除行驶请求，接地时通过有限牵引力制动。</summary>
    public void Stop()
    {
        forwardInput = 0f;
        steeringInput = 0f;
        commandTime = float.NegativeInfinity;
    }

    private bool Initialize()
    {
        if (initialized) return true;
        if (keyboard == null) keyboard = GetComponent<Zx200KeyboardController>();
        if (keyboard == null || !keyboard.ResolveArticulationRoot()) return false;
        rootBody = keyboard.articulationRoot;
        originalAngularDamping = rootBody.angularDamping;
        bodies = GetComponentsInChildren<ArticulationBody>(true);
        foreach (Collider collider in rootBody.GetComponentsInChildren<Collider>())
        {
            if (collider.isTrigger || collider.GetComponentInParent<ArticulationBody>() != rootBody) continue;
            trackColliders.Add(collider);
            originalMaterials.Add(collider.sharedMaterial);
            // 将碰撞网格的局部包围盒转换到履带坐标，不把上车体和机械臂计入接地范围。
            Bounds bounds = collider is MeshCollider mesh && mesh.sharedMesh != null
                ? mesh.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            if (collider is BoxCollider box) bounds = new Bounds(box.center, box.size);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 point = rootBody.transform.InverseTransformPoint(collider.transform.TransformPoint(corner));
                if (trackColliders.Count == 1 && i == 0) localBounds = new Bounds(point, Vector3.zero);
                else localBounds.Encapsulate(point);
            }
        }
        if (trackColliders.Count == 0) return false;
        // 普通各向同性摩擦会抵消履带驱动；低摩擦接触配合本组件的切向抓地力。
        trackMaterial = new PhysicsMaterial("ZX200 履带接触")
        {
            staticFriction = 0.02f,
            dynamicFriction = 0.02f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounciness = 0f,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
        // 停车时恢复静摩擦约束，抵抗上车回转的反作用力矩；不锁定动态底盘。
        parkingMaterial = new PhysicsMaterial("ZX200 停车接触")
        {
            staticFriction = parkingFriction,
            dynamicFriction = parkingFriction,
            frictionCombine = PhysicsMaterialCombine.Maximum,
            bounciness = 0f,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
        initialized = true;
        if (isActiveAndEnabled)
            ApplyContactMaterial(parkingMaterial);
        return true;
    }

    private void OnEnable()
    {
        if (!Initialize()) return;
        ApplyContactMaterial(parkingMaterial);
    }

    private void FixedUpdate()
    {
        GroundContactCount = 0;
        System.Array.Clear(trackContactCounts, 0, trackContactCounts.Length);
        if (!Initialize()) return;
        bool fixedBase = keyboard.fixedBase;
        if (rootBody.immovable != fixedBase)
        {
            rootBody.immovable = fixedBase;
            rootBody.useGravity = !fixedBase;
            Stop();
        }
        if (fixedBase)
        {
            System.Array.Clear(trackTargetSpeeds, 0, trackTargetSpeeds.Length);
            hasDriveHeading = false;
            return;
        }
        float forward = Time.time - commandTime <= commandTimeout ? forwardInput : 0f;
        float steering = Time.time - commandTime <= commandTimeout ? steeringInput : 0f;
        // Do not switch to high-friction parking while a heavy chassis is still braking.
        bool driving = Mathf.Abs(forward) > 0.001f || Mathf.Abs(steering) > 0.001f ||
            Vector3.ProjectOnPlane(rootBody.linearVelocity, Vector3.up).magnitude > .15f ||
            Mathf.Abs(trackTargetSpeeds[0]) > .01f || Mathf.Abs(trackTargetSpeeds[1]) > .01f;
        ApplyContactMaterial(driving ? trackMaterial : parkingMaterial);
        float mass = 0f;
        foreach (ArticulationBody body in bodies)
            if (body != null && body.gameObject.activeInHierarchy) mass += body.mass;
        float halfWidth = localBounds.size.x * 0.35f;
        float headingCorrection = 0f;
        // Keep the finite straight-line correction during gradual braking: releasing throttle
        // must not remove yaw control while the tracks are still applying braking forces on a slope.
        if (Mathf.Abs(steering) <= .001f &&
            (Mathf.Abs(forward) > .001f || (hasDriveHeading && driving)))
        {
            Vector3 heading = Vector3.ProjectOnPlane(rootBody.transform.forward, Vector3.up).normalized;
            if (!hasDriveHeading)
            {
                driveHeading = heading;
                hasDriveHeading = true;
            }
            float yawError = Vector3.SignedAngle(heading, driveHeading, Vector3.up) * Mathf.Deg2Rad;
            float desiredYawRate = Mathf.Clamp(yawError * headingHoldGain,
                -keyboard.turnSpeed * Mathf.Deg2Rad, keyboard.turnSpeed * Mathf.Deg2Rad);
            headingCorrection = Mathf.Clamp((desiredYawRate - Vector3.Dot(rootBody.angularVelocity, Vector3.up)) * halfWidth,
                -maximumHeadingCorrectionSpeed, maximumHeadingCorrectionSpeed);
        }
        else hasDriveHeading = false;
        for (int side = -1; side <= 1; side += 2)
        {
            // 右转时左履带更快；沿本地 +X 的右履带相应减速。
            float turnTrackSpeed = fullSpeedPivotTurn ? keyboard.moveSpeed :
                Mathf.Min(keyboard.moveSpeed, keyboard.turnSpeed * Mathf.Deg2Rad * halfWidth);
            // 保留左右履带输入的和差关系；满舵原地反转，复合输入逐侧限速。
            float turningSpeed = steering * turnTrackSpeed;
            float requestedSpeed = Mathf.Clamp(forward * keyboard.moveSpeed - side * turningSpeed,
                -keyboard.moveSpeed, keyboard.moveSpeed);
            int trackIndex = (side + 1) / 2;
            trackTargetSpeeds[trackIndex] = Mathf.MoveTowards(trackTargetSpeeds[trackIndex], requestedSpeed,
                trackSpeedSlewRate * Time.fixedDeltaTime);
            float targetSpeed = trackTargetSpeeds[trackIndex];
            for (int sample = -1; sample <= 1; sample++)
            {
                Vector3 local = new Vector3(localBounds.center.x + side * halfWidth,
                    localBounds.min.y, localBounds.center.z + sample * localBounds.size.z * 0.35f);
                int index = (side + 1) / 2 * 3 + sample + 1;
                if (!TryFindContact(local, index, out RaycastHit contact)) continue;
                contacts[GroundContactCount] = contact;
                contactTargetSpeeds[GroundContactCount] = targetSpeed;
                contactTracks[GroundContactCount] = trackIndex;
                trackContactCounts[trackIndex]++;
                GroundContactCount++;
            }
        }
        rootBody.angularDamping = GroundContactCount > 0
            ? Mathf.Max(originalAngularDamping, groundedAngularDamping) : originalAngularDamping;
        // Equal throttle must not create unequal left/right thrust just because one track has fewer probes.
        // Contacts on each supported track share its half of the chassis load. Fewer probes do not
        // mean the physical chassis has lost weight; one point remains capped at half the total load.
        // An airborne chassis has no valid contacts and receives no drive or gravity compensation.
        if (GroundContactCount == 0) return;
        float trackMass = mass * .5f;
        for (int i = 0; i < GroundContactCount; i++)
            ApplyContact(contacts[i], contactTargetSpeeds[i], trackMass / trackContactCounts[contactTracks[i]],
                (1 - 2 * contactTracks[i]) * headingCorrection / Mathf.Max(responseTime, Time.fixedDeltaTime));
    }

    private bool TryFindContact(Vector3 localPoint, int contactIndex, out RaycastHit nearest)
    {
        Transform root = rootBody.transform;
        Vector3 point = root.TransformPoint(localPoint);
        Vector3 up = root.up;
        nearest = default;
        float distance = float.PositiveInfinity;
        foreach (RaycastHit hit in Physics.RaycastAll(point + up * probeHeight, -up,
                     probeHeight + contactTolerance, keyboard.groundLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider.transform.IsChildOf(transform) || hit.distance >= distance) continue;
            if (Vector3.Angle(hit.normal, Vector3.up) > maxGroundAngle) continue;
            nearest = hit;
            distance = hit.distance;
        }
        if (float.IsPositiveInfinity(distance))
        {
            hasFilteredContacts[contactIndex] = false;
            return false;
        }
        float smoothing = 1f - Mathf.Exp(-Time.fixedDeltaTime / .12f);
        // 每个履带采样点独立滤波。施力点始终使用真实接地点，保留左右差速的力臂。
        filteredNormals[contactIndex] = hasFilteredContacts[contactIndex]
            ? Vector3.Slerp(filteredNormals[contactIndex], nearest.normal, smoothing).normalized
            : nearest.normal;
        hasFilteredContacts[contactIndex] = true;
        nearest.normal = filteredNormals[contactIndex];
        return true;
    }

    private void ApplyContact(RaycastHit nearest, float targetSpeed, float supportedMass, float headingAcceleration)
    {
        Transform root = rootBody.transform;
        Vector3 forward = Vector3.ProjectOnPlane(root.forward, nearest.normal).normalized;
        Vector3 right = Vector3.Cross(nearest.normal, forward).normalized;
        Vector3 velocity = rootBody.GetPointVelocity(nearest.point);
        Rigidbody other = nearest.rigidbody;
        ArticulationBody otherArticulation = nearest.articulationBody;
        if (other != null) velocity -= other.GetPointVelocity(nearest.point);
        else if (otherArticulation != null) velocity -= otherArticulation.GetPointVelocity(nearest.point);
        float acceleration = Mathf.Clamp((targetSpeed - Vector3.Dot(velocity, forward)) /
            Mathf.Max(responseTime, Time.fixedDeltaTime), -maxAcceleration, maxAcceleration);
        // Apply the differential after the speed-loop clamp: at low uphill speed both loops can
        // saturate, making a speed-target-only heading correction ineffective.
        acceleration = Mathf.Clamp(acceleration + headingAcceleration, -maxAcceleration, maxAcceleration);
        Vector3 force = supportedMass * (forward * acceleration - right *
            Vector3.Dot(velocity, right) * lateralGrip - Vector3.ProjectOnPlane(Physics.gravity, nearest.normal));
        float maxForce = supportedMass * Physics.gravity.magnitude * tractionCoefficient;
        force = Vector3.ClampMagnitude(force, maxForce);
        rootBody.AddForceAtPosition(force, nearest.point);
        // 动态地面接收反作用力，避免凭空向另一刚体传递单向推力。
        if (other != null && !other.isKinematic) other.AddForceAtPosition(-force, nearest.point);
        else if (otherArticulation != null && !otherArticulation.immovable)
            otherArticulation.AddForceAtPosition(-force, nearest.point);
    }

    private void ApplyContactMaterial(PhysicsMaterial material)
    {
        foreach (Collider collider in trackColliders)
            if (collider != null && collider.sharedMaterial != material) collider.sharedMaterial = material;
    }

    private void OnDisable()
    {
        Stop();
        System.Array.Clear(hasFilteredContacts, 0, hasFilteredContacts.Length);
        System.Array.Clear(trackTargetSpeeds, 0, trackTargetSpeeds.Length);
        hasDriveHeading = false;
        if (rootBody != null) rootBody.angularDamping = originalAngularDamping;
        RestoreMaterials();
    }

    private void RestoreMaterials()
    {
        // 停用主动抓地时恢复普通摩擦；不覆盖其他组件后来替换的材质。
        for (int i = 0; i < trackColliders.Count; i++)
            if (trackColliders[i] != null && (trackColliders[i].sharedMaterial == trackMaterial || trackColliders[i].sharedMaterial == parkingMaterial))
                trackColliders[i].sharedMaterial = originalMaterials[i];
    }

    private void OnDestroy()
    {
        RestoreMaterials();
        if (trackMaterial != null) Destroy(trackMaterial);
        if (parkingMaterial != null) Destroy(parkingMaterial);
    }

    private static float SanitizeInput(float input)
    {
        return float.IsNaN(input) || float.IsInfinity(input) ? 0f : Mathf.Clamp(input, -1f, 1f);
    }
}
