using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public sealed class EquipmentAppearanceTests
    {
        /// <summary>Upgrading only visuals must not change physics components or leak meshes across toggles.</summary>
        /// <returns>Frames for deferred destruction of appearance-owned resources.</returns>
        [UnityTest]
        public IEnumerator UpgradePreservesPhysicsAndRestoresOriginalMaterials()
        {
            var robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Robotics/zx200_description/Prefabs/zx200.prefab"));
            try
            {
                int colliders = robot.GetComponentsInChildren<Collider>(true).Length;
                var bodies = robot.GetComponentsInChildren<ArticulationBody>(true);
                float[] mass = bodies.Select(body => body.mass).ToArray();
                var renderers = robot.GetComponentsInChildren<MeshRenderer>(true);
                var original = renderers.Select(renderer => renderer.sharedMaterials).ToArray();
                var appearance = robot.AddComponent<ExcavatorAppearance>();
                Assert.That(appearance.IsDetailed, Is.True);
                Assert.That(robot.GetComponentsInChildren<Collider>(true).Length, Is.EqualTo(colliders));
                CollectionAssert.AreEqual(bodies,robot.GetComponentsInChildren<ArticulationBody>(true));
                CollectionAssert.AreEqual(mass,bodies.Select(body => body.mass).ToArray());
                Assert.That(robot.GetComponentsInChildren<MeshRenderer>(true).Length, Is.EqualTo(renderers.Length+4));
                int meshes = robot.GetComponentsInChildren<MeshFilter>(true).Length;
                var ownedMaterials = robot.GetComponentsInChildren<MeshRenderer>(true)
                    .SelectMany(renderer => renderer.sharedMaterials)
                    .Where(material => material != null && material.shader.name == "Mine/Weathered Equipment")
                    .Distinct().ToArray();
                for (int i = 0; i < 20; i++)
                {
                    appearance.SetDetailed(false);
                    for (int j = 0; j < renderers.Length; j++)
                        CollectionAssert.AreEqual(original[j],renderers[j].sharedMaterials);
                    appearance.SetDetailed(true);
                    Assert.That(robot.GetComponentsInChildren<MeshFilter>(true).Length, Is.EqualTo(meshes));
                }
                appearance.enabled = false;
                for (int j = 0; j < renderers.Length; j++)
                    CollectionAssert.AreEqual(original[j],renderers[j].sharedMaterials);
                Object.Destroy(appearance);
                yield return null;
                yield return null;
                Assert.That(robot.GetComponentsInChildren<MeshRenderer>(true).Length, Is.EqualTo(renderers.Length));
                foreach (var material in ownedMaterials) Assert.That(material == null, Is.True,
                    "Destroyed appearance must release all of its owned materials.");
            }
            finally { Object.DestroyImmediate(robot); }
        }

        /// <summary>Early robot deactivation must not permanently prevent its one-time environment capture.</summary>
        /// <returns>Frames for reactivation and the resumed reflection request.</returns>
        [UnityTest]
        public IEnumerator InterruptedInitialReflectionCaptureCanResume()
        {
            bool originalRealtime = QualitySettings.realtimeReflectionProbes;
            QualitySettings.realtimeReflectionProbes = true;
            var robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Robotics/zx200_description/Prefabs/zx200.prefab"));
            try
            {
                robot.AddComponent<ExcavatorAppearance>();
                var probe = robot.GetComponentInChildren<ReflectionProbe>();
                Assert.That(probe, Is.Not.Null);
                robot.SetActive(false);
                robot.SetActive(true);
                for (int frame = 0; frame < 30 && probe.realtimeTexture == null; frame++) yield return null;
                Assert.That(probe.realtimeTexture, Is.Not.Null,
                    "Reactivation must restart a capture interrupted during initialization.");
            }
            finally
            {
                Object.DestroyImmediate(robot);
                QualitySettings.realtimeReflectionProbes = originalRealtime;
            }
        }

        /// <summary>Report real link-space visual geometry before placing collider-free external details.</summary>
        [Test]
        public void InspectRobotVisualLayout()
        {
            var robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Robotics/zx200_description/Prefabs/zx200.prefab"));
            try
            {
                foreach (var linkName in new[] { "track_link", "body_link", "boom_link", "arm_link", "bucket_link" })
                {
                    var link = robot.GetComponentsInChildren<Transform>(true).First(t => t.name == linkName);
                    Debug.Log($"LINK {linkName} local={link.localPosition:F3} world={link.position:F3}");
                    foreach (var renderer in link.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        Transform ancestor = renderer.transform;
                        while (ancestor != null && ancestor != link && !ancestor.name.EndsWith("_link")) ancestor = ancestor.parent;
                        if (ancestor != link) continue;
                        var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                        var localBounds = new Bounds(link.InverseTransformPoint(renderer.bounds.center), Vector3.zero);
                        for (int i = 0; i < 8; i++)
                        {
                            Vector3 corner = renderer.bounds.center + Vector3.Scale(renderer.bounds.extents,
                                new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                            localBounds.Encapsulate(link.InverseTransformPoint(corner));
                        }
                        Debug.Log($"VIS {linkName}/{renderer.name} center={localBounds.center:F3} size={localBounds.size:F3} " +
                            $"mesh={mesh.name}, vertices={mesh.vertexCount}, materials={string.Join(",", renderer.sharedMaterials.Select(m => m.name))}");
                    }
                }
            }
            finally { Object.DestroyImmediate(robot); }
        }
    }
}
