using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    /// <summary>直接加载交付场景，验证默认设置可供人工试切。</summary>
    public class Zx200SoilDemoTests
    {
        /// <summary>演示启动时斗内为空，随后降低实际动臂即可切出土方。</summary>
        /// <returns>场景加载、初始化、实际试切及卸载过程。</returns>
        [UnityTest]
        public IEnumerator DeliveredSceneStartsEmptyAndCutsWithManualBoom()
        {
            return CheckScene("Assets/Robotics/zx200_description/Demos/SoilCutting/Demo/ZX200SoilDemo.unity");
        }

        /// <summary>Verify the newly delivered bounded-fill scene with real digging and carrying.</summary>
        /// <returns>Physics steps for the scene's manual cutting, lifting, and rotation.</returns>
        [UnityTest]
        public IEnumerator BoundedFillSceneCutsAndCarriesSoil()
        {
            return CheckScene("Assets/Robotics/zx200_description/Demos/SoilCutting/Demo 1/ZX200SoilDemo.unity");
        }

        private IEnumerator CheckScene(string path)
        {
            Scene previous = SceneManager.GetActiveScene();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            AsyncOperation unload = null;
            try
            {
                SceneManager.SetActiveScene(scene);
                Zx200DigController dig = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if (root.TryGetComponent(out Zx200DigController candidate)) dig = candidate;
                Assert.That(dig, Is.Not.Null);
                var cameraController = dig.GetComponent<Zx200CameraController>();
                Assert.That(cameraController.targetCamera, Is.Not.Null);
                Assert.That(cameraController.targetCamera.gameObject.scene, Is.EqualTo(scene));
                Assert.That(cameraController.orbitPivotLocalPosition.z, Is.EqualTo(3f));
                Assert.That(cameraController.initialDistance, Is.EqualTo(13f));
                dig.GetComponent<Zx200KeyboardController>().enabled = false;
                var cutter = dig.bucketJoint.GetComponent<BucketSoilCutter>();
                Assert.That(cutter, Is.Not.Null);
                for (int i = 0; i < 250; i++) yield return new WaitForFixedUpdate();
                Debug.Log($"交付场景初态：斗尖={cutter.TipWorldPosition}，斗量={cutter.VolumeM3:F5}");
                Assert.That(cutter.VolumeM3, Is.Zero.Within(0.00001));
                Assert.That(cutter.TipWorldPosition.y, Is.GreaterThan(0.6f));
                yield return ManualSoilTestMotion.Scoop(dig, cutter);
                Assert.That(cutter.VolumeM3, Is.GreaterThan(0.02));
                ManualSoilTestMotion.AssertBalance(cutter);
                Assert.That(cutter.fillVisual.gameObject.activeSelf, Is.True);
                double collected = cutter.VolumeM3;
                var fill = cutter.fillVisual.GetComponent<BucketSoilFill>();
                Assert.That(fill, Is.Not.Null);
                Assert.That(cutter.fillVisual.GetComponentsInChildren<Collider>(), Is.Empty);
                for (int i = 0; i < 100; i++)
                {
                    dig.AdjustManualPose(0, -5f * Time.fixedDeltaTime, 0, 0);
                    yield return new WaitForFixedUpdate();
                }
                double carried = cutter.VolumeM3;
                for (int i = 0; i < 100; i++)
                {
                    dig.AdjustManualPose(10f * Time.fixedDeltaTime, 0, 0, 0);
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(cutter.VolumeM3, Is.GreaterThanOrEqualTo(collected));
                Assert.That(cutter.VolumeM3, Is.EqualTo(carried).Within(1e-6));
                ManualSoilTestMotion.AssertBalance(cutter);
                Assert.That(cutter.fillVisual.localPosition, Is.EqualTo(Vector3.zero));
                Debug.Log($"交付场景试切：斗尖={cutter.TipWorldPosition}，斗量={cutter.VolumeM3:F5}");
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }
    }
}
