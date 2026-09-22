# Terrain Cutting and Bucket Retention

Open this scene by itself, then enter Play Mode:

`Assets/Robotics/zx200_description/Demos/SoilCutting/Demo 1/ZX200SoilDemo.unity`

Focus Game view. F lowers the boom into the soil; R lifts it. T/G moves the
stick, Y/H curls/opens the bucket, and Q/E rotates the upper structure.
Backspace resets both the terrain and collected soil volume. The demo fixes
the vehicle base for manual cutting trials; it does not modify the source robot
prefab or the Cesium scene.

## Behavior

- Terrain height changes are clipped to the mouth width before accounting.
- Predominantly sideways scraping is rejected, as are reverse travel, upward
  lifting, teleportation, cutting below the floor, and cuts beyond capacity.
- The cutter owns collected volume. A closed mesh displays exactly that volume
  in a calibrated bucket-local cavity, filling from the deep interior outward.
- The display adds no rigidbodies or colliders. Collected soil remains attached
  during lifting and rotation. This phase has no spill or unloading operation,
  including when the bucket is inverted; receiving/accounting must be added
  before allowing material to leave the bucket.
- Effective capacity is the smaller of configured capacity and cavity volume.
  The conservative ZX200 cavity preset is approximately 0.304 cubic metres at
  unit scale. This is a visual/collision calibration, not manufacturer capacity.
  Do not resize the robot or change the cavity while carrying soil.
- Legacy demo fill cubes are migrated at runtime without changing saved scenes.
  Newly generated demos share a soil texture between the Terrain and bucket.

## Validation

Unity 6000.5.10f1, isolated project copy, 2026-09-16:

- 84 EditMode tests passed (`edit-tests.xml`).
- 5 selected PlayMode tests passed (`play-tests.xml`), including actual cutting,
  lifting and rotation in both old and new demo scenes, and existing bucket
  collision tests.
- Tests independently integrate changed Terrain heights and closed mesh volume.
- Fill vertices and triangle centers were checked against actual bucket shell
  colliders. Ten-percent, half-full and full meshes were rendered for inspection.

Images: `fill-010.png`, `fill-050.png`, `fill-100.png`.

No Web build was regenerated in this change. The existing Cesium Web package
does not yet include this new soil demo. Soil resistance, physical load mass,
unloading, truck reception and automatic digging remain separate work.

Previous source files are backed up at `/home/sk/Unity/soil-backup-20260916`.
