using UnityEngine;

/// <summary>Bounded cutting force plus drive-target penetration guard for the local soil heightfield.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BucketSoilCutter))]
[DefaultExecutionOrder(-150)]
public sealed class BucketSoilResistance : MonoBehaviour
{
    [Min(.01f)] public float maximumPenetration = .18f;
    [Min(.001f)] public float contactMargin = .03f;
    [Min(.1f)] public float cuttingSpeedDegrees = 6;
    [Tooltip("Legacy base cutting pressure, Pa; retained for scene compatibility.")]
    [Min(0)] public float specificResistancePa = 10000;
    [Min(0)] public float soilUnitWeightNPerM3 = 17000;
    [Min(0)] public float cohesionPa = 3500;
    [Min(0)] public float surchargePa = 0;
    [Range(0, 50)] public float internalFrictionDegrees = 30;
    [Min(0)] public float viscousResistance = 900;
    [Min(0)] public float maximumForceN = 12000;
    public float PenetrationM { get; private set; }
    public float ResistanceForceN { get; private set; }
    public bool IsBlocked { get; private set; }
    public string BlockReason { get; private set; } = "空中";
    public bool IsContacting { get; private set; }
    public Vector3 AppliedForce { get; private set; }
    private BucketSoilCutter cutter;
    private ArticulationBody body;
    private Vector3 previous;
    private bool hasPrevious;
    private bool full, atFloor, tooDeep;
    private readonly System.Collections.Generic.Dictionary<ArticulationBody, float> holds =
        new System.Collections.Generic.Dictionary<ArticulationBody, float>();

    private void Awake() => Prepare();

    private void FixedUpdate()
    {
        EvaluateContact();
        Vector3 tip = cutter.TipWorldPosition;
        Vector3 velocity = hasPrevious ? (tip - previous) / Time.fixedDeltaTime : Vector3.zero;
        hasPrevious = true;
        previous = tip;
        AppliedForce = Vector3.zero;
        ResistanceForceN = 0;
        if (!IsContacting || velocity.sqrMagnitude < .000001f || velocity.magnitude > 5 || body == null) return;
        // Withdrawal is deliberately unopposed. This approximates cutting pressure, not hydraulic dynamics.
        if (velocity.y > .02f) return;
        float alignment = Vector3.Dot(velocity.normalized, transform.TransformDirection(cutter.cutDirectionLocal).normalized);
        float force = EstimateResistanceForce(PenetrationM, velocity.magnitude, alignment);
        AppliedForce = -velocity.normalized * force;
        ResistanceForceN = force;
        body.AddForceAtPosition(AppliedForce, tip, ForceMode.Force);
    }

    /// <summary>Estimate bounded earth pressure using depth-squared weight and depth-linear strength terms.</summary>
    /// <param name="depth">Actual penetration in metres; nonpositive depth produces no cutting force.</param>
    /// <param name="speed">Nonnegative blade speed in metres per second.</param>
    /// <param name="alignment">Signed motion alignment with the mouth; influences pressure continuously.</param>
    /// <returns>Newtons, limited by maximumForceN. This is an uncalibrated passive-pressure approximation, not a full Reece solver.</returns>
    public float EstimateResistanceForce(float depth, float speed, float alignment)
    {
        if (!float.IsFinite(depth) || !float.IsFinite(speed) || !float.IsFinite(alignment) || depth <= 0) return 0;
        Prepare();
        float d = Mathf.Min(depth, maximumPenetration);
        float phi = Mathf.Clamp(internalFrictionDegrees, 0, 50) * Mathf.Deg2Rad;
        float passive = Mathf.Pow(Mathf.Tan(Mathf.PI * .25f + phi * .5f), 2);
        float pressure = specificResistancePa + 2 * cohesionPa * Mathf.Sqrt(passive) + surchargePa * passive;
        float width = 2 * cutter.bladeHalfWidth * Mathf.Abs(transform.lossyScale.x);
        float angleFactor = Mathf.Lerp(.7f, 1, Mathf.Clamp01((alignment + 1) * .5f));
        float force = width * (pressure * d + .5f * soilUnitWeightNPerM3 * d * d * passive) * angleFactor;
        force += Mathf.Max(0, speed) * viscousResistance * Mathf.Clamp01(d / Mathf.Max(.01f, maximumPenetration));
        return Mathf.Clamp(force, 0, Mathf.Max(0, maximumForceN));
    }

    /// <summary>Refresh soil contact, fullness, depth and floor feedback from the actual cutting edge.</summary>
    public void EvaluateContact()
    {
        Prepare();
        Vector3 tip = cutter.TipWorldPosition;
        IsContacting = TrySurface(tip, out float surface);
        PenetrationM = IsContacting ? Mathf.Max(0, surface - tip.y) : 0;
        IsContacting &= tip.y <= surface + contactMargin;
        IsBlocked = false;
        BlockReason = "空中";
        if (!IsContacting) return;
        full = cutter.EffectiveCapacityM3 - cutter.VolumeM3 <= .00001;
        atFloor = tip.y <= cutter.soil.GetFloorWorldY(tip) + contactMargin;
        tooDeep = PenetrationM >= maximumPenetration;
        IsBlocked = full || atFloor || tooDeep;
        BlockReason = full ? "满斗：禁止继续切入" : atFloor ? "土层底部：禁止深入" : tooDeep ? "切深超限：限制深入，可沿面切削或抬升" : "切削接触";
    }

    /// <summary>Limit each joint command using its predicted cutting-edge displacement; never write solver pose.</summary>
    /// <param name="machine">Controller whose four articulation joints own this bucket.</param>
    /// <param name="requested">Requested joint targets in degrees.</param>
    /// <param name="deltaSeconds">Positive physics timestep.</param>
    /// <returns>Safe targets: retain support when held, permit withdrawal and oblique face cuts, limit cutting speed.</returns>
    public Zx200JointPose ConstrainPose(Zx200DigController machine, Zx200JointPose requested, float deltaSeconds)
    {
        EvaluateContact();
        if (!isActiveAndEnabled || machine == null || !IsContacting || deltaSeconds <= 0)
        { holds.Clear(); return requested; }
        var actual = machine.ReadActualPose();
        requested.swing = GuardJoint(machine.swingJoint, actual.swing, requested.swing, deltaSeconds);
        requested.boom = GuardJoint(machine.boomJoint, actual.boom, requested.boom, deltaSeconds);
        requested.arm = GuardJoint(machine.armJoint, actual.arm, requested.arm, deltaSeconds);
        requested.bucket = GuardJoint(machine.bucketJoint, actual.bucket, requested.bucket, deltaSeconds);
        return requested;
    }

    private float GuardJoint(ArticulationBody joint, float actual, float requested, float dt)
    {
        if (joint == null) return requested;
        // While held, compare input against the latched target, not gravity-induced tracking error.
        // Otherwise a downward command can look like withdrawal after the joint sags past its target.
        float reference = holds.TryGetValue(joint, out float held) ? held : actual;
        float delta = requested - reference;
        if (Mathf.Abs(delta) < .00001f) return reference;
        Vector3 tip = cutter.TipWorldPosition;
        Vector3 axis = joint.transform.TransformDirection(joint.anchorRotation * Vector3.right);
        Vector3 pivot = joint.transform.TransformPoint(joint.anchorPosition);
        Vector3 next = pivot + Quaternion.AngleAxis(Mathf.Clamp(delta, -1, 1), axis) * (tip - pivot);
        Vector3 movement = next - tip;
        // A positive vertical step always allows escape, even at capacity or at the excavation floor.
        if (movement.y > .0001f) { holds.Remove(joint); return requested; }
        // Contact limits are independent of another joint's direction rejection in this command.
        // On a mound, overhead height grows during a horizontal cut into its face. It is
        // not a downward plunge. The real arm cuts at about 32 degrees below horizontal;
        // allow mouth-leading oblique cuts up to about 60 degrees at the restricted cutting speed.
        if (full || atFloor) return Hold(joint, actual);
        float horizontalTravel = new Vector2(movement.x, movement.z).magnitude;
        if (tooDeep)
        {
            float mouthAlignment = Vector3.Dot(movement.normalized,
                transform.TransformDirection(cutter.cutDirectionLocal).normalized);
            if (movement.y < -1.7f * horizontalTravel - .00001f || mouthAlignment < 0f)
                return Hold(joint, actual);
        }
        if (!cutter.CanCutMovement(movement, out string reason))
        {
            IsBlocked = true;
            BlockReason = reason;
            return Hold(joint, actual);
        }
        holds.Remove(joint);
        return Mathf.MoveTowards(actual, requested, cuttingSpeedDegrees * dt);
    }

    private float Hold(ArticulationBody joint, float actual)
    {
        // Latch once: recapturing actual angles every frame would ratchet down under gravity.
        if (!holds.TryGetValue(joint, out float target))
        {
            // Preserve existing spring deflection supporting the arm's weight; replacing it with
            // actual angle removes gravity support and causes a fresh sag even with a latched hold.
            target = joint.xDrive.target;
            holds.Add(joint, target);
        }
        return target;
    }

    private bool TrySurface(Vector3 point, out float surface)
    {
        surface = 0;
        if (cutter.soil == null) return false;
        cutter.soil.Initialize(cutter.soil.GetComponent<Terrain>());
        if (!cutter.soil.HasSurfaceAt(point)) return false;
        var terrain = cutter.soil.Terrain;
        surface = terrain.SampleHeight(point) + terrain.transform.position.y;
        return true;
    }

    private void Prepare()
    {
        if (cutter == null) cutter = GetComponent<BucketSoilCutter>();
        if (body == null) body = GetComponent<ArticulationBody>();
    }

    private void OnDisable() { holds.Clear(); hasPrevious = false; AppliedForce = Vector3.zero; ResistanceForceN = 0; }
}
