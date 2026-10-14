using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class BucketSoilFillTests
    {
        /// <summary>The enlarged cavity must display a real 0.33m³ load, not silently clamp to the old cavity.</summary>
        [Test]
        public void EnlargedCavityDisplaysRequestedLoadWithoutClamping()
        {
            var bucket = new GameObject("larger real load");
            try
            {
                var fill = bucket.AddComponent<BucketSoilFill>();
                fill.SetVolume(.33);
                Assert.That(SignedVolume(bucket.GetComponent<MeshFilter>().sharedMesh), Is.EqualTo(.33).Within(1e-6));
            }
            finally { UnityEngine.Object.DestroyImmediate(bucket); }
        }

        [Test]
        public void CalibratedFillDoesNotIntersectActualBucketShell()
        {
            var robot = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Robotics/zx200_description/Prefabs/zx200.prefab"));
            var probe = new GameObject("soil shell probe");
            try
            {
                var body = robot.GetComponent<Zx200DigController>().bucketJoint;
                var visual = new GameObject("soil");
                visual.transform.SetParent(body.transform, false);
                var fill = visual.AddComponent<BucketSoilFill>();
                fill.SetVolume(fill.CapacityM3);
                var sphere = probe.AddComponent<SphereCollider>();
                sphere.radius = .003f;
                Physics.SyncTransforms();
                var mesh = visual.GetComponent<MeshFilter>().sharedMesh;
                var samples = new HashSet<Vector3>(mesh.vertices);
                var vertices = mesh.vertices;
                var indices = mesh.triangles;
                for (int i = 0; i < indices.Length; i += 3)
                    samples.Add((vertices[indices[i]] + vertices[indices[i + 1]] + vertices[indices[i + 2]]) / 3);
                var intersections = new List<string>();
                foreach (var vertex in samples)
                {
                    Vector3 world = body.transform.TransformPoint(vertex);
                    foreach (var shell in body.GetComponentsInChildren<Collider>())
                    {
                        if (shell.attachedArticulationBody != body || shell.isTrigger || !shell.enabled) continue;
                        bool overlap = Physics.ComputePenetration(sphere, world, Quaternion.identity,
                            shell, shell.transform.position, shell.transform.rotation, out _, out float depth);
                        if (overlap && depth > .001f)
                            intersections.Add($"Fill intersects bucket shell at {vertex}, depth {depth}");
                    }
                }
                Assert.That(intersections, Is.Empty, string.Join("\n", intersections));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
                UnityEngine.Object.DestroyImmediate(robot);
            }
        }

        [TestCase(.1)]
        [TestCase(.5)]
        [TestCase(1.0)]
        public void FillIsClosedAndItsGeometricFractionMatchesMeasuredFraction(double fraction)
        {
            var bucket = new GameObject("bucket fill test");
            try
            {
                Type type = typeof(BucketSoilCutter).Assembly.GetType("BucketSoilFill");
                Assert.That(type, Is.Not.Null, "A bounded cavity mesh must replace the scaled cube.");
                var fill = bucket.AddComponent(type);
                double capacity = (double)type.GetProperty("CapacityM3").GetValue(fill);
                type.GetMethod("SetVolume").Invoke(fill, new object[] { capacity });
                var filter = bucket.GetComponentInChildren<MeshFilter>(true);
                Assert.That(filter, Is.Not.Null);
                double full = SignedVolume(filter.sharedMesh);
                Vector3 fullCenter = CenterOfVolume(filter.sharedMesh);
                Assert.That(full, Is.GreaterThan(.01));
                Assert.That(full, Is.EqualTo(capacity).Within(1e-6));
                type.GetMethod("SetVolume").Invoke(fill, new object[] { capacity * fraction });
                Assert.That(SignedVolume(filter.sharedMesh) / full, Is.EqualTo(fraction).Within(.005));
                if (fraction < 1)
                    Assert.That(Vector3.Dot(CenterOfVolume(filter.sharedMesh) - fullCenter,
                        new Vector3(0, -1.25f, .82f).normalized), Is.LessThan(0),
                        "Small loads must start deep in the cavity, not form a shelf at the mouth.");
                foreach (var vertex in filter.sharedMesh.vertices)
                {
                    Assert.That(float.IsFinite(vertex.x + vertex.y + vertex.z), Is.True);
                    Assert.That(Mathf.Abs(vertex.x), Is.LessThanOrEqualTo(.46f));
                    Assert.That(vertex.y, Is.InRange(.1f, 1.3f));
                    Assert.That(vertex.z, Is.InRange(-.2f, 1.25f));
                }
                Assert.That(bucket.GetComponentsInChildren<Collider>(true), Is.Empty);
                Assert.That(bucket.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
                type.GetMethod("SetVolume").Invoke(fill, new object[] { 0.0 });
                Assert.That(filter.GetComponent<Renderer>().enabled, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(bucket); }
        }

        private static double SignedVolume(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            double total = 0;
            for (int i = 0; i < triangles.Length; i += 3)
                total += Vector3.Dot(vertices[triangles[i]],
                    Vector3.Cross(vertices[triangles[i + 1]], vertices[triangles[i + 2]])) / 6.0;
            return total;
        }

        private static Vector3 CenterOfVolume(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            Vector3 moment = Vector3.zero;
            double volume = 0;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                float part = Vector3.Dot(a, Vector3.Cross(b, c)) / 6;
                volume += part;
                moment += (a + b + c) * (part / 4);
            }
            return moment / (float)volume;
        }
    }
}
