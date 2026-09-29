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
                var liveSoil = (DeformableSoilTerrain)site.GetType().GetField("soil").GetValue(site);
                Debug.Log("[ZX200 BAKE TEST] live " + DescribeLayers(liveSoil.Terrain.terrainData));
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
                Debug.Log("[ZX200 BAKE TEST] saved " + DescribeLayers(terrain));
                float[] savedLayerMaxima = GetLayerMaximums(terrain);
                Assert.That(savedLayerMaxima[0], Is.InRange(.3f, .45f),
                    "The persistent TerrainData must keep the warm soil blend after its TerrainLayers are embedded.");
                Assert.That(savedLayerMaxima[1], Is.GreaterThan(.9f),
                    "The persistent TerrainData must retain Cesium imagery coverage.");
                double volume = (double)data.GetType().GetField("initialSoilVolumeM3").GetValue(data);
                Assert.That(volume, Is.GreaterThan(0));
                Debug.Log($"[ZX200 BAKE TEST] asset={assetPath}, initial soil={volume:F3}m³");
            }
            finally
            {
                if (scene.IsValid()) SceneManager.UnloadSceneAsync(scene);
            }
        }

        private static string DescribeLayers(TerrainData terrain)
        {
            float[] maximum = GetLayerMaximums(terrain);
            string layerNames = string.Join(" / ", System.Array.ConvertAll(terrain.terrainLayers, layer => layer != null ? layer.name : "<null>"));
            return $"layers={layerNames}, resolution={terrain.alphamapResolution}, max={string.Join(",", maximum)}";
        }

        private static float[] GetLayerMaximums(TerrainData terrain)
        {
            float[,,] weights = terrain.GetAlphamaps(0, 0, terrain.alphamapResolution, terrain.alphamapResolution);
            var maximum = new float[weights.GetLength(2)];
            for (int z = 0; z < weights.GetLength(0); z++)
                for (int x = 0; x < weights.GetLength(1); x++)
                    for (int layer = 0; layer < maximum.Length; layer++)
                        maximum[layer] = Mathf.Max(maximum[layer], weights[z, x, layer]);
            return maximum;
        }
    }
}
