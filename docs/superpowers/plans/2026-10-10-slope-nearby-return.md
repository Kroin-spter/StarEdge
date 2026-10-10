# 坡脚近距回位与单次安全切削实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 坡面旁更合理的停车位、人工前后驾驶后的真实履带回位，并以单次安全切削保持多斗装车。

**Architecture:** 烘焙资产给出唯一目标位；小型闭环履带回位器复用现有 `Zx200TrackDrive`，由自动状态机在切削前调用；现有土量反馈切削器继续负责保土和安全限制。

**Tech Stack:** Unity 6000.5.10f1、C#、Cesium for Unity、NUnit EditMode/PlayMode。

## Global Constraints

- 不瞬移，不伪造斗土/车土；不修改 Cesium 坡脚地面或矿车位置。
- 仅坡脚附近有限范围回位；越界、失去接地、碰撞、超时和人工接管时制动。
- 中文提交、作者 shenkai；保留用户现有未暂存 prefab、Web 配置和烘焙资源。

### Task 1: 履带回位闭环

**Files:** `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`、`Assets/Scripts/Zx200TrackDrive.cs`、`Assets/Tests/EditMode/AutonomousExcavationControllerTests.cs`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [ ] 写失败测试：前后偏移产生朝目标的真实履带指令；到位时零指令；越界拒绝；回位中 `Esc` 停车。
- [ ] 运行定向 EditMode 与 PlayMode 确认预期红灯。
- [ ] 接入受限位置/朝向反馈控制、接地与障碍检查、控制所有权和状态机前置回位；不调用 `TeleportRoot`。
- [ ] 运行上述测试至绿灯，并确认原人工含土接续用例。

### Task 2: 烘焙停车位向坡面微调

**Files:** `Assets/ConstructionSite/Baked/SenceTest/ConstructionSite.asset`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [ ] 记录当前坡面前向、斗尖净距和矿车净距，写场景定位验收测试。
- [ ] 小步前移烘焙 `machinePosition`，运行准备就绪和真实单斗用例；不动土层高度图或坡脚 Cesium 地面。
- [ ] 根据净距结果收敛位移，验证 Scene/Play 中一致。

### Task 3: 更充分的单斗切削与连续装车回归

**Files:** `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`、`README.md`

- [ ] 写失败测试：在安全可切土条件下单斗实收量高于当前基线，并保持底面/阻力保护。
- [ ] 调整 `targetBucketFillFraction` 和必要的早停逻辑，不降低安全阈值。
- [ ] 运行前后回位、人工接续、1 m³ 连续装车与图形阶段截图验收。
- [ ] 审核变更边界和 `git diff --check`，中文提交并按用户现有工作流合入 main，不 push。
