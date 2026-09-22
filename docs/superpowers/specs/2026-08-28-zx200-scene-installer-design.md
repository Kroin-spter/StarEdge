# ZX200 Scene Installer Design

## Goal

Provide one reliable Unity menu command that places the generated ZX200 prefab into the active scene, validates its digging controller, and makes Space-triggered motion observable and diagnosable.

## Confirmed behavior

- Add `Tools > ZX200 > Add To Current Scene`.
- Instantiate only `Assets/Robotics/zx200_description/Prefabs/zx200.prefab`.
- Do not delete or modify existing excavators in the scene.
- Ensure exactly one enabled `Zx200DigController` exists on the new instance and all four joint references resolve.
- Correct the imported continuous swing joint so its revolute degree of freedom is not locked and its drive has a usable angular range.
- Stabilize the articulation for visual animation.
- Select the new instance and mark the scene dirty.
- Emit concise runtime logs when the controller becomes ready, receives Space, starts a cycle, or rejects a request.

## Error handling

The menu command must stop with a clear exception if the prefab is missing or required articulation links cannot be resolved. Runtime validation must disable the controller and identify missing joints instead of silently doing nothing.

## Verification

EditMode tests cover prefab instantiation/configuration, continuous swing configuration, drive targets, and input-independent cycle start. A Unity batch test run must pass before completion. The user-facing manual check is: create/open any scene, run the menu command, enter Play mode, focus Game view, and press Space once.
