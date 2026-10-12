using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ExcavatorControls.Tests
{
    /// <summary>Checks the construction demo's authored, pre-Play equipment placement.</summary>
    public sealed class ConstructionSiteInitialLayoutTests
    {
        /// <summary>Reject a baked rectangle on the road and floor instead of the steep blue work face.</summary>
        [Test]
        public void BakedSoilOccupiesTheSteepFaceRatherThanFlatRoadAndBench()
        {
            BakedConstructionSiteData site = AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/SenceTest/ConstructionSite.asset");
            Assert.That(site, Is.Not.Null);
            int n = site.terrainData.heightmapResolution;
            float dz = site.terrainData.size.z / (n - 1f);
            int step = Mathf.Max(1, Mathf.RoundToInt(1f / dz));
            int soilCells = 0, slopeCells = 0;
            for (int z = step; z < n - step; z++)
                for (int x = 0; x < n; x++)
                {
                    int index = z * n + x;
                    if (site.soilMask[index] < .1f) continue;
                    soilCells++;
                    float rise = (site.groundHeights[index + step * n] -
                        site.groundHeights[index - step * n]) * site.terrainData.size.y / (2f * step * dz);
                    if (rise > .6f) slopeCells++;
                }
            Assert.That(soilCells, Is.GreaterThan(1000));
            Assert.That((float)slopeCells / soilCells, Is.GreaterThan(.7f),
                "Most cuttable soil must lie on the ascending wall, not on flat road or lower bench.");
        }

        /// <summary>The local foundation patch resolves the teeth and leaves the lower bench to Cesium.</summary>
        [Test]
        public void BakedLocalSlopeResolvesTeethButNotTheLowerBench()
        {
            BakedConstructionSiteData site = AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/SenceTest/ConstructionSite.asset");
            Assert.That(site, Is.Not.Null);
            TerrainData terrain = site.terrainData;
            Assert.That(terrain, Is.Not.Null);
            int n = terrain.heightmapResolution;
            Assert.That(terrain.size.x / (n - 1f), Is.LessThan(.015f));
            Assert.That(terrain.size.z / (n - 1f), Is.LessThan(.015f));
            foreach (float fraction in new[] { .2f, .5f, .8f })
            {
                float worldX = site.terrainOrigin.x + terrain.size.x * fraction;
                int x = Mathf.RoundToInt((worldX - site.terrainOrigin.x) /
                    terrain.size.x * (n - 1));
                int active = 0;
                for (int z = 0; z < n; z++)
                {
                    if (site.soilMask[z * n + x] < .2f) continue;
                    Vector3 point = site.terrainOrigin + new Vector3(
                        x * terrain.size.x / (n - 1f), 0f, z * terrain.size.z / (n - 1f));
                    if (IsSolidAt(terrain, site.terrainOrigin, point)) active++;
                }
                Assert.That(active, Is.GreaterThan(8), $"The wall at map X={worldX} must be cuttable.");
                Assert.That(IsSolidAt(terrain, site.terrainOrigin,
                    new Vector3(worldX, 0f, site.machinePosition.z)), Is.False,
                    "The lower bench must remain original Cesium at every wall segment.");
            }
        }

        /// <summary>The excavator and truck must be saved at the tested lower work face.</summary>
        [Test]
        public void SenceTestStartsWithMobileExcavatorAndTruckAtLowerWorkFace()
        {
            const string scenePath = "Assets/Scenes/sence_test.unity";
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                MonoBehaviour[] behaviours = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true)).ToArray();
                CesiumMoundSite site = behaviours.OfType<CesiumMoundSite>().Single();
                Zx200KeyboardController keyboard = behaviours.OfType<Zx200KeyboardController>().Single();
                keyboard.ResolveArticulationRoot();
                Assert.That(site.siteMode, Is.EqualTo(CesiumMoundSite.SitePreparationMode.Baked));
                Assert.That(site.bakedSite, Is.Not.Null);

                Transform trackBase = keyboard.articulationRoot.transform;
                AssertPosition(trackBase.position, site.bakedSite.machinePosition, "excavator track base");
                Assert.That(Quaternion.Angle(trackBase.rotation, site.bakedSite.machineRotation), Is.LessThan(.1f));
                ConstructionSiteMapClip clip = behaviours.OfType<ConstructionSiteMapClip>().SingleOrDefault();
                Assert.That(clip, Is.Not.Null, "The editor and runtime must share one map clipping boundary.");
                Bounds slopeReplacement = clip.workArea;
                Assert.That(clip.useOrientedWorkArea, Is.True,
                    "Cesium clipping must follow the slope face orientation, not a large axis-aligned square.");
                Assert.That(ConstructionSiteFootprint.ContainsHorizontal(trackBase.position, clip.workAreaCenter,
                    clip.workAreaRotation, clip.workAreaSize), Is.False,
                    "The cuttable replacement must be confined to the slope, not replace the excavator support ground.");
                Assert.That(ConstructionSiteFootprint.ContainsHorizontal(site.truck.position, clip.workAreaCenter,
                    clip.workAreaRotation, clip.workAreaSize), Is.False,
                    "The dump truck must remain supported by the original Cesium ground.");
                Assert.That(site.bakedSite.workFaceSize.x, Is.LessThanOrEqualTo(10f),
                    "Foundation acceptance uses a fine local patch before expanding to the whole wall.");
                Assert.That(site.bakedSite.workFaceSize.y, Is.LessThanOrEqualTo(10f),
                    "The replacement must stay on the wall without extending onto the lower bench.");
                Assert.That(Vector2.Distance(new Vector2(trackBase.position.x, trackBase.position.z),
                    new Vector2(281.0765f, 359.2318f)), Is.LessThan(8f));
                Assert.That(site.useExplicitWorkFaceAnchor, Is.False,
                    "The authored point should seed a small terrain-validated search rather than pinning one failing point.");
                Assert.That(site.usePrepositionedEquipment, Is.True);
                Assert.That(site.relocateEquipmentToSelectedWorkFace, Is.True);
                Assert.That(site.workFaceAnchor.x, Is.EqualTo(281.0765f).Within(.01f));
                Assert.That(site.workFaceAnchor.z, Is.EqualTo(359.2318f).Within(.01f));
                Assert.That(site.workFaceSearchRadius, Is.EqualTo(8f).Within(.01f));
                Assert.That(site.minimumSurfaceNormalY, Is.EqualTo(.55f).Within(.001f));
                Assert.That(site.minimumReadyTipClearanceMeters, Is.EqualTo(.5f).Within(.001f));
                Assert.That(clip.enabled, Is.False,
                    "Preview keeps the source map until runtime validates and clips the exact solid soil coverage.");
                Zx200DigController dig = behaviours.OfType<Zx200DigController>().Single();
                Assert.That(dig.approachPose.boom, Is.GreaterThan(-45f),
                    "The cut approach must not lift the boom to the old high -60 degree pose.");
                Assert.That(site.truckParkingOffsetLocal.x, Is.LessThanOrEqualTo(6.9f),
                    "The truck should park closer to the excavator while keeping its side clearance.");
                Assert.That(site.soilStripHalfWidth, Is.EqualTo(65f).Within(.01f));
                Assert.That(site.soilStripCenterLocalX, Is.EqualTo(-30f).Within(.01f));
                Assert.That(site.soilStripCenterLocalZ, Is.EqualTo(6.5f).Within(.01f),
                    "The cuttable soil should be centered across the complete slope face, not only near its crest.");
                Assert.That(site.soilStripHalfDepth, Is.EqualTo(5f).Within(.01f));
                AssertPosition(site.truck.position, site.bakedSite.truckPosition, "dump truck");
                Assert.That(Quaternion.Angle(site.truck.rotation, site.bakedSite.truckRotation), Is.LessThan(.1f));
                Terrain terrain = site.soil.GetComponent<Terrain>();
                Assert.That(terrain.enabled, Is.True, "The baked slope must be visible before Play.");
                Assert.That(terrain.terrainData, Is.SameAs(site.bakedSite.terrainData));
                TerrainLayer[] layers = terrain.terrainData.terrainLayers;
                Assert.That(layers.Length, Is.GreaterThanOrEqualTo(2),
                    "The baked slope must retain its Cesium imagery layer instead of rendering as a solid brown Terrain.");
                Assert.That(layers[1].diffuseTexture, Is.Not.Null,
                    "The Cesium imagery texture must be embedded with the static construction-site asset.");
                float[,,] surfaceWeights = terrain.terrainData.GetAlphamaps(0, 0,
                    terrain.terrainData.alphamapResolution, terrain.terrainData.alphamapResolution);
                float maximumSolidSoilWeight = 0f;
                float maximumMapWeight = 0f;
                for (int z = 0; z < surfaceWeights.GetLength(0); z++)
                    for (int x = 0; x < surfaceWeights.GetLength(1); x++)
                    {
                        maximumSolidSoilWeight = Mathf.Max(maximumSolidSoilWeight, surfaceWeights[z, x, 0]);
                        maximumMapWeight = Mathf.Max(maximumMapWeight, surfaceWeights[z, x, 1]);
                    }
                Assert.That(maximumSolidSoilWeight, Is.LessThanOrEqualTo(.01f),
                    "The local physical slope keeps the sampled map colour instead of an opaque brown coating.");
                Assert.That(maximumMapWeight, Is.GreaterThanOrEqualTo(.99f));
                Bounds expectedWorkArea = site.bakedSite.GetWorkFaceBounds();
                Assert.That(Vector3.Distance(clip.workArea.center, expectedWorkArea.center), Is.LessThan(.01f));
                Assert.That(Vector3.Distance(clip.workArea.size, expectedWorkArea.size), Is.LessThan(.01f));
                SoilReceivingZone receiver = site.truck.GetComponentInChildren<SoilReceivingZone>();
                BoxCollider bedFloor = site.truck.GetComponentsInChildren<BoxCollider>()
                    .Single(collider => collider.name == "Bed floor");
                Assert.That(receiver.size.x, Is.LessThanOrEqualTo(2f),
                    "The visible soil surface must fit within the imported dump-box width.");
                Assert.That(bedFloor.size.x, Is.LessThanOrEqualTo(2.2f),
                    "The physical bed floor must follow the visible dump-box width.");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void AssertPosition(Vector3 actual, Vector3 expected, string subject)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(.05f),
                $"{subject} must be authored on the lower bench before Play; actual={actual}, expected={expected}");
        }

        private static bool IsSolidAt(TerrainData terrain, Vector3 origin, Vector3 point)
        {
            Vector3 local = point - origin;
            Vector3 size = terrain.size;
            if (local.x < 0f || local.z < 0f || local.x >= size.x || local.z >= size.z) return false;
            int holes = terrain.holesResolution;
            int x = Mathf.FloorToInt(local.x / size.x * holes);
            int z = Mathf.FloorToInt(local.z / size.z * holes);
            return !terrain.IsHole(x, z);
        }

    }
}
