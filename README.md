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

主场景默认任务目标量为 `1m³`，它表示本次实际装车增量，不表示矿车装满。当前铲斗配置上限为 `0.303886m³`；矿车按有效货箱尺寸和填充高度计算的容量为 `5.984m³`，即使每斗达到上限也至少需要 20 斗才能真正满车。实际每斗接收量通常低于斗容上限，应以车厢显示的实收量判断进度，不按“0.3m³/斗”估算演示时间。自动控制器在固定回转角度间换挖点，并对当前土层厚度调整切深；底盘自动行走和矿车满载长周期验收仍属于后续阶段。

本地 PlayMode 闭环验收记录：默认 `1m³` 任务连续完成 10 斗后进入 `Completed`，车厢实收 `1.065694m³`，与控制器累计量一致，斗内余量为零，履带底盘无位移。可用 `Assets/Tests/PlayMode/AutonomousExcavationPlayModeTests.cs` 中的 `ConfiguredOneCubicMetreJobReachesCompleted` 复跑；地图瓦片目录通过 `CESIUM_TILESET_DIR` 指向 `osgb_small_005_004`。该结果仅证明任务目标量闭环，未验证满载 `5.984m³` 的约数十斗长期连续运行。

运行时快捷键：

- `P`：启动自主任务，使用 Inspector 中的 `targetVolumeM3`；该按键独立于 `A/D` 底盘转向。
- `Esc`：人工接管，停止自动姿态和履带请求，但保留现场已产生的土量。
- `Backspace`：停止并复位运行时土区、铲斗、空中土流、车厢记录和设备姿态。

人工挖掘中也可按 `P`：如果斗内已有达到最小转运量的真实土方，自动流程先从当前实测姿态完成这斗的抬升、回转、卸料，再继续下一斗；空斗从当前姿态进入挖点。按键时尚未回中的键盘或 AZ 手柄输入不会同帧取消自动；回中后新的关节或履带输入仍会立即交还人工。自动运行期间 `Esc` 始终可以接管。接管不会清空土区、斗内或车厢土量；需要全部清场时才使用 `Backspace`。
自动挖点从人工接管时的实际回转方向附近开始；每完成一斗沿坡面相邻角度换点，遇到空挖则继续寻找，连续一轮无有效土才报告挖尽。矿车保持原停车位，卸料落点仍逐斗按真实车厢位置计算；土量仍由实际切削和车厢接收决定，不靠随机数增减。
这套装车轨迹仍以烘焙的坡面作业位为基准：人工可调整大臂、小臂、铲斗和回转姿态；在坡脚安全走廊内前后挪动履带后按 `P`，设备先收拢工作装置，再用真实履带分阶段低速回到作业位、停车锁定，然后继续这斗或下一斗。回位范围相对标定位置为前后 12 米、左右 3 米、高差 1.5 米；这只是允许尝试的边界，地形、车体碰撞和接地保护仍可能提前停车。回位期间仍可按 `Esc` 人工接管；越界、持续失去接地、碰车或超时会安全停车。远离坡脚的任意路径导航尚未实现，越界时需人工返回。每斗仅做一次切削，土量以真实切出和保留量为准；为避免硬阻力卡住，入土目标预留了收斗余量，因此不保证每斗满斗。

HMI 集成使用 `AutonomousExcavationController.StartAutonomousJob()`、`RequestManualTakeover()` 和 `ResetAutonomousJob()`；通过 `State`、`Status`、`FaultReason`、`DeliveredVolumeM3` 和 `CompletedCycles` 读取反馈。完成判定以 `SoilReceivingZone.VolumeM3` 的实际增加量为准，不以铲斗释放量代替。

当前版本暂不包含底盘自动路径规划和跨位置的多挖点调度；主场景地图未就绪、车满、空挖/挖尽、姿态或卸料超时，以及卸料净空不足都会安全停止并保留原因。`Completed` 是任务目标量达到，`TruckFull` 才表示车厢余量不足以接收下一斗。施工场编辑器将车辆放在土区右前方独立装车位，并校验土区扩大边界与车辆实体碰撞盒至少保留约 2m 水平净距。

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
