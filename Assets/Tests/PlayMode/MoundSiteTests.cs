using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public class MoundSiteTests
    {
        /// <summary>Verify original map geometry and support remain intact outside the irregular mound.</summary>
        /// <returns>Scene loading, tile streaming and cleanup.</returns>
        [UnityTest]
        public IEnumerator MoundUsesOriginalMapWithoutPlatform()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true)).ToArray();
                Assert.That(all.Any(c => c != null && c.GetType().Name == "ConstructionSiteMapClip"), Is.False);
                Assert.That(all.Any(c => c != null && c.GetType().Name == "ConstructionSiteSurfaceAppearance"), Is.False);
                var dig = all.OfType<Zx200DigController>().Single();
                var keyboard = dig.GetComponent<Zx200KeyboardController>();
                keyboard.enabled = false;
                for (int i = 0; i < 250; i++) yield return new WaitForFixedUpdate();
                var map = all.First(c => c != null && c.GetType().Name == "Cesium3DTileset" && c.gameObject.activeInHierarchy);
                var colliders = map.GetComponentsInChildren<MeshCollider>();
                Assert.That(colliders.Length, Is.GreaterThan(0));
                foreach (var collider in colliders) Assert.That(collider.sharedMesh.name.Contains("work area clipped"), Is.False);
                var soil = all.OfType<DeformableSoilTerrain>().Single();
                var data = soil.Terrain.terrainData;
                Assert.That(data.IsHole(0,0), Is.True);
                Assert.That(data.IsHole(data.holesResolution/2,data.holesResolution/2), Is.False);
                Vector3 outside = soil.transform.position + new Vector3(.03f, 20, .03f);
                Assert.That(soil.SoilCollider.Raycast(new Ray(outside,Vector3.down),out _,40), Is.False,
                    "Terrain's rectangular corners must expose original map collision");
                Assert.That(colliders.Any(c => c.Raycast(new Ray(outside,Vector3.down),out _,40)), Is.True);
                foreach (var item in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)))
                {
                    if (item.name == "Solid work pad" || item.name == "Soil bottom plate (always solid)")
                        Assert.That(item.gameObject.activeInHierarchy, Is.False);
                }
                Assert.That(keyboard.articulationRoot.immovable, Is.False);
                Assert.That(dig.GetComponent<Zx200TrackDrive>().GroundContactCount, Is.GreaterThan(0));
                Assert.That(soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(1e-8));
            }
            finally { SceneManager.UnloadSceneAsync(scene); }
        }
    }
}
