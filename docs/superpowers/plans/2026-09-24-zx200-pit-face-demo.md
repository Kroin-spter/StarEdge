# ZX200 低位坑底台阶推进演示实施计划

> **执行方式：** 本会话内逐项测试并实施；保留工作区已有未提交改动。

**目标：** 将 `sence_test` 改成挖机位于低位坑底、面对高位待挖工作面并沿台阶线推进的可见装车演示。

**架构：** 在施工范围内用现有 `ConstructionSiteMapClip` 同步裁掉 Cesium 地图渲染和碰撞三角形，再由一个运行时 TerrainCollider 提供上下平台、坡面和可切削窄长土带。自主控制仍以真实高度图移除量和车厢接收量为准；切削完成后用最低安全高度回转，底盘每斗保持固定。

**技术栈：** Unity 6、C#、Cesium for Unity、Terrain/TerrainCollider、Unity Test Framework。

## 全局约束

- 不移动或覆盖本任务开始前已存在的未提交改动。
- 地图裁切范围必须与替代 Terrain 的水平范围一致；范围内仅保留一个有效地面碰撞体。
- 斗内和车厢土量只能来自高度图实际切削、土流和接收区实际落地。
- 施工演示中履带底盘固定；抬升、回转和复位按单轴姿态反馈执行，切削时允许斗杆与铲斗协同收斗并明确报告该组合动作。
- Git 提交说明用中文、作者名 `shenkai`；不推送远端。

---

### 任务 1：为台阶剖面与土带装填写回归测试

**文件：**
- 修改：`Assets/Tests/EditMode/MoundFloorTests.cs`
- 修改：`Assets/Tests/EditMode/ConstructionSiteSetupTests.cs`
- 修改：`Assets/Tests/PlayMode/MoundSiteTests.cs`
- 修改：`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [x] 测试低平台、过渡坡面、高平台的高度顺序及窄长工作面方向。
- [x] 测试场景准备后挖机与卡车在低平台、前方平台高差达到配置目标，裁切网格与替代地面碰撞同步。
- [x] 测试自动切削不会在仅达到 `minimumLoadVolumeM3` 时主动停斗，且斗内土量是实际切削结果。
- [x] 测试每斗状态顺序、卸料净空和车厢实收量；任何一项失败先保留失败证据。

### 任务 2：生成低坑底、推进台阶和局部 Cesium 替代地形

**文件：**
- 修改：`Assets/WebDelivery/Runtime/CesiumMoundSite.cs`
- 修改：`Assets/Robotics/zx200_description/Editor/Zx200ConstructionSiteSetup.cs`
- 修改：`Assets/Tests/PlayMode/MoundSiteTests.cs`

- [x] 在 Cesium 地图采样完成后，建立覆盖设备、工作面和卡车的局部 Terrain 高度图；低平台位于挖机脚下，坡面和高平台位于车头前方。
- [x] 让工作面横向延展、纵向保持窄带，外缘回接采样地图高程。
- [x] 以同一范围配置 `ConstructionSiteMapClip`，精确拆分跨界三角形并同步裁切渲染和 MeshCollider；TerrainCollider 是范围内唯一地面，范围外边界支撑连续。
- [x] 将挖机和卡车放到低平台，保持设备正对工作面；调整相机使剖面与装车关系可见。

### 任务 3：加大真实单斗切削量并降低转运高度

**文件：**
- 修改：`Assets/Scripts/Autonomy/AutonomousExcavationController.cs`
- 修改：`Assets/Scenes/sence_test.unity`
- 修改：`Assets/Tests/EditMode/AutonomousExcavationControllerTests.cs`
- 修改：`Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs`

- [x] 收斗动作继续到配置装填比例或完整有效行程结束；`minimumLoadVolumeM3` 只用于判定空挖，不作为提前停斗阈值。
- [x] 满斗后取消固定高位 `liftPose` 跳转，按车厢侧板净空自适应抬升；将额外净空余量收敛到小的安全值，并继续保留实体碰撞检查。
- [x] 履带底盘保持固定；普通抬升、回转和复位采用单轴动作段；实际切削以斗杆与铲斗协同收斗，PlayMode 检查两者均运动且状态反馈准确。

### 任务 4：运行验证、回看场景与整理本次差异

- [x] 运行相关 EditMode 与 PlayMode 测试，并检查失败日志。
- [x] 在 Unity PlayMode 加载 `Assets/Scenes/sence_test.unity`，确认 3 斗自动装车完成、履带位移 0.000 m、车厢实收约 0.38 m³、铲斗清空并返回；另验证车厢穿透急停与卸料姿态复位。
- [x] 检查 `git diff --check` 和完整差异；不纳入 `Library/`、`Temp/` 等缓存。
- [x] 所有验证成功后创建本次中文本地提交；不执行 push。

## 验证记录

- PlayMode：`AutonomousExcavationPlayModeTests` 5/5；最新整套回归三斗实收 0.379402 m³、履带位移 0.000 m；`MoundSiteTests` 1/1，直接检查真实 MeshFilter、裁后地表三角形和碰撞网格，且工作区四边外 0.05 m 均保持地图碰撞。
- EditMode：`ConstructionSiteClipTests` 5/5（边界面积/射线、两子网格、Quad+Line 混合拓扑、法线/切线/颜色/UV、全覆盖 tile 清理）；`AutonomousExcavationControllerTests` 24/24；`ConstructionSiteSetupTests` 4/4；`SmallDumpTruckTests` 1/1。
- 全量 EditMode 扫描曾有 4 项失败；其中车厢侧壁净空测试的 BoxCollider 缩放计算已修正并在专项测试通过。另 3 项失败位于土流侧障碍测试、HR35 材质导入测试和当前环境的 Linux Player 构建。
- 当前主施工场固定履带底盘；通用 ZX200 prefab 的移动模式保持不变。
- 复位中断卸料后恢复卸料器原启用状态，并验证复位后的下一轮自主装车成功。
- 另在土流已离斗、空中有土时复位，验证空中土、待沉积量、车厢账本和地形移除量清零，并保持卸料器原启用状态。
- Cesium 边界裁切按四条矩形边依次分割三角形，只移除施工区内的片段；Quad 面先三角化，线/点子网格保留渲染且不送入 MeshCollider。测试平面 12×12 m、工作区 10×10 m，保留外部面积 44 m²，工作区内无地图射线命中，四周外 0.05 m 有连续碰撞。
- 测试场景卸载时 Cesium 原生 TilesetContentManager 会记录访问已销毁 Mesh 的错误；该日志在本次几何修复前的旧回归日志中也存在，属于既有 tile 卸载清理行为，本次未改其生命周期。
