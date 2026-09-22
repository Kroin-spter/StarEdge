# 配重与切削验证

2026-09-15，Unity 6000.5.10f1，隔离工程验证。项目测试通过：80 项 EditMode、16 项 PlayMode。

PlayMode 命令使用 `-runTests -testPlatform PlayMode -testFilter ExcavatorControls.Tests`。
EditMode 命令使用 `-runTests -testPlatform EditMode -testFilter 'ExcavatorControls.Tests;UrdfUrpImporter.Tests.Zx200PrefabSetupIntegrationTests'`，包括本项目的五项预制体集成测试。
不要在工作工程无过滤运行第三方 URDFImporter 自带测试，其部分测试使用并清理固定的 Assets/Tests 路径。

本次一次未过滤的隔离运行受上述第三方清理影响而中断；已从备份恢复并重建本次回归用例，最终两个 XML 为恢复后限定项目范围的完整成功结果。主工程未运行该清理测试。

PlayMode 覆盖动态底盘、四关节、铲斗碰撞、驻车稳定性、真实物理刃口切削和交付场景。真实铲斗试切体积约 0.18796 m³；交付场景启动斗量为零，手动降臂试切约 0.02826 m³。两者均检查地形积分与斗量一致。

当前测试不包含真实土体阻力、土载荷、卸料装车或自主作业，未执行发布构建。
