using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public sealed class LocalConstructionSiteBakeTests
    {
        /// <summary>Bake the real local Cesium work face when explicitly enabled for a one-time authoring run.</summary>
        /// <returns>Scene loading, dynamic survey, editor asset capture and cleanup.</returns>
        [UnityTest]
        public IEnumerator BakeRealLocalTilesetWhenRequested()
        {
            if (Environment.GetEnvironmentVariable("ZX200_BAKE_LOCAL_SITE") != "1")
                Assert.Ignore("Set ZX200_BAKE_LOCAL_SITE=1 and use the DynamicDeveloper scene to author a site.");
            const string path = "Assets/Scenes/sence_test.unity";
            // The imported preview Terrain has three legacy tree slots with missing prefabs.
            // They are unrelated to the locally sampled construction surface.
            for (int index = 0; index < 3; index++)
                LogAssert.Expect(LogType.Error, $"Tree prefab at index {index} is missing.");
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                SceneManager.SetActiveScene(scene);
                MonoBehaviour site = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                    .Single(component => component != null && component.GetType().Name == "CesiumMoundSite");
                PropertyInfo ready = site.GetType().GetProperty("IsReady");
                PropertyInfo status = site.GetType().GetProperty("Status");
                float deadline = Time.realtimeSinceStartup + 90f;
                while (!(bool)ready.GetValue(site) && Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.That((bool)ready.GetValue(site), Is.True, status.GetValue(site).ToString());
                Type baker = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("ConstructionSiteBaker"))
                    .First(type => type != null);
                baker.GetMethod("BakeReadyDynamicSite", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, null);
                var data = Selection.activeObject as ScriptableObject;
                Assert.That(data, Is.Not.Null);
                string assetPath = AssetDatabase.GetAssetPath(data);
                Assert.That(assetPath, Does.StartWith("Assets/ConstructionSite/Baked/SenceTest/"));
                TerrainData terrain = (TerrainData)data.GetType().GetField("terrainData").GetValue(data);
                Assert.That(AssetDatabase.Contains(terrain), Is.True);
                double volume = (double)data.GetType().GetField("initialSoilVolumeM3").GetValue(data);
                Assert.That(volume, Is.GreaterThan(0));
                Debug.Log($"[ZX200 BAKE TEST] asset={assetPath}, initial soil={volume:F3}m³");
            }
            finally
            {
                if (scene.IsValid()) SceneManager.UnloadSceneAsync(scene);
            }
        }
    }
}
