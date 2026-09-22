# ZX200 Dig Cycle Design

## Goal

Add a runtime ZX200 digging cycle driven by URDF-imported `ArticulationBody` joints. Entering Play mode moves the excavator into a normal work-ready pose; pressing Space once plays one complete dig, lift, swing, dump, return, and reset cycle.

## Scope

- Runtime component: `Zx200DigController`.
- Target prefab: `Assets/Robotics/zx200_description/Prefabs/zx200.prefab`.
- Controlled links: `body_link`, `boom_link`, `arm_link`, and `bucket_link`.
- Input: one Space key press starts one cycle; input is ignored while a cycle is active.
- Soil deformation, hydraulic simulation, and track movement are outside scope.

## Joint Control

The controller finds each link's `ArticulationBody` by name and updates `xDrive.target`; it never rotates visual transforms directly. Every requested target is clamped to the drive's imported lower and upper limits. Drive stiffness, damping, and force limit are configurable and applied before motion starts.

Default work-ready targets are:

- Swing: `0°`
- Boom: `-35°`
- Arm: `75°`
- Bucket: `20°`

These values are inside the current ZX200 imported limits and replace the invalid zero targets on boom and arm.

## Motion Sequence

1. On startup, blend from current drive targets to the work-ready pose.
2. Wait for Space.
3. Approach: lower the boom and extend the arm toward the ground.
4. Curl: close the bucket while slightly adjusting the arm.
5. Lift: raise the boom with the bucket held closed.
6. Swing: rotate the upper body toward the dump side.
7. Dump: open the bucket.
8. Return: swing back and restore the work-ready boom, arm, and bucket targets.

Each phase stores swing, boom, arm, and bucket targets plus a duration. Targets and durations are serialized so the motion can be tuned in the Inspector. Interpolation uses smoothstep progression and updates all four drives together.

## Prefab Integration

An editor setup command adds or updates `Zx200DigController` on the ZX200 prefab root without duplicating it. It assigns the four articulation references and the default drive/motion settings. The generic URDF-to-URP importer invokes this setup for the ZX200 robot after saving its prefab, so re-importing preserves the behavior.

## Error Handling

The controller disables itself and reports a specific error if any required link or `ArticulationBody` is missing. It ignores another Space press during startup or an active cycle. Zero or negative phase durations complete immediately without division by zero.

## Testing

EditMode tests cover link resolution, target clamping, smooth interpolation, work-ready defaults, and idempotent prefab setup. A PlayMode-compatible controller test advances the public motion API without relying on keyboard input. Integration verification checks that the generated ZX200 prefab contains exactly one enabled controller with all four articulation references assigned.

## Success Criteria

- Entering Play mode moves ZX200 into the configured work-ready pose.
- Boom and arm initial targets are within their imported limits.
- One Space press starts exactly one complete cycle.
- Further Space presses are ignored until the cycle finishes.
- Motion uses `ArticulationBody.xDrive`, not Transform rotation.
- The cycle returns to the same work-ready pose.
- Re-importing ZX200 leaves exactly one configured controller on the prefab.
