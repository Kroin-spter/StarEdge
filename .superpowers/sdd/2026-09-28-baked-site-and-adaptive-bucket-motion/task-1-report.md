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
- 离线重复启动：将 `CESIUM_TILESET_DIR` 指向不存在的目录，`/tmp/task1-startup-green5.xml` **1/1 Passed**，内部完成 **5 次**加载/卸载。启动时间 **0.291、0.063、0.063、0.065、0.070 s**；逐点比较整个运行高度图与 holes 碰撞孔洞图、复位后高度图及地形尺寸、图层/树原型元数据；每次 Play 前后还对烘焙资源和 TerrainData 子资源文件做 SHA-256 比较。复核期间另用 `/tmp/task1-holes-red.xml` **1/1 Passed** 注入一次运行时孔洞变化，确认完整性断言能捕获并在 `finally` 中恢复后继续通过。
- 场景布局与引用：`/tmp/task1-layout-baked2.xml` **1/1 Passed**，验证 `sence_test` 的烘焙引用、下方工作面设备布局和裁剪组件。测试与编译在隔离工程/cache 完成，未占用原工程 GUI Editor。
- Review 修复 TDD：旧净体积判定下，`/tmp/task1-review-red.xml` 的“切土+回填净量归零仍禁止烘焙”和“无 Terrain 材质拒绝”两项测试按预期失败；修复后 `/tmp/task1-review-green.xml` **5/5 Passed**，覆盖以上用例、既有资源校验和运行时切削复位隔离。
- 启动测试不再修改 `LogAssert.ignoreFailingMessages`。只登记精确已知日志：Cesium 本地瓦片缺失提示，以及场景原有的三个无效树预制体引用和 ZX200 凸包回退；其他未登记的测试日志仍导致失败。测试 XML 与 Editor log 保存在 `/tmp/task1-startup-green5.xml`、`/tmp/task1-startup-green5.log`。
- 提交前执行 `git diff --check` 与暂存区差异检查；不将测试生成的 `.vscode/settings.json`、`InitTestScene*.unity` 或 crash blob 纳入提交。

## 仍需现场目视确认的边界

- `ConstructionSiteMapClip` 的编辑器预览代码已加入并通过编译/场景引用测试，但**未在 GUI Editor 目视确认**瓦片到齐后的裁剪效果及碰撞边界；这项需在原工程打开 `sence_test` 实地检查。不能把 EditMode 测试当作视觉验收。
- 此次无头烘焙保留现有土层 TerrainLayer，未从 Cesium 影像烘焙新纹理；局部土层的外观衔接仍需 GUI 目视验收。背景 Cesium 地图在可用时仍按原组件加载；离线测试只证明施工区自身可就绪。
- Unity Editor 启动日志仍有 Assimp `libdl.so` 缺失提示；它发生在测试场景加载前，属于已有 Linux URDF 导入器环境问题。本轮没有运行完整自动挖装验收，也没有验证所有项目测试。
- 当前 TerrainData 的 `GetAlphamaps()` 在该烘焙资源上会产生 Unity 错误 `splatdatabase alphamap 0 is null`，因此测试没有调用该 API；改为比较完整源资源文件哈希、heightmap 全采样、尺寸、TerrainLayer 引用、alphamap 元数据及树原型/实例。运行时重置也逐点比较完整高度图。土层纹理图权重本轮仍需在 GUI Editor 目视确认。

## 重新烘焙操作

需要更新坡面时，在可访问本地瓦片的 Editor 中临时将场地模式设为 `DynamicDeveloper`，待现场扫描就绪后于 Play 中执行 `Tools/Excavator/Construction Site/Bake Ready Dynamic Site`；退出 Play 后选中新资产并执行 `Apply Selected Baked Site` 与 `Validate Selected Baked Site`。确认场景重新处于 `Baked` 模式，保存场景，再做 GUI 预览/碰撞检查。提交作者名为 `shenkai`，远端推送由用户执行。
