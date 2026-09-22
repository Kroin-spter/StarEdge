# ZX200 Keyboard and Camera Controls Design

## Goal

Add game-style keyboard driving, manual excavator joint control, and switchable first/third-person cameras to ZX200 while retaining the Space-triggered automatic digging cycle.

## Components

- `Zx200KeyboardController` moves and steers the excavator root, follows terrain height, and sends manual joint increments to `Zx200DigController`.
- `Zx200CameraController` owns one camera, switches view with C, supports right-mouse orbit in both views, and supports wheel zoom in third person.
- `Zx200DigController` remains the authority for joint limits, current poses, deterministic `jointPosition` animation, and the automatic cycle.
- `Zx200PrefabSetup` configures all three components whenever the ZX200 prefab is generated or added to a scene.

## Keyboard mapping

| Input | Behavior |
|---|---|
| W / S | Drive forward / backward |
| A / D | Turn tracks left / right |
| Q / E | Swing upper body left / right |
| R / F | Raise / lower boom |
| T / G | Extend / retract arm |
| Y / H | Open / curl bucket |
| Space | Run one complete automatic dig cycle |
| C | Toggle first/third person |
| Hold right mouse | Look/orbit |
| Mouse wheel | Third-person zoom |
| 1 | Return to calibrated work-ready pose |

## Interaction rules

- Driving and camera input remain available during the automatic cycle.
- Manual joint keys and pose reset are ignored while `Zx200DigController.IsBusy` is true.
- Multiple manual inputs may be applied in the same frame.
- All manual targets are clamped to URDF drive limits.
- First person follows `body_link`, so cabin swing affects the viewpoint.
- Third person orbits the whole machine with clamped pitch and distance.
- Root movement remains transform-authoritative; existing stabilized articulation settings prevent gravity and collision instability.
- Ground following changes root height only after a downward raycast hit.

## Error handling

Controllers resolve required references by URDF link names. Missing joints disable manual joint input with a clear error; a missing camera falls back to `Camera.main`, then logs a warning and skips camera updates if none exists.

## Verification

EditMode tests cover movement calculation, simultaneous manual joint changes, joint-limit clamping, automatic-cycle lockout, view toggling, orbit/pitch clamping, and zoom limits. PlayMode tests instantiate the final prefab and verify visible manual motion, root driving, automatic digging, and camera switching.
