using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ExcavatorControls.Tests
{
    /// <summary>Checks the construction demo's authored, pre-Play equipment placement.</summary>
    public sealed class ConstructionSiteInitialLayoutTests
    {
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
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(trackBase.eulerAngles.y, 22.5f)), Is.LessThan(.6f));
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
                Assert.That(slopeReplacement.size.x, Is.LessThan(30f));
                Assert.That(slopeReplacement.size.z, Is.LessThan(30f));
                Assert.That(Vector2.Distance(new Vector2(trackBase.position.x, trackBase.position.z),
                    new Vector2(281.0765f, 359.2318f)), Is.LessThan(3f));
                Assert.That(site.useExplicitWorkFaceAnchor, Is.False,
                    "The authored point should seed a small terrain-validated search rather than pinning one failing point.");
                Assert.That(site.usePrepositionedEquipment, Is.True);
                Assert.That(site.relocateEquipmentToSelectedWorkFace, Is.True);
                Assert.That(site.workFaceAnchor.x, Is.EqualTo(281.0765f).Within(.01f));
                Assert.That(site.workFaceAnchor.z, Is.EqualTo(359.2318f).Within(.01f));
                Assert.That(site.workFaceSearchRadius, Is.EqualTo(8f).Within(.01f));
                Assert.That(site.minimumSurfaceNormalY, Is.EqualTo(.55f).Within(.001f));
                Assert.That(site.minimumReadyTipClearanceMeters, Is.EqualTo(.6f).Within(.001f));
                Zx200DigController dig = behaviours.OfType<Zx200DigController>().Single();
                Assert.That(dig.approachPose.boom, Is.GreaterThan(-45f),
                    "The cut approach must not lift the boom to the old high -60 degree pose.");
                Assert.That(site.truckParkingOffsetLocal.x, Is.LessThanOrEqualTo(6.9f),
                    "The truck should park closer to the excavator while keeping its side clearance.");
                Assert.That(site.soilStripHalfWidth, Is.EqualTo(12.5f).Within(.01f));
                Assert.That(site.soilStripCenterLocalZ, Is.EqualTo(6.5f).Within(.01f),
                    "The cuttable soil should be centered across the complete slope face, not only near its crest.");
                Assert.That(site.soilStripHalfDepth, Is.EqualTo(4f).Within(.01f));
                AssertPosition(site.truck.position, site.bakedSite.truckPosition, "dump truck");
                Assert.That(Quaternion.Angle(site.truck.rotation, site.bakedSite.truckRotation), Is.LessThan(.1f));
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(site.truck.eulerAngles.y, 112.5f)), Is.LessThan(.6f));
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
                Assert.That(maximumSolidSoilWeight, Is.InRange(.3f, .45f),
                    "Keep most Cesium aerial detail in the soil blend so the slope colour matches the surrounding map.");
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

    }
}
