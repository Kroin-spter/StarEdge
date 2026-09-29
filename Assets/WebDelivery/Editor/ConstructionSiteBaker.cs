using System;
using System.IO;
using System.Linq;
using CesiumForUnity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Captures a measured Play-mode work face and applies its persistent snapshot to sence_test.</summary>
public static class ConstructionSiteBaker
{
    private const string ScenePath = "Assets/Scenes/sence_test.unity";
    private const string AssetDirectory = "Assets/ConstructionSite/Baked/SenceTest";
    private const string CanonicalTerrainPath = AssetDirectory + "/ConstructionSiteTerrain.asset";

    [InitializeOnLoadMethod]
    private static void KeepLocalMapUrlsOutOfSavedScenes()
    {
        EditorSceneManager.sceneSaving -= ClearTransientLocalMapUrls;
        EditorSceneManager.sceneSaving += ClearTransientLocalMapUrls;
    }

    private static void ClearTransientLocalMapUrls(Scene scene, string path)
    {
        if (path != ScenePath || EditorApplication.isPlaying) return;
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (CesiumLocalMapReference reference in root.GetComponentsInChildren<CesiumLocalMapReference>(true))
            {
                Cesium3DTileset tileset = reference.tileset;
                if (tileset == null || !tileset.url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) continue;
                tileset.url = string.Empty;
                EditorApplication.delayCall += () =>
                {
                    if (tileset != null && !EditorApplication.isPlaying)
                        CesiumLocalMapReference.ApplyLocalMap(tileset, reference.mapName, reference);
                };
            }
    }

    /// <summary>Save the measured untouched work face as portable data plus a standalone Terrain asset.</summary>
    [MenuItem("Tools/Excavator/Construction Site/Bake Ready Dynamic Site")]
    public static void BakeReadyDynamicSite()
    {
        if (!EditorApplication.isPlaying)
            throw new InvalidOperationException("先在 Play 中使用 DynamicDeveloper 模式，等待施工区就绪。 ");
        CesiumMoundSite site = UnityEngine.Object.FindObjectsByType<CesiumMoundSite>(FindObjectsSortMode.None)
            .Single(candidate => candidate.gameObject.scene.path == ScenePath);
        if (site.siteMode != CesiumMoundSite.SitePreparationMode.DynamicDeveloper || !site.IsReady)
            throw new InvalidOperationException("当前施工区未在动态开发模式就绪，不能烘焙。");
        var data = ScriptableObject.CreateInstance<BakedConstructionSiteData>();
        TerrainData sourceTerrain = site.soil.Terrain.terrainData;
        TerrainData terrainCopy = UnityEngine.Object.Instantiate(sourceTerrain);
        terrainCopy.name = "SenceTest cuttable terrain";
        int alphaResolution = sourceTerrain.alphamapResolution;
        float[,,] alphaWeights = sourceTerrain.GetAlphamaps(0, 0, alphaResolution, alphaResolution);
        TerrainLayer[] sourceLayers = terrainCopy.terrainLayers;
        var layers = new TerrainLayer[sourceLayers.Length];
        var terrainSubassets = new System.Collections.Generic.List<UnityEngine.Object>();
        for (int i = 0; i < sourceLayers.Length; i++)
        {
            TerrainLayer sourceLayer = sourceLayers[i];
            if (sourceLayer == null) throw new InvalidOperationException("TerrainLayer 引用为空，不能烘焙材质。");
            TerrainLayer layer = UnityEngine.Object.Instantiate(sourceLayer);
            layer.name = "Site surface " + i;
            layer.diffuseTexture = CloneTexture(sourceLayer.diffuseTexture, "Site diffuse " + i, terrainSubassets);
            layer.normalMapTexture = CloneTexture(sourceLayer.normalMapTexture, "Site normal " + i, terrainSubassets);
            layer.maskMapTexture = CloneTexture(sourceLayer.maskMapTexture, "Site mask " + i, terrainSubassets);
            layers[i] = layer;
            terrainSubassets.Add(layer);
        }
        terrainCopy.SetTreeInstances(Array.Empty<TreeInstance>(), true);
        terrainCopy.treePrototypes = Array.Empty<TreePrototype>();
        terrainCopy.detailPrototypes = Array.Empty<DetailPrototype>();
        data.terrainData = terrainCopy;
        if (!site.TryCaptureBakedData(data, out string reason))
        {
            UnityEngine.Object.DestroyImmediate(terrainCopy);
            UnityEngine.Object.DestroyImmediate(data);
            throw new InvalidOperationException(reason);
        }

        string path = null;
        string terrainPath = null;
        try
        {
            EnsureDirectory(AssetDirectory);
            path = AssetDatabase.GenerateUniqueAssetPath(AssetDirectory + "/ConstructionSite.asset");
            terrainPath = AssetDatabase.GenerateUniqueAssetPath(
                AssetDirectory + "/ConstructionSiteTerrain.asset");
            // Keep temporary TerrainLayer references out of CreateAsset; Unity drops non-asset
            // layers during serialization. They are reattached after their owned copies are assets.
            terrainCopy.terrainLayers = Array.Empty<TerrainLayer>();
            AssetDatabase.CreateAsset(terrainCopy, terrainPath);
            AssetDatabase.CreateAsset(data, path);
            foreach (UnityEngine.Object subasset in terrainSubassets)
                AssetDatabase.AddObjectToAsset(subasset, terrainCopy);
            terrainCopy.terrainLayers = layers;
            // Reassigning TerrainLayers can invalidate Unity 6's native splat database.
            // Restore the sampled soil/map blend after all persistent layer references are set.
            terrainCopy.SetAlphamaps(0, 0, alphaWeights);
            if (data.terrainMaterial != null && !AssetDatabase.Contains(data.terrainMaterial))
            {
                Material material = UnityEngine.Object.Instantiate(data.terrainMaterial);
                material.name = "Site Terrain material";
                AssetDatabase.AddObjectToAsset(material, terrainCopy);
                data.terrainMaterial = material;
            }
            EditorUtility.SetDirty(terrainCopy);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            Selection.activeObject = data;
            Debug.Log($"施工区已保存到 {path}，初始可切土量 {data.initialSoilVolumeM3:F3}m³。退出 Play 后执行 Apply Selected Baked Site。", data);
        }
        catch
        {
            if (path != null && AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(path) != null)
                AssetDatabase.DeleteAsset(path);
            if (terrainPath != null && AssetDatabase.LoadAssetAtPath<TerrainData>(terrainPath) != null)
                AssetDatabase.DeleteAsset(terrainPath);
            if ((path == null || AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(path) == null) &&
                (terrainPath == null || AssetDatabase.LoadAssetAtPath<TerrainData>(terrainPath) == null))
            {
                if (terrainCopy != null && !AssetDatabase.Contains(terrainCopy)) UnityEngine.Object.DestroyImmediate(terrainCopy);
                if (data != null && !AssetDatabase.Contains(data)) UnityEngine.Object.DestroyImmediate(data);
            }
            throw;
        }
    }

    /// <summary>Apply the selected snapshot to the authored scene so Terrain, machine and truck appear before Play.</summary>
    [MenuItem("Tools/Excavator/Construction Site/Apply Selected Baked Site")]
    public static void ApplySelectedBakedSite()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("先退出 Play，再应用烘焙资源到场景。");
        if (SceneManager.GetActiveScene().path != ScenePath)
            throw new InvalidOperationException("请先打开 Assets/Scenes/sence_test.unity。");
        var data = Selection.activeObject as BakedConstructionSiteData;
        if (data == null || !AssetDatabase.Contains(data) || data.terrainData == null ||
            !AssetDatabase.Contains(data.terrainData))
            throw new InvalidOperationException("请在 Project 中选中已保存的 ConstructionSite 烘焙资源。");
        CesiumMoundSite site = UnityEngine.Object.FindObjectsByType<CesiumMoundSite>(FindObjectsSortMode.None)
            .Single(candidate => candidate.gameObject.scene.path == ScenePath);
        CesiumGeoreference reference = site.tileset.GetComponentInParent<CesiumGeoreference>();
        if (reference == null) throw new InvalidOperationException("场景缺少 Cesium 地理参考。");
        if (!data.TryValidate(site.mapName, reference.latitude,
                reference.longitude, reference.height, out string reason))
            throw new InvalidOperationException(reason);
        Terrain terrain = site.soil.GetComponent<Terrain>();
        TerrainCollider collider = site.soil.SoilCollider;
        Zx200KeyboardController keyboard = site.machine.GetComponent<Zx200KeyboardController>();
        keyboard.ResolveArticulationRoot();
        Transform machineRoot = keyboard.articulationRoot.transform;
        Undo.RecordObjects(new UnityEngine.Object[] { site, terrain, collider, site.soil.transform,
            machineRoot, site.truck }, "Apply baked construction site");
        site.bakedSite = data;
        site.siteMode = CesiumMoundSite.SitePreparationMode.Baked;
        site.soil.transform.position = data.terrainOrigin;
        terrain.terrainData = data.terrainData;
        terrain.materialTemplate = data.terrainMaterial;
        terrain.enabled = true;
        collider.terrainData = data.terrainData;
        collider.enabled = false; // The baked-site loader enables collision after validating the snapshot.
        ConstructionSiteMapClip clip = site.tileset.GetComponent<ConstructionSiteMapClip>();
        if (clip == null) clip = Undo.AddComponent<ConstructionSiteMapClip>(site.tileset.gameObject);
        clip.SetWorkFaceFootprint(data.workFaceCenter, data.workFaceRotation, data.workFaceSize,
            data.terrainOrigin.y, data.terrainOrigin.y + data.terrainData.size.y);
        machineRoot.SetPositionAndRotation(data.machinePosition, data.machineRotation);
        site.truck.SetPositionAndRotation(data.truckPosition, data.truckRotation);
        PrefabUtility.RecordPrefabInstancePropertyModifications(machineRoot);
        PrefabUtility.RecordPrefabInstancePropertyModifications(site.truck);
        EditorUtility.SetDirty(site);
        EditorUtility.SetDirty(clip);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        Debug.Log($"sence_test 已引用 {AssetDatabase.GetAssetPath(data)}；施工区可在 Play 前预览。", site);
    }

    /// <summary>Validate the selected baked asset against the currently open delivery scene.</summary>
    [MenuItem("Tools/Excavator/Construction Site/Validate Selected Baked Site")]
    public static void ValidateSelectedBakedSite()
    {
        var data = Selection.activeObject as BakedConstructionSiteData;
        var site = UnityEngine.Object.FindObjectsByType<CesiumMoundSite>(FindObjectsSortMode.None)
            .FirstOrDefault(candidate => candidate.gameObject.scene.path == ScenePath);
        if (data == null || site == null) throw new InvalidOperationException("请打开 sence_test 并选中烘焙资源。");
        var reference = site.tileset.GetComponentInParent<CesiumGeoreference>();
        if (reference == null) throw new InvalidOperationException("场景缺少 Cesium 地理参考。");
        if (!data.TryValidate(site.mapName, reference.latitude,
                reference.longitude, reference.height, out string reason))
            throw new InvalidOperationException(reason);
        Debug.Log($"烘焙施工区验证通过：{data.terrainData.size}，初始土量 {data.initialSoilVolumeM3:F3}m³。", data);
    }

    /// <summary>Apply the one generated snapshot to sence_test from an isolated Unity batch process.</summary>
    public static void ApplyOnlyBakedSiteBatch()
    {
        if (!Application.isBatchMode)
            throw new InvalidOperationException("此入口仅供隔离项目的批处理烘焙验收使用。");
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Selection.activeObject = PromoteCanonicalBakedSite(FindOnlyBakedSite(), out string[] backupPaths);
        ApplySelectedBakedSite();
        ValidateSelectedBakedSite();
        foreach (string backupPath in backupPaths)
        {
            AssetDatabase.DeleteAsset(backupPath);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    /// <summary>Remove unused legacy tree prototypes from the already captured local construction patch.</summary>
    public static void NormalizeOnlyBakedSiteBatch()
    {
        if (!Application.isBatchMode)
            throw new InvalidOperationException("此入口仅供隔离项目批处理维护烘焙资源使用。");
        BakedConstructionSiteData data = FindOnlyBakedSite();
        data.terrainData.SetTreeInstances(Array.Empty<TreeInstance>(), true);
        data.terrainData.treePrototypes = Array.Empty<TreePrototype>();
        data.terrainData.detailPrototypes = Array.Empty<DetailPrototype>();
        EditorUtility.SetDirty(data.terrainData);
        AssetDatabase.SaveAssets();
        Debug.Log("施工区烘焙 Terrain 已移除遗留树木与细节原型。", data);
    }

    private static BakedConstructionSiteData FindOnlyBakedSite()
    {
        var assets = AssetDatabase.FindAssets("t:BakedConstructionSiteData", new[] { AssetDirectory })
            .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
            .Select(path => new
            {
                Path = path,
                Data = AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(path)
            })
            .Where(candidate => candidate.Data != null &&
                !Path.GetFileNameWithoutExtension(candidate.Path).Contains("_previous", StringComparison.Ordinal) &&
                candidate.Data.formatVersion == BakedConstructionSiteData.CurrentVersion &&
                candidate.Data.terrainData != null)
            .OrderByDescending(candidate => File.GetLastWriteTimeUtc(Path.GetFullPath(candidate.Path)))
            .ToArray();
        if (assets.Length == 0)
            throw new InvalidOperationException("没有找到新版已烘焙施工区资源；旧版本不会参与应用，请先重新烘焙。");
        if (assets.Length > 1)
            Debug.LogWarning($"发现 {assets.Length} 个新版烘焙文件，将应用最近生成的：{assets[0].Path}。");
        return assets[0].Data;
    }

    private static BakedConstructionSiteData PromoteCanonicalBakedSite(BakedConstructionSiteData data,
        out string[] backupPaths)
    {
        var backups = new System.Collections.Generic.List<string>();
        string sourcePath = AssetDatabase.GetAssetPath(data);
        string sourceTerrainPath = AssetDatabase.GetAssetPath(data.terrainData);
        string canonicalPath = AssetDirectory + "/ConstructionSite.asset";
        bool terrainAlreadyCanonical = string.Equals(sourceTerrainPath, CanonicalTerrainPath, StringComparison.Ordinal);
        if (string.Equals(sourcePath, canonicalPath, StringComparison.Ordinal) &&
            terrainAlreadyCanonical)
        {
            backupPaths = Array.Empty<string>();
            return data;
        }
        if (string.Equals(sourcePath, sourceTerrainPath, StringComparison.Ordinal))
            throw new InvalidOperationException("新版 TerrainData 必须是独立资源；请重新烘焙后再应用。");

        if (AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(canonicalPath) != null)
        {
            string backupDataPath = AssetDatabase.GenerateUniqueAssetPath(AssetDirectory + "/ConstructionSite_previous.asset");
            string backupError = AssetDatabase.MoveAsset(canonicalPath, backupDataPath);
            if (!string.IsNullOrEmpty(backupError))
                throw new InvalidOperationException("无法暂存旧版施工区资源：" + backupError);
            backups.Add(backupDataPath);
        }
        if (!terrainAlreadyCanonical && AssetDatabase.LoadAssetAtPath<TerrainData>(CanonicalTerrainPath) != null)
        {
            string backupTerrainPath = AssetDatabase.GenerateUniqueAssetPath(
                AssetDirectory + "/ConstructionSiteTerrain_previous.asset");
            string backupError = AssetDatabase.MoveAsset(CanonicalTerrainPath, backupTerrainPath);
            if (!string.IsNullOrEmpty(backupError))
                throw new InvalidOperationException("无法暂存旧版独立地形资源：" + backupError);
            backups.Add(backupTerrainPath);
        }

        if (!terrainAlreadyCanonical)
        {
            string terrainMoveError = AssetDatabase.MoveAsset(sourceTerrainPath, CanonicalTerrainPath);
            if (!string.IsNullOrEmpty(terrainMoveError))
                throw new InvalidOperationException("无法将新版 TerrainData 整理为标准路径：" + terrainMoveError);
        }
        string moveError = AssetDatabase.MoveAsset(sourcePath, canonicalPath);
        if (!string.IsNullOrEmpty(moveError))
            throw new InvalidOperationException("无法将新版施工区资源整理为唯一标准路径：" + moveError);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        BakedConstructionSiteData promoted = AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(canonicalPath);
        if (promoted == null)
            throw new InvalidOperationException("新版烘焙资源移动后无法重新载入；旧版资源保留在暂存路径中：" +
                string.Join(", ", backups));
        backupPaths = backups.ToArray();
        return promoted;
    }

    private static Texture2D CloneTexture(Texture2D texture, string name,
        System.Collections.Generic.List<UnityEngine.Object> terrainSubassets)
    {
        if (texture == null) return null;
        Texture2D copy = UnityEngine.Object.Instantiate(texture);
        copy.name = name;
        terrainSubassets.Add(copy);
        return copy;
    }

    private static void EnsureDirectory(string path)
    {
        string current = "Assets";
        foreach (string segment in path.Substring("Assets/".Length).Split('/'))
        {
            string next = current + "/" + segment;
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, segment);
            current = next;
        }
    }
}
