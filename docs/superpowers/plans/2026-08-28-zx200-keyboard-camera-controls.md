# ZX200 Keyboard and Camera Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add keyboard driving, simultaneous manual joint controls, and right-mouse first/third-person cameras to the ZX200 prefab while retaining Space automatic digging.

**Architecture:** Keep joint authority in `Zx200DigController`; add one focused keyboard/movement component and one focused camera component. Configure all references and defaults through the existing ZX200 prefab postprocessor.

**Tech Stack:** Unity 6, C#, legacy Unity Input with Active Input Handling set to Both, ArticulationBody reduced coordinates, NUnit EditMode and PlayMode tests.

## Global Constraints

- Preserve Space-triggered automatic digging.
- Allow WASD driving and camera input while the automatic cycle runs.
- Lock manual joint input during the automatic cycle.
- Keep all manual joint targets inside URDF limits.
- Do not enable unstable gravity or self-collision physics.
- Document every public method with XML comments.

---

### Task 1: Manual joint API and keyboard movement

**Files:**
- Modify: `Assets/Scripts/Zx200DigController.cs`
- Create: `Assets/Scripts/Zx200KeyboardController.cs`
- Modify: `Assets/Tests/EditMode/Zx200DigControllerTests.cs`
- Create: `Assets/Tests/EditMode/Zx200KeyboardControllerTests.cs`

**Interfaces:**
- Produces: `bool AdjustManualPose(float swingDelta, float boomDelta, float armDelta, float bucketDelta)`.
- Produces: `void CalculateMotion(float vertical, float horizontal, float deltaTime, out Vector3 translation, out float yaw)`.

- [x] Write failing tests for simultaneous deltas, limit clamping, busy lockout, and root motion calculation.
- [x] Run focused tests and confirm missing APIs fail compilation.
- [x] Implement manual pose adjustment and W/S/A/D, Q/E, R/F, T/G, Y/H, 1 input handling.
- [x] Run controller and keyboard tests.

### Task 2: First/third-person camera

**Files:**
- Create: `Assets/Scripts/Zx200CameraController.cs`
- Create: `Assets/Tests/EditMode/Zx200CameraControllerTests.cs`

**Interfaces:**
- Produces: `void ToggleView()`, `void AddLookInput(float yawDelta, float pitchDelta)`, and `void AddZoomInput(float scrollDelta)`.

- [x] Write failing tests for view toggle, pitch clamping, and zoom limits.
- [x] Run focused tests and confirm the component is missing.
- [x] Implement C toggle, right-mouse look, first-person body-link following, third-person orbit, and wheel zoom.
- [x] Run camera tests.

### Task 3: Prefab wiring and end-to-end verification

**Files:**
- Modify: `Assets/Robotics/zx200_description/Editor/Zx200PrefabSetup.cs`
- Modify: `Assets/Robotics/UrdfUrpImporter/Editor/Tests/Zx200PrefabSetupIntegrationTests.cs`
- Modify generated asset: `Assets/Robotics/zx200_description/Prefabs/zx200.prefab`

**Interfaces:**
- Consumes the runtime components from Tasks 1 and 2.
- Produces one configured ZX200 prefab and scene instance.

- [x] Write a failing integration test asserting exactly one keyboard and camera controller with required references.
- [x] Configure components during prefab import and scene placement.
- [x] Regenerate the prefab in the isolated Unity project.
- [x] Run the complete EditMode suite and PlayMode checks for manual movement, manual visible joint motion, camera switching, and automatic digging.
