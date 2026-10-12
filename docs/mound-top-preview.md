# 堆顶挖掘预览

项目目录：`/home/sk/Unity/HMI_Unity6_mound_preview`。
分支：`feat/mound-top-dig-preview`，基于 main 的 `e03ab45`，不合并、不推送。

## 查看

使用 Unity 6000.5.10f1，打开 `Assets/Scenes/sence_test.unity`。
如果场景在资源更新前已经打开，请退出 Play、重新打开该场景；不要把旧场景的内存状态覆盖保存到新文件。
选择 `ZX200_Digging_Demo` 后按 F，可在 Scene 视图定位设备。
本地地图仍使用项目旁的 `../tilesets/osgb_small_005_004/tileset.json`。

## 布局

- 挖掘机站位 `(320.28, 1.43, 351.43)`，偏航 31.008°。
- 矿卡站位约 `(314.28, -1.83, 355.04)`，偏航 239.868°，避免原参考坐标造成重叠。
- 土堆覆盖 28×28m，包含履带脚下、前方和转角；不是仅贴一条斜坡土带。
- 承载核心保留不可挖底面；周围土堆可切削，烘焙初始可切土量约 724m³。
- 仅在完整 Terrain 填充区域内裁剪 Cesium，移除原扫描设备的显示和碰撞。
- 材质复用已有 Cesium 影像中的裸土片段，不投射白色扫描设备；镜像平铺避免重复边界硬接缝。
- 履带由烘焙 Terrain 支撑，不再等待已经替换掉的原 Cesium 地面。

新资源位于 `Assets/ConstructionSite/Baked/MoundTopPreview`。原 `SenceTest` 烘焙资源保留。

## 验证边界

本版本目标是布局预览和真实可挖土堆。验证覆盖高度场、快照兼容性、Play 就绪、前方地形切削及截图。
未把原自动动作重新标定到堆顶向下挖掘，也未宣称已完成自动多轮装车验收。
项目原有 Assimp Linux `libdl.so` 加载异常和旧 Terrain 的缺失树引用不在本次改动范围内。
