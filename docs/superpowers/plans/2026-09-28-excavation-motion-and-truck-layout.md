# 挖掘姿态与矿车站位实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `sence_test` 中降低切入抬臂、消除近满斗停顿、形成更自然的携土姿态，并安全缩短矿车距离，保持双斗自动装车通过。

**Architecture:** 主场景覆盖通用 ZX200 的姿态参数；自动控制器依据实测斗量、关节姿态和斗尖/土面净空调度切削与闭斗；施工区继续负责矿车位置验证。PlayMode 测试以实测动作轨迹和接收土量验收。

**Tech Stack:** Unity 6000.5.10f1、C#、Unity Test Framework、Cesium for Unity。

## Global Constraints

- 不修改 ZX200 通用预制体、底盘手动驾驶或 Cesium 选址。
- 不伪造土量；保持碰撞、坡面净空和车厢接收判断。
- Git 提交说明使用中文，作者名 `shenkai`；不推送远端。

---

### Task 1: 切入与近满斗动作

**Files:** `Assets/Scenes/sence_test.unity`、`Assets/Scripts/Autonomy/AutonomousExcavationController.cs`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [x] 增加 PlayMode 断言，记录入土前大臂角与斗量达到 80% 后的铲斗角度/进展；运行确认旧行为失败。
- [x] 用主场景姿态覆盖降低无谓抬臂；将近满斗收斗作为显式动作约束，避免等到切削超时才补做闭斗。
- [x] 运行真实单斗/双斗 PlayMode 测试，检查 `FillBucket` 不长时间静止且仍有实测土量。

### Task 2: 携土姿态与矿车站位

**Files:** `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`、`Assets/WebDelivery/Runtime/CesiumMoundSite.cs`、`Assets/Scenes/sence_test.unity`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`、`Assets/Tests/EditMode/ConstructionSiteInitialLayoutTests.cs`

- [x] 增加携土大臂/斗杆/铲斗姿态和矿车侧向距离断言，验证旧场景不满足目标。
- [x] 调整连续携土目标，并缩小矿车本地侧向偏移；若地形支撑或碰撞净空不足则回退到安全值。
- [x] 运行施工区几何测试、双斗自动装车测试及场景布局测试，核对车厢真实接收土量与碰撞保护。

### Task 3: 收尾验证与提交

**Files:** 上述改动文件与本计划。

- [x] 检查 `git diff --check`、场景序列化、测试报告和 Unity 日志。
- [x] 在本地以中文提交，确认工作区整洁；不执行 `git push`。
