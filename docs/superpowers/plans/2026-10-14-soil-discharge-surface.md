# 卸料与地面尖刺修复 Implementation Plan

**Goal:** 消除跨支撑面拉长的散土网格，限制局部回填尖峰，改善卸料和车厢堆积。

**Architecture:** 保留现有土量账本、车辆位置和自动关节动作。散土使用有界支撑面；真实落点驱动车厢堆积；卸料采用连续角度响应。

**Constraints:** 不修改场景、地图资产、ProjectSettings、串口。沿用当前功能分支和独立 Unity 验证副本。用户已批准前述方案，直接实施。

- [x] 地面：微量土堆跨两米支撑高差、稳定面集中回填两项测试先失败后通过。散土贴地高差有界，飞行回填按邻点承载坡度限制；其余土量仍走现有账本。
- [x] 车厢：新增 ReceivingSoilHeap 13×17 高度网格，按落点堆积、局部摊开、限制高度；SoilFlightTransport 传递落点并对显示的三角面做最早碰撞检测。网格体积与账目积分测试通过。
- [x] 卸料：连续保土量、近向下快速启动、有限尾料排空；保留轻微倾斜保土和斗口遮挡。
- [x] 验证：相关 EditMode 196/196；实际 PlayMode 2/2，连续三斗车厢增量 0.70045m³，手动切削/收斗/退出通过。

## 证据与限制

- 独立验证副本 Diagnostics/discharge-ground-red.xml 复现地面和卸料缺陷；discharge-heap-red.xml 复现车厢平板；discharge-oblique-red.xml 复现斜向跨土丘漏检。
- 最终结果 Diagnostics/discharge-final-unit.xml、discharge-scene.xml；检查 three-loads-completed.png 与 manual-improved-curl-cut.png。
- 手动截图中未见原高尖柱，仍有小散土块、局部不圆滑边缘；不是所有手动姿态的穷尽验收。地面散土仍是近似几何，不是完整颗粒动力学。
- 更宽筛选一次包含 Zx200SavedPoseTests.SavedSoilDemoDisplaysItsOwnWorkReadyPose，报告姿态差约 0.5075°，不在最终相关196项中；本轮未修改该场景/姿态，不宣称全项目测试通过。
- 不改地图、场景或自动关节轨迹；未合并 main、未 push、未导出前端。ProjectSettings 用户改动保持原哈希。
