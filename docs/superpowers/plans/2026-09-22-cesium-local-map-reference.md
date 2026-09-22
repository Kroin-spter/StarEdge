# Cesium 本地地图引用实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让主场景在 Unity 编辑器和运行时自动使用项目同级 `tilesets/osgb_small_005_004/tileset.json`，同时保留环境变量覆盖和 WebGL 地图路径。

**Architecture:** 新增一个轻量的 `CesiumLocalMapReference` 组件，集中解析本地地图目录、切换 Cesium Tileset 到 `FromUrl` 并生成 `file://` URL。组件挂载在主场景施工区域；`CesiumMoundSite` 复用同一解析逻辑，避免编辑器和运行时出现两套路径规则。场景只保存地图名称和组件引用，不保存当前机器的绝对路径。

**Tech Stack:** Unity `6000.5.10f1`、Cesium for Unity `1.25.1`、C#、Unity Test Framework、URP。

## Global Constraints

- 地图数据不进入 Git，继续由 `.gitignore` 排除的 `../tilesets/` 提供。
- 地图解析顺序固定为 `CESIUM_TILESET_DIR` → 项目同级 `../tilesets/<mapName>`。
- WebGL 不读取本机文件路径，继续使用构建输出中的 `maps/<mapName>/tileset.json`。
- 缺失地图时项目保持可打开，施工区域保持未就绪，不能创建重复的替代碰撞。
- 提交说明保留 `feat:`、`test:`、`docs:` 等前缀，前缀后使用中文描述，提交者为 `shenkai`。

## 文件结构

- Create: `Assets/WebDelivery/Runtime/CesiumLocalMapReference.cs` — 本地地图路径解析和 Tileset 配置组件。
- Create: `Assets/WebDelivery/Runtime/CesiumLocalMapReference.cs.meta` — Unity 脚本 GUID。
- Modify: `Assets/WebDelivery/Runtime/CesiumMoundSite.cs` — 复用统一解析逻辑，并确保运行时先切换 `FromUrl` 再设置 URL。
- Modify: `Assets/Scenes/sence_test.unity` — 将本地地图引用组件挂到施工区域并绑定激活的 Cesium Tileset。
- Create: `Assets/Tests/EditMode/CesiumLocalMapReferenceTests.cs` — 验证环境变量、同级目录和 `file://` URL 规则。
- Create: `Assets/Tests/EditMode/CesiumLocalMapReferenceTests.cs.meta` — Unity 测试脚本 GUID。
- Modify: `Assets/Tests/EditMode/ExcavatorControls.Editor.Tests.asmdef` — 让 EditMode 测试程序集引用 `WebDelivery.Runtime`。
- Modify: `README.md` — 说明场景自动引用本地地图及缺失地图时的行为。

### Task 1: 新增可复用的本地地图引用组件

**Files:**
- Create: `Assets/WebDelivery/Runtime/CesiumLocalMapReference.cs`
- Create: `Assets/WebDelivery/Runtime/CesiumLocalMapReference.cs.meta`
- Test: `Assets/Tests/EditMode/CesiumLocalMapReferenceTests.cs`
- Modify: `Assets/Tests/EditMode/ExcavatorControls.Editor.Tests.asmdef`

**Interfaces:**
- Consumes: `CesiumForUnity.Cesium3DTileset`, `Application.dataPath`、`CESIUM_TILESET_DIR` 和地图名称。
- Produces: `TryResolveTilesetPath(string mapName, out string tilesetPath)`、`ToFileUri(string tilesetPath)`、`ApplyLocalMap(Cesium3DTileset tileset, string mapName, UnityEngine.Object context)`。

- [ ] **Step 1: 编写路径解析测试**

  测试保存并恢复 `CESIUM_TILESET_DIR`，使用临时目录创建 `tileset.json`，验证环境变量目录优先于项目同级目录；再验证 `ToFileUri` 返回以 `file:///` 开头且空格被正确编码的 URL。

- [ ] **Step 2: 运行 EditMode 测试确认测试先失败**

  在 Unity Test Runner 中运行 `CesiumLocalMapReferenceTests`，预期由于组件和静态方法尚未存在而编译失败。

- [ ] **Step 3: 实现组件和路径解析**

  实现 `[ExecuteAlways]` 组件，公开 `tileset` 和 `mapName` 字段；在 `OnEnable`、`OnValidate`、`Start` 中调用 `ApplyLocalMap`。编辑器和桌面运行时解析本地文件；WebGL 非编辑器构建直接跳过本机路径应用。设置 Tileset 时使用以下顺序：

  ```csharp
  tileset.tilesetSource = CesiumDataSource.FromUrl;
  tileset.url = ToFileUri(tilesetPath);
  ```

  缺少 `tileset.json` 时只记录带实际路径的警告并返回 `false`。

- [ ] **Step 4: 运行测试确认路径逻辑通过**

  在 Unity Test Runner 中重新运行 `CesiumLocalMapReferenceTests`，预期所有测试通过；同时检查 `git diff --check` 返回成功。

- [ ] **Step 5: 提交组件和测试**

  ```bash
  git add Assets/WebDelivery/Runtime/CesiumLocalMapReference.cs Assets/WebDelivery/Runtime/CesiumLocalMapReference.cs.meta Assets/Tests/EditMode/CesiumLocalMapReferenceTests.cs Assets/Tests/EditMode/CesiumLocalMapReferenceTests.cs.meta Assets/Tests/EditMode/ExcavatorControls.Editor.Tests.asmdef
  git commit -m "feat: 增加 Cesium 本地地图引用组件"
  ```

### Task 2: 统一主场景运行时地图配置

**Files:**
- Modify: `Assets/WebDelivery/Runtime/CesiumMoundSite.cs:52-66`
- Modify: `Assets/Scenes/sence_test.unity` 中 `ZX200 construction site` 组件列表和序列化字段

**Interfaces:**
- Consumes: Task 1 的 `CesiumLocalMapReference.TryResolveTilesetPath`、`ToFileUri` 和 `ApplyLocalMap`。
- Produces: 主场景中的 `CesiumLocalMapReference` 组件引用，以及运行时统一的 Cesium `FromUrl` 配置。

- [ ] **Step 1: 更新运行时路径解析调用方**

  将 `CesiumMoundSite.ResolveTilesetUrl()` 的桌面分支改为调用 `CesiumLocalMapReference.ApplyLocalMap(tileset, mapName, this)`；WebGL 分支先设置 `tilesetSource = CesiumDataSource.FromUrl`，再设置 `maps/<mapName>/tileset.json` 的输出 URL。

- [ ] **Step 2: 将引用组件挂入主场景**

  为 `ZX200 construction site` 增加 `CesiumLocalMapReference` 组件，绑定当前激活的 Cesium Tileset `fileID: 1601958135`，设置 `mapName: osgb_small_005_004`。不启用场景中备用 Ion Tileset，不改变现有局部土层、设备和车厢引用。

- [ ] **Step 3: 做静态场景配置检查**

  检查 `sence_test.unity` 中施工区域只绑定一个激活 Tileset，组件使用 `FromUrl`，并确认场景文本不包含 `/home/sk/Unity/` 或其他机器绝对路径。

- [ ] **Step 4: 提交场景接入**

  ```bash
  git add Assets/WebDelivery/Runtime/CesiumMoundSite.cs Assets/Scenes/sence_test.unity
  git commit -m "feat: 接入主场景本地 Cesium 地图"
  ```

### Task 3: 更新使用说明并执行项目级验证

**Files:**
- Modify: `README.md` 的 Cesium 地图数据和当前场景模式章节

**Interfaces:**
- Consumes: Task 2 的场景组件和路径约定。
- Produces: 可交接的本地地图准备说明和验证结果。

- [ ] **Step 1: 更新 README**

  明确说明打开 `sence_test.unity` 后会由 `CesiumLocalMapReference` 自动选择本地地图；说明默认同级目录、`CESIUM_TILESET_DIR` 覆盖方式、地图缺失时的安全等待状态，以及地图目录不提交 Git。

- [ ] **Step 2: 执行静态验证**

  运行以下检查：

  ```bash
  git diff --check
  test -f /home/sk/Unity/tilesets/osgb_small_005_004/tileset.json
  rg -n "CesiumLocalMapReference|osgb_small_005_004|FromUrl" Assets/Scenes/sence_test.unity Assets/WebDelivery README.md
  ! rg -n "/home/sk/Unity/|file:///home/|CESIUM_TILESET_DIR=.*home" Assets/Scenes/sence_test.unity
  ```

- [ ] **Step 3: 执行 Unity 验证**

  在 Unity `6000.5.10f1` 中打开 `Assets/Scenes/sence_test.unity`，确认本地 Cesium 地图加载并生成碰撞；在 Test Runner 中运行现有 EditMode 测试和 Cesium 主场景 PlayMode 测试，确认挖掘机、局部土层和原地图没有重复碰撞。

- [ ] **Step 4: 提交文档**

  ```bash
  git add README.md
  git commit -m "docs: 补充 Cesium 本地地图使用说明"
  ```

- [ ] **Step 5: 交付前检查**

  ```bash
  git status --short --branch
  git log -5 --format='%h %an %s'
  git diff HEAD~3..HEAD --stat
  ```

  预期工作区干净、最近提交者为 `shenkai`、提交说明前缀后的描述为中文，且地图大文件未出现在 Git 变更中。
