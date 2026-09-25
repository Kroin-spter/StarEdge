using System;
using System.Collections;
using System.Linq;
using CesiumForUnity;
using UnityEngine;

/// <summary>Replace one local Cesium footprint with a cuttable slope band and stable bench-side vehicle positions.</summary>
[DefaultExecutionOrder(-13000)]
public sealed class CesiumMoundSite : MonoBehaviour
{
    /// <summary>Convert a constant surface-normal soil thickness into a vertical height-field offset.</summary>
    /// <param name="thicknessMeters">Requested thickness measured perpendicular to the source surface.</param>
    /// <param name="surfaceNormal">Source surface normal; it is normalized before conversion.</param>
    /// <param name="minimumNormalY">Smallest upward normal component this height field may represent.</param>
    /// <returns>The vertical displacement which produces the requested normal thickness.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The thickness, normal, or representable slope limit is invalid.</exception>
    public static float GetVerticalOffsetForNormalThickness(
        float thicknessMeters, Vector3 surfaceNormal, float minimumNormalY)
    {
        if (!float.IsFinite(thicknessMeters) || thicknessMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(thicknessMeters), "Thickness must be finite and positive.");
        if (!float.IsFinite(minimumNormalY) || minimumNormalY <= 0f || minimumNormalY > 1f)
            throw new ArgumentOutOfRangeException(nameof(minimumNormalY), "The minimum upward normal must be in (0, 1].");
        if (!float.IsFinite(surfaceNormal.x) || !float.IsFinite(surfaceNormal.y) ||
            !float.IsFinite(surfaceNormal.z) || surfaceNormal.sqrMagnitude < 1e-8f)
            throw new ArgumentOutOfRangeException(nameof(surfaceNormal), "Surface normal must be finite and non-zero.");

        float normalY = surfaceNormal.normalized.y;
        if (normalY < minimumNormalY)
            throw new ArgumentOutOfRangeException(nameof(surfaceNormal),
                $"The slope is too steep for a height field (normal.y={normalY:F3}, minimum={minimumNormalY:F3}).");
        return thicknessMeters / normalY;
    }

    public Cesium3DTileset tileset;
    public DeformableSoilTerrain soil;
    public Zx200DigController machine;
    public Transform truck;
    [Tooltip("Cesium local tileset directory name. The directory is resolved through CESIUM_TILESET_DIR or the project sibling tilesets folder.")]
    public string mapName = "osgb_small_005_004";
    [Header("Slope work-face layout")]
    public Vector2 replacementAreaSize = new Vector2(28f, 28f);
    [Tooltip("Enable only when a known lower-bench world position is available; otherwise a nearby safe slope is selected automatically.")]
    public bool useExplicitWorkFaceAnchor;
    [Tooltip("World X/Z of the desired track-base center. Automatic slope search is used when disabled.")]
    public Vector3 workFaceAnchor;
    [Min(8f)] public float workFaceSearchRadius = 60f;
    [Min(.5f)] public float workFaceSearchSpacing = 2f;
    [Min(.1f)] public float minimumWorkFaceRise = 1.1f;
    [Range(.2f, .9f)] public float minimumSurfaceNormalY = .42f;
    [Min(.1f)] public float soilStripHalfWidth = 4.8f;
    [Min(.1f)] public float soilStripCenterLocalZ = 6.7f;
    [Min(.1f)] public float soilStripHalfDepth = .9f;
    [Min(.05f)] public float soilStripHeight = .55f;
    [Header("Truck parking on the lower bench")]
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
    private Vector3 originalRootPosition;
    private Bounds originalTrackBounds;
    private float originalTruckBottomOffset;
    private float selectedSupportY;
    private float selectedTruckGroundY;
    private Vector3 selectedRootPosition;
    private Quaternion selectedRootRotation;
    private Vector3 selectedFaceDirection;
    private float selectedFaceRise;
    private string candidateDiagnostics = string.Empty;
    private bool workFaceCoverageIncomplete;

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
        originalRootPosition = root.transform.position;
        Collider[] truckColliders = truck.GetComponentsInChildren<Collider>()
            .Where(c => c.enabled && !c.isTrigger).ToArray();
        if (truckColliders.Length == 0)
        {
            Status = "Truck has no solid support colliders";
            enabled = false;
            return;
        }
        Bounds truckBounds = truckColliders[0].bounds;
        foreach (Collider collider in truckColliders) truckBounds.Encapsulate(collider.bounds);
        originalTruckBottomOffset = truckBounds.min.y - truck.position.y;
        originalTrackBounds = TrackBounds();
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
            if (TryTrackSupport(map, out float support))
            {
                stable = float.IsFinite(previousGround) && Mathf.Abs(previousGround - support) < .025f ? stable + 1 : 0;
                previousGround = support;
                if (stable >= 3)
                {
                    if (!PrepareMound(map))
                    {
                        if (workFaceCoverageIncomplete)
                        {
                            Status = "等待 Cesium 坡面瓦片完成加载；设备保持原位。";
                            yield return new WaitForSecondsRealtime(.75f);
                            continue;
                        }
                        Debug.LogWarning(Status, this);
                        yield break;
                    }
                    // Replace the source map over one matching footprint before parking either vehicle on the bench.
                    MapSurfacePrepared = true;
                    ConstructionSiteMapClip clip = tileset.GetComponent<ConstructionSiteMapClip>();
                    if (clip == null) clip = tileset.gameObject.AddComponent<ConstructionSiteMapClip>();
                    clip.workArea = WorkAreaBounds;
                    clip.RefreshLoadedTiles();
                    Physics.SyncTransforms();

                    // Move only after terrain preparation and Cesium clipping have both succeeded.
                    float bottom = originalTrackBounds.min.y;
                    Vector3 rootTarget = selectedRootPosition + Vector3.up * (selectedSupportY + .025f - bottom);
                    root.TeleportRoot(rootTarget, selectedRootRotation);
                    root.linearVelocity = Vector3.zero; root.angularVelocity = Vector3.zero;
                    Vector3 truckXZ = selectedRootPosition + selectedRootRotation * truckParkingOffsetLocal;
                    truck.position = new Vector3(truckXZ.x,
                        selectedTruckGroundY - originalTruckBottomOffset, truckXZ.z);
                    truck.rotation = selectedRootRotation * Quaternion.Euler(0f, truckParkingYawDegrees, 0f);
                    Physics.SyncTransforms();
                    soil.GetComponent<Terrain>().enabled = true; soil.SoilCollider.enabled = true;
                    cutter.enabled = originalCutter;
                    if (resistance != null) resistance.enabled = originalResistance;
                    keyboard.fixedBase = originalFixed; root.immovable = originalFixed; root.useGravity = !originalFixed;
                    machine.enabled = originalMachine;
                    keyboard.enabled = originalKeyboard;
                    IsReady = true; Status = "Stable lower bench and cuttable slope band ready";
                    Debug.Log($"SLOPE WORKFACE READY support={selectedSupportY:F3}, truck={selectedTruckGroundY:F3}, " +
                        $"strip={soilStripHeight:F2}m normal thickness x {soilStripHalfWidth * 2f:F1}m wide, " +
                        $"face={selectedFaceDirection}, root={root.transform.position}, " +
                        $"relocated={root.transform.position - originalRootPosition}, predictedRise={selectedFaceRise:F2}m", this);
                    yield break;
                }
            }
            yield return new WaitForSecondsRealtime(.25f);
        }
        if (IsReady) yield break;
        if (workFaceCoverageIncomplete)
            Status = "Cesium 坡面瓦片未能在限定时间内完整加载；设备保持原位，请调整视角或锚点后重试。";
        else if (string.IsNullOrEmpty(Status) || Status == "Waiting for map ground")
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

    private struct WorkFaceCandidate
    {
        internal Vector2 rootXZ;
        internal Vector3 direction;
        internal float supportY;
        internal float truckGroundY;
        internal float rise;
    }

    private bool TryTrackSupport(MeshCollider[] map, out float support)
    {
        support = float.NegativeInfinity;
        Bounds tracks = TrackBounds();
        float minimum = float.PositiveInfinity;
        foreach (int x in new[] { -1, 1 })
        foreach (int z in new[] { -1, 1 })
        {
            Vector3 point = tracks.center + new Vector3(x * tracks.extents.x * .7f, 0,
                z * tracks.extents.z * .7f);
            if (!Sample(map, point, out float y)) return false;
            support = Mathf.Max(support, y);
            minimum = Mathf.Min(minimum, y);
        }
        return support - minimum <= .35f;
    }

    private bool PrepareMound(MeshCollider[] map)
    {
        workFaceCoverageIncomplete = false;
        if (!TryChooseWorkFace(map, out WorkFaceCandidate candidate))
        {
            Status = useExplicitWorkFaceAnchor
                ? $"指定坡脚锚点没有平整履带位、连续坡面或安全矿车位；设备保持原位。{candidateDiagnostics}"
                : $"设备周边没有同时满足坡面连续度、履带支撑和矿车支撑的可达工作面；设备保持原位。{candidateDiagnostics}";
            return false;
        }

        selectedRootPosition = new Vector3(candidate.rootXZ.x, originalRootPosition.y, candidate.rootXZ.y);
        selectedRootRotation = Quaternion.LookRotation(candidate.direction, Vector3.up);
        selectedFaceDirection = candidate.direction;
        selectedSupportY = candidate.supportY;
        selectedTruckGroundY = candidate.truckGroundY;
        selectedFaceRise = candidate.rise;

        soil.Initialize(soil.GetComponent<Terrain>());
        TerrainData data = soil.Terrain.terrainData;
        int n = data.heightmapResolution;
        Vector3 size = new Vector3(replacementAreaSize.x, 1f, replacementAreaSize.y);
        Vector3 origin = new Vector3(selectedRootPosition.x - size.x * .5f, 0,
            selectedRootPosition.z - size.z * .5f);
        soil.transform.position = origin;
        data.size = size;

        var baseGround = new float[n, n];
        var stripMask = new float[n, n];
        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            Vector3 point = origin + new Vector3(size.x * x / (n - 1f), 0, size.z * z / (n - 1f));
            if (!Sample(map, point, out float y))
            {
                Status = "所选坡面替换范围超出已加载 Cesium 地图；设备保持原位。";
                workFaceCoverageIncomplete = true;
                return false;
            }
            baseGround[z, x] = y + .008f;
            Vector3 local = Quaternion.Inverse(selectedRootRotation) * (point - selectedRootPosition);
            stripMask[z, x] = SoilStripMask(local);
            minimum = Mathf.Min(minimum, baseGround[z, x]);
            maximum = Mathf.Max(maximum, baseGround[z, x]);
        }

        ApplyBenchSupportPads(baseGround, origin, size, candidate.rootXZ, candidate.direction,
            candidate.supportY, candidate.truckGroundY);
        if (!MeasurePreparedSupport(baseGround, origin, size, candidate.rootXZ, candidate.direction,
            out selectedSupportY, out selectedTruckGroundY, out string supportFailure))
        {
            Status = $"{supportFailure}；设备保持原位。";
            return false;
        }

        float dx = size.x / (n - 1f), dz = size.z / (n - 1f);
        int normalStepX = Mathf.Clamp(Mathf.RoundToInt(.75f / dx), 1, n / 8);
        int normalStepZ = Mathf.Clamp(Mathf.RoundToInt(.75f / dz), 1, n / 8);
        var heights = new float[n, n];
        var ground = new float[n, n];
        var targetWorldY = new float[n, n];
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            float thickness = 0f;
            if (stripMask[z, x] > .001f)
            {
                float slopeX = (baseGround[z, Mathf.Min(n - 1, x + normalStepX)] -
                    baseGround[z, Mathf.Max(0, x - normalStepX)]) / (2f * normalStepX * dx);
                float slopeZ = (baseGround[Mathf.Min(n - 1, z + normalStepZ), x] -
                    baseGround[Mathf.Max(0, z - normalStepZ), x]) / (2f * normalStepZ * dz);
                Vector3 normal = new Vector3(-slopeX, 1f, -slopeZ).normalized;
                if (normal.y < minimumSurfaceNormalY)
                {
                    Status = $"坡面局部过陡（法线 Y={normal.y:F2}），Unity 高度场无法安全表示；设备保持原位。";
                    return false;
                }
                thickness = GetVerticalOffsetForNormalThickness(
                    soilStripHeight * stripMask[z, x], normal, minimumSurfaceNormalY);
            }
            targetWorldY[z, x] = baseGround[z, x] + thickness;
            maximum = Mathf.Max(maximum, targetWorldY[z, x]);
        }

        origin.y = minimum - .35f;
        soil.transform.position = origin;
        size.y = maximum - origin.y + .5f;
        data.size = size;
        soil.maximumCutDepth = soilStripHeight + .15f;
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            ground[z, x] = (baseGround[z, x] - origin.y) / size.y;
            heights[z, x] = (targetWorldY[z, x] - origin.y) / size.y;
        }
        soil.PrepareMoundSurface(heights, ground);
        int holesResolution = data.holesResolution;
        var solidCells = new bool[holesResolution, holesResolution];
        for (int z = 0; z < holesResolution; z++)
        for (int x = 0; x < holesResolution; x++) solidCells[z, x] = true;
        data.SetHoles(0, 0, solidCells);
        ConstructionSiteSurfaceAppearance appearance = GetComponent<ConstructionSiteSurfaceAppearance>();
        if (appearance == null) appearance = gameObject.AddComponent<ConstructionSiteSurfaceAppearance>();
        if (!appearance.ApplySlopeBand(tileset.gameObject, soil, WorkAreaBounds, stripMask))
            Debug.Log("Cesium map image unavailable for slope texture; retaining the existing soil TerrainLayer.", this);
        Status = "坡脚、坡面和矿车支撑均已通过验证，贴坡土带已生成。";
        return true;
    }

    private bool TryChooseWorkFace(MeshCollider[] map, out WorkFaceCandidate best)
    {
        best = default;
        float spacing = Mathf.Max(.5f, workFaceSearchSpacing);
        int searchCells = Mathf.CeilToInt(workFaceSearchRadius / spacing);
        float actualRadius = searchCells * spacing;
        float padding = Mathf.Max(replacementAreaSize.x, replacementAreaSize.y) * .5f + 4f;
        int paddingCells = Mathf.CeilToInt(padding / spacing);
        int resolution = searchCells * 2 + paddingCells * 2 + 1;
        Vector3 original = originalRootPosition;
        Vector3 gridOrigin = new Vector3(original.x - (searchCells + paddingCells) * spacing, 0,
            original.z - (searchCells + paddingCells) * spacing);
        var samples = new float[resolution, resolution];
        int validSamples = 0;
        for (int z = 0; z < resolution; z++)
        for (int x = 0; x < resolution; x++)
        {
            Vector3 point = gridOrigin + new Vector3(x * spacing, 0, z * spacing);
            samples[z, x] = Sample(map, point, out float y) ? y : float.NaN;
            if (float.IsFinite(samples[z, x])) validSamples++;
        }

        int candidateStride = Mathf.Max(1, Mathf.RoundToInt(2f / spacing));
        int first = paddingCells;
        int last = paddingCells + searchCells * 2;
        float bestScore = float.PositiveInfinity;
        int testedPositions = 0, coveredPositions = 0, validFaces = 0;
        int[] rejected = new int[8];
        int directionCount = 16;
        int firstX = useExplicitWorkFaceAnchor
            ? Mathf.RoundToInt((workFaceAnchor.x - gridOrigin.x) / spacing) : first;
        int lastX = useExplicitWorkFaceAnchor ? firstX : last;
        int firstZ = useExplicitWorkFaceAnchor
            ? Mathf.RoundToInt((workFaceAnchor.z - gridOrigin.z) / spacing) : first;
        int lastZ = useExplicitWorkFaceAnchor ? firstZ : last;
        for (int z = firstZ; z <= lastZ; z += useExplicitWorkFaceAnchor ? 1 : candidateStride)
        for (int x = firstX; x <= lastX; x += useExplicitWorkFaceAnchor ? 1 : candidateStride)
        {
            Vector2 rootXZ = useExplicitWorkFaceAnchor
                ? new Vector2(workFaceAnchor.x, workFaceAnchor.z)
                : new Vector2(gridOrigin.x + x * spacing, gridOrigin.z + z * spacing);
            testedPositions++;
            if (!useExplicitWorkFaceAnchor &&
                Vector2.Distance(rootXZ, new Vector2(original.x, original.z)) > actualRadius) continue;
            if (!HasReplacementCoverage(samples, gridOrigin, spacing, rootXZ)) continue;
            coveredPositions++;

            for (int directionIndex = 0; directionIndex < directionCount; directionIndex++)
            {
                float angle = directionIndex * (360f / directionCount);
                Vector3 direction = Quaternion.Euler(0, angle, 0) * Vector3.forward;
                if (!EvaluateWorkFace(samples, gridOrigin, spacing, rootXZ, direction,
                    out float supportY, out float truckY, out float rise, out float supportSpread,
                    out int rejectionStage))
                {
                    rejected[rejectionStage]++;
                    continue;
                }
                validFaces++;
                float distance = Vector2.Distance(rootXZ, new Vector2(original.x, original.z));
                float score = Mathf.Abs(rise - 3.2f) + distance * .004f + supportSpread * 2f;
                if (score >= bestScore) continue;
                bestScore = score;
                best = new WorkFaceCandidate
                {
                    rootXZ = rootXZ,
                    direction = direction,
                    supportY = supportY,
                    truckGroundY = truckY,
                    rise = rise
                };
            }
        }
        candidateDiagnostics = $"[坡面搜索采样={validSamples}/{resolution * resolution}，" +
            $"位置={testedPositions}，替换范围完整={coveredPositions}，通过筛选={validFaces}，" +
            $"失败类别(地图/履带不平/矿车地图/矿车不平/坡面地图/坡形/坡度)=" +
            $"{rejected[1]}/{rejected[2]}/{rejected[3]}/{rejected[4]}/{rejected[5]}/{rejected[6]}/{rejected[7]}]";
        workFaceCoverageIncomplete = ShouldRetryWorkFaceSearch(
            coveredPositions, validFaces, validSamples, resolution * resolution);
        return float.IsFinite(bestScore);
    }

    private static bool ShouldRetryWorkFaceSearch(
        int coveredPositions, int validFaces, int validSamples, int totalSamples)
    {
        bool samplesIncomplete = validSamples < totalSamples;
        return samplesIncomplete && (coveredPositions == 0 || validFaces == 0);
    }

    private bool MeasurePreparedSupport(float[,] ground, Vector3 origin, Vector3 size,
        Vector2 rootXZ, Vector3 direction, out float supportY, out float truckY, out string failure)
    {
        Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
        Vector3 rootPoint = new Vector3(rootXZ.x, 0, rootXZ.y);
        float minimum = float.PositiveInfinity;
        supportY = float.NegativeInfinity;
        foreach (float x in new[] { -1.5f, 1.5f })
        foreach (float z in new[] { -2.2f, 2.2f })
        {
            Vector3 point = rootPoint + rotation * new Vector3(x, 0, z);
            if (!SamplePreparedHeight(ground, origin, size, point, out float y))
            { truckY = 0; failure = "履带支撑点超出已采样地图"; return false; }
            minimum = Mathf.Min(minimum, y);
            supportY = Mathf.Max(supportY, y);
        }
        if (supportY - minimum > .4f)
        {
            truckY = 0; failure = $"履带地面高差 {supportY - minimum:F2}m 超过0.40m"; return false;
        }

        Vector3 truckCenter = rootPoint + rotation * truckParkingOffsetLocal;
        Quaternion truckRotation = rotation * Quaternion.Euler(0, truckParkingYawDegrees, 0);
        float truckMinimum = float.PositiveInfinity;
        truckY = float.NegativeInfinity;
        foreach (float x in new[] { -1.35f, 1.35f })
        foreach (float z in new[] { -1.35f, 3.25f })
        {
            Vector3 point = truckCenter + truckRotation * new Vector3(x, 0, z);
            if (!SamplePreparedHeight(ground, origin, size, point, out float y))
            { failure = "矿车支撑点超出已采样地图"; return false; }
            truckMinimum = Mathf.Min(truckMinimum, y);
            truckY = Mathf.Max(truckY, y);
        }
        float truckSpread = truckY - truckMinimum;
        failure = truckSpread <= .5f ? string.Empty : $"矿车地面高差 {truckSpread:F2}m 超过0.50m";
        return truckSpread <= .5f;
    }

    private void ApplyBenchSupportPads(float[,] ground, Vector3 origin, Vector3 size,
        Vector2 rootXZ, Vector3 direction, float machineY, float truckY)
    {
        Quaternion machineRotation = Quaternion.LookRotation(direction, Vector3.up);
        Vector3 machineCenter = new Vector3(rootXZ.x, 0, rootXZ.y);
        Quaternion truckRotation = machineRotation * Quaternion.Euler(0, truckParkingYawDegrees, 0);
        Vector3 truckCenter = machineCenter + machineRotation * truckParkingOffsetLocal +
            truckRotation * new Vector3(0, 0, .95f);
        Quaternion inverseTruckRotation = Quaternion.Inverse(truckRotation);
        for (int z = 0; z < ground.GetLength(0); z++)
        for (int x = 0; x < ground.GetLength(1); x++)
        {
            Vector3 point = origin + new Vector3(size.x * x / (ground.GetLength(1) - 1f), 0,
                size.z * z / (ground.GetLength(0) - 1f));
            Vector3 machineLocal = Quaternion.Inverse(machineRotation) * (point - machineCenter);
            Vector3 truckLocal = inverseTruckRotation * (point - truckCenter);
            float machineMask = FlatSupportMask(machineLocal.x, machineLocal.z, 2.3f, 2.8f, .8f);
            float truckMask = FlatSupportMask(truckLocal.x, truckLocal.z, 1.6f, 2.3f, .65f);
            float mask = Mathf.Max(machineMask, truckMask);
            if (mask <= 0f) continue;
            float target = machineMask >= truckMask ? machineY : truckY;
            ground[z, x] = BlendSupportHeightWithoutLowering(ground[z, x], target, mask);
        }
    }

    private static float BlendSupportHeightWithoutLowering(float sampledHeight, float supportTarget, float blend)
    {
        float raisedTarget = Mathf.Max(sampledHeight, supportTarget);
        return Mathf.Lerp(sampledHeight, raisedTarget, Mathf.Clamp01(blend));
    }

    private static float FlatSupportMask(float x, float z, float halfWidth, float halfLength, float blend)
    {
        float width = 1f - Smooth01((Mathf.Abs(x) - halfWidth) / blend);
        float length = 1f - Smooth01((Mathf.Abs(z) - halfLength) / blend);
        return width * length;
    }

    private static bool SamplePreparedHeight(float[,] ground, Vector3 origin, Vector3 size,
        Vector3 point, out float height)
    {
        float x = (point.x - origin.x) / size.x * (ground.GetLength(1) - 1);
        float z = (point.z - origin.z) / size.z * (ground.GetLength(0) - 1);
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
        int x1 = x0 + 1, z1 = z0 + 1;
        height = 0f;
        if (x0 < 0 || z0 < 0 || x1 >= ground.GetLength(1) || z1 >= ground.GetLength(0)) return false;
        float a = ground[z0, x0], b = ground[z0, x1], c = ground[z1, x0], d = ground[z1, x1];
        height = Mathf.Lerp(Mathf.Lerp(a, b, x - x0), Mathf.Lerp(c, d, x - x0), z - z0);
        return float.IsFinite(height);
    }

    private bool HasReplacementCoverage(float[,] samples, Vector3 gridOrigin, float spacing, Vector2 rootXZ)
    {
        Vector3 center = new Vector3(rootXZ.x, 0, rootXZ.y);
        foreach (int x in new[] { -1, 1 })
        foreach (int z in new[] { -1, 1 })
        {
            Vector3 corner = center + new Vector3(
                x * replacementAreaSize.x * .5f, 0, z * replacementAreaSize.y * .5f);
            if (!GridSample(samples, gridOrigin, spacing, corner, out _)) return false;
        }
        return true;
    }

    private bool EvaluateWorkFace(float[,] samples, Vector3 gridOrigin, float spacing,
        Vector2 rootXZ, Vector3 direction, out float supportY, out float truckY,
        out float rise, out float supportSpread, out int rejectionStage)
    {
        supportY = truckY = rise = supportSpread = 0f;
        rejectionStage = 1;
        Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
        float minSupport = float.PositiveInfinity;
        supportY = float.NegativeInfinity;
        foreach (float x in new[] { -1.5f, 1.5f })
        foreach (float z in new[] { -2.2f, 2.2f })
        {
            Vector3 point = new Vector3(rootXZ.x, 0, rootXZ.y) + rotation * new Vector3(x, 0, z);
            if (!GridSample(samples, gridOrigin, spacing, point, out float y)) return false;
            minSupport = Mathf.Min(minSupport, y);
            supportY = Mathf.Max(supportY, y);
        }
        supportSpread = supportY - minSupport;
        rejectionStage = 2;
        if (supportSpread > .6f) return false;

        Vector3 truckCenter = new Vector3(rootXZ.x, 0, rootXZ.y) + rotation * truckParkingOffsetLocal;
        Quaternion truckRotation = rotation * Quaternion.Euler(0, truckParkingYawDegrees, 0);
        float minimumTruck = float.PositiveInfinity;
        truckY = float.NegativeInfinity;
        rejectionStage = 3;
        foreach (float x in new[] { -1.35f, 1.35f })
        foreach (float z in new[] { -1.35f, 3.25f })
        {
            Vector3 point = truckCenter + truckRotation * new Vector3(x, 0, z);
            if (!GridSample(samples, gridOrigin, spacing, point, out float y)) return false;
            minimumTruck = Mathf.Min(minimumTruck, y);
            truckY = Mathf.Max(truckY, y);
        }
        rejectionStage = 4;
        if (truckY - minimumTruck > .6f) return false;

        float[] lateral = { -soilStripHalfWidth * .75f, 0f, soilStripHalfWidth * .75f };
        float foot = 0, middle = 0, crest = 0;
        float minFoot = float.PositiveInfinity, maxFoot = float.NegativeInfinity;
        float minCrest = float.PositiveInfinity, maxCrest = float.NegativeInfinity;
        rejectionStage = 5;
        foreach (float x in lateral)
        {
            Vector3 side = rotation * new Vector3(x, 0, 0);
            Vector3 startPoint = new Vector3(rootXZ.x, 0, rootXZ.y) + side + direction * 2.5f;
            Vector3 centerPoint = new Vector3(rootXZ.x, 0, rootXZ.y) + side + direction * soilStripCenterLocalZ;
            Vector3 topPoint = new Vector3(rootXZ.x, 0, rootXZ.y) + side + direction * 10.5f;
            if (!GridSample(samples, gridOrigin, spacing, startPoint, out float startY) ||
                !GridSample(samples, gridOrigin, spacing, centerPoint, out float centerY) ||
                !GridSample(samples, gridOrigin, spacing, topPoint, out float topY)) return false;
            foot += startY / lateral.Length;
            middle += centerY / lateral.Length;
            crest += topY / lateral.Length;
            minFoot = Mathf.Min(minFoot, startY); maxFoot = Mathf.Max(maxFoot, startY);
            minCrest = Mathf.Min(minCrest, topY); maxCrest = Mathf.Max(maxCrest, topY);
        }
        rise = crest - foot;
        rejectionStage = 6;
        if (rise < minimumWorkFaceRise || rise > 12f || middle - foot < -1.5f ||
            crest - middle < -1.5f || maxFoot - minFoot > 6f || maxCrest - minCrest > 8f)
            return false;

        float slopeX = 0f;
        if (GridSample(samples, gridOrigin, spacing,
                new Vector3(rootXZ.x, 0, rootXZ.y) + direction * soilStripCenterLocalZ +
                    rotation * Vector3.right * .75f, out float rightY) &&
            GridSample(samples, gridOrigin, spacing,
                new Vector3(rootXZ.x, 0, rootXZ.y) + direction * soilStripCenterLocalZ -
                    rotation * Vector3.right * .75f, out float leftY))
            slopeX = (rightY - leftY) / 1.5f;
        float slopeZ = (crest - foot) / 8f;
        float normalY = 1f / Mathf.Sqrt(1f + slopeX * slopeX + slopeZ * slopeZ);
        rejectionStage = 7;
        return normalY >= minimumSurfaceNormalY;
    }

    private float SoilStripMask(Vector3 machineLocal)
    {
        float side = 1f - Smooth01((Mathf.Abs(machineLocal.x) - (soilStripHalfWidth - .7f)) / .7f);
        float distance = Mathf.Abs(machineLocal.z - soilStripCenterLocalZ);
        float end = 1f - Smooth01((distance - (soilStripHalfDepth - .25f)) / .25f);
        return side * end;
    }

    private static bool GridSample(float[,] samples, Vector3 origin, float spacing, Vector3 point, out float y)
    {
        float x = (point.x - origin.x) / spacing;
        float z = (point.z - origin.z) / spacing;
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
        int x1 = x0 + 1, z1 = z0 + 1;
        y = 0;
        if (x0 < 0 || z0 < 0 || x1 >= samples.GetLength(1) || z1 >= samples.GetLength(0)) return false;
        float a = samples[z0, x0], b = samples[z0, x1], c = samples[z1, x0], d = samples[z1, x1];
        if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c) || !float.IsFinite(d)) return false;
        y = Mathf.Lerp(Mathf.Lerp(a, b, x - x0), Mathf.Lerp(c, d, x - x0), z - z0);
        return true;
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
