using System.Collections.Generic;
using UnityEngine;

/// <summary>读取真实铲斗刃口姿态执行局部切削，并保留守恒的斗内体积。</summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class BucketSoilCutter : MonoBehaviour
{
    public DeformableSoilTerrain soil;
    public Transform tipMarker;
    public Vector3 tipLocalPosition = new Vector3(0, .82f, 1.25f);
    public Vector3 cutDirectionLocal = new Vector3(0, -1.25f, .82f);
    [HideInInspector] public float cutRadius = .5f; // Legacy serialized field; cutting uses the tooth box.
    [Min(.01f)] public float innerMouthHalfWidth = .43f;
    [Range(0f, 1f)] public float maximumSidewaysFraction = .7f;
    [Min(.001f)] public float capacityM3 = .8f;
    [Min(1f)] public float densityKgPerM3 = 1600f;
    [Min(.001f)] public float minimumTravel = .015f;
    [Min(.01f)] public float maximumTravelPerSample = .5f;
    [Range(-1f, 1f)] public float minimumDirectionDot = .1f;
    [Min(0f)] public float allowedUpwardTravel = .002f;
    [Header("Physical tooth-edge proxy (bucket local)")]
    [Tooltip("Offset behind the ZX200 tooth marker, calibrated from the imported mesh.")]
    public Vector3 bladeCenterOffset = new Vector3(0, .025f, -.016f);
    public Vector3 bladeForwardLocal = new Vector3(0, -1.25f, .82f);
    [Min(.001f)] public float bladeThickness = .03f;
    [Min(.001f)] public float bladeLength = .06f;
    [Min(.001f)] public float bladeHalfWidth = .5f;
    public bool showCuttingGeometry;
    public Transform fillVisual;
    public bool showStatus = true;
    public double VolumeM3 { get; private set; }
    public double SoilDensityKgPerM3 => capturedDensity > 0 ? capturedDensity : densityKgPerM3;
    public double MassKg => VolumeM3 * SoilDensityKgPerM3;
    public double DeliveredVolumeM3 { get; private set; }
    public double DeliveredMassKg { get; private set; }
    public double EmittedVolumeM3 { get; private set; }
    public double ExcavatedVolumeM3 { get; private set; }
    public double CollectedVolumeM3 { get; private set; }
    public double DisplacedVolumeM3 { get; private set; }
    public float CollectionEfficiency { get; private set; }
    private Vector3[] previousBlade;
    private readonly List<SoilFlightTransport.Emission> displacedParcels = new List<SoilFlightTransport.Emission>();
    public IReadOnlyList<SoilLandingRecord> Landings => landings.AsReadOnly();
    private readonly List<SoilLandingRecord> landings = new List<SoilLandingRecord>();

    /// <summary>One actual landing, including receiver, terrain or ground-pile destination.</summary>
    public sealed class SoilLandingRecord
    {
        public double TimeSeconds { get; internal set; }
        public Component Destination { get; internal set; }
        public double VolumeM3 { get; internal set; }
        public double MassKg { get; internal set; }
    }
    private double capturedDensity;
    /// <summary>Capacity limited by both the configured bucket and its calibrated display cavity, in m³.</summary>
    public double EffectiveCapacityM3 => fill != null ? System.Math.Min(capacityM3, fill.CapacityM3) : capacityM3;
    public Vector3 TipWorldPosition => tipMarker != null ? tipMarker.position : transform.TransformPoint(tipLocalPosition);
    public string Status { get; private set; } = "等待刃口运动";
    private bool hasPrevious;
    private bool startupReady;
    /// <summary>Whether cutting is unlocked by a settled work-ready pose or explicit manual input at a ready site.</summary>
    public bool StartupReady => startupReady;
    private Zx200DigController machine;
    private AutonomousExcavationController automatic;
    /// <summary>True only when neither an automatic joint move nor a complete loading job owns this bucket.</summary>
    internal bool ManualSoilControlActive
    {
        get
        {
            if (machine == null) machine = GetComponentInParent<Zx200DigController>();
            if (machine == null) return true;
            if (automatic == null)
                foreach (var candidate in FindObjectsByType<AutonomousExcavationController>(FindObjectsSortMode.None))
                    if (candidate.machine == machine && candidate.gameObject.scene == gameObject.scene)
                    { automatic = candidate; break; }
            return !machine.IsBusy && (automatic == null || !automatic.IsRunning);
        }
    }
    private uint manualRevisionAtEnable;
    private Vector3 previous;
    private Vector3 lastObserved;
    private TerrainCollider ignoredTerrain;
    private BucketSoilFill fill;
    private bool cuttingSuppressed;
    private readonly List<Collider> ignored = new List<Collider>();
    private readonly List<bool> originalIgnore = new List<bool>();

    private void FixedUpdate()
    {
        if (Application.isPlaying) SamplePose();
    }
    private void OnEnable()
    {
        hasPrevious = false;
        machine = GetComponentInParent<Zx200DigController>();
        manualRevisionAtEnable = machine != null ? machine.ManualPoseRevision : 0;
        if (machine == null || IsMachineReady()) startupReady = true;
    }
    private void Update()
    {
        if (Application.isPlaying && Input.GetKeyDown(KeyCode.Backspace)) ResetExcavation();
    }

    /// <summary>采样真实刃口并切削；手动切削预算独立于斗容量，超出收集容量的土守恒侧落。</summary>
    /// <returns>本次实际进入斗内的m³。</returns>
    public double SamplePose()
    {
        PrepareFill();
        if (soil == null) { RestoreCollisionPairs(); hasPrevious = false; return 0; }
        soil.Initialize(soil.GetComponent<Terrain>());
        ConfigureCollisionPairs();
        if (cuttingSuppressed)
        {
            hasPrevious = false;
            previousBlade = null;
            Status = "切削暂停，保留土面碰撞过滤";
            UpdateFill();
            return 0;
        }
        // Automatic initialization keeps its settled-pose gate. A manual operator
        // may start from the compact parked pose once site positioning has finished.
        if (!startupReady)
        {
            // WebDelivery depends on ExcavatorControls; avoid a circular assembly
            // dependency when querying the optional construction-site component.
            Component site = soil.transform.parent != null
                ? soil.transform.parent.GetComponent("CesiumMoundSite") : null;
            bool siteReady = site == null ||
                site.GetType().GetProperty("IsReady")?.GetValue(site) is true;
            bool manualWorkStarted = machine != null && siteReady &&
                machine.ManualPoseRevision != manualRevisionAtEnable;
            if (!IsMachineReady() && !manualWorkStarted)
            {
                hasPrevious = false;
                Status = "等待设备初始姿态稳定";
                return 0;
            }
            startupReady = true;
            // Positioning travel is not excavation: begin a fresh measured sweep.
            hasPrevious = false;
            previousBlade = null;
        }
        Vector3 current = TipWorldPosition;
        Vector3[] corners = GetBladeWorldCorners();
        Vector3 bladeCenter = (corners[0] + corners[7]) * .5f;
        if (!hasPrevious)
        {
            previous = current; lastObserved = bladeCenter; previousBlade = corners;
            hasPrevious = true; UpdateFill(); return 0;
        }
        // A curling edge has real travel even when its tooth marker barely translates.
        // Measure the steel proxy's center rather than rejecting every fixed-tip rotation.
        Vector3 movement = bladeCenter - (previousBlade[0] + previousBlade[7]) * .5f;
        Vector3 frameMovement = bladeCenter - lastObserved;
        lastObserved = bladeCenter;
        float travel = (current - previous).magnitude;
        for (int i = 0; i < 8; i++) travel = Mathf.Max(travel, (corners[i] - previousBlade[i]).magnitude);
        Status = "等待刃口有效行程";
        if (travel > maximumTravelPerSample || current.y < soil.GetFloorWorldY(current))
        {
            RememberBlade(current, corners);
            Status = travel > maximumTravelPerSample ? "姿态跳变：不补挖" : "到达土层底部";
            return 0;
        }
        float jitterBudget = Mathf.Min(.002f, minimumTravel * .15f);
        // Reject material back/side scraping immediately, but allow a genuinely downward tooth sweep.
        if (frameMovement.magnitude > jitterBudget && !CanCutMovement(frameMovement, out string frameReason))
        { RememberBlade(current, corners); Status = frameReason; return 0; }
        if (travel < minimumTravel) return 0;
        if (!CanCutMovement(movement, out string reason))
        { RememberBlade(current, corners); Status = reason; return 0; }
        Vector3[] from = previousBlade;
        RememberBlade(current, corners);
        double available = System.Math.Max(0, EffectiveCapacityM3 - VolumeM3);
        // Capacity limits collection, never the soil a moving blade can displace.
        // The autonomous planner decides when to curl/lift; it must not create a capacity wall.
        float cutBudget = 2 * bladeHalfWidth * Mathf.Abs(transform.lossyScale.x) * travel * soil.maximumCutDepth;
        if (cutBudget <= 1e-5f) { Status = "斗满：请停止切削"; return 0; }

        // Geometry determines excavation. Relative inflow determines collection; gravity retention
        // remains the unloader's independent job. A vertical tooth sweep can cut without filling.
        double removed = soil.CutBladeSweep(from, corners, cutBudget);
        if (removed < cutBudget)
        {
            foreach (var pile in SoilReceivingZone.ActiveZones)
                if (pile.gameObject.scene == gameObject.scene)
                    removed += pile.ExcavateLoose(from, corners, cutBudget - removed);
        }
        CollectionEfficiency = EstimateCollectionEfficiency(frameMovement.magnitude > jitterBudget ? frameMovement : movement);
        double collected = System.Math.Min(available, removed * CollectionEfficiency);
        double displaced = removed - collected;
        if (removed > 0 && capturedDensity <= 0) capturedDensity = Mathf.Max(1, densityKgPerM3);
        VolumeM3 += collected;
        CollectedVolumeM3 += collected;
        ExcavatedVolumeM3 += removed;
        DisplacedVolumeM3 += displaced;
        if (displaced > 0) ReleaseDisplacedSoil(displaced, current);
        Status = removed > 0 ? $"刃口切削：进斗 {CollectionEfficiency:P0} / 其余侧落" : "刃口无新土 / 低于地形分辨率";
        UpdateFill();
        return collected;
    }

    private void RememberBlade(Vector3 position, Vector3[] corners)
    { previous = position; previousBlade = corners; }

    /// <summary>Return the calibrated cutting-edge box in world space, ordered by X/Y/Z side bits.</summary>
    /// <returns>Eight corners used by Terrain sweeping and the geometry overlay.</returns>
    public Vector3[] GetBladeWorldCorners()
    {
        // A zero-offset synthetic tool has no imported ZX200 geometry; use its explicit mouth axis.
        bool generic = tipMarker == null && tipLocalPosition.sqrMagnitude < 1e-8f;
        Vector3 forward = (generic ? Vector3.forward : bladeForwardLocal).normalized;
        if (forward.sqrMagnitude < .5f) forward = Vector3.forward;
        Vector3 width = Vector3.right;
        Vector3 thickness = Vector3.Cross(forward, width).normalized;
        Vector3 center = transform.InverseTransformPoint(TipWorldPosition) +
            (generic ? -forward * bladeLength * .5f : bladeCenterOffset);
        float halfWidth = generic ? innerMouthHalfWidth : bladeHalfWidth;
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = transform.TransformPoint(center + width * ((i & 1) == 0 ? -halfWidth : halfWidth) +
                thickness * ((i & 2) == 0 ? -bladeThickness * .5f : bladeThickness * .5f) +
                forward * ((i & 4) == 0 ? -bladeLength * .5f : bladeLength * .5f));
        return corners;
    }

    /// <summary>Check steel-edge movement, without imposing a horizontal intake or mouth-up angle gate.</summary>
    /// <param name="worldMovement">Actual or predicted world blade displacement.</param>
    /// <param name="reason">Specific rejection or acceptance description.</param>
    /// <returns>True for downward entry or inward cutting, including rising scoop arcs; false for lift-only, back or lateral scraping.</returns>
    public bool CanCutMovement(Vector3 worldMovement, out string reason)
    {
        reason = "有效刃口切削";
        if (!float.IsFinite(worldMovement.sqrMagnitude) || worldMovement.sqrMagnitude < 1e-12f)
        { reason = "刃口未移动"; return false; }
        Vector3 direction = worldMovement.normalized;
        if (Mathf.Abs(Vector3.Dot(direction, transform.right)) > maximumSidewaysFraction)
        { reason = "侧向擦土：请沿刃口切入"; return false; }
        float inward = Vector3.Dot(direction, transform.TransformDirection(cutDirectionLocal).normalized);
        // Retracting/curling through soil often raises the edge. Only a predominantly
        // vertical withdrawal without a meaningful inward component is a lift-only move.
        float horizontalFraction = new Vector2(direction.x, direction.z).magnitude;
        if (worldMovement.y > allowedUpwardTravel && direction.y > .35f &&
            (inward < .25f || horizontalFraction < .49f))
        { reason = "抬升退出：不补挖"; return false; }
        if (direction.y < -.2f) return true;
        if (inward < -.1f)
        { reason = "斗背回擦：不切削"; return false; }
        return true;
    }

    /// <summary>Estimate a continuous collected fraction from soil motion into the mouth, independently of cutting.</summary>
    /// <param name="worldMovement">Blade displacement relative to stationary soil.</param>
    /// <returns>Fraction from zero to one; tangential entry displaces soil, aligned entry collects it.</returns>
    public float EstimateCollectionEfficiency(Vector3 worldMovement)
    {
        var unloader = GetComponent<BucketSoilUnloader>();
        Vector3 mouth = transform.TransformDirection(unloader != null ? unloader.openingNormalLocal : cutDirectionLocal).normalized;
        float inflow = Mathf.Clamp01(Vector3.Dot(worldMovement.normalized, mouth) / .5f);
        return inflow * inflow * (3 - 2 * inflow);
    }

    /// <summary>Compatibility query for whether some soil can enter; this does not gate Terrain cutting.</summary>
    /// <param name="worldMovement">Blade displacement in world metres.</param>
    /// <param name="reason">Collection or displacement description.</param>
    /// <returns>True when movement can cut and has a positive mouth inflow fraction.</returns>
    public bool CanCollectMovement(Vector3 worldMovement, out string reason)
    {
        if (!CanCutMovement(worldMovement, out reason)) return false;
        bool collects = EstimateCollectionEfficiency(worldMovement) > .001f;
        reason = collects ? "土穿过斗口" : "切土侧落：尚未进入斗口";
        return collects;
    }

    /// <summary>Yield a shallow patch under an actively pressing manual shell; displaced soil never becomes bucket intake.</summary>
    /// <param name="contact">Measured shell contact in world metres, not a predicted remote point.</param>
    /// <param name="budgetM3">Maximum real displaced volume for this pressure step.</param>
    /// <returns>Read-back Terrain volume displaced and routed to the existing soil transport ledger.</returns>
    public double DisplacePressedSoil(Vector3 contact, float budgetM3)
    {
        if (soil == null || !startupReady || cuttingSuppressed || !float.IsFinite(budgetM3) || budgetM3 <= 0 ||
            !soil.HasSurfaceAt(contact)) return 0;
        soil.Initialize(soil.GetComponent<Terrain>());
        float surface = soil.Terrain.SampleHeight(contact) + soil.transform.position.y;
        if (contact.y >= surface - .015f) return 0;
        Vector3 target = contact;
        target.y = Mathf.Max(contact.y, surface - .04f);
        var data = soil.Terrain.terrainData;
        float cell = Mathf.Max(data.size.x, data.size.z) / (data.heightmapResolution - 1);
        double removed = soil.CutSweep(target, target, Mathf.Clamp(cell * .75f, .1f, .2f), budgetM3);
        if (removed <= 0) return 0;
        if (capturedDensity <= 0) capturedDensity = Mathf.Max(1, densityKgPerM3);
        ExcavatedVolumeM3 += removed;
        DisplacedVolumeM3 += removed;
        ReleaseDisplacedSoil(removed, contact);
        Status = "斗壳压土：松土让位并侧落";
        return removed;
    }

    private void ReleaseDisplacedSoil(double volume, Vector3 tip)
    {
        var flight = GetComponent<SoilFlightTransport>();
        if (flight == null) flight = gameObject.AddComponent<SoilFlightTransport>();
        displacedParcels.Clear();
        // Buried cuts displace spoil to the surface beside the cutting edge. Once the
        // edge is exposed, preserve its outlet height so overflow falls from the bucket.
        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 position = tip + transform.right * (side * (bladeHalfWidth * Mathf.Abs(transform.lossyScale.x) + .08f));
            float surface = soil.Terrain.SampleHeight(position) + soil.transform.position.y;
            position.y = Mathf.Max(position.y, surface + .03f);
            displacedParcels.Add(new SoilFlightTransport.Emission(volume * .5, position,
                transform.right * (side * .12f)));
        }
        EmittedVolumeM3 += volume;
        flight.EmitBatch(displacedParcels);
    }

    /// <summary>Transfer no more than the bucket holds or the receiver accepts; spatial gating belongs to the unloader.</summary>
    /// <param name="receiver">Destination receiving bed.</param>
    /// <param name="requestedM3">Finite positive requested volume in cubic metres.</param>
    /// <returns>Actual accepted and debited volume; zero for invalid requests.</returns>
    public double TransferTo(SoilReceivingZone receiver, double requestedM3)
    {
        if (receiver == null || double.IsNaN(requestedM3) || double.IsInfinity(requestedM3) || requestedM3 <= 0) return 0;
        double moved = receiver.Accept(this, System.Math.Min(VolumeM3, requestedM3));
        VolumeM3 -= moved;
        EmittedVolumeM3 += moved;
        RecordLanding(receiver, moved);
        if (moved > 0) UpdateFill();
        return moved;
    }

    internal double ReleaseToFlight(double requestedM3)
    {
        if (!double.IsFinite(requestedM3) || requestedM3 <= 0) return 0;
        double released = System.Math.Min(VolumeM3, requestedM3);
        VolumeM3 -= released;
        EmittedVolumeM3 += released;
        if (released > 0) UpdateFill();
        return released;
    }

    internal void RecordLanding(Component destination, double volume)
    {
        if (volume <= 0) return;
        DeliveredVolumeM3 += volume;
        DeliveredMassKg += volume * SoilDensityKgPerM3;
        landings.Add(new SoilLandingRecord { TimeSeconds = Time.timeAsDouble, Destination = destination,
            VolumeM3 = volume, MassKg = volume * SoilDensityKgPerM3 });
    }

    /// <summary>Restore this terrain, all buckets using it, and only its receiver contributions.</summary>
    public void ResetExcavation()
    {
        if (soil != null) soil.ResetSoil();
        foreach (var receiver in FindObjectsByType<SoilReceivingZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            receiver.RemoveSource(soil, this);
        foreach (var bucket in FindObjectsByType<BucketSoilCutter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (bucket == this || (soil != null && bucket.soil == soil)) bucket.ResetInventory();
    }

    private void ResetInventory()
    {
        VolumeM3 = 0;
        DeliveredVolumeM3 = 0;
        DeliveredMassKg = 0;
        EmittedVolumeM3 = 0;
        ExcavatedVolumeM3 = 0;
        CollectedVolumeM3 = 0;
        DisplacedVolumeM3 = 0;
        CollectionEfficiency = 0;
        landings.Clear();
        capturedDensity = 0;
        var flight = GetComponent<SoilFlightTransport>();
        if (flight != null) flight.Clear();
        var unloader = GetComponent<BucketSoilUnloader>();
        if (unloader != null) unloader.ResetGate();
        hasPrevious = false;
        Status = "土区与斗量已复位";
        UpdateFill();
    }

    private bool IsMachineReady()
    {
        if (machine == null) return true;
        // The heavy arm can settle a few degrees off its drive target under gravity;
        // requiring the motion-controller's tight waypoint tolerance deadlocks cutting.
        if (!machine.IsPoseWithinTolerance(machine.workReadyPose,
            Mathf.Max(machine.jointToleranceDegrees, 4f))) return false;
        return IsJointSettled(machine.swingJoint) && IsJointSettled(machine.boomJoint) &&
               IsJointSettled(machine.armJoint) && IsJointSettled(machine.bucketJoint);
    }

    private static bool IsJointSettled(ArticulationBody joint)
    {
        return joint != null && joint.jointVelocity.dofCount > 0 && Mathf.Abs(joint.jointVelocity[0]) < 0.05f;
    }

    private void ConfigureCollisionPairs()
    {
        TerrainCollider target = soil.SoilCollider;
        if (ignoredTerrain == target) return;
        RestoreCollisionPairs();
        ignoredTerrain = target;
        ArticulationBody owner = GetComponent<ArticulationBody>();
        foreach (Collider collider in GetComponentsInChildren<Collider>())
        {
            if (collider == target || collider.isTrigger || (owner != null && collider.attachedArticulationBody != owner)) continue;
            ignored.Add(collider);
            originalIgnore.Add(Physics.GetIgnoreCollision(collider, target));
            Physics.IgnoreCollision(collider, target, true);
        }
    }

    /// <summary>Whether automatic soil removal is suppressed while bucket/map collision filtering stays active.</summary>
    public bool IsCuttingSuppressed => cuttingSuppressed;

    /// <summary>Pause or resume soil accounting without disabling the bucket's non-penetrating TerrainCollider pairs.</summary>
    /// <param name="suppressed">True during transfer motions; false when normal measured cutting may resume.</param>
    public void SetCuttingSuppressed(bool suppressed)
    {
        cuttingSuppressed = suppressed;
        hasPrevious = false;
        previousBlade = null;
        if (suppressed) Status = "切削暂停，保留土面碰撞过滤";
    }

    private void RestoreCollisionPairs()
    {
        for (int i = 0; i < ignored.Count; i++)
            if (ignored[i] != null && ignoredTerrain != null)
                Physics.IgnoreCollision(ignored[i], ignoredTerrain, originalIgnore[i]);
        ignored.Clear(); originalIgnore.Clear(); ignoredTerrain = null;
    }

    private void OnDisable() { RestoreCollisionPairs(); hasPrevious = false; }

    private void UpdateFill()
    {
        PrepareFill();
        if (fillVisual == null) return;
        fillVisual.gameObject.SetActive(VolumeM3 > 1e-5);
        fill.SetVolume(VolumeM3);
    }

    private void PrepareFill()
    {
        if (fillVisual == null) { fill = null; return; }
        if (fill != null && fill.transform == fillVisual) return;
        fill = fillVisual.GetComponent<BucketSoilFill>();
        if (fill == null) fill = fillVisual.gameObject.AddComponent<BucketSoilFill>();
        // Migrate the old offset/scaled display cube into bucket-local coordinates.
        fillVisual.SetParent(transform, false);
        fillVisual.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        fillVisual.localScale = Vector3.one;
        fill.SetVolume(VolumeM3);
    }

    private void OnGUI()
    {
        if (!Application.isPlaying || !showStatus || soil == null) return;
        GUI.Box(new Rect(12, 12, 600, 235), "ZX200 挖掘作业");
        GUI.Label(new Rect(24, 38, 575, 25), $"斗内 {VolumeM3:F3} / {EffectiveCapacityM3:F2} m³  |  估算土质量 {MassKg:F0} kg");
        GUI.Label(new Rect(24, 63, 575, 25), "Q/E 回转  R/F 动臂  T/G 斗杆  Y/H 铲斗  |  Backspace 作业复位");
        var unloader = GetComponent<BucketSoilUnloader>();
        GUI.Label(new Rect(24, 88, 575, 25), $"已落地 {DeliveredVolumeM3:F3} m³ / {DeliveredMassKg:F0} kg  |  {(unloader != null ? unloader.Status : Status)}");
        GUI.Label(new Rect(24, 113, 575, 25), $"累计切出 {ExcavatedVolumeM3:F3} m³  |  下落/待沉积 {(unloader != null ? unloader.AirborneVolumeM3 + unloader.SurfacePendingVolumeM3 : 0):F3} m³");
        if (unloader != null && unloader.Target != null)
            GUI.Label(new Rect(24, 138, 575, 25), $"最近接收区 {unloader.Target.VolumeM3:F3} / {unloader.Target.EffectiveCapacityM3:F2} m³  |  {unloader.Target.MassKg:F0} kg");
        GUI.Label(new Rect(24, 188, 575, 25), $"切削：{Status}");
        GUI.Label(new Rect(24, 213, 575, 25), $"累计进斗 {CollectedVolumeM3:F3} m³  |  切削侧落 {DisplacedVolumeM3:F3} m³");
        var resistance = GetComponent<BucketSoilResistance>();
        if (resistance != null)
            GUI.Label(new Rect(24, 163, 575, 25), $"切入 {resistance.PenetrationM:F3} m  |  阻力 {resistance.ResistanceForceN / 1000:F1} kN  |  {resistance.BlockReason}");
    }

    private void OnDrawGizmosSelected() => DrawCuttingGeometry();
    private void OnDrawGizmos() { if (showCuttingGeometry) DrawCuttingGeometry(); }

    private void DrawCuttingGeometry()
    {
        Vector3[] corners = GetBladeWorldCorners();
        Gizmos.color = Color.cyan;
        for (int i = 0; i < 8; i++)
            for (int bit = 1; bit <= 4; bit *= 2)
                if ((i & bit) == 0) Gizmos.DrawLine(corners[i], corners[i | bit]);
        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(TipWorldPosition, .015f);
        Gizmos.DrawLine(TipWorldPosition, TipWorldPosition + transform.TransformDirection(cutDirectionLocal).normalized * .3f);
    }
}
