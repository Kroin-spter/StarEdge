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

主场景的 `ZX200 construction site` 已挂载 `AutonomousExcavationController`。当前交付范围是从一个已验证可达挖点开始，自动完成切入、收斗、抬升、回转、卸料、确认车厢接收并返回；目标量和车厢容量判断接口已接入，连续多斗作为后续阶段验证。

本次验收已锁定为一个可达挖点的一斗自主流程：一次启动能够完成真实切削、保土、回转、落入车厢和返回挖掘方向。多斗连续循环的状态机接口已保留，但尚未作为本次交付验收项；自动换挖点、底盘路径和多挖点调度仍属于后续阶段。

运行时快捷键：

- `A`：启动自主任务，使用 Inspector 中的 `targetVolumeM3`。
- `Esc`：人工接管，停止自动姿态和履带请求，但保留现场已产生的土量。
- `Backspace`：停止并复位运行时土区、铲斗、空中土流、车厢记录和设备姿态。

HMI 集成使用 `AutonomousExcavationController.StartAutonomousJob()`、`RequestManualTakeover()` 和 `ResetAutonomousJob()`；通过 `State`、`Status`、`FaultReason`、`DeliveredVolumeM3` 和 `CompletedCycles` 读取反馈。完成判定以 `SoilReceivingZone.VolumeM3` 的实际增加量为准，不以铲斗释放量代替。

当前版本暂不包含底盘自动路径规划和多个挖点换点；主场景地图未就绪、车满、空挖/挖尽、姿态或卸料超时都会安全停止并保留原因。多斗连续作业需要在下一阶段完成真实动力学回归后再开放。

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
