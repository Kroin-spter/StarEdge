using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CesiumForUnity;
using UnityEngine;

/// <summary>Copies the streamed Cesium surface into the local excavatable Terrain before map clipping.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-11500)]
public sealed class CesiumTerrainConformer : MonoBehaviour
{
    public DeformableSoilTerrain soil;
    public Cesium3DTileset tileset;
    [Min(0.1f)] public float sampleTimeout = 12f;
    [Min(0f)] public float edgeBlendMeters = 0.75f;
    [Min(0.01f)] public float rayHeight = 80f;
    private bool applied;
    private bool completed;
    /// <summary>Whether sampling completed, including fallback to the existing surface.</summary>
    public bool SurfaceReady => completed;

    private IEnumerator Start()
    {
        if (soil == null) soil = GetComponent<DeformableSoilTerrain>();
        if (tileset == null) tileset = FindObjectsByType<Cesium3DTileset>(FindObjectsSortMode.None).FirstOrDefault();
        if (soil == null || tileset == null) { completed = true; yield break; }
        soil.Initialize(soil.GetComponent<Terrain>());
        ConstructionSiteMapClip clip = tileset.GetComponent<ConstructionSiteMapClip>();
        if (clip != null) clip.enabled = false;
        float deadline = Time.realtimeSinceStartup + sampleTimeout;
        while (!TryApplySurface())
        {
            if (Time.realtimeSinceStartup >= deadline) break;
            yield return new WaitForSecondsRealtime(0.25f);
        }
        completed = true;
        if (!applied) Debug.LogWarning("Cesium surface unavailable; retaining prepared soil and restoring map clipping.", this);
        if (clip != null) clip.enabled = true;
    }

    private bool TryApplySurface()
    {
        if (applied) return TryApplyAppearance();
        foreach (var cutter in FindObjectsByType<BucketSoilCutter>(FindObjectsSortMode.None))
            if (cutter.soil == soil && cutter.ExcavatedVolumeM3 > 0) return false;
        Terrain terrain = soil.GetComponent<Terrain>();
        TerrainData data = terrain != null ? terrain.terrainData : null;
        if (data == null) return false;
        if (Mathf.Abs((float)soil.RecalculateRemovedVolumeM3()) > 1e-8f)
            return false;
        MeshCollider[] colliders = tileset.GetComponentsInChildren<MeshCollider>(true);
        if (colliders.Length == 0) return false;
        int n = data.heightmapResolution;
        float[,] heights = data.GetHeights(0, 0, n, n);
        Vector3 origin = soil.transform.position;
        Vector3 size = data.size;
        int hits = 0;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                Vector3 point = origin + new Vector3(size.x * x / (n - 1f), rayHeight, size.z * z / (n - 1f));
                if (!TrySurface(point, colliders, out float y)) continue;
                float edge = Mathf.Min(Mathf.Min(x, n - 1 - x) * size.x,
                    Mathf.Min(z, n - 1 - z) * size.z) / (n - 1f);
                // Keep the calibrated excavation core unchanged. Only the outer transition band
                // follows Cesium; this prevents a remote tile height offset from moving the soil
                // below the tested tooth entry pose.
                float blend = edgeBlendMeters <= 0f ? 0f : 1f - Mathf.SmoothStep(0f, 1f, edge / edgeBlendMeters);
                float localHeight = Mathf.Clamp(y - origin.y, soil.floorHeight, size.y);
                float target = localHeight / size.y;
                heights[z, x] = Mathf.Lerp(heights[z, x], target, blend);
                hits++;
            }
        if (hits != n * n) return false;
        soil.SetPreparedSurface(heights);
        applied = true;
        Debug.Log($"Cesium surface copied into excavatable Terrain: {hits}/{n * n} samples", this);
        return TryApplyAppearance();
    }

    private bool TryApplyAppearance()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return true;
        var appearance = GetComponent<ConstructionSiteSurfaceAppearance>();
        if (appearance == null) appearance = gameObject.AddComponent<ConstructionSiteSurfaceAppearance>();
        return appearance.Apply(tileset.gameObject, soil, edgeBlendMeters);
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        completed = true;
        if (tileset != null)
        {
            var clip = tileset.GetComponent<ConstructionSiteMapClip>();
            if (clip != null) clip.enabled = true;
        }
    }

    private static bool TrySurface(Vector3 point, IEnumerable<MeshCollider> colliders, out float y)
    {
        y = 0f;
        bool found = false;
        Ray ray = new Ray(point, Vector3.down);
        foreach (MeshCollider collider in colliders)
            if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy &&
                collider.Raycast(ray, out RaycastHit hit, 160f))
            {
                if (!found || hit.point.y > y) y = hit.point.y;
                found = true;
            }
        return found;
    }
}
