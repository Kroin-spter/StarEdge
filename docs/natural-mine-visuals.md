# 矿区画面预设

打开 `Assets/Scenes/sence_test.unity`，进入 Play 后默认启用自然画面。按 **F8**，或点击右上角“画面：自然 / 原始”，可以随时对比。切换只调整相机后处理、抗锯齿和阳光阴影强度。

在 **Main Camera → Mine Visual Preset** 中可调整：

- Enhancement Enabled：默认是否启用。
- Show Toggle：是否显示右上角按钮。
- Exposure：曝光补偿，默认 0.2。
- Contrast：对比度，默认 8。
- Saturation：饱和度，默认 3。
- Temperature：略暖的色温，默认 2。
- Shadow Strength Scale：阴影强度相对原值的比例，默认 0.82。

参数在启用时读取；如需在 Play 中调参数，先关闭预设、调整参数、再开启。原始灯光与相机设置在关闭预设时恢复。PC 管线已有环境遮蔽，继续沿用；预设不开启泛光、景深、运动模糊、胶片颗粒或色差。

`Assets/MineRendering/NaturalMineProfile.asset` 是构建所需的序列化配置，场景中已引用。运行时使用其独立副本，退出 Play 不会把参数写回该资产。通过现有 `Tools/Excavator/导出并更新座舱前端` 菜单导出时，此组件随场景一起进入 WebGL。

视觉预设不能改善地图贴图的原始分辨率、拉伸、地形尖刺或物理穿模。调色效果需在 Game/Play 中查看，当前组件不修改编辑模式下的 Scene 视图。

同机位原始/自然截图与本地测试结果保存在验证工程 `/home/sk/Unity/manual-acceptance-5JEDMw/Diagnostics/`。渲染开销测量包含 1280×900 强制渲染及 1 像素 GPU 同步读回，只用于本机比较，不表示浏览器实际帧率。本次没有重新导出或测量前端浏览器。
