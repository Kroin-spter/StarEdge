using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class SoilUnloadingTests
    {
        private GameObject ground, bucket, receiver;
        private TerrainData data;
        private BucketSoilCutter cutter;

        [SetUp]
        public void SetUp()
        {
            data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++) heights[z, x] = .5f;
            data.SetHeights(0, 0, heights);
            ground = Terrain.CreateTerrainGameObject(data);
            var soil = ground.AddComponent<DeformableSoilTerrain>();
            soil.Initialize(ground.GetComponent<Terrain>());
            bucket = new GameObject("source bucket");
            cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .3f;
            cutter.SamplePose();
            Assert.That(cutter.VolumeM3, Is.GreaterThan(.02));
            receiver = new GameObject("receiving bed");
        }

        [TearDown]
        public void TearDown()
        {
            if (cutter != null) cutter.ResetExcavation();
            Object.DestroyImmediate(receiver);
            Object.DestroyImmediate(bucket);
            Object.DestroyImmediate(ground);
            Object.DestroyImmediate(data);
        }

        [Test]
        public void TipInsideExternalSolidCannotUnloadThroughIt()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            receiver.transform.position = new Vector3(10, 0, 0);
            bucket.transform.position = new Vector3(10, 2, 0);
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.receivers = new[] { zone };
            unloader.openingNormalLocal = Vector3.down;
            unloader.dwellSeconds = 0;
            var obstruction = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                obstruction.transform.position = bucket.transform.position;
                Physics.SyncTransforms();
                double initial = cutter.VolumeM3;
                Assert.That(unloader.Step(1), Is.Zero);
                Assert.That(cutter.VolumeM3, Is.EqualTo(initial));
                Assert.That(zone.VolumeM3, Is.Zero);
            }
            finally { Object.DestroyImmediate(obstruction); }
        }

        [Test]
        public void MultipleReceiversAndSharedTerrainResetDoNotDuplicateMaterial()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            var secondReceiver = new GameObject("second receiver");
            var secondBucket = new GameObject("same terrain bucket");
            try
            {
                var otherZone = secondReceiver.AddComponent<SoilReceivingZone>();
                var other = secondBucket.AddComponent<BucketSoilCutter>();
                other.soil = cutter.soil;
                other.tipLocalPosition = Vector3.zero;
                other.cutDirectionLocal = Vector3.forward;
                secondBucket.transform.position = new Vector3(1, .6f, 1);
                other.SamplePose();
                secondBucket.transform.position += Vector3.forward * .3f;
                other.SamplePose();
                cutter.TransferTo(zone, .01);
                cutter.TransferTo(otherZone, .01);
                other.TransferTo(otherZone, .01);
                Assert.That(cutter.soil.RecalculateRemovedVolumeM3(),
                    Is.EqualTo(cutter.VolumeM3 + other.VolumeM3 + zone.VolumeM3 + otherZone.VolumeM3).Within(1e-5));
                secondBucket.SetActive(false);
                cutter.ResetExcavation();
                Assert.That(other.VolumeM3 + zone.VolumeM3 + otherZone.VolumeM3, Is.Zero);
                Assert.That(other.DeliveredVolumeM3 + cutter.DeliveredVolumeM3, Is.Zero);
                Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(1e-6));
            }
            finally { Object.DestroyImmediate(secondBucket); Object.DestroyImmediate(secondReceiver); }
        }

        [Test]
        public void ReceivingFootprintHeightAndOrientationRejectInvalidTargets()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            Assert.That(zone.TryGetLanding(new Vector3(0, 2, 0), 3, out _), Is.True);
            Assert.That(zone.TryGetLanding(new Vector3(4, 2, 0), 3, out _), Is.False);
            Assert.That(zone.TryGetLanding(new Vector3(0, -.1f, 0), 3, out _), Is.False);
            Assert.That(zone.TryGetLanding(new Vector3(0, 4, 0), 3, out _), Is.False);
            receiver.transform.rotation = Quaternion.Euler(30, 0, 0);
            Assert.That(zone.TryGetLanding(new Vector3(0, 2, 0), 3, out _), Is.False);
            receiver.transform.rotation = Quaternion.identity;
            receiver.transform.localScale = Vector3.one * 2;
            Assert.That(zone.TryGetLanding(new Vector3(0, 2, 0), 3, out _), Is.False);
            double before = cutter.VolumeM3;
            Assert.That(cutter.TransferTo(zone, double.NaN), Is.Zero);
            Assert.That(cutter.TransferTo(zone, double.PositiveInfinity), Is.Zero);
            Assert.That(cutter.TransferTo(zone, -1), Is.Zero);
            Assert.That(cutter.TransferTo(null, 1), Is.Zero);
            Assert.That(cutter.VolumeM3, Is.EqualTo(before));
        }

        /// <summary>Retain pose hysteresis and dwell while allowing discharge outside a receiver footprint.</summary>
        [Test]
        public void GravityGateDwellAndFreeDischargeConserveSoil()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            receiver.transform.position = new Vector3(10, 0, 0);
            bucket.transform.position = new Vector3(10, 2, 0);
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.receivers = new[] { zone };
            unloader.openingNormalLocal = Vector3.down;
            unloader.dwellSeconds = .25f;
            unloader.flowM3PerSecond = .01f;
            double initial = cutter.VolumeM3;
            bucket.transform.rotation = Quaternion.Euler(180, 0, 0);
            Assert.That(unloader.Step(1), Is.Zero);
            bucket.transform.rotation = Quaternion.identity;
            Assert.That(unloader.Step(.2f), Is.Zero);
            Assert.That(unloader.State, Is.EqualTo(BucketSoilUnloader.UnloadingState.ReadyToDump));
            double emitted = unloader.Step(.1f);
            Assert.That(emitted, Is.GreaterThan(0).And.LessThanOrEqualTo(.000501),
                "Only the post-dwell portion may flow, scaled by fullness");
            Assert.That(unloader.AirborneVolumeM3, Is.EqualTo(emitted).Within(1e-10));
            Assert.That(zone.VolumeM3, Is.Zero, "Emission must not teleport soil into the bed");
            bucket.transform.rotation = Quaternion.Euler(70, 0, 0);
            Assert.That(unloader.Step(.1f), Is.GreaterThan(0), "Hysteresis must retain the active gate");
            bucket.transform.position += Vector3.right * 5;
            Assert.That(unloader.Step(1), Is.GreaterThan(0), "Missing a receiver must not close the gravity gate");
            Assert.That(cutter.VolumeM3 + unloader.AirborneVolumeM3 + LandedVolume(),
                Is.EqualTo(initial).Within(1e-10));
            bucket.transform.position -= Vector3.right * 5;
            Assert.That(unloader.Step(.1f), Is.GreaterThan(0), "Receiver reentry must not restart pose dwell");
            bucket.transform.rotation = Quaternion.Euler(85, 0, 0);
            Assert.That(unloader.Step(1), Is.Zero);
            bucket.transform.rotation = Quaternion.Euler(70, 0, 0);
            Assert.That(unloader.Step(1), Is.Zero, "Must reach the entry angle after closing");
            bucket.transform.rotation = Quaternion.identity;
            Assert.That(unloader.Step(.2f), Is.Zero, "Closing the pose gate must restart dwell");
            Assert.That(unloader.Step(.1f), Is.GreaterThan(0));
            Assert.That(cutter.VolumeM3 + unloader.AirborneVolumeM3 + LandedVolume(),
                Is.EqualTo(initial).Within(1e-10));
        }

        /// <summary>Solids below the outlet catch falling soil, and every emitted parcel remains accounted for.</summary>
        [Test]
        public void EmptyTerminationInvalidTimeAndBlockersDoNotLoseSoil()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            receiver.transform.position = new Vector3(10, 0, 0);
            bucket.transform.position = new Vector3(10, 2, 0);
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.receivers = new[] { zone };
            unloader.openingNormalLocal = Vector3.down;
            unloader.dwellSeconds = 0;
            double initial = cutter.VolumeM3;
            Assert.That(unloader.Step(float.NaN), Is.Zero);
            Assert.That(unloader.Step(float.PositiveInfinity), Is.Zero);
            Assert.That(unloader.Step(-1), Is.Zero);
            Assert.That(unloader.Step(0), Is.Zero);
            Assert.That(unloader.Step(2), Is.Zero);
            Assert.That(cutter.VolumeM3, Is.EqualTo(initial));
            Assert.That(unloader.AirborneVolumeM3 + zone.VolumeM3, Is.Zero);
            double caught = 0;
            var obstruction = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                obstruction.transform.position = new Vector3(10, 1, 0);
                Physics.SyncTransforms();
                double emitted = unloader.Step(.1f);
                Assert.That(emitted, Is.GreaterThan(0), "A solid below the tip must not suppress emission");
                Assert.That(unloader.AirborneVolumeM3, Is.EqualTo(emitted).Within(1e-10));
                Assert.That(zone.VolumeM3, Is.Zero);
                bucket.transform.rotation = Quaternion.Euler(180, 0, 0);
                Assert.That(unloader.Step(1), Is.Zero);
                caught = LandedVolume(true);
                Assert.That(caught, Is.EqualTo(emitted).Within(1e-10), "The nearest solid must catch the flight as a ground pile");
                Assert.That(unloader.AirborneVolumeM3, Is.Zero);
                Assert.That(zone.VolumeM3, Is.Zero, "The receiver below the solid cannot receive through it");
                Assert.That(cutter.VolumeM3 + caught, Is.EqualTo(initial).Within(1e-10));
            }
            finally { Object.DestroyImmediate(obstruction); }
            Physics.SyncTransforms();
            bucket.transform.rotation = Quaternion.identity;
            for (int i = 0; i < 100; i++)
            {
                unloader.Step(.1f);
                Assert.That(cutter.VolumeM3 + unloader.AirborneVolumeM3 + LandedVolume(),
                    Is.EqualTo(initial).Within(1e-10));
            }
            Assert.That(cutter.VolumeM3, Is.Zero);
            Assert.That(unloader.State, Is.EqualTo(BucketSoilUnloader.UnloadingState.Empty));
            // Emptying the bucket does not instantly land the final independently moving parcels.
            for (int i = 0; i < 50 && unloader.AirborneVolumeM3 > 0; i++)
            {
                unloader.Step(.1f);
                Assert.That(cutter.VolumeM3 + unloader.AirborneVolumeM3 + LandedVolume(),
                    Is.EqualTo(initial).Within(1e-10));
            }
            Assert.That(unloader.AirborneVolumeM3, Is.Zero);
            Assert.That(zone.VolumeM3, Is.EqualTo(initial - caught).Within(1e-10));
            Assert.That(LandedVolume(), Is.EqualTo(initial).Within(1e-10));
            Assert.That(cutter.DeliveredVolumeM3, Is.EqualTo(initial).Within(1e-10));
            Assert.That(zone.Records.Count, Is.GreaterThan(1));
            Assert.That(zone.Records[0].Bucket, Is.SameAs(cutter));
            double signed = 0;
            var mesh = receiver.GetComponent<MeshFilter>().sharedMesh;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
                signed += Vector3.Dot(vertices[triangles[i]], Vector3.Cross(vertices[triangles[i+1]], vertices[triangles[i+2]])) / 6.0;
            Assert.That(signed, Is.EqualTo(zone.VolumeM3).Within(1e-6));
        }

        private double LandedVolume(bool onlyGroundPiles = false)
        {
            double volume = 0;
            foreach (var zone in Object.FindObjectsByType<SoilReceivingZone>(FindObjectsSortMode.None))
            {
                if (onlyGroundPiles && !zone.isGroundPile) continue;
                foreach (var record in zone.Records)
                    if (record.Bucket == cutter) volume += record.VolumeM3;
            }
            return volume;
        }

        [Test]
        public void ResetPreservesOtherTerrainContributionsAndCapturedDensity()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            var otherGround = Terrain.CreateTerrainGameObject(data);
            var otherBucket = new GameObject("other source");
            try
            {
                var otherSoil = otherGround.AddComponent<DeformableSoilTerrain>();
                otherSoil.Initialize(otherGround.GetComponent<Terrain>());
                var other = otherBucket.AddComponent<BucketSoilCutter>();
                other.soil = otherSoil;
                other.densityKgPerM3 = 1200;
                other.tipLocalPosition = Vector3.zero;
                other.cutDirectionLocal = Vector3.forward;
                otherBucket.transform.position = new Vector3(2, .6f, 1);
                other.SamplePose();
                otherBucket.transform.position += Vector3.forward * .3f;
                other.SamplePose();
                double foreign = other.TransferTo(zone, other.VolumeM3);
                cutter.densityKgPerM3 = 999;
                double own = cutter.TransferTo(zone, .01);
                Assert.That(zone.MassKg, Is.EqualTo(foreign * 1200 + own * 1600).Within(1e-8));
                receiver.SetActive(false);
                cutter.ResetExcavation();
                Assert.That(zone.VolumeM3, Is.EqualTo(foreign).Within(1e-10));
                Assert.That(zone.MassKg, Is.EqualTo(foreign * 1200).Within(1e-8));
                Assert.That(zone.Records.Count, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(otherBucket); Object.DestroyImmediate(otherGround); }
        }

        [Test]
        public void TransferCapsAtReceiverCapacityAndResetRestoresConservation()
        {
            var type = typeof(BucketSoilCutter).Assembly.GetType("SoilReceivingZone");
            Assert.That(type, Is.Not.Null);
            var zone = receiver.AddComponent(type);
            type.GetField("capacityM3").SetValue(zone, .01f);
            var transfer = typeof(BucketSoilCutter).GetMethod("TransferTo");
            Assert.That(transfer, Is.Not.Null);
            double initial = cutter.VolumeM3;
            double moved = (double)transfer.Invoke(cutter, new object[] { zone, initial });
            Assert.That(moved, Is.EqualTo(.01).Within(1e-8));
            Assert.That(cutter.VolumeM3 + moved, Is.EqualTo(initial).Within(1e-10));
            Assert.That((double)type.GetProperty("MassKg").GetValue(zone), Is.EqualTo(moved * 1600).Within(1e-6));
            Assert.That((double)transfer.Invoke(cutter, new object[] { zone, initial }), Is.Zero);
            cutter.ResetExcavation();
            Assert.That(cutter.VolumeM3, Is.Zero);
            Assert.That((double)type.GetProperty("VolumeM3").GetValue(zone), Is.Zero);
            Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(1e-6));
        }
        [Test]
        public void UnloadingComponentsExist()
        {
            var assembly = typeof(BucketSoilCutter).Assembly;
            Assert.That(assembly.GetType("BucketSoilUnloader"), Is.Not.Null);
            Assert.That(assembly.GetType("SoilReceivingZone"), Is.Not.Null);
        }
    }
}
