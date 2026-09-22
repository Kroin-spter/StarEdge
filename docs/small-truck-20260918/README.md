# 小矿卡接入

资源：用户提供的 No-Brand Dump Truck。源包为 /home/sk/Unity/模型/小矿卡/no-brand-dump-truck.zip，包含原始 FBX 和 8 张 PNG。glTF 版本统计为 6489 个三角面、19 个网格、13 个材质，无现成动画。

实现：Assets/ConstructionSite/SmallDumpTruck 保存 FBX、贴图、URP 材质与署名。更新现有 ParkedDumpTruck.prefab，保留根物体与 SoilReceivingZone 的序列化标识，场景与卸料器引用无需重建。车斗、尾门、轮胎仍为独立视觉节点；当前为停放接收车辆，没有新增驾驶或翻斗动画。禁用源模型的平面假阴影与灯光贴片。

比例：导入 scale factor 100，外宽约 2.46m、全长约 7.56m；这是按轮胎和车身比例校正的演示尺寸，源文件不含可验证的车型规格。轮胎底部归零，车头为 +Z，车斗中心为根物体 X/Z 原点。

接收区：2.00 × 4.74m，底板距地约 1.0168m，填充上限约 1.0688m，几何容量约 10.132m³（不是额定载重）。保持 receiver 单位缩放。底板碰撞表面低于可见底板/接收面 2mm，防止同平面的浮点碰撞先触发普通散落处理。四侧墙、驾驶室、底盘和轮胎使用简化 BoxCollider。

生成工具：Tools > ZX200 > Soil > Use Textured Small Dump Truck；仅更新资源与 prefab，不另存或覆盖主场景。重复生成会重建本模型的材质 remap。

验证：新增 prefab 测试覆盖贴图、车斗独立节点、轮胎接地、车斗底面对齐、接收范围内外判定。主场景实际回转卸料：斗内清空，接收 0.08200365m³；车厢土显示正常。主场景完整回归最后总账断言仍失败：累计切出 0.09701801m³，当前目的地合计 0.09650828m³，差 0.00050972m³；保留原断言，未在模型替换中改动土方算法。截图为本次实际运行结果。

WebGL 尚未重新导出。

署名：This work is based on “No-Brand Dump Truck” (https://sketchfab.com/3d-models/no-brand-dump-truck-48c77f54fa4d4b8c97749532a9d0fb52) by asian3dmodel (https://sketchfab.com/nguyenhoanglam20100609), licensed under CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/). Scale, materials and collider/receiver integration have been modified.

最终编辑器回归：154/154 通过。主场景装车流程已执行，最后总账断言失败，见随附 XML。
原资源备份：/home/sk/Unity/backups/small-truck-20260918-162100
