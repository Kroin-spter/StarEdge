using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public sealed class MapFollowingSurfaceTests
    {
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
