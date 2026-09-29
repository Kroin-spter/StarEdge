using UnityEngine;

/// <summary>Extract the first rising cut face above the lower bench from Cesium height samples.</summary>
public static class ConstructionSiteSlopeMask
{
    /// <summary>Build a soil mask that follows the lower-to-upper bench transition in each terrain column.</summary>
    /// <param name="ground">World-space Cesium ground elevations indexed by [z, x].</param>
    /// <param name="cellSizeZ">Distance between adjacent rows in metres.</param>
    /// <param name="lowerBenchY">Surveyed lower-bench elevation in world metres.</param>
    /// <returns>Normalized soil thickness mask; zero leaves the original map untouched.</returns>
    public static float[,] Build(float[,] ground, float cellSizeZ, float lowerBenchY)
    {
        int rows = ground.GetLength(0), columns = ground.GetLength(1);
        var result = new float[rows, columns];
        int oneMetre = Mathf.Max(1, Mathf.RoundToInt(1f / cellSizeZ));
        int fourMetres = Mathf.Max(oneMetre + 1, Mathf.RoundToInt(4f / cellSizeZ));
        int twoMetres = Mathf.Max(2, Mathf.RoundToInt(2f / cellSizeZ));
        for (int x = 0; x < columns; x++)
        {
            int start = -1;
            for (int z = oneMetre; z < rows - fourMetres; z++)
            {
                if (ground[z, x] > lowerBenchY + 1.2f ||
                    ground[z + fourMetres, x] - ground[z, x] < 2f) continue;
                start = z;
                break;
            }
            if (start < 0) continue;

            int end = Mathf.Min(rows - oneMetre - 1, start + Mathf.RoundToInt(17f / cellSizeZ));
            for (int z = start + fourMetres; z < end - twoMetres; z++)
            {
                if (ground[z, x] - ground[start, x] < 2f ||
                    ground[z + twoMetres, x] - ground[z, x] > .35f) continue;
                end = z;
                break;
            }
            for (int z = start; z <= end; z++)
            {
                float risePerMetre = (ground[Mathf.Min(rows - 1, z + oneMetre), x] -
                    ground[Mathf.Max(0, z - oneMetre), x]) / (2f * oneMetre * cellSizeZ);
                float slope = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.35f, .8f, risePerMetre));
                float lowerEdge = Mathf.Clamp01((z - start) * cellSizeZ / .5f);
                float upperEdge = Mathf.Clamp01((end - z) * cellSizeZ / .5f);
                result[z, x] = slope * lowerEdge * upperEdge;
            }
        }
        return result;
    }
}
