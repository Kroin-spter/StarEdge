#!/usr/bin/env bash
set -euo pipefail

# Rebuild Cesium's SQLite against the same libc ABI as the Unity Web player.
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
unity_editor="${1:-/home/sk/unity-editor/6000.5.10f1/Editor/Unity}"
sdk_dir="$(dirname -- "$unity_editor")/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten"
sqlite_work="${project_dir}/Temp/CesiumSqliteBuild"
mkdir -p "$sqlite_work"
curl --fail --location --retry 2 \
  https://www.sqlite.org/2026/sqlite-amalgamation-3510300.zip \
  --output "$sqlite_work/sqlite.zip"
unzip -oq "$sqlite_work/sqlite.zip" -d "$sqlite_work"

# Use a private compiler cache so this command never clears Unity's frozen cache.
export EM_CONFIG="$sdk_dir/.emscripten"
export EM_CACHE="$sqlite_work/emscripten-cache"
export EM_FROZEN_CACHE=0
"$sdk_dir/emscripten/emcc" -O2 -pthread -msimd128 -fwasm-exceptions \
  -sSUPPORT_LONGJMP=wasm -DSQLITE_THREADSAFE=1 -DSQLITE_ENABLE_COLUMN_METADATA \
  -c "$sqlite_work/sqlite-amalgamation-3510300/sqlite3.c" \
  -o "$sqlite_work/sqlite3.o"
"$sdk_dir/llvm/llvm-ar" rcs "$sqlite_work/libsqlite3.a" "$sqlite_work/sqlite3.o"
cp "$sqlite_work/libsqlite3.a" "$project_dir/Assets/Plugins/WebGL/libsqlite3.a"
