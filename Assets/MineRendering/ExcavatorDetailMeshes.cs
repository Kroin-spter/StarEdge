using System.Collections.Generic;
using UnityEngine;

/// <summary>Small link-local hardware meshes; no primitive GameObjects or collision components.</summary>
internal static class ExcavatorDetailMeshes
{
    internal sealed class Builder
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<int> triangles = new List<int>();

        internal void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            int offset = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c);
            triangles.Add(offset); triangles.Add(offset + 1); triangles.Add(offset + 2);
        }

        internal void Cylinder(Vector3 center, Vector3 axis, float radius, float length, int sides)
        {
            axis.Normalize();
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < .9f ? Vector3.up : Vector3.forward).normalized;
            Vector3 v = Vector3.Cross(axis,u);
            Vector3 a = center - axis * (length * .5f), b = center + axis * (length * .5f);
            for (int i = 0; i < sides; i++)
            {
                float angle = i * Mathf.PI * 2 / sides, next = (i + 1) * Mathf.PI * 2 / sides;
                Vector3 p = (u * Mathf.Cos(angle) + v * Mathf.Sin(angle)) * radius;
                Vector3 q = (u * Mathf.Cos(next) + v * Mathf.Sin(next)) * radius;
                Triangle(a + p, a + q, b + p);
                Triangle(b + p, a + q, b + q);
                Triangle(a, a + q, a + p);
                Triangle(b, b + p, b + q);
            }
        }

        internal Mesh Finish(string name)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles,0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    internal static Mesh JointHardware(Vector3 end, float halfWidth)
    {
        var mesh = new Builder();
        foreach (Vector3 joint in new[] { Vector3.zero, end })
            foreach (float side in new[] { -1f, 1f })
            {
                Vector3 center = joint + Vector3.right * side * halfWidth;
                mesh.Cylinder(center, Vector3.right, .115f, .045f, 16);
                mesh.Cylinder(center + Vector3.right * side * .033f, Vector3.right, .064f, .035f, 6);
                for (int i = 0; i < 4; i++)
                {
                    float angle = i * Mathf.PI * .5f + .25f;
                    mesh.Cylinder(center + new Vector3(side * .029f, Mathf.Cos(angle)*.086f,
                        Mathf.Sin(angle)*.086f), Vector3.right, .014f, .025f, 6);
                }
            }
        return mesh.Finish("Joint pin flanges and retaining bolts");
    }

    internal static Mesh Hoses(Vector3 end, float sideOffset, float arch)
    {
        var mesh = new Builder();
        for (int hose = 0; hose < 2; hose++)
        {
            Vector3 previous = HosePoint(end,.12f,sideOffset + hose * .047f,arch);
            for (int i = 1; i <= 20; i++)
            {
                Vector3 next = HosePoint(end,Mathf.Lerp(.12f,.9f,i/20f),sideOffset + hose * .047f,arch);
                mesh.Cylinder((previous+next)*.5f,next-previous,.017f,Vector3.Distance(previous,next)+.006f,8);
                previous = next;
            }
        }
        return mesh.Finish("Paired hydraulic supply hoses");
    }

    private static Vector3 HosePoint(Vector3 end, float t, float sideOffset, float arch)
        => Vector3.Lerp(Vector3.zero,end,t) + Vector3.right * sideOffset +
            Vector3.up * (.13f + Mathf.Sin(t*Mathf.PI)*arch);
}
