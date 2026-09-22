using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// 保存一组 ZX200 四关节目标姿态及其过渡时长。
/// </summary>
[Serializable]
public struct Zx200JointPose
{
    public float swing;
    public float boom;
    public float arm;
    public float bucket;
    [Min(0f)] public float duration;

    /// <summary>
    /// 创建一组完整的 ZX200 关节目标姿态。
    /// </summary>
    /// <param name="swing">上车体回旋目标角度，单位为度。</param>
    /// <param name="boom">动臂目标角度，单位为度。</param>
    /// <param name="arm">斗杆目标角度，单位为度。</param>
    /// <param name="bucket">铲斗目标角度，单位为度。</param>
    /// <param name="duration">过渡时长，单位为秒。</param>
    public Zx200JointPose(float swing, float boom, float arm, float bucket, float duration)
    {
        this.swing = swing;
        this.boom = boom;
        this.arm = arm;
        this.bucket = bucket;
        this.duration = duration;
    }
}

/// <summary>
/// 通过 Space 键触发一套 ZX200 关节挖掘动作循环。
/// </summary>
public class Zx200DigController : MonoBehaviour
{
    [Header("Articulation Joints")]
    public ArticulationBody swingJoint;
    public ArticulationBody boomJoint;
    public ArticulationBody armJoint;
    public ArticulationBody bucketJoint;
    public Zx200FixedBasePhysics fixedBasePhysics;

    // 吨级连杆需要足够的关节刚度承受重力，阻尼用于抑制过冲。
    // 这些数值是仿真控制参数，尚未经过液压系统标定。
    [Header("Drive Settings")]
    [Min(0f)] public float stiffness = 10000000f;
    [Min(0f)] public float damping = 500000f;
    [Min(0f)] public float forceLimit = 750000f;
    [Min(0f)] public float jointToleranceDegrees = 1.5f;
    [Min(0f)] public float settleTimeoutSeconds = 3f;

    [Header("Motion Smoothing")]
    [Tooltip("关节目标起停的平滑时间，秒；减小突发加速对动态底盘的冲击。")]
    [Min(0.02f)] public float motionSmoothTime = 0.3f;
    private Zx200JointPose requestedPose;
    private Vector4 targetVelocities;
    private bool driveTargetsInitialized;
    private BucketSoilResistance soilResistance;

    [Header("Work-ready Pose")]
    public Zx200JointPose workReadyPose = new Zx200JointPose(0f, -48f, 68f, 25f, 1f);

    [Header("Dig Cycle")]
    public Zx200JointPose approachPose = new Zx200JointPose(0f, -60f, 52f, 5f, 1.2f);
    public Zx200JointPose curlPose = new Zx200JointPose(0f, -62f, 92f, 105f, 1.1f);
    [Min(0f)] public float curlHoldDuration = 0.25f;
    public Zx200JointPose liftPose = new Zx200JointPose(0f, -25f, 100f, 110f, 1.3f);
    public Zx200JointPose swingToDumpPose = new Zx200JointPose(60f, -25f, 100f, 110f, 1.4f);
    public Zx200JointPose dumpPreparePose = new Zx200JointPose(60f, -32f, 78f, 80f, 0.6f);
    public Zx200JointPose dumpPose = new Zx200JointPose(60f, -32f, 68f, -25f, 0.9f);
    [Min(0f)] public float dumpHoldDuration = 0.35f;
    public Zx200JointPose returnSwingPose = new Zx200JointPose(0f, -35f, 72f, -20f, 1.4f);

    /// <summary>获取当前是否正在执行启动定位或挖掘动作循环。</summary>
    public bool IsBusy { get; private set; }

    private void Awake()
    {
        if (!PrepareForPhysics())
        {
            Debug.LogError("ZX200 digging controller could not resolve all required articulation joints.", this);
            enabled = false;
            return;
        }
        InitializeStartupPose();
    }

    private void InitializeStartupPose()
    {
        // PhysX创建关节树时的广义坐标也必须与保存的模型姿态一致。只在Awake初始化，
        // 后续控制仅修改驱动目标，不覆写求解器状态。
        ApplyWorkReadyPose();
        InitializeJoint(swingJoint);
        InitializeJoint(boomJoint);
        InitializeJoint(armJoint);
        InitializeJoint(bucketJoint);
        requestedPose = ReadTargetPose();
        driveTargetsInitialized = true;
    }

    private static void InitializeJoint(ArticulationBody joint)
    {
        joint.jointPosition = new ArticulationReducedSpace(joint.xDrive.target * Mathf.Deg2Rad);
        joint.jointVelocity = new ArticulationReducedSpace(0f);
        joint.jointForce = new ArticulationReducedSpace(0f);
    }

    private void Start()
    {
        ApplyWorkReadyPose();
        Debug.Log("ZX200 digging controller ready. Focus the Game view and press Space to dig.", this);
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            Debug.Log("ZX200 received Space input.", this);
            PlayDigCycle();
        }
    }

    /// <summary>
    /// 查找并配置 ZX200 关节树和固定底盘物理组件。
    /// </summary>
    /// <returns>物理组件及四个可控关节均完成配置时返回 true。</returns>
    public bool PrepareForPhysics()
    {
        if (!ResolveJoints()) return false;
        if (!ValidateControlValues(out _)) return false;
        if (fixedBasePhysics == null) fixedBasePhysics = GetComponent<Zx200FixedBasePhysics>();
        if (fixedBasePhysics == null || !fixedBasePhysics.Configure()) return false;

        // URDF 导入器会锁定连续关节，并将其驱动范围设为零。
        // 将回旋关节改为较宽的受限关节，使目标角度可以正常使用。
        ArticulationDrive swingDrive = swingJoint.xDrive;
        if (swingJoint.twistLock == ArticulationDofLock.LockedMotion ||
            Mathf.Approximately(swingDrive.lowerLimit, swingDrive.upperLimit))
        {
            swingJoint.twistLock = ArticulationDofLock.LimitedMotion;
            swingDrive.lowerLimit = -179f;
            swingDrive.upperLimit = 179f;
            swingJoint.xDrive = swingDrive;
        }

        ConfigureDrives();
        return true;
    }

    /// <summary>
    /// 根据 URDF link 名称查找所需的四个关节。
    /// </summary>
    /// <returns>找到全部所需关节刚体时返回 true。</returns>
    public bool ResolveJoints()
    {
        foreach (ArticulationBody body in GetComponentsInChildren<ArticulationBody>(true))
        {
            switch (body.name)
            {
                case "body_link": swingJoint = swingJoint != null ? swingJoint : body; break;
                case "boom_link": boomJoint = boomJoint != null ? boomJoint : body; break;
                case "arm_link": armJoint = armJoint != null ? armJoint : body; break;
                case "bucket_link": bucketJoint = bucketJoint != null ? bucketJoint : body; break;
            }
        }

        return swingJoint != null && boomJoint != null && armJoint != null && bucketJoint != null;
    }

    /// <summary>
    /// 请求待机姿态；运行中通过平滑目标驱动到位，编辑时直接更新目标。
    /// </summary>
    public void ApplyWorkReadyPose()
    {
        ApplyPose(workReadyPose);
    }

    /// <summary>
    /// 在没有其他动作运行时启动一套完整的挖掘循环。
    /// </summary>
    /// <returns>成功启动新循环时返回 true，否则返回 false。</returns>
    public bool PlayDigCycle()
    {
        if (!enabled)
        {
            Debug.LogWarning("ZX200 dig request ignored because the controller is disabled.", this);
            return false;
        }
        if (IsBusy)
        {
            Debug.LogWarning("ZX200 dig request ignored because a cycle is already running.", this);
            return false;
        }
        if (!ResolveJoints())
        {
            Debug.LogError("ZX200 dig request failed because required joints are missing.", this);
            return false;
        }

        StartCoroutine(DigCycle());
        Debug.Log("ZX200 digging cycle started.", this);
        return true;
    }

    /// <summary>
    /// 同时对四个受控关节应用手动角度增量。
    /// </summary>
    /// <param name="swingDelta">上车体回旋角度增量，单位为度。</param>
    /// <param name="boomDelta">动臂角度增量，单位为度。</param>
    /// <param name="armDelta">斗杆角度增量，单位为度。</param>
    /// <param name="bucketDelta">铲斗角度增量，单位为度。</param>
    /// <returns>成功应用增量时返回 true；自动动作运行中或关节不可用时返回 false。</returns>
    public bool AdjustManualPose(
        float swingDelta,
        float boomDelta,
        float armDelta,
        float bucketDelta)
    {
        if (IsBusy || !enabled || !ResolveJoints()) return false;

        Zx200JointPose current = ReadTargetPose();
        ApplyPose(new Zx200JointPose(
            current.swing + swingDelta,
            current.boom + boomDelta,
            current.arm + armDelta,
            current.bucket + bucketDelta,
            0f));
        return true;
    }

    /// <summary>
    /// 将驱动目标限制在给定的上下限范围内。
    /// </summary>
    /// <param name="target">请求的目标角度，单位为度。</param>
    /// <param name="lowerLimit">驱动下限，单位为度。</param>
    /// <param name="upperLimit">驱动上限，单位为度。</param>
    /// <returns>限制在指定范围内的目标角度。</returns>
    public static float ClampTarget(float target, float lowerLimit, float upperLimit)
    {
        return Mathf.Clamp(target, Mathf.Min(lowerLimit, upperLimit), Mathf.Max(lowerLimit, upperLimit));
    }

    /// <summary>
    /// 将线性进度转换为限制在 0 到 1 的平滑进度。
    /// </summary>
    /// <param name="value">未限制的线性进度。</param>
    /// <returns>范围为 0 到 1 的平滑进度。</returns>
    public static float SmoothProgress(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }

    /// <summary>
    /// 使用最短角距离判断两个关节角度是否在容差范围内。
    /// </summary>
    /// <param name="actual">实际测得的关节角度，单位为度。</param>
    /// <param name="target">请求的目标关节角度，单位为度。</param>
    /// <param name="tolerance">允许的绝对角度误差，单位为度。</param>
    /// <returns>实际角度处于非负容差范围内时返回 true。</returns>
    public static bool IsAngleWithinTolerance(float actual, float target, float tolerance)
    {
        return Mathf.Abs(Mathf.DeltaAngle(actual, target)) <= Mathf.Max(0f, tolerance);
    }

    /// <summary>
    /// 读取物理解算器中四个关节的实际角度。
    /// </summary>
    /// <returns>当前关节角度，单位为度。</returns>
    public Zx200JointPose ReadActualPose()
    {
        return new Zx200JointPose(
            ReadJointDegrees(swingJoint),
            ReadJointDegrees(boomJoint),
            ReadJointDegrees(armJoint),
            ReadJointDegrees(bucketJoint),
            0f);
    }

    /// <summary>
    /// 判断全部关节的实际角度是否到达请求姿态。
    /// </summary>
    /// <param name="target">请求的目标关节姿态。</param>
    /// <param name="toleranceDegrees">每个关节允许的绝对角度误差。</param>
    /// <returns>全部关节均处于容差范围内时返回 true。</returns>
    public bool IsPoseWithinTolerance(Zx200JointPose target, float toleranceDegrees)
    {
        target = ClampPoseToDriveLimits(target);
        Zx200JointPose actual = ReadActualPose();
        return IsAngleWithinTolerance(actual.swing, target.swing, toleranceDegrees) &&
               IsAngleWithinTolerance(actual.boom, target.boom, toleranceDegrees) &&
               IsAngleWithinTolerance(actual.arm, target.arm, toleranceDegrees) &&
               IsAngleWithinTolerance(actual.bucket, target.bucket, toleranceDegrees);
    }

    private void ConfigureDrives()
    {
        ConfigureDrive(swingJoint);
        ConfigureDrive(boomJoint);
        ConfigureDrive(armJoint);
        ConfigureDrive(bucketJoint);
    }

    private void ConfigureDrive(ArticulationBody joint)
    {
        ArticulationDrive drive = joint.xDrive;
        drive.stiffness = stiffness;
        drive.damping = damping;
        drive.forceLimit = forceLimit;
        joint.xDrive = drive;
    }

    private IEnumerator DigCycle()
    {
        IsBusy = true;
        yield return AnimateTo(approachPose);
        if (!IsBusy) yield break;
        yield return AnimateTo(curlPose);
        if (!IsBusy) yield break;
        if (curlHoldDuration > 0f) yield return new WaitForSeconds(curlHoldDuration);
        yield return AnimateTo(liftPose);
        if (!IsBusy) yield break;
        yield return AnimateTo(swingToDumpPose);
        if (!IsBusy) yield break;
        yield return AnimateTo(dumpPreparePose);
        if (!IsBusy) yield break;
        yield return AnimateTo(dumpPose);
        if (!IsBusy) yield break;
        if (dumpHoldDuration > 0f) yield return new WaitForSeconds(dumpHoldDuration);
        yield return AnimateTo(returnSwingPose);
        if (!IsBusy) yield break;
        yield return AnimateTo(workReadyPose);
        if (!IsBusy) yield break;
        IsBusy = false;
    }

    private IEnumerator AnimateTo(Zx200JointPose target)
    {
        target = ClampPoseToDriveLimits(target);
        Zx200JointPose start = ReadActualPose();
        if (target.duration > 0f)
        {
            float elapsed = 0f;
            while (elapsed < target.duration)
            {
                float progress = SmoothProgress(elapsed / target.duration);
                ApplyPose(new Zx200JointPose(
                    Mathf.Lerp(start.swing, target.swing, progress),
                    Mathf.Lerp(start.boom, target.boom, progress),
                    Mathf.Lerp(start.arm, target.arm, progress),
                    Mathf.Lerp(start.bucket, target.bucket, progress),
                    0f));
                elapsed += Time.deltaTime;
                yield return null;
            }
        }
        ApplyPose(target);

        float settleElapsed = 0f;
        while (!IsPoseWithinTolerance(target, jointToleranceDegrees) &&
               settleElapsed < settleTimeoutSeconds)
        {
            settleElapsed += Time.fixedDeltaTime;
            yield return new WaitForFixedUpdate();
        }

        if (!IsPoseWithinTolerance(target, jointToleranceDegrees))
        {
            Zx200JointPose actual = ReadActualPose();
            Debug.LogWarning(
                $"ZX200 joint stage timed out. " +
                $"swing error={Mathf.Abs(Mathf.DeltaAngle(actual.swing, target.swing)):F1} deg, " +
                $"boom error={Mathf.Abs(Mathf.DeltaAngle(actual.boom, target.boom)):F1} deg, " +
                $"arm error={Mathf.Abs(Mathf.DeltaAngle(actual.arm, target.arm)):F1} deg, " +
                $"bucket error={Mathf.Abs(Mathf.DeltaAngle(actual.bucket, target.bucket)):F1} deg.",
                this);
            IsBusy = false;
        }
    }

    private Zx200JointPose ClampPoseToDriveLimits(Zx200JointPose pose)
    {
        return new Zx200JointPose(
            ClampToDrive(swingJoint, pose.swing),
            ClampToDrive(boomJoint, pose.boom),
            ClampToDrive(armJoint, pose.arm),
            ClampToDrive(bucketJoint, pose.bucket),
            pose.duration);
    }

    private Zx200JointPose ReadTargetPose()
    {
        if (Application.isPlaying && driveTargetsInitialized) return requestedPose;
        return new Zx200JointPose(
            swingJoint.xDrive.target,
            boomJoint.xDrive.target,
            armJoint.xDrive.target,
            bucketJoint.xDrive.target,
            0f);
    }

    private void ApplyPose(Zx200JointPose pose)
    {
        if (Application.isPlaying && driveTargetsInitialized)
        {
            requestedPose = ClampPoseToDriveLimits(pose);
            return;
        }
        SetTarget(swingJoint, pose.swing);
        SetTarget(boomJoint, pose.boom);
        SetTarget(armJoint, pose.arm);
        SetTarget(bucketJoint, pose.bucket);
    }

    private void FixedUpdate()
    {
        if (!driveTargetsInitialized) return;
        if (soilResistance == null && bucketJoint != null) soilResistance = bucketJoint.GetComponent<BucketSoilResistance>();
        if (soilResistance != null && soilResistance.isActiveAndEnabled)
        {
            Zx200JointPose constrained = soilResistance.ConstrainPose(this, requestedPose, Time.fixedDeltaTime);
            // Cancel accumulated target error on a constrained joint so holding a key cannot build a later lurch.
            ApplyConstrainedTarget(swingJoint, requestedPose.swing, constrained.swing, ref targetVelocities.x);
            ApplyConstrainedTarget(boomJoint, requestedPose.boom, constrained.boom, ref targetVelocities.y);
            ApplyConstrainedTarget(armJoint, requestedPose.arm, constrained.arm, ref targetVelocities.z);
            ApplyConstrainedTarget(bucketJoint, requestedPose.bucket, constrained.bucket, ref targetVelocities.w);
            requestedPose = constrained;
        }
        // 在固定物理步上对目标平滑，输入反向与松手也有连续的减速过程。
        SmoothTarget(swingJoint, requestedPose.swing, ref targetVelocities.x);
        SmoothTarget(boomJoint, requestedPose.boom, ref targetVelocities.y);
        SmoothTarget(armJoint, requestedPose.arm, ref targetVelocities.z);
        SmoothTarget(bucketJoint, requestedPose.bucket, ref targetVelocities.w);
    }

    private static void ApplyConstrainedTarget(ArticulationBody joint, float requested, float constrained, ref float velocity)
    {
        if (Mathf.Abs(requested - constrained) < .00001f) return;
        velocity = 0;
        SetTarget(joint, constrained);
    }

    private void SmoothTarget(ArticulationBody joint, float target, ref float velocity)
    {
        float next = Mathf.SmoothDamp(joint.xDrive.target, target, ref velocity,
            motionSmoothTime, Mathf.Infinity, Time.fixedDeltaTime);
        SetTarget(joint, next);
    }

    private static void SetTarget(ArticulationBody joint, float target)
    {
        ArticulationDrive drive = joint.xDrive;
        float clampedTarget = ClampTarget(target, drive.lowerLimit, drive.upperLimit);
        drive.target = clampedTarget;
        joint.xDrive = drive;
    }

    private static float ClampToDrive(ArticulationBody joint, float target)
    {
        ArticulationDrive drive = joint.xDrive;
        return ClampTarget(target, drive.lowerLimit, drive.upperLimit);
    }

    private bool ValidateControlValues(out string error)
    {
        if (!IsFinitePositive(motionSmoothTime)) return Invalid("motionSmoothTime", out error);
        if (!IsFiniteNonNegative(stiffness)) return Invalid("stiffness", out error);
        if (!IsFiniteNonNegative(damping)) return Invalid("damping", out error);
        if (!IsFinitePositive(forceLimit)) return Invalid("forceLimit", out error);
        if (!IsFiniteNonNegative(jointToleranceDegrees))
            return Invalid("jointToleranceDegrees", out error);
        if (!IsFinitePositive(settleTimeoutSeconds))
            return Invalid("settleTimeoutSeconds", out error);
        if (!IsFiniteNonNegative(curlHoldDuration))
            return Invalid("curlHoldDuration", out error);
        if (!IsFiniteNonNegative(dumpHoldDuration))
            return Invalid("dumpHoldDuration", out error);

        if (!ValidatePose(workReadyPose, "workReadyPose", out error) ||
            !ValidatePose(approachPose, "approachPose", out error) ||
            !ValidatePose(curlPose, "curlPose", out error) ||
            !ValidatePose(liftPose, "liftPose", out error) ||
            !ValidatePose(swingToDumpPose, "swingToDumpPose", out error) ||
            !ValidatePose(dumpPreparePose, "dumpPreparePose", out error) ||
            !ValidatePose(dumpPose, "dumpPose", out error) ||
            !ValidatePose(returnSwingPose, "returnSwingPose", out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidatePose(Zx200JointPose pose, string fieldName, out string error)
    {
        if (!IsFinite(pose.swing) || !IsFinite(pose.boom) || !IsFinite(pose.arm) ||
            !IsFinite(pose.bucket) || !IsFiniteNonNegative(pose.duration))
        {
            return Invalid(fieldName, out error);
        }

        error = string.Empty;
        return true;
    }

    private static bool IsFinitePositive(float value)
    {
        return value > 0f && IsFinite(value);
    }

    private static bool IsFiniteNonNegative(float value)
    {
        return value >= 0f && IsFinite(value);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool Invalid(string fieldName, out string error)
    {
        error = $"{fieldName} has an invalid value";
        return false;
    }

    private static float ReadJointDegrees(ArticulationBody joint)
    {
        if (joint == null || joint.jointPosition.dofCount == 0) return 0f;
        return joint.jointPosition[0] * Mathf.Rad2Deg;
    }
}
