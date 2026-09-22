using System.Collections.Generic;
using UnityEngine;

/// <summary>Projects the actual loaded map imagery onto the work pad and the soil perimeter.</summary>
public sealed class ConstructionSiteSurfaceAppearance : MonoBehaviour
{
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
    private TerrainLayer[] originalLayers;
    private float[,,] originalWeights;
    private int originalResolution;

    /// <summary>Capture only visible map renderers before clipping and blend the resulting image at the soil edge.</summary>
    /// <param name="map">Loaded map root.</param>
    /// <param name="soil">Initialized runtime soil, whose original assets remain unchanged.</param>
    /// <param name="edgeWidth">Transition width in metres.</param>
    /// <returns>True when map imagery was captured and assigned.</returns>
    public bool Apply(GameObject map, DeformableSoilTerrain soil, float edgeWidth)
    {
        if (image != null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return false;
        foreach (var candidate in soil.transform.parent.GetComponentsInChildren<Renderer>())
            if (candidate.name == "Solid work pad") pad = candidate;
        if (pad == null || soil.Terrain.terrainData.terrainLayers.Length != 1) return false;
        Bounds bounds = pad.bounds;
        float size = Mathf.Max(bounds.size.x, bounds.size.z);
        Vector3 origin = bounds.center - new Vector3(size * .5f, 0, size * .5f);
        var cameraObject = new GameObject("Worksite map capture") { hideFlags = HideFlags.DontSave };
        var camera = cameraObject.AddComponent<Camera>();
        var target = new RenderTexture(512, 512, 24);
        var saved = new Dictionary<GameObject, int>();
        RenderTexture previous = RenderTexture.active;
        try
        {
            // Isolate map imagery from equipment, the pad and the replacement soil.
            foreach (var renderer in map.GetComponentsInChildren<MeshRenderer>())
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
            image = new Texture2D(512, 512, TextureFormat.RGB24, true) { name = "Worksite captured map surface", wrapMode = TextureWrapMode.Clamp };
            image.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
            image.Apply();
            // Reject incomplete captures rather than showing missing tiles on the pad.
            foreach (Color32 pixel in image.GetPixels32())
                if (pixel.r > 240 && pixel.b > 240 && pixel.g < 20)
                {
                    Destroy(image); image = null;
                    return false;
                }
        }
        finally
        {
            foreach (var entry in saved) if (entry.Key != null) entry.Key.layer = entry.Value;
            RenderTexture.active = previous;
            camera.targetTexture = null;
            Destroy(cameraObject); Destroy(target);
        }

        TerrainData data = soil.Terrain.terrainData;
        TerrainLayer[] oldLayers = data.terrainLayers;
        runtimeData = data;
        originalLayers = oldLayers;
        originalResolution = data.alphamapResolution;
        originalWeights = data.GetAlphamaps(0, 0, originalResolution, originalResolution);
        layer = new TerrainLayer { name = "Cesium perimeter surface", diffuseTexture = image,
            tileSize = new Vector2(size, size), tileOffset = new Vector2(soil.transform.position.x - origin.x,
                soil.transform.position.z - origin.z), smoothness = 0 };
        data.terrainLayers = new[] { oldLayers[0], layer };
        data.alphamapResolution = Mathf.Max(256, data.alphamapResolution);
        int n = data.alphamapResolution;
        var weights = new float[n, n, 2];
        for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
        {
            float distance = Mathf.Min(Mathf.Min(x, n - 1 - x) * data.size.x,
                Mathf.Min(z, n - 1 - z) * data.size.z) / (n - 1f);
            float variation = Mathf.PerlinNoise(x * .04f, z * .04f);
            float width = Mathf.Max(.01f, edgeWidth * (1.4f + .5f * variation));
            float core = Mathf.SmoothStep(0, 1, distance / width);
            weights[z, x, 0] = core; weights[z, x, 1] = 1 - core;
        }
        data.SetAlphamaps(0, 0, weights);
        overlay = new GameObject("Map-textured work pad (visual)");
        overlay.transform.SetParent(pad.transform.parent, false);
        float y = bounds.max.y + .002f;
        mesh = new Mesh { name = "Projected pad surface" };
        mesh.vertices = new[] { new Vector3(bounds.min.x,y,bounds.min.z), new Vector3(bounds.min.x,y,bounds.max.z),
            new Vector3(bounds.max.x,y,bounds.max.z), new Vector3(bounds.max.x,y,bounds.min.z) };
        var uv = new Vector2[4];
        for (int i = 0; i < 4; i++) uv[i] = new Vector2((mesh.vertices[i].x-origin.x)/size, (mesh.vertices[i].z-origin.z)/size);
        mesh.uv = uv; mesh.triangles = new[] { 0,1,2,0,2,3 }; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        overlay.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        overlay.transform.localScale = Vector3.one;
        overlay.AddComponent<MeshFilter>().sharedMesh = mesh;
        material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.mainTexture = image; material.SetFloat("_Smoothness", 0);
        overlay.AddComponent<MeshRenderer>().sharedMaterial = material;
        padWasEnabled = pad.enabled; pad.enabled = false;
        foreach (var candidate in soil.GetComponentsInChildren<Renderer>())
            if (candidate.name == "Soil bottom plate (always solid)")
            {
                bottom = candidate; bottomWasEnabled = bottom.enabled; bottom.enabled = false;
            }
        Debug.Log("Worksite map imagery applied to pad and soil perimeter.", this);
        return true;
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
        if (bottom != null) bottom.enabled = bottomWasEnabled;
        if (overlay != null) Destroy(overlay);
        if (mesh != null) Destroy(mesh);
        if (material != null) Destroy(material);
        if (layer != null) Destroy(layer);
        if (image != null) Destroy(image);
    }
}
