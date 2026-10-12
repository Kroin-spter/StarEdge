using System;
using UnityEngine;

/// <summary>Local repair of scanned equipment geometry without replacing the surrounding mine contour.</summary>
public static class MapFollowingSoilSurface
{
    /// <summary>Fill equipment footprints harmonically from their untouched neighbouring elevations.</summary>
    /// <param name="source">World elevations indexed by [z,x], sampled before Cesium clipping.</param>
    /// <param name="equipmentMask">Cells containing scanned equipment or its immediate repair seam.</param>
    /// <returns>A new height field; every unmasked cell remains exactly equal to the source.</returns>
    public static float[,] RepairHeights(float[,] source, bool[,] equipmentMask)
    {
        if (source == null || equipmentMask == null || source.GetLength(0) != equipmentMask.GetLength(0) ||
            source.GetLength(1) != equipmentMask.GetLength(1))
            throw new ArgumentException("地表和设备掩码尺寸必须一致。");
        int rows = source.GetLength(0), columns = source.GetLength(1);
        var result = (float[,])source.Clone();
        double boundarySum = 0;
        int boundaryCount = 0;
        for (int z = 1; z < rows - 1; z++)
            for (int x = 1; x < columns - 1; x++)
                if (!equipmentMask[z, x] && (equipmentMask[z-1, x] || equipmentMask[z+1, x] ||
                    equipmentMask[z, x-1] || equipmentMask[z, x+1]))
                { boundarySum += source[z, x]; boundaryCount++; }
        if (boundaryCount == 0) throw new ArgumentException("设备修补范围必须具有未修改的周边地表。");
        float mean = (float)(boundarySum / boundaryCount);
        for (int z = 1; z < rows - 1; z++)
            for (int x = 1; x < columns - 1; x++)
                if (equipmentMask[z, x]) result[z, x] = mean;
        // Boundary-conditioned diffusion removes equipment bumps but retains the actual corner's slope gradient.
        for (int iteration = 0; iteration < 1600; iteration++)
        {
            float change = 0f;
            for (int z = 1; z < rows - 1; z++)
                for (int x = 1; x < columns - 1; x++)
                {
                    if (!equipmentMask[z, x]) continue;
                    float value = (result[z-1, x] + result[z+1, x] + result[z, x-1] + result[z, x+1]) * .25f;
                    change = Mathf.Max(change, Mathf.Abs(value - result[z, x]));
                    result[z, x] = value;
                }
            if (change < .0001f) break;
        }
        return result;
    }

    /// <summary>Find whether a world X/Z point lies inside a convex, counter-clockwise replacement boundary.</summary>
    /// <param name="polygon">World X/Z boundary vertices, without a repeated closing vertex.</param>
    /// <param name="point">World X/Z query position.</param>
    /// <returns>True inside or on the boundary; false for missing polygons or outside positions.</returns>
    public static bool Contains(Vector2[] polygon, Vector2 point)
    {
        if (polygon == null || polygon.Length < 3) return false;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 a = polygon[i], edge = polygon[(i + 1) % polygon.Length] - a;
            Vector2 delta = point - a;
            if (edge.x * delta.y - edge.y * delta.x < -.00001f) return false;
        }
        return true;
    }
}
