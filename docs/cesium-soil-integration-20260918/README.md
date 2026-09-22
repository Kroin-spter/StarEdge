# Cesium 与可挖土层融合

## 后续修正与视觉融合

新增 `ConstructionSiteSurfaceAppearance`：在裁剪前从正上方采集当前 Cesium 地表图像，投影到施工平台，并作为第二个 Terrain 图层在土区外圈渐变混合。原来的灰色平台外观被替换；灰色土层底板只隐藏渲染，碰撞仍保留。中央土层保留现有高度与土壤材质，外围使用较宽、略不规则的材质过渡。

修复上一版初始化问题：必须先克隆 Terrain 再采样；准备后的土面同时成为复位和计量基准；采样超时恢复原裁剪；已发生挖掘后不会再修改土面；需要完整采样覆盖；过渡距离按实际米数计算，土面不低于土层底部。

截图与最新测试结果见本目录 `conform-appearance-*` 文件及 `surface-overview.png`。运行主场景时会自动执行，无需重新生成施工区。若地图采集不完整，会保留原外观。

限制：这是表面融合，平台仍是原有平面，外缘地形高差并未完整重建；中央土层仍可辨认为准备好的施工土区。没有解决所有 Cesium 瓦片 Mesh 生命周期报错，也未重新构建或验证 Web。

最终记录：135 项 EditMode 通过；视觉版主场景切入、移动后挖土、装车、回填共 4 项通过。评审补充了不完整贴图的限时重试，以及移除外观组件时恢复原图层；补充修正后的切入和装车 2 项通过，包含组件移除回归。验证在隔离项目完成，交付文件已逐一校验。备份位于 `/home/sk/Unity/surface-integration-backup-20260918`。

以下保留首版记录；最新行为以上述修正为准。

本轮加入 `CesiumTerrainConformer`。运行时它等待 Cesium Tileset 的 MeshCollider，读取施工区边界采样点，并把局部可挖 Terrain 的外圈过渡带贴合到 Cesium 地表高度。Terrain 中央挖掘核心保留原有标定高度，避免远端地图高程偏移改变斗齿切入深度。

`ConstructionSiteMapClip` 会在采样期间暂缓裁剪，采样成功后再裁剪施工区内 Cesium 三角形；局部 TerrainCollider 继续承担施工区碰撞。Cesium 采样不足或超时会保留原有 Terrain，不阻塞挖掘。

这是一种运行时融合，不改写 Cesium 3D Tiles 或 USD 源文件。当前局部 Terrain 仍是唯一可挖高度图，不能表达洞穴、悬挑和颗粒坍塌。Cesium 瓦片异步更新期间仍需观察 Mesh 生命周期日志。

验证：主场景真实竖直切入测试通过，`cesium-conform-play2.xml` 为 1/1 通过，切出 0.008332 m3，进斗 0.007416 m3，侧落 0.000916 m3。编辑器测试 `ConstructionSiteClipTests` 在组件修改前通过 1/1；新增脚本只产生 Unity 6 的弃用 API 警告，不产生编译错误。
