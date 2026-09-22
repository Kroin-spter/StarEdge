# Bucket intake and distributed discharge implementation plan

User approved implementation of the previously presented design. Work in the existing isolated Unity project; preserve the open main editor and source scene. No Git repository is present.

Goal: reject false intake during vertical pressing/back scraping, and replace the single discharge trajectory with gravity-aware mouth-width emission while preserving soil accounting.

- [x] Intake: add failing EditMode scenarios for vertical press, downward opening, back scrape and valid scoop; check Terrain unchanged for rejected intake. Add a shared acceptance calculation to BucketSoilCutter using mouth alignment, horizontal sweep and pose-dependent retention capacity. Expose rejection status independently in the UI; preserve the existing depth/fullness guard and upward withdrawal. Invalid cuts leave soil in Terrain, rather than inventing untracked displaced soil.
- [x] Discharge: add failing tests for distributed emission, low-side roll bias, partial outlet obstruction and conservation; implement bounded multi-outlet emission in BucketSoilUnloader / SoilFlightTransport. Existing parcels continue to own all released volume and resolve actual landings.
- [x] Integration: run full EditMode regressions and real-articulation PlayMode digging/loading/backfill/resistance checks. Inspect rendered flow and complete screenshots. Update test operating sequence only if necessary to reflect valid scooping, never bypass new gates.
- [x] Review changes and evidence, back up changed main files, sync verified scoped assets with hash checks, document limitations and controls.

Validation commands: Unity 6000.5.10f1 -batchmode -nographics -runTests -testPlatform EditMode; rendered PlayMode uses DISPLAY=:1 -force-glcore. DOTNET_USE_POLLING_FILE_WATCHER=1. Never run two Unity instances on the isolated project concurrently; omit -quit for runTests.

Completed: final 122 EditMode and 11 rendered PlayMode passed; intake and discharge independently reviewed; scoped files synced with byte equality and SHA-256 manifest. Main scene file unchanged. Usage and measured accounting: docs/intake-flow-20260917/README.md.
