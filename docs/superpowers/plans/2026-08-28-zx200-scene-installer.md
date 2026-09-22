# ZX200 Scene Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add and verify a one-click ZX200 scene installer with correct articulation configuration and observable Space-triggered digging.

**Architecture:** Extend the existing ZX200 editor setup as the single configuration boundary. Keep runtime input and articulation motion in `Zx200DigController`, with explicit readiness and trigger diagnostics.

**Tech Stack:** Unity 6.0, C#, ArticulationBody, UnityEditor prefab/scene APIs, NUnit EditMode tests.

## Global Constraints

- Do not delete or modify existing excavators in the active scene.
- Instantiate only `Assets/Robotics/zx200_description/Prefabs/zx200.prefab`.
- Preserve Unity 6 URP materials and the existing generic URDF import flow.
- Every public method must have XML documentation.

---

### Task 1: Prove and correct articulation configuration

**Files:**
- Modify: `Assets/Tests/EditMode/Zx200DigControllerTests.cs`
- Modify: `Assets/Scripts/Zx200DigController.cs`

**Interfaces:**
- Produces: `bool PrepareForAnimation()` that resolves joints, stabilizes the tree, configures drives, and returns readiness.
- Produces: `bool PlayDigCycle()` with explicit rejection diagnostics.

- [x] Add a failing test asserting a locked zero-range continuous swing is configured as a free revolute drive with useful limits.
- [x] Run the focused EditMode test and confirm it fails for the locked swing.
- [x] Implement the minimal articulation preparation and diagnostic logging.
- [x] Run the focused test and the existing controller tests.

### Task 2: Add the active-scene installer

**Files:**
- Modify: `Assets/Robotics/zx200_description/Editor/Zx200PrefabSetup.cs`
- Modify: `Assets/Robotics/UrdfUrpImporter/Editor/Tests/Zx200PrefabSetupIntegrationTests.cs`

**Interfaces:**
- Produces: menu command `Tools/ZX200/Add To Current Scene`.
- Consumes: `Zx200DigController.PrepareForAnimation()`.

- [x] Add a failing integration test that invokes the placement function and asserts one connected prefab instance with a ready controller.
- [x] Run the focused integration test and confirm the placement API is missing.
- [x] Implement prefab loading, instantiation, validation, selection, undo registration, and scene dirty marking.
- [x] Run all ZX200 and URDF importer EditMode tests.

### Task 3: Regenerate and verify the shipped prefab

**Files:**
- Modify generated asset: `Assets/Robotics/zx200_description/Prefabs/zx200.prefab`

**Interfaces:**
- Consumes: ZX200 postprocessor configuration from Task 2.
- Produces: the prefab used by the menu command.

- [x] Regenerate/configure the prefab in an isolated Unity test project.
- [x] Run the complete relevant EditMode suite and require zero failed tests.
- [x] Inspect the serialized prefab for one controller, four non-null joint references, stable drive values, and enabled animation stabilization.
- [x] Report the exact menu path and manual verification steps.
