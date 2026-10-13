using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public sealed class MapFollowingSurfaceTests
    {
        /// <summary>The truck stays on the lower mine floor instead of being lifted onto the excavator's mound.</summary>
        [Test]
        public void PreviewTruckRemainsOnLowerParkingGround()
        {
            var snapshot = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset");
            Assert.That(snapshot.selectedSupportY - snapshot.selectedTruckGroundY, Is.GreaterThanOrEqualTo(.8f));
        }

        /// <summary>Keep the long truck footprint along the lower terrace rather than across the connecting descent.</summary>
        [Test]
        public void PreviewTruckFootprintFollowsLowerTerrace()
        {
            var snapshot = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset");
            Vector3 separation = snapshot.truckPosition - snapshot.machinePosition;
            separation.y = 0f;
            Vector3 forward = snapshot.truckRotation * Vector3.forward;
            forward.y = 0f;
            Assert.That(Mathf.Abs(Vector3.Dot(separation.normalized, forward.normalized)),
                Is.LessThan(.15f), "矿卡长轴不应跨在两车之间的下降坡上。");
        }

        /// <summary>The cab faces out of the corner; the bed, not the cab, lies toward the digging slope.</summary>
        [Test]
        public void PreviewTruckCabFacesAwayFromDiggingSlope()
        {
            var snapshot = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset");
            Vector3 diggingDirection = snapshot.machineRotation * Vector3.forward;
            Vector3 truckForward = snapshot.truckRotation * Vector3.forward;
            diggingDirection.y = truckForward.y = 0f;
            Assert.That(Vector3.Dot(diggingDirection.normalized, truckForward.normalized),
                Is.LessThan(-.8f), "车头应朝外，不能朝挖掘坡面。");
        }

        /// <summary>A rejected parking fit must leave the terrain and soil ledger exactly as they were.</summary>
        [Test]
        public void FailedLoadingGroundFitLeavesTerrainAndLedgerUntouched()
        {
            var source = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset");
            var snapshot = UnityEngine.Object.Instantiate(source);
            snapshot.terrainData = UnityEngine.Object.Instantiate(source.terrainData);
            snapshot.groundHeights = (float[])source.groundHeights.Clone();
            snapshot.soilMask = (float[])source.soilMask.Clone();
            var root = new GameObject("invalid loading parking");
            var truck = new GameObject("truck without wheels");
            try
            {
                var site = root.AddComponent<CesiumMoundSite>();
                site.truck = truck.transform;
                int n = snapshot.terrainData.heightmapResolution;
                float[,] heights = snapshot.terrainData.GetHeights(0, 0, n, n);
                float[] floor = (float[])snapshot.groundHeights.Clone();
                float[] mask = (float[])snapshot.soilMask.Clone();
                double volume = snapshot.initialSoilVolumeM3;
                Type baker = typeof(MapFollowingSurfaceTests).Assembly.GetType("MoundTopPreviewBaker");
                if (baker == null)
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    { baker = assembly.GetType("MoundTopPreviewBaker"); if (baker != null) break; }
                MethodInfo method = baker.GetMethod("BlendLoadingGround", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(method, Is.Not.Null, "需要可验证且失败不留半成品的地形准备步骤。");
                var failure = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { site, snapshot }));
                Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(snapshot.terrainData.GetHeights(0, 0, n, n), Is.EqualTo(heights));
                Assert.That(snapshot.groundHeights, Is.EqualTo(floor));
                Assert.That(snapshot.soilMask, Is.EqualTo(mask));
                Assert.That(snapshot.initialSoilVolumeM3, Is.EqualTo(volume));
                Assert.That(snapshot.truckPosition, Is.EqualTo(source.truckPosition));
                Assert.That(snapshot.truckRotation, Is.EqualTo(source.truckRotation));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(truck);
                UnityEngine.Object.DestroyImmediate(snapshot.terrainData);
                UnityEngine.Object.DestroyImmediate(snapshot);
            }
        }

        /// <summary>The loading corridor must not contain a cliff or a trench between the two machines.</summary>
        [Test]
        public void LoadingCorridorHasNoAbruptTrench()
        {
            var snapshot = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset");
            Vector3 direction = snapshot.truckPosition - snapshot.machinePosition;
            direction.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, direction.normalized);
            float length = direction.magnitude;
            float maximumGrade = 0f;
            Vector3 worst = Vector3.zero;
            for (int offset = -3; offset <= 3; offset++)
            {
                float previous = 0f;
                for (int step = 0; step <= 100; step++)
                {
                    Vector3 world = snapshot.machinePosition + direction * (step / 100f) + side * (offset * .25f);
                    float height = snapshot.terrainData.GetInterpolatedHeight(
                        (world.x - snapshot.terrainOrigin.x) / snapshot.terrainData.size.x,
                        (world.z - snapshot.terrainOrigin.z) / snapshot.terrainData.size.z);
                    if (step > 0)
                    {
                        float grade = Mathf.Abs(height - previous) / (length / 100f);
                        if (grade > maximumGrade) { maximumGrade = grade; worst = world; }
                    }
                    previous = height;
                }
            }
            // The truck now belongs on the lower floor; the inter-equipment slope is not its access road.
            Assert.That(maximumGrade, Is.LessThan(Mathf.Tan(30f * Mathf.Deg2Rad)), "连接坡不应有断沟或陡坎，最陡点=" + worst);
        }

        /// <summary>Parking must remain nearly level instead of selecting a steep pose just to fit wheel support.</summary>
        [Test]
        public void PreviewTruckParksNearlyLevel()
        {
            var snapshot = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset");
            Vector3 angles = snapshot.truckRotation.eulerAngles;
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(0f, angles.x)), Is.LessThanOrEqualTo(5.01f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(0f, angles.z)), Is.LessThanOrEqualTo(3.01f));
        }

        /// <summary>Gravity discharge must intersect the real bed plane when the truck follows a mild slope.</summary>
        [Test]
        public void TiltedTruckReceivesOnItsActualPlane()
        {
            var root = new GameObject("tilted receiving plane");
            try
            {
                var zone = root.AddComponent<SoilReceivingZone>();
                zone.size = new Vector2(2f, 4f);
                root.transform.rotation = Quaternion.Euler(12f, 25f, 2f);
                Vector3 expected = root.transform.TransformPoint(new Vector3(.3f, 0f, .7f));
                Assert.That(zone.TryGetLanding(expected + Vector3.up * 2f, 3f, out Vector3 actual), Is.True);
                Assert.That(Vector3.Distance(expected, actual), Is.LessThan(.0001f));
                Vector3 outside = root.transform.TransformPoint(new Vector3(1.2f, 0f, .7f));
                Assert.That(zone.TryGetLanding(outside + Vector3.up * 2f, 3f, out _), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        /// <summary>The truck must follow a natural surface, not a broad constant-height parking slab.</summary>
        [Test]
        public void TruckParkingRetainsNaturalHeightVariation()
        {
            var snapshot = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset");
            float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
            for (int z = -2; z <= 2; z++)
                for (int x = -1; x <= 1; x++)
                {
                    Vector3 world = snapshot.truckPosition + snapshot.truckRotation * new Vector3(x * .6f, 0f, z * .7f);
                    float height = snapshot.terrainData.GetInterpolatedHeight(
                        (world.x - snapshot.terrainOrigin.x) / snapshot.terrainData.size.x,
                        (world.z - snapshot.terrainOrigin.z) / snapshot.terrainData.size.z);
                    minimum = Mathf.Min(minimum, height);
                    maximum = Mathf.Max(maximum, height);
                }
            Assert.That(maximum - minimum, Is.GreaterThan(.03f), "不能重新生成矩形等高停车平台。");
        }

        /// <summary>The relocated receiving bed must sit closer without consuming the protected track-bearing core.</summary>
        [Test]
        public void PreviewTruckIsInsideNearLoadingRange()
        {
            var snapshot = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset");
            float distance = Vector2.Distance(new Vector2(snapshot.machinePosition.x, snapshot.machinePosition.z),
                new Vector2(snapshot.truckPosition.x, snapshot.truckPosition.z));
            Assert.That(distance, Is.InRange(5.5f, 6.5f), "车厢距离须兼顾实际斗尖可达范围和设备净距");
        }

        /// <summary>Opaque image alpha must never make baked mine soil polished in Unity 6.</summary>
        [Test]
        public void PreviewSoilUsesConstantMatteSmoothness()
        {
            var snapshot = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset");
            Assert.That(snapshot, Is.Not.Null);
            foreach (TerrainLayer layer in snapshot.terrainData.terrainLayers)
            {
                Assert.That(layer.smoothnessSource, Is.EqualTo(TerrainLayerSmoothnessSource.ConstantOnly));
                Assert.That(layer.smoothness, Is.Zero);
                Assert.That(layer.metallic, Is.Zero);
            }
        }

        /// <summary>Thin scanned shards must not survive as abrupt jumps in the front soil height field.</summary>
        [Test]
        public void FrontSoilHasNoThinHeightSpikes()
        {
            var snapshot = UnityEditor.AssetDatabase.LoadAssetAtPath<BakedConstructionSiteData>(
                "Assets/ConstructionSite/Baked/MapFollowingPreview/ConstructionSite.asset");
            TerrainData data = snapshot.terrainData;
            int n = data.heightmapResolution;
            float[,] heights = data.GetHeights(0, 0, n, n);
            float maximum = 0f;
            Vector2 worst = Vector2.zero;
            for (int z = 1; z < n - 1; z++)
                for (int x = 1; x < n - 1; x++)
                {
                    float worldX = snapshot.terrainOrigin.x + x * data.size.x / (n - 1);
                    float worldZ = snapshot.terrainOrigin.z + z * data.size.z / (n - 1);
                    if (worldX < 317.3f || worldX > 327f || worldZ < 338f || worldZ > 344f) continue;
                    float jump = Mathf.Max(Mathf.Abs(heights[z, x] - heights[z, x + 1]),
                        Mathf.Abs(heights[z, x] - heights[z + 1, x])) * data.size.y;
                    if (jump > maximum) { maximum = jump; worst = new Vector2(worldX, worldZ); }
                }
            Assert.That(maximum, Is.LessThan(.3f), "前部窄尖刺位置 " + worst);
        }

        /// <summary>A global reprofile instead of a local repair must fail by changing unmasked ground.</summary>
        [Test]
        public void RepairRemovesEquipmentBumpWithoutChangingSurroundingSlope()
        {
            Type type = typeof(CesiumMoundSite).Assembly.GetType("MapFollowingSoilSurface");
            Assert.That(type, Is.Not.Null, "缺少原地图局部地表修补");
            var heights = new float[9, 9];
            var mask = new bool[9, 9];
            for (int z = 0; z < 9; z++)
                for (int x = 0; x < 9; x++)
                {
                    heights[z, x] = x * .5f + z * .25f;
                    mask[z, x] = x >= 3 && x <= 5 && z >= 3 && z <= 5;
                }
            heights[4, 4] = 20f;
            float[,] result = (float[,])type.GetMethod("RepairHeights").Invoke(null, new object[] { heights, mask });
            Assert.That(result[4, 4], Is.EqualTo(3f).Within(.001f));
            Assert.That(result[3, 5], Is.EqualTo(3.25f).Within(.001f));
            Assert.That(heights[4, 4], Is.EqualTo(20f), "不得改写输入地图");
            for (int z = 0; z < 9; z++)
                for (int x = 0; x < 9; x++)
                    if (!mask[z, x]) Assert.That(result[z, x], Is.EqualTo(heights[z, x]));
        }

        /// <summary>A rectangular broad-phase cut must not remove an original map corner outside the actual boundary.</summary>
        [Test]
        public void PolygonClipPreservesMapCornerOutsideReplacement()
        {
            var root = new GameObject("polygon clip");
            var tile = new GameObject("corner tile");
            var mesh = new Mesh
            {
                vertices = new[] { new Vector3(-.1f, 0, -.1f), new Vector3(.1f, 0, -.1f), new Vector3(0, 0, .1f),
                    new Vector3(1.5f, 0, 1.5f), new Vector3(1.8f, 0, 1.5f), new Vector3(1.5f, 0, 1.8f) },
                triangles = new[] { 0, 2, 1, 3, 5, 4 }
            };
            tile.AddComponent<MeshFilter>().sharedMesh = mesh;
            tile.AddComponent<MeshRenderer>();
            tile.AddComponent<MeshCollider>().sharedMesh = mesh;
            try
            {
                var clip = root.AddComponent<ConstructionSiteMapClip>();
                clip.SetWorkFaceFootprint(Vector3.zero, Quaternion.identity, new Vector2(4, 4), -1, 1);
                FieldInfo field = typeof(ConstructionSiteMapClip).GetField("workAreaPolygonXZ");
                Assert.That(field, Is.Not.Null, "裁剪必须支持真实非矩形边界");
                field.SetValue(clip, new[] { new Vector2(0,-2), new Vector2(2,0), new Vector2(0,2), new Vector2(-2,0) });
                clip.ClipTile(tile);
                Assert.That(tile.GetComponent<MeshFilter>().sharedMesh.triangles, Is.EqualTo(new[] { 3, 5, 4 }));
                Assert.That(tile.GetComponent<MeshCollider>().sharedMesh.triangles, Is.EqualTo(new[] { 3, 5, 4 }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(tile);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
    }
}
