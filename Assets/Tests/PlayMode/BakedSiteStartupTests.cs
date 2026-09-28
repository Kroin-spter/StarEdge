using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public sealed class BakedSiteStartupTests
    {
        private bool previousLogPolicy;

        /// <summary>Allow unrelated legacy scene import errors while validating explicit site state.</summary>
        [SetUp]
        public void SetUp()
        {
            previousLogPolicy = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
        }

        /// <summary>Restore the Unity Test Framework log policy after this isolated fixture.</summary>
        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = previousLogPolicy;
        }

        /// <summary>Five offline starts must use the saved soil source without surveying Cesium or changing that source.</summary>
        /// <returns>Five complete scene loads, local-soil checks and unloads.</returns>
        [UnityTest]
        public IEnumerator FiveOfflineStartsUseIndependentTerrainCopies()
        {
            const string scenePath = "Assets/Scenes/sence_test.unity";
            const string assetPath = "Assets/ConstructionSite/Baked/SenceTest/ConstructionSite.asset";
            var baked = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            Assert.That(baked, Is.Not.Null);
            var source = (TerrainData)baked.GetType().GetField("terrainData").GetValue(baked);
            Assert.That(source, Is.Not.Null);
            int middle = source.heightmapResolution / 2;
            float sourceHeight = source.GetHeight(middle, middle);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                float started = Time.realtimeSinceStartup;
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode(scenePath,
                    new LoadSceneParameters(LoadSceneMode.Additive));
                Scene scene = SceneManager.GetSceneByPath(scenePath);
                Assert.That(scene.IsValid(), Is.True);
                SceneManager.SetActiveScene(scene);
                MonoBehaviour site = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                    .Single(component => component != null && component.GetType().Name == "CesiumMoundSite");
                float deadline = Time.realtimeSinceStartup + 8f;
                while (!(bool)site.GetType().GetProperty("IsReady").GetValue(site) &&
                    Time.realtimeSinceStartup < deadline)
                    yield return null;
                string status = (string)site.GetType().GetProperty("Status").GetValue(site);
                Assert.That((bool)site.GetType().GetProperty("IsReady").GetValue(site), Is.True, status);
                Assert.That((bool)site.GetType().GetProperty("MapSurfacePrepared").GetValue(site), Is.True);
                Assert.That((int)site.GetType().GetProperty("SurveyFrameYieldCount").GetValue(site), Is.Zero);
                var soil = (DeformableSoilTerrain)site.GetType().GetField("soil").GetValue(site);
                Assert.That(soil.SourceTerrainData, Is.SameAs(source));
                Assert.That(soil.Terrain.terrainData, Is.Not.SameAs(source));
                if (attempt == 0)
                {
                    Vector3 point = soil.transform.position + new Vector3(source.size.x * .5f, 0,
                        source.size.z * .5f);
                    Assert.That(soil.Deposit(point, 1f, .05), Is.GreaterThan(0));
                    soil.ResetSoil();
                }
                float elapsed = Time.realtimeSinceStartup - started;
                Debug.Log($"[ZX200 BAKED START] attempt={attempt + 1}/5, elapsed={elapsed:F3}s, status={status}");
                Assert.That(source.GetHeight(middle, middle), Is.EqualTo(sourceHeight).Within(.00001f));
                AsyncOperation unload = SceneManager.UnloadSceneAsync(scene);
                if (unload != null) yield return unload;
            }
        }
    }
}
