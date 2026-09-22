# HR35 Unity 6 导入与 URP 材质恢复设计

## 目标

把 `hr35_modeling-master.zip` 中唯一一套 HR35 源资源导入
`/home/sk/Unity/HMI_Unity6`，在 Unity 6.5、URP 17.5 环境中得到可正常显示贴图的
URDF 模型，并排除 `AZ_demo.unitypackage` 里的 `Assets/Robotics/OLD/HR35` 重复副本。

## 已确认的根因

- 原始 ZIP 和 UnityPackage 都包含 6 张纹理，DAE 中的相对纹理路径有效，贴图没有丢失。
- Unity 2021 项目使用 Built-in Render Pipeline；Unity 6 项目使用 URP 17.5。
- UnityPackage 中的 `Default.mat` 绑定内置 Standard Shader。URP 无法渲染该 Shader 时，
  Renderer 会显示紫色。
- UnityPackage 还包含旧项目生成的 1,334 个 Mesh `.asset` 和一套 `OLD/HR35`，
  不适合作为 Unity 6 的干净源导入。

## 方案比较

1. 整个 UnityPackage 导入后删除重复项：最大限度保留旧 GUID，但会先引入大量旧版生成资源，
   还需要清理 `OLD/HR35`，迁移噪声最大。
2. 复制 Unity 2021 项目的 `Assets/Robotics/HR35`：可保留旧项目导入结果，但同样携带大量
   由旧版 URDF Importer 生成的碰撞 Mesh，版本耦合明显。
3. 从原始 ZIP 重新导入源资源：只恢复 URDF、DAE、STL、纹理及必要配置，让 Unity 6 使用
   当前 URDF Importer 重建 Unity 资源，再显式建立 URP/Lit 材质。此方案最干净、可复现，采用此方案。

## 导入设计

1. 从 ZIP 的 `ix35e_description` 恢复到 `Assets/Robotics/HR35`，保留
   `meshes/visual`、`meshes/collision`、`textures` 和 `config`；把
   `urdf/ix35e.urdf` 放到 HR35 package 根目录。
2. 不导入 ZIP 顶层测试、渲染图、Python 环境，也不导入 UnityPackage 中的 `OLD/HR35`。
3. 把 URDF 的 `package://ix35e_description/meshes/...` 改为
   `package://meshes/...`，再通过当前项目已安装的 URDF Importer 导入
   `Assets/Robotics/HR35/ix35e.urdf`。当前版本以 URDF 所在目录作为 package 根目录。
4. 为 DAE 内引用的 6 张纹理创建可复用的 URP/Lit 材质，并按 DAE 的 material/image 映射
   替换模型 Renderer 上的旧 Standard/错误材质。无纹理的部件使用灰色 URP/Lit 默认材质。
5. 将最终模型保存为项目内 Prefab，避免只存在于临时 Scene。

## 安全与错误处理

- 导入前确认目标目录不存在；若存在则停止，不覆盖未知资源。
- 源压缩包保持只读，不修改 Unity 2021 项目。
- Unity 批处理失败时保留日志，按首个实际导入/编译错误定位，不继续叠加修改。
- 不修改项目的 URP 管线设置和全局 Renderer 配置。

## 验收标准

- `Assets/Robotics/HR35` 只有一套 HR35 源资源，不存在 `OLD/HR35`。
- URDF、25 个 visual DAE、25 个 collision STL 和 6 张 PNG 均存在且可被 Unity 导入。
- 最终 Prefab 可加载，主要 Renderer 使用 `Universal Render Pipeline/Lit`，没有错误 Shader。
- Unity 批处理日志无与 HR35 导入相关的编译错误、缺失 Mesh 或缺失纹理错误。
- 输出一份手工重复操作步骤，包含导入路径、URDF 菜单操作与材质转换检查方法。
