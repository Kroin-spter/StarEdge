using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class ConstructionSiteClipTests
    {
        /// <summary>Expired sampling must release clipping while preserving the original soil baseline.</summary>
        [Test]
        public void SamplingTimeoutRestoresClippingWithoutChangingSoil()
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CesiumTerrainConformer"))
                .First(t => t != null);
            Type clipType = type.Assembly.GetType("ConstructionSiteMapClip");
            var map = new GameObject("empty tileset");
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var ground = Terrain.CreateTerrainGameObject(data);
            try
            {
                var soil = ground.AddComponent<DeformableSoilTerrain>();
                var clip = (Behaviour)map.AddComponent(clipType);
                var tileset = map.AddComponent(type.GetField("tileset").FieldType);
                var conformer = ground.AddComponent(type);
                type.GetField("soil").SetValue(conformer, soil);
                type.GetField("tileset").SetValue(conformer, tileset);
                type.GetField("sampleTimeout").SetValue(conformer, -1f);
                var start = (System.Collections.IEnumerator)type.GetMethod("Start",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(conformer, null);
                Assert.That(start.MoveNext(), Is.False);
                Assert.That(type.GetProperty("SurfaceReady").GetValue(conformer), Is.True);
                Assert.That(clip.enabled, Is.True);
                Assert.That(soil.RecalculateRemovedVolumeM3(), Is.Zero);
                Assert.That(soil.Terrain.terrainData, Is.Not.SameAs(data));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ground);
                UnityEngine.Object.DestroyImmediate(map);
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        /// <summary>Catch invisible retained map collisions and accidental clipping of distant geography.</summary>
        [Test]
        public void LocalClipUpdatesRenderingAndCollisionAndPreservesOutsideTriangle()
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ConstructionSiteMapClip"))
                .FirstOrDefault(t => t != null);
            Assert.That(type, Is.Not.Null, "A scoped streamed-map clipping component is required.");
            var root = new GameObject("clip");
            var tile = new GameObject("tile");
            var source = new Mesh();
            source.vertices = new[] { new Vector3(-1,0,-1), new Vector3(1,0,-1), new Vector3(0,0,1),
                new Vector3(20,0,20), new Vector3(22,0,20), new Vector3(20,0,22) };
            source.triangles = new[] { 0,2,1,3,5,4 };
            tile.AddComponent<MeshFilter>().sharedMesh = source;
            tile.AddComponent<MeshRenderer>();
            tile.AddComponent<MeshCollider>().sharedMesh = source;
            try
            {
                Component clip = root.AddComponent(type);
                type.GetField("workArea").SetValue(clip, new Bounds(Vector3.zero, new Vector3(10,100,10)));
                type.GetMethod("ClipTile").Invoke(clip, new object[] { tile });
                Mesh result = tile.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(result.triangles, Is.EqualTo(new[] { 3,5,4 }));
                Assert.That(tile.GetComponent<MeshCollider>().sharedMesh, Is.SameAs(result));
                Assert.That(source.triangles.Length, Is.EqualTo(6), "Source meshes must remain untouched.");
                type.GetMethod("ClipTile").Invoke(clip, new object[] { tile });
                Assert.That(tile.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(result));
                UnityEngine.Object.DestroyImmediate(tile);
                type.GetMethod("Update", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(clip, null);
                Assert.That(result == null, Is.True, "Evicted streamed tiles must release their runtime clipping mesh without disabling the whole tileset");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(tile);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }
    }
}
