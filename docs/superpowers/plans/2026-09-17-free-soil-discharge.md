# Free soil discharge implementation plan

**Goal:** Discharge according to bucket pose anywhere, level soil against gravity, and deposit soil on terrain or ground without losing accounting.

**Approved design:** User approved the preceding proposal with “开始实施”. Use lightweight volume packets, not individual physical grains. Preserve existing machine tuning and user scenes. Work in the existing isolated soil-validation-20260916 project and deliver a new independent demo.

## Tasks
- [x] Add Terrain deposition API returning actual quantized added volume. Test source preservation, cut/deposit/recut net-volume accounting, limits and reset.
- [x] Extend bucket fill to gravity-oriented cavity clipping. Verify geometric volume and containment after pitch/roll; use a smoothed surface and pose-dependent retained capacity.
- [x] Replace receiver-gated unloading with pose-gated emission. Track airborne packets separately; integrate gravity and segment collisions, settle into receiver, same source terrain, or independent ground pile. Preserve residue and out-of-map material in accounting.
- [x] Coordinate reset across packets, receiver/pile contributions, terrain and bucket counters. Distinguish gross excavated and discharged totals from terrain net removal.
- [x] Add Demo 3 and verify actual joint digging, free discharge onto terrain and platform unloading. 107 EditMode and 8 PlayMode tests passed; screenshots inspected. Receiver-status check separately passed; verified files, Demo 3 and results synchronized to main.

## Interfaces
`DeformableSoilTerrain.Deposit(Vector3 worldPoint, float radius, double volumeM3)` returns actual added m³, never greater than requested. Heights use runtime clone and unchanged outer rim. `RecalculateRemovedVolumeM3` remains signed net removal.

`BucketSoilFill.SetSurfaceNormal(Vector3 localUp)` changes clipping plane normal without owning volume. `RetainedCapacityM3(Vector3 localUp)` clips below the lowest mouth boundary. Animation only changes display, not inventory.

Airborne transport owns emitted volume until impact. Cutter owns bucket inventory, gross cut/discharge counters and landed totals. One cuttable terrain per source; foreign terrain receives an independent pile to avoid cross-source reset corruption. Missing ground does not destroy packets. Terrain quantization residue remains a tracked surface parcel and can be retried/merged.

## Review ledger
- Terrain deposition and initial gravity clipping reviewed independently.
- Added nonconvex profile containment tests; real partial-fill failures fixed with ear-clipped faces, retaining the calibrated profile.
- Added mixed off-map/terrain test; actual failure fixed with per-deposit idle-time settlement. Scoped re-review approved; all 107 EditMode tests pass.
- Runtime terrain deposits batch at 0.01 m³; final sub-quantum remainder becomes an explicitly accounted loose-soil pile.
- Existing prefab/controller match the main project; no machine tuning or user scene changed.

## Verification
Run Unity 6000.5.10f1 with `DOTNET_USE_POLLING_FILE_WATCHER=1`, `-runTests` without `-quit`. Unit tests cover mesh integration, gravity/pose gates, in-flight delay, receivers, obstruction, ground piles, terrain deposition/re-excavation, conservation and reset. Actual articulation tests use the delivered demo; no Web build in this task.
