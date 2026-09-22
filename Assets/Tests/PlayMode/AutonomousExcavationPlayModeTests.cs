using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    /// <summary>Verifies autonomous excavation against the real Cesium main-scene soil ledger.</summary>
    public class AutonomousExcavationPlayModeTests
    {
        private const string ScenePath = "Assets/Scenes/sence_test.unity";

        /// <summary>Run one automatic dig, load and return cycle using actual landing volume.</summary>
        /// <returns>Scene load, Cesium readiness, one autonomous cycle and cleanup frames.</returns>
        [UnityTest]
        public IEnumerator RunsOneAutomaticDigAndLoadCycle()
        {
            Scene previous = SceneManager.GetActiveScene();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                Assert.That(autonomy, Is.Not.Null);
                Assert.That(cutter, Is.Not.Null);
                Assert.That(receiver, Is.Not.Null);

                double receiverBefore = receiver.VolumeM3;
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);
                yield return WaitForJob(autonomy, 90);

                Debug.Log($"AUTONOMOUS state={autonomy.State}, status={autonomy.Status}, " +
                    $"delivered={autonomy.DeliveredVolumeM3:F6}, receiver={receiver.VolumeM3:F6}, " +
                    $"bucket={cutter.VolumeM3:F6}, cycles={autonomy.CompletedCycles}");
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(receiver.VolumeM3 - receiverBefore, Is.GreaterThan(.005));
                Assert.That(autonomy.DeliveredVolumeM3,
                    Is.EqualTo(receiver.VolumeM3 - receiverBefore).Within(1e-5));
                Assert.That(cutter.VolumeM3, Is.LessThanOrEqualTo(.005));
                Assert.That(cutter.GetComponent<BucketSoilUnloader>().AirborneVolumeM3,
                    Is.LessThanOrEqualTo(1e-5));
                ManualSoilTestMotion.AssertBalance(cutter, .0005);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Reset a completed automatic job and verify every runtime soil ledger returns to zero.</summary>
        /// <returns>Scene load, autonomous completion, reset and cleanup frames.</returns>
        [UnityTest]
        public IEnumerator ResetRestoresSoilAndReceiverLedgers()
        {
            Scene previous = SceneManager.GetActiveScene();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);
                float excavationDeadline = Time.realtimeSinceStartup + 30;
                while (cutter.ExcavatedVolumeM3 <= .001 && autonomy.IsRunning &&
                       Time.realtimeSinceStartup < excavationDeadline)
                    yield return null;
                Assert.That(cutter.ExcavatedVolumeM3, Is.GreaterThan(.001));

                autonomy.ResetAutonomousJob();
                float deadline = Time.realtimeSinceStartup + 20;
                while (autonomy.State != AutonomousExcavationState.Idle &&
                       Time.realtimeSinceStartup < deadline)
                    yield return null;

                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Idle));
                Assert.That(cutter.VolumeM3, Is.Zero.Within(1e-8));
                Assert.That(receiver.VolumeM3, Is.Zero.Within(1e-8));
                Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(.0005));
                Assert.That(cutter.ExcavatedVolumeM3, Is.Zero.Within(1e-8));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        private static IEnumerator WaitForJob(AutonomousExcavationController autonomy, float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(autonomy.IsRunning, Is.False,
                $"Autonomous job timed out in {autonomy.State}: {autonomy.Status}");
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T component = root.GetComponentInChildren<T>(true);
                if (component != null) return component;
            }
            return null;
        }
    }
}
