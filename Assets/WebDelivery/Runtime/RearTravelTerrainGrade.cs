using System;
using UnityEngine;

/// <summary>Grades only the rear and side travel band of an already baked mound height field.</summary>
public static class RearTravelTerrainGrade
{
    /// <summary>Remove local ridges from a rear access patch while retaining its broad downhill trend and protected ground.</summary>
    /// <param name="before">Normalized Terrain heights indexed [z,x].</param>
    /// <param name="origin">Terrain world origin.</param>
    /// <param name="size">Terrain world size.</param>
    /// <param name="center">Access patch center in world X/Z.</param>
    /// <param name="radius">Access patch radius in metres.</param>
    /// <param name="machine">Machine center in world X/Z.</param>
    /// <param name="truck">Truck center in world X/Z.</param>
    /// <param name="machineRotation">Machine heading used to identify its forward work face.</param>
    /// <param name="boundary">Existing replacement polygon in world X/Z.</param>
    /// <param name="scope">Output cells in the weighted patch, including its feather, for seam-aware slope checks.</param>
    /// <returns>A smoothed height-field copy; the input and protected cells are unchanged.</returns>
    public static float[,] SmoothAccessPatch(float[,] before, Vector3 origin, Vector3 size,
        Vector2 center, float radius, Vector2 machine, Vector2 truck, Quaternion machineRotation,
        Vector2[] boundary, out bool[,] scope)
    {
        if (before == null || before.GetLength(0) != before.GetLength(1) || before.GetLength(0) < 3 ||
            size.x <= 0f || size.y <= 0f || size.z <= 0f || radius <= 1.5f ||
            boundary == null || boundary.Length < 3)
            throw new ArgumentException("Invalid access patch inputs.");
        int n = before.GetLength(0);
        float stepX = size.x / (n - 1), stepZ = size.z / (n - 1);
        var after = (float[,])before.Clone();
        scope = new bool[n, n];
        Quaternion inverse = Quaternion.Inverse(machineRotation);
        int reachX = Mathf.CeilToInt(1.5f / stepX), reachZ = Mathf.CeilToInt(1.5f / stepZ);
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                Vector2 point = new Vector2(origin.x + x * stepX, origin.z + z * stepZ);
                float patchDistance = Vector2.Distance(point, center);
                float machineDistance = Vector2.Distance(point, machine);
                float truckDistance = Vector2.Distance(point, truck);
                float localForward = (inverse * new Vector3(point.x - machine.x, 0f, point.y - machine.y)).z;
                if (patchDistance >= radius || machineDistance <= 2.5f || truckDistance <= 3f ||
                    localForward >= 1f || !MapFollowingSoilSurface.Contains(boundary, point)) continue;
                float edgeDistance = DistanceToPolygon(boundary, point);
                if (edgeDistance <= 1f) continue;

                // Every protected edge tapers to zero over at least 1.5 m; no fixed-height pad is introduced.
                float weight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((radius - patchDistance) / 1.5f));
                weight *= Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((machineDistance - 2.5f) / 1.5f));
                weight *= Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((truckDistance - 3f) / 1.5f));
                weight *= Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - localForward) / 1.5f));
                weight *= Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((edgeDistance - 1f) / 1.5f));
                if (weight <= 0f) continue;

                // Symmetric Gaussian averaging removes high-frequency ledges but reproduces a linear slope.
                float sum = 0f, total = 0f;
                for (int dz = -reachZ; dz <= reachZ; dz++)
                    for (int dx = -reachX; dx <= reachX; dx++)
                    {
                        int sx = x + dx, sz = z + dz;
                        float distanceSquared = dx * dx * stepX * stepX + dz * dz * stepZ * stepZ;
                        if (sx < 0 || sx >= n || sz < 0 || sz >= n || distanceSquared > 2.25f) continue;
                        float kernel = Mathf.Exp(-distanceSquared / 1.125f);
                        sum += before[sz, sx] * kernel;
                        total += kernel;
                    }
                after[z, x] = Mathf.Clamp01(Mathf.Lerp(before[z, x], sum / total, weight));
                scope[z, x] = true;
            }

        // Back off the entire edit if its feather would create a steeper adjacent-cell seam.
        float originalPeak = PeakTouchingScope(before, size, scope);
        for (int attempt = 0; attempt < 12 && PeakTouchingScope(after, size, scope) > originalPeak + 1e-6f; attempt++)
        {
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    if (scope[z, x]) after[z, x] = Mathf.Lerp(before[z, x], after[z, x], .5f);
        }
        if (PeakTouchingScope(after, size, scope) > originalPeak + 1e-6f)
            return (float[,])before.Clone();
        return after;
    }

    private static float PeakTouchingScope(float[,] heights, Vector3 size, bool[,] scope)
    {
        int n = heights.GetLength(0);
        float peak = 0f;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                if (x + 1 < n && (scope[z, x] || scope[z, x + 1]))
                    peak = Mathf.Max(peak, Mathf.Abs(heights[z, x + 1] - heights[z, x]) * size.y * (n - 1) / size.x);
                if (z + 1 < n && (scope[z, x] || scope[z + 1, x]))
                    peak = Mathf.Max(peak, Mathf.Abs(heights[z + 1, x] - heights[z, x]) * size.y * (n - 1) / size.z);
            }
        return peak;
    }

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
