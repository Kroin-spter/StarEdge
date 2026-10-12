using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Builds the independently saved mound-top preview without modifying the original baked site.</summary>
public static class MoundTopPreviewBaker
{
    private const string ScenePath = "Assets/Scenes/sence_test.unity";
    private const string DirectoryPath = "Assets/ConstructionSite/Baked/MoundTopPreview";
    private static double deadline;
    private static Camera surveyCamera;

    /// <summary>Reapply an existing mound snapshot to the saved scene without regenerating or overwriting its assets.</summary>
    public static void ApplyExistingBatch()
    {
        EditorSceneManager.OpenScene(ScenePath);
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(DirectoryPath + "/ConstructionSite.asset");
        ConstructionSiteBaker.ApplySelectedBakedSite();
    }

    /// <summary>Open the preview scene and wait for local Cesium tiles before baking, then exit the batch editor.</summary>
    public static void BakeBatch()
    {
        EditorSceneManager.OpenScene(ScenePath);
        CesiumMoundSite site = UnityEngine.Object.FindFirstObjectByType<CesiumMoundSite>();
        var cameraObject = new GameObject("堆顶预览采样相机") { hideFlags = HideFlags.HideAndDontSave };
        surveyCamera = cameraObject.AddComponent<Camera>();
        surveyCamera.enabled = false;
        surveyCamera.orthographic = true;
        surveyCamera.orthographicSize = 25f;
        surveyCamera.aspect = 1f;
        surveyCamera.nearClipPlane = .1f;
        surveyCamera.farClipPlane = 300f;
        surveyCamera.transform.SetPositionAndRotation(new Vector3(320.28f, 120f, 351.43f),
            Quaternion.LookRotation(Vector3.down, Vector3.forward));
        CesiumForUnity.CesiumCameraManager.GetOrCreate(site.tileset.gameObject).additionalCameras.Add(surveyCamera);
        deadline = EditorApplication.timeSinceStartup + 90;
        EditorApplication.update += WaitForMap;
    }

    private static void WaitForMap()
    {
        CesiumMoundSite site = UnityEngine.Object.FindFirstObjectByType<CesiumMoundSite>();
        if (site != null && site.tileset.GetComponentsInChildren<MeshCollider>().Length > 10 &&
            EditorApplication.timeSinceStartup > deadline - 70)
        {
            EditorApplication.update -= WaitForMap;
            try { Bake(); EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }
        else if (EditorApplication.timeSinceStartup > deadline)
        {
            EditorApplication.update -= WaitForMap;
            Debug.LogError("土堆预览烘焙未获得 Cesium 碰撞瓦片；没有保存任何替换资源。");
            EditorApplication.Exit(1);
        }
    }

    /// <summary>Bake continuous cuttable soil, remove the scanned equipment footprint and save the preview scene.</summary>
    [MenuItem("Tools/Excavator/Construction Site/Build Mound Top Preview")]
    public static void Bake()
    {
        if (EditorApplication.isPlaying || UnityEngine.SceneManagement.SceneManager.GetActiveScene().path != ScenePath)
            throw new InvalidOperationException("请在非 Play 状态打开 sence_test。");
        if (AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(DirectoryPath + "/ConstructionSite.asset") != null)
            throw new InvalidOperationException("预览资源已存在；为避免覆盖编辑结果，请先检查现有预览。");
        CesiumMoundSite site = UnityEngine.Object.FindFirstObjectByType<CesiumMoundSite>();
        BakedConstructionSiteData original = site.bakedSite;
        if (original == null) throw new InvalidOperationException("缺少原施工区的 Cesium 同色影像资源。");
        MeshCollider[] map = site.tileset.GetComponentsInChildren<MeshCollider>()
            .Where(collider => collider.enabled && collider.sharedMesh != null).ToArray();
        if (map.Length == 0) throw new InvalidOperationException("请等待 Cesium 地图加载后重新烘焙。");
        Physics.SyncTransforms();
        Bounds bounds = map[0].bounds;
        foreach (MeshCollider collider in map) bounds.Encapsulate(collider.bounds);
        Debug.Log($"堆顶预览采样：地图碰撞 {map.Length}，世界范围 {bounds}");

        Vector3 machinePosition = new Vector3(320.28f, 1.43f, 351.43f);
        Quaternion rotation = Quaternion.Euler(0f, 31.008f, 0f);
        float rootOffset = original.machinePosition.y - original.selectedSupportY;
        float supportY = machinePosition.y - rootOffset;
        const float benchY = -2f;
        var origin = new Vector3(machinePosition.x - 14f, -8f, machinePosition.z - 14f);
        var size = new Vector3(28f, 25f, 28f);
        const int n = 257;
        var terrain = new TerrainData
        {
            name = "堆顶与转角可挖土堆", heightmapResolution = n,
            alphamapResolution = 256, size = size
        };
        var top = new float[n, n];
        var floor = new float[n, n];
        var snapshot = ScriptableObject.CreateInstance<BakedConstructionSiteData>();
        snapshot.groundHeights = new float[n * n];
        snapshot.soilMask = new float[n * n];
        int sampledEdges = 0, missingEdges = 0;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                Vector3 world = origin + new Vector3(x * size.x / (n - 1f), 0f, z * size.z / (n - 1f));
                Vector3 local = Quaternion.Inverse(rotation) * (world - machinePosition);
                Vector2 profile = MoundTopExcavationProfile.Evaluate(new Vector2(local.x, local.z), benchY, supportY);
                float edgeDistance = Mathf.Min(x, z, n - 1 - x, n - 1 - z) * size.x / (n - 1f);
                float edgeBlend = 1f - Mathf.SmoothStep(0f, 1f, edgeDistance / 2f);
                // Do not sample the mound interior: photogrammetry machines would become soil-shaped bumps.
                if (edgeBlend > 0f)
                {
                    if (SampleMap(map, world, out float mapY))
                    {
                        profile.x = Mathf.Lerp(profile.x, mapY + .015f, edgeBlend);
                        profile.y = Mathf.Lerp(profile.y, mapY, edgeBlend);
                        sampledEdges++;
                    }
                    else missingEdges++;
                }
                top[z, x] = Mathf.Clamp01((profile.x - origin.y) / size.y);
                floor[z, x] = Mathf.Min(top[z, x], Mathf.Clamp01((profile.y - origin.y) / size.y));
            }
        if (missingEdges > (sampledEdges + missingEdges) * .02f)
        {
            UnityEngine.Object.DestroyImmediate(terrain);
            UnityEngine.Object.DestroyImmediate(snapshot);
            throw new InvalidOperationException($"土堆边缘 Cesium 覆盖不足：缺失 {missingEdges} 个样本；等待地图加载后重试。");
        }
        terrain.SetHeights(0, 0, top);
        // Read Unity's quantized heights back before recording floor and conservation baseline.
        top = terrain.GetHeights(0, 0, n, n);
        double cell = (double)size.x * size.y * size.z / ((n - 1) * (n - 1));
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                float ground = Mathf.Min(floor[z, x], top[z, x]);
                float thickness = top[z, x] - ground;
                snapshot.groundHeights[z * n + x] = ground;
                snapshot.soilMask[z * n + x] = thickness > .00001f ? 1f : 0f;
                snapshot.initialSoilVolumeM3 += thickness * cell *
                    (x == 0 || x == n - 1 ? .5 : 1) * (z == 0 || z == n - 1 ? .5 : 1);
            }

        Directory.CreateDirectory(DirectoryPath);
        AssetDatabase.Refresh();
        string terrainPath = DirectoryPath + "/ConstructionSiteTerrain.asset";
        AssetDatabase.CreateAsset(terrain, terrainPath);
        Texture2D dirtImage = CopyCleanMapSoil(original.terrainData.terrainLayers[1].diffuseTexture);
        var dirt = new TerrainLayer { name = "Cesium 同色净土", diffuseTexture = dirtImage,
            tileSize = new Vector2(6.5f, 6.5f), smoothness = 0f };
        var mapLayer = UnityEngine.Object.Instantiate(dirt);
        mapLayer.name = "Cesium 土方影像";
        AssetDatabase.AddObjectToAsset(dirtImage, terrain);
        AssetDatabase.AddObjectToAsset(dirt, terrain);
        AssetDatabase.AddObjectToAsset(mapLayer, terrain);
        terrain.terrainLayers = new[] { dirt, mapLayer };
        var weights = new float[256, 256, 2];
        for (int z = 0; z < 256; z++)
            for (int x = 0; x < 256; x++) { weights[z, x, 0] = .25f; weights[z, x, 1] = .75f; }
        terrain.SetAlphamaps(0, 0, weights);
        snapshot.terrainData = terrain;
        snapshot.terrainMaterial = original.terrainMaterial;
        snapshot.sourceMapName = original.sourceMapName;
        snapshot.georeferenceLatitude = original.georeferenceLatitude;
        snapshot.georeferenceLongitude = original.georeferenceLongitude;
        snapshot.georeferenceHeight = original.georeferenceHeight;
        snapshot.terrainOrigin = origin;
        snapshot.machinePosition = machinePosition;
        snapshot.machineRotation = rotation;
        snapshot.truckPosition = machinePosition + rotation * new Vector3(-7f, 0f, 0f);
        snapshot.truckPosition.y = benchY + original.truckPosition.y - original.selectedTruckGroundY;
        snapshot.truckRotation = Quaternion.Euler(0f, 239.868f, 0f);
        snapshot.workFaceCenter = origin + new Vector3(14f, 0f, 14f);
        snapshot.workFaceRotation = Quaternion.identity;
        snapshot.workFaceSize = new Vector2(28f, 28f);
        snapshot.originalSupportY = original.originalSupportY;
        snapshot.selectedSupportY = supportY;
        snapshot.selectedTruckGroundY = benchY;
        snapshot.readyTipClearanceMeters = .6f;
        snapshot.selectedSurfaceNormalY = 1f;
        snapshot.maximumCutDepth = 4.5f;
        snapshot.replacesCesiumSurface = true;
        snapshot.supportsTracksOnTerrain = true;
        if (!snapshot.TryValidate(snapshot.sourceMapName, snapshot.georeferenceLatitude,
            snapshot.georeferenceLongitude, snapshot.georeferenceHeight, out string reason))
            throw new InvalidOperationException(reason);
        AssetDatabase.CreateAsset(snapshot, DirectoryPath + "/ConstructionSite.asset");
        EditorUtility.SetDirty(terrain);
        EditorUtility.SetDirty(snapshot);
        AssetDatabase.SaveAssets();
        Selection.activeObject = snapshot;
        ConstructionSiteBaker.ApplySelectedBakedSite();
        Camera camera = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)
            .FirstOrDefault(candidate => candidate.CompareTag("MainCamera"));
        if (camera != null)
        {
            camera.transform.position = machinePosition + new Vector3(-17f, 12f, -17f);
            camera.transform.LookAt(machinePosition + rotation * new Vector3(0f, 0f, 3f));
            EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        }
        Debug.Log($"堆顶预览已保存：土堆 28×28m，可切土量 {snapshot.initialSoilVolumeM3:F2}m³；" +
            $"挖掘机 {snapshot.machinePosition}，矿卡 {snapshot.truckPosition}，边缘采样 {sampledEdges}。");
    }

    private static bool SampleMap(MeshCollider[] map, Vector3 point, out float y)
    {
        y = float.NegativeInfinity;
        Ray ray = new Ray(new Vector3(point.x, 200f, point.z), Vector3.down);
        foreach (MeshCollider collider in map)
            if (collider.Raycast(ray, out RaycastHit hit, 400f)) y = Mathf.Max(y, hit.point.y);
        return float.IsFinite(y);
    }

    private static Texture2D CopyCleanMapSoil(Texture2D source)
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            throw new InvalidOperationException("净土影像复制需要图形模式，不能使用 -nographics。");
        var target = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;
        try
        {
            // Reuse a small bare-earth portion of the existing Cesium capture, not the scanned equipment image.
            Graphics.Blit(source, target, new Vector2(.045f, .045f), new Vector2(.59f, .39f));
            RenderTexture.active = target;
            var image = new Texture2D(256, 256, TextureFormat.RGB24, false)
                { name = "Cesium 净土影像", wrapMode = TextureWrapMode.Mirror };
            image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
            image.Apply();
            return image;
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
    }
}
