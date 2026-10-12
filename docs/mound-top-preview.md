# 堆顶挖掘预览

项目目录：`/home/sk/Unity/HMI_Unity6_mound_preview`。
分支：`feat/mound-top-dig-preview`，基于 main 的 `e03ab45`，不合并、不推送。

## 查看

使用 Unity 6000.5.10f1，打开 `Assets/Scenes/sence_test.unity`。
如果场景在资源更新前已经打开，请退出 Play、重新打开该场景；不要把旧场景的内存状态覆盖保存到新文件。
选择 `ZX200_Digging_Demo` 后按 F，可在 Scene 视图定位设备。
本地地图仍使用项目旁的 `../tilesets/osgb_small_005_004/tileset.json`。

## 布局

- 挖掘机站位 X/Z `(325.28, 347.43)`，偏航 31.008°；高度按清除扫描设备后的原土堆顶面确定。
- 矿卡站位 X/Z `(316.28, 352.43)`，偏航 239.868°；轮下局部整平。
- 在 28×28m 原地图采样区内采用非矩形替换边界，保留实际转角方向，外围使用 Terrain holes 隐藏。
- 不再生成径向规则土台。66,049 个高度样本来自原 Cesium；只修补设备凸起、扫描碎片及局部承载区。
- 履带承载核心和矿卡轮下保留不可挖底面；其余替换区具有约 1.6m 可切厚度。
- Cesium 在同一非矩形区域内被裁剪；边缘保留格的外侧顶点下沉 2cm，避免覆盖原图形成凸边。
- 本位置原始俯视影像按 28×28m 一对一投影，不重复平铺。只在扫描设备和阴影掩码内混合修补影像，掩码外像素不变。
- 履带由烘焙 Terrain 支撑，不再等待已经替换掉的原 Cesium 地面。

新资源位于 `Assets/ConstructionSite/Baked/MapFollowingPreview`。原 `SenceTest` 和第一版 `MoundTopPreview` 烘焙资源保留，可回退。

`OriginalCorner.png` 是原地图采集；`RepairedCorner.png` 通过内置图像编辑工具生成。提示词核心：只清除扫描矿卡、白色挖掘机及其阴影，补为邻近矿区泥土，保留原取景、道路和崖壁纹理。实际 Terrain 纹理仅局部采用该结果，不直接替换整张原图。

## 验证边界

本版本目标是布局预览和真实可挖土堆。验证覆盖高度场、快照兼容性、Play 就绪、前方地形切削及截图。
未把原自动动作重新标定到堆顶向下挖掘，也未宣称已完成自动多轮装车验收。
项目原有 Assimp Linux `libdl.so` 加载异常和旧 Terrain 的缺失树引用不在本次改动范围内。

本轮实际 Play 检查通过：施工区就绪、履带由新 Terrain 承载、前方切削产生土量并降低高度。截图位于忽略提交的 `Diagnostics/map-following-{preview,close,front}.png`。
相关 EditMode 测试 24/24 通过，PlayMode 测试 1/1 通过；不代表全项目测试或自动装车验收通过。
原地图的颜色/投影接缝和局部扫描伪影并非全部消除；本轮未把自动动作重新标定。应先在 Scene/Play 中确认这版布局，再推进动作验收。

## 哑光与前部窄尖刺修复

- Unity 6 新 TerrainLayer 默认使用 DiffuseAlphaChannel；RGB 影像的不透明 Alpha 让 smoothness=0 失效。已显式使用 ConstantOnly，光滑度和金属度均为 0。
- Terrain 使用预览资源内独立 `MatteSoil.mat`，不修改全项目公用 Terrain 材质或车辆材质。
- 分层截图确认右下角薄片来自可挖 Terrain 的残留高度，而不是未裁掉的 Cesium 瓦片。检测到 `(321.92,341.04)` 相邻格高差约 0.71m。
- 局部修补中心 `(322,341)`、半径 X=3m/Z=2.2m 的凸起，并补充中心 `(319,339.3)`、半径 X=1.6m/Z=1.2m 的薄片范围，从周边高度连续填补；设备位置、承载核心、Cesium 裁剪范围与自动动作不变。
- 保留各格可切厚度，按 Unity 实际高度回读重算底面和初始土量；后续重新烘焙也覆盖这片漏检区域。
- 回归测试先分别复现 Alpha 反光配置与异常高度突跳，再验证修复。
- 最终相关 EditMode 测试 26/26、PlayMode 就绪与切削测试 1/1 通过；实际截图为 `Diagnostics/matte-spike-preview.png` 和 `Diagnostics/matte-spike-close.png`。未验证完整自动装车循环。
