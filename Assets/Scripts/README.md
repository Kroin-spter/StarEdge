# Scripts 目录说明

更新时间：2026-09-15。

## 当前 ZX200 运行入口

| 文件 | 职责 |
|---|---|
| `Zx200KeyboardController.cs` | 键盘和 AZ 输入仲裁，提交底盘与关节控制请求 |
| `Zx200TrackDrive.cs` | 履带接地检测、牵引、转向、侧向抓地和制动 |
| `Zx200FixedBasePhysics.cs` | 关节树质量、惯量、重力、相邻碰撞配置；支持固定和移动模式 |
| `Zx200DigController.cs` | 四关节目标、实际姿态反馈和已有挖掘动作阶段 |
| `Zx200CameraController.cs` | 驾驶室与第三人称视角 |
| `Excavation/DeformableSoilTerrain.cs` | 运行时土层副本、局部切削和地形土方积分 |
| `Excavation/BucketSoilCutter.cs` | 实际刃口采样、斗容守恒和土量显示 |

`Zx200FixedBasePhysics` 沿用原类名和 .meta，以保持已保存的场景引用；它现在会读取键盘组件的 `fixedBase` 配置，而不是始终固定底盘。履带运动由物理施力执行，不使用 Transform 位移或射线吸附高度。

## 其他已有素材

`ExcavatorWASDController`、`ExcavatorDigController` 和 `ExcavatorViewController` 属于旧演示模型控制，不应与 ZX200 控制器同时挂在同一台设备上。它们的移动机制仍是旧演示逻辑。

`*Effect`、`ImageEffects`、`MotionBlur` 等为已有图像效果素材；`Bird`、`Swarm` 等为环境素材；`.js` 文件为历史 UnityScript 素材，不是本次 ZX200 控制入口。此次统一说明注释为中文，未把这些素材改造为新的功能模块。

## 后续维护约定

- 使用中文说明注释；公开控制接口用 XML 注释说明功能、参数及返回值。
- 沿用现有 C# 类型命名，新增设备专用脚本使用 `Zx200` 前缀；通用土方组件放在 `Excavation`，每个组件承担一个明确职责。
- 新代码使用 UTF-8、四空格缩进；此次保留历史素材的原有缩进，避免大面积无关格式改动。
- 测试放 `Assets/Tests`，编辑器配置放设备的 `Editor` 目录，使用说明放 `docs`；不在运行目录放临时诊断脚本和测试结果。
- 后续移动文件时连同 `.meta` 一起移动并检查场景和预制体引用，暂不为了分类批量搬动旧资源。

## 配重与切削扩展

`Zx200FixedBasePhysics` 配置上车体后部真实固定配重；`Zx200TrackDrive` 分别处理行驶与驻车接触摩擦。地形切削只在运行时生效，斗内显示质量暂未进入物理载荷。使用步骤与当前接触近似限制见 `docs/ZX200配重与地形切削使用说明.md`。
