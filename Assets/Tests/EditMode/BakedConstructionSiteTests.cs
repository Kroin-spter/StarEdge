using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public sealed class BakedConstructionSiteTests
    {
        private TerrainData terrainData;
        private BakedConstructionSiteData baked;
        private GameObject ground;

        [SetUp]
        public void SetUp()
        {
            terrainData = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++) heights[z, x] = .4f;
            terrainData.SetHeights(0, 0, heights);
            baked = ScriptableObject.CreateInstance<BakedConstructionSiteData>();
            baked.terrainData = terrainData;
            baked.sourceMapName = "example-map";
            baked.terrainOrigin = new Vector3(10, 7, 20);
            baked.georeferenceLatitude = 43.5;
            baked.georeferenceLongitude = 81.5;
            baked.georeferenceHeight = 1322.8;
            baked.machineRotation = Quaternion.identity;
            baked.truckRotation = Quaternion.identity;
            baked.groundHeights = new float[33 * 33];
            baked.soilMask = new float[33 * 33];
            for (int i = 0; i < baked.groundHeights.Length; i++)
            {
                baked.groundHeights[i] = .2f;
                baked.soilMask[i] = 1f;
            }
            baked.initialSoilVolumeM3 = 6.4;
        }

        [TearDown]
        public void TearDown()
        {
            if (ground != null) Object.DestroyImmediate(ground);
            Object.DestroyImmediate(baked);
            Object.DestroyImmediate(terrainData);
        }

        [Test]
        public void CorruptBaselineAndWrongMapAreRejectedWithReasons()
        {
            Assert.That(baked.TryValidate("example-map", 43.5, 81.5, 1322.8, out string validReason),
                Is.True, validReason);
            Assert.That(baked.TryValidate("other-map", 43.5, 81.5, 1322.8, out string mapReason), Is.False);
            Assert.That(mapReason, Does.Contain("地图"));
            baked.groundHeights[16 * 33 + 16] = .6f;
            Assert.That(baked.TryValidate("example-map", 43.5, 81.5, 1322.8, out string floorReason), Is.False);
            Assert.That(floorReason, Does.Contain("基础"));
        }

        [Test]
        public void RuntimeCutAndResetNeverChangeBakedTerrainOrFloor()
        {
            ground = Terrain.CreateTerrainGameObject(terrainData);
            var soil = ground.AddComponent<DeformableSoilTerrain>();
            baked.ApplyTo(soil);
            Assert.That(ground.GetComponent<Terrain>().terrainData, Is.Not.SameAs(terrainData));
            Vector3 point = baked.terrainOrigin + new Vector3(2, 0, 2);
            Assert.That(soil.CutSweep(point + Vector3.up * .5f, point + Vector3.up * .5f, 1, 2), Is.GreaterThan(0));
            Assert.That(terrainData.GetHeight(16, 16), Is.EqualTo(.8f).Within(.0001f));
            Assert.That(soil.GetFloorWorldY(point), Is.EqualTo(7.4f).Within(.0001f));
            soil.ResetSoil();
            Assert.That(ground.GetComponent<Terrain>().terrainData.GetHeight(16, 16),
                Is.EqualTo(.8f).Within(.0001f));
            Assert.That(baked.groundHeights[16 * 33 + 16], Is.EqualTo(.2f));
        }

        [Test]
        public void MissingBakedAssetFailsWithoutDynamicFallback()
        {
            Assert.That(CesiumMoundSite.TryValidateBakedConfiguration(null, "example-map", 43.5, 81.5,
                1322.8, out string reason), Is.False);
            Assert.That(reason, Does.Contain("烘焙"));
        }
    }
}
