using System;
using System.Collections;
using System.Linq;
using CesiumForUnity;
using UnityEngine;

/// <summary>Replace one local Cesium footprint with a cuttable lower pit, raised bench and narrow soil strip.</summary>
[DefaultExecutionOrder(-13000)]
public sealed class CesiumMoundSite : MonoBehaviour
{
    public Cesium3DTileset tileset;
    public DeformableSoilTerrain soil;
    public Zx200DigController machine;
    public Transform truck;
    [Tooltip("Cesium local tileset directory name. The directory is resolved through CESIUM_TILESET_DIR or the project sibling tilesets folder.")]
    public string mapName = "osgb_small_005_004";
    [Header("Pit and bench layout")]
    public Vector2 replacementAreaSize = new Vector2(28f, 28f);
    [Min(.1f)] public float pitDepth = 1.8f;
    [Min(.1f)] public float benchRiseStartLocalZ = 8.4f;
    [Min(.1f)] public float benchRiseEndLocalZ = 9.4f;
    [Min(.1f)] public float soilStripHalfWidth = 6.5f;
    [Min(.1f)] public float soilStripCenterLocalZ = 6.7f;
    [Min(.1f)] public float soilStripHalfDepth = .9f;
    [Min(.05f)] public float soilStripHeight = .65f;
    [Header("Truck parking on the lower floor")]
    public Vector3 truckParkingOffsetLocal = new Vector3(8.5f, 0f, -1.2f);
    public float truckParkingYawDegrees = 90f;
    [Min(1f)] public float loadingTimeout = 30f;
    public bool IsReady { get; private set; }
    public bool MapSurfacePrepared { get; private set; }
    public string Status { get; private set; } = "Waiting for map ground";
    /// <summary>World-space footprint where the authored terrain replaces Cesium rendering and collision.</summary>
    public Bounds WorkAreaBounds
    {
        get
        {
            if (soil == null || soil.Terrain == null || soil.Terrain.terrainData == null) return default;
            Vector3 size = soil.Terrain.terrainData.size;
            return new Bounds(soil.transform.position + size * .5f, size);
        }
    }
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
        Vector3 parking = machine.transform.TransformPoint(truckParkingOffsetLocal);
        truck.position = new Vector3(parking.x, truck.position.y, parking.z);
        truck.rotation = machine.transform.rotation * Quaternion.Euler(0f, truckParkingYawDegrees, 0f);
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
        tileset.tilesetSource = CesiumDataSource.FromUrl;
        tileset.url = new Uri(new Uri(Application.absoluteURL), $"maps/{mapName}/tileset.json").AbsoluteUri;
#else
        CesiumLocalMapReference.ApplyLocalMap(tileset, mapName, this);
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
                    // Replace the source map over one matching footprint before moving either vehicle onto the pit floor.
                    MapSurfacePrepared = true;
                    ConstructionSiteMapClip clip = tileset.GetComponent<ConstructionSiteMapClip>();
                    if (clip == null) clip = tileset.gameObject.AddComponent<ConstructionSiteMapClip>();
                    clip.workArea = WorkAreaBounds;
                    clip.RefreshLoadedTiles();
                    Physics.SyncTransforms();

                    // Settle on the authored lower bench while retaining the excavator's X/Z and articulation pose.
                    float bottom = TrackBounds().min.y;
                    float machineDrop = PitDepression(machine.transform.InverseTransformPoint(root.transform.position));
                    root.TeleportRoot(root.transform.position + Vector3.up * (support - machineDrop + .025f - bottom), root.transform.rotation);
                    root.linearVelocity = Vector3.zero; root.angularVelocity = Vector3.zero;
                    float truckDrop = PitDepression(machine.transform.InverseTransformPoint(truck.position));
                    truck.position = new Vector3(truck.position.x, truckGround - truckDrop, truck.position.z);
                    Physics.SyncTransforms();
                    soil.GetComponent<Terrain>().enabled = true; soil.SoilCollider.enabled = true;
                    cutter.enabled = originalCutter;
                    if (resistance != null) resistance.enabled = originalResistance;
                    keyboard.fixedBase = originalFixed; root.immovable = originalFixed; root.useGravity = !originalFixed;
                    machine.enabled = originalMachine;
                    keyboard.enabled = originalKeyboard;
                    IsReady = true; Status = "Lower pit and raised work bench ready";
                    Debug.Log($"PIT BENCH READY map={support:F3}, pit={support - machineDrop:F3}, " +
                        $"truck={truckGround - truckDrop:F3}, strip={soilStripHeight:F2}m high x {soilStripHalfWidth * 2f:F1}m wide", this);
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
        Vector3 size = new Vector3(replacementAreaSize.x, 12f, replacementAreaSize.y);
        Vector3 machinePosition = machine.transform.position;
        Vector3 origin = new Vector3(machinePosition.x - size.x * .5f, machinePosition.y, machinePosition.z - size.z * .5f);
        soil.transform.position = origin;
        data.size = size;
        var sourceGround = new float[n, n];
        var baseGround = new float[n, n];
        var strip = new float[n, n];
        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
        for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
        {
            Vector3 point = origin + new Vector3(size.x*x/(n-1f), 0, size.z*z/(n-1f));
            if (!Sample(map, point, out float y)) return false;
            Vector3 local = machine.transform.InverseTransformPoint(point);
            sourceGround[z,x] = y + .015f;
            baseGround[z,x] = sourceGround[z,x] - PitDepression(local);
            strip[z,x] = SoilStripThickness(local);
            minimum = Mathf.Min(minimum, baseGround[z,x]);
            maximum = Mathf.Max(maximum, baseGround[z,x] + strip[z,x]);
        }
        origin.y = minimum - .35f;
        soil.transform.position = origin;
        size.y = maximum - origin.y + .5f;
        data.size = size;
        soil.maximumCutDepth = soilStripHeight + .15f;
        var heights = new float[n,n];
        var ground = new float[n,n];
        for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
        {
            ground[z,x] = (baseGround[z,x] - origin.y) / size.y;
            heights[z,x] = (baseGround[z,x] + strip[z,x] - origin.y) / size.y;
        }
        soil.PrepareMoundSurface(heights, ground);
        int h = data.holesResolution;
        var solidCells = new bool[h,h];
        for (int z = 0; z < h; z++) for (int x = 0; x < h; x++) solidCells[z,x] = true;
        data.SetHoles(0, 0, solidCells);
        return true;
    }

    private float PitDepression(Vector3 machineLocal)
    {
        float halfX = replacementAreaSize.x * .5f;
        float halfZ = replacementAreaSize.y * .5f;
        float sideMask = Smooth01((halfX - Mathf.Abs(machineLocal.x)) / 2f);
        float rearMask = Smooth01((machineLocal.z + halfZ) / 2f);
        float bench = Smooth01((machineLocal.z - benchRiseStartLocalZ) /
            Mathf.Max(.01f, benchRiseEndLocalZ - benchRiseStartLocalZ));
        return pitDepth * sideMask * rearMask * (1f - bench);
    }

    private float SoilStripThickness(Vector3 machineLocal)
    {
        float side = 1f - Smooth01((Mathf.Abs(machineLocal.x) - (soilStripHalfWidth - .8f)) / .8f);
        float distance = Mathf.Abs(machineLocal.z - soilStripCenterLocalZ);
        float end = 1f - Smooth01((distance - (soilStripHalfDepth - .25f)) / .25f);
        return soilStripHeight * side * end;
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
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
