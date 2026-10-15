using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public sealed class MineVisualPresetTests
    {
        /// <summary>Presentation uses forward depth, keeps close/invalid targets clear, and restores lifecycle state.</summary>
        /// <returns>Frames for autofocus and resource cleanup.</returns>
        [UnityTest]
        public IEnumerator PresentationProtectsSubjectAndRestoresBaseline()
        {
            var rig = new GameObject("presentation camera");
            var subject = new GameObject("presentation subject");
            try
            {
                rig.SetActive(false);
                rig.AddComponent<Camera>();
                var data = rig.AddComponent<UniversalAdditionalCameraData>();
                data.renderPostProcessing = false;
                var preset = rig.AddComponent<MineVisualPreset>();
                preset.showToggle = false;
                preset.presentationMode = DepthOfFieldMode.Gaussian;
                preset.presetProfile = UnityEditor.AssetDatabase.LoadAssetAtPath<VolumeProfile>(
                    "Assets/MineRendering/NaturalMineProfile.asset");
                Assert.That(preset.presetProfile, Is.Not.Null);
                Assert.That(preset.presetProfile.TryGet<DepthOfField>(out var assetDof), Is.True);
                preset.focusTarget = subject.transform;
                subject.transform.position = new Vector3(12, 0, 10);
                rig.SetActive(true);
                var volume = rig.GetComponentInChildren<Volume>(true);
                Assert.That(volume.profile.TryGet<DepthOfField>(out var dof), Is.True);
                Assert.That(preset.presentationEnabled, Is.False);
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Off));
                Assert.That(dof, Is.Not.SameAs(assetDof));
                Assert.That(assetDof.mode.value, Is.EqualTo(DepthOfFieldMode.Gaussian));
                preset.SetPresentation(true);
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Gaussian));
                Assert.That(dof.gaussianStart.value, Is.EqualTo(10 + preset.focusSafetyAllowance).Within(.001f));
                Assert.That(dof.gaussianEnd.value, Is.GreaterThan(dof.gaussianStart.value));
                subject.transform.position = new Vector3(12, 0, 20);
                yield return null;
                Assert.That(dof.gaussianStart.value, Is.GreaterThanOrEqualTo(20 + preset.focusSafetyAllowance));
                float previousStart = dof.gaussianStart.value;
                subject.transform.position = new Vector3(12, 0, 10);
                yield return null;
                Assert.That(dof.gaussianStart.value, Is.InRange(10 + preset.focusSafetyAllowance, previousStart));
                subject.transform.position = new Vector3(12, 0, 2);
                yield return null;
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Off));
                subject.transform.position = new Vector3(0, 0, -10);
                yield return null;
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Off));
                subject.transform.position = new Vector3(0, 0, 10);
                yield return null;
                preset.SetPresentation(false);
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Off));
                preset.SetPresentation(true);
                preset.SetEnhanced(false);
                Assert.That(volume.enabled, Is.False);
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Off));
                Assert.That(data.renderPostProcessing, Is.False);
                preset.SetEnhanced(true);
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Gaussian));
                preset.enabled = false;
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Off));
                Assert.That(data.renderPostProcessing, Is.False);
                preset.enabled = true;
                preset.focusTarget = null;
                yield return null;
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Off));
                Assert.That(rig.transform.position, Is.EqualTo(Vector3.zero));
                Assert.That(rig.transform.rotation, Is.EqualTo(Quaternion.identity));
                Assert.That(assetDof.gaussianStart.value, Is.EqualTo(13));
                Assert.That(assetDof.mode.value, Is.EqualTo(DepthOfFieldMode.Gaussian));
            }
            finally
            {
                Object.DestroyImmediate(rig);
                Object.DestroyImmediate(subject);
            }
        }

        /// <summary>The serialized profile retains Gaussian variants without runtime mutation of the asset.</summary>
        [Test]
        public void SerializedProfileRetainsPresentationDepthOfField()
        {
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/MineRendering/NaturalMineProfile.asset");
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.TryGet<DepthOfField>(out var dof), Is.True);
            Assert.That(dof.mode.overrideState, Is.True);
            Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Gaussian));
        }

        /// <summary>Bokeh focuses at smoothed forward depth and clears automatically near the subject.</summary>
        /// <returns>Frames for autofocus updates.</returns>
        [UnityTest]
        public IEnumerator BokehFocusUsesCameraDepthAndClearsCloseView()
        {
            var rig = new GameObject("bokeh camera");
            var subject = new GameObject("bokeh subject");
            try
            {
                rig.SetActive(false);
                rig.AddComponent<Camera>();
                var preset = rig.AddComponent<MineVisualPreset>();
                preset.showToggle = false;
                preset.focusTarget = subject.transform;
                subject.transform.position = new Vector3(12, 0, 10);
                rig.SetActive(true);
                Assert.That(preset.presentationMode, Is.EqualTo(DepthOfFieldMode.Bokeh));
                Assert.That(preset.aperture, Is.EqualTo(2.8f));
                Assert.That(preset.focalLength, Is.EqualTo(85));
                preset.SetPresentation(true);
                var volume = rig.GetComponentInChildren<Volume>(true);
                Assert.That(volume.profile.TryGet<DepthOfField>(out var dof), Is.True);
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Bokeh));
                Assert.That(dof.focusDistance.value, Is.EqualTo(10).Within(.001f));
                Assert.That(dof.aperture.value, Is.EqualTo(2.8f));
                Assert.That(dof.focalLength.value, Is.EqualTo(85));
                preset.aperture = 4;
                preset.focalLength = 70;
                preset.focusSmoothTime = 10;
                subject.transform.position = new Vector3(12, 0, 20);
                // Batch frames can be too short for the first smoothed step to change a float.
                for (int frame = 0; frame < 120 && dof.focusDistance.value <= 10; frame++)
                    yield return null;
                Assert.That(dof.focusDistance.value, Is.GreaterThan(10).And.LessThan(20));
                Assert.That(dof.aperture.value, Is.EqualTo(4));
                Assert.That(dof.focalLength.value, Is.EqualTo(70));
                subject.transform.position = new Vector3(12, 0, 2);
                yield return null;
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Off));
                subject.transform.position = new Vector3(12, 0, 10);
                yield return null;
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Bokeh));
                Assert.That(dof.focusDistance.value, Is.EqualTo(10).Within(.001f));
                preset.SetPresentation(false);
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Off));
                preset.SetPresentation(true);
                preset.enabled = false;
                Assert.That(dof.mode.value, Is.EqualTo(DepthOfFieldMode.Off));
                Assert.That(volume.enabled, Is.False);
                Assert.That(rig.transform.position, Is.EqualTo(Vector3.zero));
                Assert.That(rig.transform.rotation, Is.EqualTo(Quaternion.identity));
            }
            finally
            {
                Object.DestroyImmediate(rig);
                Object.DestroyImmediate(subject);
            }
        }

        /// <summary>Repeated enable/disable restores the original camera and sunlight without extra owned volumes.</summary>
        /// <returns>Frames allowing owned resources to be destroyed.</returns>
        [UnityTest]
        public IEnumerator ToggleRestoresCameraAndSunlightAndReusesVolume()
        {
            var rig = new GameObject("visual camera");
            var sunObject = new GameObject("visual sun");
            try
            {
                rig.SetActive(false);
                rig.AddComponent<Camera>();
                var data = rig.AddComponent<UniversalAdditionalCameraData>();
                data.renderPostProcessing = false;
                data.antialiasing = AntialiasingMode.None;
                data.volumeLayerMask = 1 << 5;
                var sun = sunObject.AddComponent<Light>();
                sun.shadowStrength = .7f;
                var preset = rig.AddComponent<MineVisualPreset>();
                preset.sunlight = sun;
                preset.showToggle = false;
                rig.SetActive(true);
                for (int i = 0; i < 20; i++)
                {
                    preset.SetEnhanced(true);
                    Assert.That(data.renderPostProcessing, Is.True);
                    Assert.That(data.antialiasing, Is.EqualTo(AntialiasingMode.FastApproximateAntialiasing));
                    Assert.That(sun.shadowStrength, Is.EqualTo(.574f).Within(.0001f));
                    Assert.That(rig.GetComponentsInChildren<Volume>(true).Length, Is.EqualTo(1));
                    preset.SetEnhanced(false);
                    Assert.That(data.renderPostProcessing, Is.False);
                    Assert.That(data.antialiasing, Is.EqualTo(AntialiasingMode.None));
                    Assert.That(data.volumeLayerMask.value, Is.EqualTo(32));
                    Assert.That(sun.shadowStrength, Is.EqualTo(.7f).Within(.0001f));
                }
                sun.shadowStrength = .9f;
                preset.exposure = .3f;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                preset.SetEnhanced(true);
                Assert.That(sun.shadowStrength, Is.EqualTo(.738f).Within(.0001f));
                var ownedProfile = rig.GetComponentInChildren<Volume>().profile;
                Assert.That(ownedProfile.TryGet<ColorAdjustments>(out var grading), Is.True);
                Assert.That(grading.postExposure.value, Is.EqualTo(.3f));
                Assert.That(ownedProfile.components.Count, Is.EqualTo(9));
                preset.SetEnhanced(false);
                Assert.That(sun.shadowStrength, Is.EqualTo(.9f).Within(.0001f));
                Assert.That(data.antialiasing, Is.EqualTo(AntialiasingMode.SubpixelMorphologicalAntiAliasing));
                sun.shadowStrength = .7f;
                data.antialiasing = AntialiasingMode.None;
                preset.SetEnhanced(true);
                preset.enabled = false;
                Assert.That(data.renderPostProcessing, Is.False);
                Assert.That(sun.shadowStrength, Is.EqualTo(.7f).Within(.0001f));
                preset.enabled = true;
                Assert.That(preset.IsEnhanced, Is.True);
                Object.Destroy(preset);
                yield return null;
                Assert.That(data.renderPostProcessing, Is.False);
                Assert.That(sun.shadowStrength, Is.EqualTo(.7f).Within(.0001f));
                yield return null;
                Assert.That(rig.GetComponentsInChildren<Volume>(true), Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(rig);
                Object.DestroyImmediate(sunObject);
            }
        }

        /// <summary>Compare both presets from one fixed camera and alternate timings after site/shader warmup.</summary>
        /// <returns>Site readiness, screenshot frames and four warmed sampling windows.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator SavedSceneProducesNaturalAndOriginalViews()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            for (int i = 0; i < 3; i++) LogAssert.Expect(LogType.Error, $"Tree prefab at index {i} is missing.");
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            float previousTimeScale = Time.timeScale;
            try
            {
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var roots = scene.GetRootGameObjects();
                var camera = roots.SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                    .First(candidate => candidate.CompareTag("MainCamera"));
                var preset = camera.GetComponent<MineVisualPreset>();
                Assert.That(preset, Is.Not.Null);
                Assert.That(preset.presetProfile, Is.Not.Null, "The player needs a serialized profile for shader variant retention.");
                Assert.That(preset.IsEnhanced, Is.True);
                foreach (var controller in roots.SelectMany(root => root.GetComponentsInChildren<Zx200CameraController>(true)))
                    controller.enabled = false;
                camera.transform.position = new Vector3(312, 6, 340);
                camera.transform.LookAt(new Vector3(320, 2, 352));
                Time.timeScale = 0;
                preset.showToggle = false;
                double[] times = new double[4];
                for (int pass = 0; pass < 4; pass++)
                {
                    preset.SetEnhanced((pass & 1) != 0);
                    for (int frame = 0; frame < 60; frame++) yield return null;
                    if (pass < 2) Capture(camera, pass == 0 ? "mine-original" : "mine-natural");
                    times[pass] = MeasureRender(camera);
                }
                preset.SetEnhanced(true);
                preset.SetPresentation(true);
                for (int frame = 0; frame < 60; frame++) yield return null;
                Capture(camera,"mine-showcase");
                double showcaseTime = MeasureRender(camera);
                Debug.Log($"Showcase focus target={preset.focusTarget}, mode={preset.presentationMode}, requested={preset.presentationEnabled}");
                preset.SetPresentation(false);
                if (preset.focusTarget != null)
                {
                    Transform subject = preset.focusTarget;
                    camera.transform.position = subject.position - subject.right * 5 - subject.forward * 3 + Vector3.up * 2.6f;
                    camera.transform.LookAt(subject.position + Vector3.up * 1.15f);
                    preset.SetEnhanced(false);
                    for (int frame = 0; frame < 15; frame++) yield return null;
                    Capture(camera,"equipment-original");
                    preset.SetEnhanced(true);
                    for (int frame = 0; frame < 15; frame++) yield return null;
                    Capture(camera,"equipment-upgraded");
                    preset.SetPresentation(true);
                    for (int frame = 0; frame < 15; frame++) yield return null;
                    Capture(camera,"equipment-showcase");
                    preset.SetPresentation(false);
                }
                string result = $"Fixed scene/camera, alternating 40 forced 1280x900 renders per window; " +
                    $"original={(times[0] + times[2]) / 2:F3} ms/render; natural={(times[1] + times[3]) / 2:F3} ms/render. " +
                    $"Showcase={showcaseTime:F3} ms/render. " +
                    "Includes 1-pixel GPU readback synchronization. Local Editor OpenGL, not browser FPS.";
                File.WriteAllText("Diagnostics/mine-visual-timing.txt", result);
                Debug.Log(result);
                preset.SetEnhanced(true);
                Assert.That(camera.GetComponentsInChildren<Volume>(true).Length, Is.EqualTo(1));
            }
            finally
            {
                Time.timeScale = previousTimeScale;
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
        }

        private static double MeasureRender(Camera camera)
        {
            var target = new RenderTexture(1280, 900, 24);
            var probe = new Texture2D(1, 1, TextureFormat.RGB24, false);
            RenderTexture previous = camera.targetTexture, active = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                for (int i = 0; i < 10; i++)
                {
                    camera.Render();
                    RenderTexture.active = target;
                    probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                    probe.Apply();
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 40; i++)
                {
                    camera.Render();
                    RenderTexture.active = target;
                    probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                    probe.Apply();
                }
                return watch.Elapsed.TotalMilliseconds / 40;
            }
            finally
            {
                camera.targetTexture = previous;
                RenderTexture.active = active;
                Object.DestroyImmediate(probe);
                Object.DestroyImmediate(target);
            }
        }

        private static void Capture(Camera camera, string name)
        {
            var target = new RenderTexture(1280, 900, 24);
            var image = new Texture2D(1280, 900, TextureFormat.RGB24, false);
            RenderTexture previous = camera.targetTexture, active = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1280, 900), 0, 0);
                image.Apply();
                Directory.CreateDirectory("Diagnostics");
                File.WriteAllBytes($"Diagnostics/{name}.png", image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previous;
                RenderTexture.active = active;
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(target);
            }
        }
    }
}
