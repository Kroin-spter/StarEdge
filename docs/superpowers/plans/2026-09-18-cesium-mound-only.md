# Cesium ground with a local soil mound

Approved scope: remove the large work pad and projected map cover. Retain original Cesium rendering and collisions. Only the above-ground mound uses Terrain, with an irregular hole mask outside its footprint. Preserve manual driving, cutting, lifting and loading.

- [x] Inspect intact map elevation and choose supported nearby equipment/mound/truck positions.
- [x] Add a sampled per-vertex excavation floor and tests; never cut below original map ground.
- [x] Prepare mound above sampled ground with tapered edges and Terrain holes outside; no large map clipping.
- [x] Gate startup on streamed support, settle equipment onto map, retain timeout failure state.
- [x] Verify rendered main scene, mobile digging, truck loading and accounting; update old platform-specific assertions.
- [x] Back up and compare main files, sync only scoped changes, preserve open editor state and document limits.

Implementation uses the existing isolated validation project. Unity runs serialized by root; floor implementation is delegated independently. No Web rebuild and no DEM simulation.

Validation: 149 EditMode tests passed (/tmp/mound-edit.xml), and 5 rendered PlayMode tests passed (/tmp/mound-main2.xml). Startup removed volume was zero. Driving traveled 0.397 m with six support contacts; scoop/lift completed on the dynamic base. Truck run: cut 0.085600 m3, received 0.081716 m3, returned 0.003512 m3, ground deposit 0.000372 m3, bucket/airborne/pending zero at completion (rounded values).

Delivered scoped files match the validated copy byte for byte. Previous main files are backed up in /home/sk/Unity/mound-only-backup-20260918; delivery-files.txt lists the scope. The running main editor was preserved. Reload Assets/Scenes/sence_test.unity from disk before Play; retain any unsaved user work separately before reloading.

The mound is generated after original Cesium collision support loads; the editor scene intentionally hides the old Terrain until Play. Failure to load within 30 seconds holds the machine and logs a warning. Existing X/Z positions are retained because sampled ground is sufficiently level. Only the mound is editable; below-grade pits in the original Cesium ground are outside this change. The ground floor is sampled at initialization, not continuously updated for future tileset/LOD changes. Web delivery has not been rebuilt or tested in this task.

Screenshot: /home/sk/Unity/soil-validation-20260916/Diagnostics/mobile-main-cut.png.
