using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public class Zx200UnloadingDemoTests
    {
        /// <summary>Use actual articulation drives to dig, lift, swing and empty into the delivered receiving platform.</summary>
        /// <returns>Physics frames for a manual one-bucket cycle.</returns>
        [UnityTest]
        public IEnumerator RealBucketDigsAndUnloadsIntoDeliveredPlatform()
        {
            return CheckScene("Assets/Robotics/zx200_description/Demos/SoilCutting/Demo 2/ZX200SoilDemo.unity", false);
        }

        /// <summary>Verify the newly delivered scene with the real articulated bucket and receiving platform.</summary>
        /// <returns>Physics frames for loading the platform.</returns>
        [UnityTest]
        public IEnumerator MainCesiumSceneLoadsTruck()
        {
            return CheckScene("Assets/Scenes/sence_test.unity", false);
        }

        /// <summary>Verify backfill and spill accounting in the actual construction site.</summary>
        /// <returns>Physics frames for a complete free dump in the main scene.</returns>
        [UnityTest]
        public IEnumerator MainCesiumSceneReturnsSoilToTerrain()
        {
            return CheckScene("Assets/Scenes/sence_test.unity", true);
        }

        [UnityTest]
        public IEnumerator GravityDemoLoadsPlatform()
        {
            return CheckScene("Assets/Robotics/zx200_description/Demos/SoilCutting/Demo 3/ZX200SoilDemo.unity", false);
        }

        /// <summary>Verify that actual bucket rotation releases soil back onto terrain without any receiver.</summary>
        /// <returns>Physics frames for free discharge and terrain deposition.</returns>
        [UnityTest]
        public IEnumerator GravityDemoDumpsBackOntoTerrainWithoutReceiver()
        {
            return CheckScene("Assets/Robotics/zx200_description/Demos/SoilCutting/Demo 3/ZX200SoilDemo.unity", true);
        }

        private IEnumerator CheckScene(string path, bool returnToTerrain)
        {
            bool mainSite = path.Contains("sence_test");
            string imagePrefix = mainSite ? (returnToTerrain ? "main-backfill" : "main-truck") :
                (returnToTerrain ? "free-terrain" : (path.Contains("Demo 3") ? "gravity-platform" : "legacy-platform"));
            if (mainSite)
                for (int index = 0; index < 3; index++)
                    LogAssert.Expect(LogType.Error, $"Tree prefab at index {index} is missing.");
            Scene previous = SceneManager.GetActiveScene();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                Zx200DigController dig = null;
                SoilReceivingZone zone = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var machine = root.GetComponentInChildren<Zx200DigController>();
                    var bed = root.GetComponentInChildren<SoilReceivingZone>();
                    if (machine != null) dig = machine;
                    if (bed != null) zone = bed;
                }
                Assert.That(dig, Is.Not.Null);
                Assert.That(zone, Is.Not.Null);
                dig.GetComponent<Zx200KeyboardController>().enabled = false;
                var cutter = dig.bucketJoint.GetComponent<BucketSoilCutter>();
                var unloader = cutter.GetComponent<BucketSoilUnloader>();
                if (mainSite) Assert.That(cutter.GetComponent<BucketSoilResistance>(), Is.Not.Null);
                if (returnToTerrain) unloader.receivers = new SoilReceivingZone[0];
                for (int i = 0; i < 250; i++) yield return new WaitForFixedUpdate();
                Assert.That(cutter.VolumeM3, Is.Zero);
                if (mainSite)
                {
                    int clipped = 0;
                    foreach (var root in scene.GetRootGameObjects())
                        foreach (var filter in root.GetComponentsInChildren<MeshFilter>())
                        {
                            if (filter.sharedMesh == null || !filter.sharedMesh.name.Contains("work area clipped")) continue;
                            clipped++;
                            var collider = filter.GetComponent<MeshCollider>();
                            if (collider != null) Assert.That(collider.sharedMesh, Is.SameAs(filter.sharedMesh));
                        }
                    Assert.That(clipped, Is.Zero, "Mound-only site must preserve all original streamed map geometry");
                    Vector3 sample = cutter.soil.transform.position + new Vector3(3,10,3);
                    foreach (var hit in Physics.RaycastAll(sample, Vector3.down, 20))
                        if (hit.collider is MeshCollider mapCollider && mapCollider.sharedMesh != null)
                            Assert.That(mapCollider.sharedMesh.name.Contains("work area clipped"), Is.False,
                                "Clipped Cesium cannot retain a collision sheet beneath local soil");
                }
                yield return ManualSoilTestMotion.Scoop(dig, cutter);
                Assert.That(cutter.VolumeM3, Is.GreaterThan(.02));
                if (mainSite) BladeMainSceneTests.CaptureClose(cutter,
                    dig.GetComponent<Zx200CameraController>().targetCamera, false, "blade-loaded");
                if (mainSite)
                {
                    // Clear the constrained cutting edge before curling; do not grind a blocked joint in place.
                    for (int i = 0; i < 45; i++)
                    {
                        dig.AdjustManualPose(0, -7.5f * Time.fixedDeltaTime, 0, 0);
                        yield return new WaitForFixedUpdate();
                    }
                }
                // Curl before carrying: a tilted open bucket now spills regardless of destination.
                for (int i = 0; i < (mainSite ? 240 : 75); i++)
                {
                    if (mainSite && Vector3.Angle(cutter.transform.TransformDirection(unloader.openingNormalLocal), Vector3.down) >= 140f) break;
                    dig.AdjustManualPose(0, 0, 0, 10 * Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                for (int i = 0; i < (mainSite ? 90 : 100); i++)
                {
                    dig.AdjustManualPose(0, -7.5f * Time.fixedDeltaTime, 0, 0);
                    yield return new WaitForFixedUpdate();
                }
                for (int i = 0; i < (returnToTerrain ? 0 : 150); i++)
                {
                    dig.AdjustManualPose(-15 * Time.fixedDeltaTime, 0, 0, 0);
                    yield return new WaitForFixedUpdate();
                }
                Debug.Log($"Before dump: tip={cutter.TipWorldPosition}, angle={Vector3.Angle(cutter.transform.TransformDirection(unloader.openingNormalLocal),Vector3.down)}, volume={cutter.VolumeM3}");
                bool capturedFlow = false;
                for (int i = 0; i < 500 && cutter.VolumeM3 > 0; i++)
                {
                    dig.AdjustManualPose(0, 0, 0, -15 * Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                    if (!capturedFlow && cutter.EmittedVolumeM3 > .03 && unloader.AirborneVolumeM3 > 0 && cutter.VolumeM3 > 0)
                    {
                        Assert.That(unloader.AirborneVolumeM3, Is.GreaterThan(0));
                        Capture(dig.GetComponent<Zx200CameraController>().targetCamera, imagePrefix + "-flow");
                        capturedFlow = true;
                    }
                }
                for (int i = 0; i < 300; i++) yield return new WaitForFixedUpdate();
                Debug.Log($"After dump: tip={cutter.TipWorldPosition}, angle={Vector3.Angle(cutter.transform.TransformDirection(unloader.openingNormalLocal),Vector3.down)}, volume={cutter.VolumeM3}, received={zone.VolumeM3}, status={unloader.Status}");
                // Keep visual evidence even when a later accounting assertion fails.
                if (mainSite) Capture(dig.GetComponent<Zx200CameraController>().targetCamera, imagePrefix + "-after-dump");
                if (returnToTerrain)
                {
                    double returned = 0;
                    foreach (var record in cutter.Landings) if (record.Destination == cutter.soil) returned += record.VolumeM3;
                    Assert.That(returned, Is.GreaterThan(.02));
                    Assert.That(zone.VolumeM3, Is.Zero);
                }
                else
                {
                    Assert.That(zone.VolumeM3, Is.GreaterThan(.02));
                    Assert.That(unloader.Target, Is.SameAs(zone));
                }
                Assert.That(capturedFlow, Is.True);
                Assert.That(cutter.VolumeM3, Is.Zero.Within(1e-8));
                double onGround = 0;
                foreach (var pile in Object.FindObjectsByType<SoilReceivingZone>(FindObjectsSortMode.None))
                    if (pile.isGroundPile && pile.gameObject.scene == scene) onGround += pile.VolumeM3;
                Assert.That(unloader.AirborneVolumeM3, Is.Zero.Within(1e-8));
                Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.EqualTo(zone.VolumeM3 + onGround).Within(1e-5));
                Assert.That(cutter.ExcavatedVolumeM3, Is.EqualTo(cutter.DeliveredVolumeM3).Within(1e-5));
                if (mainSite)
                {
                    double returned = 0;
                    foreach (var receipt in cutter.Landings) if (receipt.Destination == cutter.soil) returned += receipt.VolumeM3;
                    Debug.Log($"SITE ACCOUNTING cut={cutter.ExcavatedVolumeM3:F6}, bucket={cutter.VolumeM3:F6}, flight={unloader.AirborneVolumeM3:F6}, pending={unloader.SurfacePendingVolumeM3:F6}, returned={returned:F6}, truck={zone.VolumeM3:F6}, ground={onGround:F6}, netTerrain={cutter.soil.RecalculateRemovedVolumeM3():F6}");
                    Assert.That(cutter.ExcavatedVolumeM3,
                        Is.EqualTo(cutter.VolumeM3 + unloader.AirborneVolumeM3 + unloader.SurfacePendingVolumeM3 + returned + zone.VolumeM3 + onGround).Within(1e-5));
                }
                Assert.That(cutter.fillVisual.gameObject.activeSelf, Is.False);
                Assert.That(zone.GetComponent<Renderer>().enabled, Is.EqualTo(!returnToTerrain));
                Capture(dig.GetComponent<Zx200CameraController>().targetCamera, imagePrefix + "-complete");
                cutter.ResetExcavation();
                Assert.That(zone.VolumeM3, Is.Zero);
                Assert.That(zone.Records, Is.Empty);
                Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(1e-6));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        private static void Capture(Camera camera, string name)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var target = new RenderTexture(1280, 900, 24);
            var previous = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var image = new Texture2D(1280, 900, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1280, 900), 0, 0);
                image.Apply();
                System.IO.Directory.CreateDirectory("Diagnostics");
                System.IO.File.WriteAllBytes("Diagnostics/" + name + ".png", image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previous;
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(target);
            }
        }
    }
}
