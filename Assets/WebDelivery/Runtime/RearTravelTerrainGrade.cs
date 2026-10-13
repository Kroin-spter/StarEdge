using System;
using UnityEngine;

/// <summary>Grades only the rear and side travel band of an already baked mound height field.</summary>
public static class RearTravelTerrainGrade
{
    /// <summary>Build a local descending ramp while leaving the bearing core, forward face and boundary unchanged.</summary>
    /// <param name="before">Normalized Terrain heights indexed [z,x].</param>
    /// <param name="origin">Terrain world origin.</param>
    /// <param name="size">Terrain world size.</param>
    /// <param name="machine">Machine world X/Z center.</param>
    /// <param name="rotation">Machine heading; local positive Z is the forward dig face.</param>
    /// <param name="boundary">Existing replacement polygon in world X/Z.</param>
    /// <param name="scope">Output interior rear cells used for comparable slope measurements.</param>
    /// <returns>A new normalized height field; input is unchanged.</returns>
    public static float[,] Grade(float[,] before, Vector3 origin, Vector3 size, Vector2 machine,
        Quaternion rotation, Vector2[] boundary, out bool[,] scope)
    {
        if (before == null || before.GetLength(0) != before.GetLength(1) || before.GetLength(0) < 3 ||
            size.x <= 0f || size.y <= 0f || size.z <= 0f || boundary == null || boundary.Length < 3)
            throw new ArgumentException("Invalid baked terrain grade inputs.");
        int n = before.GetLength(0);
        var after = (float[,])before.Clone();
        scope = new bool[n, n];
        Quaternion inverse = Quaternion.Inverse(rotation);
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                Vector2 point = new Vector2(origin.x + x * size.x / (n - 1), origin.z + z * size.z / (n - 1));
                Vector3 local = inverse * new Vector3(point.x - machine.x, 0f, point.y - machine.y);
                float radius = new Vector2(local.x, local.z).magnitude;
                if (radius <= 2.5f || radius >= 8f || local.z >= 1f ||
                    !MapFollowingSoilSurface.Contains(boundary, point)) continue;
                float edge = DistanceToPolygon(boundary, point);
                if (edge <= 1f) continue;
                float sideWeight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - local.z) / 1.5f));
                float boundaryWeight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((edge - 1f) / 1.5f));
                float outerWeight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((radius - 6.5f) / 1.5f));
                float weight = sideWeight * boundaryWeight * outerWeight;
                if (weight <= 0f) continue;

                // Anchors follow the existing core and outer map-facing terrain; only the intervening ledge moves.
                Vector2 ray = (point - machine) / radius;
                float inner = Sample(before, origin, size, machine + ray * 2.5f);
                float outer = Sample(before, origin, size, machine + ray * 8f);
                float t = Mathf.Clamp01((radius - 2.5f) / 5.5f);
                float target = Mathf.Lerp(inner, outer, Mathf.SmoothStep(0f, 1f, t));
                after[z, x] = Mathf.Clamp01(Mathf.Lerp(before[z, x], target, weight));
                scope[z, x] = weight >= .5f;
            }
        return after;
    }

    /// <summary>Measure the largest adjacent height difference per world metre within a chosen interior scope.</summary>
    /// <param name="heights">Normalized Terrain heights indexed [z,x].</param>
    /// <param name="size">Terrain world size.</param>
    /// <param name="scope">Cells to compare; both endpoints must be in scope.</param>
    /// <returns>Largest vertical rise divided by horizontal distance.</returns>
    public static float MaxNeighborSlope(float[,] heights, Vector3 size, bool[,] scope)
    {
        int n = heights.GetLength(0);
        float peak = 0f;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                if (!scope[z, x]) continue;
                if (x + 1 < n && scope[z, x + 1])
                    peak = Mathf.Max(peak, Mathf.Abs(heights[z, x] - heights[z, x + 1]) * size.y * (n - 1) / size.x);
                if (z + 1 < n && scope[z + 1, x])
                    peak = Mathf.Max(peak, Mathf.Abs(heights[z, x] - heights[z + 1, x]) * size.y * (n - 1) / size.z);
            }
        return peak;
    }

    private static float Sample(float[,] heights, Vector3 origin, Vector3 size, Vector2 point)
    {
        int n = heights.GetLength(0);
        float fx = Mathf.Clamp((point.x - origin.x) / size.x * (n - 1), 0f, n - 1f);
        float fz = Mathf.Clamp((point.y - origin.z) / size.z * (n - 1), 0f, n - 1f);
        int x = Mathf.Min(Mathf.FloorToInt(fx), n - 2), z = Mathf.Min(Mathf.FloorToInt(fz), n - 2);
        return Mathf.Lerp(Mathf.Lerp(heights[z, x], heights[z, x + 1], fx - x),
            Mathf.Lerp(heights[z + 1, x], heights[z + 1, x + 1], fx - x), fz - z);
    }

    private static float DistanceToPolygon(Vector2[] polygon, Vector2 point)
    {
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 start = polygon[i], edge = polygon[(i + 1) % polygon.Length] - start;
            float t = Mathf.Clamp01(Vector2.Dot(point - start, edge) / edge.sqrMagnitude);
            nearest = Mathf.Min(nearest, Vector2.Distance(point, start + edge * t));
        }
        return nearest;
    }
}
