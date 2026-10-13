using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>Export the saved excavator Web scene into the configured cockpit frontend.</summary>
public static class CockpitWebExporter
{
    private const string ExportName = "AZ_demo_cesium_fixed";
    private static string PreferenceKey => "CockpitWebExporter.Target." + Application.dataPath;
    private static string TargetDirectory => EditorPrefs.GetString(PreferenceKey,
        Path.GetFullPath(Path.Combine(Application.dataPath, "../../../26q1/cockpitWeb")));

    /// <summary>Choose a validated frontend directory; no parameters or return value.</summary>
    [MenuItem("Tools/Excavator/设置座舱前端导出目录")]
    public static void ConfigureTarget()
    {
        string selected = EditorUtility.OpenFolderPanel("选择 cockpitWeb 目录", TargetDirectory, "");
        if (string.IsNullOrEmpty(selected)) return;
        ValidateFrontend(selected);
        EditorPrefs.SetString(PreferenceKey, Path.GetFullPath(selected));
        Debug.Log("座舱前端导出目录：" + selected);
    }

    /// <summary>Build sence_test and update frontend only on success; no parameters or return value.</summary>
    [MenuItem("Tools/Excavator/导出并更新座舱前端")]
    public static void ExportToFrontend()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new BuildFailedException("请先退出 Play 模式再导出。");
        if (!Application.isBatchMode && !UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;
        string target = Path.GetFullPath(TargetDirectory);
        ValidateFrontend(target);
        string project = Path.GetDirectoryName(Application.dataPath);
        CesiumWebBuild.BuildDemo();
        SyncExport(Path.Combine(project, ExportName), target, ReadVersion(project));
        Debug.Log("Unity 已更新到 " + Path.Combine(target, "public/unity-webgl") +
                  "；随后执行 npm run electron:pack:win:unity。");
    }

    /// <summary>Stage and replace Web resources, retaining the custom frontend bridge.</summary>
    /// <param name="exportDirectory">Successful Unity export, including Build and local maps.</param>
    /// <param name="frontendDirectory">cockpit-web project containing its custom WebGL index.</param>
    /// <param name="unityVersion">Code revision identifier written into delivery.json.</param>
    /// <returns>No value. Invalid exports throw before replacing frontend files.</returns>
    public static void SyncExport(string exportDirectory, string frontendDirectory, string unityVersion)
    {
        string source = Path.GetFullPath(exportDirectory);
        string frontend = Path.GetFullPath(frontendDirectory);
        ValidateFrontend(frontend);
        string target = Path.Combine(frontend, "public", "unity-webgl");
        if (source == target || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new BuildFailedException("导出源与前端目标目录不可相互包含。");
        foreach (string suffix in new[] { "loader.js", "data", "framework.js", "wasm" })
        {
            string file = Path.Combine(source, "Build", ExportName + "." + suffix);
            if (!File.Exists(file) || new FileInfo(file).Length == 0)
                throw new BuildFailedException("Unity 构建文件缺失或为空：" + file);
        }
        if (!File.Exists(Path.Combine(source, "maps", CesiumWebBuild.MapName, "tileset.json")))
            throw new BuildFailedException("Unity 导出缺少本地地图 tileset.json。");

        // Prepare the full replacement beside the target; preserve index.html and custom files.
        string token = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(frontend, "public", ".unity-stage-" + token);
        string backup = Path.Combine(frontend, "public", ".unity-backup-" + token);
        bool movedOriginal = false;
        try
        {
            CopyDirectory(target, stage);
            foreach (string folder in new[] { "Build", "TemplateData", "maps", "StreamingAssets" })
            {
                string stagedFolder = Path.Combine(stage, folder);
                if (Directory.Exists(stagedFolder)) Directory.Delete(stagedFolder, true);
                string sourceFolder = Path.Combine(source, folder);
                if (Directory.Exists(sourceFolder)) CopyDirectory(sourceFolder, stagedFolder);
            }
            File.WriteAllText(Path.Combine(stage, "delivery.json"), JsonUtility.ToJson(new Delivery
            {
                unityCommit = unityVersion,
                scene = "sence_test",
                mapName = CesiumWebBuild.MapName,
                build = new BuildFiles
                {
                    loaderUrl = "Build/" + ExportName + ".loader.js",
                    dataUrl = "Build/" + ExportName + ".data",
                    frameworkUrl = "Build/" + ExportName + ".framework.js",
                    codeUrl = "Build/" + ExportName + ".wasm"
                }
            }, true));
            Directory.Move(target, backup);
            movedOriginal = true;
            Directory.Move(stage, target);
        }
        catch
        {
            if (movedOriginal && !Directory.Exists(target)) Directory.Move(backup, target);
            throw;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        // If backup cleanup fails, keep the valid new delivery and retain the recoverable backup.
        if (Directory.Exists(backup))
        {
            try { Directory.Delete(backup, true); }
            catch (IOException error) { Debug.LogWarning("导出成功，旧资源备份保留：" + backup + "；" + error.Message); }
            catch (UnauthorizedAccessException error) { Debug.LogWarning("导出成功，旧资源备份保留：" + backup + "；" + error.Message); }
        }
    }

    private static void ValidateFrontend(string frontend)
    {
        string package = Path.Combine(frontend, "package.json");
        string index = Path.Combine(frontend, "public", "unity-webgl", "index.html");
        if (!File.Exists(package) || JsonUtility.FromJson<FrontendPackage>(File.ReadAllText(package))?.name != "cockpit-web" ||
            !File.Exists(index) || !File.ReadAllText(index).Contains("ReceiveControlTopic"))
            throw new BuildFailedException("请选择已有 package.json 和专用 Unity 桥接入口的 cockpitWeb 目录：" + frontend);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string ReadVersion(string project)
    {
        try
        {
            using (var process = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                WorkingDirectory = project, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            }))
            {
                string version = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && !string.IsNullOrEmpty(version)) return version;
            }
        }
        catch (Exception error) { Debug.LogWarning("无法读取 Git 版本：" + error.Message); }
        return "local-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
    }

    [Serializable] private class FrontendPackage { public string name; }
    [Serializable] private class Delivery
    {
        public string unityCommit;
        public string scene;
        public string mapName;
        public BuildFiles build;
    }
    [Serializable] private class BuildFiles
    {
        public string loaderUrl;
        public string dataUrl;
        public string frameworkUrl;
        public string codeUrl;
    }
}
