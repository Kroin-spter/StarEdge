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

主场景 `Assets/Scenes/sence_test.unity` 已挂载 `CesiumLocalMapReference`，打开场景时会自动选择上述本地地图并设置 Cesium 的 `From URL` 引用。地图数据不随源码提交；缺少地图时，Unity 项目仍可打开和编译，但主场景会提示 Cesium 地图缺失，挖掘机将保持安全等待状态。

## 当前场景模式

当前主场景使用 `CesiumMoundSite`：在原 Cesium 地图地面上生成局部可切削土 mound，并保留原地图碰撞；不是旧的施工区域裁剪方案。主场景包含挖掘机、铲斗土量、车厢接收和卸料相关运行组件。

## 自主挖掘装车

主场景的 `ZX200 construction site` 已挂载 `AutonomousExcavationController`。当前流程从一个已验证可达挖点开始，自动完成切入、收斗、抬升、保持高位回转、卸料、确认车厢接收、返回并继续下一斗；目标量和车厢容量判断按真实接收量执行。

当前验收目标为同一可达挖点的连续多斗自主装车：默认目标量 `1m³`，按当前约 `0.3m³/斗` 应连续完成约 4 斗，并在每斗卸料前检查车厢接收区和铲斗高位净空。自动换挖点、底盘路径和多挖点调度仍属于后续阶段。

运行时快捷键：

- `P`：启动自主任务，使用 Inspector 中的 `targetVolumeM3`；该按键独立于 `A/D` 底盘转向。
- `Esc`：人工接管，停止自动姿态和履带请求，但保留现场已产生的土量。
- `Backspace`：停止并复位运行时土区、铲斗、空中土流、车厢记录和设备姿态。

HMI 集成使用 `AutonomousExcavationController.StartAutonomousJob()`、`RequestManualTakeover()` 和 `ResetAutonomousJob()`；通过 `State`、`Status`、`FaultReason`、`DeliveredVolumeM3` 和 `CompletedCycles` 读取反馈。完成判定以 `SoilReceivingZone.VolumeM3` 的实际增加量为准，不以铲斗释放量代替。

当前版本暂不包含底盘自动路径规划和多个挖点换点；主场景地图未就绪、车满、空挖/挖尽、姿态或卸料超时，以及卸料净空不足都会安全停止并保留原因。施工场编辑器将车辆放在土区右前方独立装车位，并校验土区扩大边界与车辆实体碰撞盒至少保留约 2m 水平净距。

## Git 提交边界

源码提交包含 `Assets/`、`Packages/`、`ProjectSettings/`、工具脚本和文档。Unity 缓存、用户配置、自动生成的解决方案文件、WebGL 构建目录以及未被主场景引用的大型示例资源由 `.gitignore` 排除；这些目录不会被移动或删除，只是不进入源码仓库。

### Git 提交规范

- 提交者名称统一使用 `shenkai`。
- 提交说明的类型前缀可以保留英文，例如 `feat:`、`fix:`、`docs:`、`test:` 和 `chore:`。
- 类型前缀后面的具体说明必须使用中文。
- 示例：`feat: 完善自主挖掘流程`。

## WebGL 构建

Cesium WebGL 构建入口为 Unity 菜单：

```text
Tools > Excavator > Build Cesium Web Demo
```

构建前需要准备 Cesium 地图，并确保 `CESIUM_TILESET_DIR` 指向包含 `tileset.json` 的目录。构建脚本会将地图复制到 WebGL 输出目录的 `maps/osgb_small_005_004/` 下。
