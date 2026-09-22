# Generic URDF-to-URP Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Unity 6 editor command that imports a selected URDF, resolves both project package layouts, converts renderer materials to persistent URP/Lit assets, and saves a reusable prefab.

**Architecture:** A package resolver parses mesh URIs and identifies the robot asset root. A material converter creates deterministic URP materials, using imported textures first and DAE image references as fallback. A menu command coordinates URDF import, conversion, validation, and prefab saving.

**Tech Stack:** Unity 6000.5, C#, UnityEditor, Unity Robotics URDF Importer 0.5.2, URP 17.5, NUnit Editor tests.

## Global Constraints

- Only assets inside the selected robot package directory may be generated or updated.
- Support `package://meshes/...` and `package://<package-name>/meshes/...` layouts.
- Generated materials must use `Universal Render Pipeline/Lit`.
- Existing textures must be preserved; DAE `<init_from>` is the fallback.
- Repeated imports must update, not duplicate, materials and prefabs.

---

### Task 1: Package and DAE Resolution

**Files:**
- Create: `Assets/Robotics/UrdfUrpImporter/Editor/UrdfAssetResolver.cs`
- Create: `Assets/Robotics/UrdfUrpImporter/Editor/Tests/UrdfAssetResolverTests.cs`
- Create: assembly definitions beside implementation and tests.

**Interfaces:**
- Produces: `UrdfAssetResolver.Resolve(string urdfAssetPath)` returning `UrdfAssetLayout` with package, material, prefab, robot-name, and mesh paths.
- Produces: `UrdfAssetResolver.FindDaeTexture(string daeAssetPath)` returning a Unity texture asset path or null.

- [x] Write tests for HR35 and ZX200 package layouts and ZX200 DAE `bake.png` resolution.
- [x] Run Editor tests and confirm failure because resolver types are missing.
- [x] Implement XML parsing, path normalization, existence checks, and DAE image lookup.
- [x] Run resolver tests and confirm they pass.

### Task 2: Generic URP Material Conversion

**Files:**
- Create: `Assets/Robotics/UrdfUrpImporter/Editor/UrdfUrpMaterialConverter.cs`
- Create: `Assets/Robotics/UrdfUrpImporter/Editor/Tests/UrdfUrpMaterialConverterTests.cs`

**Interfaces:**
- Produces: `Convert(Texture texture, string materialDirectory)` returning a persistent `Material`.
- Produces: `ConvertRenderers(GameObject root, string materialDirectory, Func<Renderer, Texture> fallbackTexture)` returning converted slot count.

- [x] Write tests for URP/Lit, Base Map assignment, material reuse, opaque fallback, and renderer replacement.
- [x] Run tests and confirm failure because converter is missing.
- [x] Implement deterministic material creation and opaque URP state normalization.
- [x] Run converter tests and confirm they pass.

### Task 3: Selected URDF Import Command

**Files:**
- Create: `Assets/Robotics/UrdfUrpImporter/Editor/UrdfUrpImportTool.cs`
- Create: `Assets/Robotics/UrdfUrpImporter/Editor/Tests/UrdfUrpImportToolTests.cs`

**Interfaces:**
- Produces menu command `Tools/Robotics/Import Selected URDF as URP`.
- Produces testable `Import(string urdfAssetPath)` returning the saved prefab.

- [x] Write tests for command availability and generated ZX200 prefab material/texture requirements.
- [x] Run tests and confirm failure because import tool is missing.
- [x] Implement selection validation, URDF import, renderer-to-DAE fallback mapping, validation, prefab saving, and cleanup.
- [x] Run import tests and confirm they pass.

### Task 4: Full Verification

**Files:**
- Update generated assets under each fixture's `Materials` and `Prefabs` directories.

- [x] Run the complete importer Editor test assembly.
- [x] Execute imports for ZX200 and HR35 in Unity batch mode.
- [x] Re-run imports to verify idempotency.
- [x] Inspect generated YAML references for URP/Lit and ZX200 `bake.png`.
- [x] Check Unity logs for compilation and shader errors.
