# WebGPU URDF Plugin Build Design

## Goal

Allow the Unity 6 Web/WebGPU player build to complete without including the URDF Importer's Windows-only Assimp native libraries.

## Design

Embed `com.unity.robotics.urdf-importer` under the project's `Packages` directory so its import settings are editable and persistent. Remove the remote dependency entry from `Packages/manifest.json`, then restrict the x86 and x86_64 `assimp.dll` files to their matching Windows targets. Web, Linux, macOS, and incompatible Windows architectures must not select either DLL.

Repair the merge-conflicted `Assets/TerrainDemoScene_URP/Scripts.meta` by preserving one valid GUID and removing conflict markers. This warning is independent of the Assimp collision but represents invalid Unity metadata.

## Verification

Verify the package is embedded, both plugin metadata files exclude Web, no conflict markers remain in `Scripts.meta`, Unity resolves the package, and a Web build no longer reports `Plugins colliding with each other`.
