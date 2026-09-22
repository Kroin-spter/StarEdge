# Cesium Web demo

Start the verified build:

```bash
python3 /home/sk/Unity/HMI_Unity6/Tools/serve_web.py
```

Open http://127.0.0.1:8089/index.html in Chrome or Edge. Keep the terminal
running. Double-clicking index.html (`file://`) is not supported.
Use `--port 8090` if 8089 is already in use, then open that port instead.

The output directory is `AZ_demo_cesium_fixed`. It includes the selected map
under `maps/osgb_small_005_004`, so a separate map server on port 8000 is not
needed. The older `AZ_demo_cesium` output is retained for comparison.

## Rebuild

In Unity, use **Tools > Excavator > Build Cesium Web Demo**. This builds the
saved `Assets/Scenes/sence_test.unity` scene with WebGL2, native threads,
a 32-worker pool, and uncompressed assets. Save scene changes first.

The build processors remove URDF importer-only components from the temporary
Web scene, disable its unconfigured ion tileset, include the Web SQLite
replacement, and copy the selected map into the build. The original scene and
prefab are not rewritten. The excavator waits for collision meshes under its
tracks before restoring its original fixed/dynamic base setting and controls.

`CESIUM_TILESET_DIR` can override the source map directory. By default it is
`../tilesets/osgb_small_005_004` relative to the Unity project.

## Hosting

Upload the entire output directory to an HTTPS static host. Map URLs resolve
relative to index.html, including when hosted under a subdirectory. Configure:

```text
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
Cross-Origin-Resource-Policy: same-origin
.wasm Content-Type: application/wasm
```

If enabling gzip or Brotli in a later build, serve `.gz` / `.br` resources with
their original MIME type and matching Content-Encoding. The supplied local
server supports these extensions. A static host must permit custom response
headers; serving static files alone does not enable browser threads.

## Compatibility

Validated combination: Unity 6000.5.10f1, Cesium for Unity 1.25.1, SQLite 3.51.3
rebuilt with this Unity's Emscripten toolchain. See
`Assets/Plugins/WebGL/README.md` for native-library rebuild instructions.

Browser validation covers loading, terrain collision readiness, and keyboard
rotation. It does not certify soil excavation or a complete autonomous cycle.
The existing controls remain keyboard/mouse based.
