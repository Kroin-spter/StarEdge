using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class FreeSoilDischargeTests
    {
        private GameObject ground, bucket;
        private TerrainData data;
        private BucketSoilCutter cutter;
        private BucketSoilUnloader unloader;

        [SetUp]
        public void SetUp()
        {
            data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33,33];
            for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z,x] = .5f;
            data.SetHeights(0, 0, heights);
            ground = Terrain.CreateTerrainGameObject(data);
            var soil = ground.AddComponent<DeformableSoilTerrain>();
            soil.Initialize(ground.GetComponent<Terrain>());
            bucket = new GameObject("free discharge bucket");
            cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .3f;
            cutter.SamplePose();
            Assert.That(cutter.VolumeM3, Is.GreaterThan(.02));
            unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.openingNormalLocal = Vector3.down;
            unloader.dwellSeconds = 0;
            unloader.flowM3PerSecond = .1f;
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            cutter.ResetExcavation();
            Object.DestroyImmediate(bucket);
            Object.DestroyImmediate(ground);
            Object.DestroyImmediate(data);
        }

        [Test]
        public void SteepTerrainKeepsLooseSoilMovingUntilItReachesTheFoot()
        {
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++)
                    heights[z, x] = .1f + .7f * Mathf.Clamp01((z / 8f - 1) / 1f);
            var runtime = cutter.soil.Terrain.terrainData;
            runtime.SetHeights(0, 0, heights);
            Physics.SyncTransforms();
            bucket.transform.position = new Vector3(2, 1.3f, 1.6f);
            double initial = cutter.VolumeM3;
            unloader.Step(.02f);
            unloader.Step(.25f);
            Assert.That(cutter.DeliveredVolumeM3, Is.Zero, "A steep slope must not pin loose soil in place.");
            Assert.That(unloader.SurfacePendingVolumeM3, Is.Zero);
            Assert.That(unloader.AirborneVolumeM3, Is.GreaterThan(0));
            Drain();
            Assert.That(cutter.DeliveredVolumeM3, Is.EqualTo(initial).Within(1e-8));
            Assert.That(unloader.AirborneVolumeM3 + unloader.SurfacePendingVolumeM3, Is.Zero);
            Assert.That(runtime.GetInterpolatedHeight(.5f, .4f), Is.EqualTo(1.04f).Within(.001f),
                "The steep cutting face must not receive stationary spoil.");
            foreach (var pile in Object.FindObjectsByType<SoilReceivingZone>(FindObjectsSortMode.None))
                if (pile.isGroundPile)
                    Assert.That(pile.transform.position.z, Is.LessThan(1.15f));
        }

        [Test]
        public void OffMapFlightDoesNotPreventLaterTerrainResidueFromSettling()
        {
            bucket.transform.position = new Vector3(10, 20, 0);
            double offMap = unloader.Step(.1f);
            bucket.transform.position = new Vector3(2, 3, 2);
            Physics.SyncTransforms();
            Drain();
            Assert.That(unloader.AirborneVolumeM3, Is.EqualTo(offMap).Within(1e-8));
            Assert.That(unloader.SurfacePendingVolumeM3, Is.Zero);
            Assert.That(cutter.ExcavatedVolumeM3,
                Is.EqualTo(cutter.DeliveredVolumeM3 + unloader.AirborneVolumeM3).Within(1e-8));
        }

        [Test]
        public void ReleasedSoilLandsOnTerrainAndCanBeCutAgain()
        {
            bucket.transform.position = new Vector3(2, 3, 2);
            Physics.SyncTransforms();
            double initial = cutter.VolumeM3;
            float before = ground.GetComponent<Terrain>().terrainData.GetInterpolatedHeight(.5f, .5f);
            Drain();
            Assert.That(cutter.VolumeM3, Is.Zero);
            Assert.That(unloader.AirborneVolumeM3, Is.Zero);
            Assert.That(cutter.DeliveredVolumeM3, Is.EqualTo(initial).Within(1e-8));
            float after = ground.GetComponent<Terrain>().terrainData.GetInterpolatedHeight(.5f, .5f);
            Assert.That(after, Is.GreaterThan(before + .03f));
            double piles = GroundPileVolume();
            Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.EqualTo(piles).Within(1e-5));
            Assert.That(piles, Is.LessThan(initial * .01), "Quantization residue should be tiny");
            // Distributed mouth flow deposits over a wider footprint than the old point source.
            double recut = cutter.soil.CutSweep(new Vector3(1.3f, before, 2), new Vector3(2.7f, before, 2), 1.1f, 1);
            Assert.That(recut, Is.GreaterThan(initial * .8));
            cutter.ResetExcavation();
            Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(1e-6));
            Assert.That(cutter.ExcavatedVolumeM3 + cutter.EmittedVolumeM3 + cutter.DeliveredVolumeM3, Is.Zero);
            Assert.That(GroundPileVolume(), Is.Zero);
        }

        [Test]
        public void NearbyShallowLandingsMergeWithoutLosingVolume()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                floor.transform.position = new Vector3(10, -.5f, 0);
                floor.transform.localScale = new Vector3(10, 1, 10);
                cutter.innerMouthHalfWidth = .02f;
                bucket.transform.position = new Vector3(10, 2, 0);
                Physics.SyncTransforms();
                double initial = cutter.VolumeM3;
                unloader.Step(.1f);
                bucket.transform.position += Vector3.right * .25f;
                Drain();
                int count = 0;
                foreach (var zone in Object.FindObjectsByType<SoilReceivingZone>(FindObjectsSortMode.None))
                    if (zone.isGroundPile && zone.VolumeM3 > 0) count++;
                Assert.That(count, Is.EqualTo(1), "Neighbouring foot deposits should form one pile, not scattered fragments.");
                Assert.That(GroundPileVolume(), Is.EqualTo(initial).Within(1e-8));
                Assert.That(unloader.AirborneVolumeM3 + unloader.SurfacePendingVolumeM3, Is.Zero);
            }
            finally { Object.DestroyImmediate(floor); }
        }

        [Test]
        public void PlainGroundReceivesConicalPileAndNoSoilIsLost()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                floor.transform.position = new Vector3(10, -.5f, 0);
                floor.transform.localScale = new Vector3(10, 1, 10);
                bucket.transform.position = new Vector3(10, 2, 0);
                Physics.SyncTransforms();
                double initial = cutter.VolumeM3;
                Drain();
                Assert.That(GroundPileVolume(), Is.EqualTo(initial).Within(1e-8));
                foreach (var zone in Object.FindObjectsByType<SoilReceivingZone>(FindObjectsSortMode.None))
                {
                    if (!zone.isGroundPile) continue;
                    var mesh = zone.GetComponent<MeshFilter>().sharedMesh;
                    double volume = 0;
                    var vertices = mesh.vertices;
                    var triangles = mesh.triangles;
                    for (int i = 0; i < triangles.Length; i += 3)
                        volume += Vector3.Dot(vertices[triangles[i]], Vector3.Cross(vertices[triangles[i+1]], vertices[triangles[i+2]])) / 6.0;
                    Assert.That(volume, Is.EqualTo(zone.VolumeM3).Within(1e-6));
                }
                Assert.That(cutter.Landings.Count, Is.GreaterThan(0));
            }
            finally { Object.DestroyImmediate(floor); }
        }

        [Test]
        public void FullReceiverOverflowsToGroundInsteadOfHoldingSoilInBucket()
        {
            var receiverObject = new GameObject("small bed");
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var zone = receiverObject.AddComponent<SoilReceivingZone>();
                zone.transform.position = new Vector3(10, .5f, 0);
                zone.capacityM3 = .01f;
                unloader.receivers = new[] { zone };
                floor.transform.position = new Vector3(10, -.5f, 0);
                floor.transform.localScale = new Vector3(10, 1, 10);
                bucket.transform.position = new Vector3(10, 2, 0);
                Physics.SyncTransforms();
                double initial = cutter.VolumeM3;
                unloader.Step(.02f);
                Assert.That(zone.VolumeM3, Is.Zero, "Soil must travel before it lands");
                Drain();
                Assert.That(zone.VolumeM3, Is.EqualTo(.01).Within(1e-8));
                Assert.That(unloader.Target, Is.SameAs(zone));
                Assert.That(GroundPileVolume(), Is.GreaterThan(0));
                Assert.That(zone.VolumeM3 + GroundPileVolume(), Is.EqualTo(initial).Within(1e-8));
                Assert.That(cutter.VolumeM3 + unloader.AirborneVolumeM3, Is.Zero);
                cutter.ResetExcavation();
                Assert.That(unloader.Target, Is.Null);
            }
            finally { Object.DestroyImmediate(receiverObject); Object.DestroyImmediate(floor); }
        }

        [Test]
        public void ResetCancelsAirborneInventoryAndGrossCounters()
        {
            bucket.transform.position = new Vector3(10, 20, 0);
            unloader.Step(.1f);
            Assert.That(unloader.AirborneVolumeM3, Is.GreaterThan(0));
            Assert.That(cutter.ExcavatedVolumeM3, Is.EqualTo(cutter.VolumeM3 + unloader.AirborneVolumeM3).Within(1e-8));
            cutter.ResetExcavation();
            Assert.That(unloader.AirborneVolumeM3, Is.Zero);
            Assert.That(cutter.ExcavatedVolumeM3 + cutter.EmittedVolumeM3, Is.Zero);
            unloader.Step(.1f);
            Assert.That(cutter.DeliveredVolumeM3, Is.Zero);
            Assert.That(cutter.Landings, Is.Empty);
        }

        private void Drain()
        {
            for (int i = 0; i < 600; i++) unloader.Step(.02f);
        }

        private static double GroundPileVolume()
        {
            double sum = 0;
            foreach (var zone in Object.FindObjectsByType<SoilReceivingZone>(FindObjectsSortMode.None))
                if (zone.isGroundPile) sum += zone.VolumeM3;
            return sum;
        }

        [Test]
        public void DownwardBucketEmitsEvenWithoutReceiverOrGround()
        {
            bucket.transform.position = new Vector3(10, 3, 0);
            double before = cutter.VolumeM3;
            double emitted = unloader.Step(.1f);
            Assert.That(emitted, Is.GreaterThan(0), "Free discharge must not require a receiving zone");
            Assert.That(cutter.VolumeM3, Is.EqualTo(before - emitted).Within(1e-9));
            var property = typeof(BucketSoilUnloader).GetProperty("AirborneVolumeM3");
            Assert.That(property, Is.Not.Null);
            Assert.That((double)property.GetValue(unloader), Is.EqualTo(emitted).Within(1e-9));
            Assert.That(cutter.DeliveredVolumeM3, Is.Zero, "Emission is not landing");
        }
    }
}
