# 84 m Baked Terrain Extension Design

## Goal

Expand the current baked map footprint to 84 m × 84 m around its existing center while preserving the current 28 m × 28 m baked core, excavation behavior, equipment placement, and camera setup. Bake only the surrounding ground, and give the new map image a 4096 × 4096 output resolution.

## Existing baseline

The active baked site is `Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset`. Its work-face center is `(320.28, 351.43)` in world X/Z, and its work-face size is 28 m × 28 m. The current core Terrain starts at `(306.28, -8, 337.43)`. Its existing irregular Cesium replacement polygon, soil data, TerrainData, machine/truck positions, and scene camera transforms are part of the baseline and remain unchanged.

The local `osgb_small_005_004` source contains 318 GLB tiles across LOD levels 17–21. Sixteen L20/L21 tile bounds intersect the proposed 84 m footprint, and their embedded maps include 1024 × 1024 textures. This supports a 4K offline capture, subject to checking the actual rendered coverage and detail before saving.

The expanded footprint is centered at the same X/Z point:

- Overall bounds: X `[278.28, 362.28]`, Z `[309.43, 393.43]`.
- Preserved core bounds: X `[306.28, 334.28]`, Z `[337.43, 365.43]`.
- New coverage: the surrounding ring between those bounds and the 84 m outer bounds.

## Design

Create extension assets separately from the active core asset. The extension is static exterior ground for appearance and collision; it does not add to the cuttable soil ledger. Sample its height field from the loaded Cesium surface, align its inner edge against the existing core boundary, and align its outer edge against the Cesium surface. The original core continues to own its existing work-face replacement and excavation.

Represent the ring with one new 84 m × 84 m Unity Terrain and TerrainCollider. Its center is a hole aligned to the existing 28 m × 28 m core, leaving the current Terrain as the visible and collidable surface there. Use a 1025 × 1025 height field (about 8.2 cm between samples); if Terrain hole texel alignment requires a small overlap, keep it underneath the original Terrain and align its height to the original edge so it cannot create a visible or collidable step.

Add Cesium clipping for the four outer ring regions while keeping the existing irregular core polygon intact. Use the existing single `ConstructionSiteMapClip` lifecycle for both the original footprint and the added regions so tile mesh and matching collider edits are restored together when Cesium unloads tiles. Do not add a second clipper to the same tileset.

Capture one orthographic 4096 × 4096 map image for the full 84 m footprint during an editor bake. Save it as an extension asset and use it for the added ground with world-aligned texture coordinates. The bake must request detailed Cesium imagery over the complete footprint and reject incomplete coverage rather than stretching a small runtime screenshot over the larger area. The 4K output sets the texture resolution; visible sharpness is limited by the imagery Cesium supplies, so the bake must report when source imagery cannot support the requested detail.

The extension bake is repeatable and writes only into a dedicated extension asset folder. It must not rewrite the existing `ConstructionSite.asset`, its referenced TerrainData/material, or scene equipment and camera transforms. Normal play uses the saved texture and Terrain data; it does not capture a map image at runtime.

## Bake and runtime flow

1. The editor bake reads the approved center and footprint dimensions from the active baked site, requests high-detail Cesium coverage, and samples the outer height field.
2. It creates new Terrain and map-image assets under a separate 84 m extension folder, checks the core boundary and full image coverage, and records the extension bounds.
3. The scene loads the saved extension ground alongside the original baked core. The core remains the only cuttable baked soil.
4. The existing map clipper removes Cesium geometry and its matching collision only in the original work-face polygon and the four extension ring regions.

## Failure handling

The bake must stop with a clear error if Cesium tiles do not cover the outer footprint, if any required map area is missing, or if sampled heights cannot form a continuous surface. It must not save a partially filled image or silently fall back to a low-resolution runtime capture. If the saved extension assets do not match the active baked site's center or size, startup must leave the extension disabled and report the mismatch.

## Acceptance checks

- The active core baked asset and TerrainData remain byte-for-byte unchanged by the extension bake.
- The original soil volume, work-face polygon, machine and truck transforms, and camera transforms are unchanged.
- The saved extension covers the full surrounding ring, extending 28 m outward from each side of the preserved 28 m core; the core remains the visible and collidable surface in its bounds.
- The outer Terrain boundary follows Cesium and the inner boundary meets the existing Terrain without a visible or collidable gap.
- Cesium rendering and collision remain outside the combined replacement regions; matching mesh colliders are clipped and restored with their tiles.
- The saved map image is 4096 × 4096 and has complete Cesium coverage; its imported maximum size preserves 4096 resolution.
- The extension is static ground and does not change excavation volume or soil accounting.

## Alternatives considered

- Replacing the full 84 m core Terrain would change the user's existing excavation surface and risk repeating the earlier poor replacement result, so it is excluded.
- A texture-only overlay would not provide the new terrain collision and would leave the surrounding surface dependent on Cesium geometry, so it does not meet the extension goal.
- Multiple independent clippers on one Cesium tileset create conflicting ownership of mutable streamed meshes; the existing clipper will own the additional regions instead.

## Scope

This change covers the baked extension, its saved high-resolution image, scene integration, and the Cesium clip regions needed to show the extension. It does not alter excavation controls, haul-truck placement, the active core bake, Cesium source data, or camera framing.
