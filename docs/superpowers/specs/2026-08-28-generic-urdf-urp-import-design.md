# Generic URDF-to-URP Import Design

## Goal

Provide a repeatable Unity 6 editor command that imports any selected URDF, converts ordinary imported renderer materials to persistent URP/Lit assets, preserves their textures when available, and saves a reusable prefab without magenta rendering.

## Scope

- Input: one `.urdf` asset selected in the Unity Project window.
- Editor command: `Tools/Robotics/Import Selected URDF as URP`.
- Generated materials: a `Materials` directory beside the selected URDF package content.
- Generated prefab: a `Prefabs/<robot-name>.prefab` asset beside the selected URDF package content.
- Initial acceptance fixtures: HR35 and ZX200 under `Assets/Robotics`.

The tool only creates or updates assets inside the selected robot's package directory. It does not scan or convert unrelated project materials.

## Package Root Resolution

The tool derives a robot package directory from the selected URDF and its mesh references. It supports both layouts currently present in the project:

- HR35-style URI: `package://meshes/visual/boom.dae`, with the URDF at the package root.
- ZX200-style URI: `package://zx200_description/meshes/zx200-boom.dae`, with the URDF below `zx200_description/urdf`.

If the root cannot be resolved unambiguously, the command stops with a specific error rather than importing with missing meshes.

## Components

### URP Material Converter

Creates or updates persistent materials using `Universal Render Pipeline/Lit`. For each source material slot it preserves the source main texture when present, assigns that texture to `_BaseMap`, uses an opaque white base color, and disables alpha clipping and transparency. Slots sharing the same texture reuse one generated material.

When an imported material contains no readable texture, the converter searches the referenced DAE image path. For ZX200 this resolves to `meshes/bake.png`. If neither source material nor DAE provides a texture, the converter creates an opaque gray fallback material.

### Selected-URDF Import Tool

Validates the Project selection, resolves the package root, imports the selected URDF through Unity Robotics URDF Importer, applies the material converter, validates the hierarchy, and saves a prefab named after the URDF robot. Re-running the command updates existing generated assets rather than creating duplicates.

## Data Flow

1. User selects one `.urdf` asset and runs `Tools/Robotics/Import Selected URDF as URP`.
2. The tool validates the selection, mesh paths, URDF Importer, and URP/Lit shader.
3. The tool resolves the package root and imports a temporary robot hierarchy.
4. Every renderer material slot is mapped to a persistent URP/Lit material.
5. Texture selection follows this priority: imported source texture, DAE image reference, opaque gray fallback.
6. Validation confirms that renderers were converted and no generated material uses a missing or non-URP shader.
7. The hierarchy is saved as `Prefabs/<robot-name>.prefab` and the temporary hierarchy is destroyed.

## Error Handling

The command fails with a specific exception when the selection, URDF, mesh references, package root, URP/Lit shader, generated robot, renderers, or prefab cannot be found or created. It does not silently save a partially converted prefab. The temporary hierarchy is destroyed even when import or validation fails.

## Testing

Editor tests cover selection validation, both package URI layouts, textured and untextured material conversion, DAE texture fallback, material reuse, and renderer replacement. Integration tests import HR35 and ZX200, then verify that every prefab renderer uses URP/Lit. The ZX200 integration test additionally verifies that every visual material uses `meshes/bake.png`.

Final verification compiles the Unity project in batch mode, runs the relevant Editor tests, executes imports for both fixtures, and inspects the generated material and prefab references.

## Success Criteria

- The menu command is enabled only when exactly one `.urdf` asset is selected.
- Both existing HR35 and ZX200 URDF layouts resolve without a manual root-folder dialog.
- Generated materials use `Universal Render Pipeline/Lit`.
- Existing imported textures are preserved.
- ZX200 renderer Base Maps reference `meshes/bake.png`.
- Generated prefabs contain no null or unsupported renderer materials.
- Re-running the command is idempotent and does not create duplicate materials or prefabs.
