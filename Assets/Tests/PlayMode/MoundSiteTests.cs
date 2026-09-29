using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public class MoundSiteTests
    {
        /// <summary>Cesium Scene-view tile scheduling is suspended only while the lower work face is surveyed.</summary>
        /// <returns>Scene loading, work-face preparation, camera-setting restoration and cleanup.</returns>
        [UnityTest]
        public IEnumerator SceneViewTileSchedulingIsRestoredAfterWorkFaceSurvey()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            AsyncOperation unload = null;
            try
            {
                SceneManager.SetActiveScene(scene);
                MonoBehaviour site = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                    .Single(component => component != null && component.GetType().Name == "CesiumMoundSite");
                Type managerType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("CesiumForUnity.CesiumCameraManager"))
                    .First(type => type != null);
                Component tileset = (Component)site.GetType().GetField("tileset").GetValue(site);
                Component cameraManager = tileset.GetComponentInParent(managerType);
                Assert.That(cameraManager, Is.Not.Null);
                PropertyInfo sceneViewSetting = managerType.GetProperty("useSceneViewCameraInEditor");

                bool observedSuppression = false;
                float deadline = Time.realtimeSinceStartup + 40f;
                PropertyInfo readyProperty = site.GetType().GetProperty("IsReady");
                PropertyInfo statusProperty = site.GetType().GetProperty("Status");
                while (!(bool)readyProperty.GetValue(site) && Time.realtimeSinceStartup < deadline)
                {
                    observedSuppression |= !((bool)sceneViewSetting.GetValue(cameraManager));
                    yield return null;
                }

                Assert.That((bool)readyProperty.GetValue(site), Is.True, statusProperty.GetValue(site).ToString());
                Assert.That(observedSuppression, Is.True,
                    "The moving Editor Scene view must not invalidate the local Cesium survey.");
                Assert.That((bool)sceneViewSetting.GetValue(cameraManager), Is.True,
                    "Restore the user's Scene-view tile scheduling after preparation.");
            }
            finally
            {
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Disabling an unfinished site survey restores Cesium settings and removes its temporary camera.</summary>
        /// <returns>Scene loading, cancellation cleanup and scene unloading.</returns>
        [UnityTest]
        public IEnumerator DisablingSiteDuringSurveyRestoresCesiumCameraSettings()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            AsyncOperation unload = null;
            try
            {
                MonoBehaviour site = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                    .Single(component => component != null && component.GetType().Name == "CesiumMoundSite");
                Type managerType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("CesiumForUnity.CesiumCameraManager"))
                    .First(type => type != null);
                Component tileset = (Component)site.GetType().GetField("tileset").GetValue(site);
                Component cameraManager = tileset.GetComponentInParent(managerType);
                PropertyInfo sceneViewSetting = managerType.GetProperty("useSceneViewCameraInEditor");
                IList additionalCameras = (IList)managerType.GetProperty("additionalCameras").GetValue(cameraManager);

                Assert.That((bool)site.GetType().GetProperty("IsReady").GetValue(site), Is.False);
                Assert.That((bool)sceneViewSetting.GetValue(cameraManager), Is.False);
                Assert.That(additionalCameras.Cast<Camera>().Any(camera => camera != null &&
                    camera.name == "Cesium work-face survey camera"), Is.True);

                site.enabled = false;

                Assert.That((bool)sceneViewSetting.GetValue(cameraManager), Is.True);
                Assert.That(additionalCameras.Cast<Camera>().Any(camera => camera != null &&
                    camera.name == "Cesium work-face survey camera"), Is.False);
            }
            finally
            {
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

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
                float stripHalfDepth = (float)site.GetType().GetField("soilStripHalfDepth").GetValue(site);
                float stripHeight = (float)site.GetType().GetField("soilStripHeight").GetValue(site);
                float minimumSurfaceNormalY = (float)site.GetType().GetField("minimumSurfaceNormalY").GetValue(site);
                var dig = all.OfType<Zx200DigController>().Single();
                var keyboard = dig.GetComponent<Zx200KeyboardController>();
                Transform trackBase = keyboard.articulationRoot.transform;
                var truck = all.OfType<SoilReceivingZone>().Single().transform.parent;
                Vector3 authoredLeftSeed = new Vector3(281.0765f, trackBase.position.y, 359.2318f);
                Component clip = all.FirstOrDefault(c => c != null && c.GetType().Name == "ConstructionSiteMapClip");
                Assert.That(clip, Is.Not.Null, "The site must install a bounded Cesium render/collision clip.");
                Bounds workArea = (Bounds)clip.GetType().GetField("workArea").GetValue(clip);
                Assert.That(soil.Terrain.terrainData.size.x, Is.GreaterThanOrEqualTo(20f));
                Vector3 workFaceCenter = (Vector3)clip.GetType().GetField("workAreaCenter").GetValue(clip);
                Quaternion workFaceRotation = (Quaternion)clip.GetType().GetField("workAreaRotation").GetValue(clip);
                Vector2 workFaceSize = (Vector2)clip.GetType().GetField("workAreaSize").GetValue(clip);
                Assert.That((bool)clip.GetType().GetField("useOrientedWorkArea").GetValue(clip), Is.True);
                Assert.That(ConstructionSiteFootprint.ContainsHorizontal(trackBase.position,
                    workFaceCenter, workFaceRotation, workFaceSize), Is.False,
                    "The lower-bench track support must remain on the original Cesium surface.");
                Assert.That(Vector2.Distance(new Vector2(trackBase.position.x, trackBase.position.z),
                    new Vector2(authoredLeftSeed.x, authoredLeftSeed.z)), Is.LessThanOrEqualTo(8.5f),
                    "The terrain-validated lower-bench placement must stay near the authored left-side target.");
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(trackBase.eulerAngles.y, 22.5f)), Is.LessThan(.2f));
                float visibleRootOffset = Vector2.Distance(
                    new Vector2(trackBase.position.x, trackBase.position.z),
                    new Vector2(workArea.center.x, workArea.center.z));
                Assert.That(visibleRootOffset, Is.GreaterThan(3f),
                    "The Cesium replacement must be centered on the face, not on the machine support pad.");
                Assert.That(ConstructionSiteFootprint.ContainsHorizontal(truck.position,
                    workFaceCenter, workFaceRotation, workFaceSize), Is.False,
                    "The truck wheels must remain supported by the original Cesium surface.");
                Vector3 truckOffset = (Vector3)site.GetType()
                    .GetField("truckParkingOffsetLocal").GetValue(site);
                Vector3 expectedTruckXZ = trackBase.position + trackBase.rotation * truckOffset;
                Assert.That(Vector2.Distance(new Vector2(truck.position.x, truck.position.z),
                    new Vector2(expectedTruckXZ.x, expectedTruckXZ.z)), Is.LessThan(.1f),
                    "The truck must follow the selected excavator pose while preserving its side-parking offset.");
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(truck.eulerAngles.y, 112.5f)), Is.LessThan(.1f));

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
                foreach (float faceOffset in new[] { 3f, 4.2f, 6.5f, 8.8f, 10f })
                {
                    Vector3 facePoint = trackBase.position + trackBase.forward * faceOffset;
                    float faceSoilThickness = soil.Terrain.SampleHeight(facePoint) + soil.transform.position.y -
                        soil.GetFloorWorldY(facePoint);
                    Assert.That(faceSoilThickness, Is.GreaterThan(.35f),
                        $"The cuttable soil should cover the whole slope face, including local forward offset {faceOffset:F1}m.");
                }
                Vector3 flatApron = trackBase.position + trackBase.forward * 1.5f;
                float flatApronThickness = soil.Terrain.SampleHeight(flatApron) + soil.transform.position.y -
                    soil.GetFloorWorldY(flatApron);
                Assert.That(flatApronThickness, Is.LessThan(.1f),
                    "The lower flat apron should remain largely free of the work-face soil strip.");
                float benchDrop = (float)site.GetType().GetProperty("BenchDropMeters").GetValue(site);
                Assert.That(benchDrop, Is.GreaterThanOrEqualTo(2f),
                    "The selected track platform must be at least 2 m below the original upper platform.");
                float readyTipClearance = (float)site.GetType().GetProperty("ReadyTipClearanceMeters").GetValue(site);
                Assert.That(readyTipClearance, Is.GreaterThanOrEqualTo(.6f),
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
                float selectedNormalY = (float)site.GetType().GetProperty("SelectedSurfaceNormalY").GetValue(site);
                Assert.That(selectedNormalY, Is.GreaterThanOrEqualTo(minimumSurfaceNormalY),
                    "The selected work face should favor a less steep slope, not merely a cuttable wall.");
                float normalThickness = (topCenterY - baseCenterY) * normalY;
                Assert.That(stripHalfWidth, Is.EqualTo(12.5f).Within(.01f));
                Assert.That(stripHalfDepth, Is.EqualTo(4f).Within(.01f));
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
                    "The truck chassis must rest on the unchanged sampled Cesium surface.");
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
                    Assert.That((bool)site.GetType().GetProperty("IsReady").GetValue(site), Is.True,
                        "Headless automation may validate geometry without capturing or saving a visual texture.");
                }
                else
                    Assert.That((bool)appearance.GetType().GetProperty("MapTextureApplied").GetValue(appearance), Is.True,
                        "A visible slope may only replace the map after Cesium imagery has been captured.");
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
                            bool strictlyInside = ConstructionSiteFootprint.ContainsHorizontal(center,
                                workFaceCenter, workFaceRotation, workFaceSize);
                            Assert.That(strictlyInside, Is.False,
                                "The actual rendered Cesium triangles must not cover the replacement work area.");
                        }
                    }
                }
                Assert.That(colliders.Any(c => c.sharedMesh != null &&
                    c.sharedMesh.name.Contains("work area clipped")), Is.True,
                    "The actual Cesium MeshCollider must receive a clipped mesh.");
                var insideRay = new Ray(trackBase.position + Vector3.up * 20f, Vector3.down);
                Assert.That(soil.SoilCollider.Raycast(insideRay, out _, 40), Is.False,
                    "Terrain holes must preserve the excavator's original support ground.");
                Assert.That(colliders.Any(c => c.Raycast(insideRay, out _, 40)), Is.True,
                    "The original Cesium collider must remain under the excavator tracks.");
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
                Assert.That(keyboard.fixedBase, Is.False);
                Assert.That(keyboard.articulationRoot.immovable, Is.False,
                    "Manual track driving remains available until the autonomous job locks the base.");
                Collider[] trackColliders = keyboard.articulationRoot.GetComponentsInChildren<Collider>()
                    .Where(c => c.enabled && !c.isTrigger && c.attachedArticulationBody == keyboard.articulationRoot).ToArray();
                Bounds trackBounds = trackColliders[0].bounds;
                foreach (Collider track in trackColliders) trackBounds.Encapsulate(track.bounds);
                Assert.That(trackBounds.min.y, Is.EqualTo(lowerY).Within(.25f),
                    "The track base must rest directly on the unchanged Cesium pit-floor collider.");
                Assert.That(soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(1e-8));
            }
            finally { SceneManager.UnloadSceneAsync(scene); }
        }
    }
}
