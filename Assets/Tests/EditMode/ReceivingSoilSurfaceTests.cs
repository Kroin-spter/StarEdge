using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class ReceivingSoilSurfaceTests
    {
        private GameObject receiver, bucket;

        /// <summary>Release the generated meshes and receipt source.</summary>
        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(receiver);
            Object.DestroyImmediate(bucket);
        }

        /// <summary>Catch floor-coplanar rendering while preserving immediate closed-mesh volume and landing.</summary>
        [Test]
        public void ThinTruckDepositRendersOnlyAboveFloorWithoutChangingPhysicalHeap()
        {
            receiver = new GameObject("thin receiving bed");
            var zone = receiver.AddComponent<SoilReceivingZone>();
            bucket = new GameObject("receipt source");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.enabled = false;
            var accept = typeof(SoilReceivingZone).GetMethod("Accept",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(BucketSoilCutter), typeof(double), typeof(Vector3) }, null);
            Assert.That((double)accept.Invoke(zone, new object[] { cutter, .02, Vector3.zero }),
                Is.EqualTo(.02).Within(1e-10));

            var closed = receiver.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(closed, Is.Not.Null, "Edit Mode must still refresh immediately.");
            double volume = 0;
            var vertices = closed.vertices;
            var triangles = closed.triangles;
            bool hasFloor = false;
            foreach (var vertex in vertices) hasFloor |= vertex.y == 0;
            for (int i = 0; i < triangles.Length; i += 3)
                volume += Vector3.Dot(vertices[triangles[i]], Vector3.Cross(
                    vertices[triangles[i + 1]], vertices[triangles[i + 2]])) / 6.0;
            Assert.That(hasFloor, Is.True, "Keep the physical mesh closed at the original floor.");
            Assert.That(volume, Is.EqualTo(.02).Within(1e-6));
            Assert.That(zone.TryGetLanding(Vector3.up * 2, 3, out var landing), Is.True);
            Assert.That(landing.y, Is.EqualTo(closed.bounds.max.y).Within(1e-5));

            int renderedTriangles = 0;
            foreach (var renderer in receiver.GetComponentsInChildren<MeshRenderer>())
            {
                if (!renderer.enabled) continue;
                var rendered = renderer.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(rendered, Is.Not.Null);
                var points = rendered.vertices;
                foreach (int index in rendered.triangles)
                {
                    Vector3 local = receiver.transform.InverseTransformPoint(
                        renderer.transform.TransformPoint(points[index]));
                    Assert.That(local.y, Is.GreaterThanOrEqualTo(.00099f),
                        "Floor and near-coplanar thin fringes must not be rendered.");
                }
                renderedTriangles += rendered.triangles.Length / 3;
            }
            Assert.That(renderedTriangles, Is.GreaterThan(0), "A visible mound must remain.");
        }

        /// <summary>Catch a stale child material when the parent material is assigned after creation.</summary>
        [Test]
        public void TruckSurfaceTracksParentMaterialAssignedAfterAwake()
        {
            var zone = CreateTruck();
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            try
            {
                receiver.GetComponent<MeshRenderer>().sharedMaterial = material;
                typeof(SoilReceivingZone).GetMethod("LateUpdate",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(zone, null);
                int visible = 0;
                foreach (var renderer in receiver.GetComponentsInChildren<MeshRenderer>())
                {
                    if (!renderer.enabled) continue;
                    visible++;
                    Assert.That(renderer.sharedMaterial, Is.SameAs(material));
                }
                Assert.That(visible, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(material); }
        }

        /// <summary>Catch a ghost truck surface after a zone is configured as a ground pile.</summary>
        [Test]
        public void GroundPileDisablesExistingTruckSurfaceAndRemainsExcavatable()
        {
            var zone = CreateTruck();
            zone.isGroundPile = true;
            typeof(SoilReceivingZone).GetMethod("RefreshMesh",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(zone, null);
            Assert.That(receiver.GetComponent<MeshRenderer>().enabled, Is.True);
            foreach (var renderer in receiver.GetComponentsInChildren<MeshRenderer>())
                if (renderer.gameObject != receiver) Assert.That(renderer.enabled, Is.False);
            Assert.That(zone.ExcavateLoose(new Vector3(.6f, .03f, 0), Vector3.right * 1.2f, .005),
                Is.EqualTo(.005).Within(1e-10));
            Assert.That(zone.VolumeM3, Is.EqualTo(.015).Within(1e-10));
        }

        private SoilReceivingZone CreateTruck()
        {
            receiver = new GameObject("receiving bed");
            var zone = receiver.AddComponent<SoilReceivingZone>();
            bucket = new GameObject("receipt source");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.enabled = false;
            var accept = typeof(SoilReceivingZone).GetMethod("Accept",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(BucketSoilCutter), typeof(double), typeof(Vector3) }, null);
            Assert.That((double)accept.Invoke(zone, new object[] { cutter, .02, Vector3.zero }),
                Is.EqualTo(.02).Within(1e-10));
            return zone;
        }
    }
}
