# Excavator appearance upgrade

**Goal:** Make the existing ZX200 visibly less synthetic with differentiated surfaces, attached mechanical detail and optional showcase depth of field.

**Approved architecture:** Keep the URDF/articulation/collision hierarchy; replace only Visuals render materials with owned weathered PBR materials and attach collider-free detail meshes under existing links. Texture colors/UVs remain intact. A separate F9 showcase mode focuses on the machine and blurs distant map detail; normal operation stays sharp.

**Constraints:** Unity branch `fix/manual-soil-yield-overflow`. Preserve user terrain/settings edits and earlier manual-soil work. No replacement of the robot, no new collision or rigidbody components, no external model downloads, and no frontend publish.

## Implementation

- [x] Inspect actual visual bounds/materials and source texture; define link-local placement for added details.
- [x] Add a referenced URP forward-only weathered-equipment shader with stable link-space detail, differentiated paint/steel roughness, restrained dirt and wear. Include compatible shadow/depth/AO passes and current URP reflection-atlas variants.
- [x] Add a reversible appearance component, cached owned materials, collider-free joint hardware/hoses and restoration/cleanup tests. Attach to the saved scene robot.
- [x] Add F9 showcase Bokeh DOF, with Gaussian available as the cheaper alternative; machine focus and profile asset retain build variants. F8 also compares model appearance.
- [x] Capture identical machine/camera baseline, upgraded and showcase views; inspect actual shader output and record local synchronized render timings.
- [x] Run lifecycle/component tests, manual excavation and three automatic loads; review and prepare the upgrade-only commit on the Unity branch.

## Acceptance

Visible paint/steel differentiation, mechanical detail that follows its own link, no shader errors or pink materials, no added physics bodies/colliders, original material restoration on disable, no repeated-toggle leaks, and completed excavation/loading with upgrades active. Scene starts upgraded with DOF off; F9 enters showcase. Local screenshot timings do not establish browser FPS.

## Verification and rulings

- `Diagnostics/equipment-operation.xml`: 3/3 passed. Three loads delivered 0.71967 m³; manual cutting removed 0.80504 m³, retained 0.17382 m³ and withdrew 2.944 m. Subsequent changes are rendering passes and reflection lifecycle handling, not motion/soil behavior.
- `Diagnostics/equipment-final.xml`: 7/7 appearance, DOF, restoration and cleanup tests passed on the final runtime source. `equipment-original.png`, `equipment-upgraded.png`, `equipment-showcase.png` capture a frozen subject and identical close camera.
- Reviewed and corrected incompatible borrowed Lit pass constant buffers by sharing one material layout across dedicated shadow/depth/normal passes. Enabled body-only clear-coat variants and retained runtime variants with `multi_compile_local_fragment`.
- Interrupted capture regression was RED before lifecycle correction. Native captures also require the realtime-reflection quality option. The component now respects that option: with the current disabled setting, it uses existing sky/environment reflection; with it enabled, one 128-resolution capture is created and can resume after deactivation. Project quality assets were not changed.
- Bokeh intentionally permits out-of-focus foreground/background surfaces in showcase; normal operation defaults to DOF off and close cabin views automatically clear. Gaussian protects the subject with a far-blur allowance.
- Local forced-render measurements include GPU synchronization and shared-machine variation; no browser FPS gain is claimed. No frontend export or Electron packaging was performed.
