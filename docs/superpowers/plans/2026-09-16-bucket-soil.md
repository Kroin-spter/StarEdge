# Bucket Soil Implementation Plan

Goal: Complete the approved first phase: bounded bucket fill, mouth-limited
cutting, conserved soil during lifting and rotation, and a usable Terrain demo.

Architecture: BucketSoilCutter remains the sole owner of collected volume.
DeformableSoilTerrain integrates actual height changes. A bucket-local mesh
uses a calibrated cavity profile and volume-driven fill level, without rigidbody
particles. No volume is discarded on tilt; unloading and receiving are a later
phase and must introduce a destination before removing collected soil.

Constraints: Unity 6000.5.10f1, existing ArticulationBody controls, retain source
Terrain assets, retain legacy CutSweep API, no changes to user's open scene or
machine joint tuning. Work and tests run in an isolated project copy because
the project is not a Git repository.

## Tasks

- [x] Add failing tests for bucket-mouth clipping, volume conservation and
      bounded closed fill meshes at 10%, 50%, and 100% capacity.
- [x] Add an optional oriented mouth-width mask to Terrain sweeps. Clip the
      actual cut before calculating soil volume; reject sideways travel using
      the existing cutting direction gate. Preserve legacy callers.
- [x] Add BucketSoilFill with a bucket-local cavity profile, computed fill
      surface and closed mesh. Empty buckets hide the mesh; capacity clamps the
      display. Mesh updates cannot modify measured volume or add physics bodies.
- [x] Integrate the cutter and setup tool, migrating the old visual cube in
      the standalone demo without editing user's Cesium scene.
- [x] Run EditMode conservation and mesh tests, PlayMode actual machine tests,
      and render inspection of the cavity and different fill levels.
- [x] Copy verified scoped changes and a new ready-to-open demo to the main
      project; document startup and current unloading limitation.
