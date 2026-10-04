# 挖掘装车闭环验收实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在当前 `sence_test` 真实坡面上连续多斗挖掘，按车厢实际接收量达到任务目标后正常停止，并可复现验收。

**Architecture:** 先确认容量与失败来源，再调整挖点调度；账本沿用 `BucketSoilCutter`、`SoilReceivingZone`。场景测试针对完整终态、误差与安全闸门，不伪造容量。

**Tech Stack:** Unity 6000.5.10f1、C#、Cesium for Unity、NUnit PlayMode/EditMode。

## Global Constraints

- 原 Cesium 道路和坡脚地面不改；可切土层只在侧壁。
- 铲斗配置 0.303886 m³；矿车当前有效容量 5.984 m³，二者均不得为演示虚增或缩小。
- 只对本次代码/测试/文档/明确场景变更提交；保留用户其他未提交文件。
- 中文提交说明、作者 shenkai；不推送远端。

---

### Task 1: 容量与失败基线

**Files:** `Assets/Tests/EditMode/AutonomousExcavationControllerTests.cs`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`、`Assets/ConstructionSite/ParkedDumpTruck.prefab`

- [ ] 运行当前编辑模式容量测试和单斗场景测试，记录斗容、车容、每斗车厢增量、终态和日志。
- [ ] 用不修改生产场景的短场景测试复现多斗目标无法结束，区分“逻辑停止”和“测试框架/外网错误”。
- [ ] 为任务目标与车满状态增加独立断言：完成指定土量时允许未满车，车满只能由真实余量判定。

### Task 2: 挖点推进与真实连续循环

**Files:** `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`、`Assets/Tests/EditMode/AutonomousExcavationControllerTests.cs`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [ ] 先写失败测试：有效挖点完成一斗后不应无条件丢弃；空挖/危险底深必须推进；挖点耗尽有明确终态。
- [ ] 提炼一个只负责选择下一挖点的最小策略函数，复用当前厚度与空挖反馈，不改卸料/碰撞规则。
- [ ] 在 PlayMode 跑至至少三次真实装车和一个确定终态；检查土量守恒、回转后返回、手动接管/复位。

### Task 3: 稳定离线验收与最终交付

**Files:** `Assets/Tests/PlayMode/BakedSiteStartupTests.cs`、`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`、`README.md`、`Tools/WEB_DELIVERY.md`

- [ ] 查明 Cesium ion SSL 日志来源；仅对已确认的非工作地图连接做精确隔离，绝不全局忽略错误。
- [ ] 跑编辑/播放模式定向回归，记录通过、失败与运行时间；若 0.5～1 m³ 无法在当前工作面完成，报告需要的挖点/姿态条件，不改容量掩盖。
- [ ] 检查静态场景与运行包；更新 README，使容量、任务目标和“车满”概念一致。
- [ ] `git diff --check`、逐项对照设计验收，中文提交到隔离分支；在没有用户要求的情况下不 push。
