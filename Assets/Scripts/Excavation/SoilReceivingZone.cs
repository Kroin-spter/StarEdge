using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Receiving bed with conservative, locally sloping soil and a shared landing surface.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public sealed class SoilReceivingZone : MonoBehaviour
{
    public Vector2 size = new Vector2(3, 4);
    [Min(.001f)] public float capacityM3 = 3;
    [Min(.01f)] public float maximumFillHeight = 1;
    public bool isGroundPile;
    public double VolumeM3 { get; private set; }
    public double MassKg { get; private set; }
    public double EffectiveCapacityM3 => Math.Max(0, Math.Min(capacityM3, size.x * size.y * maximumFillHeight));
    public double AvailableM3 => Math.Max(0, EffectiveCapacityM3 - VolumeM3);
    internal float SurfaceLocalY => (float)(VolumeM3 / Math.Max(.001, size.x * size.y));
    public float SurfaceY => transform.TransformPoint(Vector3.up * SurfaceLocalY).y;
    public IReadOnlyList<TransferRecord> Records => records.AsReadOnly();
    private readonly List<TransferRecord> records = new List<TransferRecord>();
    private Mesh mesh;
    private ReceivingSoilHeap heap;
    internal float HeightAt(Vector3 local) => !isGroundPile && heap != null ? heap.Height(local) : SurfaceLocalY;

    /// <summary>Reexcavate loose soil only when a moving tooth intersects the displayed pile.</summary>
    /// <param name="tooth">Current world-space tooth position.</param>
    /// <param name="sweep">Actual world-space displacement from the preceding sample.</param>
    /// <param name="requested">Maximum removable volume in cubic metres.</param>
    /// <returns>Volume removed; zero for stationary, distant or nonintersecting teeth.</returns>
    public double ExcavateLoose(Vector3 tooth, Vector3 sweep, double requested)
    {
        if (!CanExcavate(requested) || !Finite(tooth) || !Finite(sweep) || sweep.sqrMagnitude < 1e-10f) return 0;
        if (!TouchesPile(transform.InverseTransformPoint(tooth - sweep),
            transform.InverseTransformPoint(tooth), mesh.vertices, mesh.triangles)) return 0;
        return RemoveLooseVolume(requested);
    }

    /// <summary>Test the actual tooth box across its width before reexcavating a loose pile.</summary>
    /// <param name="previousBlade">Eight world corners of the preceding cutting-edge box.</param>
    /// <param name="currentBlade">Eight world corners of the current cutting-edge box.</param>
    /// <param name="requested">Maximum removable volume in cubic metres.</param>
    /// <returns>Removed volume, or zero when the moving cutting edge misses the displayed pile.</returns>
    public double ExcavateLoose(Vector3[] previousBlade, Vector3[] currentBlade, double requested)
    {
        if (!CanExcavate(requested) || previousBlade == null || currentBlade == null ||
            previousBlade.Length != 8 || currentBlade.Length != 8) return 0;
        Bounds sweptBounds = new Bounds(transform.InverseTransformPoint(previousBlade[0]), Vector3.zero);
        for (int i = 0; i < 8; i++)
        {
            if (!Finite(previousBlade[i]) || !Finite(currentBlade[i])) return 0;
            sweptBounds.Encapsulate(transform.InverseTransformPoint(previousBlade[i]));
            sweptBounds.Encapsulate(transform.InverseTransformPoint(currentBlade[i]));
        }
        if (!sweptBounds.Intersects(mesh.bounds)) return 0;
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        // The box corner ordering uses bit 0 for width. Probe each moving edge
        // across its width so narrow piles between the corner markers are not missed.
        for (int edge = 0; edge < 8; edge += 2)
        {
            int samples = Mathf.Max(2, Mathf.CeilToInt(Vector3.Distance(
                currentBlade[edge], currentBlade[edge + 1]) / .025f));
            for (int sample = 0; sample <= samples; sample++)
            {
                float fraction = (float)sample / samples;
                Vector3 start = transform.InverseTransformPoint(Vector3.Lerp(
                    previousBlade[edge], previousBlade[edge + 1], fraction));
                Vector3 end = transform.InverseTransformPoint(Vector3.Lerp(
                    currentBlade[edge], currentBlade[edge + 1], fraction));
                if ((end - start).sqrMagnitude >= 1e-10f && TouchesPile(start, end, vertices, triangles))
                    return RemoveLooseVolume(requested);
            }
        }
        return 0;
    }

    private bool CanExcavate(double requested) => isGroundPile && isActiveAndEnabled &&
        double.IsFinite(requested) && requested > 0 && VolumeM3 > 0 && mesh != null;

    private double RemoveLooseVolume(double requested)
    {
        double density = MassKg / VolumeM3;
        double removed = Math.Min(requested, VolumeM3);
        VolumeM3 -= removed;
        MassKg = Math.Max(0, MassKg - removed * density);
        RefreshMesh();
        return removed;
    }

    private bool TouchesPile(Vector3 start, Vector3 end, Vector3[] vertices, int[] triangles)
    {
        Vector3 travel = end - start;
        float length = travel.magnitude;
        var ray = new Ray(start, travel / length);
        if (!mesh.bounds.Contains(start) &&
            (!mesh.bounds.IntersectRay(ray, out float distance) || distance > length)) return false;
        if (PointInsidePile(start, vertices, triangles) || PointInsidePile(end, vertices, triangles)) return true;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
            Vector3 edge1 = b - a, edge2 = c - a;
            Vector3 cross = Vector3.Cross(ray.direction, edge2);
            float determinant = Vector3.Dot(edge1, cross);
            if (Mathf.Abs(determinant) < 1e-8f) continue;
            float inverse = 1f / determinant;
            Vector3 offset = start - a;
            float u = Vector3.Dot(offset, cross) * inverse;
            if (u < 0 || u > 1) continue;
            Vector3 q = Vector3.Cross(offset, edge1);
            float v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0 || u + v > 1) continue;
            float hit = Vector3.Dot(edge2, q) * inverse;
            if (hit >= 0 && hit <= length) return true;
        }
        return false;
    }

    private static bool PointInsidePile(Vector3 point, Vector3[] vertices, int[] triangles)
    {
        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
        // Top and bottom fans share the same XZ footprint, including their sampled
        // slope elevations. Compare their actual vertical interval, not SurfaceY.
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
            float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(denominator) < 1e-8f) continue;
            float u = ((b.z - c.z) * (point.x - c.x) + (c.x - b.x) * (point.z - c.z)) / denominator;
            float v = ((c.z - a.z) * (point.x - c.x) + (a.x - c.x) * (point.z - c.z)) / denominator;
            if (u < -.00001f || v < -.00001f || u + v > 1.00001f) continue;
            float height = u * a.y + v * b.y + (1 - u - v) * c.y;
            minimum = Mathf.Min(minimum, height);
            maximum = Mathf.Max(maximum, height);
        }
        return point.y >= minimum && point.y <= maximum;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) &&
        float.IsFinite(value.y) && float.IsFinite(value.z);

    /// <summary>Immutable receipt for one accepted transfer; mass uses the source's captured density.</summary>
    public sealed class TransferRecord
    {
        public double TimeSeconds { get; }
        public BucketSoilCutter Bucket { get; }
        public DeformableSoilTerrain Source { get; }
        public double VolumeM3 { get; }
        public double MassKg { get; }
        internal Vector3 LocalLanding { get; }
        internal TransferRecord(BucketSoilCutter bucket, double volume, Vector3 localLanding)
        {
            TimeSeconds = Time.timeAsDouble;
            Bucket = bucket;
            Source = bucket.soil;
            VolumeM3 = volume;
            MassKg = volume * bucket.SoilDensityKgPerM3;
            LocalLanding = localLanding;
        }
    }

    /// <summary>Find the rendered receiving surface vertically beneath a discharge point.</summary>
    /// <param name="worldPoint">Bucket discharge point in world coordinates.</param>
    /// <param name="maximumDrop">Maximum permitted vertical drop in metres.</param>
    /// <param name="landing">Receiving surface position when valid.</param>
    /// <returns>True for an enabled unit-scale bed beneath the point, with a stable inclination below 30 degrees.</returns>
    public bool TryGetLanding(Vector3 worldPoint, float maximumDrop, out Vector3 landing)
    {
        landing = worldPoint;
        if (!isActiveAndEnabled || size.x <= 0 || size.y <= 0 || maximumDrop <= 0 ||
            Vector3.Dot(transform.up, Vector3.up) <= Mathf.Cos(30f * Mathf.Deg2Rad) ||
            (transform.lossyScale - Vector3.one).sqrMagnitude > .000001f) return false;
        if (!TryIntersectSegment(worldPoint, worldPoint - Vector3.up * maximumDrop, out landing)) return false;
        float drop = worldPoint.y - landing.y;
        return drop >= .02f && drop <= maximumDrop;
    }

    internal bool TryIntersectSegment(Vector3 start, Vector3 end, out Vector3 landing)
    {
        landing = start;
        if (!isActiveAndEnabled || size.x <= 0 || size.y <= 0 ||
            Vector3.Dot(transform.up, Vector3.up) <= Mathf.Cos(30f * Mathf.Deg2Rad) ||
            (transform.lossyScale - Vector3.one).sqrMagnitude > .000001f) return false;
        Vector3 a = transform.InverseTransformPoint(start), b = transform.InverseTransformPoint(end);
        if (!isGroundPile && heap != null)
        {
            if (!heap.Intersect(a, b, out Vector3 hit)) return false;
            landing = transform.TransformPoint(hit);
            return true;
        }
        if (a.y < SurfaceLocalY || b.y > SurfaceLocalY || a.y - b.y <= 1e-8f) return false;
        Vector3 local = Vector3.Lerp(a, b, (a.y - SurfaceLocalY) / (a.y - b.y));
        if (Mathf.Abs(local.x) > size.x * .5f || Mathf.Abs(local.z) > size.y * .5f) return false;
        landing = transform.TransformPoint(local);
        return true;
    }

    // Only the bucket owns debiting its inventory; accepting material is not a public minting API.
    internal double Accept(BucketSoilCutter bucket, double requested)
        => Accept(bucket, requested, transform.position);

    internal double Accept(BucketSoilCutter bucket, double requested, Vector3 landing)
    {
        if (!isActiveAndEnabled || double.IsNaN(requested) || double.IsInfinity(requested) || requested <= 0) return 0;
        double accepted = Math.Min(requested, AvailableM3);
        if (accepted <= 0) return 0;
        Vector3 local = transform.InverseTransformPoint(landing);
        local.x = Mathf.Clamp(local.x, -size.x * .5f, size.x * .5f);
        local.z = Mathf.Clamp(local.z, -size.y * .5f, size.y * .5f);
        var receipt = new TransferRecord(bucket, accepted, local);
        records.Add(receipt);
        VolumeM3 += accepted;
        MassKg += receipt.MassKg;
        if (!isGroundPile)
        {
            if (heap == null) heap = new ReceivingSoilHeap(size);
            heap.Add(accepted, local, maximumFillHeight);
        }
        RefreshMesh();
        return accepted;
    }

    internal void RemoveSource(DeformableSoilTerrain source, BucketSoilCutter singleBucket)
    {
        records.RemoveAll(record => source != null ? record.Source == source : record.Bucket == singleBucket);
        VolumeM3 = 0;
        MassKg = 0;
        heap = isGroundPile ? null : new ReceivingSoilHeap(size);
        foreach (var record in records)
        {
            VolumeM3 += record.VolumeM3;
            MassKg += record.MassKg;
            heap?.Add(record.VolumeM3, record.LocalLanding, maximumFillHeight);
        }
        RefreshMesh();
    }

    private void Awake() => RefreshMesh();

    private void RefreshMesh()
    {
        var renderer = GetComponent<MeshRenderer>();
        renderer.enabled = VolumeM3 > 0;
        if (VolumeM3 <= 0) return;
        if (mesh == null)
        {
            mesh = new Mesh { name = "Received soil", hideFlags = HideFlags.DontSave };
            GetComponent<MeshFilter>().sharedMesh = mesh;
        }
        mesh.Clear();
        if (isGroundPile)
        {
            DeformableSoilTerrain source = records.Count > 0 ? records[0].Source : null;
            const int sides = 24;
            float slope = Mathf.Tan(32 * Mathf.Deg2Rad);
            float polygonFactor = sides * Mathf.Sin(2 * Mathf.PI / sides) * .5f;
            float radius = Mathf.Pow((float)(VolumeM3 * 3 / (polygonFactor * slope)), 1f / 3);
            var vertices = new Vector3[sides + 2];
            var triangles = new int[sides * 6];
            var uv = new Vector2[sides + 2];
            vertices[0] = Vector3.up * radius * slope;
            vertices[1] = ConformPileBase(Vector3.zero, source);
            for (int i = 0; i < sides; i++)
            {
                float angle = i * 2 * Mathf.PI / sides;
                vertices[i+2] = ConformPileBase(
                    new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius, source);
                uv[i+2] = new Vector2(vertices[i+2].x, vertices[i+2].z) * 3;
                int next = (i+1) % sides + 2;
                int at = i*6;
                triangles[at] = 0; triangles[at+1] = next; triangles[at+2] = i+2;
                triangles[at+3] = 1; triangles[at+4] = i+2; triangles[at+5] = next;
            }
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.uv = uv;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return;
        }
        heap?.Build(mesh);
    }

    private Vector3 ConformPileBase(Vector3 localPoint, DeformableSoilTerrain source)
    {
        if (source == null || source.Terrain == null) return localPoint;
        Vector3 anchor = transform.position;
        if (!source.HasSurfaceAt(anchor)) return localPoint;
        float surface = source.Terrain.SampleHeight(anchor) + source.transform.position.y;
        // Only a deposit actually supported by this Terrain may conform to it.
        // Sampling every perimeter point independently bridged cliffs/vehicle impacts into tall spikes.
        if (Mathf.Abs(surface - anchor.y) > .1f) return localPoint;
        Vector3 horizontal = transform.TransformVector(localPoint);
        Vector3 sample = anchor + horizontal;
        if (!source.HasSurfaceAt(sample)) return localPoint;
        // Match the transport's 2.5 cm surface clearance at a cut edge, without allowing
        // a small pile to bridge metre-scale changes in terrain height.
        float limit = new Vector2(horizontal.x, horizontal.z).magnitude * Mathf.Tan(32f * Mathf.Deg2Rad) + .025f;
        horizontal.y = Mathf.Clamp(source.Terrain.SampleHeight(sample) + source.transform.position.y - anchor.y,
            -limit, limit);
        return transform.InverseTransformVector(horizontal);
    }

    private void OnDestroy()
    {
        if (mesh == null) return;
        if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.up * maximumFillHeight * .5f, new Vector3(size.x, maximumFillHeight, size.y));
    }
}
