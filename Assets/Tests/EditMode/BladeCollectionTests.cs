using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class BladeCollectionTests
    {
        private TerrainData data;
        private GameObject ground, bucket;
        private BucketSoilCutter cutter;

        [SetUp]
        public void SetUp()
        {
            data = new TerrainData { heightmapResolution = 65, size = new Vector3(4, 2, 4) };
            var heights = new float[65,65];
            for (int z=0; z<65; z++) for (int x=0; x<65; x++) heights[z,x] = .5f;
            data.SetHeights(0,0,heights);
            ground = Terrain.CreateTerrainGameObject(data);
            var soil = ground.AddComponent<DeformableSoilTerrain>();
            soil.Initialize(ground.GetComponent<Terrain>());
            bucket = new GameObject("physical cutting blade");
            cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
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
        public void VerticalTeethCutWithoutPretendingAllRemovedSoilEnteredSidewaysMouth()
        {
            bucket.transform.rotation = Quaternion.Euler(90,0,0);
            cutter.cutDirectionLocal = Vector3.up; // World mouth faces forward while teeth point down.
            bucket.transform.position = new Vector3(2,.92f,2);
            cutter.SamplePose();
            bucket.transform.position += Vector3.down * .2f;
            cutter.SamplePose();
            Assert.That(cutter.ExcavatedVolumeM3, Is.GreaterThan(0), "Actual tooth penetration must cut even without horizontal movement");
            Assert.That(cutter.VolumeM3, Is.LessThan(cutter.ExcavatedVolumeM3 * .5));
            var flight = bucket.GetComponent<SoilFlightTransport>();
            Assert.That(flight, Is.Not.Null, "Uncollected soil must have a real accounted destination path");
            Assert.That(cutter.soil.RecalculateRemovedVolumeM3(),
                Is.EqualTo(cutter.VolumeM3 + flight.AirborneVolumeM3 + flight.SurfacePendingVolumeM3 + cutter.DeliveredVolumeM3).Within(1e-5));
        }

        [Test]
        public void CuttingDoesNotReachHalfAMetreAheadOfActualTeeth()
        {
            bucket.transform.position = new Vector3(2,.7f,1);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .25f;
            cutter.SamplePose();
            Assert.That(cutter.ExcavatedVolumeM3, Is.GreaterThan(0));
            Assert.That(cutter.soil.Terrain.terrainData.GetHeight(32,24), Is.EqualTo(1).Within(.0001),
                "z=1.5 lies ahead of the current z=1.25 teeth; old radial cutting must not reach it");
        }

        [Test]
        public void StationaryBladeInsideSoilDoesNotKeepExcavating()
        {
            bucket.transform.position = new Vector3(2,.7f,2);
            cutter.SamplePose();
            for(int i=0;i<20;i++) cutter.SamplePose();
            Assert.That(cutter.ExcavatedVolumeM3, Is.Zero);
            Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero);
        }

        /// <summary>A rising inward scoop is not the same as lifting a bucket out of the cut.</summary>
        [TestCase(25f)]
        [TestCase(45f)]
        [TestCase(60f)]
        public void InwardRisingBladeStillCutsAndCollects(float slopeDegrees)
        {
            bucket.transform.position = new Vector3(2, .55f, 1.5f);
            cutter.SamplePose();
            Vector3 travel = new Vector3(0, Mathf.Sin(slopeDegrees * Mathf.Deg2Rad),
                Mathf.Cos(slopeDegrees * Mathf.Deg2Rad)) * .2f;
            bucket.transform.position += travel;
            cutter.SamplePose();
            Assert.That(cutter.ExcavatedVolumeM3, Is.GreaterThan(.001), cutter.Status);
            Assert.That(cutter.VolumeM3, Is.GreaterThan(.001), "Inward steel motion must still collect while rising.");
            Assert.That(cutter.soil.RecalculateRemovedVolumeM3(),
                Is.EqualTo(cutter.ExcavatedVolumeM3).Within(1e-5));
        }

        /// <summary>Relaxing inward curl must not authorize a straight lift or rearward/sideways scrape.</summary>
        [TestCase(0, 1, 0)]
        [TestCase(0, 1, -1)]
        [TestCase(1, 0, 0)]
        public void WithdrawalAndSideScrapeStillDoNotCut(float x, float y, float z)
        {
            bucket.transform.position = new Vector3(2, .55f, 2);
            cutter.SamplePose();
            bucket.transform.position += new Vector3(x, y, z).normalized * .2f;
            cutter.SamplePose();
            Assert.That(cutter.ExcavatedVolumeM3, Is.Zero);
        }

        /// <summary>Do not discard a real rotating steel sweep merely because the tip marker is stationary.</summary>
        [Test]
        public void RotatingBladeWithFixedTipCanRemoveSoil()
        {
            var marker = new GameObject("fixed tooth marker");
            try
            {
                bucket.transform.position = new Vector3(2, .8f, 2);
                marker.transform.position = bucket.transform.position;
                cutter.tipMarker = marker.transform;
                cutter.bladeForwardLocal = Vector3.forward;
                cutter.bladeCenterOffset = new Vector3(0, 0, .3f);
                cutter.bladeLength = .12f;
                cutter.SamplePose();
                bucket.transform.rotation = Quaternion.Euler(20, 0, 0);
                cutter.SamplePose();
                Assert.That(cutter.ExcavatedVolumeM3, Is.GreaterThan(.001), cutter.Status);
            }
            finally { Object.DestroyImmediate(marker); }
        }
    }
}
