using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public sealed class BakedConstructionSiteTests
    {
        private TerrainData terrainData;
        private BakedConstructionSiteData baked;
        private GameObject ground;
        private GameObject georeferenceRoot;
        private Material terrainMaterial;
        private Texture2D mapTexture;
        private TerrainLayer soilLayer;
        private TerrainLayer mapLayer;

        [SetUp]
        public void SetUp()
        {
            terrainData = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++) heights[z, x] = .4f;
            terrainData.SetHeights(0, 0, heights);
            mapTexture = new Texture2D(2, 2);
            mapTexture.SetPixels(new[] { new Color(.48f, .4f, .31f), new Color(.52f, .44f, .34f),
                new Color(.45f, .38f, .3f), new Color(.5f, .42f, .33f) });
            mapTexture.Apply();
            soilLayer = new TerrainLayer { name = "Test soil" };
            mapLayer = new TerrainLayer { name = "Test Cesium imagery", diffuseTexture = mapTexture };
            terrainData.terrainLayers = new[] { soilLayer, mapLayer };
            baked = ScriptableObject.CreateInstance<BakedConstructionSiteData>();
            baked.terrainData = terrainData;
            terrainMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/ConstructionSite/Terrain.mat");
            Assert.That(terrainMaterial, Is.Not.Null, "The test fixture requires the project's persistent terrain material.");
            baked.terrainMaterial = terrainMaterial;
            baked.sourceMapName = "example-map";
            baked.terrainOrigin = new Vector3(10, 7, 20);
            baked.georeferenceLatitude = 43.5;
            baked.georeferenceLongitude = 81.5;
            baked.georeferenceHeight = 1322.8;
            baked.workFaceCenter = baked.terrainOrigin + new Vector3(2f, 0f, 2f);
            baked.workFaceRotation = Quaternion.Euler(0f, 25f, 0f);
            baked.workFaceSize = new Vector2(2f, 2f);
            baked.machineRotation = Quaternion.identity;
            baked.truckRotation = Quaternion.identity;
            baked.groundHeights = new float[33 * 33];
            baked.soilMask = new float[33 * 33];
            for (int i = 0; i < baked.groundHeights.Length; i++)
            {
                baked.groundHeights[i] = .2f;
                baked.soilMask[i] = 1f;
            }
            baked.initialSoilVolumeM3 = 6.4;
            terrainData.SetHoles(0, 0, ConstructionSiteFootprint.BuildTerrainHoles(
                terrainData.holesResolution, baked.terrainOrigin, terrainData.size,
                baked.workFaceCenter, baked.workFaceRotation, baked.workFaceSize));
        }

        [TearDown]
        public void TearDown()
        {
            if (ground != null) Object.DestroyImmediate(ground);
            if (georeferenceRoot != null) Object.DestroyImmediate(georeferenceRoot);
            Object.DestroyImmediate(baked);
            Object.DestroyImmediate(terrainData);
            Object.DestroyImmediate(soilLayer);
            Object.DestroyImmediate(mapLayer);
            Object.DestroyImmediate(mapTexture);
        }

        /// <summary>Authoring reconciliation keeps sculpted heights but restores a valid nonnegative soil ledger.</summary>
        [Test]
        public void ReconcileManualSculptPreservesSurfaceAndRepairsFloorAndVolume()
        {
            float[,] heights = terrainData.GetHeights(0, 0, 33, 33);
            heights[8, 8] = .1f;
            heights[9, 9] = .6f;
            baked.soilMask[9 * 33 + 9] = 0;
            baked.groundHeights[9 * 33 + 9] = .4f;
            terrainData.SetHeights(0, 0, heights);
            float[,] edited = terrainData.GetHeights(0, 0, 33, 33);
            Assert.That(baked.TryValidate("example-map", 43.5, 81.5, 1322.8, out _), Is.False);
            MethodInfo reconcile = typeof(BakedConstructionSiteData).GetMethod("ReconcileEditedSurface");
            Assert.That(reconcile, Is.Not.Null, "手工改高程后必须同步土层账本，而非关闭校验。");
            reconcile.Invoke(baked, null);
            Assert.That(baked.TryValidate("example-map", 43.5, 81.5, 1322.8, out string reason), Is.True, reason);
            Assert.That(terrainData.GetHeights(0, 0, 33, 33), Is.EqualTo(edited));
            Assert.That(baked.groundHeights[8 * 33 + 8], Is.EqualTo(edited[8, 8]));
            Assert.That(baked.groundHeights[9 * 33 + 9], Is.EqualTo(edited[9, 9]));
            Assert.That(baked.groundHeights[10 * 33 + 10], Is.EqualTo(.2f));
            Assert.That(baked.initialSoilVolumeM3, Is.GreaterThan(6.3).And.LessThan(6.4));
        }

        [Test]
        public void WorkFaceBoundsUseTheSlopeFootprintInsteadOfTheFullTerrainPatch()
        {
            Bounds bounds = baked.GetWorkFaceBounds();
            Vector3 machineSupport = baked.workFaceCenter - baked.workFaceRotation * Vector3.forward * 3f;

            Assert.That(ConstructionSiteFootprint.ContainsHorizontal(machineSupport, baked.workFaceCenter,
                baked.workFaceRotation, baked.workFaceSize), Is.False);
            Assert.That(bounds.size.x, Is.LessThan(terrainData.size.x));
            Assert.That(bounds.size.z, Is.LessThan(terrainData.size.z));
        }

        [Test]
        public void CorruptBaselineAndWrongMapAreRejectedWithReasons()
        {
            Assert.That(baked.TryValidate("example-map", 43.5, 81.5, 1322.8, out string validReason),
                Is.True, validReason);
            Assert.That(baked.TryValidate("other-map", 43.5, 81.5, 1322.8, out string mapReason), Is.False);
            Assert.That(mapReason, Does.Contain("地图"));
            baked.groundHeights[16 * 33 + 16] = .6f;
            Assert.That(baked.TryValidate("example-map", 43.5, 81.5, 1322.8, out string floorReason), Is.False);
            Assert.That(floorReason, Does.Contain("基础"));
        }

        [Test]
        public void MissingTerrainMaterialIsRejectedWithRepairReason()
        {
            baked.terrainMaterial = null;

            Assert.That(baked.TryValidate("example-map", 43.5, 81.5, 1322.8, out string reason), Is.False);
            Assert.That(reason, Does.Contain("材质"));
        }

        [Test]
        public void RuntimeCutAndResetNeverChangeBakedTerrainOrFloor()
        {
            ground = Terrain.CreateTerrainGameObject(terrainData);
            var soil = ground.AddComponent<DeformableSoilTerrain>();
            baked.ApplyTo(soil);
            Assert.That(ground.GetComponent<Terrain>().terrainData, Is.Not.SameAs(terrainData));
            Vector3 point = baked.terrainOrigin + new Vector3(2, 0, 2);
            Assert.That(soil.CutSweep(point + Vector3.up * .5f, point + Vector3.up * .5f, 1, 2), Is.GreaterThan(0));
            Assert.That(terrainData.GetHeight(16, 16), Is.EqualTo(.8f).Within(.0001f));
            Assert.That(soil.GetFloorWorldY(point), Is.EqualTo(7.4f).Within(.0001f));
            soil.ResetSoil();
            Assert.That(ground.GetComponent<Terrain>().terrainData.GetHeight(16, 16),
                Is.EqualTo(.8f).Within(.0001f));
            Assert.That(baked.groundHeights[16 * 33 + 16], Is.EqualTo(.2f));
        }

        [Test]
        public void CutThenDepositWithZeroNetVolumeCannotBeBaked()
        {
            ground = Terrain.CreateTerrainGameObject(terrainData);
            ground.GetComponent<Terrain>().materialTemplate = terrainMaterial;
            var soil = ground.AddComponent<DeformableSoilTerrain>();
            soil.PrepareMoundSurface(terrainData.GetHeights(0, 0, 33, 33),
                FilledHeights(.2f));
            Vector3 point = baked.terrainOrigin + new Vector3(2f, 0, 2f);
            soil.transform.position = baked.terrainOrigin;
            double removed = soil.CutSweep(point + Vector3.up * .5f,
                point + Vector3.up * .5f, .6f, 1f);
            Assert.That(removed, Is.GreaterThan(0));
            double deposited = soil.Deposit(point, .6f, removed);
            Assert.That(deposited, Is.GreaterThan(0));

            // Terrain height quantization may leave a tiny volume remainder. Set one distant
            // vertex so the net integral cancels exactly while the cut and deposit remain visible.
            TerrainData runtime = soil.Terrain.terrainData;
            float[,] heights = runtime.GetHeights(0, 0, 33, 33);
            double net = soil.RecalculateRemovedVolumeM3();
            double vertexVolume = runtime.size.x * runtime.size.y * runtime.size.z / (32d * 32d);
            heights[8, 8] += (float)(net / vertexVolume);
            runtime.SetHeights(0, 0, heights);
            Assert.That(System.Math.Abs(soil.RecalculateRemovedVolumeM3()), Is.LessThan(1e-5));

            var site = ground.AddComponent<CesiumMoundSite>();
            georeferenceRoot = new GameObject("test georeference");
            var georeference = georeferenceRoot.AddComponent(FindCesiumType("CesiumGeoreference"));
            ((Behaviour)georeference).enabled = false;
            var map = new GameObject("test map");
            map.transform.SetParent(georeferenceRoot.transform);
            var tileset = map.AddComponent(FindCesiumType("Cesium3DTileset"));
            ((Behaviour)tileset).enabled = false;
            site.GetType().GetField("tileset").SetValue(site, tileset);
            site.siteMode = CesiumMoundSite.SitePreparationMode.DynamicDeveloper;
            site.soil = soil;
            var truck = new GameObject("test truck");
            truck.transform.SetParent(ground.transform);
            site.truck = truck.transform;
            var machineRoot = new GameObject("test articulation root");
            machineRoot.transform.SetParent(ground.transform);
            site.GetType().GetField("root", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(site, machineRoot.AddComponent<ArticulationBody>());
            typeof(CesiumMoundSite).GetProperty("IsReady").SetValue(site, true);
            typeof(CesiumMoundSite).GetField("preparedSoilMask", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(site, FilledHeights(1f));
            var target = ScriptableObject.CreateInstance<BakedConstructionSiteData>();
            target.terrainData = Object.Instantiate(runtime);
            try
            {
                Assert.That(site.TryCaptureBakedData(target, out string reason), Is.False);
                Assert.That(reason, Does.Contain("尚未挖动"));
            }
            finally
            {
                Object.DestroyImmediate(target.terrainData);
                Object.DestroyImmediate(target);
            }
        }

        private static float[,] FilledHeights(float value)
        {
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++) heights[z, x] = value;
            return heights;
        }

        private static System.Type FindCesiumType(string name)
        {
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type type = assembly.GetType("CesiumForUnity." + name);
                if (type != null) return type;
            }
            throw new System.InvalidOperationException("Missing Cesium component: " + name);
        }

        [Test]
        public void MissingBakedAssetFailsWithoutDynamicFallback()
        {
            Assert.That(CesiumMoundSite.TryValidateBakedConfiguration(null, "example-map", 43.5, 81.5,
                1322.8, out string reason), Is.False);
            Assert.That(reason, Does.Contain("烘焙"));
        }
    }
}
