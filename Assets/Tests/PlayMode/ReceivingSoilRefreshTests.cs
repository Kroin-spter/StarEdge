using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public class ReceivingSoilRefreshTests
    {
        private GameObject receiver, bucket;

        /// <summary>Release receiver meshes and the receipt source after each regression.</summary>
        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(receiver);
            Object.DestroyImmediate(bucket);
        }

        /// <summary>Catch per-parcel mesh rebuilds without allowing deferred landing or accounting.</summary>
        /// <returns>Frames in which the receiver must publish accumulated deposits.</returns>
        [UnityTest]
        public IEnumerator ParcelsUpdateLandingImmediatelyButPublishMeshOnceAtEndOfFrame()
        {
            receiver = new GameObject("batched receiving bed");
            var zone = receiver.AddComponent<SoilReceivingZone>();
            bucket = new GameObject("receipt source");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.enabled = false;
            Accept(zone, cutter, .002);
            yield return null;

            var filter = receiver.GetComponent<MeshFilter>();
            Assert.That(filter.sharedMesh, Is.Not.Null);
            var before = filter.sharedMesh.vertices;
            Assert.That(zone.TryGetLanding(Vector3.up * 2, 3, out var firstLanding), Is.True);
            for (int i = 0; i < 8; i++) Accept(zone, cutter, .002);

            Assert.That(zone.VolumeM3, Is.EqualTo(.018).Within(1e-10));
            Assert.That(zone.Records.Count, Is.EqualTo(9));
            Assert.That(zone.MassKg, Is.EqualTo(.018 * cutter.SoilDensityKgPerM3).Within(1e-8));
            Assert.That(zone.TryGetLanding(Vector3.up * 2, 3, out var latestLanding), Is.True);
            Assert.That(latestLanding.y, Is.GreaterThan(firstLanding.y));
            CollectionAssert.AreEqual(before, filter.sharedMesh.vertices,
                "Accept must leave the displayed mesh unchanged until LateUpdate.");

            yield return null;
            Assert.That(filter.sharedMesh.bounds.max.y, Is.EqualTo(latestLanding.y).Within(1e-5));
            Assert.That(Volume(filter.sharedMesh), Is.EqualTo(.018).Within(1e-6));
        }

        private static void Accept(SoilReceivingZone zone, BucketSoilCutter cutter, double volume)
        {
            var method = typeof(SoilReceivingZone).GetMethod("Accept",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(BucketSoilCutter), typeof(double), typeof(Vector3) }, null);
            Assert.That((double)method.Invoke(zone, new object[] { cutter, volume, zone.transform.position }),
                Is.EqualTo(volume).Within(1e-10));
        }

        private static double Volume(Mesh mesh)
        {
            double volume = 0;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
                volume += Vector3.Dot(vertices[triangles[i]], Vector3.Cross(
                    vertices[triangles[i + 1]], vertices[triangles[i + 2]])) / 6.0;
            return volume;
        }
    }
}
