# HMI_Unity6

Unity 6 挖掘机施工区域、土方切削和装车验证项目。

## 开发环境

- Unity `6000.5.10f1`
- Cesium for Unity `1.25.1`
- 渲染管线：URP
- 默认场景：`Assets/Scenes/sence_test.unity`

## 启动项目

1. 使用 Unity Hub 安装并打开 Unity `6000.5.10f1`。
2. 在 Unity Hub 中添加本目录并打开项目。
3. 等待 Package Manager 完成依赖解析和首次资源导入；`Library/` 不在 Git 中，会由 Unity 自动生成。
4. 打开 `Assets/Scenes/sence_test.unity`，点击 Play。

`ProjectSettings/EditorBuildSettings.asset` 已将 `Assets/Scenes/sence_test.unity` 设置为启用的默认场景。

## Cesium 地图数据

主场景需要外部地图资源：

```text
osgb_small_005_004/tileset.json
```

默认查找位置是当前项目的同级目录：

```text
<Unity目录>/tilesets/osgb_small_005_004/tileset.json
<Unity目录>/HMI_Unity6/       # 本项目
```

也可以通过环境变量指定地图目录：

```bash
export CESIUM_TILESET_DIR=/absolute/path/to/osgb_small_005_004
```

地图数据不随源码提交。缺少地图时，Unity 项目仍可打开和编译，但主场景会提示 Cesium 地图缺失，挖掘机将保持安全等待状态。

## 当前场景模式

当前主场景使用 `CesiumMoundSite`：在原 Cesium 地图地面上生成局部可切削土 mound，并保留原地图碰撞；不是旧的施工区域裁剪方案。主场景包含挖掘机、铲斗土量、车厢接收和卸料相关运行组件。

## Git 提交边界

源码提交包含 `Assets/`、`Packages/`、`ProjectSettings/`、工具脚本和文档。Unity 缓存、用户配置、自动生成的解决方案文件、WebGL 构建目录以及未被主场景引用的大型示例资源由 `.gitignore` 排除；这些目录不会被移动或删除，只是不进入源码仓库。

## WebGL 构建

Cesium WebGL 构建入口为 Unity 菜单：

```text
Tools > Excavator > Build Cesium Web Demo
```

构建前需要准备 Cesium 地图，并确保 `CESIUM_TILESET_DIR` 指向包含 `tileset.json` 的目录。构建脚本会将地图复制到 WebGL 输出目录的 `maps/osgb_small_005_004/` 下。
