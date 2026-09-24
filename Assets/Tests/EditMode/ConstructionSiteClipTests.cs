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

        /// <summary>Keep only the outside part of triangles crossing the work-area boundary.</summary>
        [Test]
        public void BoundaryTrianglesAreSplitWithoutGapsOrInteriorMapCollision()
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ConstructionSiteMapClip"))
                .FirstOrDefault(t => t != null);
            var root = new GameObject("clip");
            var tile = new GameObject("boundary tile");
            var source = new Mesh();
            source.vertices = new[]
            {
                new Vector3(-6,0,-6), new Vector3(6,0,-6), new Vector3(6,0,6), new Vector3(-6,0,6)
            };
            source.subMeshCount = 2;
            source.SetTriangles(new[] { 0,2,1 }, 0);
            source.SetTriangles(new[] { 0,3,2 }, 1);
            source.normals = Enumerable.Repeat(Vector3.up, 4).ToArray();
            source.tangents = Enumerable.Repeat(new Vector4(1,0,0,1), 4).ToArray();
            source.colors = source.vertices.Select(v => new Color(v.x, v.z, .25f, 1f)).ToArray();
            source.uv = source.vertices.Select(v => new Vector2(v.x, v.z)).ToArray();
            tile.AddComponent<MeshFilter>().sharedMesh = source;
            tile.AddComponent<MeshRenderer>();
            tile.AddComponent<MeshCollider>().sharedMesh = source;
            try
            {
                Component clip = root.AddComponent(type);
                type.GetField("workArea").SetValue(clip, new Bounds(Vector3.zero, new Vector3(10,100,10)));
                type.GetMethod("ClipTile").Invoke(clip, new object[] { tile });
                Mesh result = tile.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(result, Is.Not.SameAs(source), "Crossing triangles need a clipped replacement mesh.");
                Assert.That(tile.GetComponent<MeshCollider>().sharedMesh, Is.SameAs(result),
                    "Rendering and collision must use the exact same clipped geometry.");
                Assert.That(result.subMeshCount, Is.EqualTo(2), "Clipping must preserve streamed submesh boundaries.");
                Assert.That(source.triangles.Length, Is.EqualTo(6), "Source streamed meshes must remain untouched.");
                Assert.That(result.normals.Length, Is.EqualTo(result.vertexCount));
                Assert.That(result.tangents.Length, Is.EqualTo(result.vertexCount));
                Assert.That(result.colors.Length, Is.EqualTo(result.vertexCount));
                Assert.That(result.uv.Length, Is.EqualTo(result.vertexCount));
                for (int i = 0; i < result.vertexCount; i++)
                {
                    Assert.That(result.normals[i].y, Is.EqualTo(1f).Within(.001f));
                    Assert.That(result.tangents[i].x, Is.EqualTo(1f).Within(.001f));
                    Assert.That(result.tangents[i].w, Is.EqualTo(1f).Within(.001f));
                    Assert.That(result.colors[i].r, Is.EqualTo(result.vertices[i].x).Within(.001f));
                    Assert.That(result.colors[i].g, Is.EqualTo(result.vertices[i].z).Within(.001f));
                    Assert.That(result.uv[i].x, Is.EqualTo(result.vertices[i].x).Within(.001f));
                    Assert.That(result.uv[i].y, Is.EqualTo(result.vertices[i].z).Within(.001f));
                }

                float retainedArea = HorizontalArea(result);
                Assert.That(retainedArea, Is.EqualTo(44f).Within(.001f),
                    "Only the 10x10 construction footprint should be removed from the 12x12 map patch.");

                var collider = tile.GetComponent<MeshCollider>();
                for (int sample = -9; sample <= 9; sample++)
                {
                    float offset = sample * .5f;
                    Assert.That(HasDownwardMapHit(collider, new Vector3(-5.05f, 2, offset)), Is.True,
                        $"Map collision must remain just outside the west edge at z={offset}.");
                    Assert.That(HasDownwardMapHit(collider, new Vector3(5.05f, 2, offset)), Is.True,
                        $"Map collision must remain just outside the east edge at z={offset}.");
                    Assert.That(HasDownwardMapHit(collider, new Vector3(offset, 2, -5.05f)), Is.True,
                        $"Map collision must remain just outside the south edge at x={offset}.");
                    Assert.That(HasDownwardMapHit(collider, new Vector3(offset, 2, 5.05f)), Is.True,
                        $"Map collision must remain just outside the north edge at x={offset}.");
                }
                for (int x = -9; x <= 9; x++)
                for (int z = -9; z <= 9; z++)
                    Assert.That(HasDownwardMapHit(collider, new Vector3(x * .5f, 2, z * .5f)), Is.False,
                        $"The original Cesium surface must not collide inside the replacement at ({x * .5f},{z * .5f}).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(tile);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        /// <summary>Clear an entirely replaced map tile from physics and restore its source collider on cleanup.</summary>
        [Test]
        public void FullyCoveredTileHasNoEmptyMeshColliderAndRestoresSource()
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ConstructionSiteMapClip"))
                .FirstOrDefault(t => t != null);
            var root = new GameObject("clip");
            var tile = new GameObject("covered tile");
            var source = new Mesh
            {
                vertices = new[] { new Vector3(-1,0,-1), new Vector3(1,0,-1), new Vector3(0,0,1) },
                triangles = new[] { 0,2,1 }
            };
            tile.AddComponent<MeshFilter>().sharedMesh = source;
            tile.AddComponent<MeshRenderer>();
            var collider = tile.AddComponent<MeshCollider>();
            collider.sharedMesh = source;
            try
            {
                Component clip = root.AddComponent(type);
                type.GetField("workArea").SetValue(clip, new Bounds(Vector3.zero, new Vector3(10,100,10)));
                type.GetMethod("ClipTile").Invoke(clip, new object[] { tile });
                Assert.That(tile.GetComponent<MeshFilter>().sharedMesh.triangles, Is.Empty);
                Assert.That(collider.sharedMesh, Is.Null,
                    "A wholly replaced tile should not attach an invalid empty mesh to PhysX.");

                type.GetMethod("OnDisable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(clip, null);
                Assert.That(collider.sharedMesh, Is.SameAs(source),
                    "Disabling the clip must restore a source mesh even when its clipped collider was cleared.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(tile);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        /// <summary>Triangulate and clip a mixed quad/line tile instead of skipping its whole mesh.</summary>
        [Test]
        public void MixedTopologyTileClipsQuadSurfaceAndPreservesLineSubmesh()
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ConstructionSiteMapClip"))
                .FirstOrDefault(t => t != null);
            var root = new GameObject("clip");
            var tile = new GameObject("mixed topology tile");
            var source = new Mesh();
            source.vertices = new[]
            {
                new Vector3(-6,0,-6), new Vector3(6,0,-6), new Vector3(6,0,6), new Vector3(-6,0,6)
            };
            source.subMeshCount = 2;
            source.SetIndices(new[] { 0,1,2,3 }, MeshTopology.Quads, 0);
            source.SetIndices(new[] { 0,2 }, MeshTopology.Lines, 1);
            var collider = tile.AddComponent<MeshCollider>();
            collider.enabled = false;
            tile.AddComponent<MeshFilter>().sharedMesh = source;
            tile.AddComponent<MeshRenderer>();
            collider.sharedMesh = source;
            try
            {
                Component clip = root.AddComponent(type);
                type.GetField("workArea").SetValue(clip, new Bounds(Vector3.zero, new Vector3(10,100,10)));
                type.GetMethod("ClipTile").Invoke(clip, new object[] { tile });
                Mesh result = tile.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(result, Is.Not.SameAs(source),
                    "A line submesh must not prevent the quad terrain surface from being clipped.");
                Assert.That(HorizontalArea(result), Is.EqualTo(44f).Within(.001f));
                Assert.That(result.subMeshCount, Is.EqualTo(2));
                Assert.That(result.GetTopology(0), Is.EqualTo(MeshTopology.Triangles),
                    "The clipped quad surface must be converted into collider-compatible triangles.");
                Assert.That(result.GetTopology(1), Is.EqualTo(MeshTopology.Lines));
                Assert.That(result.GetIndices(1), Is.EqualTo(new[] { 0, 2 }));
                Mesh collisionMesh = collider.sharedMesh;
                Assert.That(collisionMesh, Is.Not.SameAs(result));
                Assert.That(collisionMesh.name, Does.Contain("triangle collision only"));
                Assert.That(collisionMesh.subMeshCount, Is.EqualTo(1));
                Assert.That(collisionMesh.GetTopology(0), Is.EqualTo(MeshTopology.Triangles));
                Assert.That(HorizontalArea(collisionMesh), Is.EqualTo(44f).Within(.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(tile);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        private static bool HasDownwardMapHit(MeshCollider collider, Vector3 origin) =>
            collider.Raycast(new Ray(origin, Vector3.down), out _, 4f);

        private static float HorizontalArea(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            float area = 0f;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                int[] triangles = mesh.GetTriangles(submesh);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                    area += Mathf.Abs((b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x)) * .5f;
                }
            }
            return area;
        }
    }
}
