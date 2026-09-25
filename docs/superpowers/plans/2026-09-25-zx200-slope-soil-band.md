# ZX200 Cesium 贴坡可切土带实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Cesium 红框坡面生成可切、可计量的贴坡土带，并把 ZX200 与矿车定位到稳定可达的坡下装车位置。

**Architecture:** 复用 Cesium 三角网采样、`ConstructionSiteMapClip` 和 `DeformableSoilTerrain` 高度场；用实际坡面梯度计算法线厚度，在地图基底上构造窄带。挖掘机和矿车由显式锚点或经稳定性筛选的候选坡面布置。切削准备暂停计量，首次切削运动同时驱动斗杆与铲斗。

**Tech Stack:** Unity 6.5.10f1、Cesium for Unity、C#、Terrain/TerrainCollider、Unity Test Framework、NUnit。

## Global Constraints

- 不覆盖、不暂存、不提交任务开始前的用户改动。
- `Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs` 已有两行用户改动，修改附近内容时保留原有 `linearVelocity` 改动。
- Cesium 替换范围必须与替代 Terrain 水平范围一致；范围内只能有一个有效地面碰撞面。
- 土方账本只认可高度图真实移除、铲斗实际保土和车厢实际接收量。
- 挖机履带固定；斗齿初次接触时斗杆和铲斗必须同步运动。
- Git 提交说明使用中文；本计划执行阶段不 push。

---

### Task 1：建立贴坡土带几何单元测试

**Files:**
- Modify: `Assets/Tests/EditMode/MoundFloorTests.cs`
- Modify: `Assets/WebDelivery/Runtime/CesiumMoundSite.cs`（仅在 RED 已确认后新增纯几何辅助函数）

- [x] **Step 1: 写法线厚度回归测试**

断言水平地面法线 `Vector3.up`、土层法向厚度 `0.5m` 时竖直增量是 `0.5m`；45° 坡面单位法线 `(0, 0.7071068, 0.7071068)` 时竖直增量约 `0.7071068m`，对应沿法线位移仍为 `0.5m`。

- [x] **Step 2: 写高度场不可表示坡度的拒绝测试**

用 `normal.y = 0` 的垂直面法线验证辅助函数拒绝生成有限高度增量；用 `normal.y = 0.1` 验证低于配置安全阈值时拒绝而非产生极高土墙。

- [x] **Step 3: 在 Unity EditMode 中运行两个新测试并确认按预期失败**

测试必须因几何辅助 API 尚不存在而失败，而非程序集导入或场景加载错误。

- [x] **Step 4: 实现最小公开纯函数并复跑测试**

新增静态函数 `CesiumMoundSite.GetVerticalOffsetForNormalThickness(float thicknessMeters, Vector3 surfaceNormal, float minimumNormalY)`；归一化输入法线，对无效输入、非正厚度和 `normal.y < minimumNormalY` 抛出 `ArgumentOutOfRangeException`，否则返回 `thicknessMeters / normal.y`。

- [x] **Step 5: 运行 `MoundFloorTests` 全部用例并确认通过**

---

### Task 2：为真实坡面与稳定设备位编写场景验收

**Files:**
- Modify: `Assets/Tests/PlayMode/MoundSiteTests.cs`
- Modify: `Assets/WebDelivery/Runtime/CesiumMoundSite.cs`

- [x] **Step 1: 将场景测试改为检查坡面土带**

在 `OpenPitWorksiteReplacesOneCesiumFootprintWithSteppedSoil` 中断言工作面中心高度相对 Cesium 基底有非零土层厚度，左右横向覆盖连续，坡面上、下边缘平滑衰减；移除原有“挖机前方水平土脊”专属高度差断言。

- [x] **Step 2: 增加设备位置和唯一碰撞验收**

断言履带接地点高度差不超过 `0.35m`，挖机朝向坡面；矿车实体边界与土带保持至少 `2m` 净距且在挖机回转/接收区可达；土区内向下射线只命中替代 TerrainCollider，不命中 Cesium MeshCollider。

- [x] **Step 3: 运行新场景测试确认在旧平土带上失败**

保存完整测试日志，区分期望断言失败和 Cesium 加载错误。

- [x] **Step 4: 实现坡面候选选择、稳定平台筛选和安全设备定位**

支持显式 `workFaceAnchor`；未配置锚点时在启动挖机原位置周边扫描候选坡面。对每个候选点采样坡脚/坡面/两侧宽度及履带、矿车支撑点；只接受坡面高度差足够、履带与车位地面高差不超过 `0.35m` 且车体无重叠的候选。无候选时不移动挖机/矿车并设置清楚的 `Status`。

- [x] **Step 5: 实现法线等厚土带与地图基底保护**

在候选替换范围逐格采样 Cesium 原地面；保留为 `ground` 高度图。对工作面掩模内的格点以中心差分求坡面法线，把目标法向土厚换算为竖直增量；沿横向和坡面上下边缘 smoothstep 收边。拒绝越界、不可表示和掩模外加土。

- [x] **Step 6: 复跑场景测试并验证 Cesium 裁切边界连续**

确认候选区内原地图渲染和碰撞已裁切，边界外 `0.05m` 探针仍命中 Cesium 网格，原 Cesium 源 Terrain 数据不变。

---

### Task 3：让替换地形恢复 Cesium 影像、仅保留土带材质

**Files:**
- Modify: `Assets/WebDelivery/Runtime/ConstructionSiteSurfaceAppearance.cs`
- Modify: `Assets/WebDelivery/Runtime/CesiumMoundSite.cs`
- Modify: `Assets/Tests/PlayMode/MoundSiteTests.cs`

- [x] **Step 1: 写外观边界测试**

验证贴坡施工区在裁切地图前安装影像外观组件；无图形设备时不伪造影像、不改 TerrainLayer，施工区仍能就绪。

- [x] **Step 2: 运行测试确认影像接入缺失**

- [x] **Step 3: 为土带提供局部影像 alpha-mask 混合入口**

捕获裁切前已加载的 Cesium 影像；用工作面网格掩模将影像赋给未加土的坡面，保留既有 Soil TerrainLayer 于可切土带，清理时还原 TerrainLayer、alphamap 和运行时纹理资源。

- [x] **Step 4: 在地图裁切前捕获影像，复跑外观测试**

在裁切前调用外观融合，不创建平面覆盖层或额外 Collider；无 GPU 时沿用原土层材质。此容器以 `-nographics` 跑测试，实际 GPU 捕获效果仍需在 Unity Game 视图目视复核。

---

### Task 4：保证初次切土时斗杆与铲斗已经协同运动

**Files:**
- Modify: `Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`
- Modify: `Assets/Scripts/Autonomy/AutonomousExcavationController.cs`

- [x] **Step 1: 先写首次移除时姿态变化的回归断言**

在真实 Cesium PlayMode 测试中记录 `ExcavatedVolumeM3` 第一次增长时的状态、姿态和 `CurrentMotionAxis`；要求状态为 `FillBucket`，且首次增长前/同一物理步里斗杆与铲斗角度都已改变。

- [x] **Step 2: 跑测试确认旧阶段顺序触发回归失败**

- [x] **Step 3: 切入定位期间暂停切削记账**

`PrepareForCut` 将 `BucketSoilCutter` 设为 suppressed，保持现有碰撞过滤；闭斗和动臂进入期间不移除地形。切削命令启动后，继续暂停记账，直到斗杆、铲斗相对起始姿态各自实际运动至少 `0.1°`。失败、人工接管和复位路径恢复原 suppression 状态。

- [x] **Step 4: 从首次土面接触启动斗杆回拉与铲斗收斗**

`FillBucket` 用同一 `StartAutomaticPose` 目标同时改变斗杆与铲斗，仅在两关节均已开始运动后恢复切削计量；目标土量、超时、空挖判断继续基于现有真实土量反馈。

- [x] **Step 5: 复跑首斗测试和完整自动装车 PlayMode 测试**

要求首次增长符合姿态断言；单斗/多斗流程完成回转、车厢接收和返回；履带位移、超时和车体碰撞保护继续通过。

---

### Task 5：打开场景并完成专项交付检查

**Files:**
- Modify only if required: `Assets/Scenes/sence_test.unity`
- Preserve: any pre-existing modifications in `Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [x] **Step 1: 保存最终场景锚点与布局，仅写入通过运行时验证的数值**

- [x] **Step 2: 运行 `git diff --check`、检查完整 diff 与 Git 状态**

- [x] **Step 3: 运行相关 EditMode、MoundSite 和自主装车 PlayMode 套件**

记录 Unity 版本、测试数、失败数、首斗土量和履带位移；既有无关 native teardown 日志单独注明。

最终专项结果（Unity 6.5.10f1，隔离副本 + Cesium 本地 tileset）：`MoundFloorTests` 25/25、`MoundSiteTests` 1/1、首斗实际移土测试 1/1（斗内峰值约 0.1910m³、地形累计移除约 0.1928m³）、完整自动循环 1/1（4 循环、车厢实际接收 0.405791m³、履带漂移 0m）。场景在无图形设备下安全使用 Soil TerrainLayer 回退；GPU 影像融合路径仍需在 Unity Game 视图目视确认。无图形测试日志另有 URDF/Assimp 的 `libdl.so` 加载警告，但上述 Unity Test Framework 报告均为 Passed。

提交前审查修正：支撑垫采用只抬高、不降低的地面融合；坡面候选搜索将“瓦片覆盖未齐”与“完整覆盖但没有可用坡面”区分，前者在 `loadingTimeout` 内重试，后者及时安全失败。对应 EditMode 回归用例已验证。

- [x] **Step 4: 若验证通过，创建中文本地提交，不执行 push**

只暂存本次新增/修改的实现文件；原有两行用户改动不得进入本次提交。

### 已验证的定位边界

- 自动搜索在当前 `test` 场景选到合格坡脚后，履带中心 X/Z 与启动前相同（日志 `relocated=(0,0,0)`），仅将机身转向坡面并把矿车停到侧方。截图红框未提供 Cesium 世界坐标，未擅自将设备平移到一个未经验证的位置；需要固定目标时可启用 `useExplicitWorkFaceAnchor` 并填写锚点。
