# HR35 Unity 6 Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Import one clean HR35 URDF source tree into HMI_Unity6, create an URP-compatible robot prefab, and verify that its renderers retain the six source textures without unsupported shaders.

**Architecture:** Extract only `ix35e_description` source assets into `Assets/Robotics/HR35`. A focused Editor tool invokes the installed URDF Importer, converts each unique embedded DAE material to a persisted URP/Lit material keyed by its texture, and saves `ix35e.prefab`. EditMode tests cover the material converter; a batch entry point performs end-to-end import and throws on missing assets or incompatible materials.

**Tech Stack:** Unity 6000.5.10f1, C# Editor scripting, Unity Robotics URDF Importer, URP 17.5, NUnit/Unity Test Framework.

## Global Constraints

- Target project: `/home/sk/Unity/HMI_Unity6`.
- Source archive: `/home/sk/Unity/模型/hr35_modeling-master.zip` remains unchanged.
- Import only one HR35 tree; never import `Assets/Robotics/OLD/HR35`.
- Do not change URP pipeline assets or global renderer configuration.
- Stop rather than overwrite an unknown pre-existing `Assets/Robotics/HR35` directory.
- The project is not a Git work tree, so verification checkpoints replace commits.

---

### Task 1: Restore the clean HR35 source tree

**Files:**
- Create: `Assets/Robotics/HR35/ix35e.urdf`
- Create: `Assets/Robotics/HR35/meshes/visual/*.dae`
- Create: `Assets/Robotics/HR35/meshes/collision/*.stl`
- Create: `Assets/Robotics/HR35/textures/*.png`
- Create: `Assets/Robotics/HR35/config/joint_origins.json`

**Interfaces:**
- Consumes: `hr35_modeling-master/ix35e_description` from the source ZIP.
- Produces: one Unity-relative source root at `Assets/Robotics/HR35` with
  `package://meshes/...` URDF paths resolved from that root.

- [ ] **Step 1: Verify the target is absent and the archive has the expected source counts**

Run a read-only shell check for one URDF, 25 DAE, 25 STL, six PNG files, and absence of `Assets/Robotics/HR35`.

- [ ] **Step 2: Extract to a temporary directory**

Use `mktemp -d` and `unzip` to extract only `hr35_modeling-master/ix35e_description/*`.

- [ ] **Step 3: Move the isolated source tree into Assets**

Move the source folders to `Assets/Robotics/HR35`, move `urdf/ix35e.urdf` to that package root,
and rewrite only its `package://ix35e_description/meshes/` prefix to `package://meshes/`.
Do not copy scripts, tests, renders, or viewer files.

- [ ] **Step 4: Verify the imported source tree**

Assert exact counts for `.urdf`, visual `.dae`, collision `.stl`, and `.png`, and assert that `Assets/Robotics/OLD` does not exist.

### Task 2: Add a testable URP material converter

**Files:**
- Create: `Assets/Robotics/HR35/Editor/Hr35.Tools.Editor.asmdef`
- Create: `Assets/Robotics/HR35/Editor/Hr35UrpMaterialConverter.cs`
- Create: `Assets/Robotics/HR35/Editor/Tests/Hr35.Tools.Editor.Tests.asmdef`
- Create: `Assets/Robotics/HR35/Editor/Tests/Hr35UrpMaterialConverterTests.cs`

**Interfaces:**
- Consumes: `Material`, its `mainTexture`, and a material output directory.
- Produces: `public static Material Convert(Material source, string materialDirectory)` and `public static int ConvertRenderers(GameObject root, string materialDirectory)`.

- [ ] **Step 1: Write a failing type-existence test**

Create an EditMode test that resolves `Hr35Import.Hr35UrpMaterialConverter` from `Hr35.Tools.Editor` and asserts it is non-null.

- [ ] **Step 2: Run the test and verify RED**

Run Unity in batch mode with `-runTests -testPlatform EditMode`; expect the type-existence assertion to fail because the converter is absent.

- [ ] **Step 3: Implement the minimal converter type and API**

Create the public static converter with XML documentation for both public methods. `Convert` must find `Universal Render Pipeline/Lit`, create or update a `.mat` keyed by the source texture asset filename (or `Default`), copy `_BaseMap`, set opaque white for textured materials or opaque gray for untextured materials, and persist through `AssetDatabase`.

- [ ] **Step 4: Add behavior tests**

Add tests that create temporary source materials and textures, then assert: the converted shader is URP/Lit; `_BaseMap` equals the source texture; two source materials using the same texture return the same persisted material; and an untextured source becomes opaque gray.

- [ ] **Step 5: Run tests and verify GREEN**

Run the same EditMode test command and require all converter tests to pass without compilation errors.

### Task 3: Add the deterministic URDF import entry point

**Files:**
- Create: `Assets/Robotics/HR35/Editor/Hr35ImportTool.cs`
- Modify: `Assets/Robotics/HR35/Editor/Hr35.Tools.Editor.asmdef`

**Interfaces:**
- Consumes: `Assets/Robotics/HR35/ix35e.urdf`, `UrdfRobotExtensions.Create`, and `Hr35UrpMaterialConverter.ConvertRenderers`.
- Produces: `public static void RunBatchImport()` and `Assets/Robotics/HR35/Prefabs/ix35e.prefab`.

- [ ] **Step 1: Add an integration precondition test**

Add an EditMode test asserting the URDF asset resolves and all 25 URDF visual mesh references and 25 collision mesh references point to existing files.

- [ ] **Step 2: Run the test and verify the source precondition passes**

Run the EditMode suite. This is a characterization/precondition test: it must pass before importer code is added, proving failures later are not missing-source failures.

- [ ] **Step 3: Implement `RunBatchImport`**

Refresh assets, synchronously exhaust `UrdfRobotExtensions.Create(fullUrdfPath, settings, false, false)`, require a returned robot, convert every child Renderer material, save `Assets/Robotics/HR35/Prefabs/ix35e.prefab`, destroy the temporary scene object, save assets, and throw `InvalidOperationException` on any missing Shader, texture, robot, Renderer, or prefab.

- [ ] **Step 4: Add a menu wrapper**

Expose the same operation as `Tools/HR35/Import URDF and Convert to URP` so the user can repeat it interactively.

- [ ] **Step 5: Run the full EditMode test suite**

Require all source and converter tests to pass.

### Task 4: Execute and verify the end-to-end import

**Files:**
- Create: `Assets/Robotics/HR35/Prefabs/ix35e.prefab`
- Create: `Assets/Robotics/HR35/Materials/*.mat`
- Create: `/tmp/hr35-unity6-import.log` (diagnostic output only)
- Create: `/tmp/hr35-unity6-tests.xml` (diagnostic output only)

**Interfaces:**
- Consumes: `Hr35Import.Hr35ImportTool.RunBatchImport`.
- Produces: an importable robot prefab and batch verification evidence.

- [ ] **Step 1: Run Unity 6 import in batch mode**

Run Unity 6000.5.10f1 with `-batchmode -quit -projectPath /home/sk/Unity/HMI_Unity6 -executeMethod Hr35Import.Hr35ImportTool.RunBatchImport -logFile /tmp/hr35-unity6-import.log`.

- [ ] **Step 2: Inspect the first actual Unity error if execution fails**

Search the log for compilation errors, exceptions, missing assets, shader errors, and texture errors. Fix one confirmed root cause at a time and rerun.

- [ ] **Step 3: Run EditMode verification again**

Run the full HR35 EditMode suite after prefab creation and require all tests to pass.

- [ ] **Step 4: Validate serialized outputs**

Verify the prefab exists, generated `.mat` files use the URP/Lit Shader GUID resolved by Unity, all six PNGs remain present, and no `Assets/Robotics/OLD/HR35` exists.

- [ ] **Step 5: Record the manual workflow**

Report the repeatable Unity UI steps: place the clean source tree under Assets, wait for import, select `ix35e.urdf`, use the URDF importer or `Tools/HR35/Import URDF and Convert to URP`, verify materials use URP/Lit and Base Map, then use the generated prefab.
