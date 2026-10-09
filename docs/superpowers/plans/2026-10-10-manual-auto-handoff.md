# 人工与自动挖掘接管实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 人工任意安全姿态一键切入自动，含土斗先装车，随后继续多斗；自动各阶段可随时人工接管。

**Architecture:** 在现有控制器入口加入回中仲裁和土量分支，复用真实姿态反馈、装车/回转/安全检查，不新增第二套卸料状态机。人工输入组件只负责提供完整实时输入活跃度。

**Tech Stack:** Unity 6000.5.10f1，C#，Cesium for Unity，NUnit EditMode/PlayMode。

**Spec:** `docs/superpowers/specs/2026-10-10-manual-auto-handoff-design.md`

## Global Constraints

- 不清空或伪造人工已挖土量、斗内土量、车厢接收量。
- Cesium 坡脚地面和侧坡可切土层保持现状；履带只在自动动作期间锁定，交还人工后恢复原设置。
- 安全条件不满足时显式拒绝/停止，不强行运动；中文提交、作者 shenkai，不 push。
- 保留工作区既有 prefab、Web 构建配置与烘焙资源的用户改动。

## Review Focus

- `P` 与持续手柄/键盘输入同帧出现：自动模式不得立即被旧输入取消；回中后新的输入仍应接管。
- AZ 履带单独移动：自动模式应识别新的履带操作并交还控制。
- 人工斗内少量、满斗或临近车满：只凭真实可接收量选择装车或安全拒绝。
- 任意切削/回转/卸料阶段接管：关节停止并保持实测姿态，卸料/切削状态和土量不回滚。
- 重复按 `P`、地图未就绪及底盘离开工作区：不可产生并行协程或异常穿透。

### Task 1: 输入所有权与回中锁

**Files:** `Assets/Scripts/Zx200KeyboardController.cs`、`Assets/Scripts/Autonomy/AutonomousExcavationController.cs`、`Assets/Tests/EditMode/Zx200KeyboardControllerTests.cs`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [x] 写失败测试：AZ 履带输入算人工动作；`P` 时保持的人工输入不立刻取消自动，回中后的新动作会接管。
- [x] 运行定向测试，确认红灯来源正确。
- [x] 实现完整输入检测和回中锁，不改变原键位；确认 `Esc` 无条件优先接管。
- [x] 运行定向测试与原人工接管测试，记录结果。

### Task 2: 含土斗从实测姿态接续装车

**Files:** `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [x] 写真实场景红灯测试：人工装入可计量土量并改变关节姿态后按自动入口，第一斗必须跳过新切削并实收入车，随后至少再自动完成一斗。
- [x] 运行并记录红灯；实现从当前实测姿态安全保土和 `Lift` 接入原卸料链，任务起始车厢量与人工斗土分开记账。
- [x] 跑绿灯，检查实际阶段序列、车厢土量守恒、斗内清零及底盘不移动。

### Task 3: 空斗任意安全姿态、异常与回归

**Files:** `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`、`README.md`

- [x] 写空斗偏离停放姿态接入、含土卸料中人工接管、无效位置/容量拒绝的失败测试。
- [x] 实施受控脱险和前置安全检查，复用原有车厢碰撞/切削限制，不修改土层容量。
- [x] 跑新接管测试、原 1m³ 多斗测试和控制器 EditMode 定向回归；图形渲染关键动作截图。
- [ ] `git diff --check`、审阅文件与用户改动边界、中文提交到隔离分支；按用户本轮要求安全并入 `main`，不 push。
