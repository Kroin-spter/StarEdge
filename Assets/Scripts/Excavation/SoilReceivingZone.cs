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
    public float SurfaceY => transform.position.y + (float)(VolumeM3 / Math.Max(.001, size.x * size.y));
    public IReadOnlyList<TransferRecord> Records => records.AsReadOnly();
    private readonly List<TransferRecord> records = new List<TransferRecord>();
    private Mesh mesh;

    /// <summary>Remove loose ground-pile soil at a tooth sweep and return the volume removed.</summary>
    public double ExcavateLoose(Vector3 tooth, Vector3 sweep, double requested)
    {
        if (!isGroundPile || requested <= 0 || !isActiveAndEnabled || VolumeM3 <= 0) return 0;
        Vector3 local = transform.InverseTransformPoint(tooth);
        float radius = Mathf.Max(.08f, Mathf.Min(size.x, size.y) * .18f);
        if (Mathf.Abs(local.x) > size.x * .5f + radius || Mathf.Abs(local.z) > size.y * .5f + radius ||
            tooth.y > SurfaceY + .12f || tooth.y < transform.position.y - .2f) return 0;
        double removed = Math.Min(requested, VolumeM3);
        VolumeM3 -= removed;
        MassKg = Math.Max(0, MassKg - removed * (records.Count > 0 ? records[0].MassKg / Math.Max(.000001, records[0].VolumeM3) : 1600));
        RefreshMesh();
        return removed;
    }

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
    /// <returns>True only for an enabled, upright, unit-scale bed beneath the point.</returns>
    public bool TryGetLanding(Vector3 worldPoint, float maximumDrop, out Vector3 landing)
    {
        landing = new Vector3(worldPoint.x, SurfaceY, worldPoint.z);
        if (!isActiveAndEnabled || size.x <= 0 || size.y <= 0 || maximumDrop <= 0 ||
            Vector3.Dot(transform.up, Vector3.up) < .9999f ||
            (transform.lossyScale - Vector3.one).sqrMagnitude > .000001f) return false;
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
