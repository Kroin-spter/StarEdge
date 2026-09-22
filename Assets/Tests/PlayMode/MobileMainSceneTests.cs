using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    /// <summary>Validates driving and excavation with the main scene's dynamic undercarriage.</summary>
    public class MobileMainSceneTests
    {
        /// <summary>Verify opposing track commands produce both yaw directions on the loaded Cesium site.</summary>
        /// <returns>Scene loading, ground settling, turning and cleanup frames.</returns>
        [UnityTest]
        public IEnumerator TurnsBothWaysOnCesiumGround()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            Scene previous = SceneManager.GetActiveScene();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                Zx200KeyboardController keyboard = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if (root.GetComponentInChildren<Zx200KeyboardController>() is Zx200KeyboardController candidate)
                        keyboard = candidate;
                Assert.That(keyboard, Is.Not.Null);
                keyboard.enabled = false;
                Assert.That(keyboard.fixedBase, Is.False);
                var body = keyboard.articulationRoot;
                var drive = keyboard.GetComponent<Zx200TrackDrive>();
                for (int i = 0; i < 250; i++) yield return new WaitForFixedUpdate();
                Assert.That(drive.GroundContactCount, Is.GreaterThan(0));
                float startYaw = body.transform.eulerAngles.y;
                for (int i = 0; i < 100; i++)
                {
                    keyboard.MoveArticulationRoot(0, 1, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                float rightYaw = Mathf.DeltaAngle(startYaw, body.transform.eulerAngles.y);
                drive.Stop();
                for (int i = 0; i < 100; i++) yield return new WaitForFixedUpdate();
                startYaw = body.transform.eulerAngles.y;
                for (int i = 0; i < 100; i++)
                {
                    keyboard.MoveArticulationRoot(0, -1, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                float leftYaw = Mathf.DeltaAngle(startYaw, body.transform.eulerAngles.y);
                drive.Stop();
                Debug.Log($"CESIUM TURN right={rightYaw:F3}, left={leftYaw:F3}, contacts={drive.GroundContactCount}");
                Assert.That(rightYaw, Is.GreaterThan(3f), "Right pivot must work on Cesium ground");
                Assert.That(leftYaw, Is.LessThan(-3f), "Left pivot must work on Cesium ground");
                Assert.That(Vector3.Angle(body.transform.up, Vector3.up), Is.LessThan(8f));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Drive on the work pad, brake, scoop and withdraw without locking the articulation root.</summary>
        /// <returns>Scene loading, physical driving, excavation and cleanup frames.</returns>
        [UnityTest]
        public IEnumerator DrivesThenExcavatesWithDynamicBase()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            Scene previous = SceneManager.GetActiveScene();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                Zx200DigController dig = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if (root.GetComponentInChildren<Zx200DigController>() is Zx200DigController candidate) dig = candidate;
                Assert.That(dig, Is.Not.Null);
                var keyboard = dig.GetComponent<Zx200KeyboardController>();
                keyboard.enabled = false;
                Assert.That(keyboard.fixedBase, Is.False, "Main scene must enable undercarriage driving");
                var body = keyboard.articulationRoot;
                var drive = dig.GetComponent<Zx200TrackDrive>();
                var cutter = dig.bucketJoint.GetComponent<BucketSoilCutter>();
                for (int i = 0; i < 250; i++) yield return new WaitForFixedUpdate();
                Assert.That(body.immovable, Is.False);
                Assert.That(drive.GroundContactCount, Is.GreaterThan(0));
                Vector3 initial = body.transform.position;
                Vector3 forward = Vector3.ProjectOnPlane(body.transform.forward, Vector3.up).normalized;
                // Move a short distance within reach of the existing soil patch.
                for (int i = 0; i < 65; i++)
                {
                    keyboard.MoveArticulationRoot(.3f, 0, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                drive.Stop();
                for (int i = 0; i < 150; i++) yield return new WaitForFixedUpdate();
                float travel = Vector3.Dot(body.transform.position - initial, forward);
                Debug.Log($"MOBILE travel={travel:F3}, contacts={drive.GroundContactCount}, speed={body.linearVelocity.magnitude:F4}");
                Assert.That(travel, Is.GreaterThan(.2f));
                Assert.That(body.linearVelocity.magnitude, Is.LessThan(.15f));
                Vector3 parked = body.transform.position;
                float drift = 0, tilt = 0;
                System.Action observe = () =>
                {
                    drift = Mathf.Max(drift, Vector3.ProjectOnPlane(body.transform.position - parked, Vector3.up).magnitude);
                    tilt = Mathf.Max(tilt, Vector3.Angle(body.transform.up, Vector3.up));
                    Assert.That(body.immovable, Is.False);
                };
                yield return ManualSoilTestMotion.Scoop(dig, cutter, .04, observe);
                Debug.Log($"MOBILE scoop={cutter.VolumeM3:F6}, cut={cutter.ExcavatedVolumeM3:F6}, drift={drift:F4}, tilt={tilt:F3}");
                Assert.That(cutter.VolumeM3, Is.GreaterThan(.01));
                float entryY = cutter.TipWorldPosition.y;
                for (int i = 0; i < 120; i++)
                {
                    dig.AdjustManualPose(0, -10 * Time.fixedDeltaTime, 0, 0);
                    yield return new WaitForFixedUpdate();
                    observe();
                }
                Assert.That(cutter.TipWorldPosition.y, Is.GreaterThan(entryY + .4f));
                Assert.That(drift, Is.LessThan(.15f), "Parking must resist excavation forces");
                Assert.That(tilt, Is.LessThan(5f), "Dynamic base must remain supported while digging");
                ManualSoilTestMotion.AssertBalance(cutter);
                BladeMainSceneTests.CaptureClose(cutter, dig.GetComponent<Zx200CameraController>().targetCamera, false, "mobile-main");
                Debug.Log($"MOBILE final bucket={cutter.VolumeM3:F6}, drift={drift:F4}, tilt={tilt:F3}");
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
