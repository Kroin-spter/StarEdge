# Truck loading and cutting constraints

User authorized completing truck integration in the main scene and simplified cutting resistance/penetration constraints.

- [x] Create a reusable parked dump truck prefab: cab, chassis, wheels, bed floor and four solid walls; receiver matches interior and volume limit. Existing project contains no truck mesh asset. This is a functional procedural model, not a manufacturer asset.
- [x] Integrate local soil, truck and cutting feedback into the saved main Cesium scene `Assets/Scenes/sence_test.unity` in an isolated project. Preserve main editor unsaved state and back up the on-disk scene before delivery. Handle local map overlap where needed to validate operation.
- [x] Add a cutting interaction component exposing penetration, resistance force, blocked status/reason. Apply bounded force against cutting motion; constrain drive targets into remaining terrain when full/deep/invalid cutting, allow withdrawal. Do not teleport articulation transforms or use position writes during operation.
- [x] Validate actual joint digging, retaining/carrying, truck reception and all soil destinations. Verify blocked inward commands and allowed withdrawal. Run regressions and render the integrated scene.
- [x] Review and synchronize verified code/assets/scene with backup and usage report.

Work in `/home/sk/Unity/soil-validation-20260916`. Unity 6000.5.10f1; serialize Unity processes, use `DOTNET_USE_POLLING_FILE_WATCHER=1`. Keep Cesium georeference and selected map. No new Web export.

Verified: 111 EditMode, 11 PlayMode regression, and 2 final main-scene focused checks passed. Delivered scoped files with byte equality checks; details in docs/truck-site-20260917/README.md. Human keyboard acceptance remains for the user; automated tests exercise the manual control API.
