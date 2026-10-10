# 人工切自动后的多挖点实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 人工任意可达回转姿态切自动后，连续从不同坡面点挖土并装入矿车。

**Architecture:** 保留现有自动状态机与真实土量链路，仅改变挖点起点与相邻点遍历顺序；人工有土回位前先抬斗脱坡。

**Tech Stack:** Unity 6、C#、PlayMode 测试。

## Global Constraints

- 每斗只切一次，不伪造土量，不移动矿车。
- 保护现有碰撞、切削阻力、人工接管和车厢接收检查。

---

### Task 1: 人工角度起点与逐斗换点

**Files:** `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`, `Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [x] 写 PlayMode 测试，证明旧代码首斗回固定 0°，并验证目标量、不同切削角和车厢实收。
- [x] 从人工实际回转角选最近挖点；成功或空挖后走向相邻挖点，连续一轮空挖才停止。
- [x] 回车厢后直接转到下一挖点的安全待机方向。
- [x] 红绿测试通过。

### Task 2: 人工已有土的回位交接

**Files:** `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`, `Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [x] 用既有人工满斗 PlayMode 复现贴坡收斗超时。
- [x] 回位前先禁切并动臂脱坡，再收拢履带；保留斗内土量。
- [x] 复测人工满斗交接和默认 1 m³ 连续装车。

### Task 3: 交付

**Files:** `README.md`

- [x] 更新使用说明，检查差异并提交中文注释、shenkai 作者的 Git 提交。
