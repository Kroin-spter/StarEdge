# ZX200 自主挖掘装车闭环设计

## 目标

在 `Assets/Scenes/sence_test.unity` 的现有 Cesium 施工区域内，实现无需逐斗人工输入的一斗自主挖掘装车，并支持连续多斗循环。第一版只使用当前已验证的一个可达挖点和 `ParkedDumpTruck` 接收区，不在本次范围内引入底盘路径规划或多个挖点导航。

验收必须同时满足：

1. 运行中点击一次“开始自主作业”后，设备能够自动完成切入、收斗、抬升、回转、卸料和返回。
2. 切削量只能来自 `BucketSoilCutter`，卸料必须经过 `BucketSoilUnloader`/`SoilFlightTransport`，车厢只记录实际落地量；不允许用状态机直接增加土量。
3. 一斗完成后可自动开始下一斗，直到达到目标量、车厢容量、土区没有可切削土，或发生异常。
4. 运行状态可被 HMI 读取，至少能区分运行中、完成、车满、挖尽、空挖、关节超时、卸料超时、人工接管和复位。
5. 按下人工控制或调用复位时，自动流程安全停止；复位后机器回到工作姿态，土区、铲斗和车厢的累计量保持守恒并可重新开始。

## 当前基础与缺口

现有工程已经具备 `CesiumMoundSite` 的地图支撑和局部土丘、`Zx200DigController` 的关节目标姿态、`BucketSoilCutter` 的刃口扫掠切削、`BucketSoilResistance` 的阻力约束、`BucketSoilUnloader` 的保土/卸土、`SoilFlightTransport` 的空中土流，以及 `SoilReceivingZone` 的接收和计量。

现有 `Zx200DigController` 的 Space 循环只负责按固定姿态播放动作，不等待有效进斗量、车厢实际接收或空挖/车满结果。因此自主功能需要把姿态执行与土量判定放入一个可中止的状态机，而不是继续扩展一个只播放姿态的协程。

## 架构

新增 `AutonomousExcavationController`，挂载在 `ZX200 construction site`，通过序列化引用连接：

- `Zx200DigController`：发送姿态目标并等待实际关节到位。
- `BucketSoilCutter`：读取斗内体积、有效切削量、斗容量、土区剩余变化和累计守恒数据。
- `BucketSoilUnloader`：读取保土、卸料和卸料异常状态。
- `SoilReceivingZone`：读取车厢容量、已接收量和剩余容量。
- `CesiumMoundSite`：等待地图支撑和土丘准备完成；未准备完成时禁止自动动作。

`Zx200DigController` 只补充可复用的“自动姿态阶段”接口和安全取消接口，保留现有手动键盘控制和 Space 兼容入口。自动控制器拥有流程状态，不直接写关节物理位置，也不直接修改任何土量属性。

## 状态流

```text
Idle
  -> WaitingForSite
  -> AcquirePoint
  -> LowerAndEnter
  -> FillBucket
  -> Lift
  -> SwingToTruck
  -> Dump
  -> ConfirmDump
  -> ReturnToDig
  -> FillBucket (下一斗)

任意运行状态 -> ManualTakeover / Fault / Resetting
FillBucket -> TargetReached / TruckFull / SoilExhausted
ConfirmDump -> DumpTimeout / Fault
ReturnToDig -> Complete (目标量、车满或挖尽)
```

每个循环使用现有标定姿态：`approachPose`、`curlPose`、`liftPose`、`swingToDumpPose`、`dumpPreparePose`、`dumpPose`、`returnSwingPose`。进入 `FillBucket` 后必须观察 `VolumeM3` 在超时窗口内增加；没有有效进斗量时进入 `EmptyCut`，不得假报成功。卸料阶段等待斗内体积下降且接收区 `VolumeM3` 增加，或确认空斗且空中/待沉积土量归零。

第一版的 `AcquirePoint` 不驱动底盘，而是验证当前刃口与已配置土区可达，并要求机器处于工作姿态、接收区有效且有剩余容量。这符合当前主场景的固定挖点验收边界；多挖点和自动行走留到下一阶段。

## 数据和停止条件

控制器维护本次任务的起始车厢量、目标量、已完成量、循环数、当前状态、错误原因和超时计时。完成量以 `SoilReceivingZone.VolumeM3` 的实际增加量为准，不能以铲斗释放量代替。

正常停止条件按优先级处理：

1. 达到 `targetVolumeM3`：完成任务。
2. 接收区 `AvailableM3` 小于最小有效装载量：车满停止。
3. 土区切削连续超时且 `BucketSoilCutter` 没有有效切削：挖尽或空挖；由土区剩余/可切削探测结果区分。

异常停止条件包括姿态到位超时、切削超时、卸料超时、接收区无效、地图未就绪、有限数值异常、动作被人工接管。所有异常都保留 `FaultReason`，停止自动关节请求并回到安全姿态或等待人工处理，不继续盲目循环。

## 手动接管与复位

自动运行期间检测键盘关节输入、底盘输入或 HMI 控制输入时，控制器调用安全停止，取消当前阶段并标记 `ManualTakeover`。手动接管不清空斗内土，也不回滚已挖土量；操作者可以手动卸料或调用复位。

复位流程先停止自动状态机和底盘驱动，再调用 `BucketSoilCutter.ResetExcavation()`，清除地形运行副本、斗量、空中土流和该土源在车厢中的记录，最后将机器送回工作姿态并等待 Cesium 施工区重新 ready。只有所有引用有效、地图 ready、斗内为空且接收区有容量时允许重新开始。

## 测试与验收

增加 EditMode 测试覆盖状态判定、停止优先级、有限输入和故障转移；增加 PlayMode 主场景测试覆盖：

- 一次启动完成至少一斗实际切削和实际车厢接收。
- 连续两斗后车厢体积等于实际落地量，铲斗、空中土流和地形满足守恒。
- 目标量、车满、空挖/挖尽、动作超时和人工接管均能停止。
- 复位后地形、斗量、车厢记录回到初始状态。
- Unity 6 主场景批处理启动成功，现有已知基线测试失败单独记录，不用“编译成功”替代自主流程验收。

## Git 提交边界

先提交本设计文档；实施阶段至少拆成以下提交，便于回退和人工审查：

1. `feat: add autonomous excavation state machine`
2. `test: verify autonomous excavation cycle`
3. `docs: record autonomous excavation verification`（仅在验证结果和当前已知限制整理完成后提交）

不提交 `Library/`、`Temp/`、`Logs/`、外部 Cesium tileset、Unity 生成工程文件或本机绝对路径；不在本次工作中代替用户执行远程 push。
