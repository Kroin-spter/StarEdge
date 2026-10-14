# Natural mine visual preset

**Goal:** Add a reversible, lightweight natural-mine presentation to the saved excavation scene.

**Approved design:** Mild color grading and highlight compression, FXAA, softer sunlight shadows, and a visible on/off control. Preserve orange/yellow machine identification and cutting-edge readability. Ambient occlusion is optional only if runtime measurements justify it. Keep depth of field, motion blur and heavy bloom disabled.

**Architecture:** A camera-local runtime component owns a temporary URP Volume/profile and snapshots the camera/light settings it changes. Disabling the preset or component restores the snapshot. A separate rendering assembly avoids adding URP dependencies to excavation controls. Scene references select the main camera and directional light explicitly.

**Scope:** Existing Unity branch `fix/manual-soil-yield-overflow`. Preserve all current excavation work and user-owned asset/settings changes. No frontend packaging or merge requested for this visual task.

## Steps

- [x] Add the rendering assembly and lifecycle tests covering on/off restoration and repeated toggles without duplicate volumes.
- [x] Implement `MineVisualPreset.SetEnhanced(bool)`, owned-volume cleanup, mild tuning and F8/on-screen toggle.
- [x] Attach the component to `Assets/Scenes/sence_test.unity` with explicit sunlight reference.
- [x] Capture baseline/enhanced images from the same frozen camera/site; alternate forced 1280x900 renders with GPU synchronization after warmup. HUD remains outside camera post-processing.
- [x] Review lifecycle/resource handling and validate saved-scene manual cutting and continuous loading with the preset active.

## Acceptance

The saved scene starts enhanced in Play, the toggle restores original presentation, and repeated toggles do not leak volumes/materials. A/B screenshots must visibly retain machine colors and improve readability without obscuring cutting. Report measured costs for the local renderer; browser target performance is only claimed if an actual browser build is measured.

## Results

Standalone visual tests passed 2/2 (`Diagnostics/mine-visuals-reviewed.xml`), including repeated toggles and restoration of settings changed while off. Review found stale baseline snapshots; corrected and covered by the lifecycle test. Subsequent Inspector tuning is reapplied without duplicate profile components.

Operation regression passed 3/3 (`Diagnostics/mine-visuals-operation.xml`): three loads delivered 0.70746 m³; manual cutting excavated 0.89023 m³, retained 0.24843 m³ and withdrew 2.844 m. These tests use the preset active in the saved scene. Final delivery tests also cover edited tuning values on reenable.

Screenshots: `Diagnostics/mine-original.png`, `Diagnostics/mine-natural.png`. Local synchronized-render timings varied between runs around 2–3 ms/render and cannot establish a browser FPS gain or a precise incremental GPU cost. No extra AO pass was added; the existing PC renderer's AO is retained. Browser packaging was not requested in this turn.
