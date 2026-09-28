using System;
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

    /// <summary>Save the fully measured, untouched dynamic site as a portable asset with owned Terrain subassets.</summary>
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
        TerrainData terrainCopy = UnityEngine.Object.Instantiate(site.soil.Terrain.terrainData);
        terrainCopy.name = "SenceTest cuttable terrain";
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
        try
        {
            EnsureDirectory(AssetDirectory);
            path = AssetDatabase.GenerateUniqueAssetPath(AssetDirectory + "/ConstructionSite.asset");
            AssetDatabase.CreateAsset(data, path);
            AssetDatabase.AddObjectToAsset(terrainCopy, data);
            TerrainLayer[] layers = terrainCopy.terrainLayers;
            for (int i = 0; i < layers.Length; i++)
            {
                TerrainLayer source = layers[i];
                if (source == null) throw new InvalidOperationException("TerrainLayer 引用为空，不能烘焙材质。");
                if (AssetDatabase.Contains(source)) continue;
                TerrainLayer layer = UnityEngine.Object.Instantiate(source);
                layer.name = "Site surface " + i;
                AssetDatabase.AddObjectToAsset(layer, data);
                layer.diffuseTexture = PersistTexture(source.diffuseTexture, data, "Site diffuse " + i);
                layer.normalMapTexture = PersistTexture(source.normalMapTexture, data, "Site normal " + i);
                layer.maskMapTexture = PersistTexture(source.maskMapTexture, data, "Site mask " + i);
                layers[i] = layer;
            }
            terrainCopy.terrainLayers = layers;
            if (data.terrainMaterial != null && !AssetDatabase.Contains(data.terrainMaterial))
            {
                Material material = UnityEngine.Object.Instantiate(data.terrainMaterial);
                material.name = "Site Terrain material";
                AssetDatabase.AddObjectToAsset(material, data);
                data.terrainMaterial = material;
            }
            EditorUtility.SetDirty(terrainCopy);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            Selection.activeObject = data;
            Debug.Log($"施工区已保存到 {path}，初始可切土量 {data.initialSoilVolumeM3:F3}m³。退出 Play 后执行 Apply Selected Baked Site。", data);
        }
        catch
        {
            if (path != null && AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(path) != null)
                AssetDatabase.DeleteAsset(path);
            else
            {
                UnityEngine.Object.DestroyImmediate(terrainCopy);
                UnityEngine.Object.DestroyImmediate(data);
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
        clip.workArea = new Bounds(data.terrainOrigin + data.terrainData.size * .5f, data.terrainData.size);
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
        Selection.activeObject = FindOnlyBakedSite();
        ApplySelectedBakedSite();
        ValidateSelectedBakedSite();
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
        string[] assets = AssetDatabase.FindAssets("t:BakedConstructionSiteData", new[] { AssetDirectory });
        if (assets.Length != 1)
            throw new InvalidOperationException($"需要恰好一个已烘焙施工区资源，实际找到 {assets.Length} 个。");
        return AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(AssetDatabase.GUIDToAssetPath(assets[0]));
    }

    private static Texture2D PersistTexture(Texture2D texture, UnityEngine.Object parent, string name)
    {
        if (texture == null || AssetDatabase.Contains(texture)) return texture;
        Texture2D copy = UnityEngine.Object.Instantiate(texture);
        copy.name = name;
        AssetDatabase.AddObjectToAsset(copy, parent);
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
