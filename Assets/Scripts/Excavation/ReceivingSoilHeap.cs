using System;
using System.Collections.Generic;
using UnityEngine;

// Small conservative height field shared by the truck's rendering and landing tests.
internal sealed class ReceivingSoilHeap
{
    private const int Columns = 13, Rows = 17;
    private readonly Vector2 size;
    private readonly double[] volumes = new double[Columns * Rows];
    private readonly double[] areas = new double[Columns * Rows];
    private readonly double[] weights = new double[Columns * Rows];
    private readonly List<Vector3> surfaceVertices = new List<Vector3>();
    private readonly List<Vector2> surfaceUv = new List<Vector2>();
    private readonly List<int> surfaceTriangles = new List<int>();
    private readonly Vector3[] clipped = new Vector3[4];
    private readonly float dx, dz;

    internal ReceivingSoilHeap(Vector2 size)
    {
        this.size = size;
        dx = size.x / (Columns - 1);
        dz = size.y / (Rows - 1);
        double thirdTriangle = (double)dx * dz / 6;
        for (int z = 0; z < Rows - 1; z++)
            for (int x = 0; x < Columns - 1; x++)
            {
                int a = z * Columns + x, b = a + 1, d = a + Columns, c = d + 1;
                areas[a] += thirdTriangle; areas[b] += 2 * thirdTriangle;
                areas[d] += 2 * thirdTriangle; areas[c] += thirdTriangle;
            }
    }

    internal void Add(double volume, Vector3 local, float ceiling)
    {
        float radius = Mathf.Max(.35f, Mathf.Min(size.x, size.y) * .22f);
        for (int i = 0; i < weights.Length; i++)
        {
            Vector3 p = Point(i);
            double distance = (p.x - local.x) * (p.x - local.x) + (p.z - local.z) * (p.z - local.z);
            weights[i] = Math.Exp(-distance / (2 * radius * radius)) * areas[i];
        }
        // Saturated nodes pass their share to remaining nodes; no soil is minted or discarded.
        for (int pass = 0; pass <= volumes.Length && volume > 1e-12; pass++)
        {
            double sum = 0;
            for (int i = 0; i < volumes.Length; i++)
                if (volumes[i] < ceiling * areas[i] - 1e-14) sum += weights[i];
            if (sum <= 0) break;
            double assigned = 0;
            for (int i = 0; i < volumes.Length; i++)
            {
                double room = Math.Max(0, ceiling * areas[i] - volumes[i]);
                double addition = Math.Min(room, volume * weights[i] / sum);
                volumes[i] += addition;
                assigned += addition;
            }
            volume -= assigned;
        }
        // Local avalanching limits slopes without changing the integral of the mesh.
        for (int pass = 0; pass < 32; pass++)
        {
            bool changed = false;
            for (int z = 0; z < Rows; z++)
                for (int x = 0; x < Columns; x++)
                {
                    int a = z * Columns + x;
                    if (x + 1 < Columns) changed |= Relax(a, a + 1, dx);
                    if (z + 1 < Rows) changed |= Relax(a, a + Columns, dz);
                }
            if (!changed) break;
        }
    }

    private bool Relax(int a, int b, float distance)
    {
        double difference = volumes[a] / areas[a] - volumes[b] / areas[b];
        double excess = Math.Abs(difference) - distance * .624869;
        if (excess <= 0) return false;
        double transfer = excess / (1 / areas[a] + 1 / areas[b]);
        if (difference < 0) transfer = -transfer;
        volumes[a] -= transfer;
        volumes[b] += transfer;
        return true;
    }

    internal float Height(Vector3 local)
    {
        float x = Mathf.Clamp((local.x + size.x * .5f) / dx, 0, Columns - 1.00001f);
        float z = Mathf.Clamp((local.z + size.y * .5f) / dz, 0, Rows - 1.00001f);
        int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z), a = iz * Columns + ix;
        float tx = x - ix, tz = z - iz;
        float ha = H(a), hb = H(a + 1), hd = H(a + Columns), hc = H(a + Columns + 1);
        return tx + tz <= 1 ? ha + (hb - ha) * tx + (hd - ha) * tz
            : hc + (hd - hc) * (1 - tx) + (hb - hc) * (1 - tz);
    }

    private float H(int i) => (float)(volumes[i] / areas[i]);
    private Vector3 Point(int i) => new Vector3(i % Columns * dx - size.x * .5f, H(i),
        i / Columns * dz - size.y * .5f);

    internal bool Intersect(Vector3 start, Vector3 end, out Vector3 landing)
    {
        landing = start;
        // Most loose-soil parcels are nowhere near the truck. Reject their segment bounds
        // before visiting the heap triangles; this also handles horizontal off-bed spills.
        if (Mathf.Min(start.x, end.x) > size.x * .5f || Mathf.Max(start.x, end.x) < -size.x * .5f ||
            Mathf.Min(start.z, end.z) > size.y * .5f || Mathf.Max(start.z, end.z) < -size.y * .5f ||
            Mathf.Max(start.y, end.y) < 0) return false;
        Vector3 direction = end - start;
        float nearest = float.PositiveInfinity;
        for (int z = 0; z < Rows - 1; z++)
            for (int x = 0; x < Columns - 1; x++)
            {
                int a = z * Columns + x, b = a + 1, d = a + Columns, c = d + 1;
                Triangle(Point(a), Point(d), Point(b));
                Triangle(Point(b), Point(d), Point(c));
            }
        landing = float.IsFinite(nearest) ? start + direction * nearest : start;
        return float.IsFinite(nearest);

        // Test every crossed surface triangle, not just endpoint heights: an oblique parcel
        // may enter and leave a mound within one fixed step. Keep the earliest impact.
        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(direction, e2);
            float determinant = Vector3.Dot(e1, p);
            if (Mathf.Abs(determinant) < 1e-10f) return;
            float inverse = 1 / determinant;
            Vector3 offset = start - a;
            float u = Vector3.Dot(offset, p) * inverse;
            if (u < 0 || u > 1) return;
            Vector3 q = Vector3.Cross(offset, e1);
            float v = Vector3.Dot(direction, q) * inverse;
            if (v < 0 || u + v > 1) return;
            float t = Vector3.Dot(e2, q) * inverse;
            if (t >= 0 && t <= 1 && t < nearest) nearest = t;
        }
    }

    // Clip only the visual surface. The closed mesh and the landing height field retain
    // every deposit, including soil thinner than the 1 mm floor clearance.
    internal void BuildSurface(Mesh mesh)
    {
        const float clearance = .001f;
        surfaceVertices.Clear();
        surfaceUv.Clear();
        surfaceTriangles.Clear();
        for (int z = 0; z < Rows - 1; z++)
            for (int x = 0; x < Columns - 1; x++)
            {
                int a = z * Columns + x, b = a + 1, d = a + Columns, c = d + 1;
                Triangle(Point(a), Point(d), Point(b));
                Triangle(Point(b), Point(d), Point(c));
                if (z == 0) Side(Point(a), Point(b));
                if (z == Rows - 2) Side(Point(c), Point(d));
                if (x == 0) Side(Point(d), Point(a));
                if (x == Columns - 2) Side(Point(b), Point(c));
            }
        mesh.Clear();
        mesh.SetVertices(surfaceVertices);
        mesh.SetUVs(0, surfaceUv);
        mesh.SetTriangles(surfaceTriangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        void Side(Vector3 a, Vector3 b)
        {
            Vector3 bottomA = new Vector3(a.x, 0, a.z);
            Vector3 bottomB = new Vector3(b.x, 0, b.z);
            Triangle(a, b, bottomA);
            Triangle(b, bottomB, bottomA);
        }

        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            int count = 0;
            Edge(c, a);
            Edge(a, b);
            Edge(b, c);
            if (count < 3) return;
            int first = surfaceVertices.Count;
            for (int i = 0; i < count; i++)
            {
                surfaceVertices.Add(clipped[i]);
                surfaceUv.Add(new Vector2(clipped[i].x, clipped[i].z));
            }
            for (int i = 1; i < count - 1; i++)
            {
                surfaceTriangles.Add(first);
                surfaceTriangles.Add(first + i);
                surfaceTriangles.Add(first + i + 1);
            }

            void Edge(Vector3 from, Vector3 to)
            {
                bool fromInside = from.y >= clearance, toInside = to.y >= clearance;
                if (fromInside != toInside)
                {
                    Vector3 crossing = Vector3.Lerp(from, to, (clearance - from.y) / (to.y - from.y));
                    crossing.y = clearance;
                    clipped[count++] = crossing;
                }
                if (toInside) clipped[count++] = to;
            }
        }
    }

    internal void Build(Mesh mesh)
    {
        int count = volumes.Length;
        var vertices = new Vector3[count * 2];
        var uv = new Vector2[count * 2];
        var triangles = new List<int>();
        for (int i = 0; i < count; i++)
        {
            vertices[i] = Point(i);
            vertices[i + count] = new Vector3(vertices[i].x, 0, vertices[i].z);
            uv[i] = uv[i + count] = new Vector2(vertices[i].x, vertices[i].z);
        }
        for (int z = 0; z < Rows - 1; z++)
            for (int x = 0; x < Columns - 1; x++)
            {
                int a = z * Columns + x, b = a + 1, d = a + Columns, c = d + 1;
                triangles.AddRange(new[] { a, d, b, b, d, c,
                    a + count, b + count, d + count, b + count, c + count, d + count });
                if (z == 0) Side(a, b);
                if (z == Rows - 2) Side(c, d);
                if (x == 0) Side(d, a);
                if (x == Columns - 2) Side(b, c);
            }
        mesh.Clear();
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        void Side(int a, int b) => triangles.AddRange(new[] { a, b, a + count, b, b + count, a + count });
    }
}
