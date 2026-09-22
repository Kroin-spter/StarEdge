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
    }
}
