# HR35 在 Unity 6 / URP 中的导入流程

## 本项目中的最快操作

1. 用 Unity 6 打开 `HMI_Unity6`，等待右下角资源导入和脚本编译结束。
2. 点击菜单 `Tools > HR35 > Import URDF and Convert to URP`。
3. 导入完成后使用 `Assets/Robotics/HR35/Prefabs/ix35e.prefab`。
4. 在 Inspector 中抽查材质：Shader 应为 `Universal Render Pipeline/Lit`，
   贴图应位于 `Base Map`。

该菜单可以重复执行：它会重新生成 HR35 Prefab，并复用
`Assets/Robotics/HR35/Materials` 中按贴图命名的 URP 材质。

## 从原始 ZIP 重新开始

1. 确认项目安装了 `Universal RP` 和 `URDF Importer`，并且 Graphics Settings 已启用 URP。
2. 从 `hr35_modeling-master.zip` 只取 `ix35e_description` 中的 `meshes`、`textures`、
   `config` 和 `urdf/ix35e.urdf`，不要导入 ZIP 顶层的测试、渲染图或 Python 文件。
3. 目录整理成下面的现代 URDF Importer package 布局：

   ```text
   Assets/Robotics/HR35/
   ├── ix35e.urdf
   ├── meshes/
   │   ├── visual/*.dae
   │   └── collision/*.stl
   ├── textures/*.png
   └── config/joint_origins.json
   ```

4. 把 URDF 中的 Mesh 路径从
   `package://ix35e_description/meshes/...` 改为 `package://meshes/...`。
   当前 URDF Importer 以 URDF 所在目录作为 package 根目录。
5. 回到 Unity 等待 DAE、STL 和 PNG 全部导入。
6. 使用 `Tools > HR35 > Import URDF and Convert to URP` 完成导入和材质转换。

## 不使用本项目工具时

1. 在 Project 窗口选中 `ix35e.urdf`。
2. 选择 `Assets > Import Robot from Selected URDF file`。
3. 坐标轴使用默认 Y Axis；碰撞分解可选 Unity；首次导入不需要覆盖已有 Prefab。
4. 导入后把模型保存成 Prefab。
5. 对所有模型材质执行 Built-in 到 URP 的转换；若 DAE 的嵌入材质无法批量转换，
   需要把材质提取为外部 `.mat`，设置为 `Universal Render Pipeline/Lit`，并把原 PNG
   放入 `Base Map`。

## 紫色模型的检查顺序

1. 先看材质 Shader。紫色首先表示 Shader 缺失或与当前渲染管线不兼容，不等于 PNG 丢失。
2. Unity 6 URP 项目中应使用 `Universal Render Pipeline/Lit`，不要继续使用 Built-in 的
   `Standard`。
3. Shader 正确但仍无纹理时，再检查 `Base Map` 是否引用了
   `Assets/Robotics/HR35/textures` 中的 PNG。
4. Mesh 缺失时，检查 URDF 是否采用 `package://meshes/...`，且 URDF 位于 HR35 package 根目录。
5. 不建议导入 `AZ_demo.unitypackage` 的全部内容：它同时包含 `HR35`、`OLD/HR35` 和
   Unity 2021 生成的旧资源，会重新带入 Built-in 材质与重复文件。
