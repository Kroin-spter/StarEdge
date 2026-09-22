# Unity HMI_Unity6 源码仓库整理设计

## 目标

将现有 `/home/sk/Unity/HMI_Unity6` 整理为可提交到用户 Git 仓库的 Unity 源码仓库。保留当前 Unity 工程结构和功能，排除本地生成物、构建产物及不应进入版本库的用户配置，并验证项目可以正常启动。

## 仓库边界

- Git 根目录使用现有项目目录 `/home/sk/Unity/HMI_Unity6`。
- 不新建第二份 Unity 工程，不移动或删除当前工程文件。
- Cesium 地图数据目录 `/home/sk/Unity/tilesets/osgb_small_005_004` 不纳入本次源码提交；在项目说明中记录其准备方式和路径要求。

## 提交内容

- `Assets/` 中当前项目实际使用的场景、脚本、模型、材质、插件、测试和配置资源。
- `Packages/manifest.json`、`Packages/packages-lock.json` 以及项目需要的本地包。
- `ProjectSettings/`。
- 项目启动脚本、工具脚本和必要文档。
- 根目录 `.gitignore` 和项目 `README.md`，说明 Unity 版本、启动场景、Cesium 地图准备和首次导入行为。

## 排除内容

- Unity 生成目录：`Library/`、`Temp/`、`Logs/`、`obj/`、`UserSettings/`。
- Unity 自动生成解决方案文件：`*.csproj`、`*.sln`、`*.slnx`、`*.user`。
- `AZ_demo*` 等 WebGL 构建输出及 Burst 生成文件。
- 旧的恢复备份、临时文件和本机编辑器配置。
- 外部 Cesium tileset 地图数据。

## 启动与提交验证

1. 检查 Git 暂存内容，确认未包含生成目录、构建产物、用户配置和本机绝对路径敏感配置。
2. 使用项目指定的 Unity 6000.5.10f1 以无界面方式打开并退出，确认工程导入和脚本编译不阻断启动。
3. 检查默认场景仍为 `Assets/Scenes/sence_test.unity`。
4. 创建本地 Git commit，不配置远程仓库、不执行 push。

## 取舍

当前先采用“源码仓库 + 外部 Cesium 地图数据”的方案。这样可以保持仓库适合 Git 管理，避免将大地图数据和本机绝对路径绑定到源码中；地图数据仍由 README 说明其准备位置和启动要求。
