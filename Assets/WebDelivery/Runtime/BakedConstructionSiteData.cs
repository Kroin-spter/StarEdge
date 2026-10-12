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
                        $"顶面={surface:F7}, 基础={floor:F7}, 差={surface - floor:F7}），需要重新烘焙。";
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
        { reason = "烘焙资源的完整土量基线不一致，需要重新烘焙。"; return false; }
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

    /// <summary>Sample the surveyed Cesium ground beneath a machine or truck support point.</summary>
    /// <param name="worldPoint">World position whose X/Z coordinates select the map sample.</param>
    /// <param name="worldY">Interpolated immutable map-ground elevation in world metres.</param>
    /// <returns>False for missing data, a nonfinite point, or a point outside the baked survey.</returns>
    public bool TrySampleGroundWorldY(Vector3 worldPoint, out float worldY)
    {
        worldY = 0f;
        if (terrainData == null || groundHeights == null || !Finite(worldPoint)) return false;
        int resolution = terrainData.heightmapResolution;
        if (resolution < 2 || groundHeights.Length != resolution * resolution) return false;
        Vector3 size = terrainData.size;
        if (!Finite(size) || size.x <= 0f || size.y <= 0f || size.z <= 0f) return false;
        float x = (worldPoint.x - terrainOrigin.x) / size.x;
        float z = (worldPoint.z - terrainOrigin.z) / size.z;
        if (x < 0f || x > 1f || z < 0f || z > 1f) return false;
        float gridX = x * (resolution - 1);
        float gridZ = z * (resolution - 1);
        int x0 = Mathf.FloorToInt(gridX), z0 = Mathf.FloorToInt(gridZ);
        int x1 = Mathf.Min(x0 + 1, resolution - 1);
        int z1 = Mathf.Min(z0 + 1, resolution - 1);
        float bottom = Mathf.Lerp(groundHeights[z0 * resolution + x0],
            groundHeights[z0 * resolution + x1], gridX - x0);
        float top = Mathf.Lerp(groundHeights[z1 * resolution + x0],
            groundHeights[z1 * resolution + x1], gridX - x0);
        worldY = terrainOrigin.y + Mathf.Lerp(bottom, top, gridZ - z0) * size.y;
        return float.IsFinite(worldY);
    }

    /// <summary>Create a finer local slope snapshot without changing the surveyed ground or source assets.</summary>
    /// <param name="center">World-space horizontal centre of the local patch; Y is ignored.</param>
    /// <param name="size">Patch width and depth in metres, entirely inside the source survey.</param>
    /// <param name="resolution">Heightmap resolution, a power of two plus one between 33 and 4097.</param>
    /// <returns>An unsaved snapshot owning its new TerrainData and TerrainLayers; textures are shared with the source.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The footprint or resolution is invalid or outside the survey.</exception>
    public BakedConstructionSiteData CreateLocalSlopeSnapshot(Vector3 center, Vector2 size, int resolution)
    {
        if (terrainData == null || !Finite(center) || !float.IsFinite(size.x) || !float.IsFinite(size.y) ||
            size.x <= 0 || size.y <= 0 || resolution < 33 || resolution > 4097 ||
            ((resolution - 1) & (resolution - 2)) != 0)
            throw new ArgumentOutOfRangeException(nameof(resolution), "局部坡面范围与分辨率无效。");
        Vector3 origin = new Vector3(center.x - size.x * .5f, terrainOrigin.y, center.z - size.y * .5f);
        Vector3 sourceSize = terrainData.size;
        if (origin.x < terrainOrigin.x || origin.z < terrainOrigin.z ||
            origin.x + size.x > terrainOrigin.x + sourceSize.x || origin.z + size.y > terrainOrigin.z + sourceSize.z)
            throw new ArgumentOutOfRangeException(nameof(center), "局部坡面超出原始测绘范围。");

        var local = Instantiate(this);
        local.name = name + " local blade-resolution slope";
        local.terrainOrigin = origin;
        local.workFaceCenter = new Vector3(center.x, terrainOrigin.y, center.z);
        local.workFaceRotation = Quaternion.identity;
        local.workFaceSize = size;
        var data = new TerrainData { heightmapResolution = resolution,
            size = new Vector3(size.x, sourceSize.y, size.y), alphamapResolution = 128 };
        local.terrainData = data;
        int sourceResolution = terrainData.heightmapResolution;
        float[,] sourceTop = terrainData.GetHeights(0, 0, sourceResolution, sourceResolution);
        bool[,] sourceHoles = terrainData.GetHoles(0, 0, terrainData.holesResolution, terrainData.holesResolution);
        local.groundHeights = new float[resolution * resolution];
        local.soilMask = new float[resolution * resolution];
        var top = new float[resolution, resolution];
        for (int z = 0; z < resolution; z++)
        for (int x = 0; x < resolution; x++)
        {
            float px = size.x * x / (resolution - 1f), pz = size.y * z / (resolution - 1f);
            float u = (origin.x + px - terrainOrigin.x) / sourceSize.x;
            float v = (origin.z + pz - terrainOrigin.z) / sourceSize.z;
            // Feather only the coating at the new boundary; the immutable map floor stays unchanged.
            float edge = Mathf.SmoothStep(0, 1, Mathf.Clamp01(
                Mathf.Min(px, size.x - px, pz, size.y - pz) / .25f));
            int index = z * resolution + x;
            float floor = SampleArray(groundHeights, sourceResolution, u, v);
            local.groundHeights[index] = floor;
            local.soilMask[index] = SampleArray(soilMask, sourceResolution, u, v) * edge;
            float gx = u * (sourceResolution - 1), gz = v * (sourceResolution - 1);
            int x0 = Mathf.FloorToInt(gx), z0 = Mathf.FloorToInt(gz);
            int x1 = Mathf.Min(x0 + 1, sourceResolution - 1), z1 = Mathf.Min(z0 + 1, sourceResolution - 1);
            float surface = Mathf.Lerp(Mathf.Lerp(sourceTop[z0, x0], sourceTop[z0, x1], gx - x0),
                Mathf.Lerp(sourceTop[z1, x0], sourceTop[z1, x1], gx - x0), gz - z0);
            top[z, x] = floor + Mathf.Max(0, surface - floor) * edge;
        }
        data.SetHeights(0, 0, top);
        float[,] stored = data.GetHeights(0, 0, resolution, resolution);
        local.initialSoilVolumeM3 = 0;
        double vertexVolume = (double)data.size.x * data.size.y * data.size.z / ((resolution - 1d) * (resolution - 1d));
        for (int z = 0; z < resolution; z++)
        for (int x = 0; x < resolution; x++)
        {
            int index = z * resolution + x;
            local.groundHeights[index] = Mathf.Min(local.groundHeights[index], stored[z, x]);
            if (local.soilMask[index] <= 0) local.groundHeights[index] = stored[z, x];
            local.initialSoilVolumeM3 += (stored[z, x] - local.groundHeights[index]) * vertexVolume *
                (x == 0 || x == resolution - 1 ? .5 : 1) * (z == 0 || z == resolution - 1 ? .5 : 1);
        }
        var holes = new bool[resolution - 1, resolution - 1];
        for (int z = 0; z < resolution - 1; z++)
        for (int x = 0; x < resolution - 1; x++)
        {
            float u = (origin.x + size.x * (x + .5f) / (resolution - 1) - terrainOrigin.x) / sourceSize.x;
            float v = (origin.z + size.y * (z + .5f) / (resolution - 1) - terrainOrigin.z) / sourceSize.z;
            int hx = Mathf.Min(sourceHoles.GetLength(1) - 1, Mathf.FloorToInt(u * sourceHoles.GetLength(1)));
            int hz = Mathf.Min(sourceHoles.GetLength(0) - 1, Mathf.FloorToInt(v * sourceHoles.GetLength(0)));
            holes[z, x] = sourceHoles[hz, hx] && local.soilMask[z * resolution + x] > .05f;
        }
        data.SetHoles(0, 0, holes);
        TerrainLayer[] layers = terrainData.terrainLayers;
        var localLayers = new TerrainLayer[layers.Length];
        for (int i = 0; i < layers.Length; i++)
        {
            localLayers[i] = Instantiate(layers[i]);
            localLayers[i].tileOffset += new Vector2(origin.x - terrainOrigin.x, origin.z - terrainOrigin.z);
        }
        data.terrainLayers = localLayers;
        var weights = new float[128, 128, layers.Length];
        for (int z = 0; z < 128; z++)
            for (int x = 0; x < 128; x++) weights[z, x, layers.Length - 1] = 1;
        data.SetAlphamaps(0, 0, weights);
        return local;
    }

    private static float SampleArray(float[] values, int resolution, float u, float v)
    {
        float x = Mathf.Clamp01(u) * (resolution - 1), z = Mathf.Clamp01(v) * (resolution - 1);
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
        int x1 = Mathf.Min(x0 + 1, resolution - 1), z1 = Mathf.Min(z0 + 1, resolution - 1);
        return Mathf.Lerp(Mathf.Lerp(values[z0 * resolution + x0], values[z0 * resolution + x1], x - x0),
            Mathf.Lerp(values[z1 * resolution + x0], values[z1 * resolution + x1], x - x0), z - z0);
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

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Finite(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y) &&
        float.IsFinite(value.z) && float.IsFinite(value.w);
}
