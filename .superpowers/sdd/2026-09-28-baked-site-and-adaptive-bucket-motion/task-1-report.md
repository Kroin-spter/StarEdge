# Task 1 报告：固定可重复的施工区

状态：**DONE（限 Task 1 范围）**。交付场景为 `Assets/Scenes/sence_test.unity`；已在隔离 Unity 工程中以本地真实 Cesium 瓦片烘焙施工区、保存资产、应用到场景，并验证脱离瓦片路径后的重复启动。未实施 Task 2+ 的挖掘动作修改。

## 交付内容

- 真实地图输入为 `osgb_small_005_004` 本地 tileset，运行测试时通过 `CESIUM_TILESET_DIR` 指向工程外本机目录；工程资产及场景中不保存机器绝对路径。烘焙资产为 `Assets/ConstructionSite/Baked/SenceTest/ConstructionSite.asset`（GUID `203be0d0e456a44309b8f7f6812e0c4a`），实测初始可切土量 **33.268 m³**。场景保持 `Baked` 模式并引用该资产，不会在每次 Play 时等待动态采样。
- 烘焙数据包含地形顶面、基础地面、土层掩码、材质、地图标识及地理参考、设备和车厢布局、初始体积。捕获时依据 Terrain 实际量化后的高度保存基础地面；无土层位置令基础地面等于顶面，土层位置仅在一个量化步长量级内收敛，随后由保存数组重新积分土量。读取时检查尺寸、地图和地理参考、顶面/基础地面关系、掩码与土量；资源缺失或损坏明确失败，不隐式回退动态扫描。
- `CesiumMoundSite` 保留显式 `DynamicDeveloper` 模式供重新采样。编辑器烘焙器提供就绪后捕获、退出 Play 后应用、校验等操作；`ConstructionSiteMapClip` 对烘焙替换范围提供运行时衔接和编辑器预览裁剪逻辑。
- 运行时根据烘焙数据新建独立 TerrainData，再由现有土层逻辑切削/复位；不会把运行时土层写回烘焙源。复位恢复初始土层。

## 验证证据（2026-09-28）

- TDD 红灯：`/tmp/task1-red.log` 在新增数据类型前报 `CS0246 BakedConstructionSiteData`；实现后，`/tmp/task1-editmode-rebuild.xml` 的 3 个定向 EditMode 测试 **3/3 Passed**，覆盖损坏数据/错地图、缺资源不回退、运行时切削与复位不污染源数据。
- 真实本地 Cesium 烘焙：`/tmp/task1-local-bake4.xml` **1/1 Passed**；`/tmp/task1-local-bake4.log` 记录资产路径和 **33.268 m³**。场景随后应用了该资产，并保持 `Baked` 模式。
- 离线重复启动：将 `CESIUM_TILESET_DIR` 指向不存在的目录，`/tmp/task1-offline-five5.xml` **1/1 Passed**，测试内部实际完成 **5 次**施工区加载与运行时地形副本检查；`/tmp/task1-offline-five5.log` 分别记录 **0.288、0.054、0.053、0.056、0.056 s**，均报告本地施工区就绪。测试包含挖改/复位检查；这些结果只证明本地施工区链路，不代表整机自动挖装流程已通过。
- 场景布局与引用：`/tmp/task1-layout-baked2.xml` **1/1 Passed**，验证 `sence_test` 的烘焙引用、下方工作面设备布局和裁剪组件。测试与编译在隔离工程/cache 完成，未占用原工程 GUI Editor。
- 提交前执行 `git diff --check` 与暂存区差异检查；不将测试生成的 `.vscode/settings.json`、`InitTestScene*.unity` 或 crash blob 纳入提交。

## 仍需现场目视确认的边界

- `ConstructionSiteMapClip` 的编辑器预览代码已加入并通过编译/场景引用测试，但**未在 GUI Editor 目视确认**瓦片到齐后的裁剪效果及碰撞边界；这项需在原工程打开 `sence_test` 实地检查。不能把 EditMode 测试当作视觉验收。
- 此次无头烘焙保留现有土层 TerrainLayer，未从 Cesium 影像烘焙新纹理；局部土层的外观衔接仍需 GUI 目视验收。背景 Cesium 地图在可用时仍按原组件加载；离线测试只证明施工区自身可就绪。
- Unity 日志中还存在原场景树 prefab 缺失提示及 Assimp `libdl.so` 提示，不属于本次烘焙链路的验收条件。本轮没有运行完整自动挖装验收，也没有验证所有项目测试。
- 测试验证了运行时源地形与副本隔离及复位，没有对退出 Play 前后的整个资产文件做独立哈希对照；如需文件级不变性审计，可在 GUI 作业前后对烘焙资产执行哈希比对。

## 重新烘焙操作

需要更新坡面时，在可访问本地瓦片的 Editor 中临时将场地模式设为 `DynamicDeveloper`，待现场扫描就绪后于 Play 中执行 `Tools/Excavator/Construction Site/Bake Ready Dynamic Site`；退出 Play 后选中新资产并执行 `Apply Selected Baked Site` 与 `Validate Selected Baked Site`。确认场景重新处于 `Baked` 模式，保存场景，再做 GUI 预览/碰撞检查。提交作者名为 `shenkai`，远端推送由用户执行。
