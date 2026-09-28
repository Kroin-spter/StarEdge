# Task 2 报告：紧凑停放姿态与切削准备分离

状态：实现与定向验证完成，待本地提交。场景：`Assets/Scenes/sence_test.unity`。没有推送。

## 实施内容

- 新增 `parkedPose=(swing 0°, boom -42°, arm 88°, bucket 42°, duration 0.8s)`；现有 `workReadyPose` 与切削轨迹保持独立，场景的 `workReadyPose.boom=-29°` 覆盖也保留。
- `Awake` 只在首次初始化时用停放姿态设置 drive target 和 ArticulationBody 初始广义坐标；删除 `Start` 中再次将设备硬切到工作准备姿态的逻辑。
- 将实测稳定后的动臂、斗杆和铲斗局部旋转写入 ZX200 prefab，并同步 `sence_test` 铲斗旋转覆盖；场景初始目标与序列化模型姿态一致，避免旧姿态启动时甩臂。
- 保留 `ApplyWorkReadyPose()` 供键盘 Alpha1 使用。自主 `AcquirePoint` 仍须先到 `workReadyPose`，`BucketSoilCutter.StartupReady` 不会被停放姿态绕过。
- `ResetRoutine` 的最终姿态改为 `parkedPose`；自动控制结束后恢复用户设定的可移动底盘模式。

## 验证（Unity 6000.5.10f1，2026-09-29）

- EditMode：`/tmp/task2-park-green-edit.xml`，ZX200 控制器定向测试 **37/37 Passed**。
- PlayMode：`/tmp/task2-startup-final.xml`，启动稳定性 **3/3 Passed**。包括停放→工作准备过渡、最大手动反向稳定性和首次物理帧；首次 150 个固定物理帧内斗尖最大移动 **0.0941 m**，底盘最大倾角 **0.0523°**、最大角速度 **0.021 rad/s**。停放斗壳/斗齿碰撞体离平地测试面超过 **0.4 m**。
- 场景 PlayMode：`/tmp/task2-scene-final.xml`，离线重复启动 **5/5 Passed**。本地施工区独立于 Cesium 网络可就绪，暖启动就绪 **0.047–0.055 s**；首轮从进入场景到就绪 **0.130 s**（场景载入另计 **0.116 s**）。对实际斜坡 TerrainCollider 的 8 个刃口角点采样，最近净空 **2.072 m**。
- 同一场景测试从停放姿态启动自主任务，抵达工作准备姿态且切削器就绪；人工接管后执行复位，设备回到停放姿态 3°以内，底盘保持解锁。
- Terrain 完整性测试也包含在上述场景验证中：逐点比较运行 Terrain 高度图和 holes 图；刻意改动一个 runtime hole 会触发断言，随后测试恢复数据。

## 尚未完成的验证

- 以上是 Batchmode 的物理、场景与碰撞验证，未通过 GUI Editor 目视确认非 Play Scene 视图的模型姿态、土带/背景裁剪外观。
- 单独的“完整卸料中复位并重新挖装”长 PlayMode 测试在 Batchmode 运行 6 分钟未产生 XML 结果，已中断，不能记为通过。窄场景验证已覆盖停放启动、工作准备、接管和复位，但不替代多斗完整流程验收。
- 由于底盘复位后解除固定，物理姿态存在小幅重力沉降；测试用 3°作为复位后停放验收容差。切削器准备就绪仍使用其既有更严格的关节容差。
