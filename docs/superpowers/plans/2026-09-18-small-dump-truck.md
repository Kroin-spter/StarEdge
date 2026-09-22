# Small dump truck integration

Goal: Replace the parked primitive truck with the user-selected textured FBX, preserving receiver references and soil transfer behavior.

User approved using this model following the proposed visual + simplified collision + existing receiving/measurement approach. Main project has an open editor; use the existing isolated validation project. No git repository, no commit.

- [x] Inspect imported FBX bounds, orientation and bed geometry. Use original supplied textures with URP materials; retain CC BY attribution.
- [x] Modify existing ParkedDumpTruck prefab while preserving root and SoilReceivingZone identities. Fit simple bed colliders and unit-scale receiver to visible interior. Keep runtime ground alignment.
- [x] Validate prefab references, textures, floor/wall containment; run main-scene loading test and capture preview.
- [x] Back up and synchronize only verified assets/scripts/tests to main project. Do not rebuild WebGL in this task.

Validation limitation: main-scene receiving succeeded, final soil-ledger assertion still fails by about 0.51 litres; not marked fully passing.
