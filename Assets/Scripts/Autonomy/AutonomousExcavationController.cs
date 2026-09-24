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
    Resetting
}

/// <summary>Identifies the single articulation axis currently commanded by the autonomous sequencer.</summary>
public enum AutonomousMotionAxis
{
    None,
    Bucket,
    Boom,
    Arm,
    Swing,
    ArmAndBucket
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
    private const float DumpLandingInsetMeters = .7f;
    private const float TruckSwingClearanceReserveMeters = .05f;
    private const float TruckSwingStartReserveMeters = .7f;
    private const float TruckCollisionSlopToleranceMeters = .01f;
    private const float TruckClearanceRecoveryToleranceMeters = .03f;
    private const float BucketResidualToleranceM3 = .01f;

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
        if (IsRunning || State == AutonomousExcavationState.Resetting)
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

        taskTargetVolumeM3 = requestedTargetM3;
        taskReceiverStartVolumeM3 = receiver.VolumeM3;
        cycleReceiverStartVolumeM3 = receiver.VolumeM3;
        DeliveredVolumeM3 = 0;
        CompletedCycles = 0;
        nextDigPointIndex = 0;
        stateHistory.Clear();
        FaultReason = string.Empty;
        IsRunning = true;
        keyboard.SetAutomaticControl(true);
        SetState(AutonomousExcavationState.WaitingForSite, "等待施工区域就绪");
        jobRoutine = StartCoroutine(RunJob());
        return jobRoutine != null;
    }

    /// <summary>
    /// 请求人工接管；不回滚已挖土、斗内土或车厢实际接收量。
    /// </summary>
    public void RequestManualTakeover()
    {
        if (!IsRunning) return;
        IsRunning = false;
        if (machine != null) machine.StopAutomaticMotion();
        if (keyboard != null) keyboard.SetAutomaticControl(false);
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
        CurrentMotionAxis = AutonomousMotionAxis.None;
        if (resetRoutine != null) StopCoroutine(resetRoutine);
        IsRunning = false;
        if (machine != null) machine.StopAutomaticMotion();
        if (keyboard != null) keyboard.SetAutomaticControl(true);
        if (cutter != null) cutter.ResetExcavation();
        DeliveredVolumeM3 = 0;
        CompletedCycles = 0;
        FaultReason = string.Empty;
        SetState(AutonomousExcavationState.Resetting, "正在复位土区与设备");
        if (!Application.isPlaying)
        {
            if (keyboard != null) keyboard.SetAutomaticControl(false);
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
        if (IsRunning && keyboard != null && keyboard.ManualInputActive)
            RequestManualTakeover();
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

    private IEnumerator RunJob()
    {
        yield return WaitForSiteReady();
        if (!IsRunning || !stageSucceeded) yield break;

        while (IsRunning)
        {
            SetState(AutonomousExcavationState.AcquirePoint, "检查当前挖点和车厢容量");
            yield return AcquirePoint();
            if (!IsRunning) yield break;
            if (!stageSucceeded) yield break;

            SetState(AutonomousExcavationState.LowerAndEnter, "动臂下放并进入土层");
            yield return PrepareForCut();
            if (!IsRunning) yield break;
            if (State == AutonomousExcavationState.EmptyCut)
            {
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
                yield return RecoverFromEmptyCut();
                if (!IsRunning || !stageSucceeded) yield break;
                continue;
            }

            SetState(AutonomousExcavationState.Lift, "抬升满斗");
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

            SetState(AutonomousExcavationState.Dump, "打开铲斗并等待土流落入车厢");
            cycleReceiverStartVolumeM3 = receiver.VolumeM3;
            Zx200JointPose dump = machine.dumpPose;
            Zx200JointPose actualBeforeDump = machine.ReadActualPose();
            dump.swing = actualBeforeDump.swing;
            dump.boom = actualBeforeDump.boom;
            dump.arm = actualBeforeDump.arm;
            unloaderEnabledBeforeDump = unloader.enabled;
            dumpOwnsUnloaderEnabledState = true;
            unloader.enabled = false;
            try
            {
                yield return MoveToPose(dump, "卸料姿态超时");
                if (!IsRunning || !stageSucceeded) yield break;
                bool safeDumpPose = false;
                ArticulationDrive boomDrive = machine.boomJoint.xDrive;
                for (int attempt = 0; IsRunning && attempt < 9; attempt++)
                {
                    yield return AlignCurrentSwing(false);
                    if (!IsRunning) yield break;
                    if (stageSucceeded && IsDumpApproachSafe())
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
                unloader.enabled = true;
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
        if (!FiniteMetrics())
        {
            EnterFault("挖点土量数据不是有限数值");
            yield break;
        }
        nextDigPointIndex++;
        stageSucceeded = true;
    }

    private IEnumerator RecoverFromEmptyCut()
    {
        // Lift away from the terrain before changing swing points; do not pivot the bucket while it is buried.
        yield return MoveToPose(machine.liftPose, "空挖点抬升脱离土层超时");
    }

    private IEnumerator FillBucket()
    {
        stageSucceeded = false;
        double before = cutter.VolumeM3;
        Zx200JointPose actual = machine.ReadActualPose();
        double targetLoad = Math.Max(before, cutter.EffectiveCapacityM3 * targetBucketFillFraction);
        // Retracting the arm and curling the bucket together is the deliberate physical scoop stroke.
        Zx200JointPose target = new Zx200JointPose(
            actual.swing, actual.boom, machine.curlPose.arm,
            machine.curlPose.bucket, Mathf.Max(machine.curlPose.duration, cutTimeoutSeconds * .75f));
        CurrentMotionAxis = AutonomousMotionAxis.ArmAndBucket;
        try
        {
            if (!machine.StartAutomaticPose(target))
            {
                EnterFault("无法启动收斗切削姿态");
                yield break;
            }
            float deadline = Time.realtimeSinceStartup + cutTimeoutSeconds;
            while (IsRunning && machine.IsBusy && Time.realtimeSinceStartup < deadline)
            {
                HandleManualInput();
                if (cutter.VolumeM3 >= targetLoad)
                {
                    machine.StopAutomaticMotion();
                    stageSucceeded = true;
                    yield break;
                }
                yield return new WaitForFixedUpdate();
            }
            if (!IsRunning) yield break;
            if (cutter.VolumeM3 - before >= minimumLoadVolumeM3)
            {
                stageSucceeded = true;
                yield break;
            }

            bool atFloor = cutter.TipWorldPosition.y <=
                cutter.soil.GetFloorWorldY(cutter.TipWorldPosition) + soilFloorMargin;
            SetState(AutonomousExcavationState.EmptyCut,
                atFloor ? "当前挖点已到土层底部，继续检查下一个可达挖点" : "当前挖点切削无效，继续检查下一个可达挖点");
            stageSucceeded = true;
        }
        finally
        {
            CurrentMotionAxis = AutonomousMotionAxis.None;
        }
    }

    private IEnumerator PrepareForCut()
    {
        stageSucceeded = false;
        Zx200JointPose actual = machine.ReadActualPose();
        yield return MoveToPose(new Zx200JointPose(
            actual.swing, actual.boom, actual.arm,
            actual.bucket + bucketCloseDeltaDegrees, .8f), "闭斗姿态超时", 3f);
        if (!IsRunning || !stageSucceeded) yield break;

        actual = machine.ReadActualPose();
        Zx200JointPose target = new Zx200JointPose(
            actual.swing, machine.workReadyPose.boom + boomEntryStepDegrees * maximumEntrySteps,
            actual.arm, actual.bucket, Mathf.Max(1f, stageTimeoutSeconds * .5f));
        if (!machine.StartAutomaticPose(target))
        {
            EnterFault("无法启动进入土层姿态");
            yield break;
        }
        float deadline = Time.realtimeSinceStartup + stageTimeoutSeconds;
        while (IsRunning && machine.IsBusy && Time.realtimeSinceStartup < deadline)
        {
            HandleManualInput();
            Vector3 tip = cutter.TipWorldPosition;
            float surface = cutter.soil.Terrain.SampleHeight(tip) + cutter.soil.transform.position.y;
            if (tip.y <= surface - cutEntryDepthMeters)
            {
                machine.StopAutomaticMotion();
                stageSucceeded = true;
                yield break;
            }
            yield return new WaitForFixedUpdate();
        }

        if (!IsRunning) yield break;
        machine.StopAutomaticMotion();
        Zx200JointPose failedPose = machine.ReadActualPose();
        float failedSurface = cutter.soil.Terrain.SampleHeight(cutter.TipWorldPosition) + cutter.soil.transform.position.y;
        SetState(AutonomousExcavationState.EmptyCut,
            $"当前挖点下放无效，继续尝试下一挖点；姿态=({failedPose.swing:F1},{failedPose.boom:F1}," +
            $"{failedPose.arm:F1},{failedPose.bucket:F1})，斗尖={cutter.TipWorldPosition}，土面={failedSurface:F2}");
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
            Zx200JointPose swingToDump = new Zx200JointPose(
                dumpSwingTarget, actual.boom, actual.arm, actual.bucket, .9f);
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
            dumpSwingTarget = ComputeDumpSwingTarget();
            if (State == AutonomousExcavationState.SwingToTruck &&
                !HasPredictedTruckSwingClearance(dumpSwingTarget))
            {
                yield return RaiseBoomToTruckClearance(dumpSwingTarget);
                if (!IsRunning || !stageSucceeded) yield break;
                dumpSwingTarget = ComputeDumpSwingTarget();
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
                dumpSwingTarget, actual.boom, actual.arm, actual.bucket, .7f);
            if (!machine.StartAutomaticPose(target))
            {
                EnterFault("无法微调卸料回转姿态");
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + stageTimeoutSeconds + target.duration;
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
                    EnterFault("卸料对准动作与卡车实体发生穿透，已停止：" + collision);
                    yield break;
                }
                if (IsBucketOverReceiver())
                {
                    machine.StopAutomaticMotion();
                    stageSucceeded = true;
                    yield break;
                }
                yield return null;
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
            stageSucceeded = false;
            if (faultOnFailure)
                EnterFault($"卸料姿态变化后无法重新对准车厢接收区；实际姿态=" +
                    $"({actual.swing:F1},{actual.boom:F1},{actual.arm:F1},{actual.bucket:F1})，" +
                    $"斗尖={cutter.TipWorldPosition}，车厢中心={receiver.transform.position}，" +
                    $"接收区判定={IsBucketOverReceiver()}，车厢尺寸={receiver.size}，" +
                    $"车厢增量={receiver.VolumeM3 - cycleReceiverStartVolumeM3:F4}");
        }
    }

    private IEnumerator RaiseToDumpClearance()
    {
        stageSucceeded = false;
        Zx200JointPose actual = machine.ReadActualPose();
        ArticulationDrive boomDrive = machine.boomJoint.xDrive;
        ArticulationDrive armDrive = machine.armJoint.xDrive;
        ArticulationDrive bucketDrive = machine.bucketJoint.xDrive;
        // Use the configured truck-side working reach while still in the dig sector, before slewing over the bed.
        Zx200JointPose truckReady = machine.dumpPose;
        // Keep the measured curl reached by FillBucket; forcing liftPose.bucket adds an unnecessary final curl
        // while the bucket is still close to the bench and can wedge the physical bucket against the ground.
        float retainedBucketAngle = Zx200DigController.ClampTarget(
            actual.bucket, bucketDrive.lowerLimit, bucketDrive.upperLimit);
        Zx200JointPose target = CreatePreSwingApproachPose(
            actual,
            truckReady,
            retainedBucketAngle);
        target.arm = Zx200DigController.ClampTarget(truckReady.arm,
            armDrive.lowerLimit, armDrive.upperLimit);
        target.duration = Mathf.Max(1f, machine.swingToDumpPose.duration);
        BucketSoilResistance resistance = machine.bucketJoint.GetComponent<BucketSoilResistance>();
        bool cutterWasEnabled = cutter != null && cutter.enabled;
        bool wasCuttingSuppressed = cutter != null && cutter.IsCuttingSuppressed;
        bool resistanceWasEnabled = resistance != null && resistance.enabled;
        if (cutterWasEnabled) cutter.SetCuttingSuppressed(true);
        if (resistanceWasEnabled) resistance.enabled = false;
        try
        {
            Zx200JointPose waypoint = machine.ReadActualPose();
            waypoint.bucket = target.bucket;
            waypoint.duration = Mathf.Max(.8f, machine.swingToDumpPose.duration * .5f);
            yield return MoveToPose(waypoint, "抬升前闭斗姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;

            waypoint = machine.ReadActualPose();
            waypoint.boom = target.boom;
            waypoint.duration = Mathf.Max(1f, machine.swingToDumpPose.duration);
            yield return MoveToPose(waypoint, "逐步抬升动臂超时");
            if (!IsRunning || !stageSucceeded) yield break;

            // Raise with the measured arm pose first so extending the arm cannot sweep through the ground pile.
            yield return RaiseBoomUntilTruckClearance("伸展斗杆前抬升至车厢净空超时");
            if (!IsRunning || !stageSucceeded) yield break;

            waypoint = machine.ReadActualPose();
            waypoint.arm = target.arm;
            waypoint.duration = Mathf.Max(.8f, machine.swingToDumpPose.duration * .5f);
            yield return MoveToPose(waypoint, "高位转运斗杆调整超时");
            if (!IsRunning || !stageSucceeded) yield break;

            // The arm change can lower the tip; revalidate and raise again before slewing toward the bed.
            yield return RaiseBoomUntilTruckClearance("斗杆伸展后恢复车厢净空超时");
        }
        finally
        {
            if (resistanceWasEnabled && resistance != null) resistance.enabled = true;
            if (cutterWasEnabled && cutter != null) cutter.SetCuttingSuppressed(wasCuttingSuppressed);
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
            float nextBoom = Zx200DigController.ClampTarget(
                actual.boom + correctionSign * 5f,
                boomDrive.lowerLimit,
                boomDrive.upperLimit);
            if (Mathf.Approximately(correctionSign, 0f) || Mathf.Approximately(nextBoom, actual.boom))
                break;

            Zx200JointPose waypoint = actual;
            waypoint.boom = nextBoom;
            waypoint.duration = Mathf.Max(1f, machine.swingToDumpPose.duration);
            yield return MoveToPose(waypoint, timeoutReason, 2f);
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
            // Close the bucket and lift before moving the arm or slewing; never interpolate a multi-joint shortcut.
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
            if (State == AutonomousExcavationState.SwingToTruck ||
                State == AutonomousExcavationState.Dump ||
                State == AutonomousExcavationState.ReturnToDig ||
                State == AutonomousExcavationState.Resetting)
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

                yield return MoveToPose(machine.workReadyPose, "复位待机姿态超时", -1f, true, true);
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

    private string GetMotionAxisLabel()
    {
        return CurrentMotionAxis switch
        {
            AutonomousMotionAxis.None => "无",
            AutonomousMotionAxis.Bucket => "铲斗",
            AutonomousMotionAxis.Boom => "动臂",
            AutonomousMotionAxis.Arm => "斗杆",
            AutonomousMotionAxis.Swing => "上车回转",
            AutonomousMotionAxis.ArmAndBucket => "斗杆+铲斗协同切削",
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

    private float ComputeDumpSwingTarget()
    {
        Zx200JointPose actual = machine.ReadActualPose();
        ArticulationBody joint = machine.swingJoint;
        if (joint == null || receiver == null) return machine.swingToDumpPose.swing;

        Vector3 axis = joint.transform.TransformDirection(joint.anchorRotation * Vector3.right).normalized;
        Vector3 pivot = joint.transform.TransformPoint(joint.anchorPosition);
        Vector3 currentRadial = Vector3.ProjectOnPlane(cutter.TipWorldPosition - pivot, axis);
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
        if (IsRunning && keyboard != null && keyboard.ManualInputActive)
            RequestManualTakeover();
    }

    private void Finish(AutonomousExcavationState terminalState, string message)
    {
        IsRunning = false;
        if (machine != null) machine.StopAutomaticMotion();
        if (keyboard != null) keyboard.SetAutomaticControl(false);
        SetState(terminalState, message);
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
