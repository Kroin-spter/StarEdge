# 坡脚履带平滑回位实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 扩大安全回位范围，避免 15° 转向边界停滞，并保持多斗装车。

**Architecture:** 在原有回位状态内分离朝向误差和位移误差；现场边界仍由 `CesiumMoundSite` 负责，履带接地仍由 `Zx200TrackDrive` 提供。测试使用真实场景与物理履带。

**Tech Stack:** Unity 6、C#、Unity PlayMode 测试。

## Global Constraints

- 不瞬移、不伪造土量，不关闭碰撞、接地、人工接管保护。
- Git 提交说明用中文，作者为 shenkai。

---

### Task 1: 复现远距带偏航回位

**Files:** `Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [ ] 用真实履带人工倒车和转向，直到距作业位超过既有近距测试且约 15° 偏航。
- [ ] 启动自动任务，断言实际停车误差、完成至少一斗且车厢实收大于零。
- [ ] PlayMode 运行该测试，确认旧实现失败于停滞或范围限制。

### Task 2: 平滑分阶段回位与安全走廊

**Files:** `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`, `Assets/WebDelivery/Runtime/CesiumMoundSite.cs`, `Assets/Scripts/Zx200TrackDrive.cs`

- [ ] 用目标前后投影与偏航滞回决定原地转向或行驶；距离和角度分别更新进度时间。
- [ ] 扩大经地图验证的纵向走廊，有限放宽短时接地探测丢失，不取消安全故障。
- [ ] PlayMode 复跑红色测试，确认停车精度及装车实收。

### Task 3: 回归与交付

**Files:** `README.md`

- [ ] 复跑前进、倒车、人工接管和多斗测试。
- [ ] 更新使用说明，检查 `git diff --check`，仅提交本次文件。
