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
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(trackBase.eulerAngles.y, 22.5f)), Is.LessThan(.1f));
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
                Assert.That(site.soilStripHalfWidth, Is.EqualTo(6f).Within(.01f));
                Assert.That(site.soilStripHalfDepth, Is.EqualTo(2f).Within(.01f));
                AssertPosition(site.truck.position, site.bakedSite.truckPosition, "dump truck");
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(site.truck.eulerAngles.y, 112.5f)), Is.LessThan(.1f));
                Terrain terrain = site.soil.GetComponent<Terrain>();
                Assert.That(terrain.enabled, Is.True, "The baked slope must be visible before Play.");
                Assert.That(terrain.terrainData, Is.SameAs(site.bakedSite.terrainData));
                ConstructionSiteMapClip clip = behaviours.OfType<ConstructionSiteMapClip>().SingleOrDefault();
                Assert.That(clip, Is.Not.Null, "The editor and runtime must share one map clipping boundary.");
                Assert.That(clip.workArea.center.x, Is.EqualTo(site.bakedSite.terrainOrigin.x +
                    site.bakedSite.terrainData.size.x * .5f).Within(.01f));
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
