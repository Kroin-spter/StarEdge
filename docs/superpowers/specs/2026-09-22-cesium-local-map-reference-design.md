# Cesium 本地地图引用设计

## 目标

让 `Assets/Scenes/sence_test.unity` 在当前工作站的 Unity 编辑器和运行时优先引用项目同级目录中的本地 Cesium 3D Tiles 数据：

```text
../tilesets/osgb_small_005_004/tileset.json
```

地图数据仍然不进入 Git。其他机器可以通过相同的同级目录约定，或通过 `CESIUM_TILESET_DIR` 环境变量指定地图目录。

## 现状与问题

- 本地地图已存在于 `/home/sk/Unity/tilesets/osgb_small_005_004/tileset.json`。
- 主场景包含两个 Cesium Tileset，其中用于施工区域的激活 Tileset 当前使用 Cesium Ion 数据源，场景序列化 URL 为空。
- `CesiumMoundSite` 已经在运行时解析本地地图，但 Unity 编辑器打开场景时不会执行这段运行时初始化，因此编辑器预览可能没有本地地形。
- 直接把 `/home/sk/...` 绝对路径写入场景会破坏仓库在其他机器上的可移植性。

## 方案

新增一个仅负责本地地图引用的编辑器/运行时桥接组件，挂载在施工区域的 Cesium Tileset 上：

1. 统一解析顺序：`CESIUM_TILESET_DIR` → 项目同级 `../tilesets/<mapName>`。
2. 在编辑器中使用 `AssetDatabase`/场景序列化兼容的方式更新 Tileset 的 URL 和数据源，使场景可预览本地地图，但不保存机器绝对路径。
3. 在运行时继续由 `CesiumMoundSite` 设置本地 `file://` URL，WebGL 仍使用 `maps/<mapName>/tileset.json` 的构建输出路径。
4. 本地文件不存在时保持现有安全行为：记录明确警告，项目仍可打开，挖掘机等待地图碰撞就绪，不创建重复的替代地图碰撞。
5. 不移动或复制地图目录，不修改 `.gitignore` 的排除策略。

## 数据流

```text
环境变量/同级 tilesets
        ↓
本地地图路径解析器
        ↓
Cesium Tileset（FromUrl + tileset.json）
        ↓
Cesium 碰撞网格
        ↓
CesiumMoundSite / CesiumTerrainConformer
        ↓
局部土层、挖掘机和车厢物理
```

## 错误处理

- 路径为空或 `tileset.json` 不存在：输出包含实际查找路径的警告。
- Cesium Tileset 不可用：保持现有 `IsReady == false`，自主控制器进入安全等待/超时状态。
- 场景中存在备用 Ion Tileset：保持禁用，避免原地图和本地地图同时产生重复渲染或碰撞。
- WebGL：不读取本机文件路径，只使用 WebGL 输出目录中的 `maps/<mapName>/tileset.json`。

## 验证范围

- 检查主场景施工区域 Tileset 使用 `FromUrl`，并能解析本地 `tileset.json`。
- 检查缺少地图时项目仍能打开，且不产生未处理异常。
- 检查场景中只有一个施工地图 Tileset 参与碰撞。
- 运行现有 EditMode 测试，并在 Unity 可用时运行主场景 Cesium PlayMode 测试。

