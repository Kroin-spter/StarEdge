using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Volume parcels follow gravity; collision sweeps resolve actual destinations without particle rigidbodies.</summary>
[DisallowMultipleComponent]
public sealed class SoilFlightTransport : MonoBehaviour
{
    internal readonly struct Emission
    {
        internal readonly double volume;
        internal readonly Vector3 position;
        internal readonly Vector3 velocity;

        internal Emission(double volume, Vector3 position, Vector3 velocity)
        {
            this.volume = volume;
            this.position = position;
            this.velocity = velocity;
        }
    }

    public double AirborneVolumeM3
    {
        get { double sum = 0; foreach (var parcel in parcels) sum += parcel.volume; return sum; }
    }
    public int ParcelCount => parcels.Count;
    public SoilReceivingZone LastReceiver { get; private set; }
    public double SurfacePendingVolumeM3
    {
        get { double sum = 0; foreach (var pending in deposits) sum += pending.volume; return sum; }
    }
    private sealed class Parcel
    {
        internal Vector3 position, velocity;
        internal double volume;
        internal float idleSeconds;
    }
    private readonly List<Parcel> parcels = new List<Parcel>();
    private readonly List<Parcel> deposits = new List<Parcel>();
    private readonly List<SoilReceivingZone> piles = new List<SoilReceivingZone>();
    private BucketSoilCutter cutter;
    private GameObject visual;
    private Mesh mesh;
    // Match the loose-pile repose angle. Steeper faces transport spoil, not support it.
    private static readonly float MinimumStableNormalY = Mathf.Cos(32 * Mathf.Deg2Rad);

    /// <summary>Add all outlet parcels and rebuild the shared visual mesh once.</summary>
    internal void EmitBatch(IReadOnlyList<Emission> emissions)
    {
        if (emissions == null || emissions.Count == 0) return;
        if (cutter == null) cutter = GetComponent<BucketSoilCutter>();
        for (int i = 0; i < emissions.Count; i++)
        {
            Emission emission = emissions[i];
            if (emission.volume <= 0) continue;
            parcels.Add(new Parcel
            {
                volume = emission.volume,
                position = emission.position,
                velocity = emission.velocity
            });
        }
        RefreshVisual();
    }

    private void FixedUpdate()
    {
        var unloader = GetComponent<BucketSoilUnloader>();
        if (unloader == null || !unloader.isActiveAndEnabled)
            Advance(Time.fixedDeltaTime, unloader != null ? unloader.receivers : null,
                unloader != null ? unloader.obstructionMask : (LayerMask)~0);
    }

    internal void Advance(float deltaSeconds, SoilReceivingZone[] receivers, LayerMask mask)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0) return;
        if (cutter == null) cutter = GetComponent<BucketSoilCutter>();
        // Sweeps prevent tunnelling, and small substeps resolve receiver-plane impacts before the solid floor.
        int count = Mathf.Max(1, Mathf.CeilToInt(deltaSeconds / .02f));
        float dt = deltaSeconds / count;
        foreach (var pending in deposits) pending.idleSeconds += deltaSeconds;
        for (int step = 0; step < count; step++)
            for (int i = parcels.Count - 1; i >= 0; i--)
                if (AdvanceParcel(parcels[i], dt, receivers, mask)) parcels.RemoveAt(i);
        FlushTerrainDeposits(parcels.Count == 0);
        RefreshVisual();
    }

    internal void Clear()
    {
        parcels.Clear();
        deposits.Clear();
        LastReceiver = null;
        foreach (var pile in piles)
            if (pile != null) DestroyObject(pile.gameObject);
        piles.Clear();
        if (visual != null) visual.SetActive(false);
    }

    private bool AdvanceParcel(Parcel parcel, float dt, SoilReceivingZone[] receivers, LayerMask mask)
    {
        // Off-map parcels stay in the ledger rather than being silently deleted by a lifetime timeout.
        if (parcel.position.y < -10000) return false;
        Vector3 start = parcel.position;
        // A previous parcel can raise the heightmap around a following parcel in the same frame.
        // Resolve that engulfed parcel against the new surface; a ray starting inside terrain would miss it.
        var source = cutter.soil;
        if (source != null && source.Terrain != null && source.HasSurfaceAt(start))
        {
            Vector3 local = start - source.transform.position;
            Vector3 size = source.Terrain.terrainData.size;
            if (local.x > 0 && local.z > 0 && local.x < size.x && local.z < size.z)
            {
                float height = source.Terrain.SampleHeight(start) + source.transform.position.y;
                if (start.y <= height)
                {
                    Vector3 normal = source.Terrain.terrainData.GetInterpolatedNormal(
                        local.x / size.x, local.z / size.z);
                    if (normal.y < MinimumStableNormalY)
                    {
                        SlideOnSurface(parcel, new Vector3(start.x, height, start.z), normal, dt);
                        return false;
                    }
                    Settle(parcel, new Vector3(start.x, height, start.z), source);
                    return true;
                }
            }
        }
        SoilReceivingZone engulfingReceiver = FindEngulfingReceiver(start, receivers);
        if (engulfingReceiver != null)
            return ResolveReceiver(parcel, engulfingReceiver, start);
        Vector3 next = start + parcel.velocity * dt + Physics.gravity * (.5f * dt * dt);
        parcel.velocity += Physics.gravity * dt;
        Vector3 segment = next - start;
        float length = segment.magnitude;
        if (length < 1e-8f) return false;
        float nearest = length + 1;
        SoilReceivingZone receiver = null;
        RaycastHit collision = default;
        bool hasCollision = false;
        foreach (var hit in Physics.RaycastAll(start, segment / length, length, mask, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider.transform.IsChildOf(transform) || hit.distance >= nearest) continue;
            nearest = hit.distance;
            collision = hit;
            hasCollision = true;
        }
        if (receivers != null && next.y < start.y)
            foreach (var zone in receivers)
            {
                if (zone == null) continue;
                Vector3 normal = zone.transform.up;
                Vector3 center = zone.transform.TransformPoint(Vector3.up * zone.SurfaceLocalY);
                float denominator = Vector3.Dot(start - next, normal);
                if (denominator <= 1e-8f) continue;
                float t = Vector3.Dot(start - center, normal) / denominator;
                if (t < 0 || t > 1 || t * length > nearest) continue;
                Vector3 point = Vector3.Lerp(start, next, t);
                if (!zone.TryGetLanding(point + Vector3.up * .03f, .1f, out _)) continue;
                receiver = zone;
                nearest = t * length;
            }
        if (receiver != null)
            return ResolveReceiver(parcel, receiver, Vector3.Lerp(start, next, nearest / length));
        if (hasCollision)
        {
            var terrain = collision.collider.GetComponent<DeformableSoilTerrain>();
            // Restrict the new repose rule to soil Terrain; preserve vehicle/map collision behavior.
            if (terrain != null ? collision.normal.y < MinimumStableNormalY : collision.normal.y < .45f)
            {
                parcel.position = collision.point + collision.normal * .025f;
                parcel.velocity = Vector3.ProjectOnPlane(parcel.velocity, collision.normal) * (terrain != null ? 1 : .5f);
                return false;
            }
            Settle(parcel, collision.point, terrain);
            return true;
        }
        parcel.position = next;
        return false;
    }

    private static void SlideOnSurface(Parcel parcel, Vector3 point, Vector3 normal, float dt)
    {
        // Integrate tangential gravity even when the parcel begins inside Terrain.
        // Frame-independent damping avoids the old per-impact halving that pinned spoil.
        parcel.velocity = Vector3.ProjectOnPlane(parcel.velocity + Physics.gravity * dt, normal)
            * Mathf.Exp(-1.5f * dt);
        parcel.position = point + normal * .025f + parcel.velocity * dt;
    }

    private static SoilReceivingZone FindEngulfingReceiver(Vector3 position, SoilReceivingZone[] receivers)
    {
        if (receivers == null) return null;
        SoilReceivingZone highest = null;
        foreach (SoilReceivingZone receiver in receivers)
        {
            if (receiver == null || !receiver.isActiveAndEnabled || receiver.size.x <= 0 || receiver.size.y <= 0 ||
                Vector3.Dot(receiver.transform.up, Vector3.up) <= Mathf.Cos(30f * Mathf.Deg2Rad) ||
                (receiver.transform.lossyScale - Vector3.one).sqrMagnitude > .000001f) continue;
            Vector3 local = receiver.transform.InverseTransformPoint(position);
            if (local.y < 0f || local.y > receiver.SurfaceLocalY) continue;
            if (Mathf.Abs(local.x) <= receiver.size.x * .5f && Mathf.Abs(local.z) <= receiver.size.y * .5f)
                if (highest == null || receiver.SurfaceY > highest.SurfaceY) highest = receiver;
        }
        return highest;
    }

    private bool ResolveReceiver(Parcel parcel, SoilReceivingZone receiver, Vector3 impactPoint)
    {
        double accepted = receiver.Accept(cutter, parcel.volume);
        if (accepted > 0) LastReceiver = receiver;
        cutter.RecordLanding(receiver, accepted);
        parcel.volume -= accepted;
        if (parcel.volume <= 0) return true;
        // A full bed spills at its nearest side instead of stopping discharge at the bucket.
        Vector3 local = receiver.transform.InverseTransformPoint(impactPoint);
        float sign = local.x >= 0 ? 1 : -1;
        local.x = sign * (receiver.size.x * .5f + .2f);
        local.y = receiver.SurfaceLocalY + .05f;
        parcel.position = receiver.transform.TransformPoint(local);
        parcel.velocity = receiver.transform.right * sign * .5f;
        return false;
    }

    private void Settle(Parcel parcel, Vector3 point, DeformableSoilTerrain terrain)
    {
        // Foreign source terrain stays untouched so per-source resets remain coherent.
        if (terrain != null && terrain == cutter.soil)
        {
            // Batch neighbouring impacts before writing the quantized heightmap.
            foreach (var pending in deposits)
            {
                Vector3 offset = pending.position - point;
                if (offset.x * offset.x + offset.z * offset.z > .25f * .25f) continue;
                pending.volume += parcel.volume;
                pending.idleSeconds = 0;
                return;
            }
            deposits.Add(new Parcel { position = point, volume = parcel.volume });
            return;
        }
        if (parcel.volume > 0) DepositPile(point, parcel.volume);
    }

    private void FlushTerrainDeposits(bool force)
    {
        for (int i = deposits.Count - 1; i >= 0; i--)
        {
            var pending = deposits[i];
            bool settleRemainder = force || pending.idleSeconds >= .5f;
            if (!settleRemainder && pending.volume < .01) continue;
            if (cutter.soil != null)
            {
                double added = cutter.soil.Deposit(pending.position, .65f, pending.volume, MinimumStableNormalY);
                cutter.RecordLanding(cutter.soil, added);
                pending.volume -= added;
            }
            if (settleRemainder)
            {
                // Preserve the final sub-heightmap quantum and any height-limit overflow as visible loose soil.
                if (pending.volume > 0)
                {
                    Vector3 point = pending.position;
                    if (cutter.soil != null && cutter.soil.HasSurfaceAt(point))
                        point.y = cutter.soil.Terrain.SampleHeight(point) + cutter.soil.transform.position.y;
                    DepositPile(point, pending.volume);
                }
                deposits.RemoveAt(i);
            }
        }
    }

    private void DepositPile(Vector3 point, double volume)
    {
        SoilReceivingZone target = null;
        foreach (var pile in piles)
            if (pile != null && (pile.transform.position - point).sqrMagnitude < .5f * .5f)
            { target = pile; break; }
        if (target == null)
        {
            var go = new GameObject("Deposited soil pile (runtime)");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, gameObject.scene);
            go.transform.position = point;
            target = go.AddComponent<SoilReceivingZone>();
            target.isGroundPile = true;
            target.size = new Vector2(100, 100);
            target.capacityM3 = 100000;
            target.maximumFillHeight = 100;
            if (cutter.fillVisual != null)
                target.GetComponent<Renderer>().sharedMaterial = cutter.fillVisual.GetComponent<Renderer>().sharedMaterial;
            piles.Add(target);
        }
        double accepted = target.Accept(cutter, volume);
        cutter.RecordLanding(target, accepted);
        if (accepted < volume)
            throw new InvalidOperationException("Ground pile capacity exhausted; soil cannot be discarded.");
    }

    private void RefreshVisual()
    {
        if (parcels.Count == 0 && deposits.Count == 0) { if (visual != null) visual.SetActive(false); return; }
        if (visual == null)
        {
            visual = new GameObject("Falling soil parcels (visual only)") { hideFlags = HideFlags.DontSave };
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(visual, gameObject.scene);
            mesh = new Mesh { name = "Falling soil", hideFlags = HideFlags.DontSave };
            mesh.MarkDynamic();
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = visual.AddComponent<MeshRenderer>();
            if (cutter.fillVisual != null) renderer.sharedMaterial = cutter.fillVisual.GetComponent<Renderer>().sharedMaterial;
        }
        visual.SetActive(true);
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        // Three offset clumps per parcel: visual only, with deterministic spread and no physics cost.
        for (int pIndex = 0; pIndex < parcels.Count + deposits.Count; pIndex++)
        {
            var parcel = pIndex < parcels.Count ? parcels[pIndex] : deposits[pIndex - parcels.Count];
            float r = Mathf.Clamp((float)Math.Pow(parcel.volume, 1.0 / 3) * .6f, .025f, .14f);
            Vector3 p = parcel.position;
            if (pIndex >= parcels.Count && cutter.soil != null)
                p.y = cutter.soil.Terrain.SampleHeight(p) + cutter.soil.transform.position.y + r;
            for (int clump = 0; clump < 3; clump++)
            {
                int index = vertices.Count;
                float angle = (pIndex * 2.17f + clump * 2.09f);
                Vector3 offset = new Vector3(Mathf.Cos(angle), .15f * Mathf.Sin(angle * 1.7f), Mathf.Sin(angle)) * r * .55f;
                Vector3 c = p + offset;
                float cr = r * (clump == 0 ? 1f : .7f);
                vertices.AddRange(new[] { c + Vector3.up*cr, c-Vector3.up*cr, c+Vector3.right*cr,
                    c+Vector3.forward*cr, c-Vector3.right*cr, c-Vector3.forward*cr });
                foreach (int t in new[] { 0,3,2,0,4,3,0,5,4,0,2,5,1,2,3,1,3,4,1,4,5,1,5,2 }) triangles.Add(index+t);
            }
        }
        mesh.Clear();
        mesh.indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private static void DestroyObject(UnityEngine.Object value)
    {
        if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
    }

    private void OnDestroy()
    {
        Clear();
        if (visual != null) DestroyObject(visual);
        if (mesh != null) DestroyObject(mesh);
    }
}
