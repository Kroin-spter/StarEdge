#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
editor=/home/sk/unity-editor/6000.5.10f1/Editor/Unity

/usr/bin/python3 "$project_dir/cleanup-stale-proxy-watchers.py"

# Avoid exhausting the per-user inotify instances in Unity's .NET IL postprocessor.
export DOTNET_USE_POLLING_FILE_WATCHER=1
exec "$editor" -projectPath "$project_dir" -force-glcore "$@"
