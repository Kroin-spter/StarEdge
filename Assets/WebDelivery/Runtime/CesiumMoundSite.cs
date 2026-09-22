using System;
using System.Collections;
using System.IO;
using System.Linq;
using CesiumForUnity;
using UnityEngine;

/// <summary>Place a local diggable mound above intact streamed map ground, without a work pad or map clipping.</summary>
[DefaultExecutionOrder(-13000)]
public sealed class CesiumMoundSite : MonoBehaviour
{
    public Cesium3DTileset tileset;
    public DeformableSoilTerrain soil;
    public Zx200DigController machine;
    public Transform truck;
    [Tooltip("Cesium local tileset directory name. The directory is resolved through CESIUM_TILESET_DIR or the project sibling tilesets folder.")]
    public string mapName = "osgb_small_005_004";
    [Min(.1f)] public float moundHeight = .9f;
    [Min(1f)] public float loadingTimeout = 30f;
    public bool IsReady { get; private set; }
    public string Status { get; private set; } = "Waiting for map ground";
    private Zx200KeyboardController keyboard;
    private ArticulationBody root;
    private bool originalFixed, originalKeyboard, originalMachine;
    private BucketSoilCutter cutter;
    private BucketSoilResistance resistance;
    private bool originalCutter, originalResistance;

    private void Awake()
    {
        if (soil == null || machine == null || tileset == null || truck == null)
        {
            Status = "Missing mound site references";
            enabled = false;
            return;
        }
        ResolveTilesetUrl();
        keyboard = machine.GetComponent<Zx200KeyboardController>();
        keyboard.ResolveArticulationRoot(); root = keyboard.articulationRoot;
        originalFixed = keyboard.fixedBase; originalKeyboard = keyboard.enabled;
        originalMachine = machine.enabled; machine.enabled = false;
        keyboard.fixedBase = true; keyboard.enabled = false; root.immovable = true;
        cutter = machine.GetComponentInChildren<BucketSoilCutter>();
        resistance = cutter.GetComponent<BucketSoilResistance>();
        originalCutter = cutter.enabled; cutter.enabled = false;
        if (resistance != null) { originalResistance = resistance.enabled; resistance.enabled = false; }
        soil.GetComponent<Terrain>().enabled = false;
        soil.SoilCollider.enabled = false;
    }

    /// <summary>Resolve the local Cesium tileset without persisting a machine-specific absolute path in the scene.</summary>
    private void ResolveTilesetUrl()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        tileset.url = new Uri(new Uri(Application.absoluteURL), $"maps/{mapName}/tileset.json").AbsoluteUri;
#else
        string configuredDirectory = Environment.GetEnvironmentVariable("CESIUM_TILESET_DIR");
        string directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Application.dataPath, "../../tilesets", mapName)
            : configuredDirectory;
        string tilesetPath = Path.GetFullPath(Path.Combine(directory, "tileset.json"));
        tileset.url = new Uri(tilesetPath).AbsoluteUri;
        if (!File.Exists(tilesetPath))
            Debug.LogWarning($"Cesium local map is missing: {tilesetPath}. Set CESIUM_TILESET_DIR or place the map beside the project.", this);
#endif
    }

    private IEnumerator Start()
    {
        float deadline = Time.realtimeSinceStartup + loadingTimeout;
        float previousGround = float.NaN;
        int stable = 0;
        while (Time.realtimeSinceStartup < deadline)
        {
            var map = tileset.GetComponentsInChildren<MeshCollider>().Where(c => c.enabled).ToArray();
            if (TrySupport(map, out float support, out float truckGround))
            {
                stable = float.IsFinite(previousGround) && Mathf.Abs(previousGround - support) < .025f ? stable + 1 : 0;
                previousGround = support;
                if (stable >= 3 && PrepareMound(map))
                {
                    // Settle on actual map collisions, preserving X/Z and articulation pose.
                    float bottom = TrackBounds().min.y;
                    root.TeleportRoot(root.transform.position + Vector3.up * (support + .025f - bottom), root.transform.rotation);
                    root.linearVelocity = Vector3.zero; root.angularVelocity = Vector3.zero;
                    truck.position = new Vector3(truck.position.x, truckGround, truck.position.z);
                    Physics.SyncTransforms();
                    soil.GetComponent<Terrain>().enabled = true; soil.SoilCollider.enabled = true;
                    cutter.enabled = originalCutter;
                    if (resistance != null) resistance.enabled = originalResistance;
                    keyboard.fixedBase = originalFixed; root.immovable = originalFixed; root.useGravity = !originalFixed;
                    machine.enabled = originalMachine;
                    keyboard.enabled = originalKeyboard;
                    IsReady = true; Status = "Mound ready on original map ground";
                    Debug.Log($"MOUND READY ground={support:F3}, truck={truckGround:F3}, soil={soil.transform.position}", this);
                    yield break;
                }
            }
            yield return new WaitForSecondsRealtime(.25f);
        }
        Status = "Map support unavailable; machine held for safety. Reload scene to retry.";
        Debug.LogWarning(Status, this);
    }

    private Bounds TrackBounds()
    {
        var tracks = root.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger && c.attachedArticulationBody == root).ToArray();
        Bounds bounds = tracks[0].bounds;
        foreach (var track in tracks) bounds.Encapsulate(track.bounds);
        return bounds;
    }

    private bool TrySupport(MeshCollider[] map, out float support, out float truckGround)
    {
        support = truckGround = float.NegativeInfinity;
        Bounds tracks = TrackBounds();
        float minimum = float.PositiveInfinity;
        foreach (int x in new[] { -1, 1 }) foreach (int z in new[] { -1, 1 })
        {
            Vector3 p = tracks.center + new Vector3(x * tracks.extents.x * .7f, 0, z * tracks.extents.z * .7f);
            if (!Sample(map, p, out float y)) return false;
            support = Mathf.Max(support, y); minimum = Mathf.Min(minimum, y);
        }
        if (support - minimum > .35f) return false;
        foreach (int x in new[] { -1, 1 }) foreach (float z in new[] { -1.35f, 3.25f })
        {
            if (!Sample(map, truck.TransformPoint(new Vector3(x * 1.35f, 0, z)), out float y)) return false;
            truckGround = Mathf.Max(truckGround, y);
        }
        return true;
    }

    private bool PrepareMound(MeshCollider[] map)
    {
        soil.Initialize(soil.GetComponent<Terrain>());
        TerrainData data = soil.Terrain.terrainData;
        int n = data.heightmapResolution;
        var ground = new float[n, n];
        Vector3 origin = soil.transform.position, size = data.size;
        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
        for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
        {
            Vector3 point = origin + new Vector3(size.x*x/(n-1f), 0, size.z*z/(n-1f));
            if (!Sample(map, point, out float y)) return false;
            ground[z,x] = y + .015f;
            minimum = Mathf.Min(minimum, ground[z,x]); maximum = Mathf.Max(maximum, ground[z,x]);
        }
        origin.y = minimum - .1f;
        soil.transform.position = origin;
        size.y = maximum - origin.y + moundHeight + 2f;
        data.size = size;
        soil.maximumCutDepth = moundHeight + .1f;
        var heights = new float[n,n];
        for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
        {
            ground[z,x] = (ground[z,x] - origin.y) / size.y;
            heights[z,x] = ground[z,x] + Thickness(x/(n-1f), z/(n-1f)) / size.y;
        }
        soil.PrepareMoundSurface(heights, ground);
        int h = data.holesResolution;
        var visible = new bool[h,h];
        for (int z = 0; z < h; z++) for (int x = 0; x < h; x++)
            visible[z,x] = Thickness((x+.5f)/h, (z+.5f)/h) > .015f;
        data.SetHoles(0,0,visible);
        return true;
    }

    private float Thickness(float u, float v)
    {
        float x = (u-.5f)/.43f, z = (v-.5f)/.46f;
        float angle = Mathf.Atan2(z,x);
        float radius = Mathf.Sqrt(x*x+z*z) / (1f + .045f*Mathf.Sin(angle*3) + .025f*Mathf.Cos(angle*5));
        return moundHeight * Mathf.Pow(Mathf.Max(0, 1-radius*radius), 1.35f);
    }

    private static bool Sample(MeshCollider[] map, Vector3 point, out float y)
    {
        y = float.NegativeInfinity;
        foreach (var collider in map)
            if (collider != null && collider.gameObject.activeInHierarchy &&
                collider.Raycast(new Ray(new Vector3(point.x,100,point.z),Vector3.down),out var hit,200))
                y = Mathf.Max(y, hit.point.y);
        return float.IsFinite(y);
    }
}
