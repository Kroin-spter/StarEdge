# 稳定多斗自主挖掘装车 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 `sence_test` 调整为挖掘机、土区、车辆互不重叠的装车场，并让自主流程在安全抬升、回转、卸料、返回后连续执行多斗。

**Architecture:** 保留现有 `AutonomousExcavationController` 的守恒土量和终止状态机，在其卸料前增加独立的高位准备和最终接收区校验；由施工场地编辑器统一计算车辆停车位，避免场景序列化位置与生成逻辑再次漂移。用小型几何策略类承载布局安全判定，使 EditMode 测试不依赖 Cesium 和 PhysX 播放态。

**Tech Stack:** Unity 6, C#, NUnit EditMode tests, Unity ArticulationBody, Cesium runtime/editor integration.

## Global Constraints

- 不改变 Cesium 本地地图资源和 `CesiumMoundSite` 的地面衔接逻辑。
- 不把土量直接写入铲斗或车厢；所有土量仍由 `BucketSoilCutter`、`SoilFlightTransport`、`SoilReceivingZone` 实际转移。
- 自动启动键继续使用 `P`，不占用挖掘机的 `A/D` 履带转向。
- 目标量 `1m³` 保持为多斗验证目标，车辆满、挖尽、空挖、超时、人工接管和复位行为保持有效。
- 新增或修改的公开 C# 方法必须有中文 XML 文档，Git 提交说明使用中文，作者名使用 `shenkai`。
- 当前环境没有 Unity Editor 可执行文件；必须完成静态检查和可执行的仓库级测试，Unity EditMode/PlayMode 测试若无法启动要明确记录。

## 文件结构与职责

- Create: `Assets/Scripts/Autonomy/ExcavationSiteLayoutSafety.cs` — 纯几何布局策略和安全间距判定。
- Modify: `Assets/Robotics/zx200_description/Editor/Zx200ConstructionSiteSetup.cs` — 集中定义车辆停车位，生成主场景时不再将车辆放进土区边缘。
- Modify: `Assets/Scripts/Autonomy/AutonomousExcavationController.cs` — 将卸料过程改为先高位准备、再保持高位回转，并在打开铲斗前验证接收区与净空。
- Modify: `Assets/Scenes/sence_test.unity` — 序列化当前主场景的新车辆位置和自动流程参数。
- Modify: `Assets/Tests/EditMode/AutonomousExcavationControllerTests.cs` — 几何安全和多斗边界回归测试。
- Modify: `Assets/Tests/EditMode/ConstructionSiteSetupTests.cs` — 施工场生成停车位的尺寸/位置约束测试。
- Create: `docs/superpowers/plans/2026-09-23-stable-multi-bucket-loading.md` — 本实施计划。

### Task 1: 建立布局安全策略的失败测试

**Files:**
- Create: `Assets/Scripts/Autonomy/ExcavationSiteLayoutSafety.cs`（先不创建生产文件，测试先以编译失败作为红灯）
- Modify: `Assets/Tests/EditMode/ConstructionSiteSetupTests.cs`
- Modify: `Assets/Tests/EditMode/AutonomousExcavationControllerTests.cs`

**Interfaces:**
- 生产代码将提供 `ExcavationSiteLayoutSafety.HasRequiredClearance(Bounds soilBounds, Bounds truckBounds, float clearanceMeters)`。
- 生产代码将提供 `ExcavationSiteLayoutSafety.IsInsideExpandedBounds(Vector3 point, Bounds bounds, float marginMeters)`。
- 测试验证土区扩大边界内的车体位置被拒绝，留出 2m 侧向间隔的位置被接受；并验证 1m³ 目标量在 0.303886m³ 铲斗容量下需要至少 4 斗的循环边界。

- [x] **Step 1: 写失败测试**

  在 `ConstructionSiteSetupTests` 增加以下测试方法：

  ```csharp
  [Test]
  public void LayoutSafety_RejectsTruckOverlappingExpandedSoilBounds()
  {
      Bounds soil = new Bounds(new Vector3(330, 8, 405), new Vector3(6, 1, 6));
      Bounds overlappingTruck = new Bounds(new Vector3(332, 8, 403), new Vector3(3.24f, 3, 6.3f));
      Bounds separatedTruck = new Bounds(new Vector3(337, 8, 402), new Vector3(3.24f, 3, 6.3f));

      Assert.That(ExcavationSiteLayoutSafety.HasRequiredClearance(soil, overlappingTruck, 2f), Is.False);
      Assert.That(ExcavationSiteLayoutSafety.HasRequiredClearance(soil, separatedTruck, 2f), Is.True);
  }
  ```

  在 `AutonomousExcavationControllerTests` 增加：

  ```csharp
  [Test]
  public void OneCubicMetreTargetRequiresFourBucketsAtCurrentCapacity()
  {
      const double target = 1.0;
      const double bucket = 0.303886;

      Assert.That(System.Math.Ceiling(target / bucket), Is.EqualTo(4));
  }
  ```

- [x] **Step 2: 运行指定 EditMode 测试确认红灯**

  运行 Unity Test Runner 的 `ConstructionSiteSetupTests` 和 `AutonomousExcavationControllerTests`。本环境没有 Unity Editor，命令返回 `UNITY_EDITOR_NOT_FOUND`（退出码 127），因此未能启动红灯编译阶段。

- [x] **Step 3: Commit**

  测试红灯命令受环境限制，未提交独立红灯测试，直接进入实现，避免把必然无法编译的测试提交到主分支。

### Task 2: 实现布局策略并统一车辆停车位

**Files:**
- Create: `Assets/Scripts/Autonomy/ExcavationSiteLayoutSafety.cs`
- Modify: `Assets/Robotics/zx200_description/Editor/Zx200ConstructionSiteSetup.cs`
- Modify: `Assets/Tests/EditMode/ConstructionSiteSetupTests.cs`

**Interfaces:**
- `public static bool HasRequiredClearance(Bounds soilBounds, Bounds truckBounds, float clearanceMeters)`：比较 X/Z 平面上的扩大土区和车辆包围盒。
- `public static bool IsInsideExpandedBounds(Vector3 point, Bounds bounds, float marginMeters)`：判断位置是否进入扩大包围盒。
- `Zx200ConstructionSiteSetup.TruckParkingOffsetLocal`：统一使用挖掘机局部坐标中的右前方停车位偏移，不再散落 `new Vector3(5,0,5)`。

- [x] **Step 1: 写最小生产实现**

  `ExcavationSiteLayoutSafety` 只比较有限的 X/Z 平面，拒绝非有限或负间距输入；垂直尺寸不参与车辆与土区的水平占位判断。每个公开方法写中文 XML 文档。

- [x] **Step 2: 统一编辑器停车位**

  在 `Zx200ConstructionSiteSetup` 中定义：

  ```csharp
  public static readonly Vector3 TruckParkingOffsetLocal = new Vector3(7f, 0f, 4f);
  private const float TruckSoilClearanceMeters = 2f;
  ```

  生成场景时使用 `robot.transform.TransformPoint(TruckParkingOffsetLocal)`，并在保存前通过车辆/土区 Collider bounds 调用安全策略；不满足条件时抛出包含“车辆停车位与土区净距不足”的异常。

- [x] **Step 3: 运行布局和已有施工场测试确认绿灯**

  已尝试运行 `ConstructionSiteSetupTests`，但本环境没有 Unity Editor；已通过静态检查确认没有增加重复施工场逻辑，旧 Solid work pad 仍由 Cesium 配置流程禁用。

- [x] **Step 4: Commit**

  ```bash
  git add Assets/Scripts/Autonomy/ExcavationSiteLayoutSafety.cs Assets/Robotics/zx200_description/Editor/Zx200ConstructionSiteSetup.cs Assets/Tests/EditMode/ConstructionSiteSetupTests.cs
  git -c user.name=shenkai commit -m "feat: 增加施工场地安全停车布局"
  ```

### Task 3: 将自动卸料路径改成高位回转的多斗流程

**Files:**
- Modify: `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`
- Modify: `Assets/Tests/EditMode/AutonomousExcavationControllerTests.cs`

**Interfaces:**
- 新增序列化参数 `dumpClearanceMarginMeters`，默认 `0.25m`，表示铲斗刃口相对车厢最高实体的安全高度。
- 新增私有协程 `RaiseToDumpClearance()`，在回转前保持当前回转角，将动臂/斗杆/铲斗调整到 `swingToDumpPose` 的高位参数。
- `AlignDumpSwing()` 只修改回转角，不再和动臂/斗杆同时运动。
- 新增私有方法 `IsDumpApproachSafe()`，同时要求铲斗斗尖位于接收区上方、斗尖超过车厢最高实体加安全裕量。

- [x] **Step 1: 写失败测试**

  在 `AutonomousExcavationControllerTests` 增加默认净空参数测试：

  ```csharp
  [Test]
  public void NewController_UsesPositiveDumpClearanceMargin()
  {
      GameObject root = new GameObject("autonomous");
      try
      {
          var controller = root.AddComponent<AutonomousExcavationController>();
          Assert.That(controller.DumpClearanceMarginMeters, Is.EqualTo(.25f).Within(.0001f));
      }
      finally { Object.DestroyImmediate(root); }
  }
  ```

- [x] **Step 2: 运行测试确认红灯**

  本环境没有 Unity Editor，命令返回 `UNITY_EDITOR_NOT_FOUND`（退出码 127）；通过静态检查确认新接口已补齐。

- [x] **Step 3: 实现高位回转和卸料安全门控**

  在 `RunJob()` 的 `Lift` 与 `SwingToTruck` 之间调用 `RaiseToDumpClearance()`。`AlignDumpSwing()` 改为以当前 boom/arm/bucket 为固定值，只以目标回转角运行。对准后、打开铲斗前调用 `IsDumpApproachSafe()`；失败时进入中文 `Fault`，不允许进入 `Dump`。

  继续使用现有 `WaitForDump()` 的实际接收量确认和 `CompletedCycles++`，并保留原有循环终止条件。

- [x] **Step 4: 运行自动流程静态回归检查**

  检查 `RunJob()` 中仍存在 `while (IsRunning)`、`CompletedCycles++`、目标量/车满判断；检查 `P`、`A`、`D` 的按键契约未被改动。

- [x] **Step 5: Commit**

  ```bash
  git add Assets/Scripts/Autonomy/AutonomousExcavationController.cs Assets/Tests/EditMode/AutonomousExcavationControllerTests.cs
  git -c user.name=shenkai commit -m "fix: 增加多斗高位回转卸料保护"
  ```

### Task 4: 更新场景序列化数据并完成验证

**Files:**
- Modify: `Assets/Scenes/sence_test.unity`
- Modify: `Assets/Robotics/zx200_description/Editor/Zx200ConstructionSiteSetup.cs`（若需要同步默认参数）
- Modify: `README.md`（若自动流程说明需要同步）

- [x] **Step 1: 更新主场景**

  将 `ParkedDumpTruck` 的场景位置同步到编辑器停车位，保持 CesiumMoundSite 的 `machine/soil/truck` 引用不变；确认 Soil、Truck Receiver、Autonomous Controller 引用仍指向原对象。

- [x] **Step 2: 检查序列化参数**

  确认主场景中 `targetVolumeM3: 1`、`dumpClearanceMarginMeters: 0.25`、`autonomousStartKey: P`，没有将 `A/D` 写成自主启动键。

- [x] **Step 3: 运行验证**

  依次执行：

  ```bash
  git diff --check
  rg -n "TruckParkingOffsetLocal|dumpClearanceMarginMeters|CompletedCycles\+\+|while \(IsRunning\)|KeyCode\.A|KeyCode\.D|KeyCode\.P" Assets/Scripts Assets/Scenes/sence_test.unity
  git status --short
  git log -3 --pretty=format:'%h %an %s'
  ```

  已运行 `git diff --check` 和 `rg` 静态检查；Unity Editor 不可用，未完成 EditMode/PlayMode 和真实 `P` 多斗卸料验证。

- [x] **Step 4: Commit 场景与文档**

  ```bash
  git add Assets/Scenes/sence_test.unity README.md
  git -c user.name=shenkai commit -m "chore: 同步多斗装车测试场景"
  ```

## Plan Self-Review

- 场景距离问题：Task 2 与 Task 4 覆盖土区/车辆净距和 Cesium 场景引用保持。
- 首斗卡车问题：Task 3 覆盖先抬高、再回转、接收区/车厢最高点门控。
- 多次挖掘：Task 3 保留现有多斗 while 循环、实际接收量、目标量和车满停止；Task 1 覆盖 1m³ 至少 4 斗边界。
- 输入约束：全局约束和 Task 4 保证 `P` 不被改回 `A/D`。
- 验证限制：Task 4 明确 Unity Editor 不可用时的可验证范围，不以静态检查替代运行态证明。

## Execution Note

实现已落在当前工作区，提交前仍需在有 Unity 6 Editor 的机器上打开 `Assets/Scenes/sence_test.unity`，运行 EditMode 测试，并按 README 使用 `P` 验证至少四斗或达到目标量；本次本地提交不会代替该运行态验收。
