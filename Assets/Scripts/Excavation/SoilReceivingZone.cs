using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Upright receiving bed with in-situ volume accounting and a level soil display.</summary>
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
        internal TransferRecord(BucketSoilCutter bucket, double volume)
        {
            TimeSeconds = Time.timeAsDouble;
            Bucket = bucket;
            Source = bucket.soil;
            VolumeM3 = volume;
            MassKg = volume * bucket.SoilDensityKgPerM3;
        }
    }

    /// <summary>Find the receiving surface vertically beneath a discharge point.</summary>
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
        // Intersect the vertical gravity ray with the actual tilted receiving plane.
        Vector3 normal = transform.up;
        Vector3 center = transform.TransformPoint(Vector3.up * SurfaceLocalY);
        float planeDrop = Vector3.Dot(worldPoint - center, normal) / normal.y;
        landing = worldPoint - Vector3.up * planeDrop;
        Vector3 local = transform.InverseTransformPoint(landing);
        float drop = worldPoint.y - landing.y;
        return Mathf.Abs(local.x) <= size.x * .5f && Mathf.Abs(local.z) <= size.y * .5f &&
            drop >= .02f && drop <= maximumDrop;
    }

    // Only the bucket owns debiting its inventory; accepting material is not a public minting API.
    internal double Accept(BucketSoilCutter bucket, double requested)
    {
        if (!isActiveAndEnabled || double.IsNaN(requested) || double.IsInfinity(requested) || requested <= 0) return 0;
        double accepted = Math.Min(requested, AvailableM3);
        if (accepted <= 0) return 0;
        var receipt = new TransferRecord(bucket, accepted);
        records.Add(receipt);
        VolumeM3 += accepted;
        MassKg += receipt.MassKg;
        RefreshMesh();
        return accepted;
    }

    internal void RemoveSource(DeformableSoilTerrain source, BucketSoilCutter singleBucket)
    {
        records.RemoveAll(record => source != null ? record.Source == source : record.Bucket == singleBucket);
        VolumeM3 = 0;
        MassKg = 0;
        foreach (var record in records) { VolumeM3 += record.VolumeM3; MassKg += record.MassKg; }
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
        float x = size.x * .5f, z = size.y * .5f;
        float height = (float)(VolumeM3 / (size.x * size.y));
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
        mesh.vertices = new[] { new Vector3(-x,0,-z), new Vector3(x,0,-z), new Vector3(x,0,z), new Vector3(-x,0,z),
            new Vector3(-x,height,-z), new Vector3(x,height,-z), new Vector3(x,height,z), new Vector3(-x,height,z) };
        mesh.triangles = new[] { 0,1,2,0,2,3, 4,6,5,4,7,6, 0,5,1,0,4,5,
            1,6,2,1,5,6, 2,7,3,2,6,7, 3,4,0,3,7,4 };
        mesh.uv = new[] { new Vector2(0,0),new Vector2(size.x,0),size,new Vector2(0,size.y),
            new Vector2(0,0),new Vector2(size.x,0),size,new Vector2(0,size.y) };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private Vector3 ConformPileBase(Vector3 localPoint, DeformableSoilTerrain source)
    {
        if (source == null || source.Terrain == null) return localPoint;
        Vector3 world = transform.TransformPoint(localPoint);
        if (!source.HasSurfaceAt(world)) return localPoint;
        world.y = source.Terrain.SampleHeight(world) + source.transform.position.y;
        return transform.InverseTransformPoint(world);
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
