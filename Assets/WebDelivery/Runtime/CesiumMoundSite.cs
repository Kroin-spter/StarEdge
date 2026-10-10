using System;
using System.Collections;
using System.Linq;
using CesiumForUnity;
using UnityEngine;

/// <summary>Replace one local Cesium footprint with a cuttable slope band and stable bench-side vehicle positions.</summary>
[DefaultExecutionOrder(-13000)]
public sealed class CesiumMoundSite : MonoBehaviour
{
    public enum SitePreparationMode { Baked = 0, DynamicDeveloper = 1 }
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
    [Header("Construction site source")]
    [Tooltip("Baked is the delivery path. DynamicDeveloper surveys Cesium at Play time for authoring only.")]
    public SitePreparationMode siteMode = SitePreparationMode.Baked;
    public BakedConstructionSiteData bakedSite;
    [Tooltip("Cesium local tileset directory name. The directory is resolved through CESIUM_TILESET_DIR or the project sibling tilesets folder.")]
    public string mapName = "osgb_small_005_004";
    [Header("Slope work-face layout")]
    public Vector2 replacementAreaSize = new Vector2(28f, 28f);
    [Tooltip("Enable only when a known lower-bench world position is available; otherwise a nearby safe slope is selected automatically.")]
    public bool useExplicitWorkFaceAnchor;
    [Tooltip("World X/Z of the desired track-base center. Automatic slope search is used when disabled.")]
    public Vector3 workFaceAnchor;
    [Tooltip("Use the authored machine heading and upper-bench height as references while selecting a lower work face.")]
    public bool usePrepositionedEquipment;
    [Tooltip("Move the excavator and truck to the selected work face after all map, support, slope, and bucket-clearance checks pass.")]
    public bool relocateEquipmentToSelectedWorkFace;
    [Tooltip("Measured upper-bench support height used to report the bench drop for pre-positioned equipment.")]
    public float prepositionedUpperBenchSupportY = 7.512f;
    [Min(8f)] public float workFaceSearchRadius = 60f;
    [Min(.5f)] public float workFaceSearchSpacing = 2f;
    [Min(.1f)] public float minimumWorkFaceRise = 2.5f;
    [Min(0f)] public float minimumBenchDropMeters = 2f;
    [Range(.2f, .9f)] public float minimumSurfaceNormalY = .55f;
    [Min(.25f)] public float minimumReadyTipClearanceMeters = .6f;
    [Min(.1f)] public float soilStripHalfWidth = 6f;
    public float soilStripCenterLocalX;
    [Min(.1f)] public float soilStripCenterLocalZ = 6.7f;
    [Min(.1f)] public float soilStripHalfDepth = 2f;
    [Min(.05f)] public float soilStripHeight = .55f;
    [Header("Truck parking on the lower bench")]
    public Vector3 truckParkingOffsetLocal = new Vector3(7.9f, 0f, -1.2f);
    public float truckParkingYawDegrees = 90f;
    [Min(1f)] public float loadingTimeout = 30f;
    public bool IsReady { get; private set; }
    /// <summary>Check whether the parked-base excavation route remains valid after manual driving.</summary>
    /// <param name="position">Current articulation-root world position.</param>
    /// <param name="rotation">Current articulation-root world rotation.</param>
    /// <returns>True before preparation or while the machine remains close to its surveyed work pose.</returns>
    public bool CanStartAutonomousFrom(Vector3 position, Quaternion rotation)
    {
        if (!IsReady || siteMode != SitePreparationMode.Baked || bakedSite == null) return true;
        Vector3 offset = position - bakedSite.machinePosition;
        return new Vector2(offset.x, offset.z).magnitude <= .15f &&
            Mathf.Abs(offset.y) <= 1f &&
            Quaternion.Angle(rotation, bakedSite.machineRotation) <= 3f;
    }
    /// <summary>Get the surveyed base target used by a bounded physical return.</summary>
    public Vector3 AutonomousBasePosition => bakedSite != null ? bakedSite.machinePosition : selectedRootPosition;
    /// <summary>Get the surveyed base orientation used by a bounded physical return.</summary>
    public Quaternion AutonomousBaseRotation => bakedSite != null ? bakedSite.machineRotation : selectedRootRotation;
    /// <summary>Limit physical return to the nearby lower-bench travel corridor.</summary>
    /// <param name="position">Measured track-root position.</param>
    /// <returns>True when the root remains in a short, mostly longitudinal corridor around the work pose.</returns>
    public bool CanReturnAutonomousFrom(Vector3 position)
    {
        if (!IsReady || siteMode != SitePreparationMode.Baked || bakedSite == null) return false;
        Vector3 local = Quaternion.Inverse(bakedSite.machineRotation) *
            (position - bakedSite.machinePosition);
        return Mathf.Abs(local.x) <= 2.5f && Mathf.Abs(local.z) <= 8f &&
            Mathf.Abs(local.y) <= 1.5f;
    }
    public bool MapSurfacePrepared { get; private set; }
    public string Status { get; private set; } = "Waiting for map ground";
    /// <summary>Vertical drop from the excavator's original platform to the selected lower bench.</summary>
    public float BenchDropMeters => float.IsFinite(originalSupportY) && float.IsFinite(selectedSupportY)
        ? originalSupportY - selectedSupportY : 0f;
    /// <summary>Whether the track base was fixed before temporary Cesium site preparation began.</summary>
    public bool OriginalFixedBase => originalFixed;
    /// <summary>Predicted clearance from the initial bucket tooth to the prepared slope band.</summary>
    public float ReadyTipClearanceMeters { get; private set; }
    /// <summary>Vertical component of the normalized surface normal at the selected work face.</summary>
    public float SelectedSurfaceNormalY { get; private set; }
    /// <summary>Number of frame yields used while sampling the Cesium work area.</summary>
    public int SurveyFrameYieldCount { get; private set; }
    /// <summary>World-space center of the face-only cuttable replacement.</summary>
    public Vector3 WorkFaceCenter => siteMode == SitePreparationMode.Baked && bakedSite != null
        ? bakedSite.workFaceCenter
        : selectedRootPosition + selectedRootRotation *
            new Vector3(soilStripCenterLocalX, 0f, soilStripCenterLocalZ);
    /// <summary>World orientation of the cuttable slope face.</summary>
    public Quaternion WorkFaceRotation => siteMode == SitePreparationMode.Baked && bakedSite != null
        ? bakedSite.workFaceRotation : selectedRootRotation;
    /// <summary>Width and depth of the cuttable slope face, in metres.</summary>
    public Vector2 WorkFaceSize => siteMode == SitePreparationMode.Baked && bakedSite != null
        ? bakedSite.workFaceSize : new Vector2(soilStripHalfWidth * 2f, soilStripHalfDepth * 2f);
    /// <summary>World-axis-aligned bounds of the face-only Cesium replacement and its Terrain height range.</summary>
    public Bounds WorkAreaBounds
    {
        get
        {
            if (siteMode == SitePreparationMode.Baked && bakedSite != null && bakedSite.terrainData != null)
                return bakedSite.GetWorkFaceBounds();
            Terrain terrain = soil != null ? soil.GetComponent<Terrain>() : null;
            if (terrain == null || terrain.terrainData == null) return default;
            Vector3 size = terrain.terrainData.size;
            Vector2 faceSize = WorkFaceSize;
            Quaternion faceRotation = WorkFaceRotation;
            Vector3 faceCenter = WorkFaceCenter;
            if (!float.IsFinite(faceSize.x) || !float.IsFinite(faceSize.y) || faceSize.x <= 0f || faceSize.y <= 0f ||
                !float.IsFinite(faceRotation.x) || !float.IsFinite(faceRotation.y) ||
                !float.IsFinite(faceRotation.z) || !float.IsFinite(faceRotation.w) ||
                Quaternion.Dot(faceRotation, faceRotation) < .9f ||
                !float.IsFinite(faceCenter.x) || !float.IsFinite(faceCenter.y) || !float.IsFinite(faceCenter.z))
                return new Bounds(terrain.transform.position + size * .5f, size);
            return ConstructionSiteFootprint.CalculateBounds(faceCenter, faceRotation, faceSize,
                terrain.transform.position.y, terrain.transform.position.y + size.y);
        }
    }

    private void ConfigureMapClip(ConstructionSiteMapClip clip)
    {
        Terrain terrain = soil.GetComponent<Terrain>();
        float minimumY = terrain.transform.position.y;
        float maximumY = minimumY + terrain.terrainData.size.y;
        clip.SetWorkFaceFootprint(WorkFaceCenter, WorkFaceRotation, WorkFaceSize, minimumY, maximumY);
        // The terrain is a thin coating on the Cesium face, not a replacement of the
        // vertical map column. Keep the map (and its road/bench collision) underneath.
        clip.enabled = false;
    }
    private Zx200KeyboardController keyboard;
    private ArticulationBody root;
    private bool originalFixed, originalKeyboard, originalMachine;
    private BucketSoilCutter cutter;
    private Vector3 readyTipOffsetInRoot;
    private Vector3 swingPivotOffsetInRoot;
    private BucketSoilResistance resistance;
    private bool originalCutter, originalResistance;
    private Vector3 originalRootPosition;
    private Bounds originalTrackBounds;
    private float originalTruckBottomOffset;
    private float selectedSupportY;
    private float selectedTruckGroundY;
    private Vector3 selectedRootPosition;
    private Quaternion selectedRootRotation = Quaternion.identity;
    private Vector3 selectedFaceDirection;
    private float selectedFaceRise;
    private string candidateDiagnostics = string.Empty;
    private bool workFaceCoverageIncomplete;
    private bool workFaceSearchSucceeded;
    private bool moundPreparationSucceeded;
    private float originalSupportY = float.NaN;
    private int lastSearchMapRevision = int.MinValue;
    private WorkFaceCandidate selectedWorkFace;
    private float[,] preparedSoilMask;
    private CesiumCameraManager cameraManager;
    private Camera surveyCamera;
    private bool originalSceneViewCameraInEditor;
    private bool sceneViewCameraSettingCaptured;

    private void Awake()
    {
        if (soil == null || machine == null || tileset == null || truck == null)
        {
            Status = "Missing mound site references";
            enabled = false;
            return;
        }
        soil.deferAutomaticInitialization = true;
        ResolveTilesetUrl();
        keyboard = machine.GetComponent<Zx200KeyboardController>();
        keyboard.ResolveArticulationRoot(); root = keyboard.articulationRoot;
        originalRootPosition = root.transform.position;
        if (siteMode == SitePreparationMode.DynamicDeveloper)
        {
            InstallSurveyCamera();
            soil.InitializeForConstructionSite(soil.GetComponent<Terrain>());
        }
        else if (bakedSite != null && bakedSite.terrainData != null)
        {
            // Keep Cesium requesting the parked bench even when no Game view is rendered.
            InstallSurveyCamera();
            soil.GetComponent<Terrain>().terrainData = bakedSite.terrainData;
            soil.SoilCollider.terrainData = bakedSite.terrainData;
        }
        Collider[] truckColliders = truck.GetComponentsInChildren<Collider>()
            .Where(c => c.enabled && !c.isTrigger).ToArray();
        if (truckColliders.Length == 0)
        {
            Status = "Truck has no solid support colliders";
            ReleaseSurveyCamera();
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
        readyTipOffsetInRoot = root.transform.InverseTransformPoint(cutter.TipWorldPosition);
        if (machine.swingJoint != null)
            swingPivotOffsetInRoot = root.transform.InverseTransformPoint(
                machine.swingJoint.transform.TransformPoint(machine.swingJoint.anchorPosition));
        resistance = cutter.GetComponent<BucketSoilResistance>();
        originalCutter = cutter.enabled; cutter.enabled = false;
        if (resistance != null) { originalResistance = resistance.enabled; resistance.enabled = false; }
        soil.GetComponent<Terrain>().enabled = false;
        soil.SoilCollider.enabled = false;
    }

    private void OnDestroy()
    {
        ReleaseSurveyCamera();
    }

    private void OnDisable()
    {
        if (!IsReady) ReleaseSurveyCamera();
    }

    private void InstallSurveyCamera()
    {
        cameraManager = tileset.GetComponentInParent<CesiumCameraManager>();
        if (cameraManager == null) cameraManager = CesiumCameraManager.GetOrCreate(tileset.gameObject);
        if (cameraManager != null && !sceneViewCameraSettingCaptured)
        {
            originalSceneViewCameraInEditor = cameraManager.useSceneViewCameraInEditor;
            sceneViewCameraSettingCaptured = true;
            cameraManager.useSceneViewCameraInEditor = false;
        }

        float footprintPadding = ReplacementSearchPadding();
        float halfExtent = workFaceSearchRadius + footprintPadding;
        float altitude = Mathf.Max(120f, halfExtent * 2f);
        var cameraObject = new GameObject("Cesium work-face survey camera")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        surveyCamera = cameraObject.AddComponent<Camera>();
        surveyCamera.enabled = false;
        surveyCamera.orthographic = true;
        surveyCamera.orthographicSize = halfExtent;
        surveyCamera.aspect = 1f;
        surveyCamera.nearClipPlane = .1f;
        surveyCamera.farClipPlane = altitude + 200f;
        surveyCamera.transform.SetPositionAndRotation(
            originalRootPosition + Vector3.up * altitude,
            Quaternion.LookRotation(Vector3.down, Vector3.forward));
        cameraManager.additionalCameras.Add(surveyCamera);
    }

    private void ReleaseSurveyCamera()
    {
        RestoreSceneViewCameraSetting();
        if (cameraManager != null && surveyCamera != null)
            cameraManager.additionalCameras.Remove(surveyCamera);
        if (surveyCamera != null)
        {
            if (Application.isPlaying) Destroy(surveyCamera.gameObject);
            else DestroyImmediate(surveyCamera.gameObject);
        }
        surveyCamera = null;
    }

    private void RestoreSceneViewCameraSetting()
    {
        if (!sceneViewCameraSettingCaptured) return;
        if (cameraManager != null)
            cameraManager.useSceneViewCameraInEditor = originalSceneViewCameraInEditor;
        sceneViewCameraSettingCaptured = false;
    }

    private void FocusSurveyCameraOnWorkFace()
    {
        if (surveyCamera == null) return;
        float footprintPadding = ReplacementSearchPadding();
        float altitude = Mathf.Max(120f, (workFaceSearchRadius + footprintPadding) * 2f);
        surveyCamera.transform.SetPositionAndRotation(
            selectedRootPosition + Vector3.up * altitude,
            Quaternion.LookRotation(Vector3.down, Vector3.forward));
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
        if (siteMode == SitePreparationMode.Baked)
        {
            yield return LoadBakedSite();
            yield break;
        }
        float deadline = Time.realtimeSinceStartup + loadingTimeout;
        float previousGround = float.NaN;
        int stable = 0;
        int lastAttemptedMapRevision = int.MinValue;
        while (Time.realtimeSinceStartup < deadline)
        {
            MeshCollider[] map = GetLoadedMapColliders();
            if (TryTrackSupport(map, out float support))
            {
                stable = float.IsFinite(previousGround) && Mathf.Abs(previousGround - support) < .025f ? stable + 1 : 0;
                previousGround = support;
                if (stable >= 3)
                {
                    if (!float.IsFinite(originalSupportY))
                    {
                        if (usePrepositionedEquipment &&
                            (!float.IsFinite(prepositionedUpperBenchSupportY) ||
                             prepositionedUpperBenchSupportY <= support + minimumBenchDropMeters))
                        {
                            Status = "预置低台位置未能验证与上层平台的高度差；设备保持原位。";
                            ReleaseSurveyCamera();
                            Debug.LogWarning(Status, this);
                            yield break;
                        }
                        originalSupportY = usePrepositionedEquipment ? prepositionedUpperBenchSupportY : support;
                    }
                    int mapRevision = GetMapRevision(map);
                    if (mapRevision == lastAttemptedMapRevision)
                    {
                        Status = $"等待 Cesium 坡面瓦片加载；当前碰撞瓦片={map.Length}。";
                        yield return null;
                        continue;
                    }
                    lastAttemptedMapRevision = mapRevision;
                    lastSearchMapRevision = mapRevision;
                    yield return PrepareMound(map, support);
                    if (!moundPreparationSucceeded)
                    {
                        if (workFaceCoverageIncomplete)
                        {
                            Status = $"等待坡面瓦片覆盖完整采样区；碰撞瓦片={GetLoadedMapColliders().Length}，{candidateDiagnostics}";
                            yield return null;
                            continue;
                        }
                        ReleaseSurveyCamera();
                        Debug.LogWarning(Status, this);
                        yield break;
                    }
                    FocusSurveyCameraOnWorkFace();
                    // Replace the source map over one matching footprint before parking either vehicle on the bench.
                    MapSurfacePrepared = true;
                    ConstructionSiteMapClip clip = tileset.GetComponent<ConstructionSiteMapClip>();
                    if (clip == null) clip = tileset.gameObject.AddComponent<ConstructionSiteMapClip>();
                    ConfigureMapClip(clip);
                    Physics.SyncTransforms();

                    // Move equipment only after the replacement terrain and all work-face checks are ready.
                    Vector3 rootTarget = originalRootPosition;
                    if (relocateEquipmentToSelectedWorkFace)
                    {
                        float bottom = originalTrackBounds.min.y;
                        rootTarget = selectedRootPosition + Vector3.up * (selectedSupportY + .025f - bottom);
                        root.TeleportRoot(rootTarget, selectedRootRotation);
                        root.linearVelocity = Vector3.zero; root.angularVelocity = Vector3.zero;
                        Quaternion levelHeading = ConstructionSiteFootprint.GetHorizontalRotation(selectedRootRotation);
                        Vector3 truckXZ = selectedRootPosition + levelHeading * truckParkingOffsetLocal;
                        truck.position = new Vector3(truckXZ.x,
                            selectedTruckGroundY - originalTruckBottomOffset, truckXZ.z);
                        truck.rotation = levelHeading * Quaternion.Euler(0f, truckParkingYawDegrees, 0f);
                    }
                    Physics.SyncTransforms();
                    soil.GetComponent<Terrain>().enabled = true; soil.SoilCollider.enabled = true;
                    cutter.enabled = originalCutter;
                    if (resistance != null) resistance.enabled = originalResistance;
                    keyboard.fixedBase = originalFixed; root.immovable = originalFixed; root.useGravity = !originalFixed;
                    machine.enabled = originalMachine;
                    keyboard.enabled = originalKeyboard;
                    yield return new WaitForFixedUpdate();
                    Physics.SyncTransforms();
                    IsReady = true; Status = "Stable lower bench and cuttable slope band ready";
                    RestoreSceneViewCameraSetting();
                    Debug.Log($"SLOPE WORKFACE READY support={selectedSupportY:F3}, truck={selectedTruckGroundY:F3}, " +
                        $"strip={soilStripHeight:F2}m normal thickness x {soilStripHalfWidth * 2f:F1}m wide, " +
                        $"face={selectedFaceDirection}, root={root.transform.position}, " +
                        $"targetRoot={rootTarget}, relocated={root.transform.position - originalRootPosition}, " +
                        $"normalY={SelectedSurfaceNormalY:F2}, " +
                        $"trackBottom={TrackBounds().min.y:F3} initialTrackBottom={originalTrackBounds.min.y:F3}, " +
                        $"benchDrop={BenchDropMeters:F2}m predictedRise={selectedFaceRise:F2}m", this);
                    yield break;
                }
            }
            yield return new WaitForSecondsRealtime(.25f);
        }
        if (IsReady) yield break;
        if (workFaceCoverageIncomplete)
            Status = $"Cesium 坡面瓦片未能在限定时间内完整加载；设备保持原位。{candidateDiagnostics}";
        else if (string.IsNullOrEmpty(Status) || Status == "Waiting for map ground")
            Status = "Map support unavailable; machine held for safety. Reload scene to retry.";
        ReleaseSurveyCamera();
        Debug.LogWarning(Status, this);
    }

    /// <summary>Validate a baked configuration without silently selecting the dynamic Cesium survey.</summary>
    /// <param name="data">Referenced persistent site snapshot; may be null.</param>
    /// <param name="mapName">Configured source map identifier.</param>
    /// <param name="latitude">Current georeference latitude.</param>
    /// <param name="longitude">Current georeference longitude.</param>
    /// <param name="height">Current georeference height.</param>
    /// <param name="reason">Actionable failure reason or empty on success.</param>
    /// <returns>True only when the baked snapshot is present and valid.</returns>
    public static bool TryValidateBakedConfiguration(BakedConstructionSiteData data, string mapName,
        double latitude, double longitude, double height, out string reason)
    {
        if (data == null)
        {
            reason = "缺少烘焙施工区资源；请在动态开发模式完成一次现场烘焙，然后应用到 sence_test。";
            return false;
        }
        return data.TryValidate(mapName, latitude, longitude, height, out reason);
    }

    private IEnumerator LoadBakedSite()
    {
        CesiumGeoreference georeference = tileset.GetComponentInParent<CesiumGeoreference>();
        if (georeference == null)
        {
            Status = "缺少 Cesium 地理参考；无法校验烘焙施工区。";
            Debug.LogError(Status, this);
            yield break;
        }
        if (!TryValidateBakedConfiguration(bakedSite, mapName, georeference.latitude,
                georeference.longitude, georeference.height, out string reason))
        {
            Status = reason;
            Debug.LogError(Status, this);
            yield break;
        }
        bool loaded = false;
        try
        {
            bakedSite.ApplyTo(soil);
            originalSupportY = bakedSite.originalSupportY;
            selectedSupportY = bakedSite.selectedSupportY;
            selectedTruckGroundY = bakedSite.selectedTruckGroundY;
            ReadyTipClearanceMeters = bakedSite.readyTipClearanceMeters;
            SelectedSurfaceNormalY = bakedSite.selectedSurfaceNormalY;
            root.TeleportRoot(bakedSite.machinePosition, bakedSite.machineRotation);
            root.linearVelocity = Vector3.zero;
            root.angularVelocity = Vector3.zero;
            truck.SetPositionAndRotation(bakedSite.truckPosition, bakedSite.truckRotation);
            Physics.SyncTransforms();
            MapSurfacePrepared = true;
            ConstructionSiteMapClip clip = tileset.GetComponent<ConstructionSiteMapClip>();
            if (clip == null) clip = tileset.gameObject.AddComponent<ConstructionSiteMapClip>();
            ConfigureMapClip(clip);
            soil.GetComponent<Terrain>().enabled = true;
            soil.SoilCollider.enabled = true;
            loaded = true;
        }
        catch (Exception exception)
        {
            loaded = false;
            MapSurfacePrepared = false;
            soil.GetComponent<Terrain>().enabled = false;
            soil.SoilCollider.enabled = false;
            cutter.enabled = false;
            if (resistance != null) resistance.enabled = false;
            machine.enabled = false;
            keyboard.enabled = false;
            root.immovable = true;
            Status = "烘焙施工区加载失败，请重新烘焙：" + exception.Message;
            Debug.LogError(Status, this);
        }
        if (!loaded) yield break;

        // The baked Terrain contains only the slope. Tracks remain locked until the
        // original Cesium lower bench is physically present beneath them.
        float deadline = Time.realtimeSinceStartup + loadingTimeout;
        int stableSupportFrames = 0;
        while (Time.realtimeSinceStartup < deadline)
        {
            MeshCollider[] map = GetLoadedMapColliders();
            if (TryTrackSupport(map, out float support) &&
                Mathf.Abs(support - selectedSupportY) <= .65f)
            {
                if (++stableSupportFrames >= 3) break;
            }
            else stableSupportFrames = 0;
            Status = $"等待原 Cesium 履带支撑地面；当前碰撞瓦片={map.Length}";
            yield return new WaitForSecondsRealtime(.25f);
        }
        if (stableSupportFrames < 3)
        {
            Status = "原 Cesium 履带支撑地面在限定时间内未就绪；设备保持锁定";
            Debug.LogWarning(Status, this);
            ReleaseSurveyCamera();
            yield break;
        }

        try
        {
            cutter.enabled = originalCutter;
            if (resistance != null) resistance.enabled = originalResistance;
            keyboard.fixedBase = originalFixed;
            root.immovable = originalFixed;
            root.useGravity = !originalFixed;
            machine.enabled = originalMachine;
            keyboard.enabled = originalKeyboard;
        }
        catch (Exception exception)
        {
            loaded = false;
            MapSurfacePrepared = false;
            soil.GetComponent<Terrain>().enabled = false;
            soil.SoilCollider.enabled = false;
            cutter.enabled = false;
            if (resistance != null) resistance.enabled = false;
            machine.enabled = false;
            keyboard.enabled = false;
            root.immovable = true;
            Status = "烘焙施工区加载失败，请重新烘焙：" + exception.Message;
            Debug.LogError(Status, this);
        }
        if (!loaded) yield break;
        yield return new WaitForFixedUpdate();
        Physics.SyncTransforms();
        IsReady = true;
        RestoreSceneViewCameraSetting();
        Status = "本地烘焙施工区已就绪";
        Debug.Log($"[ZX200] {Status}；初始土量={bakedSite.initialSoilVolumeM3:F3}m³", this);
    }

    /// <summary>Capture a ready, untouched dynamic work face into a persistent snapshot being assembled by the editor.</summary>
    /// <param name="target">Snapshot with its TerrainData copy already assigned.</param>
    /// <param name="reason">Failure reason when the dynamic site is not safe to bake.</param>
    /// <returns>True after geometry, georeference, equipment layout and soil baseline have been copied.</returns>
    public bool TryCaptureBakedData(BakedConstructionSiteData target, out string reason)
    {
        reason = string.Empty;
        if (siteMode != SitePreparationMode.DynamicDeveloper || !IsReady || target == null || target.terrainData == null ||
            soil == null || !soil.IsPreparedSurfaceUnchanged())
        { reason = "仅能在动态开发模式施工区就绪且土层尚未挖动时烘焙。"; return false; }
        float[,] floor = soil.GetPreparedGroundCopy();
        if (floor == null || preparedSoilMask == null)
        { reason = "缺少基础地面或土带掩码，不能保存不完整的施工区。"; return false; }
        CesiumGeoreference reference = tileset.GetComponentInParent<CesiumGeoreference>();
        if (reference == null) { reason = "缺少 Cesium 地理参考。"; return false; }
        int n = target.terrainData.heightmapResolution;
        if (floor.GetLength(0) != n || floor.GetLength(1) != n ||
            preparedSoilMask.GetLength(0) != n || preparedSoilMask.GetLength(1) != n)
        { reason = "烘焙地形分辨率与已测基础地面不一致。"; return false; }
        target.formatVersion = BakedConstructionSiteData.CurrentVersion;
        target.sourceMapName = mapName;
        target.georeferenceLatitude = reference.latitude;
        target.georeferenceLongitude = reference.longitude;
        target.georeferenceHeight = reference.height;
        target.terrainOrigin = soil.transform.position;
        target.machinePosition = root.transform.position;
        target.machineRotation = root.transform.rotation;
        target.truckPosition = truck.position;
        target.truckRotation = truck.rotation;
        target.workFaceCenter = WorkFaceCenter;
        target.workFaceRotation = WorkFaceRotation;
        target.workFaceSize = WorkFaceSize;
        target.originalSupportY = originalSupportY;
        target.selectedSupportY = selectedSupportY;
        target.selectedTruckGroundY = selectedTruckGroundY;
        target.readyTipClearanceMeters = ReadyTipClearanceMeters;
        target.selectedSurfaceNormalY = SelectedSurfaceNormalY;
        target.maximumCutDepth = soil.maximumCutDepth;
        target.terrainMaterial = soil.GetComponent<Terrain>().materialTemplate;
        target.groundHeights = new float[n * n];
        target.soilMask = new float[n * n];
        float[,] top = target.terrainData.GetHeights(0, 0, n, n);
        double cell = (double)target.terrainData.size.x * target.terrainData.size.y *
            target.terrainData.size.z / ((n - 1) * (n - 1));
        double volume = 0;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int index = z * n + x;
                float mask = preparedSoilMask[z, x];
                float sampledFloor = floor[z, x];
                float surface = top[z, x];
                // TerrainData quantizes heights. Outside the authored band, top and floor describe
                // the same source map surface; make the persistent floor match the read-back top.
                // Inside the band, tolerate only one sample of quantization, never a real inversion.
                if (mask > .001f && sampledFloor - surface > 2f / 65535f)
                {
                    reason = $"采样地面高于土层超过高度图量化误差（x={x}, z={z}）。";
                    return false;
                }
                float storedFloor = mask <= .001f ? surface : Mathf.Min(sampledFloor, surface);
                target.groundHeights[index] = storedFloor;
                target.soilMask[index] = mask;
                volume += (surface - storedFloor) * cell *
                    (x == 0 || x == n - 1 ? .5 : 1) * (z == 0 || z == n - 1 ? .5 : 1);
            }
        target.initialSoilVolumeM3 = volume;
        return target.TryValidate(mapName, reference.latitude, reference.longitude, reference.height, out reason);
    }

    private MeshCollider[] GetLoadedMapColliders()
    {
        return tileset.GetComponentsInChildren<MeshCollider>()
            .Where(c => c != null && c.enabled && c.gameObject.activeInHierarchy).ToArray();
    }

    private static int GetMapRevision(MeshCollider[] map)
    {
        unchecked
        {
            int revision = 17;
            foreach (MeshCollider collider in map)
            {
                revision = revision * 31 + collider.GetEntityId().GetHashCode();
                revision = revision * 31 + (collider.sharedMesh != null
                    ? collider.sharedMesh.GetEntityId().GetHashCode() : 0);
            }
            return revision;
        }
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
        internal float surfaceNormalY;
        internal float readyTipClearance;
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

    private IEnumerator PrepareMound(MeshCollider[] map, float currentSupportY)
    {
        moundPreparationSucceeded = false;
        workFaceCoverageIncomplete = false;
        yield return ChooseWorkFace(map, currentSupportY);
        if (MapChangedSinceSearch())
        {
            workFaceCoverageIncomplete = true;
            Status = "Cesium 瓦片在坡面扫描期间更新；等待稳定后重新扫描。";
            yield break;
        }
        if (!workFaceSearchSucceeded)
        {
            Status = usePrepositionedEquipment
                ? $"预置低台位置没有稳定履带位和可切坡面；设备保持原位。{candidateDiagnostics}"
                : useExplicitWorkFaceAnchor
                ? $"指定锚点没有至少 {minimumBenchDropMeters:F1}m 低差、稳定履带位和可切坡面；设备保持原位。{candidateDiagnostics}"
                : $"搜索范围内没有至少 {minimumBenchDropMeters:F1}m 低差、坡高至少 {minimumWorkFaceRise:F1}m 的可达工作面；设备保持原位。{candidateDiagnostics}";
            yield break;
        }
        WorkFaceCandidate candidate = selectedWorkFace;
        selectedRootPosition = new Vector3(candidate.rootXZ.x, originalRootPosition.y, candidate.rootXZ.y);
        selectedRootRotation = Quaternion.LookRotation(candidate.direction, Vector3.up);
        selectedFaceDirection = candidate.direction;
        selectedSupportY = candidate.supportY;
        selectedTruckGroundY = candidate.truckGroundY;
        selectedFaceRise = candidate.rise;
        SelectedSurfaceNormalY = candidate.surfaceNormalY;
        ReadyTipClearanceMeters = candidate.readyTipClearance;

        soil.Initialize(soil.GetComponent<Terrain>());
        TerrainData data = soil.Terrain.terrainData;
        int n = data.heightmapResolution;
        Vector3 size = new Vector3(replacementAreaSize.x, 1f, replacementAreaSize.y);
        Vector3 patchCenter = selectedRootPosition + selectedRootRotation *
            new Vector3(soilStripCenterLocalX, 0f, soilStripCenterLocalZ);
        Vector3 origin = new Vector3(patchCenter.x - size.x * .5f, 0,
            patchCenter.z - size.z * .5f);
        soil.transform.position = origin;
        data.size = size;

        var baseGround = new float[n, n];
        float[,] stripMask;
        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                Vector3 point = origin + new Vector3(size.x * x / (n - 1f), 0, size.z * z / (n - 1f));
                if (!Sample(map, point, out float y))
                {
                    Status = "所选坡面替换范围超出已加载 Cesium 地图；设备保持原位。";
                    workFaceCoverageIncomplete = true;
                    yield break;
                }
                baseGround[z, x] = y + .008f;
                minimum = Mathf.Min(minimum, baseGround[z, x]);
                maximum = Mathf.Max(maximum, baseGround[z, x]);
            }
            if ((z + 1) % 4 == 0)
            {
                Status = $"采样坡面地面高度：{z + 1}/{n} 行。";
                SurveyFrameYieldCount++;
                yield return null;
                if (MapChangedSinceSearch())
                {
                    workFaceCoverageIncomplete = true;
                    Status = "Cesium 瓦片在地面采样期间更新；等待稳定后重新扫描。";
                    yield break;
                }
            }
        }

        if (!MeasurePreparedSupport(baseGround, origin, size, candidate.rootXZ, candidate.direction,
            out selectedSupportY, out selectedTruckGroundY, out string supportFailure))
        {
            Status = $"{supportFailure}；设备保持原位。";
            yield break;
        }
        stripMask = ConstructionSiteSlopeMask.Build(baseGround, size.z / (n - 1f), selectedSupportY);

        float dx = size.x / (n - 1f), dz = size.z / (n - 1f);
        int normalStepX = Mathf.Clamp(Mathf.RoundToInt(.75f / dx), 1, n / 8);
        int normalStepZ = Mathf.Clamp(Mathf.RoundToInt(.75f / dz), 1, n / 8);
        var heights = new float[n, n];
        var ground = new float[n, n];
        var targetWorldY = new float[n, n];
        int steepBandCells = 0;
        float lowestBandNormalY = 1f;
        for (int z = 0; z < n; z++)
        {
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
                    lowestBandNormalY = Mathf.Min(lowestBandNormalY, normal.y);
                    if (normal.y < minimumSurfaceNormalY) steepBandCells++;
                    // On isolated steep photogrammetry cells, cap vertical buildup rather than
                    // aborting the whole work face or creating a tall soil spike.
                    thickness = soilStripHeight * stripMask[z, x] /
                        Mathf.Max(minimumSurfaceNormalY, normal.y);
                }
                targetWorldY[z, x] = baseGround[z, x] + thickness;
                maximum = Mathf.Max(maximum, targetWorldY[z, x]);
            }
            if ((z + 1) % 8 == 0)
            {
                Status = $"计算贴坡土带高度：{z + 1}/{n} 行。";
                SurveyFrameYieldCount++;
                yield return null;
                if (MapChangedSinceSearch())
                {
                    workFaceCoverageIncomplete = true;
                    Status = "Cesium 瓦片在土带高度计算期间更新；等待稳定后重新扫描。";
                    yield break;
                }
            }
        }
        if (steepBandCells > 0)
            Debug.Log($"施工区土带已对 {steepBandCells} 个陡坡网格限厚；最小法线 Y={lowestBandNormalY:F3}。", this);

        origin.y = minimum - .35f;
        soil.transform.position = origin;
        size.y = maximum - origin.y + .5f;
        data.size = size;
        soil.maximumCutDepth = soilStripHeight + .15f;
        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                ground[z, x] = (baseGround[z, x] - origin.y) / size.y;
                heights[z, x] = (targetWorldY[z, x] - origin.y) / size.y;
            }
            if ((z + 1) % 8 == 0)
            {
                SurveyFrameYieldCount++;
                yield return null;
                if (MapChangedSinceSearch())
                {
                    workFaceCoverageIncomplete = true;
                    Status = "Cesium 瓦片在高度图写入期间更新；等待稳定后重新扫描。";
                    yield break;
                }
            }
        }
        soil.PrepareMoundSurface(heights, ground);
        Vector3 workFaceCenter = WorkFaceCenter;
        Vector2 workFaceSize = new Vector2(soilStripHalfWidth * 2f, soilStripHalfDepth * 2f);
        int holesResolution = data.holesResolution;
        var holes = new bool[holesResolution, holesResolution];
        for (int z = 0; z < holesResolution; z++)
        {
            int sampleZ = Mathf.Clamp(Mathf.RoundToInt((z + .5f) * (n - 1f) / holesResolution), 0, n - 1);
            for (int x = 0; x < holesResolution; x++)
            {
                int sampleX = Mathf.Clamp(Mathf.RoundToInt((x + .5f) * (n - 1f) / holesResolution), 0, n - 1);
                holes[z, x] = stripMask[sampleZ, sampleX] > .05f;
            }
            if ((z + 1) % 8 == 0)
            {
                SurveyFrameYieldCount++;
                yield return null;
                if (MapChangedSinceSearch())
                {
                    workFaceCoverageIncomplete = true;
                    Status = "Cesium 瓦片在碰撞孔洞准备期间更新；等待稳定后重新扫描。";
                    yield break;
                }
            }
        }
        data.SetHoles(0, 0, holes);
        if (MapChangedSinceSearch())
        {
            workFaceCoverageIncomplete = true;
            Status = "Cesium 瓦片在施工区准备期间更新；等待稳定后重新扫描。";
            yield break;
        }
        ConstructionSiteSurfaceAppearance appearance = GetComponent<ConstructionSiteSurfaceAppearance>();
        if (appearance == null) appearance = gameObject.AddComponent<ConstructionSiteSurfaceAppearance>();
        Bounds workFaceBounds = ConstructionSiteFootprint.CalculateBounds(workFaceCenter,
            selectedRootRotation, workFaceSize, origin.y, origin.y + size.y);
        if (!appearance.ApplySlopeBand(tileset.gameObject, soil, workFaceBounds, stripMask))
        {
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Status = "无法捕获坡面范围内的 Cesium 影像；保留原地图和设备位置，请检查图形渲染后重试。";
                Debug.LogWarning(Status, this);
                yield break;
            }
            Debug.Log("无图形设备：跳过仅供自动化仿真的 Cesium 坡面影像采集；交付烘焙必须在图形模式完成。", this);
        }
        preparedSoilMask = (float[,])stripMask.Clone();
        Status = "完整坡面土层已生成；挖掘机与矿车支撑面保留原 Cesium 地图。";
        moundPreparationSucceeded = true;
    }

    private IEnumerator ChooseWorkFace(MeshCollider[] map, float currentSupportY)
    {
        workFaceSearchSucceeded = false;
        float spacing = Mathf.Max(.5f, workFaceSearchSpacing);
        int searchCells = Mathf.CeilToInt(workFaceSearchRadius / spacing);
        float actualRadius = searchCells * spacing;
        float padding = ReplacementSearchPadding();
        int paddingCells = Mathf.CeilToInt(padding / spacing);
        int resolution = searchCells * 2 + paddingCells * 2 + 1;
        Vector3 original = originalRootPosition;
        Vector3 gridOrigin = new Vector3(original.x - (searchCells + paddingCells) * spacing, 0,
            original.z - (searchCells + paddingCells) * spacing);
        var samples = new float[resolution, resolution];
        int validSamples = 0;
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                Vector3 point = gridOrigin + new Vector3(x * spacing, 0, z * spacing);
                samples[z, x] = Sample(map, point, out float y) ? y : float.NaN;
                if (float.IsFinite(samples[z, x])) validSamples++;
            }
            if ((z + 1) % 2 == 0)
            {
                Status = $"扫描 Cesium 坡面候选：{z + 1}/{resolution} 行，已采样 {validSamples} 点。";
                SurveyFrameYieldCount++;
                yield return null;
                if (MapChangedSinceSearch())
                {
                    workFaceCoverageIncomplete = true;
                    Status = "Cesium 瓦片在坡面扫描期间更新；等待稳定后重新扫描。";
                    yield break;
                }
            }
        }

        int candidateStride = usePrepositionedEquipment ? 1 : Mathf.Max(1, Mathf.RoundToInt(2f / spacing));
        int first = paddingCells;
        int last = paddingCells + searchCells * 2;
        float bestScore = float.PositiveInfinity;
        int testedPositions = 0, coveredPositions = 0, validFaces = 0;
        int[] rejected = new int[10];
        int directionCount = usePrepositionedEquipment ? 1 : 16;
        selectedWorkFace = default;
        float bestRejectedClearance = float.NegativeInfinity;
        Vector2 bestRejectedPosition = default;
        int firstX = useExplicitWorkFaceAnchor
            ? Mathf.RoundToInt((workFaceAnchor.x - gridOrigin.x) / spacing) : first;
        int lastX = useExplicitWorkFaceAnchor ? firstX : last;
        int firstZ = useExplicitWorkFaceAnchor
            ? Mathf.RoundToInt((workFaceAnchor.z - gridOrigin.z) / spacing) : first;
        int lastZ = useExplicitWorkFaceAnchor ? firstZ : last;
        for (int z = firstZ; z <= lastZ; z += useExplicitWorkFaceAnchor ? 1 : candidateStride)
        {
            for (int x = firstX; x <= lastX; x += useExplicitWorkFaceAnchor ? 1 : candidateStride)
            {
                Vector2 rootXZ = useExplicitWorkFaceAnchor
                    ? new Vector2(workFaceAnchor.x, workFaceAnchor.z)
                    : new Vector2(gridOrigin.x + x * spacing, gridOrigin.z + z * spacing);
                testedPositions++;
                if (!useExplicitWorkFaceAnchor &&
                    Vector2.Distance(rootXZ, new Vector2(original.x, original.z)) > actualRadius) continue;
                for (int directionIndex = 0; directionIndex < directionCount; directionIndex++)
                {
                    Vector3 direction = usePrepositionedEquipment
                        ? root.transform.forward
                        : Quaternion.Euler(0, directionIndex * (360f / directionCount), 0) * Vector3.forward;
                    if (!HasReplacementCoverage(samples, gridOrigin, spacing, rootXZ, direction)) continue;
                    coveredPositions++;
                    if (!EvaluateWorkFace(samples, gridOrigin, spacing, rootXZ, direction,
                        out float supportY, out float truckY, out float rise, out float supportSpread,
                        out float surfaceNormalY, out int rejectionStage))
                    {
                        rejected[rejectionStage]++;
                        continue;
                    }
                    float benchReferenceY = usePrepositionedEquipment ? originalSupportY : currentSupportY;
                    if (supportY > benchReferenceY - minimumBenchDropMeters)
                    {
                        rejected[8]++;
                        continue;
                    }
                    if (!TryMeasureReadyTipClearance(samples, gridOrigin, spacing, rootXZ,
                        direction, supportY, out float readyTipClearance) ||
                        readyTipClearance < minimumReadyTipClearanceMeters)
                    {
                        if (readyTipClearance > bestRejectedClearance)
                        {
                            bestRejectedClearance = readyTipClearance;
                            bestRejectedPosition = rootXZ;
                        }
                        rejected[9]++;
                        continue;
                    }
                    validFaces++;
                    float distance = Vector2.Distance(rootXZ, new Vector2(original.x, original.z));
                    float benchDrop = benchReferenceY - supportY;
                    float score = Mathf.Abs(rise - 3.2f) + distance * .04f + supportSpread * 2f +
                        (1f - surfaceNormalY) * 4f - benchDrop * .08f -
                        Mathf.Min(readyTipClearance, 1f) * .15f;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    selectedWorkFace = new WorkFaceCandidate
                    {
                        rootXZ = rootXZ,
                        direction = direction,
                        supportY = supportY,
                        truckGroundY = truckY,
                        rise = rise,
                        surfaceNormalY = surfaceNormalY,
                        readyTipClearance = readyTipClearance
                    };
                }
            }
            if (!useExplicitWorkFaceAnchor || z != lastZ)
            {
                SurveyFrameYieldCount++;
                yield return null;
            }
        }
        candidateDiagnostics = $"[坡面搜索采样={validSamples}/{resolution * resolution}，" +
            $"位置={testedPositions}，替换范围完整={coveredPositions}，通过筛选={validFaces}，" +
            $"失败类别(地图/履带不平/矿车地图/矿车不平/坡面地图/坡形/坡度/平台低差/斗尖净空)=" +
            $"{rejected[1]}/{rejected[2]}/{rejected[3]}/{rejected[4]}/{rejected[5]}/{rejected[6]}/{rejected[7]}/{rejected[8]}/{rejected[9]}，" +
            $"被拒最大斗尖净空={bestRejectedClearance:F2}@{bestRejectedPosition}]";
        workFaceCoverageIncomplete = ShouldRetryWorkFaceSearch(
            coveredPositions, validFaces, validSamples, resolution * resolution);
        workFaceSearchSucceeded = float.IsFinite(bestScore);
    }

    private bool MapChangedSinceSearch()
    {
        return GetMapRevision(GetLoadedMapColliders()) != lastSearchMapRevision;
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

    private float ReplacementSearchPadding() =>
        Mathf.Max(replacementAreaSize.x, replacementAreaSize.y) * .5f +
        Mathf.Sqrt(soilStripCenterLocalX * soilStripCenterLocalX +
            soilStripCenterLocalZ * soilStripCenterLocalZ) + 4f;

    private bool HasReplacementCoverage(float[,] samples, Vector3 gridOrigin, float spacing,
        Vector2 rootXZ, Vector3 direction)
    {
        Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
        Vector3 center = new Vector3(rootXZ.x, 0, rootXZ.y) + rotation *
            new Vector3(soilStripCenterLocalX, 0f, soilStripCenterLocalZ);
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
        out float rise, out float supportSpread, out float surfaceNormalY, out int rejectionStage)
    {
        supportY = truckY = rise = supportSpread = 0f;
        surfaceNormalY = 1f;
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

        // Candidate validation concerns the active digging reach near the machine;
        // the authored soil can continue much farther along the same wall.
        float reach = Mathf.Min(soilStripHalfWidth, 12.5f) * .75f;
        float[] lateral = { -reach, 0f, reach };
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
        surfaceNormalY = 1f / Mathf.Sqrt(1f + slopeX * slopeX + slopeZ * slopeZ);
        rejectionStage = 7;
        return surfaceNormalY >= minimumSurfaceNormalY;
    }

    private float SoilStripMask(Vector3 machineLocal)
    {
        float side = 1f - Smooth01((Mathf.Abs(machineLocal.x - soilStripCenterLocalX) -
            (soilStripHalfWidth - .7f)) / .7f);
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

    private bool TryMeasureReadyTipClearance(float[,] samples, Vector3 gridOrigin, float spacing,
        Vector2 rootXZ, Vector3 direction, float supportY, out float clearance)
    {
        clearance = float.PositiveInfinity;
        Quaternion rootRotation = Quaternion.LookRotation(direction, Vector3.up);
        Vector3 rootPosition = new Vector3(rootXZ.x, 0f, rootXZ.y);
        float readyTipHeight = supportY + .025f + (cutter.TipWorldPosition.y - originalTrackBounds.min.y);
        for (int swingStep = -3; swingStep <= 3; swingStep++)
        {
            float swingDegrees = swingStep * 8f;
            Vector3 swungTipOffset = swingPivotOffsetInRoot +
                Quaternion.Euler(0f, swingDegrees, 0f) * (readyTipOffsetInRoot - swingPivotOffsetInRoot);
            Vector3 tipPosition = rootPosition + rootRotation * swungTipOffset;
            tipPosition.y = readyTipHeight;
            if (!GridSample(samples, gridOrigin, spacing, tipPosition, out float groundY))
            {
                clearance = float.NegativeInfinity;
                return false;
            }

            float bandMask = SoilStripMask(swungTipOffset);
            float bandHeight = 0f;
            if (bandMask > .001f)
            {
                const float normalSampleOffset = .75f;
                if (!GridSample(samples, gridOrigin, spacing, tipPosition + Vector3.right * normalSampleOffset, out float rightY) ||
                    !GridSample(samples, gridOrigin, spacing, tipPosition - Vector3.right * normalSampleOffset, out float leftY) ||
                    !GridSample(samples, gridOrigin, spacing, tipPosition + Vector3.forward * normalSampleOffset, out float forwardY) ||
                    !GridSample(samples, gridOrigin, spacing, tipPosition - Vector3.forward * normalSampleOffset, out float backY))
                {
                    clearance = float.NegativeInfinity;
                    return false;
                }
                float slopeX = (rightY - leftY) / (2f * normalSampleOffset);
                float slopeZ = (forwardY - backY) / (2f * normalSampleOffset);
                float normalY = 1f / Mathf.Sqrt(1f + slopeX * slopeX + slopeZ * slopeZ);
                bandHeight = soilStripHeight * bandMask / Mathf.Max(minimumSurfaceNormalY, normalY);
            }
            clearance = Mathf.Min(clearance, tipPosition.y - groundY - bandHeight);
        }
        return float.IsFinite(clearance);
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
        {
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
            Bounds bounds = collider.bounds;
            if (point.x < bounds.min.x || point.x > bounds.max.x ||
                point.z < bounds.min.z || point.z > bounds.max.z ||
                bounds.min.y > 100f || bounds.max.y < -100f) continue;
            if (collider.Raycast(new Ray(new Vector3(point.x, 100f, point.z), Vector3.down), out var hit, 200f))
                y = Mathf.Max(y, hit.point.y);
        }
        return float.IsFinite(y);
    }
}
