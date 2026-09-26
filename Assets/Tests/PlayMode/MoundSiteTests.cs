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
        public IEnumerator SelectsStableSlopeWorkFaceWithCuttableBandAndClipsOneCesiumFootprint()
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
                var site = all.Single(c => c != null && c.GetType().Name == "CesiumMoundSite");
                float stripCenterLocalZ = (float)site.GetType().GetField("soilStripCenterLocalZ").GetValue(site);
                float stripHalfWidth = (float)site.GetType().GetField("soilStripHalfWidth").GetValue(site);
                float stripHeight = (float)site.GetType().GetField("soilStripHeight").GetValue(site);
                var dig = all.OfType<Zx200DigController>().Single();
                var keyboard = dig.GetComponent<Zx200KeyboardController>();
                Transform trackBase = keyboard.articulationRoot.transform;
                var truck = all.OfType<SoilReceivingZone>().Single().transform.parent;
                Component clip = all.FirstOrDefault(c => c != null && c.GetType().Name == "ConstructionSiteMapClip");
                Assert.That(clip, Is.Not.Null, "The site must install a bounded Cesium render/collision clip.");
                Bounds workArea = (Bounds)clip.GetType().GetField("workArea").GetValue(clip);
                Assert.That(soil.Terrain.terrainData.size.x, Is.GreaterThanOrEqualTo(20f));
                Assert.That(trackBase.position.x, Is.InRange(workArea.min.x, workArea.max.x),
                    "The excavator must stand inside the one-for-one Cesium replacement area.");
                Assert.That(trackBase.position.z, Is.InRange(workArea.min.z, workArea.max.z));
                float visibleRootOffset = Vector2.Distance(
                    new Vector2(trackBase.position.x, trackBase.position.z),
                    new Vector2(workArea.center.x, workArea.center.z));
                Assert.That(visibleRootOffset, Is.LessThan(.1f),
                    "The visible articulation root must move to the selected lower work-face, not leave the machine rendering on the upper platform.");
                Assert.That(truck.position.x, Is.InRange(workArea.min.x, workArea.max.x),
                    "The truck must stand on the same replaced lower work floor.");
                Assert.That(truck.position.z, Is.InRange(workArea.min.z, workArea.max.z));

                float lowerY = float.NegativeInfinity;
                foreach (float x in new[] { -1.5f, 1.5f })
                foreach (float z in new[] { -2.2f, 2.2f })
                    lowerY = Mathf.Max(lowerY, soil.GetFloorWorldY(
                        trackBase.position + trackBase.right * x + trackBase.forward * z));
                Vector3 faceFoot = trackBase.position + trackBase.forward * 4.2f;
                Vector3 faceCrest = trackBase.position + trackBase.forward * 10.5f;
                float faceRise = soil.GetFloorWorldY(faceCrest) - soil.GetFloorWorldY(faceFoot);
                Assert.That(faceRise, Is.InRange(2.5f, 8f),
                    "The machine must stand on the lower bench and face a clearly raised, continuous cuttable slope.");
                float benchDrop = (float)site.GetType().GetProperty("BenchDropMeters").GetValue(site);
                Assert.That(benchDrop, Is.GreaterThanOrEqualTo(2f),
                    "The selected track platform must be at least 2 m below the original upper platform.");
                float readyTipClearance = (float)site.GetType().GetProperty("ReadyTipClearanceMeters").GetValue(site);
                Assert.That(readyTipClearance, Is.GreaterThanOrEqualTo(.25f),
                    "The initial bucket tooth must clear the cuttable slope enough to curl before entering the soil.");
                int surveyFrameYields = (int)site.GetType().GetProperty("SurveyFrameYieldCount").GetValue(site);
                Assert.That(surveyFrameYields, Is.GreaterThan(0),
                    "Cesium terrain surveying must yield across frames instead of blocking Play on one large scan.");

                Vector3 stripCenter = trackBase.position + trackBase.forward * stripCenterLocalZ;
                float baseCenterY = soil.GetFloorWorldY(stripCenter);
                float topCenterY = soil.Terrain.SampleHeight(stripCenter) + soil.transform.position.y;
                float slopeX = (soil.GetFloorWorldY(stripCenter + trackBase.right) -
                    soil.GetFloorWorldY(stripCenter - trackBase.right)) * .5f;
                float slopeZ = (soil.GetFloorWorldY(stripCenter + trackBase.forward) -
                    soil.GetFloorWorldY(stripCenter - trackBase.forward)) * .5f;
                float normalY = 1f / Mathf.Sqrt(1f + slopeX * slopeX + slopeZ * slopeZ);
                float normalThickness = (topCenterY - baseCenterY) * normalY;
                Assert.That(normalThickness, Is.InRange(stripHeight * .75f, stripHeight * 1.25f),
                    "The soil band must follow the Cesium slope with approximately constant normal thickness.");
                float besideBandY = soil.Terrain.SampleHeight(
                    stripCenter + trackBase.right * (stripHalfWidth + .8f)) + soil.transform.position.y;
                Assert.That(besideBandY - soil.GetFloorWorldY(
                    stripCenter + trackBase.right * (stripHalfWidth + .8f)), Is.LessThan(.04f),
                    "The soil mask must fade back to the untouched map outside its narrow width.");
                float truckGroundY = float.NegativeInfinity;
                float truckLowestSupportY = float.PositiveInfinity;
                foreach (float x in new[] { -1.35f, 1.35f })
                foreach (float z in new[] { -1.35f, 3.25f })
                {
                    float groundY = soil.GetFloorWorldY(truck.TransformPoint(new Vector3(x, 0, z)));
                    truckGroundY = Mathf.Max(truckGroundY, groundY);
                    truckLowestSupportY = Mathf.Min(truckLowestSupportY, groundY);
                }
                Assert.That(truckGroundY - truckLowestSupportY, Is.LessThan(.4f),
                    "The truck's own four wheel/support points must share a stable parking surface.");
                Collider[] truckColliders = truck.GetComponentsInChildren<Collider>()
                    .Where(c => c.enabled && !c.isTrigger).ToArray();
                Bounds truckBounds = truckColliders[0].bounds;
                foreach (Collider collider in truckColliders) truckBounds.Encapsulate(collider.bounds);
                Assert.That(truckBounds.min.y, Is.EqualTo(truckGroundY).Within(.25f),
                    "The truck chassis must rest on the sampled replacement surface.");
                Assert.That(Vector3.Distance(new Vector3(truck.position.x, 0, truck.position.z),
                    new Vector3(stripCenter.x, 0, stripCenter.z)), Is.GreaterThan(7f),
                    "The side-parked truck must remain clear of the active slope strip.");

                var appearance = all.SingleOrDefault(c => c != null && c.GetType().Name == "ConstructionSiteSurfaceAppearance");
                Assert.That(appearance, Is.Not.Null,
                    "The soil replacement must try to project the loaded Cesium imagery before clipping the source map.");
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    bool textureApplied = (bool)appearance.GetType().GetProperty("MapTextureApplied").GetValue(appearance);
                    Assert.That(textureApplied, Is.False,
                        "Headless runs must keep the established material fallback without fabricating a map capture.");
                    Assert.That(soil.Terrain.terrainData.terrainLayers.Length, Is.EqualTo(1),
                        "The graphics-free fallback must leave the source TerrainLayer untouched.");
                }
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
