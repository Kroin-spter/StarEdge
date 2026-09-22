using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public class Zx200SoilResistanceTests
    {
        /// <summary>Real articulation cutting is resisted, full-bucket inward targets stop and upward withdrawal remains available.</summary>
        /// <returns>Physics steps for cutting, blocking and withdrawal.</returns>
        [UnityTest]
        public IEnumerator ActualJointsRespectFullBucketGuardAndCanWithdraw()
        {
            const string path = "Assets/Robotics/zx200_description/Demos/SoilCutting/Demo 3/ZX200SoilDemo.unity";
            Scene previous = SceneManager.GetActiveScene();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            AsyncOperation unloading;
            try
            {
                SceneManager.SetActiveScene(scene);
                Zx200DigController dig = null;
                foreach (var root in scene.GetRootGameObjects()) if (root.TryGetComponent(out Zx200DigController found)) dig = found;
                Assert.That(dig, Is.Not.Null);
                dig.GetComponent<Zx200KeyboardController>().enabled = false;
                var cutter = dig.bucketJoint.GetComponent<BucketSoilCutter>();
                cutter.GetComponent<BucketSoilUnloader>().enabled = false;
                var resistance = cutter.gameObject.AddComponent<BucketSoilResistance>();
                for (int i = 0; i < 250; i++) yield return new WaitForFixedUpdate();
                float largestForce = 0;
                yield return ManualSoilTestMotion.Scoop(dig, cutter, .035,
                    () => largestForce = Mathf.Max(largestForce, resistance.ResistanceForceN));
                Debug.Log($"Guard cutting: volume={cutter.VolumeM3:F5}, tip={cutter.TipWorldPosition}, force={largestForce:F1}, reason={resistance.BlockReason}");
                Assert.That(cutter.VolumeM3, Is.GreaterThan(.02));
                Assert.That(largestForce, Is.GreaterThan(0));
                Assert.That(largestForce, Is.LessThanOrEqualTo(resistance.maximumForceN));
                cutter.capacityM3 = (float)cutter.VolumeM3;
                float startY = cutter.TipWorldPosition.y;
                float minimumY = startY;
                Debug.Log($"Before full actual boom={dig.ReadActualPose().boom:F3}, drive={dig.boomJoint.xDrive.target:F3}, arm={dig.ReadActualPose().arm:F3}, bucket={dig.ReadActualPose().bucket:F3}");
                for (int i = 0; i < 150; i++)
                {
                    dig.AdjustManualPose(0, 10*Time.fixedDeltaTime, 0, 0);
                    yield return new WaitForFixedUpdate();
                    minimumY = Mathf.Min(minimumY, cutter.TipWorldPosition.y);
                    if (i % 30 == 0) Debug.Log($"Full {i} actual boom={dig.ReadActualPose().boom:F3}, drive={dig.boomJoint.xDrive.target:F3}, arm={dig.ReadActualPose().arm:F3}, bucket={dig.ReadActualPose().bucket:F3}, depth={resistance.PenetrationM:F3}, contact={resistance.IsContacting}");
                }
                Debug.Log($"Guard full: start={startY:F3}, minimum={minimumY:F3}, penetration={resistance.PenetrationM:F3}, reason={resistance.BlockReason}");
                Assert.That(minimumY, Is.GreaterThan(startY - .08f), "Full bucket must not keep tunnelling under held input");
                Assert.That(resistance.IsBlocked, Is.True);
                double volume = cutter.VolumeM3;
                for (int i = 0; i < 150; i++)
                {
                    dig.AdjustManualPose(0, -10*Time.fixedDeltaTime, 0, 0);
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(cutter.TipWorldPosition.y, Is.GreaterThan(startY + .5f));
                Assert.That(cutter.VolumeM3, Is.EqualTo(volume).Within(1e-8));
                ManualSoilTestMotion.AssertBalance(cutter);
                // Simulate soil that the cutter cannot remove; the guard must stop without relying on fullness.
                cutter.ResetExcavation();
                cutter.capacityM3 = .8f;
                cutter.soil.minimumCutDepth = 2;
                resistance.maximumPenetration = .08f;
                float deepest = 0;
                for (int i = 0; i < 450; i++)
                {
                    dig.AdjustManualPose(0, 10*Time.fixedDeltaTime, 0, 0);
                    yield return new WaitForFixedUpdate();
                    deepest = Mathf.Max(deepest, resistance.PenetrationM);
                }
                Debug.Log($"Uncuttable depth={deepest:F3}, reason={resistance.BlockReason}");
                Assert.That(deepest, Is.LessThan(.18f), "Depth guard must stop progressive penetration into uncut soil");
                Assert.That(resistance.IsBlocked, Is.True);
                Assert.That(cutter.VolumeM3, Is.Zero);
                float blockedY = cutter.TipWorldPosition.y;
                for (int i = 0; i < 120; i++)
                {
                    dig.AdjustManualPose(0, -10*Time.fixedDeltaTime, 0, 0);
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(cutter.TipWorldPosition.y, Is.GreaterThan(blockedY + .5f));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unloading = SceneManager.UnloadSceneAsync(scene);
            }
            if (unloading != null) yield return unloading;
        }
    }
}
