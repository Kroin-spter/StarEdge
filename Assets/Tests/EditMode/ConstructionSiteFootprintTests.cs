using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public sealed class ConstructionSiteFootprintTests
    {
        [Test]
        public void RotatedSlopeFootprintExcludesLowerBenchAndContainsItsCorners()
        {
            Vector3 center = new Vector3(12f, 4f, -7f);
            Quaternion rotation = Quaternion.Euler(0f, 30f, 0f);
            Vector2 size = new Vector2(12f, 8f);
            Bounds bounds = ConstructionSiteFootprint.CalculateBounds(center, rotation, size, 0f, 12f);

            Assert.That(ConstructionSiteFootprint.ContainsHorizontal(center, center, rotation, size), Is.True);
            Assert.That(ConstructionSiteFootprint.ContainsHorizontal(
                center - rotation * Vector3.forward * 5f, center, rotation, size), Is.False,
                "The lower-bench support position must stay outside the slope replacement.");
            foreach (float x in new[] { -size.x * .5f, size.x * .5f })
            foreach (float z in new[] { -size.y * .5f, size.y * .5f })
            {
                Vector3 corner = center + rotation * new Vector3(x, 0f, z);
                Assert.That(corner.x, Is.InRange(bounds.min.x - .001f, bounds.max.x + .001f));
                Assert.That(corner.z, Is.InRange(bounds.min.z - .001f, bounds.max.z + .001f));
            }
        }

        [Test]
        public void HorizontalFootprintIgnoresTheHeightDifferenceAndTerrainPitch()
        {
            Vector3 center = new Vector3(12f, 10f, -7f);
            Quaternion terrainRotation = Quaternion.Euler(25f, 30f, 0f);
            Quaternion horizontalRotation = Quaternion.LookRotation(
                Vector3.ProjectOnPlane(terrainRotation * Vector3.forward, Vector3.up), Vector3.up);
            Vector3 pointOnLowerBench = center + horizontalRotation * new Vector3(0f, 0f, 1.9f);
            pointOnLowerBench.y = 0f;

            Assert.That(ConstructionSiteFootprint.ContainsHorizontal(pointOnLowerBench, center,
                terrainRotation, new Vector2(6f, 4f)), Is.True,
                "A point inside the horizontal slope footprint must stay inside even when it is far below the face center.");
        }

        [Test]
        public void TerrainHolesKeepTheSlopeSolidAndLeaveTheSurroundingMapOpen()
        {
            Vector3 origin = new Vector3(-10f, 0f, -10f);
            Vector3 size = new Vector3(20f, 8f, 20f);
            bool[,] holes = ConstructionSiteFootprint.BuildTerrainHoles(20, origin, size,
                Vector3.zero, Quaternion.identity, new Vector2(8f, 4f));

            Assert.That(holes[9, 9], Is.True, "Cells in the cuttable slope must retain Terrain collision.");
            Assert.That(holes[0, 0], Is.False, "Cells outside the slope must reveal the original Cesium surface.");
            Assert.That(holes[9, 15], Is.False, "The machine-side apron must remain a Terrain hole.");
        }

        [Test]
        public void DynamicSiteHasValidPreSurveyClipBoundsBeforeSelectingAWorkFace()
        {
            TerrainData terrainData = new TerrainData
            {
                heightmapResolution = 33,
                size = new Vector3(30f, 10f, 30f)
            };
            GameObject terrainObject = Terrain.CreateTerrainGameObject(terrainData);
            try
            {
                DeformableSoilTerrain soil = terrainObject.AddComponent<DeformableSoilTerrain>();
                CesiumMoundSite site = terrainObject.AddComponent<CesiumMoundSite>();
                site.soil = soil;
                site.siteMode = CesiumMoundSite.SitePreparationMode.DynamicDeveloper;

                Bounds bounds = site.WorkAreaBounds;

                Assert.That(bounds.size.x, Is.EqualTo(12f).Within(.001f));
                Assert.That(bounds.size.z, Is.EqualTo(4f).Within(.001f));
                Assert.That(bounds.size.y, Is.EqualTo(10f).Within(.001f));
            }
            finally
            {
                Object.DestroyImmediate(terrainObject);
                Object.DestroyImmediate(terrainData);
            }
        }
    }
}
