using System;
using UnityEngine;

/// <summary>Geometry shared by the cuttable slope Terrain, Cesium clipping, and their support tests.</summary>
public static class ConstructionSiteFootprint
{
    /// <summary>Build the axis-aligned broad-phase bounds of a yaw-oriented slope footprint.</summary>
    /// <param name="center">World-space center of the slope footprint.</param>
    /// <param name="rotation">World orientation whose horizontal forward axis follows the slope.</param>
    /// <param name="size">Footprint width along local X and depth along local Z, in metres.</param>
    /// <param name="minimumY">Lower vertical bound used by the replacement Terrain.</param>
    /// <param name="maximumY">Upper vertical bound used by the replacement Terrain.</param>
    /// <returns>The smallest world-axis-aligned bounds containing the rotated footprint.</returns>
    public static Bounds CalculateBounds(Vector3 center, Quaternion rotation, Vector2 size,
        float minimumY, float maximumY)
    {
        Validate(center, rotation, size);
        Quaternion horizontalRotation = GetHorizontalRotation(rotation);
        if (!float.IsFinite(minimumY) || !float.IsFinite(maximumY) || maximumY <= minimumY)
            throw new ArgumentOutOfRangeException(nameof(maximumY), "Vertical bounds must be finite and increasing.");

        float halfWidth = size.x * .5f;
        float halfDepth = size.y * .5f;
        Vector3 first = center + horizontalRotation * new Vector3(-halfWidth, 0f, -halfDepth);
        Vector3 minimum = new Vector3(first.x, minimumY, first.z);
        Vector3 maximum = new Vector3(first.x, maximumY, first.z);
        for (int x = -1; x <= 1; x += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            Vector3 corner = center + horizontalRotation * new Vector3(x * halfWidth, 0f, z * halfDepth);
            minimum.x = Mathf.Min(minimum.x, corner.x);
            minimum.z = Mathf.Min(minimum.z, corner.z);
            maximum.x = Mathf.Max(maximum.x, corner.x);
            maximum.z = Mathf.Max(maximum.z, corner.z);
        }
        return new Bounds((minimum + maximum) * .5f, maximum - minimum);
    }

    /// <summary>Test whether a world position is within a yaw-oriented horizontal footprint.</summary>
    /// <param name="point">World position to test; its Y coordinate is ignored.</param>
    /// <param name="center">World-space center of the footprint.</param>
    /// <param name="rotation">World orientation whose horizontal axes define the footprint.</param>
    /// <param name="size">Footprint width along local X and depth along local Z, in metres.</param>
    /// <returns>True when the point lies on or inside the footprint.</returns>
    public static bool ContainsHorizontal(Vector3 point, Vector3 center, Quaternion rotation, Vector2 size)
    {
        Validate(center, rotation, size);
        if (!float.IsFinite(point.x) || !float.IsFinite(point.z)) return false;
        Vector3 delta = point - center;
        delta.y = 0f;
        Vector3 local = Quaternion.Inverse(GetHorizontalRotation(rotation)) * delta;
        return Mathf.Abs(local.x) <= size.x * .5f && Mathf.Abs(local.z) <= size.y * .5f;
    }

    /// <summary>Project a terrain orientation onto the horizontal plane for footprint geometry.</summary>
    /// <param name="rotation">World orientation that may include the slope's pitch or roll.</param>
    /// <returns>A yaw-only rotation following the horizontal projection of the local forward axis.</returns>
    public static Quaternion GetHorizontalRotation(Quaternion rotation)
    {
        if (!Finite(rotation) || rotation.x * rotation.x + rotation.y * rotation.y +
            rotation.z * rotation.z + rotation.w * rotation.w < 1e-8f)
            throw new ArgumentOutOfRangeException(nameof(rotation), "Footprint rotation must be finite and non-zero.");
        Vector3 forward = rotation.normalized * Vector3.forward;
        forward = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-8f)
            throw new ArgumentOutOfRangeException(nameof(rotation), "Footprint forward axis must have a horizontal direction.");
        return Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    /// <summary>Create a Unity Terrain hole map that leaves only the slope footprint solid.</summary>
    /// <param name="resolution">Square Terrain holes resolution in cells.</param>
    /// <param name="terrainOrigin">World-space lower corner of the axis-aligned Terrain.</param>
    /// <param name="terrainSize">World dimensions of the Terrain.</param>
    /// <param name="center">World-space center of the oriented slope footprint.</param>
    /// <param name="rotation">World orientation whose horizontal axes define the footprint.</param>
    /// <param name="footprintSize">Footprint width along local X and depth along local Z, in metres.</param>
    /// <returns>A Unity Terrain holes array, where false marks a hole and true keeps the cell solid.</returns>
    public static bool[,] BuildTerrainHoles(int resolution, Vector3 terrainOrigin, Vector3 terrainSize,
        Vector3 center, Quaternion rotation, Vector2 footprintSize)
    {
        if (resolution < 1) throw new ArgumentOutOfRangeException(nameof(resolution));
        if (!Finite(terrainOrigin) || !Finite(terrainSize) || terrainSize.x <= 0f || terrainSize.z <= 0f)
            throw new ArgumentOutOfRangeException(nameof(terrainSize), "Terrain origin and size must be finite and positive.");
        Validate(center, rotation, footprintSize);

        var holes = new bool[resolution, resolution];
        for (int z = 0; z < resolution; z++)
        for (int x = 0; x < resolution; x++)
        {
            // Terrain holes correspond to heightmap cells, so test each cell at its centre.
            Vector3 point = terrainOrigin + new Vector3(
                (x + .5f) / resolution * terrainSize.x, 0f,
                (z + .5f) / resolution * terrainSize.z);
            holes[z, x] = ContainsHorizontal(point, center, rotation, footprintSize);
        }
        return holes;
    }

    private static void Validate(Vector3 center, Quaternion rotation, Vector2 size)
    {
        if (!Finite(center) || !Finite(rotation) || !float.IsFinite(size.x) || !float.IsFinite(size.y) ||
            size.x <= 0f || size.y <= 0f ||
            rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w < 1e-8f)
            throw new ArgumentOutOfRangeException(nameof(size), "Footprint center, rotation, and size must be finite and non-zero.");
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static bool Finite(Quaternion value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);
}
