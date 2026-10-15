# 矿车高清贴图

当前 Unity 分支使用 `SmallDumpTruck/Textures/Dumper_HD.png`（车头、底盘）和
`SmallDumpTruck/Textures/Dumper_Back_HD.png`（车厢），均为 1254×1254。
原图为 512×512，仍原样保留。这里只改变两个材质的 BaseMap/MainTex 引用，
未修改模型、UV、场景位置、碰撞体、土方接收区或自动装车代码。

导入保留实际尺寸，开启 mipmap、三线性过滤、4 倍各向异性过滤和高质量压缩。
打开 `Assets/Scenes/sence_test.unity` 即可查看，不依赖 F8/F9 开关。

回退：将 `Dumper.mat` 的 BaseMap/MainTex 改回 `Dumper.png`，
将 `Dumper_Back.mat` 的 BaseMap/MainTex 改回 `Dumper_Back.png`。

## 生成记录

使用内置 imagegen 图像编辑工具，分别以原始两张贴图为输入。
第一轮存在 UV 排布偏移，未采用；采用第二轮并在 Unity 场景中检查映射。
这属于 AI 辅助细节重建，并非恢复原作者的高分辨率源文件。

最终两张贴图使用相同提示词：

> HIGH FIDELITY IMAGE RESTORATION ONLY. This is a production UV ATLAS, not concept art.
> Upscale and sharpen the input image retaining absolutely exact spatial alignment with the source.
> Keep all pixel features and UV island edges at IDENTICAL fractional x/y positions, including the
> original outer margins and every black gap. DO NOT recompose, center, resize individual islands,
> straighten panels, rearrange, redraw seams, add decorative details or change colors.
> Only restore fine detail and remove low-resolution blur while retaining the input geometry and lighting.
> The output should be indistinguishable from the input when downsampled to 512x512.
> Square higher-resolution texture atlas, preferably 2048x2048. No new image composition, no text.

实际输出为 1254×1254，不以插值扩到 2048 冒充原生 2K。

## 验证

- 场景渲染测试 `SavedSceneProducesNaturalAndOriginalViews` 通过，检查了场景截图。
- 连续三斗回归本次只完成两斗（累计 0.46278m³），第三斗在挖后携土姿态超时。
  不能将本次结果描述为三斗连续装车验收通过；本次没有修改动作规划代码。
- 结果位于验证副本 `Diagnostics/truck-hd-render.xml`、`truck-hd-loading.xml`。
