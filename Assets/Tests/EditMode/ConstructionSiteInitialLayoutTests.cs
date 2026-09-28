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
        public void SenceTestStartsWithExcavatorAndTruckAtFixedLowerWorkFace()
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

                Transform trackBase = keyboard.articulationRoot.transform;
                AssertPosition(trackBase.position, new Vector3(292.01f, -2.74f, 354.27f), "excavator track base");
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(trackBase.eulerAngles.y, 22.5f)), Is.LessThan(.1f));
                Assert.That(site.useExplicitWorkFaceAnchor, Is.True);
                Assert.That(site.usePrepositionedEquipment, Is.True);
                Assert.That(site.workFaceAnchor.x, Is.EqualTo(292.01f).Within(.01f));
                Assert.That(site.workFaceAnchor.z, Is.EqualTo(354.27f).Within(.01f));
                AssertPosition(site.truck.position, new Vector3(299.4038f, -2.847f, 349.9085f), "dump truck");
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(site.truck.eulerAngles.y, 112.5f)), Is.LessThan(.1f));
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
