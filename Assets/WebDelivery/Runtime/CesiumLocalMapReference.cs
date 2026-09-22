using System;
using System.IO;
using CesiumForUnity;
using UnityEngine;

/// <summary>Resolves and applies the portable local Cesium tileset used by the construction site.</summary>
[ExecuteAlways]
[DefaultExecutionOrder(-13050)]
public sealed class CesiumLocalMapReference : MonoBehaviour
{
    [Tooltip("Cesium 3D Tileset that should load the local construction map.")]
    public Cesium3DTileset tileset;

    [Tooltip("Directory name under the sibling tilesets directory.")]
    public string mapName = "osgb_small_005_004";

    private void OnEnable()
    {
        ApplyConfiguredMap();
    }

    private void OnValidate()
    {
        if (!Application.isPlaying) ApplyConfiguredMap();
    }

    private void Start()
    {
        ApplyConfiguredMap();
    }

    private void ApplyConfiguredMap()
    {
        if (tileset != null && !string.IsNullOrWhiteSpace(mapName))
            ApplyLocalMap(tileset, mapName, this);
    }

    /// <summary>Resolve a map directory override or the project's portable sibling tilesets directory.</summary>
    /// <param name="mapName">Directory name containing the local <c>tileset.json</c>.</param>
    /// <param name="tilesetPath">Resolved absolute path to <c>tileset.json</c>, even when it does not exist.</param>
    /// <returns><c>true</c> when the resolved tileset file exists; otherwise <c>false</c>.</returns>
    public static bool TryResolveTilesetPath(string mapName, out string tilesetPath)
    {
        tilesetPath = string.Empty;
        if (string.IsNullOrWhiteSpace(mapName)) return false;

        string configuredDirectory = Environment.GetEnvironmentVariable("CESIUM_TILESET_DIR");
        string directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Application.dataPath, "../../tilesets", mapName)
            : configuredDirectory;
        tilesetPath = Path.GetFullPath(Path.Combine(directory, "tileset.json"));
        return File.Exists(tilesetPath);
    }

    /// <summary>Convert a local tileset path to the file URL format expected by Cesium for Unity.</summary>
    /// <param name="tilesetPath">Absolute or relative path to a local <c>tileset.json</c>.</param>
    /// <returns>An absolute <c>file:///</c> URI with characters such as spaces escaped.</returns>
    public static string ToFileUri(string tilesetPath)
    {
        if (string.IsNullOrWhiteSpace(tilesetPath)) throw new ArgumentException("Tileset path is required.", nameof(tilesetPath));
        return new Uri(Path.GetFullPath(tilesetPath)).AbsoluteUri;
    }

    /// <summary>Configure a Cesium Tileset from the resolved local map without creating another collider source.</summary>
    /// <param name="tileset">Cesium Tileset component to configure.</param>
    /// <param name="mapName">Directory name containing the local <c>tileset.json</c>.</param>
    /// <param name="context">Optional Unity object used as the warning context.</param>
    /// <returns><c>true</c> when the local map was found and applied; otherwise <c>false</c>.</returns>
    public static bool ApplyLocalMap(Cesium3DTileset tileset, string mapName, UnityEngine.Object context)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return false;
#else
        if (tileset == null || !TryResolveTilesetPath(mapName, out string tilesetPath))
        {
            string configuredDirectory = Environment.GetEnvironmentVariable("CESIUM_TILESET_DIR");
            string directory = string.IsNullOrWhiteSpace(configuredDirectory)
                ? Path.Combine(Application.dataPath, "../../tilesets", mapName ?? string.Empty)
                : configuredDirectory;
            string missingPath = Path.GetFullPath(Path.Combine(directory, "tileset.json"));
            string message = $"Cesium local map is missing: {missingPath}. Set CESIUM_TILESET_DIR or place the map beside the project.";
            if (context == null) Debug.LogWarning(message);
            else Debug.LogWarning(message, context);
            return false;
        }

        if (tileset.tilesetSource != CesiumDataSource.FromUrl)
            tileset.tilesetSource = CesiumDataSource.FromUrl;
        string fileUri = ToFileUri(tilesetPath);
        if (!string.Equals(tileset.url, fileUri, StringComparison.Ordinal)) tileset.url = fileUri;
        return true;
#endif
    }
}
