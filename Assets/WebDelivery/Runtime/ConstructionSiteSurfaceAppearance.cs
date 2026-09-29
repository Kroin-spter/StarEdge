using System.Collections.Generic;
using UnityEngine;

/// <summary>Projects loaded Cesium imagery onto a local excavatable Terrain without adding collisions.</summary>
public sealed class ConstructionSiteSurfaceAppearance : MonoBehaviour
{
    private const float MaximumSlopeSoilBlend = .38f;
    private const float MinimumMapImageCoverage = .75f;
    private Texture2D image;
    private TerrainLayer layer;
    private Material material;
    private Mesh mesh;
    private GameObject overlay;
    private Renderer pad;
    private bool padWasEnabled;
    private Renderer bottom;
    private bool bottomWasEnabled;
    private TerrainData runtimeData;
    private DeformableSoilTerrain soilForCleanup;
    private TerrainLayer[] originalLayers;
    private float[,,] originalWeights;
    private int originalResolution;
    private bool mapTextureApplied;

    /// <summary>Whether a Cesium image was captured and applied to this runtime Terrain.</summary>
    public bool MapTextureApplied => mapTextureApplied;

    /// <summary>Capture the map and blend it around a legacy flat work pad.</summary>
    /// <param name="map">Loaded map root.</param>
    /// <param name="soil">Initialized runtime soil, whose source asset remains unchanged.</param>
    /// <param name="edgeWidth">Transition width in metres.</param>
    /// <returns>True when map imagery was captured and assigned.</returns>
    public bool Apply(GameObject map, DeformableSoilTerrain soil, float edgeWidth)
    {
        if (image != null || map == null || soil == null ||
            SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return false;
        foreach (Renderer candidate in soil.transform.parent.GetComponentsInChildren<Renderer>(true))
            if (candidate.name == "Solid work pad") pad = candidate;
        if (pad == null || soil.Terrain.terrainData.terrainLayers.Length != 1) return false;

        Bounds bounds = pad.bounds;
        float size = Mathf.Max(bounds.size.x, bounds.size.z);
        if (!ApplyMapTexture(map, soil, bounds, size, null, edgeWidth, true)) return false;
        CreateFlatPadOverlay(bounds, size);
        Debug.Log("Worksite map imagery applied to pad and soil perimeter.", this);
        return true;
    }

    /// <summary>Project the source map over untouched ground and retain the soil layer only in the cuttable band.</summary>
    /// <param name="map">Loaded Cesium map root, sampled before its work-area triangles are clipped.</param>
    /// <param name="soil">Initialized runtime replacement Terrain.</param>
    /// <param name="workArea">World-space Terrain bounds used for the orthographic map capture.</param>
    /// <param name="soilBandMask">Smooth height-map-resolution mask, from zero outside to one in the band core.</param>
    /// <returns>True when imagery and the band blend mask were applied; false leaves the original soil layer intact.</returns>
    public bool ApplySlopeBand(GameObject map, DeformableSoilTerrain soil, Bounds workArea, float[,] soilBandMask)
    {
        if (image != null || map == null || soil == null || soilBandMask == null ||
            SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null ||
            soil.Terrain.terrainData.terrainLayers.Length != 1) return false;

        float size = Mathf.Max(workArea.size.x, workArea.size.z);
        return ApplyMapTexture(map, soil, workArea, size, soilBandMask, 0f, false);
    }

    private bool ApplyMapTexture(GameObject map, DeformableSoilTerrain soil, Bounds bounds,
        float textureSize, float[,] soilBandMask, float edgeWidth, bool blendPerimeter)
    {
        if (textureSize <= 0f || !TryCaptureMapImage(map, bounds, textureSize, out Texture2D captured)) return false;
        image = captured;

        TerrainData data = soil.Terrain.terrainData;
        soilForCleanup = soil;
        TerrainLayer[] oldLayers = data.terrainLayers;
        runtimeData = data;
        originalLayers = oldLayers;
        originalResolution = data.alphamapResolution;
        originalWeights = data.GetAlphamaps(0, 0, originalResolution, originalResolution);
        layer = new TerrainLayer
        {
            name = "Cesium local surface",
            diffuseTexture = image,
            tileSize = new Vector2(textureSize, textureSize),
            tileOffset = new Vector2(soil.transform.position.x - (bounds.center.x - textureSize * .5f),
                soil.transform.position.z - (bounds.center.z - textureSize * .5f)),
            smoothness = 0
        };
        data.terrainLayers = new[] { oldLayers[0], layer };
        data.alphamapResolution = Mathf.Max(256, originalResolution);
        int n = data.alphamapResolution;
        var weights = new float[n, n, 2];
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            float soilWeight;
            if (soilBandMask != null)
            {
                // Keep the sampled Cesium imagery dominant so excavated soil follows the site's
                // aerial colour and texture instead of becoming a featureless brown slab.
                soilWeight = SampleMask(soilBandMask, x / (n - 1f), z / (n - 1f)) * MaximumSlopeSoilBlend;
            }
            else
            {
                float distance = Mathf.Min(Mathf.Min(x, n - 1 - x) * data.size.x,
                    Mathf.Min(z, n - 1 - z) * data.size.z) / (n - 1f);
                float variation = Mathf.PerlinNoise(x * .04f, z * .04f);
                float width = Mathf.Max(.01f, edgeWidth * (1.4f + .5f * variation));
                soilWeight = Mathf.SmoothStep(0, 1, distance / width);
            }
            weights[z, x, 0] = soilWeight;
            weights[z, x, 1] = 1f - soilWeight;
        }
        data.SetAlphamaps(0, 0, weights);
        mapTextureApplied = true;
        if (blendPerimeter) Debug.Log("Worksite map texture blended around the soil perimeter.", this);
        else Debug.Log("Cesium imagery blended over the replacement terrain; the cuttable soil-band mask retains the soil material.", this);
        return true;
    }

    private static float SampleMask(float[,] mask, float u, float v)
    {
        float x = Mathf.Clamp01(u) * (mask.GetLength(1) - 1);
        float z = Mathf.Clamp01(v) * (mask.GetLength(0) - 1);
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
        int x1 = Mathf.Min(x0 + 1, mask.GetLength(1) - 1);
        int z1 = Mathf.Min(z0 + 1, mask.GetLength(0) - 1);
        return Mathf.Lerp(Mathf.Lerp(mask[z0, x0], mask[z0, x1], x - x0),
            Mathf.Lerp(mask[z1, x0], mask[z1, x1], x - x0), z - z0);
    }

    private bool TryCaptureMapImage(GameObject map, Bounds bounds, float size, out Texture2D captured)
    {
        captured = null;
        Vector3 origin = bounds.center - new Vector3(size * .5f, 0, size * .5f);
        var cameraObject = new GameObject("Worksite map capture") { hideFlags = HideFlags.DontSave };
        var camera = cameraObject.AddComponent<Camera>();
        var target = new RenderTexture(512, 512, 24);
        var saved = new Dictionary<GameObject, int>();
        RenderTexture previous = RenderTexture.active;
        Texture2D candidate = null;
        try
        {
            // Isolate only currently rendered Cesium tiles, restoring every layer even on failure.
            foreach (MeshRenderer renderer in map.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (!saved.ContainsKey(renderer.gameObject)) saved.Add(renderer.gameObject, renderer.gameObject.layer);
                renderer.gameObject.layer = 31;
            }
            if (saved.Count == 0) return false;
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = size * .5f;
            camera.aspect = 1;
            camera.nearClipPlane = .1f;
            camera.farClipPlane = 200;
            camera.cullingMask = 1 << 31;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.magenta;
            camera.transform.SetPositionAndRotation(bounds.center + Vector3.up * 100,
                Quaternion.LookRotation(Vector3.down, Vector3.forward));
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            candidate = new Texture2D(512, 512, TextureFormat.RGB24, true)
            { name = "Worksite captured Cesium surface", wrapMode = TextureWrapMode.Clamp };
            candidate.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
            candidate.Apply();
            Color32[] pixels = candidate.GetPixels32();
            if (!FillUncoveredPixels(pixels, out float coverage) || coverage < MinimumMapImageCoverage)
            {
                Debug.LogWarning($"坡面影像覆盖率不足，已捕获 {coverage:P0}；拒绝保存纯棕色替代面。", this);
                return false;
            }
            candidate.SetPixels32(pixels);
            candidate.Apply(true, false);
            captured = candidate;
            candidate = null;
            return true;
        }
        finally
        {
            foreach (var entry in saved) if (entry.Key != null) entry.Key.layer = entry.Value;
            RenderTexture.active = previous;
            camera.targetTexture = null;
            Destroy(cameraObject);
            target.Release();
            Destroy(target);
            if (candidate != null) Destroy(candidate);
        }
    }

    private static bool FillUncoveredPixels(Color32[] pixels, out float coverage)
    {
        int[] nearestSource = new int[pixels.Length];
        int[] queue = new int[pixels.Length];
        int tail = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 pixel = pixels[i];
            bool uncovered = pixel.r > 240 && pixel.b > 240 && pixel.g < 20;
            nearestSource[i] = uncovered ? -1 : i;
            if (!uncovered) queue[tail++] = i;
        }

        coverage = tail / (float)pixels.Length;
        if (tail == 0) return false;

        // Flood valid pixels outward so small gaps at Cesium tile edges inherit their nearest map colour.
        int head = 0;
        while (head < tail)
        {
            int current = queue[head++];
            int x = current % 512;
            if (x > 0) Fill(current - 1, current);
            if (x < 511) Fill(current + 1, current);
            if (current >= 512) Fill(current - 512, current);
            if (current < pixels.Length - 512) Fill(current + 512, current);
        }
        for (int i = 0; i < pixels.Length; i++)
            if (nearestSource[i] != i) pixels[i] = pixels[nearestSource[i]];
        return true;

        void Fill(int destination, int source)
        {
            if (nearestSource[destination] >= 0) return;
            nearestSource[destination] = nearestSource[source];
            queue[tail++] = destination;
        }
    }

    private void CreateFlatPadOverlay(Bounds bounds, float size)
    {
        Vector3 origin = bounds.center - new Vector3(size * .5f, 0, size * .5f);
        overlay = new GameObject("Map-textured work pad (visual)");
        overlay.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        overlay.transform.localScale = Vector3.one;
        float y = bounds.max.y + .002f;
        mesh = new Mesh { name = "Projected pad surface" };
        mesh.vertices = new[] { new Vector3(bounds.min.x, y, bounds.min.z), new Vector3(bounds.min.x, y, bounds.max.z),
            new Vector3(bounds.max.x, y, bounds.max.z), new Vector3(bounds.max.x, y, bounds.min.z) };
        var uv = new Vector2[4];
        for (int i = 0; i < 4; i++) uv[i] = new Vector2((mesh.vertices[i].x - origin.x) / size, (mesh.vertices[i].z - origin.z) / size);
        mesh.uv = uv; mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 }; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        overlay.AddComponent<MeshFilter>().sharedMesh = mesh;
        material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.mainTexture = image; material.SetFloat("_Smoothness", 0);
        overlay.AddComponent<MeshRenderer>().sharedMaterial = material;
        padWasEnabled = pad.enabled; pad.enabled = false;
        foreach (Renderer candidate in soilForCleanup.GetComponentsInChildren<Renderer>(true))
            if (candidate.name == "Soil bottom plate (always solid)")
            {
                bottom = candidate; bottomWasEnabled = bottom.enabled; bottom.enabled = false;
            }
    }

    private void OnDestroy()
    {
        if (runtimeData != null && layer != null)
        {
            TerrainLayer[] current = runtimeData.terrainLayers;
            if (current.Length == 2 && current[1] == layer)
            {
                runtimeData.terrainLayers = originalLayers;
                runtimeData.alphamapResolution = originalResolution;
                runtimeData.SetAlphamaps(0, 0, originalWeights);
            }
        }
        if (pad != null && overlay != null) pad.enabled = padWasEnabled;
        if (bottom != null && overlay != null) bottom.enabled = bottomWasEnabled;
        if (overlay != null) Destroy(overlay);
        if (mesh != null) Destroy(mesh);
        if (material != null) Destroy(material);
        if (layer != null) Destroy(layer);
        if (image != null) Destroy(image);
    }
}
