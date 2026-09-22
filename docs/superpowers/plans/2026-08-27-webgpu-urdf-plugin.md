# WebGPU URDF Plugin Build Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Unity 6 Web/WebGPU build exclude the URDF Importer's Windows Assimp DLLs.

**Architecture:** Replace the immutable Git package cache entry with an embedded package and correct its native plugin platform metadata. Separately repair the invalid Terrain demo directory metadata.

**Tech Stack:** Unity 6.0, Unity Package Manager, PluginImporter metadata, Web platform/WebGPU

## Global Constraints

- Preserve URDF runtime and editor functionality.
- Include x86 Assimp only in 32-bit Windows and x86_64 Assimp only in 64-bit Windows.
- Exclude both Assimp DLLs from Web/WebGPU.
- Do not modify unrelated user assets.

---

### Task 1: Embed and configure the URDF package

**Files:**
- Create: `Packages/com.unity.robotics.urdf-importer/**`
- Modify: `Packages/manifest.json`
- Modify: `Packages/com.unity.robotics.urdf-importer/Runtime/UnityMeshImporter/Plugins/AssimpNet/Native/win/x86/assimp.dll.meta`
- Modify: `Packages/com.unity.robotics.urdf-importer/Runtime/UnityMeshImporter/Plugins/AssimpNet/Native/win/x86_64/assimp.dll.meta`

- [ ] Copy the currently resolved URDF package into `Packages/com.unity.robotics.urdf-importer`.
- [ ] Remove its remote dependency from `Packages/manifest.json` so Unity resolves the embedded copy.
- [ ] Set explicit Windows-only platform data on both native DLLs.
- [ ] Validate JSON, package identity, and plugin platform metadata.

### Task 2: Repair invalid Terrain demo metadata

**Files:**
- Modify: `Assets/TerrainDemoScene_URP/Scripts.meta`

- [ ] Preserve the first valid GUID and remove Git conflict markers and the competing GUID.
- [ ] Validate that the file is parseable Unity YAML metadata and contains exactly one GUID.

### Task 3: Verify Web build

**Files:**
- Inspect: `Logs/Editor.log`

- [ ] Wait for Unity to resolve and compile the embedded package.
- [ ] Run a Web build using the active Web build profile.
- [ ] Confirm the latest build log has no Assimp plugin collision; report any next blocking error separately.
