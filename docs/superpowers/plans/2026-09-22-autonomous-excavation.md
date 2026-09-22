# Autonomous Excavation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Cesium 主场景中完成无需逐斗人工输入的一斗自主挖掘装车，并以实际地形、铲斗和车厢土量驱动连续多斗循环。

**Architecture:** 保留现有 `BucketSoilCutter`、`BucketSoilUnloader` 和 `SoilReceivingZone` 作为唯一土量账本；给 `Zx200DigController` 增加可等待、可取消的单姿态自动执行接口；新增 `AutonomousExcavationController` 作为可中止的流程状态机，并挂到主场景施工区域。第一版只验证当前固定可达挖点和 `ParkedDumpTruck` 接收区，不实现底盘导航。

**Tech Stack:** Unity 6 `6000.5.10f1`、C#、ArticulationBody、Unity Test Framework EditMode/PlayMode、Cesium for Unity 本地 tileset。

## Global Constraints

- 第一版只使用当前主场景的一个可达挖点和 `ParkedDumpTruck` 接收区。
- 切削量必须来自 `BucketSoilCutter`，卸料必须经过 `BucketSoilUnloader`/`SoilFlightTransport`，状态机不直接增加土量。
- 自动流程必须支持目标量、车满、挖尽/空挖、姿态超时、卸料超时、人工接管和复位。
- 运行中不修改原始 Cesium tileset 或地形资产；只使用运行时地形副本。
- 不提交 `Library/`、`Temp/`、`Logs/`、外部 tileset、本机绝对路径和 Unity 生成工程文件。
- 每个独立阶段完成后运行对应测试并提交一次 Git commit；不执行远程 push。

## 文件结构

- Create: `Assets/Scripts/Autonomy/AutonomousExcavationController.cs` — 自主任务状态、阶段协程、停止条件、人工接管和复位。
- Create: `Assets/Scripts/Autonomy/AutonomousExcavationController.cs.meta` — Unity 脚本 GUID，供主场景序列化引用。
- Modify: `Assets/Scripts/Zx200DigController.cs` — 单姿态自动执行、完成结果和安全取消接口。
- Modify: `Assets/Scripts/Zx200KeyboardController.cs` — 自动控制锁定和人工输入探测。
- Modify: `Assets/Scenes/sence_test.unity` — 将自主控制器挂到 `ZX200 construction site` 并绑定已有组件。
- Create: `Assets/Tests/EditMode/AutonomousExcavationControllerTests.cs` — 不依赖 Cesium 的状态和边界测试。
- Create: `Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs` — 主场景一斗真实切削、卸料、守恒和复位测试。
- Modify: `README.md` — 记录启动键/API、当前验收范围和已知限制。

### Task 1: Add cancellable automatic pose execution

**Files:**
- Modify: `Assets/Scripts/Zx200DigController.cs`
- Modify: `Assets/Tests/EditMode/Zx200DigControllerTests.cs`

**Interfaces:**
- Produces `bool StartAutomaticPose(Zx200JointPose target)`; it starts one pose only when the controller is enabled and idle.
- Produces `void StopAutomaticMotion()`; it cancels the active pose at the next safe coroutine check and leaves current physics state intact.
- Produces `bool LastAutomaticMotionSucceeded { get; private set; }`; it is true only when the actual joints settle within tolerance.
- Existing `PlayDigCycle()`, `AdjustManualPose()` and Space behavior remain source-compatible.

- [ ] **Step 1: Add tests for the new public contract.**

  Extend the existing controller test fixture with a test that creates the same four named articulation joints, calls `PrepareForPhysics()`, verifies `StartAutomaticPose()` accepts one request and rejects a second while busy, and verifies `StopAutomaticMotion()` clears the busy request. Add a test that invalid/non-finite target poses are rejected through the existing validation path.

- [ ] **Step 2: Run the focused EditMode tests and record the expected failure.**

  Run:

  ```bash
  /home/sk/unity-editor/6000.5.10f1/Editor/Unity -batchmode -nographics -projectPath /home/sk/Unity/HMI_Unity6 -runTests -testPlatform editmode -testFilter ExcavatorControls.Tests.Zx200DigControllerTests -testResults /tmp/hmi-zx200-pose-before.xml -logFile /tmp/hmi-zx200-pose-before.log
  ```

  Expected: the new API tests fail to compile or fail because the API does not yet exist; existing controller tests remain unchanged.

- [ ] **Step 3: Implement the smallest cancellable pose API.**

  Add a private `automaticPoseRoutine`, a private cancellation flag, and the public members below. The routine sets `IsBusy`, calls the existing `AnimateTo(target)`, checks `IsPoseWithinTolerance`, writes `LastAutomaticMotionSucceeded`, and clears `IsBusy`. Add cancellation checks inside `AnimateTo`'s interpolation and settle loops so cancellation cannot leave a later target write running.

  ```csharp
  public bool StartAutomaticPose(Zx200JointPose target);
  public void StopAutomaticMotion();
  public bool LastAutomaticMotionSucceeded { get; private set; }
  ```

  Keep `PlayDigCycle()` on its current path and make it clear the result flag before starting, so the legacy Space cycle does not change behavior.

- [ ] **Step 4: Run the focused EditMode tests again.**

  Re-run the command from Step 2. Expected: the new contract tests and all existing `Zx200DigControllerTests` pass. If Unity rewrites the imported ZX200 prefab during testing, restore only that generated prefab change before reviewing the diff.

- [ ] **Step 5: Commit the pose API.**

  ```bash
  git add Assets/Scripts/Zx200DigController.cs Assets/Tests/EditMode/Zx200DigControllerTests.cs
  git commit -m "feat: add cancellable automatic pose control"
  ```

### Task 2: Prevent conflicting manual input

**Files:**
- Modify: `Assets/Scripts/Zx200KeyboardController.cs`
- Modify: `Assets/Tests/EditMode/Zx200KeyboardControllerTests.cs`

**Interfaces:**
- Produces `bool ManualInputActive { get; private set; }`, refreshed every `Update` from keyboard axes/keys and non-neutral AZ topic values.
- Produces `void SetAutomaticControl(bool active)`, which stops track input while automatic control owns the machine.
- The autonomy controller consumes `ManualInputActive` to enter `ManualTakeover`; keyboard joint input remains disabled while `Zx200DigController.IsBusy` is true.

- [ ] **Step 1: Add input ownership tests.**

  Add EditMode tests for `ResolveAxisInput()` and the new ownership contract: automatic ownership is enabled/disabled deterministically, neutral topic data is not considered takeover, and a non-neutral input can be reported. Keep tests independent of `Input.GetKey` by putting the normalization/ownership decision in a public static helper with exact signature:

  ```csharp
  public static bool HasNonNeutralInput(float vertical, float horizontal,
      float swing, float boom, float arm, float bucket, float deadzone);
  ```

- [ ] **Step 2: Run the focused keyboard tests before implementation.**

  Run the existing keyboard EditMode filter and expect the new helper test to fail because the helper is not present.

- [ ] **Step 3: Implement ownership without changing existing mappings.**

  Compute `ManualInputActive` before the automatic-control early return. When `AutomaticControlActive` is true, call `trackDrive.Stop()` and skip movement/joint writes. `SetAutomaticControl(false)` restores normal input on the next frame. Do not alter the current AZ axis sign conventions.

- [ ] **Step 4: Run all keyboard EditMode tests and commit.**

  ```bash
  /home/sk/unity-editor/6000.5.10f1/Editor/Unity -batchmode -nographics -projectPath /home/sk/Unity/HMI_Unity6 -runTests -testPlatform editmode -testFilter ExcavatorControls.Tests.Zx200KeyboardControllerTests -testResults /tmp/hmi-keyboard-after.xml -logFile /tmp/hmi-keyboard-after.log
  git add Assets/Scripts/Zx200KeyboardController.cs Assets/Tests/EditMode/Zx200KeyboardControllerTests.cs
  git commit -m "feat: add automatic control ownership"
  ```

### Task 3: Implement the autonomous excavation state machine

**Files:**
- Create: `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`
- Create: `Assets/Scripts/Autonomy/AutonomousExcavationController.cs.meta`

**Interfaces:**
- Public enum `AutonomousExcavationState`: `Idle`, `WaitingForSite`, `AcquirePoint`, `LowerAndEnter`, `FillBucket`, `Lift`, `SwingToTruck`, `Dump`, `ConfirmDump`, `ReturnToDig`, `Completed`, `TruckFull`, `SoilExhausted`, `EmptyCut`, `ManualTakeover`, `Fault`, `Resetting`.
- Public properties `State`, `Status`, `FaultReason`, `IsRunning`, `DeliveredVolumeM3`, and `CompletedCycles`.
- Public methods `bool StartAutonomousJob()`, `bool StartAutonomousJob(double requestedTargetM3)`, `void RequestManualTakeover()`, and `void ResetAutonomousJob()`.
- Serialized dependencies `Zx200DigController machine`, `BucketSoilCutter cutter`, `BucketSoilUnloader unloader`, `SoilReceivingZone receiver`, and `MonoBehaviour siteReadinessSource`; all auto-resolve from the parent/main scene when omitted.

- [ ] **Step 1: Add pure boundary tests first.**

  Add `AutonomousExcavationControllerTests.cs` covering initial `Idle`, rejection when required dependencies are missing, finite positive target validation, target reached when delivered delta is at least target, and truck full when available capacity is at or below the configured minimum transfer volume. Use static helpers with exact signatures:

  ```csharp
  public static bool HasReachedTarget(double deliveredM3, double targetM3);
  public static bool IsReceiverFull(double availableM3, double minimumTransferM3);
  ```

- [ ] **Step 2: Run the new EditMode filter and verify it fails before implementation.**

  Run the Unity EditMode filter for `ExcavatorControls.Tests.AutonomousExcavationControllerTests`; expected failure is missing controller/helper symbols.

- [ ] **Step 3: Implement dependency resolution and public lifecycle.**

  In `Awake`, resolve the machine, cutter, unloader, receiver, keyboard controller, and the optional `CesiumMoundSite` readiness component by component name. Cache the `IsReady` property through reflection so the `ExcavatorControls` assembly does not introduce a circular reference to `WebDelivery.Runtime`. Reject missing references with `FaultReason` and never start a coroutine in that case.

- [ ] **Step 4: Implement the state coroutine with actual data gates.**

  Implement these exact stage rules:

  ```text
  WaitingForSite: wait for IsReady, with siteReadyTimeoutSeconds.
  AcquirePoint: require workReadyPose, empty bucket, active soil, receiver capacity, finite metrics.
  LowerAndEnter: StartAutomaticPose(approachPose), then require success.
  FillBucket: StartAutomaticPose(curlPose); require cutter.VolumeM3 increase >= minimumLoadVolumeM3.
  Lift: StartAutomaticPose(liftPose), then require success.
  SwingToTruck: StartAutomaticPose(swingToDumpPose), then dumpPreparePose.
  Dump: StartAutomaticPose(dumpPose); wait for receiver.VolumeM3 increase and empty airborne/pending volume.
  ConfirmDump: reject zero receiver increase; accept only actual receiver increase and empty bucket/flight.
  ReturnToDig: use returnSwingPose; stop as Completed/TruckFull/SoilExhausted or begin next cycle.
  ```

  `Dump` must wait on `BucketSoilUnloader`/`SoilFlightTransport` settling rather than call `TransferTo()` directly. Capture receiver baseline at job start and compute `DeliveredVolumeM3` from actual receiver records. Use a single stage timeout for each pose, `cutTimeoutSeconds`, and `dumpTimeoutSeconds`; route timeout to `Fault` with a specific message. If the curl stage reaches the soil floor without collecting the minimum, route to `SoilExhausted`; otherwise route to `EmptyCut`.

- [ ] **Step 5: Implement stop, manual takeover, and reset.**

  In `Update`, if a running job sees `keyboard.ManualInputActive`, call `RequestManualTakeover()`. That method stops the current automatic pose, releases keyboard ownership, stops the track drive, and leaves actual soil/vehicle inventory unchanged. `ResetAutonomousJob()` stops the coroutine, stops automatic motion and tracks, calls `cutter.ResetExcavation()`, requests `workReadyPose`, clears counters, and enters `Resetting` until the pose settles, then returns to `Idle`.

- [ ] **Step 6: Run EditMode state tests and commit the controller.**

  Re-run the new filter and the existing controller/keyboard filters. Expected: all new pure boundary tests pass; no C# compile errors. Commit the script and its meta only after the test output is captured:

  ```bash
  git add Assets/Scripts/Autonomy/AutonomousExcavationController.cs Assets/Scripts/Autonomy/AutonomousExcavationController.cs.meta
  git commit -m "feat: add autonomous excavation state machine"
  ```

### Task 4: Wire the controller into the main scene

**Files:**
- Modify: `Assets/Scenes/sence_test.unity`
- Modify: `README.md`

**Interfaces:**
- Scene object `ZX200 construction site` gets one `AutonomousExcavationController` component.
- Scene references bind existing file IDs: machine `10072338`, cutter `40524788`, unloader `40524786`, receiver `91113229`, site readiness `471781301`.
- `A` starts the default autonomous job, `Escape` requests manual takeover, and `Backspace` resets the excavation job; public lifecycle methods remain the integration API for HMI.

- [ ] **Step 1: Add the component to the scene YAML.**

  Add one component entry to the existing `ZX200 construction site` root and serialize only project-relative component references and bounded numeric settings. Do not serialize Cesium URL, tileset path, generated objects, or machine-specific absolute paths.

- [ ] **Step 2: Add the operator/API instructions to README.**

  Document the current one-point scope, the three runtime keys, the public lifecycle methods, and the exact completion criteria: receiver volume must increase from a real landing while cutter/flight/terrain accounting remains balanced.

- [ ] **Step 3: Start Unity in batch mode and verify scene compilation.**

  ```bash
  /home/sk/unity-editor/6000.5.10f1/Editor/Unity -batchmode -nographics -projectPath /home/sk/Unity/HMI_Unity6 -quit
  ```

  Expected: exit code 0 and no C# compiler errors. Review `git diff -- Assets/Scenes/sence_test.unity README.md` for only the intended component and documentation changes.

- [ ] **Step 4: Commit scene wiring.**

  ```bash
  git add Assets/Scenes/sence_test.unity README.md
  git commit -m "feat: wire autonomous excavation into main scene"
  ```

### Task 5: Verify one-shot and continuous real soil flow

**Files:**
- Create: `Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

**Interfaces:**
- Tests load `Assets/Scenes/sence_test.unity` additively, wait for `CesiumMoundSite.IsReady`, call `StartAutonomousJob(0.02)`, and wait until `IsRunning` is false or 90 realtime seconds expire.
- Tests read `AutonomousExcavationController.State`, `DeliveredVolumeM3`, `BucketSoilCutter`, `BucketSoilUnloader`, `SoilReceivingZone`, and `DeformableSoilTerrain` without injecting soil.

- [ ] **Step 1: Add the PlayMode test for one complete autonomous cycle.**

  Assert that the terminal state is not `Fault`, `ManualTakeover`, or `EmptyCut`; assert `DeliveredVolumeM3 > 0.005`, receiver volume increased by the same amount within `1e-5`, bucket and airborne/pending volume are near zero after settling, and `ManualSoilTestMotion.AssertBalance(cutter)` passes.

- [ ] **Step 2: Add the reset test.**

  After a completed job, call `ResetAutonomousJob()`, wait for `Idle`, and assert cutter volume, receiver source records, and `soil.RecalculateRemovedVolumeM3()` are zero within the existing tolerances.

- [ ] **Step 3: Run the focused PlayMode tests.**

  ```bash
  /home/sk/unity-editor/6000.5.10f1/Editor/Unity -batchmode -nographics -projectPath /home/sk/Unity/HMI_Unity6 -runTests -testPlatform playmode -testFilter ExcavatorControls.Tests.AutonomousExcavationPlayModeTests -testResults /tmp/hmi-autonomous-playmode.xml -logFile /tmp/hmi-autonomous-playmode.log
  ```

  Expected: one-cycle and reset tests pass. If they fail, inspect the state/status and soil-volume logs before changing pose or timeout parameters; never bypass a failed receiver or balance assertion.

- [ ] **Step 4: Run the complete EditMode suite and the main-scene startup check.**

  Run the full EditMode suite without `-quit` so Unity writes the XML results, then run the batch startup check. Known baseline failures from the earlier repository audit remain separately documented: `SoilMouthFlowTests.SideObstructionBlocksOnlyCoveredOutletBeforeInventoryDebit` and the Linux player-builder environment test. Do not count either as autonomous-flow success.

- [ ] **Step 5: Commit the verification tests.**

  ```bash
  git add Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs
  git commit -m "test: verify autonomous excavation cycle"
  ```

### Task 6: Record final verification and handoff

**Files:**
- Modify: `README.md`
- Create: `docs/verification/2026-09-22-autonomous-excavation.md`

- [ ] **Step 1: Record exact commands and results.**

  Include Unity version, scene path, terminal state, delivered volume, receiver volume, balance assertion, reset result, startup exit code, and any baseline failures. Separate “compile/startup passed” from “autonomous PlayMode passed”.

- [ ] **Step 2: Run `git diff --check`, `git status`, and a final commit audit.**

  ```bash
  git diff --check
  git status --short --branch
  git log --oneline --decorate -8
  ```

  Expected: only the documented source/design/test commits are ahead of `origin/main`; generated Unity directories remain ignored and no external map files are staged.

- [ ] **Step 3: Commit the verification record.**

  ```bash
  git add README.md docs/verification/2026-09-22-autonomous-excavation.md
  git commit -m "docs: record autonomous excavation verification"
  ```

## Self-Review Checklist

- Spec coverage: Tasks 1–2 provide cancellable motion and manual ownership; Task 3 covers all workflow states and stop conditions; Task 4 wires the main scene; Task 5 verifies real cut/receive/reset; Task 6 records evidence and Git state.
- Placeholder scan: no `TBD`, `TODO`, or unspecified implementation step appears in this plan.
- Type consistency: `StartAutomaticPose`, `StopAutomaticMotion`, `ManualInputActive`, `SetAutomaticControl`, `HasReachedTarget`, `IsReceiverFull`, and the public state/lifecycle members are named consistently across tasks.
- Scope: no automatic route planning or multi-point navigation is included; the current acceptance boundary is one fixed reachable point with continuous bucket cycles.
