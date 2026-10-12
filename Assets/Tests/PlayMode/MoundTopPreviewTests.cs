using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public sealed class MoundTopPreviewTests
    {
        /// <summary>Verify real baked startup and manual Terrain cutting, then render the saved preview layout.</summary>
        /// <returns>Scene loading, support readiness, cutting assertions, preview capture and scene teardown.</returns>
        [UnityTest]
        public IEnumerator MoundSupportsTracksAndFrontSoilCanBeCut()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            // The unchanged legacy Terrain elsewhere in sence_test contains three missing tree references.
            LogAssert.Expect(LogType.Error, "Tree prefab at index 0 is missing.");
            LogAssert.Expect(LogType.Error, "Tree prefab at index 1 is missing.");
            LogAssert.Expect(LogType.Error, "Tree prefab at index 2 is missing.");
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            CesiumMoundSite site = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<CesiumMoundSite>(true)).Single();
            try
            {
                Assert.That(site.bakedSite.supportsTracksOnTerrain, Is.True);
                Assert.That(site.bakedSite.replacesCesiumSurface, Is.True);
                float deadline = Time.realtimeSinceStartup + 12f;
                while (!site.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(site.IsReady, Is.True, site.Status);
                Assert.That(site.machine.transform.position.x, Is.EqualTo(320.28f).Within(.2f));
                Assert.That(site.soil.HasSurfaceAt(site.bakedSite.machinePosition), Is.True);
                TerrainData data = site.soil.Terrain.terrainData;
                Assert.That(data, Is.Not.SameAs(site.bakedSite.terrainData));
                Vector3 front = site.bakedSite.machinePosition + site.bakedSite.machineRotation * new Vector3(0f, 0f, 5f);
                float surface = site.soil.Terrain.SampleHeight(front) + site.soil.transform.position.y;
                front.y = surface - .15f;
                double removed = site.soil.CutSweep(front, front + Vector3.right * .3f, .45f, .3f);
                Assert.That(removed, Is.GreaterThan(.01), "前方斜坡必须有真实可切土量，而不只是影像");
                Assert.That(site.soil.Terrain.SampleHeight(front) + site.soil.transform.position.y,
                    Is.LessThan(surface));
                site.soil.ResetSoil();
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    for (int frame = 0; frame < 240; frame++) yield return null;
                    Camera camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                        .First(candidate => candidate.CompareTag("MainCamera"));
                    Capture(camera, site.bakedSite.machinePosition + new Vector3(-18f, 16f, -19f),
                        site.bakedSite.machinePosition + Vector3.forward * 2f, "Diagnostics/mound-top-preview.png");
                    Capture(camera, site.bakedSite.machinePosition + new Vector3(-9f, 7f, -11f),
                        site.bakedSite.machinePosition + Vector3.forward * 2f, "Diagnostics/mound-top-close.png");
                    Capture(camera, site.bakedSite.machinePosition + new Vector3(14f, 12f, 17f),
                        site.bakedSite.machinePosition, "Diagnostics/mound-top-front.png");
                }
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
        }

        private static void Capture(Camera camera, Vector3 position, Vector3 lookAt, string path)
        {
            var target = new RenderTexture(1400, 1000, 24);
            var image = new Texture2D(1400, 1000, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active, previousTarget = camera.targetTexture;
            try
            {
                camera.transform.position = position;
                camera.transform.LookAt(lookAt);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1400, 1000), 0, 0);
                image.Apply();
                Directory.CreateDirectory("Diagnostics");
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previous;
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(target);
            }
        }
    }
}
