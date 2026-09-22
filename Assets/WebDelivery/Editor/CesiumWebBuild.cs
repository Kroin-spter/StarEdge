using System;
using System.IO;
using System.Linq;
using CesiumForUnity;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Build and package the excavator Web demo with its local Cesium map.</summary>
public static class CesiumWebBuild
{
    public const string MapName = "osgb_small_005_004";
    public const string DemoScene = "Assets/Scenes/sence_test.unity";

    [InitializeOnLoadMethod]
    private static void SelectWebSqliteOverride()
    {
        // WebGL does not deduplicate all archive paths via isOverridable.
        // Exclude only the package's Web archive through Unity's build API.
        var original = AssetImporter.GetAtPath(
            "Packages/com.cesium.unity/Plugins/WebGL/lib/libsqlite3.a") as PluginImporter;
        if (original != null)
            original.SetIncludeInBuildDelegate(path => false);
    }

    /// <summary>Build the saved demo into AZ_demo_cesium_fixed; throw on invalid setup or build failure.</summary>
    [MenuItem("Tools/Excavator/Build Cesium Web Demo")]
    public static void BuildDemo()
    {
        var profile = BuildProfile.GetActiveBuildProfile();
        try
        {
            BuildProfile.SetActiveBuildProfile(null);
            PlayerSettings.WebGL.threadsSupport = true;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            if (!PlayerSettings.WebGL.emscriptenArgs.Contains("PTHREAD_POOL_SIZE"))
                PlayerSettings.WebGL.emscriptenArgs += " -sPTHREAD_POOL_SIZE=32";
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL,
                new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { DemoScene },
                locationPathName = "AZ_demo_cesium_fixed",
                target = BuildTarget.WebGL,
                options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException("Cesium Web build failed: " + report.summary.result);
        }
        finally
        {
            BuildProfile.SetActiveBuildProfile(profile);
        }
    }

    /// <summary>Return the selected map directory, accepting CESIUM_TILESET_DIR as a portable override.</summary>
    public static string MapDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("CESIUM_TILESET_DIR");
        return string.IsNullOrEmpty(configured)
            ? Path.GetFullPath(Path.Combine(Application.dataPath, "../../tilesets", MapName))
            : Path.GetFullPath(configured);
    }
}

/// <summary>Validate Web dependencies and prepare only the temporary demo build scene.</summary>
public sealed class CesiumWebBuildProcessor : IPreprocessBuildWithReport,
    IProcessSceneWithReport, IPostprocessBuildWithReport
{
    public int callbackOrder => 0;

    /// <summary>Reject missing map, threading, or SQLite override before a Web build.</summary>
    /// <param name="report">Build configuration being validated.</param>
    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.WebGL) return;
        if (!PlayerSettings.WebGL.threadsSupport)
            throw new BuildFailedException("Cesium requires Enable Native C/C++ Multithreading in the active Web profile.");
        if (!PlayerSettings.WebGL.emscriptenArgs.Contains("PTHREAD_POOL_SIZE"))
            PlayerSettings.WebGL.emscriptenArgs += " -sPTHREAD_POOL_SIZE=32";
        if (!File.Exists(Path.Combine(CesiumWebBuild.MapDirectory(), "tileset.json")))
            throw new BuildFailedException("Local Cesium map missing: " + CesiumWebBuild.MapDirectory());
        var sqlite = PluginImporter.GetImporters(BuildTarget.WebGL)
            .Where(plugin => Path.GetFileName(plugin.assetPath) == "libsqlite3.a" &&
                plugin.ShouldIncludeInBuild()).ToArray();
        if (sqlite.Length != 1 || !sqlite[0].assetPath.StartsWith("Assets/"))
            throw new BuildFailedException("The project SQLite Web override was not selected.");
        Debug.Log("SQLite Web override: " + sqlite[0].assetPath);
    }

    /// <summary>Resolve the demo map URL at runtime and remove unsupported URDF import metadata.</summary>
    /// <param name="scene">Temporary build scene; the saved scene is never modified.</param>
    /// <param name="report">Current build report.</param>
    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (report == null || report.summary.platform != BuildTarget.WebGL ||
            scene.path != CesiumWebBuild.DemoScene) return;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var keyboard in root.GetComponentsInChildren<Zx200KeyboardController>(true))
                if (!keyboard.fixedBase && keyboard.GetComponent<CesiumWebGroundReady>() == null)
                    keyboard.gameObject.AddComponent<CesiumWebGroundReady>();
            foreach (var tileset in root.GetComponentsInChildren<Cesium3DTileset>(true))
            {
                if (tileset.tilesetSource == CesiumDataSource.FromUrl &&
                    (tileset.url.Contains(CesiumWebBuild.MapName) ||
                     scene.GetRootGameObjects().SelectMany(candidate => candidate.GetComponentsInChildren<CesiumMoundSite>(true))
                         .Any(site => site.tileset == tileset)))
                {
                    if (tileset.GetComponent<CesiumWebMapUrl>() == null)
                        tileset.gameObject.AddComponent<CesiumWebMapUrl>();
                }
                else if (tileset.tilesetSource == CesiumDataSource.FromCesiumIon && tileset.ionAssetID == 0)
                    tileset.gameObject.SetActive(false);
            }
            // These metadata scripts are excluded from Web by the URDF importer assembly.
            // Keep the imported meshes, joints, ArticulationBody components, and application controls.
            foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true).Reverse())
                if (component != null && component.GetType().Assembly.GetName().Name == "Unity.Robotics.UrdfImporter")
                    UnityEngine.Object.DestroyImmediate(component);
        }
    }

    /// <summary>Copy the selected tileset beside the Web player so both share one origin.</summary>
    /// <param name="report">Successful Web build containing the destination directory.</param>
    public void OnPostprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.WebGL) return;
        string source = CesiumWebBuild.MapDirectory();
        string destination = Path.Combine(report.summary.outputPath, "maps", CesiumWebBuild.MapName);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, true);
        }
    }
}
