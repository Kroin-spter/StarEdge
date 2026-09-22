using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>A closed, bucket-local soil mesh. Geometry displays volume but never owns or removes it.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public sealed class BucketSoilFill : MonoBehaviour
{
    [Min(.01f)] public float innerHalfWidth = .38f;
    [Tooltip("Convex cavity outline in bucket-local (Y,Z) metres, inset from the ZX200 shell.")]
    public Vector2[] cavityProfile = {
        new Vector2(.50f, -.13f), new Vector2(.33f, -.04f),
        new Vector2(.30f, .12f), new Vector2(.52f, .48f),
        new Vector2(.73f, .80f), new Vector2(.79f, .84f),
        new Vector2(.85f, .63f), new Vector2(.98f, .18f), new Vector2(.93f, -.07f)
    };
    private static readonly Vector2 FillNormal = new Vector2(-1.25f, .82f).normalized;
    private Mesh mesh;
    private double lastVolume = double.NaN;
    private Vector3 surfaceNormal = new Vector3(0, FillNormal.x, FillNormal.y);

    /// <summary>Maximum geometric volume of the calibrated cavity in cubic metres at unit scale.</summary>
    public double CapacityM3 => Area(cavityProfile) * innerHalfWidth * 2;

    /// <summary>Direction out of the bucket opening in bucket-local coordinates.</summary>
    public Vector3 OpeningNormalLocal => new Vector3(0, FillNormal.x, FillNormal.y);

    /// <summary>Current bucket-local upward surface normal; defaults to the opening direction.</summary>
    public Vector3 SurfaceNormalLocal => surfaceNormal;

    /// <summary>Immediately rotate the free surface while preserving the displayed volume.</summary>
    /// <param name="localUp">Finite, nonzero gravity-opposing direction in bucket-local coordinates.</param>
    public void SetSurfaceNormal(Vector3 localUp)
    {
        Vector3 normal = ValidNormal(localUp);
        if ((normal - surfaceNormal).sqrMagnitude < 1e-12f) return;
        surfaceNormal = normal;
        if (double.IsNaN(lastVolume)) return;
        double volume = lastVolume;
        lastVolume = double.NaN;
        SetVolume(volume);
    }

    /// <summary>Return cubic metres below the lowest mouth lip, a quasi-static spill approximation.</summary>
    /// <param name="localUp">Finite, nonzero gravity-opposing direction in bucket-local coordinates.</param>
    /// <returns>Retainable cavity volume; a fully downward opening retains no soil.</returns>
    public double RetainedCapacityM3(Vector3 localUp)
    {
        Vector3 normal = ValidNormal(localUp);
        if (Vector3.Dot(normal, OpeningNormalLocal) <= -.999f) return 0;
        float height = float.PositiveInfinity;
        foreach (int index in new[] { 2, 5 })
        {
            Vector2 lip = cavityProfile[index];
            height = Mathf.Min(height, Vector3.Dot(new Vector3(-innerHalfWidth, lip.x, lip.y), normal));
            height = Mathf.Min(height, Vector3.Dot(new Vector3(innerHalfWidth, lip.x, lip.y), normal));
        }
        return Math.Min(CapacityM3, Volume(Clip(normal, height)));
    }

    /// <summary>Display the supplied cubic metres, clamped to the cavity. Does not change collected soil.</summary>
    /// <param name="volumeM3">Finite, nonnegative soil volume; values above the cavity display as full.</param>
    public void SetVolume(double volumeM3)
    {
        if (!double.IsFinite(volumeM3) || volumeM3 < 0)
            throw new ArgumentOutOfRangeException(nameof(volumeM3));
        double target = Math.Min(volumeM3, CapacityM3);
        var renderer = GetComponent<MeshRenderer>();
        renderer.enabled = target > 1e-7;
        if (mesh == null)
        {
            mesh = new Mesh { name = "Bucket soil (runtime cavity mesh)", hideFlags = HideFlags.DontSave };
            mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = mesh;
        }
        if (Math.Abs(target - lastVolume) < 1e-8) return;
        lastVolume = target;
        mesh.Clear();
        if (!renderer.enabled) return;

        float low = float.PositiveInfinity, high = float.NegativeInfinity;
        foreach (Vector2 point in cavityProfile)
        {
            float center = Vector3.Dot(new Vector3(0, point.x, point.y), surfaceNormal);
            float extent = Mathf.Abs(surfaceNormal.x) * innerHalfWidth;
            low = Mathf.Min(low, center - extent);
            high = Mathf.Max(high, center + extent);
        }
        // Solve a plane height by the enclosed tetrahedral volume, including roll.
        for (int i = 0; i < 28; i++)
        {
            float middle = (low + high) * .5f;
            if (Volume(Clip(surfaceNormal, middle)) < target) low = middle;
            else high = middle;
        }
        BuildMesh(Clip(surfaceNormal, high));
    }

    // Clip each outward-facing cavity wall and close the cut with an upward-facing cap.
    private List<List<Vector3>> Clip(Vector3 normal, float height)
    {
        var profile = new List<Vector2>(cavityProfile);
        if (SignedArea(profile) < 0) profile.Reverse();
        var faces = new List<List<Vector3>>();
        var left = new List<Vector3>();
        var right = new List<Vector3>();
        foreach (Vector2 point in profile)
        {
            left.Add(new Vector3(-innerHalfWidth, point.x, point.y));
            right.Add(new Vector3(innerHalfWidth, point.x, point.y));
        }
        for (int i = 0; i < profile.Count; i++)
        {
            int next = (i + 1) % profile.Count;
            faces.Add(new List<Vector3> { left[i], left[next], right[next], right[i] });
        }
        left.Reverse();
        faces.Add(left);
        faces.Add(right);

        var result = new List<List<Vector3>>();
        var cap = new List<Vector3>();
        foreach (var face in faces)
        {
            var clipped = new List<Vector3>();
            Vector3 previous = face[face.Count - 1];
            float before = Vector3.Dot(previous, normal) - height;
            foreach (Vector3 current in face)
            {
                float after = Vector3.Dot(current, normal) - height;
                if ((before <= 0) != (after <= 0))
                {
                    Vector3 crossing = Vector3.Lerp(previous, current, before / (before - after));
                    AddDistinct(clipped, crossing);
                    AddDistinct(cap, crossing);
                }
                if (after <= 0) AddDistinct(clipped, current);
                previous = current;
                before = after;
            }
            if (clipped.Count >= 3) result.Add(clipped);
        }
        if (cap.Count >= 3)
        {
            Vector3 center = Vector3.zero;
            foreach (Vector3 point in cap) center += point;
            center /= cap.Count;
            Vector3 axis = Vector3.Cross(normal, Mathf.Abs(normal.x) < .8f ? Vector3.right : Vector3.up).normalized;
            Vector3 other = Vector3.Cross(normal, axis);
            cap.Sort((a, b) => Mathf.Atan2(Vector3.Dot(a - center, other), Vector3.Dot(a - center, axis))
                .CompareTo(Mathf.Atan2(Vector3.Dot(b - center, other), Vector3.Dot(b - center, axis))));
            result.Add(cap);
        }
        return result;
    }

    private static void AddDistinct(List<Vector3> points, Vector3 point)
    {
        foreach (Vector3 existing in points)
            if ((existing - point).sqrMagnitude < 1e-14f) return;
        points.Add(point);
    }

    private static double Volume(List<List<Vector3>> faces)
    {
        double volume = 0;
        foreach (var face in faces)
            for (int i = 1; i < face.Count - 1; i++)
            {
                Vector3 a = face[0], b = face[i], c = face[i + 1];
                volume += ((double)a.x * ((double)b.y * c.z - (double)b.z * c.y)
                    + (double)a.y * ((double)b.z * c.x - (double)b.x * c.z)
                    + (double)a.z * ((double)b.x * c.y - (double)b.y * c.x)) / 6;
            }
        return Math.Max(0, volume);
    }

    private void BuildMesh(List<List<Vector3>> faces)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var uv = new List<Vector2>();
        foreach (var face in faces)
        {
            int start = vertices.Count;
            Vector3 tangent = (face[1] - face[0]).normalized;
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < face.Count; i++)
                normal += Vector3.Cross(face[i], face[(i + 1) % face.Count]);
            normal.Normalize();
            Vector3 bitangent = Vector3.Cross(normal, tangent);
            foreach (Vector3 point in face)
            {
                vertices.Add(point);
                uv.Add(new Vector2(Vector3.Dot(point, tangent), Vector3.Dot(point, bitangent)) * 3);
            }
            TriangulateFace(face, normal, start, triangles);
        }
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uv);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private static void TriangulateFace(List<Vector3> face, Vector3 normal, int offset, List<int> triangles)
    {
        var remaining = new List<int>();
        for (int i = 0; i < face.Count; i++) remaining.Add(i);
        // The calibrated profile contains a shallow reflex corner. Ear clipping
        // keeps every triangle within that outline instead of bridging its notch.
        while (remaining.Count > 3)
        {
            bool removed = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int previous = remaining[(i + remaining.Count - 1) % remaining.Count];
                int current = remaining[i];
                int next = remaining[(i + 1) % remaining.Count];
                Vector3 a = face[previous], b = face[current], c = face[next];
                float turn = Vector3.Dot(Vector3.Cross(b - a, c - a), normal);
                if (Mathf.Abs(turn) <= 1e-10f)
                {
                    remaining.RemoveAt(i);
                    removed = true;
                    break;
                }
                if (turn < 0) continue;
                bool occupied = false;
                foreach (int candidate in remaining)
                {
                    if (candidate == previous || candidate == current || candidate == next) continue;
                    Vector3 point = face[candidate];
                    if (Vector3.Dot(Vector3.Cross(b - a, point - a), normal) >= -1e-10f
                        && Vector3.Dot(Vector3.Cross(c - b, point - b), normal) >= -1e-10f
                        && Vector3.Dot(Vector3.Cross(a - c, point - c), normal) >= -1e-10f)
                    {
                        occupied = true;
                        break;
                    }
                }
                if (occupied) continue;
                triangles.AddRange(new[] { offset + previous, offset + current, offset + next });
                remaining.RemoveAt(i);
                removed = true;
                break;
            }
            if (!removed) throw new InvalidOperationException("Soil cavity face could not be triangulated.");
        }
        if (remaining.Count == 3)
            triangles.AddRange(new[] { offset + remaining[0], offset + remaining[1], offset + remaining[2] });
    }

    private static Vector3 ValidNormal(Vector3 localUp)
    {
        if (!float.IsFinite(localUp.x) || !float.IsFinite(localUp.y) || !float.IsFinite(localUp.z)
            || localUp.sqrMagnitude < 1e-12f || !float.IsFinite(localUp.sqrMagnitude))
            throw new ArgumentOutOfRangeException(nameof(localUp), "A finite, nonzero up direction is required.");
        return localUp.normalized;
    }

    private static double SignedArea(IList<Vector2> points)
    {
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Vector2 a = points[i], b = points[(i + 1) % points.Count];
            area += (double)a.x * b.y - (double)b.x * a.y;
        }
        return area * .5;
    }

    private static double Area(IList<Vector2> points) => Math.Abs(SignedArea(points));

    private void OnValidate() { lastVolume = double.NaN; }

    private void OnDestroy()
    {
        if (mesh == null) return;
        if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
    }
}
