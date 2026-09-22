# Cesium SQLite Web override

This project uses SQLite 3.51.3, rebuilt with Unity 6000.5.10f1's
Emscripten 4.0.20 toolchain. SQLite source is public domain:
https://www.sqlite.org/copyright.html

Cesium for Unity 1.25.1's shipped Web archive uses incompatible libc file
operations with this Unity version. Its cache initialization fails with
`disk I/O error`. The same SQLite CREATE TABLE / WAL / INSERT test fails
with the shipped archive and succeeds with this rebuild.

The project excludes the package's Web SQLite archive through Unity's
`SetIncludeInBuildDelegate` API and supplies this Web-only archive. The Web
build preprocessor verifies that exactly this replacement is selected. No
package-cache edits are required; native desktop plugins are unaffected.

Rebuild after changing the Unity toolchain:

```bash
bash Tools/rebuild_cesium_sqlite.sh /path/to/Editor/Unity
```

Retest the browser after any Unity or Cesium upgrade. This fixes the tested
SQLite ABI mismatch; it is not a compatibility guarantee for all versions.
