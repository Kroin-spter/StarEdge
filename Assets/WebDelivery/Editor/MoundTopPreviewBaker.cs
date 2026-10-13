using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Bakes a map-following cuttable mound, repairing only scanned equipment and local bearing pads.</summary>
public static class MoundTopPreviewBaker
{
    private const string ScenePath = "Assets/Scenes/sence_test.unity";
    private const string DirectoryPath = "Assets/ConstructionSite/Baked/MapFollowingPreview";
    private static double deadline;
    private static Camera surveyCamera;

    /// <summary>Remove the identified front scan shards while preserving layout, track support and map clipping.</summary>
    [MenuItem("Tools/Excavator/Construction Site/Repair Preview Front Scan Shards")]
    public static void RepairFrontSurfaceBatch()
    {
        RepairShadingBatch();
        var snapshot = AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(DirectoryPath + "/ConstructionSite.asset");
        TerrainData data = snapshot.terrainData;
        int n = data.heightmapResolution;
        float[,] previous = data.GetHeights(0, 0, n, n);
        var mask = new bool[n, n];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                mask[z, x] = IsFrontShardArea(new Vector2(
                    snapshot.terrainOrigin.x + x * data.size.x / (n - 1),
                    snapshot.terrainOrigin.z + z * data.size.z / (n - 1)));
        data.SetHeights(0, 0, MapFollowingSoilSurface.RepairHeights(previous, mask));
        float[,] repaired = data.GetHeights(0, 0, n, n);
        snapshot.initialSoilVolumeM3 = 0;
        double cell = (double)data.size.x * data.size.y * data.size.z / ((n - 1) * (n - 1));
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int index = z * n + x;
                // Keep each cell's authored soil thickness; only remove its spurious raised geometry.
                float thickness = Mathf.Max(0f, previous[z, x] - snapshot.groundHeights[index]);
                snapshot.groundHeights[index] = Mathf.Max(0f, repaired[z, x] - thickness);
                snapshot.initialSoilVolumeM3 += (repaired[z, x] - snapshot.groundHeights[index]) * cell *
                    (x == 0 || x == n - 1 ? .5 : 1) * (z == 0 || z == n - 1 ? .5 : 1);
            }
        if (!snapshot.TryValidate(snapshot.sourceMapName, snapshot.georeferenceLatitude,
            snapshot.georeferenceLongitude, snapshot.georeferenceHeight, out string reason))
            throw new InvalidOperationException(reason);
        EditorUtility.SetDirty(data);
        EditorUtility.SetDirty(snapshot);
        AssetDatabase.SaveAssets();
        Selection.activeObject = snapshot;
        ConstructionSiteBaker.ApplySelectedBakedSite();
    }

    /// <summary>Repair existing preview soil shading without resampling terrain or moving equipment.</summary>
    [MenuItem("Tools/Excavator/Construction Site/Repair Preview Soil Shading")]
    public static void RepairShadingBatch()
    {
        EditorSceneManager.OpenScene(ScenePath);
        var snapshot = AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(DirectoryPath + "/ConstructionSite.asset");
        if (snapshot == null) throw new InvalidOperationException("缺少原地图融合预览快照。");
        foreach (TerrainLayer layer in snapshot.terrainData.terrainLayers)
        {
            layer.smoothnessSource = TerrainLayerSmoothnessSource.ConstantOnly;
            layer.smoothness = 0f;
            layer.metallic = 0f;
            EditorUtility.SetDirty(layer);
        }
        string materialPath = DirectoryPath + "/MatteSoil.mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            material = UnityEngine.Object.Instantiate(snapshot.terrainMaterial);
            material.name = "原地图融合哑光泥土";
            AssetDatabase.CreateAsset(material, materialPath);
        }
        for (int i = 0; i < 4; i++)
        {
            material.SetFloat("_Smoothness" + i, 0f);
            material.SetFloat("_Metallic" + i, 0f);
        }
        snapshot.terrainMaterial = material;
        EditorUtility.SetDirty(material);
        EditorUtility.SetDirty(snapshot);
        EditorUtility.SetDirty(snapshot.terrainData);
        AssetDatabase.SaveAssets();
        Selection.activeObject = snapshot;
        ConstructionSiteBaker.ApplySelectedBakedSite();
    }

    /// <summary>Reapply the map-following snapshot without regenerating or overwriting its assets.</summary>
    public static void ApplyExistingBatch()
    {
        EditorSceneManager.OpenScene(ScenePath);
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(DirectoryPath + "/ConstructionSite.asset");
        ConstructionSiteBaker.ApplySelectedBakedSite();
    }

    /// <summary>Open the preview scene and wait for local Cesium tiles before baking, then exit the batch editor.</summary>
    public static void BakeBatch()
    {
        restoringParking = false;
        StartMapSurvey();
    }

    private static bool restoringParking;

    /// <summary>Resample original map heights and remove both authored truck pads without reshaping the track core.</summary>
    public static void RestoreNaturalParkingBatch()
    {
        restoringParking = true;
        StartMapSurvey();
    }

    private static void StartMapSurvey()
    {
        EditorSceneManager.OpenScene(ScenePath);
        CesiumMoundSite site = UnityEngine.Object.FindFirstObjectByType<CesiumMoundSite>();
        foreach (ConstructionSiteMapClip clip in site.tileset.GetComponents<ConstructionSiteMapClip>()) clip.enabled = false;
        if (site.soil != null && site.soil.Terrain != null) site.soil.Terrain.enabled = false;
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
            try
            {
                if (restoringParking) RestoreNaturalParking();
                else Bake();
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }
        else if (EditorApplication.timeSinceStartup > deadline)
        {
            EditorApplication.update -= WaitForMap;
            Debug.LogError("土堆预览烘焙未获得 Cesium 碰撞瓦片；没有保存任何替换资源。");
            EditorApplication.Exit(1);
        }
    }

    private static void RestoreNaturalParking()
    {
        var site = UnityEngine.Object.FindFirstObjectByType<CesiumMoundSite>();
        var snapshot = site.bakedSite;
        TerrainData data = snapshot.terrainData;
        int n = data.heightmapResolution;
        float[,] before = data.GetHeights(0, 0, n, n);
        float[,] sampled = new float[n, n];
        bool[,] equipment = new bool[n, n];
        bool[,] restore = new bool[n, n];
        float[,] restoreWeight = new float[n, n];
        MeshCollider[] map = site.tileset.GetComponentsInChildren<MeshCollider>()
            .Where(collider => collider.enabled && collider.sharedMesh != null).ToArray();
        Physics.SyncTransforms();
        Quaternion inverse = Quaternion.Inverse(Quaternion.Euler(0f, 239.868f, 0f));
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                Vector3 world = snapshot.terrainOrigin + new Vector3(x * data.size.x / (n - 1), 0f,
                    z * data.size.z / (n - 1));
                if (!SampleMap(map, world, out float height))
                    throw new InvalidOperationException("原地图采样不完整，不保存停车地形。");
                sampled[z, x] = (height + .015f - snapshot.terrainOrigin.y) / data.size.y;
                Vector2 point = new Vector2(world.x, world.z);
                equipment[z, x] = EquipmentWeight(point) > 0f || IsFrontShardArea(point);
                Vector3 oldLocal = inverse * (world - new Vector3(316.28f, 0f, 352.43f));
                Vector3 newLocal = inverse * (world - snapshot.truckPosition);
                float oldPad = Mathf.Max(Mathf.Abs(oldLocal.x) / 1.7f, Mathf.Abs(oldLocal.z) / 3.8f);
                float newPad = Mathf.Max(Mathf.Abs(newLocal.x) / 1.7f, Mathf.Abs(newLocal.z - .8f) / 4.8f);
                bool scanTruck = new Vector2((world.x - 316.4f) / 6.1f, (world.z - 351.7f) / 4.6f).sqrMagnitude < 1f;
                float machineDistance = Vector2.Distance(point,
                    new Vector2(snapshot.machinePosition.x, snapshot.machinePosition.z));
                restore[z, x] = machineDistance >= 3.5f && (oldPad < 1.3f || newPad < 1.3f || scanTruck);
                float padWeight = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(1f, 1.3f, Mathf.Min(oldPad, newPad)));
                float scanRadius = new Vector2((world.x - 316.4f) / 6.1f, (world.z - 351.7f) / 4.6f).magnitude;
                float scanWeight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.85f, 1f, scanRadius));
                float trackWeight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3.5f, 4.5f, machineDistance));
                restoreWeight[z, x] = Mathf.Max(padWeight, scanWeight) * trackWeight;
            }
        float[,] natural = MapFollowingSoilSurface.RepairHeights(sampled, equipment);
        float[,] after = (float[,])before.Clone();
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                if (restore[z, x]) after[z, x] = Mathf.Lerp(before[z, x], Mathf.Clamp01(natural[z, x]), restoreWeight[z, x]);
        data.SetHeights(0, 0, after);
        after = data.GetHeights(0, 0, n, n);
        snapshot.initialSoilVolumeM3 = 0;
        double cell = (double)data.size.x * data.size.y * data.size.z / ((n - 1) * (n - 1));
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int index = z * n + x;
                float thickness = Mathf.Max(0f, before[z, x] - snapshot.groundHeights[index]);
                snapshot.groundHeights[index] = Mathf.Max(0f, after[z, x] - thickness);
                snapshot.soilMask[index] = thickness > .00001f ? 1f : 0f;
                snapshot.initialSoilVolumeM3 += (after[z, x] - snapshot.groundHeights[index]) * cell *
                    (x == 0 || x == n - 1 ? .5 : 1) * (z == 0 || z == n - 1 ? .5 : 1);
            }
        FitTruckToNaturalGround(site, snapshot);
        Vector3 center = snapshot.truckPosition;
        if (!snapshot.TryValidate(snapshot.sourceMapName, snapshot.georeferenceLatitude,
            snapshot.georeferenceLongitude, snapshot.georeferenceHeight, out string reason))
            throw new InvalidOperationException(reason);
        EditorUtility.SetDirty(data);
        EditorUtility.SetDirty(snapshot);
        AssetDatabase.SaveAssets();
        if (surveyCamera != null)
            CesiumForUnity.CesiumCameraManager.GetOrCreate(site.tileset.gameObject).additionalCameras.Remove(surveyCamera);
        Selection.activeObject = snapshot;
        ConstructionSiteBaker.ApplySelectedBakedSite();
        Debug.Log($"自然停车地面已恢复，矿车位置={center}，姿态={snapshot.truckRotation.eulerAngles}");
    }

    /// <summary>Fit the saved truck's real wheel colliders to natural terrain without modifying terrain heights.</summary>
    public static void FitNaturalTruckParkingBatch()
    {
        EditorSceneManager.OpenScene(ScenePath);
        var site = UnityEngine.Object.FindFirstObjectByType<CesiumMoundSite>();
        FitTruckToNaturalGround(site, site.bakedSite);
        EditorUtility.SetDirty(site.bakedSite);
        AssetDatabase.SaveAssets();
        Selection.activeObject = site.bakedSite;
        ConstructionSiteBaker.ApplySelectedBakedSite();
    }

    private static void FitTruckToNaturalGround(CesiumMoundSite site, BakedConstructionSiteData snapshot)
    {
        Collider[] wheels = site.truck.GetComponentsInChildren<Collider>().Where(wheel =>
            wheel.enabled && wheel.name.ToLowerInvariant().Contains("wheel") && wheel.name.Contains("collision")).ToArray();
        if (wheels.Length < 4) throw new InvalidOperationException("缺少轮胎实体，不能保存停车布局。");
        TerrainData data = snapshot.terrainData;
        float Surface(Vector3 world) => snapshot.terrainOrigin.y + data.GetInterpolatedHeight(
            (world.x - snapshot.terrainOrigin.x) / data.size.x,
            (world.z - snapshot.terrainOrigin.z) / data.size.z);
        Vector3 origin = snapshot.truckPosition;
        Vector3 bestPosition = origin;
        Quaternion bestRotation = snapshot.truckRotation;
        float bestPitch = 0f, bestRoll = 0f, bestScore = float.PositiveInfinity, bestGap = float.PositiveInfinity;
        void Consider(Vector3 position, float pitch, float roll)
        {
            if (Mathf.Abs(pitch) > 5f || Mathf.Abs(roll) > 3f) return;
            float distance = Vector2.Distance(new Vector2(position.x, position.z),
                new Vector2(snapshot.machinePosition.x, snapshot.machinePosition.z));
            if (distance < 5.5f || distance > 6.5f) return;
            position.y = 0f;
            Quaternion rotation = Quaternion.Euler(pitch, 239.868f, roll);
            site.truck.SetPositionAndRotation(position, rotation);
            Physics.SyncTransforms();
            float highest = float.NegativeInfinity, lowest = float.PositiveInfinity;
            foreach (Collider wheel in wheels)
            {
                var box = wheel as BoxCollider;
                if (box == null) throw new InvalidOperationException("轮胎支撑拟合需要当前矿卡的盒碰撞体。");
                float required = float.NegativeInfinity;
                for (int x = -4; x <= 4; x++)
                    for (int z = -8; z <= 8; z++)
                    {
                        Vector3 bottom = box.transform.TransformPoint(box.center +
                            new Vector3(x * box.size.x / 8f, -box.size.y * .5f, z * box.size.z / 16f));
                        required = Mathf.Max(required, Surface(bottom) - bottom.y);
                    }
                highest = Mathf.Max(highest, required);
                lowest = Mathf.Min(lowest, required);
            }
            float gap = highest - lowest;
            Vector2 offset = new Vector2(position.x - origin.x, position.z - origin.z);
            float score = gap + offset.sqrMagnitude * .005f + (pitch * pitch + roll * roll) * .001f;
            if (score >= bestScore) return;
            bestScore = score;
            bestGap = gap;
            position.y = highest + .01f; // Reserve 1 cm for terrain triangle interpolation between samples.
            bestPosition = position;
            bestRotation = rotation;
            bestPitch = pitch;
            bestRoll = roll;
        }
        // Search a small nearby parking region using actual wheel support, not a center-point slope estimate.
        for (int x = -4; x <= 4; x++)
            for (int z = -4; z <= 4; z++)
                for (int pitch = -5; pitch <= 5; pitch++)
                    for (int roll = -3; roll <= 3; roll++)
                        Consider(origin + new Vector3(x * .6f, 0f, z * .6f), pitch, roll);
        Vector3 coarsePosition = bestPosition;
        float coarsePitch = bestPitch, coarseRoll = bestRoll;
        for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                for (int pitch = -2; pitch <= 2; pitch++)
                    for (int roll = -2; roll <= 2; roll++)
                        Consider(coarsePosition + new Vector3(x * .2f, 0f, z * .2f), coarsePitch + pitch, coarseRoll + roll);
        if (bestGap >= .14f)
        {
            site.truck.SetPositionAndRotation(origin, snapshot.truckRotation);
            throw new InvalidOperationException($"附近自然地形无法满足轮胎支撑，最大离地={bestGap:F3}m；不保存悬空停车布局。");
        }
        snapshot.truckPosition = bestPosition;
        snapshot.truckRotation = bestRotation;
        snapshot.selectedTruckGroundY = Surface(bestPosition);
        site.truck.SetPositionAndRotation(bestPosition, bestRotation);
        Physics.SyncTransforms();
        Debug.Log($"自然轮胎支撑：矿车={bestPosition}，姿态={bestRotation.eulerAngles}，最大轮间支撑差={bestGap:F4}m");
    }

    /// <summary>Blend a gently graded loading corridor into the saved mound, fit the real wheels and save the scene.</summary>
    /// <remarks>Batch-only authoring operation; preserves local soil thickness and the central track-bearing core.</remarks>
    public static void BlendLoadingGroundBatch()
    {
        EditorSceneManager.OpenScene(ScenePath);
        var site = UnityEngine.Object.FindFirstObjectByType<CesiumMoundSite>();
        var snapshot = site.bakedSite;
        BlendLoadingGround(site, snapshot);
        EditorUtility.SetDirty(snapshot.terrainData);
        EditorUtility.SetDirty(snapshot);
        AssetDatabase.SaveAssets();
        Selection.activeObject = snapshot;
        ConstructionSiteBaker.ApplySelectedBakedSite();
        Debug.Log("装车区已生成连续缓坡，履带核心和土层厚度保持不变。");
    }

    private static void BlendLoadingGround(CesiumMoundSite site, BakedConstructionSiteData snapshot)
    {
        TerrainData data = snapshot.terrainData;
        int n = data.heightmapResolution;
        float[,] heights = data.GetHeights(0, 0, n, n);
        float[] floor = (float[])snapshot.groundHeights.Clone();
        float[] mask = (float[])snapshot.soilMask.Clone();
        double volume = snapshot.initialSoilVolumeM3;
        Vector3 position = snapshot.truckPosition;
        Quaternion rotation = snapshot.truckRotation;
        float groundY = snapshot.selectedTruckGroundY;
        Vector3 actualPosition = site.truck.position;
        Quaternion actualRotation = site.truck.rotation;
        try { PrepareLoadingGround(site, snapshot); }
        catch
        {
            // Terrain and the soil ledger form one authoring transaction. A failed parking search
            // must not leave modified asset objects available for a later accidental save.
            data.SetHeights(0, 0, heights);
            Array.Copy(floor, snapshot.groundHeights, floor.Length);
            Array.Copy(mask, snapshot.soilMask, mask.Length);
            snapshot.initialSoilVolumeM3 = volume;
            snapshot.truckPosition = position;
            snapshot.truckRotation = rotation;
            snapshot.selectedTruckGroundY = groundY;
            site.truck.SetPositionAndRotation(actualPosition, actualRotation);
            Physics.SyncTransforms();
            throw;
        }
    }

    private static void PrepareLoadingGround(CesiumMoundSite site, BakedConstructionSiteData snapshot)
    {
        TerrainData data = snapshot.terrainData;
        int n = data.heightmapResolution;
        float[,] before = data.GetHeights(0, 0, n, n);
        float[,] after = (float[,])before.Clone();
        Vector2 machine = new Vector2(snapshot.machinePosition.x, snapshot.machinePosition.z);
        Vector2 truck = new Vector2(snapshot.truckPosition.x, snapshot.truckPosition.z);
        Vector2 direction = truck - machine;
        float lengthSquared = direction.sqrMagnitude;
        Quaternion inverse = Quaternion.Inverse(Quaternion.Euler(0f, 239.868f, 0f));
        float machineHeight = snapshot.terrainOrigin.y + data.GetInterpolatedHeight(
            (machine.x - snapshot.terrainOrigin.x) / data.size.x,
            (machine.y - snapshot.terrainOrigin.z) / data.size.z);
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                Vector3 world = snapshot.terrainOrigin + new Vector3(x * data.size.x / (n - 1), 0f, z * data.size.z / (n - 1));
                Vector2 point = new Vector2(world.x, world.z);
                float projectedDistance = Vector2.Dot(point - machine, direction.normalized);
                float along = Mathf.Clamp01(projectedDistance / direction.magnitude);
                float distance = Vector2.Distance(point, machine + direction * along);
                float corridorWeight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.8f, 4.8f, distance));
                Vector3 parking = inverse * (world - snapshot.truckPosition);
                float parkingRadius = new Vector2(parking.x / 2.1f, (parking.z - .8f) / 4.7f).magnitude;
                float parkingWeight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f, 1.8f, parkingRadius));
                // Leave the bearing core untouched. Away from it, a shallow plane connects both machines,
                // with a wide feather rather than the old one-metre transition into a deep depression.
                float coreWeight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.8f, 2.8f, Vector2.Distance(point, machine)));
                float loadingSideWeight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 1.2f, projectedDistance));
                float weight = Mathf.Max(corridorWeight, parkingWeight) * coreWeight * loadingSideWeight;
                if (weight <= 0f) continue;
                Vector3 relative = inverse * (world - new Vector3(machine.x, 0f, machine.y));
                float target = (machineHeight + relative.z * .025f - snapshot.terrainOrigin.y) / data.size.y;
                after[z, x] = Mathf.Lerp(before[z, x], Mathf.Clamp01(target), weight);
            }
        data.SetHeights(0, 0, after);
        after = data.GetHeights(0, 0, n, n);
        snapshot.initialSoilVolumeM3 = 0;
        double cell = (double)data.size.x * data.size.y * data.size.z / ((n - 1) * (n - 1));
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int index = z * n + x;
                float thickness = Mathf.Max(0f, before[z, x] - snapshot.groundHeights[index]);
                snapshot.groundHeights[index] = Mathf.Max(0f, after[z, x] - thickness);
                snapshot.soilMask[index] = thickness > .00001f ? 1f : 0f;
                snapshot.initialSoilVolumeM3 += (after[z, x] - snapshot.groundHeights[index]) * cell *
                    (x == 0 || x == n - 1 ? .5 : 1) * (z == 0 || z == n - 1 ? .5 : 1);
            }
        FitTruckToNaturalGround(site, snapshot);
        if (!snapshot.TryValidate(snapshot.sourceMapName, snapshot.georeferenceLatitude,
            snapshot.georeferenceLongitude, snapshot.georeferenceHeight, out string reason))
            throw new InvalidOperationException(reason);
    }

    /// <summary>Sample original map elevations, repair scanned equipment and save the independently baked preview.</summary>
    [MenuItem("Tools/Excavator/Construction Site/Build Map Following Mound Preview")]
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

        Vector3 machinePosition = new Vector3(325.28f, 1.43f, 347.43f);
        Quaternion rotation = Quaternion.Euler(0f, 31.008f, 0f);
        float rootOffset = original.machinePosition.y - original.selectedSupportY;
        float supportY;
        float benchY;
        var origin = new Vector3(306.28f, -8f, 337.43f);
        var size = new Vector3(28f, 25f, 28f);
        const int n = 257;
        var terrain = new TerrainData
        {
            name = "堆顶与转角可挖土堆", heightmapResolution = n,
            alphamapResolution = 256, size = size
        };
        var top = new float[n, n];
        var floor = new float[n, n];
        var equipment = new bool[n, n];
        Vector2[] boundary = { new Vector2(309f,338f), new Vector2(330f,338f),
            new Vector2(334f,340f), new Vector2(334f,350f), new Vector2(332f,355f),
            new Vector2(326f,358f), new Vector2(313f,359f), new Vector2(309f,356f), new Vector2(307f,348f) };
        var snapshot = ScriptableObject.CreateInstance<BakedConstructionSiteData>();
        snapshot.groundHeights = new float[n * n];
        snapshot.soilMask = new float[n * n];
        int sampledEdges = 0, missingEdges = 0;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                Vector3 world = origin + new Vector3(x * size.x / (n - 1f), 0f, z * size.z / (n - 1f));
                if (SampleMap(map, world, out float mapY)) { top[z,x] = mapY; sampledEdges++; }
                else missingEdges++;
                Vector2 point = new Vector2(world.x, world.z);
                equipment[z,x] = EquipmentWeight(point) > 0f || IsFrontShardArea(point);
            }
        if (missingEdges > 0)
        {
            UnityEngine.Object.DestroyImmediate(terrain);
            UnityEngine.Object.DestroyImmediate(snapshot);
            throw new InvalidOperationException($"土堆边缘 Cesium 覆盖不足：缺失 {missingEdges} 个样本；等待地图加载后重试。");
        }
        top = MapFollowingSoilSurface.RepairHeights(top, equipment);
        int machineX = Mathf.RoundToInt((machinePosition.x-origin.x)/size.x*(n-1));
        int machineZ = Mathf.RoundToInt((machinePosition.z-origin.z)/size.z*(n-1));
        supportY = top[machineZ,machineX] + .1f;
        Vector3 truckPosition = new Vector3(316.28f, 0f, 352.43f);
        benchY = top[Mathf.RoundToInt((truckPosition.z-origin.z)/size.z*(n-1)),
            Mathf.RoundToInt((truckPosition.x-origin.x)/size.x*(n-1))] + .05f;
        machinePosition.y = supportY + rootOffset;
        var holes = new bool[n-1,n-1];
        for (int z=0; z<n; z++)
            for (int x=0; x<n; x++)
            {
                Vector2 point = new Vector2(origin.x+x*size.x/(n-1), origin.z+z*size.z/(n-1));
                float machineDistance = Vector2.Distance(point,new Vector2(machinePosition.x,machinePosition.z));
                Vector3 truckLocal = Quaternion.Inverse(Quaternion.Euler(0f,239.868f,0f)) *
                    new Vector3(point.x-truckPosition.x,0f,point.y-truckPosition.z);
                float truckDistance = Mathf.Max(Mathf.Abs(truckLocal.x)/1.7f,Mathf.Abs(truckLocal.z)/3.8f);
                float bearing = 1f-Mathf.SmoothStep(0f,1f,(machineDistance-2.5f)/1f);
                float truckBearing = 1f-Mathf.SmoothStep(0f,1f,(truckDistance-1f)/.3f);
                float height = Mathf.Lerp(top[z,x]+.015f,supportY,bearing);
                height = Mathf.Lerp(height,benchY,truckBearing);
                bool inside = MapFollowingSoilSurface.Contains(boundary,point);
                // Retained border cells extend slightly past the exact clip. Recess their outer vertices
                // below the untouched map so they cannot form an elevated rim or coplanar flashing strip.
                if (!inside) height = top[z,x] - .02f;
                top[z,x] = Mathf.Clamp01((height-origin.y)/size.y);
                floor[z,x] = Mathf.Clamp01((height-(inside && bearing<.99f && truckBearing<.99f?1.6f:0f)-origin.y)/size.y);
                if (z<n-1 && x<n-1)
                {
                    // Conservatively keep edge cells so the exact Cesium clip never reveals a narrow gap.
                    bool overlap=true;
                    for(int edgeIndex=0; edgeIndex<boundary.Length; edgeIndex++)
                    {
                        Vector2 a=boundary[edgeIndex], edge=boundary[(edgeIndex+1)%boundary.Length]-a;
                        float cross=edge.x*(point.y-a.y)-edge.y*(point.x-a.x);
                        float maximum=cross+Mathf.Max(0f,-edge.y*size.x/(n-1))+Mathf.Max(0f,edge.x*size.z/(n-1));
                        if(maximum<0f) { overlap=false; break; }
                    }
                    holes[z,x]=overlap;
                }
            }
        terrain.SetHeights(0, 0, top);
        terrain.SetHoles(0,0,holes);
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
        Texture2D dirtImage = CopyCleanMapSoil();
        var dirt = new TerrainLayer { name = "Cesium 同色净土", diffuseTexture = dirtImage,
            tileSize = new Vector2(28f, 28f), smoothness = 0f, metallic = 0f,
            smoothnessSource = TerrainLayerSmoothnessSource.ConstantOnly };
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
        snapshot.truckPosition = truckPosition;
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
        snapshot.replacementPolygonXZ = boundary;
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

    private static Texture2D CopyCleanMapSoil()
    {
        var original = new Texture2D(2,2,TextureFormat.RGB24,false);
        var repaired = new Texture2D(2,2,TextureFormat.RGB24,false);
        try
        {
            if(!original.LoadImage(File.ReadAllBytes(DirectoryPath+"/OriginalCorner.png")) ||
                !repaired.LoadImage(File.ReadAllBytes(DirectoryPath+"/RepairedCorner.png")))
                throw new InvalidOperationException("缺少原地图及设备清除影像。");
            Color[] pixels=original.GetPixels();
            for(int z=0;z<original.height;z++)
                for(int x=0;x<original.width;x++)
                {
                    float u=x/(original.width-1f),v=z/(original.height-1f);
                    float weight=EquipmentWeight(new Vector2(306.28f+28f*u,337.43f+28f*v));
                    if(weight>0f) pixels[z*original.width+x]=Color.Lerp(pixels[z*original.width+x],repaired.GetPixelBilinear(u,v),weight);
                }
            original.SetPixels(pixels); original.Apply();
            original.name="原地图局部净土影像"; original.wrapMode=TextureWrapMode.Clamp;
            return original;
        }
        finally { UnityEngine.Object.DestroyImmediate(repaired); }
    }

    private static float EquipmentWeight(Vector2 point)
    {
        float truck = new Vector2((point.x-316.4f)/6.1f,(point.y-351.7f)/4.6f).magnitude;
        float excavator = new Vector2((point.x-326.9f)/5.8f,(point.y-346.5f)/4.7f).magnitude;
        float arm = new Vector2((point.x-332.3f)/1.6f,(point.y-343.6f)/2.4f).magnitude;
        float scanDebris = new Vector2((point.x-326f)/4f,(point.y-340.5f)/2f).magnitude;
        return 1f-Mathf.SmoothStep(0f,1f,(Mathf.Min(Mathf.Min(truck,excavator,arm),scanDebris)-.85f)/.15f);
    }

    private static bool IsFrontShardArea(Vector2 point)
    {
        // The measured shard at (321.92,341.04) lay just outside the earlier equipment ellipse.
        bool moundShard = new Vector2((point.x - 322f) / 3f, (point.y - 341f) / 2.2f).sqrMagnitude < 1f;
        bool thinPlate = new Vector2((point.x - 319f) / 1.6f, (point.y - 339.3f) / 1.2f).sqrMagnitude < 1f;
        return moundShard || thinPlate;
    }
}
