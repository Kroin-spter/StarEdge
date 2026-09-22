# ZX200 Fixed-Base Physics Design

Date: 2026-09-14

## Objective

Convert the existing ZX200 visual animation into a stable fixed-base physics prototype. The track assembly remains fixed while the swing body, boom, arm, and bucket are simulated by Unity ArticulationBody physics.

This version supports later terrain cutting and payload work. It does not model track locomotion, hydraulic pressure, whole-machine stability, or manufacturer-accurate dynamics.

## Existing Problems

- The URDF assigns `0.03 kg` and the same `1, 1, 1 kg m2` inertia tensor to every main link. These are placeholder values, not credible ZX200 physical properties.
- Runtime control writes both `ArticulationDrive.target` and `jointPosition`, bypassing the solver while also asking it to solve the joint.
- The keyboard controller writes the articulation root Transform every frame.
- Imported adjacent-link colliders have no collision exclusions and can push connected links apart.
- Existing stabilization disables gravity and every collider, so it cannot be reused as physical behavior.

## Architecture

### Fixed Base

`track_link` is the articulation root and remains `immovable`. Fixed-base mode ignores track movement input, ground-following raycasts, and all runtime root Transform writes.

### Link Properties

The moving links use configurable engineering-proxy masses with distinct values for the swing body, boom, arm, and bucket. Unity calculates center of mass and inertia from the imported convex colliders. Startup validation rejects non-positive or non-finite mass and inertia values.

The proxy values are stability-oriented and preserve reasonable relative mass ordering. They are not identified as manufacturer data.

### Joint Drives

The controller configures finite stiffness, damping, force limits, and joint friction. Commands update only `ArticulationDrive.target`. Runtime code never assigns `jointPosition` or a child link Transform.

The current pose comes from the articulation joint state. Automated stages finish when every commanded joint is within a configurable angular tolerance. A stage timeout stops the cycle and identifies the joints that did not settle.

An immediate pose operation may be retained only for explicit reset before simulation begins; it is not part of normal driving or digging.

### Collision Filtering

Colliders belonging to directly connected links ignore each other:

- track and swing body
- swing body and boom
- boom and arm
- arm and bucket
- bucket and the bucket-end marker

Non-adjacent self-collision remains enabled initially. Bucket collision with terrain, vehicles, and external obstacles remains enabled. If runtime diagnostics identify a non-adjacent initial overlap, that exact pair will be excluded rather than disabling all self-collision.

## Control Flow

1. Resolve the articulation root and four controlled joints.
2. Apply and validate the fixed-base physics configuration.
3. Register adjacent-link collision exclusions.
4. Send smooth drive targets for manual or automatic commands.
5. Read actual joint positions to determine progress.
6. Stop and report a timeout or invalid physics state instead of continuing an unstable cycle.

## Diagnostics

At startup, the controller reports the selected physics mode and rejects missing joints, invalid mass values, invalid inertia values, and a non-immovable root.

The first verification separates two cases:

1. Free-space settling and motion with external collision disabled.
2. Terrain and obstacle contact with external collision enabled.

This separation distinguishes drive or inertia instability from contact instability.

## Tests And Acceptance

- Edit-mode tests cover target clamping, angular tolerance, timeout decisions, and physics-parameter validation.
- A fixed-base play-mode diagnostic confirms the root pose remains unchanged while joint targets move.
- The excavator remains still at rest without visible jitter or continuously growing velocity.
- Repeated empty-bucket cycles do not produce non-finite state or explosive movement.
- A bucket contacting a fixed obstacle is resisted without moving the track root.
- No normal control path assigns articulation `jointPosition` or the root Transform.

## Deferred Work

- Track locomotion and ground-contact suspension
- Manufacturer-calibrated masses, centers of mass, and inertia tensors
- Hydraulic actuator and pressure modeling
- Soil reaction-force calibration
- Full-machine stability and tipping analysis
