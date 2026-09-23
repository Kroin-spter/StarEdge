using System;
using System.Collections;
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

/// <summary>
/// Coordinates one or more physical ZX200 dig-and-load cycles without minting or teleporting soil.
/// </summary>
[DisallowMultipleComponent]
public sealed class AutonomousExcavationController : MonoBehaviour
{
    [Header("References")]
    public Zx200DigController machine;
    public BucketSoilCutter cutter;
    public BucketSoilUnloader unloader;
    public SoilReceivingZone receiver;
    public MonoBehaviour siteReadinessSource;

    [Header("Job Settings")]
    [Min(.001f)] public float targetVolumeM3 = 1f;
    [Min(.0001f)] public float minimumLoadVolumeM3 = .005f;
    [Min(.0001f)] public float minimumTransferVolumeM3 = .005f;
    [Min(1f)] public float siteReadyTimeoutSeconds = 40f;
    [Min(1f)] public float stageTimeoutSeconds = 12f;
    [Min(.1f)] public float cutTimeoutSeconds = 8f;
    [Min(.1f)] public float dumpTimeoutSeconds = 12f;
    [Min(.001f)] public float soilFloorMargin = .04f;
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

    private Zx200KeyboardController keyboard;
    private Coroutine jobRoutine;
    private Coroutine resetRoutine;
    private PropertyInfo siteReadyProperty;
    private double taskTargetVolumeM3;
    private double taskReceiverStartVolumeM3;
    private double cycleReceiverStartVolumeM3;
    private float dumpSwingTarget;
    private bool stageSucceeded;

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
            if (!IsRunning || !stageSucceeded) yield break;

            SetState(AutonomousExcavationState.FillBucket, "收斗切削并等待有效进斗量");
            yield return FillBucket();
            if (!IsRunning || !stageSucceeded) yield break;

            SetState(AutonomousExcavationState.Lift, "抬升满斗");
            yield return MoveToPose(machine.liftPose, "抬升姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;
            yield return RaiseForTruckClearance();
            if (!IsRunning || !stageSucceeded) yield break;

            SetState(AutonomousExcavationState.SwingToTruck, "回转至车厢并准备卸料");
            yield return AlignDumpSwing();
            if (!IsRunning || !stageSucceeded) yield break;
            Zx200JointPose actualAtDump = machine.ReadActualPose();
            dumpSwingTarget = actualAtDump.swing;
            Zx200JointPose dumpPrepare = machine.dumpPreparePose;
            dumpPrepare.swing = dumpSwingTarget;
            dumpPrepare.boom = actualAtDump.boom;
            dumpPrepare.arm = actualAtDump.arm;
            yield return MoveToPose(dumpPrepare, "卸料准备姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;
            yield return AlignCurrentSwing();
            if (!IsRunning || !stageSucceeded) yield break;

            SetState(AutonomousExcavationState.Dump, "打开铲斗并等待土流落入车厢");
            cycleReceiverStartVolumeM3 = receiver.VolumeM3;
            Zx200JointPose dump = machine.dumpPose;
            Zx200JointPose actualBeforeDump = machine.ReadActualPose();
            dump.swing = actualBeforeDump.swing;
            dump.boom = actualBeforeDump.boom;
            dump.arm = actualBeforeDump.arm;
            yield return MoveToPose(dump, "卸料姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;
            yield return WaitForDump();
            if (!IsRunning || !stageSucceeded) yield break;

            SetState(AutonomousExcavationState.ConfirmDump, "核对车厢实际接收量");
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
        if (cutter.VolumeM3 > minimumLoadVolumeM3)
        {
            EnterFault("开始新一斗前铲斗仍有未处理土量");
            yield break;
        }
        if (receiver == null || IsReceiverFull(receiver.AvailableM3, minimumTransferVolumeM3))
        {
            Finish(AutonomousExcavationState.TruckFull, "车厢没有足够的剩余容量");
            yield break;
        }
        if (cutter.soil == null || !cutter.soil.isActiveAndEnabled)
        {
            EnterFault("可挖土区未启用");
            yield break;
        }
        if (CompletedCycles == 0 && !machine.IsPoseWithinTolerance(machine.workReadyPose, machine.jointToleranceDegrees))
        {
            yield return MoveToPose(machine.workReadyPose, "进入挖点时待机姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;
        }
        if (!FiniteMetrics())
        {
            EnterFault("挖点土量数据不是有限数值");
            yield break;
        }
        stageSucceeded = true;
    }

    private IEnumerator FillBucket()
    {
        stageSucceeded = false;
        double before = cutter.VolumeM3;
        Zx200JointPose actual = machine.ReadActualPose();
        Zx200JointPose target = new Zx200JointPose(
            actual.swing, actual.boom, actual.arm + armScoopStepDegrees * maximumScoopSteps,
            actual.bucket, Mathf.Max(1f, cutTimeoutSeconds * .75f));
        if (!machine.StartAutomaticPose(target))
        {
            EnterFault("无法启动收斗切削姿态");
            yield break;
        }
        float deadline = Time.realtimeSinceStartup + cutTimeoutSeconds;
        while (IsRunning && machine.IsBusy && Time.realtimeSinceStartup < deadline)
        {
            HandleManualInput();
            if (cutter.VolumeM3 - before >= minimumLoadVolumeM3)
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
        Finish(atFloor ? AutonomousExcavationState.SoilExhausted : AutonomousExcavationState.EmptyCut,
            atFloor ? "刃口已到土层底部，当前挖点挖尽" : "切削超时，未形成有效进斗量");
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
            if (tip.y <= surface + .025f)
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
        Finish(AutonomousExcavationState.EmptyCut,
            $"下放达到时间上限但刃口未进入土层；姿态=({failedPose.swing:F1},{failedPose.boom:F1}," +
            $"{failedPose.arm:F1},{failedPose.bucket:F1})，斗尖={cutter.TipWorldPosition}，土面={failedSurface:F2}");
    }

    private IEnumerator WaitForDump()
    {
        stageSucceeded = false;
        float deadline = Time.realtimeSinceStartup + dumpTimeoutSeconds;
        while (IsRunning && Time.realtimeSinceStartup < deadline)
        {
            HandleManualInput();
            UpdateDeliveredVolume();
            bool bucketEmpty = cutter.VolumeM3 <= minimumTransferVolumeM3;
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
            Zx200JointPose swingToDump = machine.swingToDumpPose;
            swingToDump.swing = dumpSwingTarget;
            if (!machine.StartAutomaticPose(swingToDump))
            {
                EnterFault("无法启动回转至车厢姿态");
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + stageTimeoutSeconds + swingToDump.duration;
            while (IsRunning && machine.IsBusy && Time.realtimeSinceStartup < deadline)
            {
                HandleManualInput();
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
            EnterFault($"回转未能将斗尖送至车厢接收区；实际回转={actual.swing:F1}°，" +
                $"斗尖={cutter.TipWorldPosition}，车厢={receiver.transform.position}");
        }
    }

    private IEnumerator AlignCurrentSwing()
    {
        stageSucceeded = false;
        for (int attempt = 0; IsRunning && attempt < 4; attempt++)
        {
            dumpSwingTarget = ComputeDumpSwingTarget();
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
            EnterFault($"卸料姿态变化后无法重新对准车厢接收区；实际回转={actual.swing:F1}°，" +
                $"斗尖={cutter.TipWorldPosition}，车厢增量={receiver.VolumeM3 - cycleReceiverStartVolumeM3:F4}");
        }
    }

    private IEnumerator RaiseForTruckClearance()
    {
        stageSucceeded = false;
        float clearanceY = GetTruckClearanceY() + .12f;
        if (cutter.TipWorldPosition.y >= clearanceY)
        {
            stageSucceeded = true;
            yield break;
        }

        Zx200JointPose actual = machine.ReadActualPose();
        Zx200JointPose target = new Zx200JointPose(
            actual.swing, actual.boom - boomEntryStepDegrees * maximumEntrySteps,
            actual.arm, actual.bucket, 1f);
        yield return MoveToPose(target, "越过车厢壁的抬升姿态超时");
        if (!IsRunning || !stageSucceeded) yield break;
        if (cutter.TipWorldPosition.y < clearanceY)
        {
            EnterFault($"斗尖未越过车厢壁；斗尖高度={cutter.TipWorldPosition.y:F2}，" +
                $"所需高度={clearanceY:F2}");
            yield break;
        }
        stageSucceeded = true;
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
        bool resistanceWasEnabled = resistance != null && resistance.enabled;
        if (cutterWasEnabled) cutter.enabled = false;
        if (resistanceWasEnabled) resistance.enabled = false;
        try
        {
            yield return MoveToPose(clearTruck, "离开车厢回转姿态超时", 3f);
        }
        finally
        {
            if (resistanceWasEnabled && resistance != null) resistance.enabled = true;
            if (cutterWasEnabled && cutter != null) cutter.enabled = true;
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

    private IEnumerator MoveToPose(Zx200JointPose pose, string timeoutReason, float completionToleranceDegrees = -1f)
    {
        stageSucceeded = false;
        if (!machine.StartAutomaticPose(pose))
        {
            EnterFault("无法启动自动姿态：" + timeoutReason);
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + stageTimeoutSeconds + pose.duration;
        while (IsRunning && machine.IsBusy && Time.realtimeSinceStartup < deadline)
        {
            HandleManualInput();
            yield return null;
        }
        if (!IsRunning) yield break;
        bool reachedWithRequestedTolerance = completionToleranceDegrees >= 0f &&
            machine.IsPoseWithinTolerance(pose, completionToleranceDegrees);
        if (machine.IsBusy || (!machine.LastAutomaticMotionSucceeded && !reachedWithRequestedTolerance))
        {
            string detail = timeoutReason;
            if (timeoutReason.Contains("回转"))
            {
                Zx200JointPose actual = machine.ReadActualPose();
                detail += $"；实际回转={actual.swing:F1}°，斗尖={cutter.TipWorldPosition}，" +
                    $"车厢={receiver.transform.position}";
            }
            EnterFault(detail);
            yield break;
        }
        stageSucceeded = true;
    }

    private IEnumerator ResetRoutine()
    {
        BucketSoilResistance resistance = machine != null && machine.bucketJoint != null
            ? machine.bucketJoint.GetComponent<BucketSoilResistance>() : null;
        bool cutterWasEnabled = cutter != null && cutter.enabled;
        bool resistanceWasEnabled = resistance != null && resistance.enabled;
        if (cutterWasEnabled) cutter.enabled = false;
        if (resistanceWasEnabled) resistance.enabled = false;
        try
        {
            if (machine != null && machine.enabled && machine.StartAutomaticPose(machine.workReadyPose))
            {
                float deadline = Time.realtimeSinceStartup + stageTimeoutSeconds + machine.workReadyPose.duration;
                while (machine.IsBusy && Time.realtimeSinceStartup < deadline) yield return null;
            }
        }
        finally
        {
            if (machine != null && machine.IsBusy) machine.StopAutomaticMotion();
            if (resistanceWasEnabled && resistance != null) resistance.enabled = true;
            if (cutterWasEnabled && cutter != null) cutter.enabled = true;
            if (keyboard != null) keyboard.SetAutomaticControl(false);
            resetRoutine = null;
            SetState(AutonomousExcavationState.Idle, "已复位，等待启动");
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

    private float ComputeDumpSwingTarget()
    {
        Zx200JointPose actual = machine.ReadActualPose();
        ArticulationBody joint = machine.swingJoint;
        if (joint == null || receiver == null) return machine.swingToDumpPose.swing;

        Vector3 axis = joint.transform.TransformDirection(joint.anchorRotation * Vector3.right).normalized;
        Vector3 pivot = joint.transform.TransformPoint(joint.anchorPosition);
        Vector3 currentRadial = Vector3.ProjectOnPlane(cutter.TipWorldPosition - pivot, axis);
        Vector3 targetRadial = Vector3.ProjectOnPlane(receiver.transform.position - pivot, axis);
        if (currentRadial.sqrMagnitude < .0001f || targetRadial.sqrMagnitude < .0001f)
            return machine.swingToDumpPose.swing;

        float correction = Vector3.SignedAngle(currentRadial, targetRadial, axis);
        return actual.swing + correction;
    }

    private bool IsBucketOverReceiver()
    {
        return receiver != null && cutter != null &&
            receiver.TryGetLanding(cutter.TipWorldPosition, 5f, out _);
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
