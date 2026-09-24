using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public class MoundSiteTests
    {
        /// <summary>Verify the excavator occupies a lower pit, faces a raised work strip, and uses one clipped ground.</summary>
        /// <returns>Scene loading, tile streaming and cleanup.</returns>
        [UnityTest]
        public IEnumerator OpenPitWorksiteReplacesOneCesiumFootprintWithSteppedSoil()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var all = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true)).ToArray();
                var soil = all.OfType<DeformableSoilTerrain>().Single();
                var dig = all.OfType<Zx200DigController>().Single();
                var keyboard = dig.GetComponent<Zx200KeyboardController>();
                Transform trackBase = keyboard.articulationRoot.transform;
                var truck = all.OfType<SoilReceivingZone>().Single().transform.parent;
                Component clip = all.FirstOrDefault(c => c != null && c.GetType().Name == "ConstructionSiteMapClip");
                Assert.That(clip, Is.Not.Null, "The site must install a bounded Cesium render/collision clip.");
                Bounds workArea = (Bounds)clip.GetType().GetField("workArea").GetValue(clip);
                Assert.That(soil.Terrain.terrainData.size.x, Is.GreaterThanOrEqualTo(20f));
                Assert.That(workArea.Contains(dig.transform.position), Is.True,
                    "The low pit floor must be inside the one-for-one Cesium replacement area.");
                Assert.That(workArea.Contains(truck.position), Is.True,
                    "The truck must stand on the same replaced lower work floor.");

                float lowerY = soil.Terrain.SampleHeight(trackBase.position) + soil.transform.position.y;
                Vector3 facePoint = trackBase.position + trackBase.forward * 10f;
                float upperY = soil.Terrain.SampleHeight(facePoint) + soil.transform.position.y;
                Assert.That(upperY - lowerY, Is.InRange(1.5f, 2.5f),
                    "The forward high bench must be visibly above the excavator's pit floor.");
                Vector3 stripCenter = trackBase.position + trackBase.forward * 6.7f;
                float stripCenterY = soil.Terrain.SampleHeight(stripCenter) + soil.transform.position.y;
                float stripLeftY = soil.Terrain.SampleHeight(stripCenter - trackBase.right * 4f) + soil.transform.position.y;
                float stripRightY = soil.Terrain.SampleHeight(stripCenter + trackBase.right * 4f) + soil.transform.position.y;
                float besideStripY = soil.Terrain.SampleHeight(stripCenter + trackBase.right * 6.5f) + soil.transform.position.y;
                Assert.That(stripCenterY - besideStripY, Is.GreaterThan(.2f),
                    "The cuttable soil must form a distinct narrow band along the bench, not a broad mound.");
                Assert.That(stripCenterY - besideStripY, Is.InRange(.55f, .75f),
                    "The protruding strip must remain a modest but distinct cuttable ridge above the pit floor.");
                Assert.That(stripLeftY - besideStripY, Is.GreaterThan(.1f));
                Assert.That(stripRightY - besideStripY, Is.GreaterThan(.1f));
                float truckGroundY = soil.Terrain.SampleHeight(truck.position) + soil.transform.position.y;
                Assert.That(truckGroundY, Is.EqualTo(lowerY).Within(.4f));

                Assert.That(all.Any(c => c != null && c.GetType().Name == "ConstructionSiteSurfaceAppearance"), Is.False);
                keyboard.enabled = false;
                for (int i = 0; i < 250; i++) yield return new WaitForFixedUpdate();
                var map = all.First(c => c != null && c.GetType().Name == "Cesium3DTileset" && c.gameObject.activeInHierarchy);
                var colliders = map.GetComponentsInChildren<MeshCollider>();
                Assert.That(colliders.Length, Is.GreaterThan(0));
                var clippedRenderMeshes = map.GetComponentsInChildren<MeshFilter>()
                    .Where(filter => filter.sharedMesh != null && filter.sharedMesh.name.Contains("work area clipped") &&
                        filter.GetComponent<MeshRenderer>() != null).ToArray();
                Assert.That(clippedRenderMeshes.Length, Is.GreaterThan(0),
                    "The real streamed Cesium MeshFilter must receive the clipped render mesh.");
                Assert.That(clippedRenderMeshes.Any(filter =>
                    filter.GetComponent<MeshCollider>() != null && filter.GetComponent<MeshCollider>().sharedMesh != null &&
                    filter.GetComponent<MeshCollider>().sharedMesh.name.Contains("work area clipped")), Is.True,
                    "At least one real Cesium renderer must have a matching triangle-only clipped collision mesh.");
                foreach (MeshFilter filter in clippedRenderMeshes)
                {
                    Mesh mesh = filter.sharedMesh;
                    for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    {
                        if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                        int[] triangles = mesh.GetTriangles(submesh);
                        Vector3[] vertices = mesh.vertices;
                        for (int index = 0; index < triangles.Length; index += 3)
                        {
                            Vector3 center = filter.transform.TransformPoint(
                                (vertices[triangles[index]] + vertices[triangles[index + 1]] + vertices[triangles[index + 2]]) / 3f);
                            bool strictlyInside = center.x > workArea.min.x + .001f && center.x < workArea.max.x - .001f &&
                                center.z > workArea.min.z + .001f && center.z < workArea.max.z - .001f;
                            Assert.That(strictlyInside, Is.False,
                                "The actual rendered Cesium triangles must not cover the replacement work area.");
                        }
                    }
                }
                Assert.That(colliders.Any(c => c.sharedMesh != null &&
                    c.sharedMesh.name.Contains("work area clipped")), Is.True,
                    "The actual Cesium MeshCollider must receive a clipped mesh.");
                var insideRay = new Ray(trackBase.position + Vector3.up * 20f, Vector3.down);
                Assert.That(soil.SoilCollider.Raycast(insideRay, out _, 40), Is.True,
                    "The replacement TerrainCollider must support the lower pit floor.");
                Assert.That(colliders.Any(c => c.Raycast(insideRay, out _, 40)), Is.False,
                    "The original Cesium floor must not remain as a second collider under the pit.");
                var unsupportedBoundaryPoints = new System.Collections.Generic.List<Vector3>();
                const float boundaryProbeOffset = .05f;
                for (int sample = 1; sample < 8; sample++)
                {
                    float alongX = Mathf.Lerp(workArea.min.x + 2f, workArea.max.x - 2f, sample / 8f);
                    float alongZ = Mathf.Lerp(workArea.min.z + 2f, workArea.max.z - 2f, sample / 8f);
                    Vector3[] outsideEdgePoints =
                    {
                        new Vector3(workArea.min.x - boundaryProbeOffset, trackBase.position.y, alongZ),
                        new Vector3(workArea.max.x + boundaryProbeOffset, trackBase.position.y, alongZ),
                        new Vector3(alongX, trackBase.position.y, workArea.min.z - boundaryProbeOffset),
                        new Vector3(alongX, trackBase.position.y, workArea.max.z + boundaryProbeOffset)
                    };
                    foreach (Vector3 point in outsideEdgePoints)
                    {
                        var ray = new Ray(point + Vector3.up * 20f, Vector3.down);
                        if (!colliders.Any(c => c.Raycast(ray, out _, 40)))
                            unsupportedBoundaryPoints.Add(point);
                    }
                }
                Assert.That(unsupportedBoundaryPoints, Is.Empty,
                    "Cesium map support must remain continuous immediately outside the replacement Terrain footprint. " +
                    $"Unsupported samples: {string.Join("; ", unsupportedBoundaryPoints)}");
                Vector3 outsidePoint = trackBase.position + Vector3.right * 18f + Vector3.up * 20f;
                Assert.That(colliders.Any(c => c.Raycast(new Ray(outsidePoint, Vector3.down), out _, 40)), Is.True,
                    "The original map must remain collidable just outside the local construction area.");
                foreach (var item in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)))
                {
                    if (item.name == "Solid work pad" || item.name == "Soil bottom plate (always solid)")
                        Assert.That(item.gameObject.activeInHierarchy, Is.False);
                }
                Assert.That(keyboard.fixedBase, Is.True);
                Assert.That(keyboard.articulationRoot.immovable, Is.True,
                    "The open-pit demo parks the track base while the upper articulation remains movable.");
                Collider[] trackColliders = keyboard.articulationRoot.GetComponentsInChildren<Collider>()
                    .Where(c => c.enabled && !c.isTrigger && c.attachedArticulationBody == keyboard.articulationRoot).ToArray();
                Bounds trackBounds = trackColliders[0].bounds;
                foreach (Collider track in trackColliders) trackBounds.Encapsulate(track.bounds);
                Assert.That(trackBounds.min.y, Is.EqualTo(lowerY).Within(.25f),
                    "The track base must rest directly on the replacement pit-floor collider.");
                Assert.That(soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(1e-8));
            }
            finally { SceneManager.UnloadSceneAsync(scene); }
        }
    }
}
