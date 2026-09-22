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
        if (Input.GetKeyDown(KeyCode.A)) StartAutonomousJob();
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
            yield return MoveToPose(machine.approachPose, "下放姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;

            SetState(AutonomousExcavationState.FillBucket, "收斗切削并等待有效进斗量");
            yield return FillBucket();
            if (!IsRunning || !stageSucceeded) yield break;

            SetState(AutonomousExcavationState.Lift, "抬升满斗");
            yield return MoveToPose(machine.liftPose, "抬升姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;

            SetState(AutonomousExcavationState.SwingToTruck, "回转至车厢并准备卸料");
            yield return MoveToPose(machine.swingToDumpPose, "回转姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;
            yield return MoveToPose(machine.dumpPreparePose, "卸料准备姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;

            SetState(AutonomousExcavationState.Dump, "打开铲斗并等待土流落入车厢");
            cycleReceiverStartVolumeM3 = receiver.VolumeM3;
            yield return MoveToPose(machine.dumpPose, "卸料姿态超时");
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
            yield return MoveToPose(machine.returnSwingPose, "返回姿态超时");
            if (!IsRunning || !stageSucceeded) yield break;

            UpdateDeliveredVolume();
            if (HasReachedTarget(DeliveredVolumeM3, taskTargetVolumeM3))
            {
                yield return MoveToPose(machine.workReadyPose, "完成任务时待机姿态超时");
                if (!IsRunning || !stageSucceeded) yield break;
                Finish(AutonomousExcavationState.Completed, "达到目标量，单斗/多斗自主装车完成");
                yield break;
            }

            if (IsReceiverFull(receiver.AvailableM3, minimumTransferVolumeM3))
            {
                yield return MoveToPose(machine.workReadyPose, "车满时待机姿态超时");
                if (!IsRunning || !stageSucceeded) yield break;
                Finish(AutonomousExcavationState.TruckFull, "车厢已满，自动流程正常停止");
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
        if (!machine.IsPoseWithinTolerance(machine.workReadyPose, machine.jointToleranceDegrees))
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
        yield return MoveToPose(machine.curlPose, "收斗切削姿态超时");
        if (!IsRunning || !stageSucceeded) yield break;

        float deadline = Time.realtimeSinceStartup + cutTimeoutSeconds;
        while (IsRunning && Time.realtimeSinceStartup < deadline &&
               cutter.VolumeM3 - before < minimumLoadVolumeM3)
        {
            HandleManualInput();
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
        if (IsRunning) EnterFault("卸料超时，未确认土方落入车厢");
    }

    private IEnumerator MoveToPose(Zx200JointPose pose, string timeoutReason)
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
        if (machine.IsBusy || !machine.LastAutomaticMotionSucceeded)
        {
            EnterFault(timeoutReason);
            yield break;
        }
        stageSucceeded = true;
    }

    private IEnumerator ResetRoutine()
    {
        if (machine != null && machine.enabled && machine.StartAutomaticPose(machine.workReadyPose))
        {
            float deadline = Time.realtimeSinceStartup + stageTimeoutSeconds + machine.workReadyPose.duration;
            while (machine.IsBusy && Time.realtimeSinceStartup < deadline) yield return null;
        }
        if (machine != null && machine.IsBusy) machine.StopAutomaticMotion();
        if (keyboard != null) keyboard.SetAutomaticControl(false);
        resetRoutine = null;
        SetState(AutonomousExcavationState.Idle, "已复位，等待启动");
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
            IsFinite(soilFloorMargin) && soilFloorMargin >= 0;
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
