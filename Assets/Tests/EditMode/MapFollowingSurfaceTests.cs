using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public sealed class MapFollowingSurfaceTests
    {
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
