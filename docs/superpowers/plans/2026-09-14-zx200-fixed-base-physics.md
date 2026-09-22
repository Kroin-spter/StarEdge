# ZX200 Fixed-Base Physics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the unstable ZX200 visual-only control path with a fixed-base ArticulationBody configuration that has credible proxy mass ratios, solver-owned joint state, and adjacent-link collision filtering.

**Architecture:** A focused `Zx200FixedBasePhysics` component owns mass, gravity, root locking, validation, and collision exclusions. `Zx200DigController` owns drive targets and reads actual joint positions; `Zx200KeyboardController` explicitly suppresses root motion in fixed-base mode. The ZX200 editor postprocessor wires consistent defaults into the generated prefab.

**Tech Stack:** Unity 6000.5.10f1, C#, Unity ArticulationBody/PhysX, NUnit EditMode tests

## Global Constraints

- `track_link` remains fixed and its Transform is not changed during normal runtime control.
- Normal runtime control must not assign `ArticulationBody.jointPosition`.
- Moving-link masses are engineering proxies, not manufacturer-calibrated data.
- Adjacent links ignore each other while bucket-to-world collision stays enabled.
- Invalid or non-finite physics parameters prevent the controller from starting.
- The project directory is not a Git repository, so commit steps are unavailable.

---

### Task 1: Fixed-Base Physics Configuration

**Files:**
- Create: `Assets/Scripts/Zx200FixedBasePhysics.cs`
- Create: `Assets/Scripts/Zx200FixedBasePhysics.cs.meta`
- Create: `Assets/Tests/EditMode/Zx200FixedBasePhysicsTests.cs`
- Create: `Assets/Tests/EditMode/Zx200FixedBasePhysicsTests.cs.meta`

**Interfaces:**
- Consumes: a ZX200 hierarchy containing `track_link`, `body_link`, `boom_link`, `arm_link`, and `bucket_link` ArticulationBody components.
- Produces: `bool Configure()`, `bool ResolveBodies()`, `bool ValidateConfiguration(out string error)`, and `static bool IsFinitePositive(float value)`.

- [x] **Step 1: Write failing configuration and validation tests**

Create tests that build a five-link articulation hierarchy and assert:

```csharp
Assert.That(physics.Configure(), Is.True);
Assert.That(physics.rootBody.immovable, Is.True);
Assert.That(physics.swingBody.mass, Is.EqualTo(8000f));
Assert.That(physics.boomBody.mass, Is.EqualTo(2000f));
Assert.That(physics.armBody.mass, Is.EqualTo(1200f));
Assert.That(physics.bucketBody.mass, Is.EqualTo(800f));
Assert.That(physics.swingBody.useGravity, Is.True);
Assert.That(physics.ValidateConfiguration(out _), Is.True);
Assert.That(Zx200FixedBasePhysics.IsFinitePositive(float.NaN), Is.False);
```

Add colliders to adjacent links, call `Configure()`, and verify the component remains valid without disabling the bucket collider.

- [x] **Step 2: Run the focused tests and confirm they fail because the component is absent**

Run:

```bash
/home/sk/unity-editor/6000.5.10f1/Editor/Unity -batchmode -nographics -projectPath /home/sk/Unity/HMI_Unity6 -runTests -testPlatform EditMode -testFilter ExcavatorControls.Tests.Zx200FixedBasePhysicsTests -testResults /tmp/zx200-fixed-base-red.xml -logFile /tmp/zx200-fixed-base-red.log -quit
```

Expected: test compilation fails because `Zx200FixedBasePhysics` does not exist.

- [x] **Step 3: Implement the focused component**

Add serialized proxy defaults:

```csharp
public float swingMass = 8000f;
public float boomMass = 2000f;
public float armMass = 1200f;
public float bucketMass = 800f;
public float jointFriction = 0.05f;
```

`Configure()` resolves the named bodies, sets `rootBody.immovable = true`, enables gravity on moving bodies, assigns distinct masses, enables `automaticCenterOfMass` and `automaticInertiaTensor`, applies finite damping/friction, and calls `Physics.IgnoreCollision` for every collider pair on directly connected links. It must not disable any collider.

`ValidateConfiguration(out string error)` checks all five references, root immovability, positive finite proxy masses, positive finite calculated inertia components, and reports the first invalid field by name.

- [x] **Step 4: Run the focused tests and confirm they pass**

Use the Task 1 command with results path `/tmp/zx200-fixed-base-green.xml`. Expected: all `Zx200FixedBasePhysicsTests` pass.

### Task 2: Solver-Owned Joint Control

**Files:**
- Modify: `Assets/Scripts/Zx200DigController.cs`
- Modify: `Assets/Tests/EditMode/Zx200DigControllerTests.cs`

**Interfaces:**
- Consumes: configured revolute articulation joints and drive limits.
- Produces: `Zx200JointPose ReadActualPose()`, `bool IsPoseWithinTolerance(Zx200JointPose target, float toleranceDegrees)`, and drive-only pose commands.

- [x] **Step 1: Replace visual-stabilization tests with solver-state tests**

Delete the expectation that all colliders and gravity are disabled. Add tests asserting `PrepareForPhysics()` leaves colliders enabled, resolves the physics component, and updates drive targets without changing each joint's `jointPosition`.

Add pure tolerance coverage:

```csharp
Assert.That(Zx200DigController.IsAngleWithinTolerance(10f, 10.4f, 0.5f), Is.True);
Assert.That(Zx200DigController.IsAngleWithinTolerance(10f, 10.6f, 0.5f), Is.False);
```

- [x] **Step 2: Run the dig-controller test fixture and verify the new tests fail**

Run the Unity test command with filter `ExcavatorControls.Tests.Zx200DigControllerTests` and results path `/tmp/zx200-dig-red.xml`.

- [x] **Step 3: Convert the controller to drive-only physics control**

Rename the setup path to `PrepareForPhysics()`. Remove `stabilizeForAnimation` and `StabilizeForAnimation()`. `SetTarget` must only clamp and assign `joint.xDrive.target`.

Read actual degrees with:

```csharp
float degrees = joint.jointPosition[0] * Mathf.Rad2Deg;
```

Keep target interpolation for command smoothness, then wait for actual pose convergence using `jointToleranceDegrees` and `settleTimeoutSeconds`. On timeout, set `IsBusy = false`, log joint errors, and stop the cycle.

- [x] **Step 4: Run the dig-controller tests and confirm they pass**

Use results path `/tmp/zx200-dig-green.xml`. Expected: all `Zx200DigControllerTests` pass.

### Task 3: Suppress Fixed-Base Root Motion

**Files:**
- Modify: `Assets/Scripts/Zx200KeyboardController.cs`
- Modify: `Assets/Tests/EditMode/Zx200KeyboardControllerTests.cs`

**Interfaces:**
- Consumes: `public bool fixedBase` with default `true`.
- Produces: `MoveArticulationRoot(...) == false` with no pose change when fixed, while preserving the existing opt-in mobile behavior for future work.

- [x] **Step 1: Write the fixed-base root invariance test**

Set a non-zero track command, invoke `MoveArticulationRoot`, and assert:

```csharp
Assert.That(moved, Is.False);
Assert.That(articulationRoot.transform.position, Is.EqualTo(originalPosition));
Assert.That(articulationRoot.transform.rotation, Is.EqualTo(originalRotation));
```

- [x] **Step 2: Run the keyboard-controller fixture and confirm the test fails**

Run the Unity test command with filter `ExcavatorControls.Tests.Zx200KeyboardControllerTests` and results path `/tmp/zx200-keyboard-red.xml`.

- [x] **Step 3: Implement the fixed-base guard**

Default `fixedBase = true`. Return before initializing or writing a commanded root pose in `Update`, `LateUpdate`, `MoveArticulationRoot`, and `FollowGroundHeight`. Retain the old movement implementation only behind `fixedBase == false` for later track work.

- [x] **Step 4: Run the keyboard-controller tests and confirm they pass**

Use results path `/tmp/zx200-keyboard-green.xml`. Expected: all `Zx200KeyboardControllerTests` pass.

### Task 4: Import Defaults And Prefab Integration

**Files:**
- Modify: `Assets/Robotics/zx200_description/Editor/Zx200PrefabSetup.cs`
- Modify: `Assets/Robotics/UrdfUrpImporter/Editor/Tests/Zx200PrefabSetupIntegrationTests.cs`
- Modify: `Assets/Robotics/zx200_description/urdf/zx200.xacro`
- Modify: `Assets/Robotics/zx200_description/urdf/zx200.urdf`
- Regenerate: `Assets/Robotics/zx200_description/Prefabs/zx200.prefab`

**Interfaces:**
- Consumes: `Zx200FixedBasePhysics.Configure()` and `Zx200DigController.PrepareForPhysics()`.
- Produces: an idempotently configured prefab with one physics component, fixed root, proxy masses, enabled colliders, and fixed-base keyboard control.

- [x] **Step 1: Extend integration tests for physical defaults**

Invoke `ConfigurePrefab` twice and assert one `Zx200FixedBasePhysics` exists, the root is immovable, moving-body masses match the configured proxies, moving links use gravity, colliders remain enabled, `fixedBase` is true, and the old `stabilizeForAnimation` field is absent.

- [x] **Step 2: Run the integration fixture and confirm it fails**

Run the Unity test command with filter `UrdfUrpImporter.Tests.Zx200PrefabSetupIntegrationTests` and results path `/tmp/zx200-prefab-red.xml`.

- [x] **Step 3: Update source defaults and postprocessing**

Replace the repeated placeholder inertials in both URDF sources with documented proxy mass/inertia values so future reimports do not restore `0.03 kg` links. Configure the new component and call `Configure()` from `Zx200PrefabSetup`; set `keyboard.fixedBase = true`; use `PrepareForPhysics()` when validating the prefab.

For importer compatibility, use positive diagonal engineering-proxy tensors and keep cross terms at zero. Runtime automatic tensors remain authoritative for the generated prefab.

- [x] **Step 4: Regenerate and test the prefab**

Run the integration fixture again with results path `/tmp/zx200-prefab-green.xml`. Expected: all integration tests pass and the generated prefab contains the physical defaults.

### Task 5: Full Verification

**Files:**
- Modify only if a verified defect is found in files already listed above.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: compile/test evidence and a static check that forbidden runtime writes are absent.

- [x] **Step 1: Run all relevant EditMode tests**

```bash
/home/sk/unity-editor/6000.5.10f1/Editor/Unity -batchmode -nographics -projectPath /home/sk/Unity/HMI_Unity6 -runTests -testPlatform EditMode -testFilter 'ExcavatorControls.Tests;UrdfUrpImporter.Tests.Zx200PrefabSetupIntegrationTests' -testResults /tmp/zx200-physics-all.xml -logFile /tmp/zx200-physics-all.log -quit
```

Expected: zero failed tests and no C# compilation errors.

- [x] **Step 2: Check forbidden writes and serialized state**

```bash
rg -n 'jointPosition\s*=|SetPositionAndRotation|transform\.(position|rotation)\s*=' Assets/Scripts/Zx200DigController.cs Assets/Scripts/Zx200KeyboardController.cs
rg -n 'm_Immovable: 1|m_UseGravity: 1|m_Mass:' Assets/Robotics/zx200_description/Prefabs/zx200.prefab
```

Expected: no joint-position write; root transform writes exist only inside the `fixedBase == false` branch; prefab root is immovable and moving links use gravity with distinct masses.

- [x] **Step 3: Report manual PlayMode validation still required**

Record that visual jitter, obstacle contact, and long-cycle stability require opening `Assets/Scenes/SampleScene.unity` in the Unity editor on a graphics-capable session. Do not claim these visual acceptance checks passed from EditMode tests alone.
