using System;
using UnityEngine;

/// <summary>Persistent snapshot of one surveyed Cesium construction footprint and its initial cuttable soil.</summary>
public sealed class BakedConstructionSiteData : ScriptableObject
{
    public const int CurrentVersion = 2;
    public int formatVersion = CurrentVersion;
    public string sourceMapName;
    public double georeferenceLatitude;
    public double georeferenceLongitude;
    public double georeferenceHeight;
    public TerrainData terrainData;
    public Material terrainMaterial;
    public Vector3 terrainOrigin;
    public Vector3 machinePosition;
    public Quaternion machineRotation = Quaternion.identity;
    public Vector3 truckPosition;
    public Quaternion truckRotation = Quaternion.identity;
    public Vector3 workFaceCenter;
    public Quaternion workFaceRotation = Quaternion.identity;
    public Vector2 workFaceSize;
    public float originalSupportY;
    public float selectedSupportY;
    public float selectedTruckGroundY;
    public float readyTipClearanceMeters;
    public float selectedSurfaceNormalY;
    public float maximumCutDepth;
    [Tooltip("This snapshot replaces Cesium geometry only inside its fully filled Terrain footprint.")]
    public bool replacesCesiumSurface;
    [Tooltip("Tracks stand on the baked mound rather than the original Cesium lower bench.")]
    public bool supportsTracksOnTerrain;
    [Tooltip("Optional counter-clockwise convex boundary in world X/Z; old snapshots use their rectangle.")]
    public Vector2[] replacementPolygonXZ;
    public double initialSoilVolumeM3;
    // Row-major [z * resolution + x]. These are source-ground elevations and the authored soil-band mask.
    public float[] groundHeights;
    public float[] soilMask;

    /// <summary>Validate the baked height field, soil baseline and map reference before using it for excavation.</summary>
    /// <param name="mapName">Configured Cesium dataset identifier.</param>
    /// <param name="latitude">Current georeference latitude in degrees.</param>
    /// <param name="longitude">Current georeference longitude in degrees.</param>
    /// <param name="height">Current georeference height in metres.</param>
    /// <param name="reason">Human-readable reason on failure; empty on success.</param>
    /// <returns>True only if the persistent snapshot is internally consistent and belongs to this map origin.</returns>
    public bool TryValidate(string mapName, double latitude, double longitude, double height, out string reason)
    {
        reason = string.Empty;
        if (formatVersion != CurrentVersion) { reason = "烘焙数据版本不匹配，需要重新烘焙。"; return false; }
        if (string.IsNullOrWhiteSpace(sourceMapName) || !string.Equals(sourceMapName, mapName, StringComparison.Ordinal))
        { reason = "烘焙资源与当前地图不匹配，需要重新烘焙。"; return false; }
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(height) ||
            !double.IsFinite(georeferenceLatitude) || !double.IsFinite(georeferenceLongitude) ||
            !double.IsFinite(georeferenceHeight) || Math.Abs(latitude - georeferenceLatitude) > 1e-7 ||
            Math.Abs(longitude - georeferenceLongitude) > 1e-7 || Math.Abs(height - georeferenceHeight) > .01)
        { reason = "烘焙资源地理参考与场景不匹配，需要重新烘焙。"; return false; }
        if (terrainData == null || terrainMaterial == null || !Finite(terrainOrigin) || !Finite(machinePosition) || !Finite(truckPosition) ||
            !Finite(machineRotation) || !Finite(truckRotation) || Quaternion.Dot(machineRotation, machineRotation) < .9f ||
            Quaternion.Dot(truckRotation, truckRotation) < .9f)
        { reason = "烘焙资源缺少地形/材质或设备布局损坏，需要重新烘焙。"; return false; }
        if (!Finite(workFaceCenter) || !Finite(workFaceRotation) || Quaternion.Dot(workFaceRotation, workFaceRotation) < .9f ||
            !float.IsFinite(workFaceSize.x) || !float.IsFinite(workFaceSize.y) ||
            workFaceSize.x <= 0f || workFaceSize.y <= 0f)
        { reason = "烘焙资源缺少坡面替换范围，需要重新烘焙。"; return false; }
        Vector3 size = terrainData.size;
        int n = terrainData.heightmapResolution;
        if (!Finite(size) || size.x <= 0 || size.y <= 0 || size.z <= 0 || n < 3 ||
            groundHeights == null || soilMask == null || groundHeights.Length != n * n || soilMask.Length != n * n)
        { reason = "烘焙资源的基础地面或土带掩码尺寸损坏，需要重新烘焙。"; return false; }
        TerrainLayer[] layers = terrainData.terrainLayers;
        if (layers == null || layers.Length < 2 || layers[1] == null || layers[1].diffuseTexture == null)
        { reason = "烘焙资源缺少贴合地图的坡面影像材质，需要在图形模式下重新烘焙。"; return false; }
        if (!double.IsFinite(initialSoilVolumeM3) || initialSoilVolumeM3 <= 0)
        { reason = "烘焙资源的初始土量无效，需要重新烘焙。"; return false; }
        float[,] top;
        try { top = terrainData.GetHeights(0, 0, n, n); }
        catch (Exception exception)
        { reason = "烘焙高度图无法读取，需要重新烘焙：" + exception.Message; return false; }
        double volume = 0;
        double cell = (double)size.x * size.y * size.z / ((n - 1) * (n - 1));
        bool hasBand = false;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int index = z * n + x;
                float floor = groundHeights[index], mask = soilMask[index], surface = top[z, x];
                if (!float.IsFinite(floor) || floor < 0 || floor > 1 || !float.IsFinite(surface) ||
                    surface < floor - 1e-5f || surface > 1f)
                {
                    reason = $"烘焙资源的基础地面高于土层或高度数据损坏（x={x}, z={z}, " +
                        $"顶面={surface:F7}, 基础={floor:F7}, 差={surface - floor:F7}）。若刚手工修改 Terrain，请退出 Play 后执行 Tools/Excavator/Construction Site/Sync Manually Edited Preview Terrain；数据损坏则需重新烘焙。";
                    return false;
                }
                if (!float.IsFinite(mask) || mask < 0 || mask > 1 ||
                    (surface - floor > 1e-4f && mask < 1e-4f))
                { reason = "烘焙资源的土带掩码与顶面不一致，需要重新烘焙。"; return false; }
                hasBand |= mask > .01f;
                volume += (surface - floor) * cell *
                    (x == 0 || x == n - 1 ? .5 : 1) * (z == 0 || z == n - 1 ? .5 : 1);
            }
        if (!hasBand || Math.Abs(volume - initialSoilVolumeM3) > Math.Max(.02, initialSoilVolumeM3 * .01))
        { reason = "烘焙资源的完整土量基线不一致；若刚手工修改 Terrain，请退出 Play 后执行 Tools/Excavator/Construction Site/Sync Manually Edited Preview Terrain，否则需要重新烘焙。"; return false; }
        return true;
    }

    /// <summary>Get the world-axis-aligned bounds of the oriented slope replacement.</summary>
    /// <returns>Bounds enclosing the slope footprint and the full vertical Terrain range.</returns>
    public Bounds GetWorkFaceBounds()
    {
        if (terrainData == null) return default;
        if (workFaceSize.x <= 0f || workFaceSize.y <= 0f)
            return new Bounds(terrainOrigin + terrainData.size * .5f, terrainData.size);
        return ConstructionSiteFootprint.CalculateBounds(workFaceCenter, workFaceRotation, workFaceSize,
            terrainOrigin.y, terrainOrigin.y + terrainData.size.y);
    }

    /// <summary>Prepare the soil component from this snapshot using its own mutable TerrainData copy.</summary>
    /// <param name="soil">Terrain-bearing soil component in the delivery scene.</param>
    /// <exception cref="InvalidOperationException">The target already uses a different source Terrain.</exception>
    public void ApplyTo(DeformableSoilTerrain soil)
    {
        if (soil == null || terrainData == null) throw new ArgumentException("烘焙土坡及目标 Terrain 均不可为空。");
        Terrain terrain = soil.GetComponent<Terrain>();
        TerrainCollider collider = soil.SoilCollider;
        if (soil.Terrain == null)
        {
            terrain.terrainData = terrainData;
            collider.terrainData = terrainData;
        }
        else if (soil.SourceTerrainData != terrainData)
            throw new InvalidOperationException("目标 Terrain 已从其他资源初始化；请重新加载场景。");
        soil.transform.position = terrainOrigin;
        soil.InitializeBaked(terrain);
        soil.deferAutomaticInitialization = false;
        int n = terrainData.heightmapResolution;
        var ground = new float[n, n];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++) ground[z, x] = groundHeights[z * n + x];
        soil.PrepareMoundSurface(terrainData.GetHeights(0, 0, n, n), ground);
        if (maximumCutDepth > 0) soil.maximumCutDepth = maximumCutDepth;
        terrain.materialTemplate = terrainMaterial;
    }

    /// <summary>Reconcile the soil ledger after an explicit Terrain sculpting operation without changing its surface.</summary>
    /// <remarks>Authoring only. Non-soil cells remain non-soil; existing soil floors are only lowered where necessary.</remarks>
    /// <exception cref="InvalidOperationException">Metadata is corrupt or the edited resource cannot form a valid soil region.</exception>
    public void ReconcileEditedSurface()
    {
        if (Application.isPlaying) throw new InvalidOperationException("请退出 Play 后同步手工地形。");
        if (terrainData == null) throw new InvalidOperationException("缺少 Terrain 资源。");
        int n = terrainData.heightmapResolution;
        if (groundHeights == null || soilMask == null || groundHeights.Length != n * n || soilMask.Length != n * n)
            throw new InvalidOperationException("土层底面/掩码尺寸不匹配，不能自动修复。");
        float[,] top = terrainData.GetHeights(0, 0, n, n);
        float[] repaired = new float[n * n];
        Vector3 size = terrainData.size;
        double cell = (double)size.x * size.y * size.z / ((n - 1) * (n - 1));
        double volume = 0;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int i = z * n + x;
                float floor = groundHeights[i], mask = soilMask[i], surface = top[z, x];
                if (!float.IsFinite(floor) || floor < 0 || floor > 1 || !float.IsFinite(mask) || mask < 0 || mask > 1)
                    throw new InvalidOperationException("土层元数据损坏，不能用手工高度覆盖掩盖。");
                repaired[i] = mask < 1e-4f ? surface : Mathf.Min(surface, floor);
                volume += (surface - repaired[i]) * cell *
                    (x == 0 || x == n - 1 ? .5 : 1) * (z == 0 || z == n - 1 ? .5 : 1);
            }
        float[] previous = groundHeights;
        double previousVolume = initialSoilVolumeM3;
        groundHeights = repaired;
        initialSoilVolumeM3 = volume;
        if (!TryValidate(sourceMapName, georeferenceLatitude, georeferenceLongitude, georeferenceHeight, out string reason))
        {
            groundHeights = previous;
            initialSoilVolumeM3 = previousVolume;
            throw new InvalidOperationException(reason);
        }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Finite(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y) &&
        float.IsFinite(value.z) && float.IsFinite(value.w);
}
