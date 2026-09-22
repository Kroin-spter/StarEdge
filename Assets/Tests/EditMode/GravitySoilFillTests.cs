using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class GravitySoilFillTests
    {
        [Test]
        public void GravityPlaneCanChangeWithoutChangingContainedMeshVolume()
        {
            var go = new GameObject("gravity fill");
            try
            {
                var fill = go.AddComponent<BucketSoilFill>();
                var method = typeof(BucketSoilFill).GetMethod("SetSurfaceNormal");
                Assert.That(method, Is.Not.Null, "Missing gravity surface interface");
                double volume = fill.CapacityM3 * .35;
                fill.SetVolume(volume);
                foreach (var normal in new[] { Vector3.up, new Vector3(.5f, .8f, .3f).normalized, Vector3.down })
                {
                    method.Invoke(fill, new object[] { normal });
                    var mesh = go.GetComponent<MeshFilter>().sharedMesh;
                    double integral = 0;
                    var v = mesh.vertices;
                    var t = mesh.triangles;
                    for (int i = 0; i < t.Length; i += 3)
                        integral += Vector3.Dot(v[t[i]], Vector3.Cross(v[t[i + 1]], v[t[i + 2]])) / 6.0;
                    Assert.That(integral, Is.EqualTo(volume).Within(1e-6));
                    foreach (var point in v) Assert.That(Mathf.Abs(point.x), Is.LessThanOrEqualTo(fill.innerHalfWidth + 1e-5));
                    float top = float.NegativeInfinity;
                    foreach (var point in v) top = Mathf.Max(top, Vector3.Dot(point, normal));
                    double topArea = 0;
                    for (int i = 0; i < t.Length; i += 3)
                    {
                        Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                        if (Mathf.Abs(Vector3.Dot(a, normal) - top) > 1e-5f
                            || Mathf.Abs(Vector3.Dot(b, normal) - top) > 1e-5f
                            || Mathf.Abs(Vector3.Dot(c, normal) - top) > 1e-5f) continue;
                        Vector3 cross = Vector3.Cross(b - a, c - a);
                        if (cross.sqrMagnitude < 1e-12f) continue;
                        Assert.That(Vector3.Dot(cross.normalized, normal), Is.GreaterThan(.999f));
                        topArea += cross.magnitude * .5;
                    }
                    Assert.That(topArea, Is.GreaterThan(.001));
                }
            }
            finally { Object.DestroyImmediate(go); }
        }
        [TestCase(.1)]
        [TestCase(.5)]
        [TestCase(1.0)]
        public void RolledAndPitchedFillStaysInsideCalibratedProfile(double fraction)
        {
            var go = new GameObject("gravity containment");
            try
            {
                var fill = go.AddComponent<BucketSoilFill>();
                double volume = fill.CapacityM3 * fraction;
                fill.SetVolume(volume);
                var normals = new[] {
                    Vector3.up, Vector3.down, Vector3.right, Vector3.left,
                    Vector3.forward, Vector3.back, fill.OpeningNormalLocal, -fill.OpeningNormalLocal,
                    new Vector3(.5f, .8f, .3f), new Vector3(-.7f, .2f, .9f),
                    new Vector3(.4f, -.6f, -.8f), new Vector3(.03f, .96f, .28f)
                };
                foreach (Vector3 normal in normals)
                {
                    fill.SetSurfaceNormal(normal);
                    var mesh = go.GetComponent<MeshFilter>().sharedMesh;
                    var vertices = mesh.vertices;
                    var triangles = mesh.triangles;
                    string context = $"fraction={fraction}, up={normal}";
                    foreach (Vector3 point in vertices) AssertContained(fill, point, context);
                    double integral = 0;
                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                        AssertContained(fill, (a + b + c) / 3, context + " triangle centroid");
                        AssertContained(fill, (a + b) / 2, context + " triangle edge");
                        AssertContained(fill, (b + c) / 2, context + " triangle edge");
                        AssertContained(fill, (c + a) / 2, context + " triangle edge");
                        integral += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
                    }
                    Assert.That(integral, Is.EqualTo(volume).Within(1e-6), context);
                }
            }
            finally { Object.DestroyImmediate(go); }
        }

        private static void AssertContained(BucketSoilFill fill, Vector3 point, string context)
        {
            Assert.That(Mathf.Abs(point.x), Is.LessThanOrEqualTo(fill.innerHalfWidth + 1e-5f), context);
            Vector2 sample = new Vector2(point.y, point.z);
            bool inside = false;
            // Ray parity works for the calibrated outline's shallow reflex corner too.
            for (int i = 0; i < fill.cavityProfile.Length; i++)
            {
                Vector2 a = fill.cavityProfile[i];
                Vector2 b = fill.cavityProfile[(i + 1) % fill.cavityProfile.Length];
                Vector2 edge = b - a;
                float along = Mathf.Clamp01(Vector2.Dot(sample - a, edge) / edge.sqrMagnitude);
                if (Vector2.Distance(sample, a + edge * along) <= 1e-5f) return;
                if ((a.y > sample.y) != (b.y > sample.y)
                    && sample.x < a.x + (sample.y - a.y) * (b.x - a.x) / (b.y - a.y))
                    inside = !inside;
            }
            Assert.That(inside, Is.True, $"{context}: {point} lies outside calibrated cavity");
        }

        [Test]
        public void LowestMouthLipLimitsRetentionAndDownwardOpeningEmpties()
        {
            var go = new GameObject("retained fill");
            try
            {
                var fill = go.AddComponent<BucketSoilFill>();
                double capacity = fill.CapacityM3;
                double upright = fill.RetainedCapacityM3(fill.OpeningNormalLocal);
                double inverted = fill.RetainedCapacityM3(-fill.OpeningNormalLocal);
                Assert.That(upright, Is.GreaterThan(capacity * .5));
                Assert.That(upright, Is.LessThanOrEqualTo(capacity));
                Assert.That(inverted, Is.EqualTo(0));
                Assert.That(fill.CapacityM3, Is.EqualTo(capacity));
                Assert.Throws<System.ArgumentOutOfRangeException>(() => fill.SetSurfaceNormal(Vector3.zero));
                Assert.Throws<System.ArgumentOutOfRangeException>(() => fill.SetSurfaceNormal(new Vector3(float.NaN, 0, 0)));
                Assert.Throws<System.ArgumentOutOfRangeException>(() => fill.RetainedCapacityM3(new Vector3(0, float.PositiveInfinity, 0)));
            }
            finally { Object.DestroyImmediate(go); }
        }

    }
}
