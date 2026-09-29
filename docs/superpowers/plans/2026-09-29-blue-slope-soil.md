# 蓝线陡坡可切土层实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `sence_test` 只在道路下沿与坡脚之间的蓝线陡坡生成可切土层，铲斗可实际切出土；道路和坡脚保持原 Cesium。

**Architecture:** 从本地 Cesium 高度采样识别连续陡坡，使用坡度与位置共同约束 Terrain 实体和土量。禁止用一个长矩形整体替换地图；Cesium 原地图作为土层下方基底。烘焙后在静态场景验证铲斗可达性和土量变化。

**Tech Stack:** Unity 6、C#、Cesium for Unity、Unity Terrain、NUnit。

## Global Constraints

- 保留挖掘机和矿车原 Cesium 支撑地面。
- 不改用户无关的 prefab、设置和编辑器文件。
- 中文提交说明，作者 shenkai；不推送远端。

---

### Task 1: 确定蓝线坡面与切削失败原因

**Files:** `Assets/WebDelivery/Runtime/CesiumMoundSite.cs`; `Assets/Tests/EditMode/ConstructionSiteInitialLayoutTests.cs`

- [ ] 检查烘焙资源的坡度、洞口、斗尖附近土量与实际切削调用链。
- [ ] 写出道路、坡面和坡脚的独立坐标断言，运行确认现有烘焙失败。
- [ ] 记录应覆盖的坡面采样范围及铲斗可达段。

### Task 2: 生成沿陡坡的实体土层

**Files:** `Assets/WebDelivery/Runtime/CesiumMoundSite.cs`; `Assets/WebDelivery/Runtime/ConstructionSiteMapClip.cs`; `Assets/Tests/EditMode/ConstructionSiteClipTests.cs`

- [ ] 写地形分类与裁切失败测试并确认红灯。
- [ ] 将坡度分类同时用于实体、可切厚度和非土面洞口；原 Cesium 地面不被矩形清除。
- [ ] 运行测试确认绿色，并检查道路、坡脚仍可碰撞。

### Task 3: 重新烘焙与端到端验证

**Files:** `Assets/Scenes/sence_test.unity`; `Assets/ConstructionSite/Baked/SenceTest/*`; `Assets/Tests/PlayMode/*`

- [ ] 在隔离 Unity 实例重新烘焙带地图色彩的坡面静态资源。
- [ ] 验证 Play 就绪、斗尖落在可切实体、实际切削导致土量减少。
- [ ] 检查场景截图，确认蓝坡连续、橙色道路及地坪覆盖消失。
- [ ] 提交到本地分支并快进合并 `main`；不推送。
