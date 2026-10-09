using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>Describes the externally observable stage of an autonomous excavation job.</summary>
public enum AutonomousExcavationState
{
    Idle,
    WaitingForSite,
    AcquirePoint,
    LowerAndEnter,
    FillBucket,
    Lift,
    SwingToTruck,
    Dump,
    ConfirmDump,
    ReturnToDig,
    Completed,
    TruckFull,
    SoilExhausted,
    EmptyCut,
    ManualTakeover,
    Fault,
    Resetting,
    PrepareDump
}

/// <summary>Identifies the single articulation axis currently commanded by the autonomous sequencer.</summary>
public enum AutonomousMotionAxis
{
    None,
    Bucket,
    Boom,
    Arm,
    Swing,
    ArmAndBucket,
    BoomArmBucket
}

/// <summary>
/// Coordinates one or more physical ZX200 dig-and-load cycles without minting or teleporting soil.
/// </summary>
[DisallowMultipleComponent]
public sealed class AutonomousExcavationController : MonoBehaviour
{
    private static readonly float[] DigPointSwingOffsetsDegrees = { 0f, -8f, 8f, -16f, 16f, -24f, 24f };
    // The validated high-clearance dump pose may be up to 7 m above the receiver; wall clearance is checked separately.
    private const float MaximumDumpDropMeters = 7f;
    private const float DumpLandingInsetMeters = .25f;
    private const float TruckSwingClearanceReserveMeters = .05f;
    private const float TruckSwingStartReserveMeters = .7f;
    private const float TruckCollisionSlopToleranceMeters = .01f;
    private const float TruckClearanceRecoveryToleranceMeters = .03f;
    private const float BucketResidualToleranceM3 = .01f;
    private const float ScoopRateWindowSeconds = .4f;
    private const float MinimumScoopRateWindowSeconds = .2f;
    private const float ScoopNoProgressTimeoutSeconds = .45f;
    private const float MinimumPredictedCurlSpeedDegreesPerSecond = 8f;
    private const float MaximumPredictedCurlTimeSeconds = 2.5f;
    private const float MaximumAdaptiveBoomLiftDegrees = 4f;

    private struct ScoopVolumeSample
    {
        public float time;
        public double volume;

        public ScoopVolumeSample(float time, double volume)
        {
            this.time = time;
            this.volume = volume;
        }
    }

    /// <summary>Continuous articulation weights derived from measured and predicted bucket fill.</summary>
    public struct AdaptiveScoopMotionCommand
    {
        /// <summary>Fraction of the configured arm-scoop rate that may continue cutting.</summary>
        public float armAdvanceScale;
        /// <summary>Multiplier applied to the configured bucket-curl rate.</summary>
        public float bucketCurlScale;
        /// <summary>Fraction of the bounded auxiliary boom-lift rate.</summary>
        public float boomLiftScale;
        /// <summary>Greater of current and predicted fill fractions, before output clamping.</summary>
        public float projectedFillFraction;
    }

    [Header("References")]
    public Zx200DigController machine;
    public BucketSoilCutter cutter;
    public BucketSoilUnloader unloader;
    public SoilReceivingZone receiver;
    public MonoBehaviour siteReadinessSource;

    [Header("Job Settings")]
    [Min(.001f)] public float targetVolumeM3 = 1f;
    [Min(.0001f)] public float minimumLoadVolumeM3 = .005f;
    [Range(.1f, .95f)] public float targetBucketFillFraction = .65f;
    [Min(.0001f)] public float minimumTransferVolumeM3 = .005f;
    [Min(1f)] public float siteReadyTimeoutSeconds = 40f;
    [Min(1f)] public float stageTimeoutSeconds = 12f;
    [Min(.1f)] public float cutTimeoutSeconds = 8f;
    [Min(.1f)] public float dumpTimeoutSeconds = 12f;
    [Min(.001f)] public float soilFloorMargin = .04f;
    [Min(0f)] public float cutEntryDepthMeters = .12f;
    [Min(0f)] public float dumpClearanceMarginMeters = .25f;
    [Min(.1f)] public float bucketCloseDeltaDegrees = 35f;
    [Min(.1f)] public float boomEntryStepDegrees = 7f;
    [Min(.1f)] public float armScoopStepDegrees = 10f;
    [Min(1)] public int maximumEntrySteps = 24;
    [Min(1)] public int maximumScoopSteps = 24;

    [Header("Keyboard Shortcuts")]
    [SerializeField] private KeyCode autonomousStartKey = KeyCode.P;

    /// <summary>获取启动自主挖掘装车任务的独立键盘按键。</summary>
    public KeyCode AutonomousStartKey => autonomousStartKey;

    /// <summary>获取当前自动任务阶段。</summary>
    public AutonomousExcavationState State { get; private set; } = AutonomousExcavationState.Idle;

    /// <summary>获取当前任务已实际经过的状态序列，用于 HMI 诊断与流程验收。</summary>
    public IReadOnlyList<AutonomousExcavationState> StateHistory => stateHistory;

    /// <summary>获取当前单轴运动段；没有自动关节运动时为 None。</summary>
    public AutonomousMotionAxis CurrentMotionAxis { get; private set; } = AutonomousMotionAxis.None;

    /// <summary>获取面向 HMI 的当前状态说明。</summary>
    public string Status { get; private set; } = "等待启动";

    /// <summary>获取最近一次异常停止原因；正常状态下为空。</summary>
    public string FaultReason { get; private set; } = string.Empty;

    /// <summary>获取当前是否有自主任务协程在运行。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>获取当前任务已由接收区实际落地的土量，单位为立方米。</summary>
    public double DeliveredVolumeM3 { get; private set; }

    /// <summary>获取当前任务已完成的斗数。</summary>
    public int CompletedCycles { get; private set; }

    /// <summary>获取当前任务触发“满斗先脱坡、再补收斗”异常恢复的次数。</summary>
    public int FullBucketRecoveryCount { get; private set; }

    /// <summary>获取当前任务在底面限制处先脱坡、再收拢有效部分土量的恢复次数。</summary>
    public int PartialBucketRecoveryCount { get; private set; }

    /// <summary>获取最近挖点依据局部土层计算出的安全切入深度。</summary>
    public float LastCutEntryDepthMeters { get; private set; }

    /// <summary>获取卸料时斗尖相对车厢最高实体保留的最小安全高度。</summary>
    public float DumpClearanceMarginMeters => dumpClearanceMarginMeters;

    /// <summary>Build a waypoint that changes one articulation axis and holds the measured pose of all others.</summary>
    /// <param name="actual">The latest measured four-joint pose.</param>
    /// <param name="axis">The only joint allowed to change.</param>
    /// <param name="targetDegrees">The target angle for the selected joint, in degrees.</param>
    /// <param name="durationSeconds">The requested transition duration, in seconds.</param>
    /// <returns>A complete pose with exactly one requested joint target changed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="axis"/> is None or invalid.</exception>
    public static Zx200JointPose CreateSingleAxisWaypoint(
        Zx200JointPose actual,
        AutonomousMotionAxis axis,
        float targetDegrees,
        float durationSeconds)
    {
        switch (axis)
        {
            case AutonomousMotionAxis.Bucket: actual.bucket = targetDegrees; break;
            case AutonomousMotionAxis.Boom: actual.boom = targetDegrees; break;
            case AutonomousMotionAxis.Arm: actual.arm = targetDegrees; break;
            case AutonomousMotionAxis.Swing: actual.swing = targetDegrees; break;
            default: throw new ArgumentOutOfRangeException(nameof(axis), axis, "A motion segment must select one joint axis.");
        }
        actual.duration = durationSeconds;
        return actual;
    }

    /// <summary>Build a truck-side approach pose without lowering the measured boom before clearance checks.</summary>
    /// <param name="liftedPose">The measured pose after the loaded bucket has reached its lift waypoint.</param>
    /// <param name="dumpPose">The configured pose supplying the truck-side arm reach and motion duration.</param>
    /// <param name="closedBucketAngle">The requested closed-bucket angle, in degrees.</param>
    /// <returns>A pose that preserves measured swing and boom while using the configured arm reach.</returns>
    public static Zx200JointPose CreatePreSwingApproachPose(
        Zx200JointPose liftedPose,
        Zx200JointPose dumpPose,
        float closedBucketAngle)
    {
        return new Zx200JointPose(
            liftedPose.swing,
            liftedPose.boom,
            dumpPose.arm,
            closedBucketAngle,
            dumpPose.duration);
    }

    /// <summary>Estimate nonnegative incoming soil volume per second from a bounded sample window.</summary>
    /// <param name="previousVolumeM3">Earlier measured bucket volume in cubic metres.</param>
    /// <param name="currentVolumeM3">Latest measured bucket volume in cubic metres.</param>
    /// <param name="elapsedSeconds">Elapsed sample time in seconds.</param>
    /// <returns>A finite nonnegative inflow rate in cubic metres per second.</returns>
    public static double EstimateScoopInflowRate(
        double previousVolumeM3,
        double currentVolumeM3,
        float elapsedSeconds)
    {
        if (!IsFinite(previousVolumeM3) || !IsFinite(currentVolumeM3) ||
            !IsFinite(elapsedSeconds) || elapsedSeconds <= .0001f)
            return 0;

        return Math.Max(0, currentVolumeM3 - previousVolumeM3) / elapsedSeconds;
    }

    /// <summary>Predict the fill fraction expected before the bucket reaches a safe closed angle.</summary>
    /// <param name="currentVolumeM3">Measured soil currently retained by the bucket.</param>
    /// <param name="capacityM3">Effective measured bucket capacity.</param>
    /// <param name="inflowRateM3PerSecond">Nonnegative soil inflow estimated over the recent sample window.</param>
    /// <param name="remainingCurlDegrees">Absolute angle remaining to the configured closed-bucket target.</param>
    /// <param name="actualCurlSpeedDegreesPerSecond">Measured bucket angular speed in degrees per second.</param>
    /// <param name="maximumCurlSpeedDegreesPerSecond">Upper bound for the predicted closing speed.</param>
    /// <returns>A bounded projected fill fraction; values above one indicate predicted over-capacity.</returns>
    public static float PredictScoopFillFraction(
        double currentVolumeM3,
        double capacityM3,
        double inflowRateM3PerSecond,
        float remainingCurlDegrees,
        float actualCurlSpeedDegreesPerSecond,
        float maximumCurlSpeedDegreesPerSecond)
    {
        if (!IsFinite(capacityM3) || capacityM3 <= .000001) return 1f;

        double currentVolume = IsFinite(currentVolumeM3)
            ? Math.Max(0, Math.Min(capacityM3, currentVolumeM3)) : 0;
        double inflowRate = IsFinite(inflowRateM3PerSecond)
            ? Math.Max(0, inflowRateM3PerSecond) : 0;
        float maximumSpeed = IsFinite(maximumCurlSpeedDegreesPerSecond)
            ? Mathf.Max(MinimumPredictedCurlSpeedDegreesPerSecond, maximumCurlSpeedDegreesPerSecond)
            : MinimumPredictedCurlSpeedDegreesPerSecond;
        float actualSpeed = IsFinite(actualCurlSpeedDegreesPerSecond)
            ? Mathf.Abs(actualCurlSpeedDegreesPerSecond) : 0f;
        float boundedSpeed = Mathf.Clamp(actualSpeed,
            MinimumPredictedCurlSpeedDegreesPerSecond, maximumSpeed);
        float remaining = IsFinite(remainingCurlDegrees) ? Mathf.Max(0f, remainingCurlDegrees) : 0f;
        float closeTime = Mathf.Min(MaximumPredictedCurlTimeSeconds, remaining / boundedSpeed);
        double projectedVolume = currentVolume + inflowRate * closeTime;
        return Mathf.Clamp((float)(projectedVolume / capacityM3), 0f, 1.5f);
    }

    /// <summary>Convert current and projected fill into monotone arm, bucket-curl and boom-assist weights.</summary>
    /// <param name="currentFillFraction">Measured bucket fraction, clamped to the valid capacity range.</param>
    /// <param name="predictedFillFraction">Projected fraction at safe closure, possibly above capacity.</param>
    /// <returns>A bounded continuous motion command that progressively prioritizes bucket closure.</returns>
    public static AdaptiveScoopMotionCommand CalculateAdaptiveScoopMotionCommand(
        float currentFillFraction,
        float predictedFillFraction)
    {
        float current = IsFinite(currentFillFraction) ? Mathf.Clamp01(currentFillFraction) : 0f;
        float predicted = IsFinite(predictedFillFraction) ? Mathf.Clamp(predictedFillFraction, 0f, 1.5f) : current;
        float projected = Mathf.Max(current, predicted);
        float armProgress = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.35f, .88f, projected));
        float curlProgress = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.15f, .88f, projected));
        float liftProgress = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.55f, .88f, projected));
        return new AdaptiveScoopMotionCommand
        {
            armAdvanceScale = 1f - armProgress,
            bucketCurlScale = Mathf.Lerp(.2f, 1.2f, curlProgress),
            boomLiftScale = liftProgress * .35f,
            projectedFillFraction = projected
        };
    }

    /// <summary>Find the allowed bucket angle that points its current world-space mouth normal upward.</summary>
    /// <param name="mouthNormalWorld">The measured mouth normal before changing the bucket joint.</param>
    /// <param name="bucketAxisWorld">The bucket articulation axis in world coordinates.</param>
    /// <param name="currentBucketAngle">The measured current bucket angle in degrees.</param>
    /// <param name="lowerLimit">The articulation drive lower limit in degrees.</param>
    /// <param name="upperLimit">The articulation drive upper limit in degrees.</param>
    /// <returns>The in-limit joint angle producing the most upward-facing mouth, preferring smaller changes on ties.</returns>
    public static float CalculateBucketAngleForUpwardMouth(
        Vector3 mouthNormalWorld,
        Vector3 bucketAxisWorld,
        float currentBucketAngle,
        float lowerLimit,
        float upperLimit)
    {
        float lower = Mathf.Min(lowerLimit, upperLimit);
        float upper = Mathf.Max(lowerLimit, upperLimit);
        float current = Zx200DigController.ClampTarget(currentBucketAngle, lower, upper);
        if (mouthNormalWorld.sqrMagnitude < 1e-8f || bucketAxisWorld.sqrMagnitude < 1e-8f ||
            !IsFinite(current) || !IsFinite(lower) || !IsFinite(upper))
            return current;

        Vector3 mouth = mouthNormalWorld.normalized;
        Vector3 axis = bucketAxisWorld.normalized;
        int steps = Mathf.Max(1, Mathf.CeilToInt(upper - lower));
        float bestAngle = current;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i <= steps; i++)
        {
            float candidate = Mathf.Lerp(lower, upper, i / (float)steps);
            Vector3 candidateMouth = Quaternion.AngleAxis(candidate - current, axis) * mouth;
            float score = Vector3.Dot(candidateMouth.normalized, Vector3.up) -
                Mathf.Abs(candidate - current) * .00005f;
            if (score <= bestScore) continue;
            bestScore = score;
            bestAngle = candidate;
        }
        return bestAngle;
    }

    /// <summary>Limit the configured plunge depth to the locally available soil above the protected floor.</summary>
    /// <param name="configuredDepthMeters">Nominal cut-entry depth requested by the job.</param>
    /// <param name="availableLayerThicknessMeters">Measured surface-to-floor soil thickness at the dig point.</param>
    /// <param name="floorClearanceMeters">Required vertical clearance above the non-cuttable ground.</param>
    /// <returns>A nonnegative entry depth that leaves the requested floor clearance.</returns>
    public static float CalculateAdaptiveCutEntryDepth(
        float configuredDepthMeters,
        float availableLayerThicknessMeters,
        float floorClearanceMeters)
    {
        if (!IsFinite(configuredDepthMeters) || !IsFinite(availableLayerThicknessMeters) ||
            !IsFinite(floorClearanceMeters) || configuredDepthMeters <= 0 ||
            availableLayerThicknessMeters <= 0 || floorClearanceMeters < 0)
            return 0f;

        return Mathf.Min(configuredDepthMeters,
            Mathf.Max(0f, availableLayerThicknessMeters - floorClearanceMeters));
    }

    /// <summary>Choose the joint rotation direction that increases a point's world-space height.</summary>
    /// <param name="jointAxisWorld">The normalized or unnormalized world-space rotation axis.</param>
    /// <param name="pivotToPointWorld">The world-space vector from the joint pivot to the point.</param>
    /// <returns>Positive one, negative one, or zero when rotation cannot raise the point.</returns>
    public static float GetHeightImprovingRotationSign(Vector3 jointAxisWorld, Vector3 pivotToPointWorld)
    {
        if (jointAxisWorld.sqrMagnitude < 1e-8f || pivotToPointWorld.sqrMagnitude < 1e-8f)
            return 0f;
        float verticalDerivative = Vector3.Cross(jointAxisWorld.normalized, pivotToPointWorld).y;
        return Mathf.Abs(verticalDerivative) < 1e-5f ? 0f : Mathf.Sign(verticalDerivative);
    }

    private Zx200KeyboardController keyboard;
    private readonly List<AutonomousExcavationState> stateHistory = new List<AutonomousExcavationState>();
    private readonly List<Collider> machineCollisionScratch = new List<Collider>();
    private readonly List<Collider> truckCollisionScratch = new List<Collider>();
    private Coroutine jobRoutine;
    private Coroutine resetRoutine;
    private PropertyInfo siteReadyProperty;
    private double taskTargetVolumeM3;
    private double taskReceiverStartVolumeM3;
    private double cycleReceiverStartVolumeM3;
    private int nextDigPointIndex;
    private float dumpSwingTarget;
    private bool stageSucceeded;
    private bool dumpOwnsUnloaderEnabledState;
    private bool unloaderEnabledBeforeDump;
    private bool jobOwnsUnloaderEnabledState;
    private bool unloaderEnabledBeforeJob;
    private bool automaticModeOwnsBaseLock;
    private bool fixedBaseBeforeAutomaticMode;
    private bool awaitingManualInputNeutral;
    private bool cuttingSuppressionPending;
    private bool cuttingWasSuppressedBeforeEntry;

    /// <summary>判断实际交付量是否达到正的任务目标。</summary>
    /// <param name="deliveredM3">已实际落地的土量。</param>
    /// <param name="targetM3">任务目标土量。</param>
    /// <returns>目标有效且已交付量达到目标时返回 true。</returns>
    public static bool HasReachedTarget(double deliveredM3, double targetM3)
    {
        return IsFinite(deliveredM3) && IsFinite(targetM3) && targetM3 > 0 && deliveredM3 >= targetM3;
    }

    /// <summary>判断接收区剩余容量是否不足以再完成最小有效转运。</summary>
    /// <param name="availableM3">接收区剩余容量。</param>
    /// <param name="minimumTransferM3">最小有效转运量。</param>
    /// <returns>剩余容量小于等于最小有效转运量时返回 true。</returns>
    public static bool IsReceiverFull(double availableM3, double minimumTransferM3)
    {
        return !IsFinite(availableM3) || !IsFinite(minimumTransferM3) ||
            availableM3 <= Math.Max(0, minimumTransferM3);
    }

    /// <summary>Determine whether the truck can accept a complete next bucket without spilling soil.</summary>
    /// <param name="availableM3">The receiver's current unused volume.</param>
    /// <param name="minimumTransferM3">The minimum meaningful delivery volume.</param>
    /// <param name="loadCapacityM3">The bucket's effective maximum load volume.</param>
    /// <returns>True when the available capacity cannot hold a full bucket load.</returns>
    public static bool HasCapacityForNextLoad(double availableM3, double minimumTransferM3, double loadCapacityM3)
    {
        double required = Math.Max(minimumTransferM3, loadCapacityM3);
        return IsFinite(availableM3) && IsFinite(required) && required > 0 && availableM3 >= required;
    }

    /// <summary>启动使用 Inspector 目标量的自主挖掘装车任务。</summary>
    /// <returns>任务成功启动时返回 true。</returns>
    public bool StartAutonomousJob()
    {
        return StartAutonomousJob(targetVolumeM3);
    }

    /// <summary>启动指定增量土量的自主挖掘装车任务。</summary>
    /// <param name="requestedTargetM3">本次任务相对于当前车厢量的目标增量。</param>
    /// <returns>任务成功启动时返回 true。</returns>
    public bool StartAutonomousJob(double requestedTargetM3)
    {
        if (IsRunning || jobRoutine != null || State == AutonomousExcavationState.Resetting)
            return false;
        if (!ResolveReferences())
        {
            EnterFault("自主流程依赖缺失");
            return false;
        }
        if (!Application.isPlaying)
        {
            EnterFault("自主挖掘只能在播放模式启动");
            return false;
        }
        if (!ValidateSettings(requestedTargetM3))
        {
            EnterFault("自主流程参数无效");
            return false;
        }
        if (!CanStartFromCurrentBase())
        {
            EnterFault("设备已离开校准作业位；请人工返回坡面装车位后再启动自动流程");
            return false;
        }
        if (cutter.VolumeM3 > 1e-5 &&
            cutter.VolumeM3 < minimumTransferVolumeM3)
        {
            EnterFault("斗内已有少量土，但未达到安全转运量；请继续人工收斗或清斗后再启动");
            return false;
        }
        string initialTruckPenetration = FindTruckPenetration();
        if (initialTruckPenetration != null)
        {
            EnterFault("当前姿态已与矿车穿透，请人工脱离后再启动：" + initialTruckPenetration);
            return false;
        }

        taskTargetVolumeM3 = requestedTargetM3;
        taskReceiverStartVolumeM3 = receiver.VolumeM3;
        cycleReceiverStartVolumeM3 = receiver.VolumeM3;
        DeliveredVolumeM3 = 0;
        CompletedCycles = 0;
        FullBucketRecoveryCount = 0;
        PartialBucketRecoveryCount = 0;
        LastCutEntryDepthMeters = 0f;
        nextDigPointIndex = 0;
        stateHistory.Clear();
        FaultReason = string.Empty;
        keyboard.SetAutomaticControl(true);
        awaitingManualInputNeutral = true;
        machine.StopAutomaticMotion();
        LockBaseForAutomaticMotion();
        IsRunning = true;
        unloaderEnabledBeforeJob = unloader.enabled;
        jobOwnsUnloaderEnabledState = true;
        unloader.enabled = false;
        SetState(AutonomousExcavationState.WaitingForSite, "等待施工区域就绪");
        jobRoutine = StartCoroutine(RunJobOwned());
        return jobRoutine != null;
    }

    /// <summary>
    /// 请求人工接管；不回滚已挖土、斗内土或车厢实际接收量。
    /// </summary>
    public void RequestManualTakeover()
    {
        if (!IsRunning) return;
        IsRunning = false;
        awaitingManualInputNeutral = false;
        RestoreUnloaderEnabledState();
        RestoreJobUnloaderEnabledState();
        RestoreCuttingSuppression();
        if (machine != null) machine.StopAutomaticMotion();
        if (keyboard != null) keyboard.SetAutomaticControl(false);
        RestoreBaseDriveMode();
        CurrentMotionAxis = AutonomousMotionAxis.None;
        SetState(AutonomousExcavationState.ManualTakeover, "人工接管，自动流程已停止");
    }

    /// <summary>
    /// 停止任务并恢复运行时土区、铲斗、空中土流和接收区记录，然后回到待机姿态。
    /// </summary>
    public void ResetAutonomousJob()
    {
        if (jobRoutine != null)
        {
            StopCoroutine(jobRoutine);
            jobRoutine = null;
        }
        RestoreUnloaderEnabledState();
        RestoreJobUnloaderEnabledState();
        RestoreCuttingSuppression();
        CurrentMotionAxis = AutonomousMotionAxis.None;
        if (resetRoutine != null) StopCoroutine(resetRoutine);
        IsRunning = false;
        if (machine != null) machine.StopAutomaticMotion();
        if (Application.isPlaying) LockBaseForAutomaticMotion();
        if (keyboard != null) keyboard.SetAutomaticControl(true);
        if (cutter != null) cutter.ResetExcavation();
        DeliveredVolumeM3 = 0;
        CompletedCycles = 0;
        FullBucketRecoveryCount = 0;
        PartialBucketRecoveryCount = 0;
        LastCutEntryDepthMeters = 0f;
        FaultReason = string.Empty;
        SetState(AutonomousExcavationState.Resetting, "正在复位土区与设备");
        if (!Application.isPlaying)
        {
            if (keyboard != null) keyboard.SetAutomaticControl(false);
            RestoreBaseDriveMode();
            SetState(AutonomousExcavationState.Idle, "已复位，等待启动");
            return;
        }
        resetRoutine = StartCoroutine(ResetRoutine());
    }

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (Input.GetKeyDown(autonomousStartKey)) StartAutonomousJob();
        if (Input.GetKeyDown(KeyCode.Escape)) RequestManualTakeover();
        if (Input.GetKeyDown(KeyCode.Backspace)) ResetAutonomousJob();
        HandleManualInput();
    }

    private void OnDisable()
    {
        if (!IsRunning && !jobOwnsUnloaderEnabledState && !dumpOwnsUnloaderEnabledState &&
            !automaticModeOwnsBaseLock) return;
        IsRunning = false;
        if (jobRoutine != null) StopCoroutine(jobRoutine);
        jobRoutine = null;
        RestoreUnloaderEnabledState();
        RestoreJobUnloaderEnabledState();
        RestoreCuttingSuppression();
        if (machine != null) machine.StopAutomaticMotion();
        if (keyboard != null) keyboard.SetAutomaticControl(false);
        RestoreBaseDriveMode();
        CurrentMotionAxis = AutonomousMotionAxis.None;
    }

    private void OnGUI()
    {
        if (!Application.isPlaying) return;

        GUI.Box(new Rect(12, 258, 600, 136), "自主挖掘装车");
        GUI.Label(new Rect(24, 282, 575, 22), $"阶段：{State}  |  {Status}");
        GUI.Label(new Rect(24, 304, 575, 22), $"当前动作：{GetMotionAxisLabel()}");
        GUI.Label(new Rect(24, 327, 575, 22),
            $"完成斗数：{CompletedCycles}  |  已装车：{DeliveredVolumeM3:F3} m³ / 目标 {taskTargetVolumeM3:F3} m³");
        GUI.Label(new Rect(24, 350, 575, 22),
            $"斗内：{(cutter != null ? cutter.VolumeM3 : 0):F3} m³  |  P 启动  Esc 人工接管  Backspace 复位");
        if (!string.IsNullOrEmpty(FaultReason))
            GUI.Label(new Rect(24, 370, 575, 20), "故障：" + FaultReason);
    }

    private IEnumerator RunJobOwned()
    {
        try { yield return RunJob(); }
        finally { jobRoutine = null; }
    }

    private IEnumerator RunJob()
    {
        while (IsRunning && awaitingManualInputNeutral)
            yield return null;
        if (!IsRunning) yield break;
        yield return WaitForSiteReady();
        if (!IsRunning || !stageSucceeded) yield break;
        // Cesium site preparation restores the scene's manual-drive setting when it becomes ready.
        // Reassert the autonomous lock before the first machine motion.
        LockBaseForAutomaticMotion();
        bool carryManualBucket = cutter.VolumeM3 >= minimumTransferVolumeM3;

        while (IsRunning)
        {
            if (carryManualBucket)
            {
                carryManualBucket = false;
                if (receiver.AvailableM3 < cutter.VolumeM3)
                {
                    Finish(AutonomousExcavationState.TruckFull,
                        "车厢余量不足以接收人工已装入铲斗的土量");
                    yield break;
                }
            }
            else
            {
                SetState(AutonomousExcavationState.AcquirePoint, "检查当前挖点和车厢容量");
                yield return AcquirePoint();
                if (!IsRunning || !stageSucceeded) yield break;

                SetState(AutonomousExcavationState.LowerAndEnter, "动臂下放并进入土层");
                yield return PrepareForCut();
                if (!IsRunning) yield break;
                if (State == AutonomousExcavationState.EmptyCut)
                {
                    nextDigPointIndex++;
                    yield return RecoverFromEmptyCut();
                    if (!IsRunning || !stageSucceeded) yield break;
                    continue;
                }
                if (!stageSucceeded) yield break;

                SetState(AutonomousExcavationState.FillBucket, "收斗切削并等待有效进斗量");
                yield return FillBucket();
                if (!IsRunning || !stageSucceeded) yield break;
                if (State == AutonomousExcavationState.EmptyCut)
                {
                    nextDigPointIndex++;
                    yield return RecoverFromEmptyCut();
                    if (!IsRunning || !stageSucceeded) yield break;
                    continue;
                }
            }

            SetState(AutonomousExcavationState.Lift, "从当前姿态抬升并保土");
            yield return RaiseToDumpClearance();
            if (!IsRunning || !stageSucceeded) yield break;

            SetState(AutonomousExcavationState.SwingToTruck, "回转至车厢并准备卸料");
            yield return AlignDumpSwing();
            if (!IsRunning || !stageSucceeded) yield break;
            Zx200JointPose actualAtDump = machine.ReadActualPose();
            dumpSwingTarget = actualAtDump.swing;
            if (!IsDumpApproachSafe())
            {
                Zx200JointPose unsafePose = machine.ReadActualPose();
                EnterFault($"卸料准备姿态未位于车厢上方安全区域；回转/动臂/斗杆/铲斗=" +
                    $"({unsafePose.swing:F1},{unsafePose.boom:F1},{unsafePose.arm:F1},{unsafePose.bucket:F1})，" +
                    $"斗尖={cutter.TipWorldPosition}，要求高度={GetTruckClearanceY() + dumpClearanceMarginMeters:F2}米，" +
                    $"接收区判定={IsBucketOverReceiver()}");
                yield break;
            }

            unloaderEnabledBeforeDump = unloader.enabled;
            dumpOwnsUnloaderEnabledState = true;
            unloader.enabled = false;
            try
            {
                SetState(AutonomousExcavationState.PrepareDump, "向车厢侧展开斗杆并重新校准卸料落点");
                Zx200JointPose actualBeforeDump = machine.ReadActualPose();
                Zx200JointPose deployForDump = CreatePreSwingApproachPose(
                    actualBeforeDump, machine.dumpPreparePose, actualBeforeDump.bucket);
                deployForDump.duration = Mathf.Max(1.4f, machine.dumpPreparePose.duration);
                yield return MoveCoordinatedPose(
                    deployForDump, "车厢侧展开定位超时", AutonomousMotionAxis.Arm);
                if (!IsRunning || !stageSucceeded) yield break;

                // The open-tip projection changes as the arm extends. Recenter with the bucket
                // still closed, then validate the final open position before enabling soil flow.
                yield return AlignCurrentSwing(false);
                if (!IsRunning) yield break;
                if (!stageSucceeded || !IsProjectedDumpApproachSafe())
                {
                    yield return RecenterLoadedBucketForDump();
                    if (!IsRunning) yield break;
                }
                if (!stageSucceeded || !IsProjectedDumpApproachSafe())
                {
                    Zx200JointPose unsafePose = machine.ReadActualPose();
                    EnterFault($"车厢侧展开后无法安全校位，尚未打开铲斗；姿态=" +
                        $"({unsafePose.swing:F1},{unsafePose.boom:F1},{unsafePose.arm:F1},{unsafePose.bucket:F1})，" +
                        $"预计开斗斗尖={GetPredictedOpenTip()}，" +
                        $"车厢局部斗尖={receiver.transform.InverseTransformPoint(GetPredictedOpenTip())}，" +
                        $"接收区判定={IsProjectedDumpCentered()}");
                    yield break;
                }

                SetState(AutonomousExcavationState.Dump, "打开铲斗并等待土流落入车厢");
                cycleReceiverStartVolumeM3 = receiver.VolumeM3;
                Zx200JointPose dump = machine.dumpPose;
                Zx200JointPose actualDumpPose = machine.ReadActualPose();
                dump.swing = actualDumpPose.swing;
                dump.boom = actualDumpPose.boom;
                dump.arm = actualDumpPose.arm;
                bool safeDumpPose = false;
                ArticulationDrive boomDrive = machine.boomJoint.xDrive;
                for (int attempt = 0; IsRunning && attempt < 9; attempt++)
                {
                    yield return AlignCurrentSwing(false);
                    if (!IsRunning) yield break;
                    if (stageSucceeded && IsProjectedDumpApproachSafe())
                    {
                        safeDumpPose = true;
                        break;
                    }

                    Zx200JointPose candidate = machine.ReadActualPose();
                    Vector3 boomAxis = machine.boomJoint.transform.TransformDirection(
                        machine.boomJoint.anchorRotation * Vector3.right).normalized;
                    Vector3 boomPivot = machine.boomJoint.transform.TransformPoint(
                        machine.boomJoint.anchorPosition);
                    float correctionSign = GetHeightImprovingRotationSign(
                        boomAxis, cutter.TipWorldPosition - boomPivot);
                    if (Mathf.Approximately(correctionSign, 0f)) break;
                    candidate.boom = Zx200DigController.ClampTarget(
                        candidate.boom + correctionSign * 5f,
                        boomDrive.lowerLimit,
                        boomDrive.upperLimit);
                    candidate.duration = Mathf.Max(1f, machine.dumpPose.duration);
                    if (Mathf.Approximately(candidate.boom, machine.ReadActualPose().boom)) break;
                    yield return MoveToPose(candidate, "搜索安全卸料斗尖位置超时", -1f, false);
                    if (!IsRunning) yield break;
                }
                if (!safeDumpPose)
                {
                    Zx200JointPose unsafePose = machine.ReadActualPose();
                    EnterFault($"未找到车厢上方安全卸料姿态；回转/动臂/斗杆/铲斗=" +
                        $"({unsafePose.swing:F1},{unsafePose.boom:F1},{unsafePose.arm:F1},{unsafePose.bucket:F1})，" +
                        $"斗尖={cutter.TipWorldPosition}，要求高度={GetTruckClearanceY() + dumpClearanceMarginMeters:F2}米");
                    yield break;
                }
                // The projected open tip was checked above; enable soil flow during the opening stroke
                // so the bucket starts emptying as its mouth rotates, not only at the final angle.
                dump = machine.ReadActualPose();
                dump.bucket = machine.dumpPose.bucket;
                dump.duration = Mathf.Max(1.2f, machine.dumpPose.duration);
                unloader.enabled = true;
                yield return MoveToPose(dump, "卸料姿态超时");
                if (!IsRunning || !stageSucceeded) yield break;
                if (!IsBucketOverReceiver())
                {
                    Vector3 openedTip = cutter.TipWorldPosition;
                    Vector3 openedTipInBed = receiver.transform.InverseTransformPoint(openedTip);
                    EnterFault($"铲斗打开后斗尖未处于车厢有效接收范围，未释放土方；" +
                        $"斗尖={openedTip}，车厢局部坐标={openedTipInBed}，" +
                        $"接收面高={receiver.SurfaceY:F2}m，最大落差={MaximumDumpDropMeters:F2}m");
                    yield break;
                }
                yield return WaitForDump();
                if (!IsRunning || !stageSucceeded) yield break;
            }
            finally
            {
                RestoreUnloaderEnabledState();
            }

            SetState(AutonomousExcavationState.ConfirmDump, "核对车厢实际接收量");
            yield return null;
            if (!IsRunning) yield break;
            UpdateDeliveredVolume();
            double cycleDelivered = receiver.VolumeM3 - cycleReceiverStartVolumeM3;
            if (cycleDelivered < minimumTransferVolumeM3)
            {
                EnterFault("卸料完成但车厢没有实际接收土量");
                yield break;
            }

            CompletedCycles++;
            SetState(AutonomousExcavationState.ReturnToDig, "返回挖掘方向");
            yield return ReturnToDigPoint();
            if (!IsRunning || !stageSucceeded) yield break;

            UpdateDeliveredVolume();
            if (HasReachedTarget(DeliveredVolumeM3, taskTargetVolumeM3))
            {
                Finish(AutonomousExcavationState.Completed, "达到目标量，已返回挖掘方向，单斗/多斗自主装车完成");
                yield break;
            }

            if (IsReceiverFull(receiver.AvailableM3, minimumTransferVolumeM3))
            {
                Finish(AutonomousExcavationState.TruckFull, "车厢已满，已返回挖掘方向，自动流程正常停止");
                yield break;
            }
        }
    }

    private IEnumerator WaitForSiteReady()
    {
        float deadline = Time.realtimeSinceStartup + siteReadyTimeoutSeconds;
        while (IsRunning && !IsSiteReady() && Time.realtimeSinceStartup < deadline)
        {
            HandleManualInput();
            yield return null;
        }
        if (!IsRunning) yield break;
        if (!IsSiteReady())
        {
            EnterFault("Cesium 施工区域在限定时间内未就绪");
            yield break;
        }
        stageSucceeded = true;
    }

    private IEnumerator AcquirePoint()
    {
        stageSucceeded = false;
        if (!ResolveReferences())
        {
            EnterFault("挖点依赖缺失");
            yield break;
        }
        if (cutter.VolumeM3 > GetBucketResidualToleranceM3())
        {
            EnterFault("开始新一斗前铲斗仍有未处理土量");
            yield break;
        }
        if (receiver == null || !HasCapacityForNextLoad(
                receiver.AvailableM3, minimumTransferVolumeM3, cutter.EffectiveCapacityM3))
        {
            double available = receiver != null ? receiver.AvailableM3 : 0;
            Finish(AutonomousExcavationState.TruckFull,
                $"车厢余量不足以接收完整一斗（余量={available:F3} m³，" +
                $"单斗上限={cutter.EffectiveCapacityM3:F3} m³，接收区={receiver?.name ?? "缺失"}）");
            yield break;
        }
        if (cutter.soil == null || !cutter.soil.isActiveAndEnabled)
        {
            EnterFault("可挖土区未启用");
            yield break;
        }
        if (nextDigPointIndex >= DigPointSwingOffsetsDegrees.Length)
        {
            Finish(AutonomousExcavationState.SoilExhausted, "已遍历全部预设可达挖点，停止继续切削");
            yield break;
        }
        Zx200JointPose digPointPose = machine.workReadyPose;
        digPointPose.swing += DigPointSwingOffsetsDegrees[nextDigPointIndex];
        if (!machine.IsPoseWithinTolerance(digPointPose, machine.jointToleranceDegrees))
        {
            yield return MoveToPose(digPointPose, "进入下一个挖点时待机姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;
        }
        if (!cutter.StartupReady)
        {
            float readyDeadline = Time.realtimeSinceStartup + stageTimeoutSeconds;
            while (IsRunning && !cutter.StartupReady && Time.realtimeSinceStartup < readyDeadline)
            {
                HandleManualInput();
                yield return new WaitForFixedUpdate();
            }
            if (!IsRunning) yield break;
            if (!cutter.StartupReady)
            {
                EnterFault("待机姿态未稳定，切削器尚未解锁；停止自动挖掘");
                yield break;
            }
        }
        if (!FiniteMetrics())
        {
            EnterFault("挖点土量数据不是有限数值");
            yield break;
        }
        // Reuse this point after a productive bucket. Only an empty or unsafe cut
        // advances to the next reachable swing offset.
        stageSucceeded = true;
    }

    private IEnumerator RecoverFromEmptyCut()
    {
        // Move only the boom until the tooth is clear. MoveToPose normally changes the
        // bucket first, which can drive an empty, half-open bucket into the slope.
        bool wasSuppressed = cutter.IsCuttingSuppressed;
        // Takeover/fault/reset may restore the entry state before this coroutine unwinds.
        // Do not reapply the pending automatic-cut suppression after that terminal cleanup.
        bool restoreAfterStop = cuttingSuppressionPending
            ? cuttingWasSuppressedBeforeEntry : wasSuppressed;
        cutter.SetCuttingSuppressed(true);
        try
        {
            ArticulationDrive boomDrive = machine.boomJoint.xDrive;
            for (int attempt = 0; IsRunning && attempt < 12; attempt++)
            {
                Vector3 tip = cutter.TipWorldPosition;
                float surface = cutter.soil.Terrain.SampleHeight(tip) + cutter.soil.transform.position.y;
                if (tip.y >= surface + .8f) break;
                Zx200JointPose clearance = machine.ReadActualPose();
                Vector3 axis = machine.boomJoint.transform.TransformDirection(
                    machine.boomJoint.anchorRotation * Vector3.right).normalized;
                Vector3 pivot = machine.boomJoint.transform.TransformPoint(machine.boomJoint.anchorPosition);
                float sign = GetHeightImprovingRotationSign(axis, tip - pivot);
                clearance.boom = Zx200DigController.ClampTarget(clearance.boom + sign * 6f,
                    boomDrive.lowerLimit, boomDrive.upperLimit);
                if (Mathf.Abs(clearance.boom - machine.ReadActualPose().boom) < .5f) break;
                clearance.duration = 1.2f;
                yield return MoveToPose(clearance, "空挖点斗尖脱坡超时");
                if (!IsRunning || !stageSucceeded) yield break;
            }
            Vector3 clearedTip = cutter.TipWorldPosition;
            float clearedSurface = cutter.soil.Terrain.SampleHeight(clearedTip) + cutter.soil.transform.position.y;
            if (clearedTip.y < clearedSurface + .8f)
            {
                EnterFault($"空挖点无法抬斗脱离坡面；斗尖={clearedTip.y:F2}m，土面={clearedSurface:F2}m");
                yield break;
            }
            // Keep this cleared pose; the generic liftPose can lower the empty tooth
            // back onto the changing face before the next work-ready command.
            stageSucceeded = true;
        }
        finally
        {
            cutter.SetCuttingSuppressed(IsRunning ? wasSuppressed : restoreAfterStop);
            if (IsRunning) RestoreCuttingSuppression();
        }
    }

    private IEnumerator FillBucket()
    {
        stageSucceeded = false;
        double before = cutter.VolumeM3;
        double capacity = cutter.EffectiveCapacityM3;
        Zx200JointPose actual = machine.ReadActualPose();
        double targetLoad = Math.Max(before, capacity * targetBucketFillFraction);
        Zx200JointPose target = new Zx200JointPose(
            actual.swing, actual.boom, machine.curlPose.arm,
            machine.curlPose.bucket, 0f);
        float minimumCurlDegrees = Mathf.Lerp(actual.bucket, target.bucket, .8f);
        CurrentMotionAxis = AutonomousMotionAxis.ArmAndBucket;
        var volumeSamples = new List<ScoopVolumeSample>(24)
        {
            new ScoopVolumeSample(Time.fixedTime, before)
        };
        Zx200JointPose continuousTarget = actual;
        continuousTarget.duration = 0f;
        Zx200JointPose previousActual = actual;
        Zx200JointPose lastProgressPose = actual;
        double lastProgressVolume = before;
        float previousActualTime = Time.fixedTime;
        float lastProgressTime = Time.fixedTime;
        float startTime = Time.fixedTime;
        float deadline = startTime + cutTimeoutSeconds;
        float armRate = Mathf.Max(1f, armScoopStepDegrees / Mathf.Max(.5f, machine.curlPose.duration));
        float bucketRate = Mathf.Max(6f, bucketCloseDeltaDegrees / Mathf.Max(.5f, machine.curlPose.duration));
        float boomRate = Mathf.Max(1f, boomEntryStepDegrees / Mathf.Max(.5f, machine.curlPose.duration));
        BucketSoilResistance floorGuard = machine.bucketJoint.GetComponent<BucketSoilResistance>();
        float originalCuttingSpeed = floorGuard != null ? floorGuard.cuttingSpeedDegrees : 0f;
        float floorStopClearance = Mathf.Max(soilFloorMargin,
            floorGuard != null ? floorGuard.contactMargin : 0f) + .005f;

        if (!machine.StartContinuousPoseControl(continuousTarget))
        {
            EnterFault("无法启动按土量反馈的连续切削控制");
            CurrentMotionAxis = AutonomousMotionAxis.None;
            yield break;
        }

        try
        {
            while (IsRunning && Time.fixedTime < deadline)
            {
                HandleManualInput();
                if (!IsRunning) yield break;
                float now = Time.fixedTime;
                actual = machine.ReadActualPose();
                double currentVolume = cutter.VolumeM3;
                Vector3 currentTip = cutter.TipWorldPosition;
                float currentFloorClearance = currentTip.y - cutter.soil.GetFloorWorldY(currentTip);
                if (currentFloorClearance <= floorStopClearance)
                {
                    machine.StopContinuousPoseControl(false);
                    if (currentVolume - before >= minimumLoadVolumeM3)
                        yield return FinishPartialBucketCurl(minimumCurlDegrees, target.bucket);
                    else
                    {
                        SetState(AutonomousExcavationState.EmptyCut,
                            $"斗尖到达底面安全净空；局部净空={currentFloorClearance:F3}m，当前斗量不足，检查下一个挖点");
                        stageSucceeded = true;
                    }
                    yield break;
                }
                volumeSamples.Add(new ScoopVolumeSample(now, currentVolume));
                while (volumeSamples.Count > 2 &&
                    now - volumeSamples[1].time > ScoopRateWindowSeconds)
                    volumeSamples.RemoveAt(0);

                ScoopVolumeSample oldestSample = volumeSamples[0];
                float sampleWindow = now - oldestSample.time;
                double inflowRate = sampleWindow >= MinimumScoopRateWindowSeconds
                    ? EstimateScoopInflowRate(oldestSample.volume, currentVolume, sampleWindow)
                    : 0;
                float actualSampleDelta = now - previousActualTime;
                float actualCurlSpeed = actualSampleDelta > .0001f
                    ? Mathf.Abs(actual.bucket - previousActual.bucket) / actualSampleDelta
                    : 0f;
                previousActual = actual;
                previousActualTime = now;

                float currentFill = capacity > .000001 ? (float)(currentVolume / capacity) : 1f;
                float remainingCurl = Mathf.Abs(target.bucket - actual.bucket);
                float predictedFill = PredictScoopFillFraction(
                    currentVolume,
                    capacity,
                    inflowRate,
                    remainingCurl,
                    actualCurlSpeed,
                    bucketRate * 1.1f);
                AdaptiveScoopMotionCommand command = CalculateAdaptiveScoopMotionCommand(
                    currentFill, predictedFill);
                if (currentVolume >= targetLoad) command.armAdvanceScale = 0f;

                // Ease both joints as the tooth approaches the prepared floor. A thin layer
                // has little vertical travel left, so the same nominal scoop rate would coast
                // through the bottom before feedback can close the bucket.
                float floorMotionScale = Mathf.Clamp01(
                    (currentFloorClearance - floorStopClearance) / .5f);
                if (floorGuard != null)
                    floorGuard.cuttingSpeedDegrees = Mathf.Lerp(
                        2f, Mathf.Min(18f, originalCuttingSpeed), floorMotionScale);
                command.armAdvanceScale *= Mathf.Lerp(.05f, 1f, floorMotionScale);
                // Near the protected floor, stop pushing the arm forward but keep the
                // bucket curling; throttling both together recreated the late-close stall.
                command.bucketCurlScale *= Mathf.Lerp(.65f, 1f, floorMotionScale);

                float stepSeconds = Time.fixedDeltaTime;
                continuousTarget.arm = Mathf.MoveTowards(
                    continuousTarget.arm,
                    target.arm,
                    armRate * command.armAdvanceScale * stepSeconds);
                continuousTarget.bucket = Mathf.MoveTowards(
                    continuousTarget.bucket,
                    target.bucket,
                    bucketRate * command.bucketCurlScale * stepSeconds);
                if (command.boomLiftScale > 0f)
                {
                    Vector3 axis = machine.boomJoint.transform.TransformDirection(
                        machine.boomJoint.anchorRotation * Vector3.right).normalized;
                    Vector3 pivot = machine.boomJoint.transform.TransformPoint(machine.boomJoint.anchorPosition);
                    float liftSign = GetHeightImprovingRotationSign(axis, cutter.TipWorldPosition - pivot);
                    if (liftSign != 0f)
                    {
                        float maximumAssist = Zx200DigController.ClampTarget(
                            actual.boom + liftSign * MaximumAdaptiveBoomLiftDegrees,
                            machine.boomJoint.xDrive.lowerLimit,
                            machine.boomJoint.xDrive.upperLimit);
                        continuousTarget.boom = Mathf.MoveTowards(
                            continuousTarget.boom,
                            maximumAssist,
                            boomRate * command.boomLiftScale * stepSeconds);
                    }
                }

                continuousTarget.duration = 0f;
                if (!machine.UpdateContinuousPoseTarget(continuousTarget))
                {
                    machine.StopContinuousPoseControl(false);
                    EnterFault("按土量更新连续切削目标失败");
                    yield break;
                }

                if (cuttingSuppressionPending)
                {
                    const float minimumCoordinatedStartAngle = .05f;
                    bool armStarted = Mathf.Abs(actual.arm - lastProgressPose.arm) >= minimumCoordinatedStartAngle ||
                        Mathf.Abs(continuousTarget.arm - actual.arm) >= minimumCoordinatedStartAngle;
                    bool bucketStarted = Mathf.Abs(actual.bucket - lastProgressPose.bucket) >= minimumCoordinatedStartAngle ||
                        Mathf.Abs(continuousTarget.bucket - actual.bucket) >= minimumCoordinatedStartAngle;
                    if (armStarted && bucketStarted) RestoreCuttingSuppression();
                }

                bool bucketMostlyCurled = target.bucket >= actual.bucket
                    ? actual.bucket >= minimumCurlDegrees
                    : actual.bucket <= minimumCurlDegrees;
                if (currentVolume >= targetLoad && bucketMostlyCurled)
                {
                    machine.StopContinuousPoseControl(true);
                    stageSucceeded = true;
                    yield break;
                }

                bool poseProgressed = Mathf.Abs(actual.boom - lastProgressPose.boom) >= .15f ||
                    Mathf.Abs(actual.arm - lastProgressPose.arm) >= .15f ||
                    Mathf.Abs(actual.bucket - lastProgressPose.bucket) >= .15f;
                bool soilProgressed = currentVolume - lastProgressVolume >= .00002;
                if (poseProgressed || soilProgressed)
                {
                    lastProgressTime = now;
                    lastProgressPose = actual;
                    lastProgressVolume = currentVolume;
                }

                if (now - lastProgressTime >= ScoopNoProgressTimeoutSeconds &&
                    now - startTime >= ScoopNoProgressTimeoutSeconds)
                {
                    machine.StopContinuousPoseControl(false);
                    if (!bucketMostlyCurled && currentVolume >= capacity - .0001)
                    {
                        yield return FinishFullBucketCurl(minimumCurlDegrees, target.bucket);
                        yield break;
                    }
                    if (currentVolume - before >= minimumLoadVolumeM3 && bucketMostlyCurled)
                    {
                        stageSucceeded = true;
                        yield break;
                    }
                    if (currentVolume - before >= minimumLoadVolumeM3)
                    {
                        yield return FinishPartialBucketCurl(minimumCurlDegrees, target.bucket);
                        yield break;
                    }
                    SetState(AutonomousExcavationState.EmptyCut,
                        $"切削停滞 {ScoopNoProgressTimeoutSeconds:F2}s，当前斗量不足；阻力状态={GetCutResistanceStatus()}");
                    stageSucceeded = true;
                    yield break;
                }

                yield return new WaitForFixedUpdate();
            }

            if (!IsRunning) yield break;
            if (machine.IsBusy) machine.StopContinuousPoseControl(false);
            Zx200JointPose cutEnd = machine.ReadActualPose();
            bool curlComplete = target.bucket >= actual.bucket
                ? cutEnd.bucket >= minimumCurlDegrees
                : cutEnd.bucket <= minimumCurlDegrees;
            double finalLoad = cutter.VolumeM3;
            if (!curlComplete && finalLoad >= capacity - .0001)
            {
                yield return FinishFullBucketCurl(minimumCurlDegrees, target.bucket);
                yield break;
            }
            if (!curlComplete && finalLoad - before >= minimumLoadVolumeM3)
            {
                yield return FinishPartialBucketCurl(minimumCurlDegrees, target.bucket);
                yield break;
            }
            if (finalLoad - before >= minimumLoadVolumeM3)
            {
                stageSucceeded = true;
                yield break;
            }

            bool atFloor = cutter.TipWorldPosition.y <=
                cutter.soil.GetFloorWorldY(cutter.TipWorldPosition) + soilFloorMargin;
            SetState(AutonomousExcavationState.EmptyCut,
                atFloor ? "当前挖点已到土层底部，继续检查下一个可达挖点" :
                $"当前挖点切削无效或进度停滞，阻力状态={GetCutResistanceStatus()}");
            stageSucceeded = true;
        }
        finally
        {
            machine.StopContinuousPoseControl(false);
            if (floorGuard != null) floorGuard.cuttingSpeedDegrees = originalCuttingSpeed;
            CurrentMotionAxis = AutonomousMotionAxis.None;
        }
    }

    private string GetCutResistanceStatus()
    {
        BucketSoilResistance resistance = machine != null && machine.bucketJoint != null
            ? machine.bucketJoint.GetComponent<BucketSoilResistance>() : null;
        string reason = resistance != null ? resistance.BlockReason : "未配置切削阻力组件";
        if (cutter == null || cutter.soil == null || cutter.soil.Terrain == null) return reason;

        Vector3 tip = cutter.TipWorldPosition;
        float surface = cutter.soil.Terrain.SampleHeight(tip) + cutter.soil.transform.position.y;
        float floor = cutter.soil.GetFloorWorldY(tip);
        return $"{reason}；土层厚={Mathf.Max(0f, surface - floor):F3}m，" +
            $"斗尖离地={tip.y - floor:F3}m，刃口埋深={surface - tip.y:F3}m";
    }

    private IEnumerator FinishFullBucketCurl(float minimumCurlDegrees, float targetBucketDegrees)
    {
        FullBucketRecoveryCount++;
        yield return FinishBucketCurlRecovery(minimumCurlDegrees, targetBucketDegrees, "满斗");
    }

    private IEnumerator FinishPartialBucketCurl(float minimumCurlDegrees, float targetBucketDegrees)
    {
        PartialBucketRecoveryCount++;
        yield return FinishBucketCurlRecovery(minimumCurlDegrees, targetBucketDegrees, "部分装土");
    }

    private IEnumerator FinishBucketCurlRecovery(
        float minimumCurlDegrees, float targetBucketDegrees, string loadDescription)
    {
        // A floor-limited scoop cannot safely finish curling while the tooth is embedded.
        // Withdraw the loaded bucket above the face first, then complete containment.
        ArticulationDrive boomDrive = machine.boomJoint.xDrive;
        for (int attempt = 0; IsRunning && attempt < 8; attempt++)
        {
            Vector3 tip = cutter.TipWorldPosition;
            float surface = cutter.soil.Terrain.SampleHeight(tip) + cutter.soil.transform.position.y;
            if (tip.y >= surface + .12f) break;
            Zx200JointPose withdrawal = machine.ReadActualPose();
            Vector3 axis = machine.boomJoint.transform.TransformDirection(
                machine.boomJoint.anchorRotation * Vector3.right).normalized;
            Vector3 pivot = machine.boomJoint.transform.TransformPoint(machine.boomJoint.anchorPosition);
            float sign = GetHeightImprovingRotationSign(axis, tip - pivot);
            withdrawal.boom = Zx200DigController.ClampTarget(withdrawal.boom + sign * 6f,
                boomDrive.lowerLimit, boomDrive.upperLimit);
            if (Mathf.Abs(withdrawal.boom - machine.ReadActualPose().boom) < .5f) break;
            withdrawal.duration = 1.2f;
            yield return MoveToPose(withdrawal, $"{loadDescription}小幅脱离切削面超时");
            if (!IsRunning || !stageSucceeded) yield break;
        }

        Vector3 clearedTip = cutter.TipWorldPosition;
        float clearedSurface = cutter.soil.Terrain.SampleHeight(clearedTip) + cutter.soil.transform.position.y;
        if (clearedTip.y < clearedSurface + .12f)
        {
            EnterFault($"{loadDescription}收斗前无法脱离坡面；斗尖={clearedTip.y:F2}m，土面={clearedSurface:F2}m");
            yield break;
        }

        Zx200JointPose close = machine.ReadActualPose();
        close.bucket = targetBucketDegrees;
        close.duration = Mathf.Max(1.5f, machine.curlPose.duration);
        BucketSoilResistance resistance = machine.bucketJoint.GetComponent<BucketSoilResistance>();
        bool resistanceWasEnabled = resistance != null && resistance.enabled;
        bool cuttingWasSuppressed = cutter.IsCuttingSuppressed;
        cutter.SetCuttingSuppressed(true);
        if (resistanceWasEnabled) resistance.enabled = false;
        try
        {
            // Once clear of the face, finish containing the measured load. The cutting
            // guard must not reinterpret this carry curl as a new downward cut.
            yield return MoveCoordinatedPose(close, $"{loadDescription}脱离土面后收斗超时",
                AutonomousMotionAxis.ArmAndBucket);
        }
        finally
        {
            if (resistanceWasEnabled && resistance != null) resistance.enabled = true;
            cutter.SetCuttingSuppressed(cuttingWasSuppressed);
        }
        if (!IsRunning || !stageSucceeded) yield break;
        float closed = machine.ReadActualPose().bucket;
        if (closed < minimumCurlDegrees)
            EnterFault($"{loadDescription}脱离土面后仍未收斗；实际={closed:F1}°，目标至少={minimumCurlDegrees:F1}°");
    }

    private IEnumerator PrepareForCut()
    {
        stageSucceeded = false;
        if (cutter == null)
        {
            EnterFault("切入准备阶段缺少铲斗土量计量器");
            yield break;
        }
        if (!cuttingSuppressionPending)
        {
            cuttingWasSuppressedBeforeEntry = cutter.IsCuttingSuppressed;
            cutter.SetCuttingSuppressed(true);
            cuttingSuppressionPending = true;
        }
        Zx200JointPose actual = machine.ReadActualPose();
        Zx200JointPose approach = machine.approachPose;
        approach.swing = actual.swing;
        if (!machine.StartAutomaticPose(approach))
        {
            EnterFault("无法启动坡面切入准备姿态");
            yield break;
        }
        float approachDeadline = Time.realtimeSinceStartup + stageTimeoutSeconds;
        while (IsRunning && machine.IsBusy && Time.realtimeSinceStartup < approachDeadline)
        {
            HandleManualInput();
            Vector3 tip = cutter.TipWorldPosition;
            if (cutter.soil.HasSurfaceAt(tip))
            {
                float surface = cutter.soil.Terrain.SampleHeight(tip) + cutter.soil.transform.position.y;
                BucketSoilResistance resistance = machine.bucketJoint.GetComponent<BucketSoilResistance>();
                if (resistance != null && surface - tip.y >= resistance.maximumPenetration)
                {
                    machine.StopAutomaticMotion();
                    EnterFault("坡面准备动作斗齿提前深入土层，已停止关节运动");
                    yield break;
                }
            }
            yield return new WaitForFixedUpdate();
        }
        if (!IsRunning) yield break;
        if (machine.IsBusy || !machine.IsPoseWithinTolerance(approach, machine.jointToleranceDegrees))
        {
            machine.StopAutomaticMotion();
            EnterFault("坡面切入准备姿态超时");
            yield break;
        }

        actual = machine.ReadActualPose();
        Vector3 entryTip = cutter.TipWorldPosition;
        if (!cutter.soil.HasSurfaceAt(entryTip))
        {
            LastCutEntryDepthMeters = 0f;
            SetState(AutonomousExcavationState.EmptyCut, "当前挖点没有可切削地表，转到下一个挖点");
            stageSucceeded = true;
            yield break;
        }
        float entrySurface = cutter.soil.Terrain.SampleHeight(entryTip) + cutter.soil.transform.position.y;
        float entryFloor = cutter.soil.GetFloorWorldY(entryTip);
        float availableLayerThickness = Mathf.Max(0f, entrySurface - entryFloor);
        BucketSoilResistance entryResistance = machine.bucketJoint.GetComponent<BucketSoilResistance>();
        float contactClearance = entryResistance != null ? entryResistance.contactMargin : 0f;
        // Reserve room for the articulation controller to brake before the discrete floor guard engages.
        float floorClearance = Mathf.Max(soilFloorMargin, contactClearance) + .025f;
        float adaptiveEntryDepth = CalculateAdaptiveCutEntryDepth(
            cutEntryDepthMeters, availableLayerThickness, floorClearance);
        LastCutEntryDepthMeters = adaptiveEntryDepth;
        if (adaptiveEntryDepth <= .001f)
        {
            SetState(AutonomousExcavationState.EmptyCut,
                $"当前挖点安全土层不足；土层厚度={availableLayerThickness:F3}m，" +
                $"安全切入深度={adaptiveEntryDepth:F3}m，底部净空要求={floorClearance:F3}m");
            stageSucceeded = true;
            yield break;
        }

        // The fixed approach pose can already put the tooth below a thin local cut floor.
        // Retract to a small above-surface clearance before beginning this point's measured entry.
        const float aboveSurfaceEntryClearance = .08f;
        if (entryTip.y < entrySurface + aboveSurfaceEntryClearance)
        {
            Zx200JointPose retractTarget = machine.ReadActualPose();
            retractTarget.duration = 0f;
            if (!machine.StartContinuousPoseControl(retractTarget))
            {
                EnterFault("无法启动切入前斗尖脱土控制");
                yield break;
            }

            float retractDeadline = Time.realtimeSinceStartup + stageTimeoutSeconds;
            bool clearOfSurface = false;
            while (IsRunning && Time.realtimeSinceStartup < retractDeadline)
            {
                HandleManualInput();
                if (!IsRunning) yield break;
                Vector3 tip = cutter.TipWorldPosition;
                if (!cutter.soil.HasSurfaceAt(tip)) break;
                float surface = cutter.soil.Terrain.SampleHeight(tip) + cutter.soil.transform.position.y;
                if (tip.y >= surface + aboveSurfaceEntryClearance)
                {
                    clearOfSurface = true;
                    break;
                }

                actual = machine.ReadActualPose();
                Vector3 boomAxis = machine.boomJoint.transform.TransformDirection(
                    machine.boomJoint.anchorRotation * Vector3.right).normalized;
                Vector3 boomPivot = machine.boomJoint.transform.TransformPoint(machine.boomJoint.anchorPosition);
                float raiseSign = GetHeightImprovingRotationSign(boomAxis, tip - boomPivot);
                if (raiseSign == 0f)
                {
                    machine.StopContinuousPoseControl(false);
                    EnterFault("切入前无法沿动臂方向抬升斗尖脱离土层");
                    yield break;
                }
                retractTarget.boom = Zx200DigController.ClampTarget(
                    retractTarget.boom + raiseSign * Mathf.Max(1f, boomEntryStepDegrees) * Time.fixedDeltaTime,
                    machine.boomJoint.xDrive.lowerLimit, machine.boomJoint.xDrive.upperLimit);
                retractTarget.duration = 0f;
                if (!machine.UpdateContinuousPoseTarget(retractTarget))
                {
                    machine.StopContinuousPoseControl(false);
                    EnterFault("切入前更新斗尖脱土目标失败");
                    yield break;
                }
                yield return new WaitForFixedUpdate();
            }

            machine.StopContinuousPoseControl(clearOfSurface);
            if (!IsRunning) yield break;
            if (!clearOfSurface)
            {
                EnterFault("切入前斗尖未能退至土面上方安全位置");
                yield break;
            }

            // Retraction may move the tooth across a sloping or irregular local face.
            entryTip = cutter.TipWorldPosition;
            if (!cutter.soil.HasSurfaceAt(entryTip))
            {
                SetState(AutonomousExcavationState.EmptyCut, "斗尖脱土后已离开当前挖点地表，检查下一个挖点");
                stageSucceeded = true;
                yield break;
            }
            entrySurface = cutter.soil.Terrain.SampleHeight(entryTip) + cutter.soil.transform.position.y;
            entryFloor = cutter.soil.GetFloorWorldY(entryTip);
            availableLayerThickness = Mathf.Max(0f, entrySurface - entryFloor);
            adaptiveEntryDepth = CalculateAdaptiveCutEntryDepth(
                cutEntryDepthMeters, availableLayerThickness, floorClearance);
            LastCutEntryDepthMeters = adaptiveEntryDepth;
            if (adaptiveEntryDepth <= .001f)
            {
                SetState(AutonomousExcavationState.EmptyCut,
                    $"斗尖脱土后局部安全土层不足；土层厚度={availableLayerThickness:F3}m，" +
                    $"底部净空要求={floorClearance:F3}m");
                stageSucceeded = true;
                yield break;
            }
        }

        // Lower under live floor feedback at a bounded angular rate. A single long boom target
        // can carry the tooth through a thin layer before the next render/physics observation.
        Zx200JointPose entryTarget = machine.ReadActualPose();
        entryTarget.duration = 0f;
        if (!machine.StartContinuousPoseControl(entryTarget))
        {
            EnterFault("无法启动按局部底面约束的切入控制");
            yield break;
        }
        float entryDeadline = Time.realtimeSinceStartup + stageTimeoutSeconds;
        bool reachedSafeEntry = false;
        while (IsRunning && Time.realtimeSinceStartup < entryDeadline)
        {
            HandleManualInput();
            if (!IsRunning) yield break;
            Vector3 tip = cutter.TipWorldPosition;
            if (!cutter.soil.HasSurfaceAt(tip)) break;
            float surface = cutter.soil.Terrain.SampleHeight(tip) + cutter.soil.transform.position.y;
            float floor = cutter.soil.GetFloorWorldY(tip);
            float safeLowerBound = Mathf.Max(surface - adaptiveEntryDepth, floor + floorClearance);
            if (tip.y <= safeLowerBound)
            {
                reachedSafeEntry = true;
                break;
            }

            actual = machine.ReadActualPose();
            Vector3 boomAxis = machine.boomJoint.transform.TransformDirection(
                machine.boomJoint.anchorRotation * Vector3.right).normalized;
            Vector3 boomPivot = machine.boomJoint.transform.TransformPoint(machine.boomJoint.anchorPosition);
            float raiseSign = GetHeightImprovingRotationSign(boomAxis, tip - boomPivot);
            if (raiseSign == 0f) break;
            entryTarget.boom = Zx200DigController.ClampTarget(
                entryTarget.boom - raiseSign * Mathf.Max(1f, boomEntryStepDegrees) * Time.fixedDeltaTime,
                machine.boomJoint.xDrive.lowerLimit, machine.boomJoint.xDrive.upperLimit);
            entryTarget.duration = 0f;
            if (!machine.UpdateContinuousPoseTarget(entryTarget)) break;
            yield return new WaitForFixedUpdate();
        }

        if (!IsRunning) yield break;
        machine.StopContinuousPoseControl(reachedSafeEntry);
        if (reachedSafeEntry)
        {
            stageSucceeded = true;
            yield break;
        }

        Vector3 failedTip = cutter.TipWorldPosition;
        float failedSurface = cutter.soil.Terrain.SampleHeight(failedTip) + cutter.soil.transform.position.y;
        float failedFloor = cutter.soil.GetFloorWorldY(failedTip);
        SetState(AutonomousExcavationState.EmptyCut,
            $"当前挖点未能到达安全切深，检查下一个挖点；目标切深={adaptiveEntryDepth:F3}m，" +
            $"斗尖={failedTip}，土面={failedSurface:F2}，底面={failedFloor:F2}");
        stageSucceeded = true;
    }

    private IEnumerator WaitForDump()
    {
        stageSucceeded = false;
        float deadline = Time.realtimeSinceStartup + dumpTimeoutSeconds;
        while (IsRunning && Time.realtimeSinceStartup < deadline)
        {
            HandleManualInput();
            UpdateDeliveredVolume();
            bool bucketEmpty = cutter.VolumeM3 <= GetBucketResidualToleranceM3();
            bool flightSettled = unloader.AirborneVolumeM3 <= 1e-5 &&
                unloader.SurfacePendingVolumeM3 <= 1e-5;
            bool receiverAccepted = receiver.VolumeM3 - cycleReceiverStartVolumeM3 >= minimumTransferVolumeM3;
            if (bucketEmpty && flightSettled && receiverAccepted)
            {
                stageSucceeded = true;
                yield break;
            }
            yield return new WaitForFixedUpdate();
        }
        if (IsRunning)
        {
            EnterFault($"卸料超时，未确认土方落入车厢；斗量={cutter.VolumeM3:F4}，" +
                $"空中={unloader.AirborneVolumeM3:F4}，待沉积={unloader.SurfacePendingVolumeM3:F4}，" +
                $"车厢增量={receiver.VolumeM3 - cycleReceiverStartVolumeM3:F4}，" +
                $"斗口状态={unloader.Status}，斗尖={cutter.TipWorldPosition}，车厢={receiver.transform.position}");
        }
    }

    private IEnumerator AlignDumpSwing()
    {
        stageSucceeded = false;
        for (int attempt = 0; IsRunning && attempt < 4; attempt++)
        {
            dumpSwingTarget = ComputeDumpSwingTarget();
            if (!HasPredictedTruckSwingClearance(dumpSwingTarget))
            {
                yield return RaiseBoomToTruckClearance(dumpSwingTarget);
                if (!IsRunning || !stageSucceeded) yield break;
                dumpSwingTarget = ComputeDumpSwingTarget();
                if (!HasPredictedTruckSwingClearance(dumpSwingTarget))
                {
                    Zx200JointPose unsafePose = machine.ReadActualPose();
                    EnterFault($"满斗回转路径未达到车厢净空；路径最低斗尖高=" +
                        $"{GetPredictedMinimumTipHeight(dumpSwingTarget):F2}m，要求高=" +
                        $"{GetTruckSwingStartHeight():F2}m，姿态=" +
                        $"({unsafePose.swing:F1},{unsafePose.boom:F1},{unsafePose.arm:F1},{unsafePose.bucket:F1})");
                    yield break;
                }
            }
            Zx200JointPose actual = machine.ReadActualPose();
            float swingDuration = Mathf.Max(2.5f, Mathf.Abs(Mathf.DeltaAngle(actual.swing, dumpSwingTarget)) / 24f);
            Zx200JointPose swingToDump = new Zx200JointPose(
                dumpSwingTarget, actual.boom, actual.arm, actual.bucket, swingDuration);
            if (!machine.StartAutomaticPose(swingToDump))
            {
                EnterFault("无法启动回转至车厢姿态");
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + stageTimeoutSeconds + swingToDump.duration;
            while (IsRunning && machine.IsBusy && Time.realtimeSinceStartup < deadline)
            {
                HandleManualInput();
                if (State == AutonomousExcavationState.SwingToTruck && !EnsureLoadedSwingClearance())
                {
                    machine.StopAutomaticMotion();
                    yield return RaiseBoomToTruckClearance(dumpSwingTarget);
                    if (!IsRunning || !stageSucceeded) yield break;
                    continue;
                }
                string collision = FindTruckPenetration();
                if (collision != null)
                {
                    machine.StopAutomaticMotion();
                    EnterFault("带土回转与卡车实体发生穿透，已停止：" + collision);
                    yield break;
                }
                if (IsBucketOverReceiver())
                {
                    machine.StopAutomaticMotion();
                    stageSucceeded = true;
                    yield break;
                }
                yield return new WaitForFixedUpdate();
            }
            if (!IsRunning) yield break;
            machine.StopAutomaticMotion();
            if (IsBucketOverReceiver())
            {
                stageSucceeded = true;
                yield break;
            }
        }

        if (IsRunning)
        {
            Zx200JointPose actual = machine.ReadActualPose();
            Vector3 tipInBed = receiver.transform.InverseTransformPoint(cutter.TipWorldPosition);
            EnterFault($"回转未能将斗尖送至车厢接收区；实际回转={actual.swing:F1}°，" +
                $"目标回转={dumpSwingTarget:F1}°，斗尖={cutter.TipWorldPosition}，" +
                $"车厢局部斗尖={tipInBed}，车厢={receiver.transform.position}");
        }
    }

    private IEnumerator AlignCurrentSwing(bool faultOnFailure = true)
    {
        stageSucceeded = false;
        for (int attempt = 0; IsRunning && attempt < 4; attempt++)
        {
            dumpSwingTarget = ComputeDumpSwingTarget(true);
            if ((State == AutonomousExcavationState.SwingToTruck ||
                State == AutonomousExcavationState.PrepareDump) &&
                !HasPredictedTruckSwingClearance(dumpSwingTarget))
            {
                yield return RaiseBoomToTruckClearance(dumpSwingTarget);
                if (!IsRunning || !stageSucceeded) yield break;
                dumpSwingTarget = ComputeDumpSwingTarget(true);
                if (!HasPredictedTruckSwingClearance(dumpSwingTarget))
                {
                    stageSucceeded = false;
                    if (faultOnFailure)
                        EnterFault($"卸料对准路径净空不足；预计最低斗尖高=" +
                            $"{GetPredictedMinimumTipHeight(dumpSwingTarget):F2}m，要求高={GetTruckSwingStartHeight():F2}m");
                    yield break;
                }
            }
            Zx200JointPose actual = machine.ReadActualPose();
            Zx200JointPose target = new Zx200JointPose(
                dumpSwingTarget, actual.boom, actual.arm, actual.bucket,
                Mathf.Max(.7f, Mathf.Abs(Mathf.DeltaAngle(actual.swing, dumpSwingTarget)) / 24f));
            if (!machine.StartAutomaticPose(target))
            {
                EnterFault("无法微调卸料回转姿态");
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + stageTimeoutSeconds + target.duration;
            while (IsRunning && machine.IsBusy && Time.realtimeSinceStartup < deadline)
            {
                HandleManualInput();
                if ((State == AutonomousExcavationState.SwingToTruck ||
                    State == AutonomousExcavationState.PrepareDump) && !EnsureLoadedSwingClearance())
                {
                    machine.StopAutomaticMotion();
                    yield return RaiseBoomToTruckClearance(dumpSwingTarget);
                    if (!IsRunning || !stageSucceeded) yield break;
                    continue;
                }
                string collision = FindTruckPenetration();
                if (collision != null)
                {
                    machine.StopAutomaticMotion();
                    EnterFault("卸料对准动作与卡车实体发生穿透，已停止：" + collision);
                    yield break;
                }
                if (IsProjectedDumpCentered())
                {
                    machine.StopAutomaticMotion();
                    stageSucceeded = true;
                    yield break;
                }
                yield return null;
            }
            if (!IsRunning) yield break;
            machine.StopAutomaticMotion();
            if (IsProjectedDumpCentered())
            {
                stageSucceeded = true;
                yield break;
            }
        }

        if (IsRunning)
        {
            Zx200JointPose actual = machine.ReadActualPose();
            stageSucceeded = false;
            if (faultOnFailure)
                EnterFault($"卸料姿态变化后无法重新对准车厢接收区；实际姿态=" +
                    $"({actual.swing:F1},{actual.boom:F1},{actual.arm:F1},{actual.bucket:F1})，" +
                    $"斗尖={cutter.TipWorldPosition}，车厢中心={receiver.transform.position}，" +
                    $"接收区判定={IsBucketOverReceiver()}，车厢尺寸={receiver.size}，" +
                    $"车厢增量={receiver.VolumeM3 - cycleReceiverStartVolumeM3:F4}");
        }
    }

    private IEnumerator RecenterLoadedBucketForDump()
    {
        // Manual entry can alter the opening projection after the first truck-side alignment.
        // Search only small measured arm steps; the ordinary collision and landing checks stay active.
        ArticulationDrive armDrive = machine.armJoint.xDrive;
        for (int attempt = 0; IsRunning && attempt < 6; attempt++)
        {
            if (IsProjectedDumpApproachSafe())
            {
                stageSucceeded = true;
                yield break;
            }
            Zx200JointPose actual = machine.ReadActualPose();
            Vector3 projectedTip = GetPredictedOpenTip();
            Vector3 pivot = machine.armJoint.transform.TransformPoint(machine.armJoint.anchorPosition);
            Vector3 axis = machine.armJoint.transform.TransformDirection(
                machine.armJoint.anchorRotation * Vector3.right).normalized;
            Vector3 currentLocal = receiver.transform.InverseTransformPoint(projectedTip);
            float bestArm = actual.arm;
            float bestScore = Mathf.Abs(currentLocal.z) + Mathf.Abs(currentLocal.x);
            foreach (float delta in new[] { -3f, 3f })
            {
                float candidate = Zx200DigController.ClampTarget(actual.arm + delta,
                    armDrive.lowerLimit, armDrive.upperLimit);
                if (Mathf.Abs(candidate - actual.arm) < 1f) continue;
                Vector3 candidateTip = pivot + Quaternion.AngleAxis(candidate - actual.arm, axis) *
                    (projectedTip - pivot);
                Vector3 candidateLocal = receiver.transform.InverseTransformPoint(candidateTip);
                float score = Mathf.Abs(candidateLocal.z) + Mathf.Abs(candidateLocal.x);
                if (score >= bestScore) continue;
                bestScore = score;
                bestArm = candidate;
            }
            if (Mathf.Abs(bestArm - actual.arm) < 1f) yield break;
            actual.arm = bestArm;
            actual.duration = 1.2f;
            yield return MoveToPose(actual, "车厢侧微调斗杆落点超时");
            if (!IsRunning || !stageSucceeded) yield break;
            yield return AlignCurrentSwing(false);
            if (!IsRunning) yield break;
        }
    }

    private IEnumerator RaiseToDumpClearance()
    {
        stageSucceeded = false;
        ArticulationDrive boomDrive = machine.boomJoint.xDrive;
        ArticulationDrive bucketDrive = machine.bucketJoint.xDrive;
        BucketSoilResistance resistance = machine.bucketJoint.GetComponent<BucketSoilResistance>();
        bool cutterWasEnabled = cutter != null && cutter.enabled;
        bool wasCuttingSuppressed = cutter != null && cutter.IsCuttingSuppressed;
        bool resistanceWasEnabled = resistance != null && resistance.enabled;
        if (cutterWasEnabled) cutter.SetCuttingSuppressed(true);
        if (resistanceWasEnabled) resistance.enabled = false;
        try
        {
            // Only clear the immediate soil contact before the coordinated carry motion.
            Zx200JointPose initialLift = machine.ReadActualPose();
            Vector3 boomAxis = machine.boomJoint.transform.TransformDirection(
                machine.boomJoint.anchorRotation * Vector3.right).normalized;
            Vector3 boomPivot = machine.boomJoint.transform.TransformPoint(machine.boomJoint.anchorPosition);
            float liftSign = GetHeightImprovingRotationSign(boomAxis, cutter.TipWorldPosition - boomPivot);
            initialLift.boom = Zx200DigController.ClampTarget(initialLift.boom + liftSign * 8f,
                boomDrive.lowerLimit, boomDrive.upperLimit);
            initialLift.duration = Mathf.Max(1.2f, machine.liftPose.duration);
            yield return MoveToPose(initialLift, "满斗离开切削面超时");
            if (!IsRunning || !stageSucceeded) yield break;

            Zx200JointPose waypoint = machine.carryPose;
            waypoint.swing = machine.ReadActualPose().swing;
            waypoint.boom = Zx200DigController.ClampTarget(
                waypoint.boom, boomDrive.lowerLimit, boomDrive.upperLimit);
            waypoint.arm = Zx200DigController.ClampTarget(
                waypoint.arm, machine.armJoint.xDrive.lowerLimit, machine.armJoint.xDrive.upperLimit);
            // Preserve the measured closed-bucket angle while tucking the arm and setting carry height.
            waypoint.bucket = machine.ReadActualPose().bucket;
            waypoint.duration = Mathf.Max(2.2f, waypoint.duration);
            yield return MoveCoordinatedPose(waypoint, "挖后携土姿态超时", AutonomousMotionAxis.BoomArmBucket);
            if (!IsRunning || !stageSucceeded) yield break;

            // The arm change can lower the tip; revalidate and raise again before slewing toward the bed.
            yield return RaiseBoomUntilTruckClearance("斗杆伸展后恢复车厢净空超时");
            if (!IsRunning || !stageSucceeded) yield break;

            BucketSoilUnloader mouth = cutter.GetComponent<BucketSoilUnloader>();
            if (mouth != null)
            {
                Zx200JointPose actual = machine.ReadActualPose();
                Vector3 worldMouth = mouth.transform.TransformDirection(mouth.openingNormalLocal).normalized;
                Vector3 bucketAxis = machine.bucketJoint.transform.TransformDirection(
                    machine.bucketJoint.anchorRotation * Vector3.right).normalized;
                float carryBucketAngle = CalculateBucketAngleForUpwardMouth(
                    worldMouth, bucketAxis, actual.bucket,
                    bucketDrive.lowerLimit, bucketDrive.upperLimit);
                if (Mathf.Abs(Mathf.DeltaAngle(actual.bucket, carryBucketAngle)) > .5f)
                {
                    actual.bucket = carryBucketAngle;
                    actual.duration = 1.1f;
                    yield return MoveCoordinatedPose(actual, "携土斗口校平超时", AutonomousMotionAxis.Bucket);
                    if (!IsRunning || !stageSucceeded) yield break;
                }

                float mouthUpDot = Vector3.Dot(
                    mouth.transform.TransformDirection(mouth.openingNormalLocal).normalized, Vector3.up);
                if (mouthUpDot < Mathf.Cos(20f * Mathf.Deg2Rad))
                {
                    EnterFault($"携土姿态无法把斗口收至水平保土范围；斗口上向度={mouthUpDot:F2}");
                    yield break;
                }
            }
        }
        finally
        {
            if (resistanceWasEnabled && resistance != null) resistance.enabled = true;
            if (cutterWasEnabled && cutter != null) cutter.SetCuttingSuppressed(wasCuttingSuppressed);
        }
    }

    private IEnumerator MoveCoordinatedPose(
        Zx200JointPose target, string timeoutReason, AutonomousMotionAxis motionAxis)
    {
        stageSucceeded = false;
        CurrentMotionAxis = motionAxis;
        try
        {
            float startDeadline = Time.realtimeSinceStartup + stageTimeoutSeconds;
            while (IsRunning && machine.IsBusy && Time.realtimeSinceStartup < startDeadline)
            {
                HandleManualInput();
                yield return new WaitForFixedUpdate();
            }
            if (!IsRunning) yield break;
            if (machine.IsBusy)
            {
                EnterFault($"等待关节释放超时，无法执行挖后回收：{timeoutReason}");
                yield break;
            }
            if (!machine.StartAutomaticPose(target))
            {
                EnterFault("无法启动挖后协同姿态");
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + stageTimeoutSeconds + target.duration;
            while (IsRunning && machine.IsBusy && Time.realtimeSinceStartup < deadline)
            {
                HandleManualInput();
                string penetration = FindTruckPenetration();
                if (penetration != null)
                {
                    machine.StopAutomaticMotion();
                    EnterFault("挖后协同动作与矿车实体发生穿透：" + penetration);
                    yield break;
                }
                yield return new WaitForFixedUpdate();
            }
            if (!IsRunning) yield break;
            if (machine.IsBusy || !machine.IsPoseWithinTolerance(target, machine.jointToleranceDegrees))
            {
                machine.StopAutomaticMotion();
                Zx200JointPose failed = machine.ReadActualPose();
                BucketSoilResistance guard = machine.bucketJoint.GetComponent<BucketSoilResistance>();
                EnterFault($"{timeoutReason}；目标姿态=({target.swing:F1},{target.boom:F1}," +
                    $"{target.arm:F1},{target.bucket:F1})，实际姿态=" +
                    $"({failed.swing:F1},{failed.boom:F1},{failed.arm:F1},{failed.bucket:F1})，" +
                    $"斗尖={cutter.TipWorldPosition}，切削保护={guard?.BlockReason}");
                yield break;
            }
            stageSucceeded = true;
        }
        finally
        {
            CurrentMotionAxis = AutonomousMotionAxis.None;
        }
    }

    private IEnumerator RaiseBoomUntilTruckClearance(string timeoutReason)
    {
        stageSucceeded = false;
        ArticulationDrive boomDrive = machine.boomJoint.xDrive;
        const int maximumRecoveryAttempts = 8;
        float requiredHeight = GetTruckClearanceY() + dumpClearanceMarginMeters;
        for (int attempt = 0; IsRunning && attempt < maximumRecoveryAttempts; attempt++)
        {
            if (cutter.TipWorldPosition.y >= requiredHeight)
            {
                stageSucceeded = true;
                yield break;
            }

            Zx200JointPose actual = machine.ReadActualPose();
            Vector3 boomAxis = machine.boomJoint.transform.TransformDirection(
                machine.boomJoint.anchorRotation * Vector3.right).normalized;
            Vector3 boomPivot = machine.boomJoint.transform.TransformPoint(
                machine.boomJoint.anchorPosition);
            float correctionSign = GetHeightImprovingRotationSign(
                boomAxis, cutter.TipWorldPosition - boomPivot);
            float verticalDegrees = Vector3.Cross(boomAxis, cutter.TipWorldPosition - boomPivot).y *
                Mathf.Deg2Rad * correctionSign;
            float neededDegrees = (requiredHeight - cutter.TipWorldPosition.y) /
                Mathf.Max(.02f, verticalDegrees);
            // Near the rim, a one-degree drive change settles before the physical tip clears;
            // command a small recoverable step and remeasure the tip after each step.
            float nextBoom = Zx200DigController.ClampTarget(
                actual.boom + correctionSign * Mathf.Clamp(neededDegrees, 3f, 30f),
                boomDrive.lowerLimit,
                boomDrive.upperLimit);
            if (Mathf.Approximately(correctionSign, 0f) || Mathf.Approximately(nextBoom, actual.boom))
                break;

            Zx200JointPose waypoint = actual;
            waypoint.boom = nextBoom;
            waypoint.duration = Mathf.Max(1.8f, machine.swingToDumpPose.duration);
            // A sub-two-degree correction still matters at the truck rim; do not skip it
            // using the normal coarse joint tolerance.
            yield return MoveToPose(waypoint, timeoutReason, .25f);
            if (!IsRunning || !stageSucceeded) yield break;
        }

        if (IsRunning && cutter.TipWorldPosition.y < requiredHeight)
        {
            EnterFault($"斗尖未越过车厢壁；斗尖高度={cutter.TipWorldPosition.y:F2}，" +
                $"所需高度={requiredHeight:F2}，姿态={machine.ReadActualPose()}");
            yield break;
        }
        stageSucceeded = true;
    }

    private IEnumerator RaiseBoomToTruckClearance(float swingTargetDegrees)
    {
        stageSucceeded = false;
        ArticulationDrive boomDrive = machine.boomJoint.xDrive;
        const int maximumAttempts = 12;
        for (int attempt = 0; IsRunning && attempt < maximumAttempts; attempt++)
        {
            if (HasPredictedTruckSwingClearance(swingTargetDegrees))
            {
                stageSucceeded = true;
                yield break;
            }

            Vector3 axis = machine.boomJoint.transform.TransformDirection(
                machine.boomJoint.anchorRotation * Vector3.right).normalized;
            Vector3 pivot = machine.boomJoint.transform.TransformPoint(machine.boomJoint.anchorPosition);
            float correctionSign = GetHeightImprovingRotationSign(axis, cutter.TipWorldPosition - pivot);
            Zx200JointPose target = machine.ReadActualPose();
            float correctedBoom = Zx200DigController.ClampTarget(
                target.boom + correctionSign * 5f,
                boomDrive.lowerLimit,
                boomDrive.upperLimit);
            if (Mathf.Approximately(correctionSign, 0f) || Mathf.Approximately(correctedBoom, target.boom))
                break;

            target.boom = correctedBoom;
            target.duration = Mathf.Max(1f, machine.swingToDumpPose.duration);
            yield return MoveToPose(target, "车厢上方补充抬升超时");
            if (!IsRunning || !stageSucceeded) yield break;
        }

        if (IsRunning && !HasPredictedTruckSwingClearance(swingTargetDegrees))
            EnterFault($"卸料回转路径未能整体越过车厢壁；预计最低斗尖高=" +
                $"{GetPredictedMinimumTipHeight(swingTargetDegrees):F2}m，要求高={GetTruckSwingStartHeight():F2}m");
        else
            stageSucceeded = true;
    }

    private bool HasPredictedTruckSwingClearance(float swingTargetDegrees)
    {
        return cutter == null || cutter.VolumeM3 <= minimumLoadVolumeM3 ||
            GetPredictedMinimumTipHeight(swingTargetDegrees) >= GetTruckSwingStartHeight();
    }

    private float GetPredictedMinimumTipHeight(float swingTargetDegrees)
    {
        Zx200JointPose actual = machine.ReadActualPose();
        ArticulationBody joint = machine.swingJoint;
        Vector3 axis = joint.transform.TransformDirection(joint.anchorRotation * Vector3.right).normalized;
        Vector3 pivot = joint.transform.TransformPoint(joint.anchorPosition);
        Vector3 offset = cutter.TipWorldPosition - pivot;
        float delta = Mathf.DeltaAngle(actual.swing, swingTargetDegrees);
        float minimumHeight = float.PositiveInfinity;
        const int samples = 24;
        for (int sample = 0; sample <= samples; sample++)
        {
            float progress = sample / (float)samples;
            Vector3 predictedTip = pivot + Quaternion.AngleAxis(delta * progress, axis) * offset;
            minimumHeight = Mathf.Min(minimumHeight, predictedTip.y);
        }
        return minimumHeight;
    }

    private float GetTruckSwingStartHeight()
    {
        return GetTruckSwingClearanceY() + TruckSwingStartReserveMeters;
    }

    private IEnumerator ReturnToDigPoint()
    {
        stageSucceeded = false;
        Zx200JointPose actual = machine.ReadActualPose();
        Zx200JointPose clearTruck = machine.returnSwingPose;
        clearTruck.boom = actual.boom;
        clearTruck.arm = actual.arm;
        clearTruck.bucket = actual.bucket;
        BucketSoilResistance resistance = machine.bucketJoint != null
            ? machine.bucketJoint.GetComponent<BucketSoilResistance>() : null;
        bool cutterWasEnabled = cutter != null && cutter.enabled;
        bool wasCuttingSuppressed = cutter != null && cutter.IsCuttingSuppressed;
        bool resistanceWasEnabled = resistance != null && resistance.enabled;
        if (cutterWasEnabled) cutter.SetCuttingSuppressed(true);
        if (resistanceWasEnabled) resistance.enabled = false;
        try
        {
            yield return MoveToPose(clearTruck, "离开车厢回转姿态超时", 3f);
            if (IsRunning && stageSucceeded)
                yield return MoveToPose(machine.workReadyPose, "多斗循环返回挖点准备姿态超时");
        }
        finally
        {
            if (resistanceWasEnabled && resistance != null) resistance.enabled = true;
            if (cutterWasEnabled && cutter != null) cutter.SetCuttingSuppressed(wasCuttingSuppressed);
        }
    }

    private float GetTruckClearanceY()
    {
        float highest = receiver.transform.position.y;
        Transform truck = receiver.transform.parent != null ? receiver.transform.parent : receiver.transform;
        foreach (Collider collider in truck.GetComponentsInChildren<Collider>(true))
        {
            Vector3 local = receiver.transform.InverseTransformPoint(collider.bounds.center);
            if (Mathf.Abs(local.x) <= receiver.size.x * .5f + .75f &&
                Mathf.Abs(local.z) <= receiver.size.y * .5f + .75f)
                highest = Mathf.Max(highest, collider.bounds.max.y);
        }
        return highest;
    }

    private float GetTruckSwingClearanceY()
    {
        return GetTruckClearanceY() + dumpClearanceMarginMeters + TruckSwingClearanceReserveMeters;
    }

    private bool IsDumpApproachSafe()
    {
        return IsBucketOverReceiver() &&
            cutter.TipWorldPosition.y >= GetTruckClearanceY() + dumpClearanceMarginMeters;
    }

    private bool IsProjectedDumpApproachSafe()
    {
        return IsProjectedDumpCentered() &&
            cutter.TipWorldPosition.y >= GetTruckClearanceY() + dumpClearanceMarginMeters;
    }

    private bool IsProjectedDumpCentered()
    {
        if (!IsProjectedDumpOverReceiver()) return false;
        Vector3 local = receiver.transform.InverseTransformPoint(GetPredictedOpenTip());
        float safeHalfWidth = receiver.size.x * .5f - cutter.innerMouthHalfWidth - .08f;
        return safeHalfWidth > 0f && Mathf.Abs(local.x) <= Mathf.Min(.08f, safeHalfWidth) &&
            Mathf.Abs(local.z) <= receiver.size.y * .5f - .35f;
    }

    private bool IsProjectedDumpOverReceiver()
    {
        return receiver != null &&
            receiver.TryGetLanding(GetPredictedOpenTip(), MaximumDumpDropMeters, out _);
    }

    private Vector3 GetPredictedOpenTip()
    {
        Zx200JointPose actual = machine.ReadActualPose();
        ArticulationBody joint = machine.bucketJoint;
        Vector3 axis = joint.transform.TransformDirection(joint.anchorRotation * Vector3.right).normalized;
        Vector3 pivot = joint.transform.TransformPoint(joint.anchorPosition);
        return pivot + Quaternion.AngleAxis(machine.dumpPose.bucket - actual.bucket, axis) *
            (cutter.TipWorldPosition - pivot);
    }

    private bool CanContinueMotion(bool allowDuringReset)
    {
        return IsRunning || (allowDuringReset && State == AutonomousExcavationState.Resetting);
    }

    private string FindTruckPenetration(float toleranceMeters = TruckCollisionSlopToleranceMeters)
    {
        if (machine == null || receiver == null) return null;
        Transform truckRoot = receiver.transform.parent != null
            ? receiver.transform.parent
            : receiver.transform;
        machineCollisionScratch.Clear();
        truckCollisionScratch.Clear();
        machine.GetComponentsInChildren(true, machineCollisionScratch);
        truckRoot.GetComponentsInChildren(true, truckCollisionScratch);
        foreach (Collider machineCollider in machineCollisionScratch)
        {
            if (!machineCollider.enabled || !machineCollider.gameObject.activeInHierarchy || machineCollider.isTrigger)
                continue;
            foreach (Collider truckCollider in truckCollisionScratch)
            {
                if (!truckCollider.enabled || !truckCollider.gameObject.activeInHierarchy || truckCollider.isTrigger)
                    continue;
                if (!Physics.ComputePenetration(
                    machineCollider,
                    machineCollider.transform.position,
                    machineCollider.transform.rotation,
                    truckCollider,
                    truckCollider.transform.position,
                    truckCollider.transform.rotation,
                    out _,
                    out float distance))
                    continue;
                // Imported convex bucket hulls can show up to 1 cm of contact slop; deeper penetration still stops motion.
                if (distance <= toleranceMeters) continue;

                Vector3 localCenter = receiver.transform.InverseTransformPoint(machineCollider.bounds.center);
                float verticalOverlap = Mathf.Min(machineCollider.bounds.max.y, truckCollider.bounds.max.y) -
                    Mathf.Max(machineCollider.bounds.min.y, truckCollider.bounds.min.y);
                Zx200JointPose actual = machine.ReadActualPose();
                return $"{machineCollider.name} 穿入 {truckCollider.name}；车厢局部中心={localCenter}，" +
                    $"穿入深度={distance:F3}m，高度重叠={verticalOverlap:F3}m，" +
                    $"姿态=({actual.swing:F1},{actual.boom:F1},{actual.arm:F1},{actual.bucket:F1})，斗尖={cutter.TipWorldPosition}";
            }
        }
        return null;
    }

    private bool IsBoomTargetRaisingBucket(float targetBoomDegrees)
    {
        if (machine == null || machine.boomJoint == null || cutter == null) return false;
        Zx200JointPose actual = machine.ReadActualPose();
        Vector3 axis = machine.boomJoint.transform.TransformDirection(
            machine.boomJoint.anchorRotation * Vector3.right).normalized;
        Vector3 pivot = machine.boomJoint.transform.TransformPoint(
            machine.boomJoint.anchorPosition);
        float raiseSign = GetHeightImprovingRotationSign(axis, cutter.TipWorldPosition - pivot);
        return !Mathf.Approximately(raiseSign, 0f) &&
            Mathf.Sign(Mathf.DeltaAngle(actual.boom, targetBoomDegrees)) == raiseSign;
    }

    private IEnumerator MoveToPose(
        Zx200JointPose pose,
        string timeoutReason,
        float completionToleranceDegrees = -1f,
        bool faultOnFailure = true,
        bool allowDuringReset = false)
    {
        stageSucceeded = false;
        try
        {
            // Safety-critical repositioning changes one axis at a time; the loaded carry
            // uses the separately checked coordinated motion path above.
            AutonomousMotionAxis[] sequence =
            {
                AutonomousMotionAxis.Bucket,
                AutonomousMotionAxis.Boom,
                AutonomousMotionAxis.Arm,
                AutonomousMotionAxis.Swing
            };
            foreach (AutonomousMotionAxis axis in sequence)
            {
                Zx200JointPose actual = machine.ReadActualPose();
                float targetAngle = GetPoseAngle(pose, axis);
                float tolerance = completionToleranceDegrees >= 0f
                    ? completionToleranceDegrees : machine.jointToleranceDegrees;
                if (Mathf.Abs(GetPoseAngle(actual, axis) - targetAngle) <= tolerance) continue;

                if (axis == AutonomousMotionAxis.Swing &&
                    State == AutonomousExcavationState.SwingToTruck && !EnsureLoadedSwingClearance())
                {
                    if (faultOnFailure) EnterFault("带土回转前斗尖未达到车厢实体净空高度");
                    yield break;
                }

                Zx200JointPose waypoint = CreateSingleAxisWaypoint(
                    actual, axis, targetAngle, pose.duration);
                CurrentMotionAxis = axis;
                yield return MoveSinglePose(waypoint, timeoutReason, axis, faultOnFailure, allowDuringReset);
                if (!CanContinueMotion(allowDuringReset) || !stageSucceeded) yield break;
            }
            stageSucceeded = true;
        }
        finally
        {
            CurrentMotionAxis = AutonomousMotionAxis.None;
        }
    }

    private IEnumerator MoveSinglePose(
        Zx200JointPose pose,
        string timeoutReason,
        AutonomousMotionAxis axis,
        bool faultOnFailure,
        bool allowDuringReset)
    {
        stageSucceeded = false;
        float startDeadline = Time.realtimeSinceStartup + stageTimeoutSeconds;
        while (CanContinueMotion(allowDuringReset) && machine.IsBusy && Time.realtimeSinceStartup < startDeadline)
        {
            HandleManualInput();
            yield return new WaitForFixedUpdate();
        }
        if (!CanContinueMotion(allowDuringReset)) yield break;
        if (machine.IsBusy)
        {
            if (faultOnFailure)
                EnterFault($"等待前一关节动作释放超时，无法启动{axis}段：{timeoutReason}");
            yield break;
        }
        if (!machine.StartAutomaticPose(pose))
        {
            if (faultOnFailure)
                EnterFault($"无法启动{axis}关节运动段：{timeoutReason}；设备启用={machine.enabled}，" +
                    $"设备忙碌={machine.IsBusy}，目标=({pose.swing:F1},{pose.boom:F1},{pose.arm:F1},{pose.bucket:F1})");
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + stageTimeoutSeconds + pose.duration;
        while (CanContinueMotion(allowDuringReset) && machine.IsBusy && Time.realtimeSinceStartup < deadline)
        {
            HandleManualInput();
            if (RequiresTruckCollisionMonitoring(State))
            {
                bool raisingToClearTruckWall = State == AutonomousExcavationState.SwingToTruck &&
                    axis == AutonomousMotionAxis.Boom && !EnsureLoadedSwingClearance() &&
                    IsBoomTargetRaisingBucket(pose.boom);
                string collision = FindTruckPenetration(raisingToClearTruckWall
                    ? TruckClearanceRecoveryToleranceMeters
                    : TruckCollisionSlopToleranceMeters);
                if (collision != null)
                {
                    machine.StopAutomaticMotion();
                    EnterFault("自动关节动作与卡车实体发生穿透，已停止：" + collision);
                    yield break;
                }
            }
            if (axis == AutonomousMotionAxis.Swing &&
                State == AutonomousExcavationState.SwingToTruck && !EnsureLoadedSwingClearance())
            {
                machine.StopAutomaticMotion();
                EnterFault("带土回转期间斗尖低于车厢实体净空高度，已停止动作");
                yield break;
            }
            yield return new WaitForFixedUpdate();
        }
        if (!CanContinueMotion(allowDuringReset)) yield break;
        bool selectedAxisReached = IsAxisAtTarget(machine.ReadActualPose(), pose, axis,
            machine.jointToleranceDegrees);
        if (selectedAxisReached && (machine.IsBusy || !machine.LastAutomaticMotionSucceeded))
        {
            // The machine-level routine waits for every hold axis; this segment only owns one measured axis.
            machine.StopAutomaticMotion();
            stageSucceeded = true;
            yield break;
        }
        if (machine.IsBusy || !machine.LastAutomaticMotionSucceeded)
        {
            Zx200JointPose actual = machine.ReadActualPose();
            string detail = $"{timeoutReason}；关节段={axis}，" +
                $"目标=({pose.swing:F1},{pose.boom:F1},{pose.arm:F1},{pose.bucket:F1})，" +
                $"实际=({actual.swing:F1},{actual.boom:F1},{actual.arm:F1},{actual.bucket:F1})，" +
                $"斗尖={cutter.TipWorldPosition}，车厢={receiver.transform.position}";
            if (faultOnFailure) EnterFault(detail);
            yield break;
        }
        stageSucceeded = true;
    }

    private bool EnsureLoadedSwingClearance()
    {
        return cutter == null || cutter.VolumeM3 <= minimumLoadVolumeM3 ||
            cutter.TipWorldPosition.y >= GetTruckClearanceY() + dumpClearanceMarginMeters;
    }

    private static float GetPoseAngle(Zx200JointPose pose, AutonomousMotionAxis axis)
    {
        switch (axis)
        {
            case AutonomousMotionAxis.Bucket: return pose.bucket;
            case AutonomousMotionAxis.Boom: return pose.boom;
            case AutonomousMotionAxis.Arm: return pose.arm;
            case AutonomousMotionAxis.Swing: return pose.swing;
            default: return 0f;
        }
    }

    private static bool IsAxisAtTarget(
        Zx200JointPose actual,
        Zx200JointPose target,
        AutonomousMotionAxis axis,
        float toleranceDegrees)
    {
        return Mathf.Abs(GetPoseAngle(actual, axis) - GetPoseAngle(target, axis)) <= toleranceDegrees;
    }

    private IEnumerator ResetRoutine()
    {
        bool resetSucceeded = false;
        BucketSoilResistance resistance = machine != null && machine.bucketJoint != null
            ? machine.bucketJoint.GetComponent<BucketSoilResistance>() : null;
        bool cutterWasEnabled = cutter != null && cutter.enabled;
        bool wasCuttingSuppressed = cutter != null && cutter.IsCuttingSuppressed;
        bool resistanceWasEnabled = resistance != null && resistance.enabled;
        if (cutterWasEnabled) cutter.SetCuttingSuppressed(true);
        if (resistanceWasEnabled) resistance.enabled = false;
        try
        {
            if (machine != null && machine.enabled)
            {
                Zx200JointPose clearTruck = machine.ReadActualPose();
                clearTruck.swing = machine.returnSwingPose.swing;
                clearTruck.duration = Mathf.Max(.8f, machine.returnSwingPose.duration);
                yield return MoveToPose(clearTruck, "复位时回转离开车厢超时", 3f, true, true);
                if (State != AutonomousExcavationState.Resetting || !stageSucceeded) yield break;

                yield return MoveToPose(machine.parkedPose, "复位停放姿态超时", -1f, true, true);
                if (State != AutonomousExcavationState.Resetting || !stageSucceeded) yield break;
            }
            resetSucceeded = true;
        }
        finally
        {
            if (machine != null && machine.IsBusy) machine.StopAutomaticMotion();
            if (resistanceWasEnabled && resistance != null) resistance.enabled = true;
            if (cutterWasEnabled && cutter != null) cutter.SetCuttingSuppressed(wasCuttingSuppressed);
            if (keyboard != null) keyboard.SetAutomaticControl(false);
            RestoreBaseDriveMode();
            CurrentMotionAxis = AutonomousMotionAxis.None;
            resetRoutine = null;
            if (resetSucceeded)
                SetState(AutonomousExcavationState.Idle, "已复位，等待启动");
            else if (State == AutonomousExcavationState.Resetting)
            {
                FaultReason = "复位动作未能安全到达待机姿态";
                SetState(AutonomousExcavationState.Fault, FaultReason);
            }
        }
    }

    private bool ResolveReferences()
    {
        if (machine == null) machine = GetComponentInChildren<Zx200DigController>(true);
        if (cutter == null && machine != null) cutter = machine.GetComponentInChildren<BucketSoilCutter>(true);
        if (unloader == null && machine != null) unloader = machine.GetComponentInChildren<BucketSoilUnloader>(true);
        if (receiver == null && unloader != null && unloader.receivers != null)
            foreach (SoilReceivingZone candidate in unloader.receivers)
                if (candidate != null) { receiver = candidate; break; }
        if (receiver == null) receiver = GetComponentInChildren<SoilReceivingZone>(true);
        if (keyboard == null && machine != null) keyboard = machine.GetComponent<Zx200KeyboardController>();
        if (siteReadinessSource == null)
            siteReadinessSource = GetComponent("CesiumMoundSite") as MonoBehaviour;
        if (siteReadinessSource != null && siteReadyProperty == null)
            siteReadyProperty = siteReadinessSource.GetType().GetProperty("IsReady",
                BindingFlags.Instance | BindingFlags.Public);
        return machine != null && cutter != null && unloader != null && receiver != null && keyboard != null;
    }

    private void RestoreUnloaderEnabledState()
    {
        if (!dumpOwnsUnloaderEnabledState) return;
        if (unloader != null) unloader.enabled = unloaderEnabledBeforeDump;
        dumpOwnsUnloaderEnabledState = false;
    }

    private void RestoreJobUnloaderEnabledState()
    {
        if (!jobOwnsUnloaderEnabledState) return;
        if (unloader != null) unloader.enabled = unloaderEnabledBeforeJob;
        jobOwnsUnloaderEnabledState = false;
    }

    private void LockBaseForAutomaticMotion()
    {
        if (keyboard == null) return;
        if (!automaticModeOwnsBaseLock)
        {
            fixedBaseBeforeAutomaticMode = GetBaseDriveModeBeforeAutomaticMotion();
            automaticModeOwnsBaseLock = true;
        }
        ApplyBaseDriveMode(true);
    }

    private bool GetBaseDriveModeBeforeAutomaticMotion()
    {
        if (IsSitePreparationInProgress())
        {
            PropertyInfo originalFixedBaseProperty = siteReadinessSource.GetType().GetProperty(
                "OriginalFixedBase", BindingFlags.Instance | BindingFlags.Public);
            if (originalFixedBaseProperty != null &&
                originalFixedBaseProperty.GetValue(siteReadinessSource) is bool originalFixedBase)
                return originalFixedBase;
        }
        return keyboard.fixedBase;
    }

    private void RestoreBaseDriveMode()
    {
        if (!automaticModeOwnsBaseLock) return;
        // The site component owns the temporary lock until Cesium support preparation finishes.
        if (!IsSitePreparationInProgress())
            ApplyBaseDriveMode(fixedBaseBeforeAutomaticMode);
        automaticModeOwnsBaseLock = false;
    }

    private bool IsSitePreparationInProgress()
    {
        if (siteReadinessSource == null || siteReadyProperty == null) return false;
        try { return siteReadyProperty.GetValue(siteReadinessSource) is bool ready && !ready; }
        catch { return false; }
    }

    private void ApplyBaseDriveMode(bool fixedBase)
    {
        if (keyboard == null) return;
        keyboard.fixedBase = fixedBase;
        if (!keyboard.ResolveArticulationRoot()) return;

        ArticulationBody rootBody = keyboard.articulationRoot;
        rootBody.linearVelocity = Vector3.zero;
        rootBody.angularVelocity = Vector3.zero;
        rootBody.immovable = fixedBase;
        rootBody.useGravity = !fixedBase;
    }

    private string GetMotionAxisLabel()
    {
        return CurrentMotionAxis switch
        {
            AutonomousMotionAxis.None => "无",
            AutonomousMotionAxis.Bucket => "铲斗",
            AutonomousMotionAxis.Boom => "动臂",
            AutonomousMotionAxis.Arm => "斗杆",
            AutonomousMotionAxis.Swing => "上车回转",
            AutonomousMotionAxis.ArmAndBucket => "斗杆+铲斗协同运动",
            AutonomousMotionAxis.BoomArmBucket => "动臂+斗杆+铲斗协同运动",
            _ => "未知"
        };
    }

    private bool ValidateSettings(double requestedTargetM3)
    {
        return IsFinite(requestedTargetM3) && requestedTargetM3 > 0 &&
            IsFinite(minimumLoadVolumeM3) && minimumLoadVolumeM3 > 0 &&
            IsFinite(minimumTransferVolumeM3) && minimumTransferVolumeM3 > 0 &&
            IsFinite(siteReadyTimeoutSeconds) && siteReadyTimeoutSeconds > 0 &&
            IsFinite(stageTimeoutSeconds) && stageTimeoutSeconds > 0 &&
            IsFinite(cutTimeoutSeconds) && cutTimeoutSeconds > 0 &&
            IsFinite(dumpTimeoutSeconds) && dumpTimeoutSeconds > 0 &&
            IsFinite(soilFloorMargin) && soilFloorMargin >= 0 &&
            IsFinite(dumpClearanceMarginMeters) && dumpClearanceMarginMeters >= 0 &&
            IsFinite(targetBucketFillFraction) && targetBucketFillFraction >= .1f && targetBucketFillFraction <= .95f &&
            IsFinite(bucketCloseDeltaDegrees) && bucketCloseDeltaDegrees > 0 &&
            IsFinite(boomEntryStepDegrees) && boomEntryStepDegrees > 0 &&
            IsFinite(armScoopStepDegrees) && armScoopStepDegrees > 0 &&
            maximumEntrySteps > 0 && maximumScoopSteps > 0;
    }

    private bool IsSiteReady()
    {
        if (siteReadinessSource == null || siteReadyProperty == null) return true;
        try { return siteReadyProperty.GetValue(siteReadinessSource) is bool ready && ready; }
        catch (Exception error)
        {
            EnterFault("读取施工区域状态失败：" + error.Message);
            return false;
        }
    }

    private static bool RequiresTruckCollisionMonitoring(AutonomousExcavationState state)
    {
        return state == AutonomousExcavationState.SwingToTruck ||
            state == AutonomousExcavationState.PrepareDump ||
            state == AutonomousExcavationState.Dump ||
            state == AutonomousExcavationState.ReturnToDig ||
            state == AutonomousExcavationState.Resetting;
    }

    private bool CanStartFromCurrentBase()
    {
        if (siteReadinessSource == null || keyboard == null || !keyboard.ResolveArticulationRoot())
            return true;
        MethodInfo check = siteReadinessSource.GetType().GetMethod("CanStartAutonomousFrom",
            BindingFlags.Instance | BindingFlags.Public);
        if (check == null) return true;
        Transform root = keyboard.articulationRoot.transform;
        try { return check.Invoke(siteReadinessSource, new object[] { root.position, root.rotation }) is bool allowed && allowed; }
        catch (Exception error)
        {
            Debug.LogWarning("无法检查自动作业位：" + error.Message, this);
            return false;
        }
    }

    private bool FiniteMetrics()
    {
        return IsFinite(cutter.VolumeM3) && IsFinite(cutter.ExcavatedVolumeM3) &&
            IsFinite(receiver.VolumeM3) && IsFinite(receiver.AvailableM3);
    }

    private void UpdateDeliveredVolume()
    {
        DeliveredVolumeM3 = Math.Max(0, receiver.VolumeM3 - taskReceiverStartVolumeM3);
    }

    private float GetBucketResidualToleranceM3()
    {
        return Mathf.Max(minimumTransferVolumeM3, BucketResidualToleranceM3);
    }

    private float ComputeDumpSwingTarget(bool forOpenBucket = false)
    {
        Zx200JointPose actual = machine.ReadActualPose();
        ArticulationBody joint = machine.swingJoint;
        if (joint == null || receiver == null) return machine.swingToDumpPose.swing;

        Vector3 axis = joint.transform.TransformDirection(joint.anchorRotation * Vector3.right).normalized;
        Vector3 pivot = joint.transform.TransformPoint(joint.anchorPosition);
        Vector3 landingTip = forOpenBucket ? GetPredictedOpenTip() : cutter.TipWorldPosition;
        if (forOpenBucket)
        {
            float bestOpenScore = float.PositiveInfinity;
            float bestAngle = 0f;
            ArticulationDrive drive = joint.xDrive;
            for (int step = -70; step <= 70; step++)
            {
                float delta = step * .5f;
                float candidateAngle = actual.swing + delta;
                if (candidateAngle < drive.lowerLimit || candidateAngle > drive.upperLimit) continue;
                Vector3 tip = pivot + Quaternion.AngleAxis(delta, axis) * (landingTip - pivot);
                Vector3 local = receiver.transform.InverseTransformPoint(tip);
                float score = local.x * local.x + .1f * local.z * local.z + .0001f * delta * delta;
                if (Mathf.Abs(local.x) > receiver.size.x * .5f - cutter.innerMouthHalfWidth - .08f ||
                    Mathf.Abs(local.z) > receiver.size.y * .5f - .35f) score += 100f;
                if (score >= bestOpenScore) continue;
                bestOpenScore = score;
                bestAngle = candidateAngle;
            }
            return bestAngle;
        }
        Vector3 currentRadial = Vector3.ProjectOnPlane(landingTip - pivot, axis);
        if (currentRadial.sqrMagnitude < .0001f)
            return machine.swingToDumpPose.swing;

        // The boom-tip radius is fixed while slewing; select a safe point inside the bed on that radius.
        const int samplesPerAxis = 24;
        Vector3 targetRadial = Vector3.zero;
        float bestScore = float.PositiveInfinity;
        float halfWidth = receiver.size.x * .5f - DumpLandingInsetMeters;
        float halfLength = receiver.size.y * .5f - DumpLandingInsetMeters;
        for (int xIndex = 0; xIndex <= samplesPerAxis; xIndex++)
        {
            float localX = Mathf.Lerp(-halfWidth, halfWidth, xIndex / (float)samplesPerAxis);
            for (int zIndex = 0; zIndex <= samplesPerAxis; zIndex++)
            {
                float localZ = Mathf.Lerp(-halfLength, halfLength, zIndex / (float)samplesPerAxis);
                Vector3 candidateWorld = receiver.transform.TransformPoint(new Vector3(localX, 0f, localZ));
                Vector3 candidateRadial = Vector3.ProjectOnPlane(candidateWorld - pivot, axis);
                if (candidateRadial.sqrMagnitude < .0001f) continue;
                float radiusError = Mathf.Abs(candidateRadial.magnitude - currentRadial.magnitude);
                float angle = Mathf.Abs(Vector3.SignedAngle(currentRadial, candidateRadial, axis));
                float score = radiusError * 100f + angle * .001f +
                    (localX * localX + localZ * localZ) * .0001f;
                if (score >= bestScore) continue;
                bestScore = score;
                targetRadial = candidateRadial;
            }
        }
        if (targetRadial.sqrMagnitude < .0001f)
            return machine.swingToDumpPose.swing;

        float correction = Vector3.SignedAngle(currentRadial, targetRadial, axis);
        return actual.swing + correction;
    }

    private bool IsBucketOverReceiver()
    {
        return receiver != null && cutter != null &&
            receiver.TryGetLanding(cutter.TipWorldPosition, MaximumDumpDropMeters, out _);
    }

    private void HandleManualInput()
    {
        if (!IsRunning || keyboard == null) return;
        if (awaitingManualInputNeutral)
        {
            if (!keyboard.ManualInputActive) awaitingManualInputNeutral = false;
            return;
        }
        if (keyboard.ManualInputActive) RequestManualTakeover();
    }

    private void Finish(AutonomousExcavationState terminalState, string message)
    {
        IsRunning = false;
        RestoreUnloaderEnabledState();
        RestoreJobUnloaderEnabledState();
        RestoreCuttingSuppression();
        if (machine != null) machine.StopAutomaticMotion();
        if (keyboard != null) keyboard.SetAutomaticControl(false);
        RestoreBaseDriveMode();
        SetState(terminalState, message);
    }

    /// <summary>Restore soil cutting after a safe entry, manual takeover, fault, or reset transition.</summary>
    private void RestoreCuttingSuppression()
    {
        if (!cuttingSuppressionPending) return;
        cuttingSuppressionPending = false;
        if (cutter != null) cutter.SetCuttingSuppressed(cuttingWasSuppressedBeforeEntry);
    }

    private void EnterFault(string reason)
    {
        FaultReason = string.IsNullOrWhiteSpace(reason) ? "未知自主流程异常" : reason;
        Finish(AutonomousExcavationState.Fault, FaultReason);
    }

    private void SetState(AutonomousExcavationState state, string message)
    {
        if (stateHistory.Count == 0 || stateHistory[stateHistory.Count - 1] != state)
            stateHistory.Add(state);
        if (State != state || !string.Equals(Status, message, StringComparison.Ordinal))
            Debug.Log($"[ZX200 自主装车] {state}：{message}", this);
        State = state;
        Status = message ?? string.Empty;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
