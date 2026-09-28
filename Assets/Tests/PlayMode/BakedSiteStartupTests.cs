using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        private const string ScenePath = "Assets/Scenes/sence_test.unity";
        private const string BakedAssetPath = "Assets/ConstructionSite/Baked/SenceTest/ConstructionSite.asset";
        private const string CesiumTilesetMissingHint =
            " Set CESIUM_TILESET_DIR or place the map beside the project.";
        private string tilesetPath;
        private bool localMapIsMissing;
        private Scene previousActiveScene;
        private Scene loadedScene;

        /// <summary>Resolve the configured local map and retain normal Unity log assertions.</summary>
        [SetUp]
        public void SetUp()
        {
            string configuredDirectory = Environment.GetEnvironmentVariable("CESIUM_TILESET_DIR");
            string tilesetDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
                ? Path.GetFullPath(Path.Combine(Application.dataPath, "../../tilesets/osgb_small_005_004"))
                : Path.GetFullPath(configuredDirectory);
            tilesetPath = Path.Combine(tilesetDirectory, "tileset.json");
            localMapIsMissing = !File.Exists(tilesetPath);
            previousActiveScene = SceneManager.GetActiveScene();
            loadedScene = default;
        }

        /// <summary>Unload an additive test scene even when an assertion fails.</summary>
        /// <returns>The asynchronous scene-unload operation, when needed.</returns>
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                SceneManager.SetActiveScene(previousActiveScene);
            if (loadedScene.IsValid() && loadedScene.isLoaded)
            {
                AsyncOperation unload = SceneManager.UnloadSceneAsync(loadedScene);
                if (unload != null) yield return unload;
            }
        }

        /// <summary>Five offline starts use independent mutable TerrainData and preserve the entire baked source.</summary>
        /// <returns>Five complete scene loads, source-integrity checks and unloads.</returns>
        [UnityTest]
        public IEnumerator FiveOfflineStartsUseIndependentTerrainCopies()
        {
            ScriptableObject baked = AssetDatabase.LoadAssetAtPath<ScriptableObject>(BakedAssetPath);
            Assert.That(baked, Is.Not.Null);
            TerrainData source = (TerrainData)baked.GetType().GetField("terrainData").GetValue(baked);
            Assert.That(source, Is.Not.Null);
            TerrainSnapshot sourceSnapshot = Capture(source);
            string bakedAssetHash = HashAssetFile(baked);
            string terrainAssetHash = HashAssetFile(source);
            float[,] initialHeights = (float[,])sourceSnapshot.Heights.Clone();

            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt == 0) ExpectKnownLegacySceneWarnings();
                ExpectThreeMissingMapWarnings();
                float loadStarted = Time.realtimeSinceStartup;
                yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                    new LoadSceneParameters(LoadSceneMode.Additive));
                float sceneLoadElapsed = Time.realtimeSinceStartup - loadStarted;
                float siteReadyStarted = Time.realtimeSinceStartup;
                loadedScene = SceneManager.GetSceneByPath(ScenePath);
                Assert.That(loadedScene.IsValid(), Is.True);
                SceneManager.SetActiveScene(loadedScene);

                MonoBehaviour site = loadedScene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                    .Single(component => component != null && component.GetType().Name == "CesiumMoundSite");
                Type siteType = site.GetType();
                float deadline = Time.realtimeSinceStartup + 8f;
                while (!(bool)siteType.GetProperty("IsReady").GetValue(site) &&
                    Time.realtimeSinceStartup < deadline) yield return null;
                string status = (string)siteType.GetProperty("Status").GetValue(site);
                Assert.That((bool)siteType.GetProperty("IsReady").GetValue(site), Is.True, status);
                Assert.That((bool)siteType.GetProperty("MapSurfacePrepared").GetValue(site), Is.True);
                Assert.That((int)siteType.GetProperty("SurveyFrameYieldCount").GetValue(site), Is.Zero);
                float siteReadyElapsed = Time.realtimeSinceStartup - siteReadyStarted;

                DeformableSoilTerrain soil = (DeformableSoilTerrain)siteType.GetField("soil").GetValue(site);
                Zx200DigController machine = FindInScene<Zx200DigController>(loadedScene);
                BucketSoilCutter cutter = FindInScene<BucketSoilCutter>(loadedScene);
                Zx200KeyboardController keyboard = machine.GetComponent<Zx200KeyboardController>();
                float poseDeadline = Time.realtimeSinceStartup + 8f;
                while (!machine.IsPoseWithinTolerance(machine.parkedPose, 2f) &&
                    Time.realtimeSinceStartup < poseDeadline) yield return new WaitForFixedUpdate();
                Assert.That(machine.IsPoseWithinTolerance(machine.parkedPose, 2f), Is.True,
                    $"The scene must start parked: actual={machine.ReadActualPose().boom:F1}/" +
                    $"{machine.ReadActualPose().arm:F1}/{machine.ReadActualPose().bucket:F1}");
                Assert.That(keyboard.articulationRoot.immovable, Is.False,
                    "A parked excavator must remain physically mobile.");
                Assert.That(cutter.StartupReady, Is.False,
                    "Parked pose must not bypass the cutter's separate work-ready interlock.");
                AssertTerrainClearance(cutter, soil.Terrain.GetComponent<TerrainCollider>());
                Assert.That(soil.SourceTerrainData, Is.SameAs(source));
                Assert.That(soil.Terrain.terrainData, Is.Not.SameAs(source));
                AssertTerrainMatches(soil.Terrain.terrainData, sourceSnapshot,
                    $"runtime initial Terrain, start {attempt + 1}");

                if (attempt == 0)
                {
                    AssertRuntimeHoleChangesAreDetected(soil.Terrain.terrainData, sourceSnapshot);
                    Vector3 point = soil.transform.position + new Vector3(source.size.x * .5f, 0,
                        source.size.z * .5f);
                    Assert.That(soil.Deposit(point, 1f, .05), Is.GreaterThan(0));
                    soil.ResetSoil();
                    AssertTerrainMatches(soil.Terrain.terrainData, sourceSnapshot,
                        "runtime Terrain after reset");

                    AutonomousExcavationController autonomy = FindInScene<AutonomousExcavationController>(loadedScene);
                    Assert.That(autonomy.StartAutonomousJob(.02), Is.True,
                        "An autonomous task must start from the parked pose.");
                    float workReadyDeadline = Time.realtimeSinceStartup + 15f;
                    while (autonomy.IsRunning && !cutter.StartupReady &&
                        Time.realtimeSinceStartup < workReadyDeadline) yield return new WaitForFixedUpdate();
                    Assert.That(autonomy.IsRunning, Is.True, autonomy.Status);
                    Assert.That(cutter.StartupReady, Is.True,
                        $"The job must move into work-ready before cutting is enabled: {autonomy.Status}");
                    Assert.That(machine.IsPoseWithinTolerance(machine.workReadyPose, 2f), Is.True);
                    autonomy.RequestManualTakeover();

                    autonomy.ResetAutonomousJob();
                    float resetDeadline = Time.realtimeSinceStartup + 10f;
                    while (autonomy.State != AutonomousExcavationState.Idle &&
                        Time.realtimeSinceStartup < resetDeadline) yield return new WaitForFixedUpdate();
                    Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Idle), autonomy.Status);
                    // The crawler is intentionally unlocked on reset; allow its small gravity-settling offset.
                    Assert.That(machine.IsPoseWithinTolerance(machine.parkedPose, 3f), Is.True,
                        $"Backspace reset must return to parked pose; actual=" +
                        $"{machine.ReadActualPose().swing:F1}/{machine.ReadActualPose().boom:F1}/" +
                        $"{machine.ReadActualPose().arm:F1}/{machine.ReadActualPose().bucket:F1}, " +
                        $"target={machine.parkedPose.swing:F1}/{machine.parkedPose.boom:F1}/" +
                        $"{machine.parkedPose.arm:F1}/{machine.parkedPose.bucket:F1}");
                    Assert.That(keyboard.articulationRoot.immovable, Is.False,
                        "Reset must release the automatic base lock.");
                }

                Debug.Log($"[ZX200 BAKED START] attempt={attempt + 1}/5, sceneLoad={sceneLoadElapsed:F3}s, " +
                    $"siteReady={siteReadyElapsed:F3}s, status={status}");
                AssertSourceUnchanged(source, sourceSnapshot, initialHeights, baked, bakedAssetHash,
                    terrainAssetHash, $"before unloading start {attempt + 1}");

                AsyncOperation unload = SceneManager.UnloadSceneAsync(loadedScene);
                if (unload != null) yield return unload;
                loadedScene = default;
                if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                    SceneManager.SetActiveScene(previousActiveScene);
                AssertSourceUnchanged(source, sourceSnapshot, initialHeights, baked, bakedAssetHash,
                    terrainAssetHash, $"after unloading start {attempt + 1}");
            }
        }

        private void ExpectThreeMissingMapWarnings()
        {
            if (!localMapIsMissing) return;
            tilesetPath = Path.GetFullPath(tilesetPath);
            string expected = $"Cesium local map is missing: {tilesetPath}.{CesiumTilesetMissingHint}";
            // CesiumLocalMapReference.OnEnable, CesiumMoundSite.Awake and Start each resolve the same absent local map.
            for (int i = 0; i < 3; i++) LogAssert.Expect(LogType.Warning, expected);
        }

        private static void ExpectKnownLegacySceneWarnings()
        {
            // These exact invalid legacy tree references and the imported ZX200 convex-hull fallback
            // are emitted once when the scene's Terrain and articulation colliders are first loaded.
            LogAssert.Expect(LogType.Error, "Tree prefab at index 0 is missing.");
            LogAssert.Expect(LogType.Error, "Tree prefab at index 1 is missing.");
            LogAssert.Expect(LogType.Error, "Tree prefab at index 2 is missing.");
            LogAssert.Expect(LogType.Warning,
                "Couldn't create a Convex Mesh from source mesh, within the maximum polygons limit (256). " +
                "The partial hull will be used. Consider simplifying your mesh. Source mesh name: body");
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            return scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<T>(true))
                .Single(component => component != null);
        }

        private static void AssertTerrainClearance(BucketSoilCutter cutter, TerrainCollider terrain)
        {
            Assert.That(cutter, Is.Not.Null);
            Assert.That(terrain, Is.Not.Null);
            float minimumClearance = float.PositiveInfinity;
            int sampledCorners = 0;
            foreach (Vector3 corner in cutter.GetBladeWorldCorners())
            {
                Ray ray = new Ray(corner + Vector3.up * 50f, Vector3.down);
                if (!terrain.Raycast(ray, out RaycastHit hit, 100f)) continue;
                sampledCorners++;
                minimumClearance = Mathf.Min(minimumClearance, corner.y - hit.point.y);
            }
            Assert.That(sampledCorners, Is.GreaterThan(0),
                "At least one blade corner must be over the baked work slope.");
            Assert.That(minimumClearance, Is.GreaterThan(.1f),
                $"The parked blade must stay clear of the work slope; measured {minimumClearance:F3}m.");
            Debug.Log($"[ZX200 PARKED CLEARANCE] corners={sampledCorners}, minimum={minimumClearance:F3}m");
        }

        private static TerrainSnapshot Capture(TerrainData source)
        {
            int heightResolution = source.heightmapResolution;
            float[,] heights = source.GetHeights(0, 0, heightResolution, heightResolution);
            int holesResolution = source.holesResolution;
            bool[,] holes = source.GetHoles(0, 0, holesResolution, holesResolution);
            TerrainLayer[] terrainLayers = source.terrainLayers ?? Array.Empty<TerrainLayer>();
            return new TerrainSnapshot(source.size, heightResolution, heights, holesResolution, holes,
                source.alphamapResolution, source.alphamapLayers,
                terrainLayers, source.treePrototypes ?? Array.Empty<TreePrototype>(), source.treeInstances);
        }

        private static void AssertTerrainMatches(TerrainData actual, TerrainSnapshot expected, string label)
        {
            Assert.That(actual.size, Is.EqualTo(expected.Size), $"{label}: Terrain size changed.");
            Assert.That(actual.heightmapResolution, Is.EqualTo(expected.HeightResolution),
                $"{label}: heightmap resolution changed.");
            Assert.That(actual.holesResolution, Is.EqualTo(expected.HolesResolution),
                $"{label}: holes resolution changed.");
            Assert.That(actual.alphamapResolution, Is.EqualTo(expected.AlphamapResolution),
                $"{label}: alphamap resolution changed.");
            Assert.That(actual.alphamapLayers, Is.EqualTo(expected.AlphamapLayers),
                $"{label}: alphamap layer count changed.");
            AssertHeightmapsEqual(actual.GetHeights(0, 0, expected.HeightResolution, expected.HeightResolution),
                expected.Heights, label);
            AssertHolemapsEqual(actual.GetHoles(0, 0, expected.HolesResolution, expected.HolesResolution),
                expected.Holes, label);
            TerrainLayer[] actualLayers = actual.terrainLayers ?? Array.Empty<TerrainLayer>();
            Assert.That(actualLayers, Is.EqualTo(expected.TerrainLayers),
                $"{label}: TerrainLayer references changed.");
            Assert.That(actual.treePrototypes.Length, Is.EqualTo(expected.TreePrototypes.Length),
                $"{label}: tree prototype count changed.");
            for (int i = 0; i < expected.TreePrototypes.Length; i++)
                Assert.That(actual.treePrototypes[i].prefab, Is.SameAs(expected.TreePrototypes[i].prefab),
                    $"{label}: tree prototype {i} changed.");
            CollectionAssert.AreEqual(expected.TreeInstances, actual.treeInstances,
                $"{label}: tree instances changed.");
        }

        private static void AssertSourceUnchanged(TerrainData source, TerrainSnapshot expected,
            float[,] initialHeights, ScriptableObject baked, string bakedAssetHash,
            string terrainAssetHash, string label)
        {
            AssertTerrainMatches(source, expected, $"baked source {label}");
            AssertHeightmapsEqual(source.GetHeights(0, 0, expected.HeightResolution, expected.HeightResolution),
                initialHeights, $"baked source heightmap {label}");
            Assert.That(HashAssetFile(baked), Is.EqualTo(bakedAssetHash),
                $"The full baked ScriptableObject asset changed {label}.");
            Assert.That(HashAssetFile(source), Is.EqualTo(terrainAssetHash),
                $"The serialized TerrainData subasset changed {label}.");
        }

        private static string HashAssetFile(UnityEngine.Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            Assert.That(path, Is.Not.Empty, $"No persistent asset path for {asset.name}.");
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)));
        }

        private static void AssertHeightmapsEqual(float[,] actual, float[,] expected, string label)
        {
            Assert.That(actual.GetLength(0), Is.EqualTo(expected.GetLength(0)), $"{label}: heightmap rows differ.");
            Assert.That(actual.GetLength(1), Is.EqualTo(expected.GetLength(1)), $"{label}: heightmap columns differ.");
            for (int z = 0; z < expected.GetLength(0); z++)
                for (int x = 0; x < expected.GetLength(1); x++)
                    if (Mathf.Abs(actual[z, x] - expected[z, x]) > 1e-6f)
                        Assert.Fail($"{label}: height sample ({x}, {z}) changed from {expected[z, x]:R} to {actual[z, x]:R}.");
        }

        private static void AssertRuntimeHoleChangesAreDetected(TerrainData runtime, TerrainSnapshot expected)
        {
            bool[,] original = runtime.GetHoles(0, 0, expected.HolesResolution, expected.HolesResolution);
            bool[,] changed = (bool[,])original.Clone();
            changed[0, 0] = !changed[0, 0];
            runtime.SetHoles(0, 0, changed);
            try
            {
                Assert.Throws<AssertionException>(() => AssertTerrainMatches(runtime, expected,
                    "deliberately changed runtime holes"));
            }
            finally
            {
                runtime.SetHoles(0, 0, original);
            }
            AssertTerrainMatches(runtime, expected, "runtime holes after probe restore");
        }

        private static void AssertHolemapsEqual(bool[,] actual, bool[,] expected, string label)
        {
            Assert.That(actual.GetLength(0), Is.EqualTo(expected.GetLength(0)),
                $"{label}: hole rows differ.");
            Assert.That(actual.GetLength(1), Is.EqualTo(expected.GetLength(1)),
                $"{label}: hole columns differ.");
            for (int z = 0; z < expected.GetLength(0); z++)
                for (int x = 0; x < expected.GetLength(1); x++)
                    if (actual[z, x] != expected[z, x])
                        Assert.Fail($"{label}: Terrain hole ({x}, {z}) changed from " +
                            $"{expected[z, x]} to {actual[z, x]}.");
        }

        private sealed class TerrainSnapshot
        {
            internal readonly Vector3 Size;
            internal readonly int HeightResolution;
            internal readonly float[,] Heights;
            internal readonly int HolesResolution;
            internal readonly bool[,] Holes;
            internal readonly int AlphamapResolution;
            internal readonly int AlphamapLayers;
            internal readonly TerrainLayer[] TerrainLayers;
            internal readonly TreePrototype[] TreePrototypes;
            internal readonly TreeInstance[] TreeInstances;

            internal TerrainSnapshot(Vector3 size, int heightResolution, float[,] heights,
                int holesResolution, bool[,] holes,
                int alphamapResolution, int alphamapLayers, TerrainLayer[] terrainLayers, TreePrototype[] treePrototypes,
                TreeInstance[] treeInstances)
            {
                Size = size;
                HeightResolution = heightResolution;
                Heights = heights;
                HolesResolution = holesResolution;
                Holes = holes;
                AlphamapResolution = alphamapResolution;
                AlphamapLayers = alphamapLayers;
                TerrainLayers = terrainLayers;
                TreePrototypes = treePrototypes;
                TreeInstances = treeInstances;
            }
        }
    }
}
