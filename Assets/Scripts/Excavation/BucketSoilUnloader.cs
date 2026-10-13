using System.Collections.Generic;
using UnityEngine;

/// <summary>Pose determines retention and emission; falling parcels determine the destination.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BucketSoilCutter))]
public sealed class BucketSoilUnloader : MonoBehaviour
{
    public enum UnloadingState { Empty, Loaded, ReadyToDump, Dumping }
    public SoilReceivingZone[] receivers = new SoilReceivingZone[0];
    public Vector3 openingNormalLocal = new Vector3(0, -1.25f, .82f);
    [Range(0, 89)] public float enterAngleFromDown = 65;
    [Range(0, 89)] public float exitAngleFromDown = 78;
    [Range(0, 45)] public float reposeAngle = 30;
    [Min(0)] public float dwellSeconds = .25f;
    [Min(.001f)] public float flowM3PerSecond = .24f;
    [HideInInspector] public float maximumDrop = 5; // Legacy scene field; flight has no drop cutoff.
    [Min(.01f)] public float surfaceResponseSeconds = .2f;
    public LayerMask obstructionMask = ~0;
    public UnloadingState State { get; private set; }
    /// <summary>Most recent receiving bed that accepted a parcel, for status display.</summary>
    public SoilReceivingZone Target => flight != null ? flight.LastReceiver : null;
    public string Status { get; private set; } = "空斗";
    public double AirborneVolumeM3 => flight != null ? flight.AirborneVolumeM3 : 0;
    public double AirborneMassKg => AirborneVolumeM3 * (cutter != null ? cutter.SoilDensityKgPerM3 : 0);
    public double SurfacePendingVolumeM3 => flight != null ? flight.SurfacePendingVolumeM3 : 0;
    public double RetainedVolumeM3 { get; private set; }
    [HideInInspector] public LineRenderer soilStream; // Replaced by independent airborne clumps.
    private BucketSoilCutter cutter;
    private SoilFlightTransport flight;
    private float dwell;
    private bool angleLatched;
    private int emissionSequence;
    private bool flowPaused;
    private readonly List<Outlet> openOutlets = new List<Outlet>(5);
    private readonly List<SoilFlightTransport.Emission> emissions = new List<SoilFlightTransport.Emission>(5);

    private readonly struct Outlet
    {
        internal readonly Vector3 position;
        internal readonly float weight;

        internal Outlet(Vector3 position, float weight)
        {
            this.position = position;
            this.weight = weight;
        }
    }

    private void Awake() => Prepare();
    private void FixedUpdate() => Step(Time.fixedDeltaTime);
    private void OnDisable() => ResetGate();

    /// <summary>Advance airborne soil and emit according to actual pose; the fill owns surface orientation.</summary>
    /// <param name="deltaSeconds">Finite elapsed seconds, greater than zero and at most one.</param>
    /// <returns>Newly released cubic metres, not the amount landed this frame.</returns>
    public double Step(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0 || deltaSeconds > 1) return 0;
        Prepare();
        if (soilStream != null) soilStream.enabled = false;
        flight.Advance(deltaSeconds, receivers, obstructionMask);
        if (Physics.gravity.sqrMagnitude < 1e-8f || openingNormalLocal.sqrMagnitude < 1e-8f ||
            Vector3.Dot(Physics.gravity.normalized, Vector3.down) < .9999f)
        { ResetGate(); Status = "重力或斗口配置无效"; return 0; }
        if (cutter.VolumeM3 <= 0)
        { ResetGate(); State = UnloadingState.Empty; Status = AirborneVolumeM3 > 0 ? "空斗，土方下落中" : "空斗"; return 0; }
        float angle = Vector3.Angle(transform.TransformDirection(openingNormalLocal), Physics.gravity);
        angleLatched = angle <= (angleLatched ? Mathf.Max(enterAngleFromDown, exitAngleFromDown) : enterAngleFromDown);
        // A carried load stays in the bucket until the operator deliberately tips past the
        // dump threshold. The geometric free surface alone must not shed soil on a mild tilt.
        RetainedVolumeM3 = angleLatched ? 0 : cutter.VolumeM3;
        double excess = System.Math.Max(0, cutter.VolumeM3 - RetainedVolumeM3);
        if (excess <= 1e-7)
        { dwell = 0; State = UnloadingState.Loaded; Status = "斗内保土"; return 0; }
        State = UnloadingState.ReadyToDump;
        Status = "土体滑向斗口";
        float before = dwell;
        dwell += deltaSeconds;
        float flowingTime = Mathf.Max(0, dwell - Mathf.Max(before, dwellSeconds));
        if (flowingTime <= 0) return 0;
        // A shallow tilt leaks slowly; near a downward mouth gravity clears the bucket quickly.
        float dumpProgress = Mathf.InverseLerp(enterAngleFromDown, 0f, angle);
        float tilt = angleLatched ? Mathf.Lerp(.55f, 1.8f, dumpProgress) : .10f;
        float fullness = Mathf.Clamp01((float)(cutter.VolumeM3 / System.Math.Max(.001, cutter.EffectiveCapacityM3)));
        double requested = System.Math.Min(excess, flowingTime * Mathf.Max(0, flowM3PerSecond) * tilt * Mathf.Lerp(.3f, 1, fullness));
        if (cutter.VolumeM3 < .0001 && angleLatched) requested = cutter.VolumeM3;
        double emitted = EmitAcrossMouth(requested);
        if (emitted <= 0)
        {
            if (flowPaused)
            {
                State = UnloadingState.ReadyToDump;
                Status = "余土间歇下落";
                return 0;
            }
            dwell = 0;
            State = UnloadingState.Loaded;
            Status = "斗口被实体遮挡";
            return 0;
        }
        State = cutter.VolumeM3 <= 0 ? UnloadingState.Empty : UnloadingState.Dumping;
        Status = angleLatched ? "正在卸土" : "倾斜撒土";
        return emitted;
    }

    private double EmitAcrossMouth(double requested)
    {
        flowPaused = false;
        if (requested <= 0) return 0;
        float tailReference = Mathf.Max(.001f, Mathf.Min(.01f, (float)cutter.EffectiveCapacityM3 * .05f));
        float tailRatio = Mathf.Clamp01((float)cutter.VolumeM3 / tailReference);
        if (tailRatio < .25f && (emissionSequence & 1) != 0)
        {
            emissionSequence++;
            flowPaused = true;
            return 0;
        }
        int outletCount = Mathf.RoundToInt(Mathf.Lerp(3, 5, tailRatio));
        float halfWidth = cutter.innerMouthHalfWidth * Mathf.Abs(transform.lossyScale.x);
        Vector3 lateral = transform.right.normalized;
        Vector3 velocityDirection = transform.TransformDirection(openingNormalLocal).normalized;
        float lowSide = Mathf.Clamp(Vector3.Dot(lateral, Physics.gravity.normalized), -.75f, .75f);
        float widthFraction = Mathf.Lerp(.3f, 1, tailRatio);
        openOutlets.Clear();
        float openWeight = 0;
        float allWeight = 0;
        for (int i = 0; i < outletCount; i++)
        {
            float basePosition = -1 + 2f * i / (outletCount - 1);
            float positionJitter = DeterministicWave(i, emissionSequence, 1.618034f) * .07f;
            float normalized = Mathf.Clamp(basePosition + positionJitter, -1, 1);
            float weightVariation = 1 + DeterministicWave(i, emissionSequence, 2.414214f) * .08f;
            float weight = Mathf.Max(.1f, (1 + normalized * lowSide * 1.25f) * weightVariation);
            allWeight += weight;
            Vector3 position = cutter.TipWorldPosition + lateral * (normalized * halfWidth * widthFraction);
            if (IsOutletBlocked(position)) continue;
            openOutlets.Add(new Outlet(position, weight));
            openWeight += weight;
        }
        if (openOutlets.Count == 0 || allWeight <= 0) return 0;

        double availableRequest = requested * openWeight / allWeight;
        if (cutter.VolumeM3 < .000001)
            availableRequest = cutter.VolumeM3;
        else if (tailRatio < .25f)
            availableRequest = System.Math.Min(availableRequest, cutter.VolumeM3 * .6);
        double released = cutter.ReleaseToFlight(availableRequest);
        if (released <= 0) return 0;
        emissions.Clear();
        double assigned = 0;
        for (int i = 0; i < openOutlets.Count; i++)
        {
            Outlet outlet = openOutlets[i];
            double volume = i == openOutlets.Count - 1 ? released - assigned : released * outlet.weight / openWeight;
            assigned += volume;
            float speedVariation = 1 + .08f * DeterministicWave(i, emissionSequence, 2.399963f);
            float lateralVariation = DeterministicWave(i, emissionSequence, 4.236068f);
            Vector3 velocity = velocityDirection * (.15f * speedVariation) + lateral * (lateralVariation * .025f);
            emissions.Add(new SoilFlightTransport.Emission(volume, outlet.position, velocity));
        }
        emissionSequence++;
        flight.EmitBatch(emissions);
        return released;
    }

    private bool IsOutletBlocked(Vector3 position)
    {
        foreach (Collider collider in Physics.OverlapSphere(position, .025f, obstructionMask, QueryTriggerInteraction.Ignore))
            if (!collider.transform.IsChildOf(transform)) return true;
        return false;
    }

    private static float DeterministicWave(int outletIndex, int sequence, float phase)
    {
        return Mathf.Sin((outletIndex + 1) * phase + sequence * 1.324718f);
    }

    internal void ResetGate()
    {
        dwell = 0;
        angleLatched = false;
        emissionSequence = 0;
        flowPaused = false;
        State = cutter != null && cutter.VolumeM3 > 0 ? UnloadingState.Loaded : UnloadingState.Empty;
        Status = State == UnloadingState.Empty ? "空斗" : "斗内保土";
        if (soilStream != null) soilStream.enabled = false;
    }

    private void Prepare()
    {
        if (cutter == null) cutter = GetComponent<BucketSoilCutter>();
        if (flight == null)
        {
            flight = GetComponent<SoilFlightTransport>();
            if (flight == null) flight = gameObject.AddComponent<SoilFlightTransport>();
        }
    }
}
