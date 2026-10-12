using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        private const string CesiumTilesetMissingHint =
            " Set CESIUM_TILESET_DIR or place the map beside the project.";
        private static bool expectedInitialSceneWarnings;

        /// <summary>Ready-site manual input must unlock cutting without visiting the automatic work-ready pose.</summary>
        /// <returns>Scene preparation and the first accepted manual physics command.</returns>
        [UnityTest]
        public IEnumerator ManualCommandUnlocksParkedSceneWithoutWorkReady()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                Assert.That(machine.IsPoseWithinTolerance(machine.workReadyPose, 4f), Is.False,
                    "This regression must start outside the old fixed-pose unlock gate.");
                Assert.That(cutter.StartupReady, Is.False);
                Assert.That(machine.AdjustManualPose(0, 0, 0, 0), Is.True);
                cutter.SamplePose();
                Assert.That(cutter.StartupReady, Is.False, "Neutral input is not an operator command.");
                Assert.That(machine.AdjustManualPose(0, 0, .1f, 0), Is.True);
                yield return new WaitForFixedUpdate();
                cutter.SamplePose();
                Assert.That(cutter.StartupReady, Is.True,
                    "A manual operator must not visit the automatic work-ready waypoint to enable cutting.");
                Assert.That(cutter.ExcavatedVolumeM3, Is.Zero,
                    "Unlocking must reset the sweep baseline, not excavate the positioning journey.");
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>A held manual command at automatic start must not cancel the new job until it returns to neutral.</summary>
        /// <returns>One site load and control handoff frames.</returns>
        [UnityTest]
        public IEnumerator StartingWithHeldAzInputWaitsForNeutralBeforeAllowingTakeover()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var keyboard = FindInScene<Zx200KeyboardController>(scene);
                keyboard.ReceiveControlTopic("{\"action4\":254}");
                yield return null;
                Assert.That(keyboard.ManualInputActive, Is.True);
                Assert.That(autonomy.StartAutonomousJob(.3), Is.True);
                yield return null;
                Assert.That(autonomy.IsRunning, Is.True,
                    "Held manual input at the P transition must not immediately cancel the automatic job.");
                keyboard.ReceiveControlTopic("{\"action4\":127}");
                yield return null;
                Assert.That(keyboard.ManualInputActive, Is.False,
                    "The automatic handoff must observe a neutral manual command before accepting takeover input.");
                keyboard.ReceiveControlTopic("{\"action1\":254}");
                yield return null;
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.ManualTakeover));
                Assert.That(keyboard.AutomaticControlActive, Is.False);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Continue a real hand-dug bucket into the truck, then keep digging automatically.</summary>
        /// <returns>Manual excavation, automatic loaded handoff, multiple receipts and cleanup.</returns>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ManualLoadedBucketHandsOffAndCompletesFurtherAutomaticLoads()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                double receiverBefore = receiver.VolumeM3;
                double groundPileBefore = ManualSoilTestMotion.GroundPileVolumeFor(cutter);
                machine.ApplyWorkReadyPose();
                float readyDeadline = Time.realtimeSinceStartup + 20f;
                while (!cutter.StartupReady && Time.realtimeSinceStartup < readyDeadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(cutter.StartupReady, Is.True,
                    "Manual excavation also requires the real machine to reach its initial work-ready pose.");
                for (int frame = 0; frame < 400; frame++)
                {
                    Zx200JointPose target = machine.ReadActualPose();
                    Zx200JointPose approach = machine.approachPose;
                    float boom = Mathf.DeltaAngle(target.boom, approach.boom);
                    float arm = Mathf.DeltaAngle(target.arm, approach.arm);
                    float bucket = Mathf.DeltaAngle(target.bucket, approach.bucket);
                    if (Mathf.Abs(boom) < .5f && Mathf.Abs(arm) < .5f && Mathf.Abs(bucket) < .5f) break;
                    machine.AdjustManualPose(0f, Mathf.Clamp(boom, -.3f, .3f),
                        Mathf.Clamp(arm, -.3f, .3f), Mathf.Clamp(bucket, -.3f, .3f));
                    yield return new WaitForFixedUpdate();
                }
                for (int frame = 0; frame < 250 && cutter.VolumeM3 < .015; frame++)
                {
                    machine.AdjustManualPose(0f, 10f * Time.fixedDeltaTime, 0f, 0f);
                    yield return new WaitForFixedUpdate();
                }
                for (int frame = 0; frame < 180 && cutter.VolumeM3 < .015; frame++)
                {
                    machine.AdjustManualPose(0f, 0f, 10f * Time.fixedDeltaTime,
                        5f * Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                Vector3 manualTip = cutter.TipWorldPosition;
                Debug.Log($"HAND-DUG bucket={cutter.VolumeM3:F6}, removed={cutter.ExcavatedVolumeM3:F6}, " +
                    $"tip={manualTip}, surface={cutter.soil.Terrain.SampleHeight(manualTip) + cutter.soil.transform.position.y:F3}, " +
                    $"hasSurface={cutter.soil.HasSurfaceAt(manualTip)}, ready={cutter.StartupReady}, " +
                    $"status={cutter.Status}, pose={machine.ReadActualPose()}");
                Assert.That(cutter.VolumeM3, Is.GreaterThan(.005),
                    "The handoff must begin with actual soil in the manually operated bucket.");
                Assert.That(autonomy.StartAutonomousJob(.22), Is.True);
                Camera previewCamera = SystemInfo.graphicsDeviceType ==
                    UnityEngine.Rendering.GraphicsDeviceType.Null
                    ? null : machine.GetComponent<Zx200CameraController>().targetCamera;
                var capturedStages = new HashSet<AutonomousExcavationState>();
                AutonomousExcavationState lastStage = autonomy.State;
                float stageEntered = Time.realtimeSinceStartup;
                float deadline = Time.realtimeSinceStartup + 450f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    if (autonomy.State != lastStage)
                    {
                        lastStage = autonomy.State;
                        stageEntered = Time.realtimeSinceStartup;
                    }
                    if (previewCamera != null && Time.realtimeSinceStartup - stageEntered > .45f &&
                        (lastStage == AutonomousExcavationState.Lift ||
                         lastStage == AutonomousExcavationState.SwingToTruck ||
                         lastStage == AutonomousExcavationState.Dump ||
                         lastStage == AutonomousExcavationState.ReturnToDig) &&
                        capturedStages.Add(lastStage))
                        CaptureHandoffFrame(previewCamera, lastStage.ToString());
                    yield return new WaitForFixedUpdate();
                }
                if (previewCamera != null) CaptureHandoffFrame(previewCamera, "Completed");
                Debug.Log($"HANDOFF RESULT state={autonomy.State}, cycles={autonomy.CompletedCycles}, " +
                    $"delivered={autonomy.DeliveredVolumeM3:F6}, receiver={receiver.VolumeM3:F6}, " +
                    $"bucket={cutter.VolumeM3:F6}, status={autonomy.Status}");
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(autonomy.CompletedCycles, Is.GreaterThanOrEqualTo(2));
                Assert.That(autonomy.DeliveredVolumeM3, Is.GreaterThanOrEqualTo(.22));
                Assert.That(autonomy.DeliveredVolumeM3,
                    Is.EqualTo(receiver.VolumeM3 - receiverBefore).Within(1e-5));
                Assert.That(cutter.VolumeM3, Is.LessThanOrEqualTo(.01));
                ManualSoilTestMotion.AssertBalanceIncludingPiles(cutter, groundPileBefore, .003);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Start an empty automatic dig from an operator-adjusted safe linkage pose.</summary>
        /// <returns>Manual pose adjustment, one automatic truck receipt and scene cleanup.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator EmptyManualPoseCanEnterAutomaticDigging()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                machine.ApplyWorkReadyPose();
                float readyDeadline = Time.realtimeSinceStartup + 20f;
                while (!cutter.StartupReady && Time.realtimeSinceStartup < readyDeadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(cutter.StartupReady, Is.True);
                machine.AdjustManualPose(0f, -5f, 5f, 8f);
                for (int frame = 0; frame < 120; frame++) yield return new WaitForFixedUpdate();
                Assert.That(cutter.VolumeM3, Is.Zero.Within(1e-5));
                Assert.That(machine.IsPoseWithinTolerance(machine.workReadyPose, 2f), Is.False);
                double receiverBefore = receiver.VolumeM3;
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);
                float deadline = Time.realtimeSinceStartup + 120f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(receiver.VolumeM3 - receiverBefore, Is.GreaterThanOrEqualTo(.02));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Starting from a hand-steered slope pose should use that area first and change cut location on later buckets.</summary>
        /// <returns>Two or more real cuts and matching truck receipts.</returns>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator HandSteeredStartUsesDifferentDigPointsAndLoadsTruck()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                machine.ApplyWorkReadyPose();
                for (int frame = 0; frame < 80; frame++) yield return new WaitForFixedUpdate();
                machine.AdjustManualPose(16f, 0f, 0f, 0f);
                for (int frame = 0; frame < 120; frame++) yield return new WaitForFixedUpdate();
                Assert.That(machine.ReadActualPose().swing, Is.GreaterThan(10f));
                double before = receiver.VolumeM3;
                Assert.That(autonomy.StartAutonomousJob(.2), Is.True);
                var cutAngles = new System.Collections.Generic.List<float>();
                bool wasCutting = false;
                float deadline = Time.realtimeSinceStartup + 250f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    bool cutting = autonomy.State == AutonomousExcavationState.FillBucket;
                    if (cutting && !wasCutting) cutAngles.Add(machine.ReadActualPose().swing);
                    wasCutting = cutting;
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(cutAngles.Count, Is.GreaterThanOrEqualTo(2));
                Assert.That(cutAngles[0], Is.GreaterThan(8f),
                    "The first automatic cut should stay near the operator-selected part of the slope.");
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(cutAngles[0], cutAngles[1])), Is.GreaterThan(4f),
                    "The next bucket should target a different reachable patch.");
                Assert.That(receiver.VolumeM3 - before, Is.GreaterThanOrEqualTo(.2));
                Assert.That(autonomy.DeliveredVolumeM3,
                    Is.EqualTo(receiver.VolumeM3 - before).Within(1e-5));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Release automatic ownership during live unloading without resetting soil or pose.</summary>
        /// <returns>A real automatic bucket, dump-phase interruption and cleanup.</returns>
        [UnityTest]
        [Timeout(150000)]
        public IEnumerator ManualTakeoverDuringDumpKeepsSoilAndUnlocksControls()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var keyboard = FindInScene<Zx200KeyboardController>(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                Assert.That(autonomy.StartAutonomousJob(.3), Is.True);
                float deadline = Time.realtimeSinceStartup + 100f;
                while (autonomy.IsRunning && autonomy.State != AutonomousExcavationState.Dump &&
                       Time.realtimeSinceStartup < deadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Dump), autonomy.Status);
                double excavatedBefore = cutter.ExcavatedVolumeM3;
                autonomy.RequestManualTakeover();
                yield return null;
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.ManualTakeover));
                Assert.That(autonomy.IsRunning, Is.False);
                Assert.That(keyboard.AutomaticControlActive, Is.False);
                Assert.That(keyboard.fixedBase, Is.False);
                Assert.That(machine.IsBusy, Is.False);
                Assert.That(cutter.ExcavatedVolumeM3, Is.EqualTo(excavatedBefore).Within(1e-6));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Reject the fixed work-face route after the operator drives the track base away.</summary>
        /// <returns>One real site load, displaced base, rejected start and cleanup.</returns>
        [UnityTest]
        public IEnumerator DisplacedTrackBaseCannotStartFixedWorkFaceJob()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var site = FindInScene<CesiumMoundSite>(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var keyboard = FindInScene<Zx200KeyboardController>(scene);
                keyboard.articulationRoot.TeleportRoot(site.bakedSite.machinePosition + Vector3.right * 25f,
                    site.bakedSite.machineRotation);
                Physics.SyncTransforms();
                yield return new WaitForFixedUpdate();
                Debug.Log($"DISPLACED BASE root={keyboard.articulationRoot.transform.position}, " +
                    $"surveyed={site.bakedSite.machinePosition}, siteAllows=" +
                    $"{site.CanStartAutonomousFrom(keyboard.articulationRoot.transform.position, keyboard.articulationRoot.transform.rotation)}");
                Assert.That(autonomy.StartAutonomousJob(.2), Is.False);
                Assert.That(autonomy.IsRunning, Is.False);
                Assert.That(keyboard.AutomaticControlActive, Is.False);
                Assert.That(autonomy.FaultReason, Does.Contain("作业位"));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Drive backward by real track forces, then demand a physical return before the next cut.</summary>
        /// <returns>Scene setup, manual travel, automatic track return, excavation and cleanup.</returns>
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator NearbyManualTrackTravelReturnsPhysicallyBeforeDigging()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var keyboard = FindInScene<Zx200KeyboardController>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                ArticulationBody root = keyboard.articulationRoot;
                Vector3 workPosition = root.transform.position;
                double receiverBefore = receiver.VolumeM3;
                keyboard.enabled = false;
                for (int frame = 0; frame < 140; frame++)
                {
                    keyboard.MoveArticulationRoot(-.65f, 0f, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                keyboard.GetComponent<Zx200TrackDrive>().Stop();
                for (int frame = 0; frame < 90; frame++) yield return new WaitForFixedUpdate();
                keyboard.enabled = true;
                float moved = Vector3.Distance(root.transform.position, workPosition);
                Assert.That(moved, Is.GreaterThan(.35f), "The test must physically move the tracks first.");
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);
                float deadline = Time.realtimeSinceStartup + 180f;
                bool observedReturn = false;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    observedReturn |= autonomy.State.ToString() == "ReturnToWorkSite";
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(observedReturn, Is.True, "Automatic mode must visibly drive back to the work pose.");
                Assert.That(Vector3.Distance(root.transform.position, workPosition), Is.LessThan(.5f));
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(receiver.VolumeM3 - receiverBefore, Is.GreaterThanOrEqualTo(.02));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Manual takeover during physical return must stop the owned track command.</summary>
        /// <returns>Nearby track travel, return entry, takeover and unlocked controls.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator ManualTakeoverDuringTrackReturnStopsDrive()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var keyboard = FindInScene<Zx200KeyboardController>(scene);
                ArticulationBody root = keyboard.articulationRoot;
                Vector3 start = root.transform.position;
                keyboard.enabled = false;
                for (int frame = 0; frame < 140; frame++)
                {
                    keyboard.MoveArticulationRoot(-.65f, 0f, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                keyboard.GetComponent<Zx200TrackDrive>().Stop();
                for (int frame = 0; frame < 90; frame++) yield return new WaitForFixedUpdate();
                keyboard.enabled = true;
                Assert.That(Vector3.Distance(root.transform.position, start), Is.GreaterThan(.35f));
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);
                float deadline = Time.realtimeSinceStartup + 30f;
                while (autonomy.IsRunning && autonomy.State.ToString() != "ReturnToWorkSite" &&
                       Time.realtimeSinceStartup < deadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(autonomy.State.ToString(), Is.EqualTo("ReturnToWorkSite"));
                autonomy.RequestManualTakeover();
                yield return new WaitForFixedUpdate();
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.ManualTakeover));
                Assert.That(keyboard.AutomaticControlActive, Is.False);
                Assert.That(keyboard.fixedBase, Is.False);
                Assert.That(keyboard.GetComponent<Zx200TrackDrive>().ForwardInput, Is.EqualTo(0f));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Forward manual travel must also drive physically back before any cutting starts.</summary>
        /// <returns>Forward travel, automatic return and one received bucket.</returns>
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator NearbyForwardTravelReturnsBeforeAutomaticCut()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var keyboard = FindInScene<Zx200KeyboardController>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                var site = FindInScene<CesiumMoundSite>(scene);
                ArticulationBody root = keyboard.articulationRoot;
                Vector3 workPosition = root.transform.position;
                double before = receiver.VolumeM3;
                keyboard.enabled = false;
                for (int frame = 0; frame < 180 &&
                     Vector3.Distance(root.transform.position, workPosition) < .7f; frame++)
                {
                    keyboard.MoveArticulationRoot(.45f, 0f, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                keyboard.GetComponent<Zx200TrackDrive>().Stop();
                for (int frame = 0; frame < 90; frame++) yield return new WaitForFixedUpdate();
                keyboard.enabled = true;
                Assert.That(Vector3.Distance(root.transform.position, workPosition), Is.GreaterThan(.5f));
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);
                float deadline = Time.realtimeSinceStartup + 180f;
                bool returned = false;
                bool verifiedParked = false;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    returned |= autonomy.State.ToString() == "ReturnToWorkSite";
                    if (returned && !verifiedParked && autonomy.State == AutonomousExcavationState.AcquirePoint)
                    {
                        verifiedParked = true;
                        float positionError = Vector3.ProjectOnPlane(
                            root.transform.position - site.bakedSite.machinePosition, Vector3.up).magnitude;
                        float headingError = Vector3.Angle(
                            Vector3.ProjectOnPlane(root.transform.forward, Vector3.up),
                            Vector3.ProjectOnPlane(site.bakedSite.machineRotation * Vector3.forward, Vector3.up));
                        Debug.Log($"FORWARD RETURN parked error={positionError:F3}m, yaw={headingError:F2}deg");
                        Assert.That(positionError, Is.LessThan(.21f));
                        Assert.That(headingError, Is.LessThan(3.5f));
                    }
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(returned, Is.True);
                Assert.That(verifiedParked, Is.True);
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(Vector3.Distance(root.transform.position, workPosition), Is.LessThan(.5f));
                Assert.That(receiver.VolumeM3 - before, Is.GreaterThanOrEqualTo(.02));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Return from a manually driven pose with a substantial heading error without stalling at the turn boundary.</summary>
        /// <returns>Physical drive, measured return pose and a received bucket.</returns>
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator AngledManualTravelReturnsAndLoadsOneBucket()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var keyboard = FindInScene<Zx200KeyboardController>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                var site = FindInScene<CesiumMoundSite>(scene);
                ArticulationBody root = keyboard.articulationRoot;
                Vector3 start = root.transform.position;
                Vector3 surveyedForward = site.bakedSite.machineRotation * Vector3.forward;
                Assert.That(site.CanReturnAutonomousFrom(start - surveyedForward * 10f), Is.True,
                    "A ten-metre move along the lower bench should remain eligible for physical return.");
                Assert.That(site.CanReturnAutonomousFrom(start - surveyedForward * 14f), Is.False,
                    "The return corridor must remain bounded.");
                Vector3 targetForward = Vector3.ProjectOnPlane(
                    site.bakedSite.machineRotation * Vector3.forward, Vector3.up);
                keyboard.enabled = false;
                for (int frame = 0; frame < 500 &&
                     Vector3.Distance(root.transform.position, start) < 3f; frame++)
                {
                    keyboard.MoveArticulationRoot(-.7f, 0f, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                for (int frame = 0; frame < 150; frame++)
                {
                    float yaw = Vector3.SignedAngle(
                        Vector3.ProjectOnPlane(root.transform.forward, Vector3.up), targetForward, Vector3.up);
                    if (Mathf.Abs(yaw) >= 14f) break;
                    keyboard.MoveArticulationRoot(0f, .35f, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                keyboard.GetComponent<Zx200TrackDrive>().Stop();
                for (int frame = 0; frame < 90; frame++) yield return new WaitForFixedUpdate();
                keyboard.enabled = true;
                float initialYaw = Vector3.Angle(
                    Vector3.ProjectOnPlane(root.transform.forward, Vector3.up), targetForward);
                Assert.That(Vector3.Distance(root.transform.position, start), Is.GreaterThan(2f));
                Assert.That(initialYaw, Is.GreaterThan(10f));
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);
                float deadline = Time.realtimeSinceStartup + 180f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(receiver.VolumeM3, Is.GreaterThanOrEqualTo(.02));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Check that the saved parking pose is modestly closer to the cuttable slope without touching its floor.</summary>
        /// <returns>Scene load, measured face distance and tip clearance, then cleanup.</returns>
        [UnityTest]
        public IEnumerator SavedWorkPoseIsCloserToSlopeAndKeepsTipClearance()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var site = FindInScene<CesiumMoundSite>(scene);
                var keyboard = FindInScene<Zx200KeyboardController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                Vector3 oldWorkPosition = new Vector3(287.27197f, -2.6241553f, 355.4482f);
                Vector3 forward = Vector3.ProjectOnPlane(site.bakedSite.machineRotation * Vector3.forward,
                    Vector3.up).normalized;
                float advance = Vector3.Dot(keyboard.articulationRoot.transform.position - oldWorkPosition, forward);
                float floorClearance = cutter.TipWorldPosition.y -
                    cutter.soil.GetFloorWorldY(cutter.TipWorldPosition);
                float surfaceClearance = cutter.TipWorldPosition.y -
                    (cutter.soil.Terrain.SampleHeight(cutter.TipWorldPosition) + cutter.soil.transform.position.y);
                Debug.Log($"NEAR SLOPE advance={advance:F3}, floorClearance={floorClearance:F3}, " +
                    $"surfaceClearance={surfaceClearance:F3}, " +
                    $"tip={cutter.TipWorldPosition}, base={keyboard.articulationRoot.transform.position}");
                Assert.That(advance, Is.InRange(.15f, .5f));
                Assert.That(floorClearance, Is.GreaterThan(.08f));
                Assert.That(surfaceClearance, Is.GreaterThan(.08f));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Stopping and restarting in the same frame must leave only the new job driving the machine.</summary>
        /// <returns>Immediate ownership switch and one real truck receipt.</returns>
        [UnityTest]
        [Timeout(150000)]
        public IEnumerator ImmediateRestartAfterManualTakeoverHasOnlyOneAutomaticJob()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                double before = receiver.VolumeM3;
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);
                autonomy.RequestManualTakeover();
                var routineField = typeof(AutonomousExcavationController).GetField("jobRoutine",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That(routineField, Is.Not.Null);
                Assert.That(autonomy.StartAutonomousJob(.02), Is.False,
                    "A new job must wait until the previous coroutine has unwound its safety cleanup.");
                float releaseDeadline = Time.realtimeSinceStartup + 2f;
                while (routineField.GetValue(autonomy) != null &&
                       Time.realtimeSinceStartup < releaseDeadline) yield return null;
                Assert.That(routineField.GetValue(autonomy), Is.Null);
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);
                float deadline = Time.realtimeSinceStartup + 100f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(autonomy.CompletedCycles, Is.EqualTo(1));
                Assert.That(receiver.VolumeM3 - before, Is.GreaterThanOrEqualTo(.02));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Finish the saved scene's one-cubic-metre job using actual truck receipts and a parked track base.</summary>
        /// <returns>Scene startup, the configured multi-bucket job and scene cleanup.</returns>
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator ConfiguredOneCubicMetreJobReachesCompleted()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                AutonomousExcavationController autonomy = FindInScene<AutonomousExcavationController>(scene);
                BucketSoilCutter cutter = FindInScene<BucketSoilCutter>(scene);
                SoilReceivingZone receiver = FindInScene<SoilReceivingZone>(scene);
                Zx200KeyboardController keyboard = autonomy.machine.GetComponent<Zx200KeyboardController>();
                keyboard.ResolveArticulationRoot();
                Vector3 trackBaseAtStart = keyboard.articulationRoot.transform.position;
                double receiverBefore = receiver.VolumeM3;
                double groundPileBefore = ManualSoilTestMotion.GroundPileVolumeFor(cutter);
                Assert.That(autonomy.targetVolumeM3, Is.EqualTo(1f).Within(.001f));
                Assert.That(autonomy.StartAutonomousJob(), Is.True);
                float deadline = Time.realtimeSinceStartup + 720f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                    yield return new WaitForFixedUpdate();

                Debug.Log($"CONFIGURED JOB state={autonomy.State}, cycles={autonomy.CompletedCycles}, " +
                    $"delivered={autonomy.DeliveredVolumeM3:F6}, receiver={receiver.VolumeM3:F6}, " +
                    $"bucket={cutter.VolumeM3:F6}, status={autonomy.Status}");
                Assert.That(autonomy.IsRunning, Is.False, "The configured 1 m³ job exceeded its physical-run deadline.");
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(autonomy.CompletedCycles, Is.GreaterThanOrEqualTo(4));
                Assert.That(autonomy.DeliveredVolumeM3, Is.GreaterThanOrEqualTo(1d));
                Assert.That(autonomy.DeliveredVolumeM3,
                    Is.EqualTo(receiver.VolumeM3 - receiverBefore).Within(1e-5));
                Assert.That(cutter.VolumeM3, Is.LessThanOrEqualTo(.01));
                Assert.That(Vector3.Distance(trackBaseAtStart, keyboard.articulationRoot.transform.position),
                    Is.LessThan(.2f));
                ManualSoilTestMotion.AssertBalanceIncludingPiles(cutter, groundPileBefore, .003);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Run at least three automatic dig, load and return cycles using actual landing volume.</summary>
        /// <returns>Scene load, Cesium readiness, repeated autonomous cycles and cleanup frames.</returns>
        [UnityTest]
        [Timeout(420000)]
        public IEnumerator RunsThreeAutomaticDigAndLoadCycles()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                Assert.That(autonomy, Is.Not.Null);
                Assert.That(machine, Is.Not.Null);
                Assert.That(cutter, Is.Not.Null);
                Assert.That(receiver, Is.Not.Null);
                Assert.That(receiver.size, Is.EqualTo(new Vector2(1.7f, 4.4f)),
                    "The serialized truck bed must match the verified loading-bed geometry.");

                double receiverBefore = receiver.VolumeM3;
                double groundPileBefore = ManualSoilTestMotion.GroundPileVolumeFor(cutter);
                Zx200KeyboardController keyboard = machine.GetComponent<Zx200KeyboardController>();
                ArticulationBody trackBase = keyboard.articulationRoot;
                Assert.That(keyboard.fixedBase, Is.False,
                    "Manual track driving is available while the autonomous job is idle.");
                Assert.That(trackBase.immovable, Is.False,
                    "The idle track articulation root is released for manual driving.");
                Vector3 trackBaseAtSpawn = trackBase.transform.position;
                for (int i = 0; i < 100; i++) yield return new WaitForFixedUpdate();
                Vector3 trackBaseAtStart = trackBase.transform.position;
                Debug.Log($"TRACK BASE IDLE spawn={trackBaseAtSpawn}, settled={trackBaseAtStart}, " +
                    $"delta={trackBaseAtStart - trackBaseAtSpawn}, velocity={trackBase.linearVelocity}");
                double maximumObservedBucketLoad = cutter.VolumeM3;
                float maximumTrackBaseDrift = 0f;
                float maximumSwingAfterDumpOpens = 0f;
                float swingAtOpenBucket = 0f;
                bool bucketOpeningObserved = false;
                int openedBucketCycles = 0;
                float maximumCutBucketAngle = float.NegativeInfinity;
                float nearFullStartTime = float.NaN;
                float bucketAngleAtNearFull = float.NaN;
                float nearFullCurlDelay = float.NaN;
                float longestNearFullPause = 0f;
                float lastNearFullMotionTime = float.NaN;
                Vector3 lastNearFullTip = Vector3.zero;
                Zx200JointPose lastNearFullPose = default;
                float carriedArmAngle = float.NegativeInfinity;
                float carriedBoomAngle = float.NaN;
                float carriedBucketAngle = float.NaN;
                float carriedMouthUpDot = float.NaN;
                float bucketAngleAtFillStart = float.NaN;
                Vector3 trackBaseAtMaximumDrift = trackBaseAtStart;
                AutonomousExcavationState stateAtMaximumTrackBaseDrift = autonomy.State;
                Assert.That(autonomy.StartAutonomousJob(.5), Is.True);
                var observedStates = new List<AutonomousExcavationState>();
                float deadline = Time.realtimeSinceStartup + 330;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    maximumObservedBucketLoad = System.Math.Max(maximumObservedBucketLoad, cutter.VolumeM3);
                    if (autonomy.State == AutonomousExcavationState.LowerAndEnter ||
                        autonomy.State == AutonomousExcavationState.FillBucket ||
                        autonomy.State == AutonomousExcavationState.Lift ||
                        autonomy.State == AutonomousExcavationState.SwingToTruck ||
                        autonomy.State == AutonomousExcavationState.ReturnToDig)
                        Assert.That(cutter.GetComponent<BucketSoilUnloader>().enabled, Is.False,
                            "Soil must stay in the bucket until it opens over the truck.");
                    if (autonomy.State == AutonomousExcavationState.FillBucket)
                    {
                        Zx200JointPose cuttingPose = machine.ReadActualPose();
                        maximumCutBucketAngle = Mathf.Max(maximumCutBucketAngle, cuttingPose.bucket);
                        if (float.IsNaN(bucketAngleAtFillStart))
                            bucketAngleAtFillStart = cuttingPose.bucket;
                        float fillFraction = cutter.EffectiveCapacityM3 > .0001
                            ? (float)(cutter.VolumeM3 / cutter.EffectiveCapacityM3) : 0f;
                        if (fillFraction >= .55f)
                        {
                            if (float.IsNaN(nearFullStartTime))
                            {
                                nearFullStartTime = Time.realtimeSinceStartup;
                                bucketAngleAtNearFull = cuttingPose.bucket;
                                lastNearFullMotionTime = nearFullStartTime;
                                lastNearFullTip = cutter.TipWorldPosition;
                                lastNearFullPose = cuttingPose;
                                if (cuttingPose.bucket >= bucketAngleAtFillStart + 5f)
                                    nearFullCurlDelay = 0f;
                            }
                            else if (float.IsNaN(nearFullCurlDelay) &&
                                cuttingPose.bucket >= bucketAngleAtNearFull + 5f)
                                nearFullCurlDelay = Time.realtimeSinceStartup - nearFullStartTime;
                            Vector3 nearFullTip = cutter.TipWorldPosition;
                            bool progressed = Vector3.Distance(nearFullTip, lastNearFullTip) > .04f ||
                                Mathf.Abs(cuttingPose.boom - lastNearFullPose.boom) > 1f ||
                                Mathf.Abs(cuttingPose.arm - lastNearFullPose.arm) > 1f ||
                                Mathf.Abs(cuttingPose.bucket - lastNearFullPose.bucket) > 1f;
                            if (progressed)
                            {
                                lastNearFullMotionTime = Time.realtimeSinceStartup;
                                lastNearFullTip = nearFullTip;
                                lastNearFullPose = cuttingPose;
                            }
                            longestNearFullPause = Mathf.Max(longestNearFullPause,
                                Time.realtimeSinceStartup - lastNearFullMotionTime);
                        }
                    }
                    if (autonomy.State == AutonomousExcavationState.SwingToTruck && carriedArmAngle < 0f)
                    {
                        carriedArmAngle = machine.ReadActualPose().arm;
                        carriedBoomAngle = machine.ReadActualPose().boom;
                        carriedBucketAngle = machine.ReadActualPose().bucket;
                        BucketSoilUnloader mouth = cutter.GetComponent<BucketSoilUnloader>();
                        carriedMouthUpDot = Vector3.Dot(
                            mouth.transform.TransformDirection(mouth.openingNormalLocal).normalized,
                            Vector3.up);
                    }
                    if (autonomy.State == AutonomousExcavationState.Dump)
                    {
                        Zx200JointPose dumpMotion = machine.ReadActualPose();
                        if (autonomy.CurrentMotionAxis == AutonomousMotionAxis.Bucket ||
                            bucketOpeningObserved)
                        {
                            if (!bucketOpeningObserved)
                            {
                                swingAtOpenBucket = dumpMotion.swing;
                                bucketOpeningObserved = true;
                                openedBucketCycles++;
                            }
                            maximumSwingAfterDumpOpens = Mathf.Max(maximumSwingAfterDumpOpens,
                                Mathf.Abs(Mathf.DeltaAngle(swingAtOpenBucket, dumpMotion.swing)));
                        }
                    }
                    else bucketOpeningObserved = false;
                    float trackBaseDrift = Vector3.Distance(trackBaseAtStart, trackBase.transform.position);
                    if (trackBaseDrift > maximumTrackBaseDrift)
                    {
                        maximumTrackBaseDrift = trackBaseDrift;
                        trackBaseAtMaximumDrift = trackBase.transform.position;
                        stateAtMaximumTrackBaseDrift = autonomy.State;
                    }
                    if (observedStates.Count == 0 || observedStates[observedStates.Count - 1] != autonomy.State)
                        observedStates.Add(autonomy.State);
                    bool recoveringTruckClearance = autonomy.State == AutonomousExcavationState.SwingToTruck &&
                        autonomy.CurrentMotionAxis == AutonomousMotionAxis.Boom;
                    string truckPenetration = FindTruckPenetration(machine, receiver, recoveringTruckClearance);
                    Assert.That(truckPenetration, Is.Null,
                        $"机器人关节运动穿透卡车实体；阶段={autonomy.State}/{autonomy.CurrentMotionAxis}；{truckPenetration}");
                    yield return new WaitForFixedUpdate();
                }
                if (observedStates.Count == 0 || observedStates[observedStates.Count - 1] != autonomy.State)
                    observedStates.Add(autonomy.State);
                maximumObservedBucketLoad = System.Math.Max(maximumObservedBucketLoad, cutter.VolumeM3);
                float finalTrackBaseDrift = Vector3.Distance(trackBaseAtStart, trackBase.transform.position);
                Debug.Log($"TRACK BASE ACTIVE start={trackBaseAtStart}, max={trackBaseAtMaximumDrift} " +
                    $"({maximumTrackBaseDrift:F3}m during {stateAtMaximumTrackBaseDrift}), " +
                    $"final={trackBase.transform.position} ({finalTrackBaseDrift:F3}m), " +
                    $"velocity={trackBase.linearVelocity}");
                observedStates.Clear();
                observedStates.AddRange(autonomy.StateHistory);
                Zx200JointPose actualPose = machine.ReadActualPose();
                Assert.That(autonomy.IsRunning, Is.False,
                    $"自主作业超时，最后状态={autonomy.State}/{autonomy.CurrentMotionAxis}: {autonomy.Status}; " +
                    $"设备忙碌={machine.IsBusy}，实际姿态=" +
                    $"({actualPose.swing:F1},{actualPose.boom:F1},{actualPose.arm:F1},{actualPose.bucket:F1})");

                Debug.Log($"AUTONOMOUS state={autonomy.State}, status={autonomy.Status}, " +
                    $"delivered={autonomy.DeliveredVolumeM3:F6}, receiver={receiver.VolumeM3:F6}, " +
                    $"bucket={cutter.VolumeM3:F6}, cycles={autonomy.CompletedCycles}");
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(autonomy.CompletedCycles, Is.GreaterThanOrEqualTo(3));
                Assert.That(openedBucketCycles, Is.GreaterThanOrEqualTo(3));
                Assert.That(autonomy.FullBucketRecoveryCount, Is.Zero,
                    "The first three normal bucket cycles must close from live feedback, not the full-bucket lift-then-close fallback.");
                Assert.That(maximumCutBucketAngle, Is.GreaterThan(70f),
                    "The bucket must curl through most of its scoop stroke before the loaded swing.");
                Debug.Log($"NEAR FULL motion angle={bucketAngleAtNearFull:F1}, " +
                    $"next 5-degree curl delay={nearFullCurlDelay:F2}s, " +
                    $"longest no-motion pause={longestNearFullPause:F2}s, " +
                    $"carry boom/arm/bucket=({carriedBoomAngle:F1},{carriedArmAngle:F1},{carriedBucketAngle:F1}), " +
                    $"mouth up dot={carriedMouthUpDot:F2}");
                Assert.That(maximumCutBucketAngle - bucketAngleAtFillStart, Is.GreaterThan(10f),
                    "The bucket must curl through the cut even when a valid bucket never reaches 55% fill.");
                if (!float.IsNaN(bucketAngleAtNearFull))
                {
                    Assert.That(float.IsNaN(nearFullCurlDelay), Is.False,
                        "By 55% fill the bucket must already be curling, or advance another 5 degrees without waiting for capacity.");
                    Assert.That(nearFullCurlDelay, Is.LessThan(4f),
                        "The near-full bucket must not remain stationary waiting for the cut timeout.");
                    Assert.That(longestNearFullPause, Is.LessThan(1.5f),
                        "The near-full digging phase must not pause with every working joint stationary.");
                }
                Assert.That(carriedArmAngle, Is.GreaterThan(85f),
                    "The loaded arm should tuck farther in than the previous fixed 80-degree carry target.");
                Assert.That(carriedBoomAngle, Is.LessThan(-32f),
                    "The loaded boom should rise to a useful, but not excessive, carry height.");
                Assert.That(float.IsNaN(carriedBucketAngle), Is.False,
                    "The bucket pose must be resolved before the carry swing starts.");
                Assert.That(carriedMouthUpDot, Is.GreaterThan(Mathf.Cos(20f * Mathf.Deg2Rad)),
                    "The full bucket mouth must stay within 20 degrees of upward during the carry swing.");
                Assert.That(maximumSwingAfterDumpOpens, Is.LessThan(2f),
                    "The upper structure must finish aligning before the bucket opens over the truck.");
                Assert.That(maximumObservedBucketLoad, Is.GreaterThanOrEqualTo(.12),
                    "A successful cut must continue beyond the empty-cut validity threshold and collect a substantial part of the 0.304 m³ bucket.");
                Assert.That(Vector3.Distance(trackBaseAtStart, trackBase.transform.position), Is.LessThan(.2f),
                    "The track base must remain parked for all bucket cycles; only the upper structure slews.");
                Assert.That(observedStates, Does.Contain(AutonomousExcavationState.SwingToTruck));
                Assert.That(observedStates, Does.Contain(AutonomousExcavationState.Dump));
                Assert.That(observedStates, Does.Contain(AutonomousExcavationState.ConfirmDump));
                Assert.That(observedStates, Does.Contain(AutonomousExcavationState.ReturnToDig));
                Assert.That(ContainsOrderedCycle(observedStates), Is.True,
                    "Physical workflow stages must reach the truck, dump, confirm the delivered soil, and return in order. " +
                    $"Observed={string.Join(" -> ", observedStates)}");
                Assert.That(receiver.VolumeM3 - receiverBefore, Is.GreaterThan(.005));
                Assert.That(autonomy.DeliveredVolumeM3,
                    Is.EqualTo(receiver.VolumeM3 - receiverBefore).Within(1e-5));
                Assert.That(cutter.VolumeM3, Is.LessThanOrEqualTo(.01));
                Assert.That(cutter.GetComponent<BucketSoilUnloader>().AirborneVolumeM3,
                    Is.LessThanOrEqualTo(1e-5));
                // Repeated terrain edits include loose-pile cuts outside the terrain heightmap integration.
                ManualSoilTestMotion.AssertBalanceIncludingPiles(cutter, groundPileBefore, .003);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Catch the regression where the controller stops scooping as soon as its tiny non-empty threshold is met.</summary>
        /// <returns>One real Cesium-backed scoop and cleanup after measured loading.</returns>
        [UnityTest]
        public IEnumerator FirstAutomaticBucketCollectsMoreThanTheEmptyCutThreshold()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
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
                Assert.That(autonomy.StartAutonomousJob(.01), Is.True);
                double maximumObservedLoad = cutter.VolumeM3;
                double excavatedVolumeAtPeakLoad = cutter.ExcavatedVolumeM3;
                float collectionEfficiencyAtPeakLoad = cutter.CollectionEfficiency;
                string cutterStatusAtPeakLoad = cutter.Status;
                Zx200JointPose poseAtPeakLoad = autonomy.machine.ReadActualPose();
                bool capturedCutStartPose = false;
                bool observedArmAndBucketMotion = false;
                bool capturedFirstRemoval = false;
                bool firstRemovalWasInCoordinatedCut = false;
                bool bothJointsMovedAtFirstRemoval = false;
                Zx200JointPose cutStartPose = default;
                string cutExitMetrics = "未观察到切削退出";
                bool observedCutting = false;
                float minimumCutFloorClearance = float.PositiveInfinity;
                float maximumCutPenetration = 0f;
                string maximumPenetrationReason = string.Empty;
                float deadline = Time.realtimeSinceStartup + 60f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    Zx200JointPose currentCutPose = default;
                    bool readingCoordinatedCut = autonomy.State == AutonomousExcavationState.FillBucket &&
                        autonomy.machine.IsBusy;
                    if (autonomy.State == AutonomousExcavationState.FillBucket) observedCutting = true;
                    else if (observedCutting && cutExitMetrics == "未观察到切削退出")
                    {
                        Vector3 exitTip = cutter.TipWorldPosition;
                        var exitResistance = autonomy.machine.bucketJoint.GetComponent<BucketSoilResistance>();
                        cutExitMetrics = $"volume={cutter.VolumeM3:F4}, capacity={cutter.EffectiveCapacityM3:F4}, " +
                            $"floorClearance={exitTip.y - cutter.soil.GetFloorWorldY(exitTip):F3}, " +
                            $"resistance={exitResistance.BlockReason}, pose={autonomy.machine.ReadActualPose()}";
                    }
                    if (autonomy.State == AutonomousExcavationState.FillBucket)
                    {
                        Vector3 sampleTip = cutter.TipWorldPosition;
                        minimumCutFloorClearance = Mathf.Min(minimumCutFloorClearance,
                            sampleTip.y - cutter.soil.GetFloorWorldY(sampleTip));
                        var sampleResistance = autonomy.machine.bucketJoint.GetComponent<BucketSoilResistance>();
                        if (sampleResistance.PenetrationM > maximumCutPenetration)
                        {
                            maximumCutPenetration = sampleResistance.PenetrationM;
                            maximumPenetrationReason = sampleResistance.BlockReason;
                        }
                    }
                    if (readingCoordinatedCut)
                    {
                        currentCutPose = autonomy.machine.ReadActualPose();
                        if (!capturedCutStartPose)
                        {
                            cutStartPose = currentCutPose;
                            capturedCutStartPose = true;
                        }
                    }
                    if (!capturedFirstRemoval && cutter.ExcavatedVolumeM3 > 1e-5)
                    {
                        capturedFirstRemoval = true;
                        firstRemovalWasInCoordinatedCut =
                            autonomy.State == AutonomousExcavationState.FillBucket &&
                            autonomy.CurrentMotionAxis == AutonomousMotionAxis.ArmAndBucket &&
                            !cutter.IsCuttingSuppressed;
                        bothJointsMovedAtFirstRemoval = readingCoordinatedCut &&
                            Mathf.Abs(currentCutPose.arm - cutStartPose.arm) > .01f &&
                            Mathf.Abs(currentCutPose.bucket - cutStartPose.bucket) > .01f;
                    }
                    if (readingCoordinatedCut)
                    {
                        if (autonomy.CurrentMotionAxis == AutonomousMotionAxis.ArmAndBucket &&
                            Mathf.Abs(currentCutPose.arm - cutStartPose.arm) > .1f &&
                            Mathf.Abs(currentCutPose.bucket - cutStartPose.bucket) > .1f)
                            observedArmAndBucketMotion = true;
                    }
                    if (cutter.VolumeM3 > maximumObservedLoad)
                    {
                        maximumObservedLoad = cutter.VolumeM3;
                        excavatedVolumeAtPeakLoad = cutter.ExcavatedVolumeM3;
                        collectionEfficiencyAtPeakLoad = cutter.CollectionEfficiency;
                        cutterStatusAtPeakLoad = cutter.Status;
                        poseAtPeakLoad = autonomy.machine.ReadActualPose();
                    }
                    yield return new WaitForFixedUpdate();
                }
                if (cutter.VolumeM3 > maximumObservedLoad)
                {
                    maximumObservedLoad = cutter.VolumeM3;
                    excavatedVolumeAtPeakLoad = cutter.ExcavatedVolumeM3;
                    collectionEfficiencyAtPeakLoad = cutter.CollectionEfficiency;
                    cutterStatusAtPeakLoad = cutter.Status;
                    poseAtPeakLoad = autonomy.machine.ReadActualPose();
                }
                Debug.Log($"FIRST BUCKET max={maximumObservedLoad:F4}m³, cut={excavatedVolumeAtPeakLoad:F4}m³, " +
                    $"last-efficiency={collectionEfficiencyAtPeakLoad:P0}, status={cutterStatusAtPeakLoad}, " +
                    $"pose=({poseAtPeakLoad.swing:F1},{poseAtPeakLoad.boom:F1},{poseAtPeakLoad.arm:F1},{poseAtPeakLoad.bucket:F1})");
                Debug.Log("FIRST BUCKET CUT EXIT " + cutExitMetrics);
                Debug.Log($"FIRST BUCKET CUT LIMIT minFloor={minimumCutFloorClearance:F3}, " +
                    $"maxPenetration={maximumCutPenetration:F3}, reason={maximumPenetrationReason}, " +
                    $"partialRecovery={autonomy.PartialBucketRecoveryCount}");
                var entryResistance = autonomy.machine.bucketJoint.GetComponent<BucketSoilResistance>();
                Assert.That(autonomy.LastCutEntryDepthMeters,
                    Is.LessThanOrEqualTo(entryResistance.maximumPenetration - .025f),
                    "The initial entry must leave room for arm-and-bucket cutting before the hard depth stop.");
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(maximumObservedLoad, Is.GreaterThanOrEqualTo(.12),
                    "The scoop stroke must continue well beyond minimumLoadVolumeM3=0.005 m³ and collect real terrain soil.");
                Assert.That(observedArmAndBucketMotion, Is.True,
                    "The cutting state must report and execute the coordinated arm-and-bucket scoop stroke.");
                Assert.That(capturedFirstRemoval, Is.True,
                    "The first measured terrain removal must be observed during the automatic scoop.");
                Assert.That(firstRemovalWasInCoordinatedCut, Is.True,
                    "Soil removal must not occur during boom-only entry; it starts unsuppressed in FillBucket with ArmAndBucket motion.");
                Assert.That(bothJointsMovedAtFirstRemoval, Is.True,
                    "At the first measured terrain removal, both the arm and bucket must already have moved from the coordinated-cut start pose.");
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Finish a thin, finite soil layer with its real partial volume while preserving the local floor.</summary>
        /// <returns>A 26 cm prepared soil layer, floor-clearance monitoring, actual transfer and soil-ledger checks.</returns>
        [UnityTest]
        public IEnumerator ThinSoilLayerCompletesWithARealPartialBucket()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                AutonomousExcavationController autonomy = FindInScene<AutonomousExcavationController>(scene);
                BucketSoilCutter cutter = FindInScene<BucketSoilCutter>(scene);
                BucketSoilUnloader unloader = cutter.GetComponent<BucketSoilUnloader>();
                DeformableSoilTerrain soil = cutter.soil;
                BucketSoilResistance resistance = autonomy.machine.bucketJoint.GetComponent<BucketSoilResistance>();
                autonomy.cutEntryDepthMeters = .12f;
                float[,] ground = soil.GetPreparedGroundCopy();
                Assert.That(ground, Is.Not.Null, "The baked site must preserve a separate cutting floor.");
                TerrainData terrain = soil.Terrain.terrainData;
                float[,] thinLayer = new float[ground.GetLength(0), ground.GetLength(1)];
                float normalizedThickness = .26f / terrain.size.y;
                for (int z = 0; z < thinLayer.GetLength(0); z++)
                    for (int x = 0; x < thinLayer.GetLength(1); x++)
                        thinLayer[z, x] = Mathf.Min(1f, ground[z, x] + normalizedThickness);
                soil.PrepareMoundSurface(thinLayer, ground);
                Physics.SyncTransforms();
                yield return new WaitForFixedUpdate();

                double initialPileVolume = ManualSoilTestMotion.GroundPileVolumeFor(cutter);
                Assert.That(autonomy.StartAutonomousJob(.005), Is.True);
                float deadline = Time.realtimeSinceStartup + 90f;
                float minimumScoopFloorClearance = float.PositiveInfinity;
                AutonomousExcavationState minimumClearanceState = autonomy.State;
                Vector3 minimumClearanceTip = cutter.TipWorldPosition;
                Vector3 minimumClearanceTipVelocity = resistance.TipVelocity;
                Zx200JointPose minimumClearancePose = autonomy.machine.ReadActualPose();
                int floorBrakeCountAtMinimumClearance = resistance.VelocityFloorBrakeCount;
                bool dumpPreparationObserved = false;
                bool unloaderStayedClosedDuringPreparation = true;
                float armAtBucketOpening = float.NaN;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    if (autonomy.State == AutonomousExcavationState.PrepareDump)
                    {
                        dumpPreparationObserved = true;
                        unloaderStayedClosedDuringPreparation &= !unloader.enabled;
                    }
                    if (autonomy.State == AutonomousExcavationState.Dump && unloader.enabled &&
                        float.IsNaN(armAtBucketOpening))
                        armAtBucketOpening = autonomy.machine.ReadActualPose().arm;

                    Vector3 tip = cutter.TipWorldPosition;
                    float floorClearance = tip.y - soil.GetFloorWorldY(tip);
                    if (floorClearance < minimumScoopFloorClearance)
                    {
                        minimumScoopFloorClearance = floorClearance;
                        minimumClearanceState = autonomy.State;
                        minimumClearanceTip = tip;
                        minimumClearanceTipVelocity = resistance.TipVelocity;
                        minimumClearancePose = autonomy.machine.ReadActualPose();
                        floorBrakeCountAtMinimumClearance = resistance.VelocityFloorBrakeCount;
                    }
                    yield return new WaitForFixedUpdate();
                }

                Assert.That(autonomy.IsRunning, Is.False, autonomy.Status);
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(autonomy.DeliveredVolumeM3, Is.GreaterThanOrEqualTo(.005));
                Assert.That(dumpPreparationObserved, Is.True,
                    "The workflow must enter a visible truck-bed deployment and alignment phase before opening the bucket.");
                Assert.That(unloaderStayedClosedDuringPreparation, Is.True,
                    "The bucket must retain its soil while extending and re-centering over the truck.");
                Assert.That(ContainsStateInOrder(
                    autonomy.StateHistory,
                    AutonomousExcavationState.PrepareDump,
                    AutonomousExcavationState.Dump), Is.True,
                    "The truck-side deployment phase must precede actual unloading.");
                Assert.That(armAtBucketOpening,
                    Is.EqualTo(autonomy.machine.dumpPreparePose.arm).Within(3f),
                    "The arm must reach the configured truck-side deployment pose before the bucket opens.");
                Assert.That(resistance.VelocityFloorBrakeCount, Is.GreaterThan(0),
                    "The thin-layer dig must invoke the per-physics-step floor momentum brake before reaching the cut floor.");
                Assert.That(minimumScoopFloorClearance, Is.GreaterThanOrEqualTo(-.005f),
                    $"Adaptive cutting must brake before the baked local floor; state={minimumClearanceState}, " +
                    $"tip={minimumClearanceTip}, pose=({minimumClearancePose.boom:F1}," +
                    $"{minimumClearancePose.arm:F1},{minimumClearancePose.bucket:F1}), " +
                    $"toothVelocity={minimumClearanceTipVelocity}, " +
                    $"brakeStepsAtMinimum={floorBrakeCountAtMinimumClearance}.");
                Assert.That(autonomy.LastCutEntryDepthMeters, Is.GreaterThanOrEqualTo(soil.minimumCutDepth));
                Assert.That(autonomy.LastCutEntryDepthMeters, Is.EqualTo(autonomy.cutEntryDepthMeters).Within(.001f),
                    "This thin layer must retain the configured shallow plunge; local reduction is covered separately by helper tests.");
                Assert.That(autonomy.FullBucketRecoveryCount, Is.Zero,
                    "A thin layer must complete with a safe partial bucket, without invoking the full-bucket fallback.");
                ManualSoilTestMotion.AssertBalanceIncludingPiles(cutter, initialPileVolume, .003);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Reject an already-overlapping truck pose before granting automatic control.</summary>
        /// <returns>Scene setup, collision injection, start rejection and scene cleanup.</returns>
        [UnityTest]
        public IEnumerator PreexistingTruckPenetrationRejectsAutomaticStart()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            GameObject probe = null;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                var keyboard = FindInScene<Zx200KeyboardController>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                Collider machineCollider = machine.GetComponentInChildren<Collider>(true);
                Assert.That(machineCollider, Is.Not.Null);
                Transform truckRoot = receiver.transform.parent != null
                    ? receiver.transform.parent
                    : receiver.transform;
                probe = new GameObject("Preexisting truck collision probe");
                probe.transform.SetParent(truckRoot, true);
                probe.transform.position = machineCollider.bounds.center;
                probe.AddComponent<BoxCollider>().size = Vector3.one * .25f;
                Physics.SyncTransforms();

                Assert.That(autonomy.StartAutonomousJob(.02), Is.False);
                Assert.That(autonomy.IsRunning, Is.False);
                Assert.That(keyboard.AutomaticControlActive, Is.False);
                Assert.That(autonomy.FaultReason, Does.Contain("矿车穿透"));
            }
            finally
            {
                if (probe != null) Object.Destroy(probe);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Verify the production motion monitor stops automatically when a truck collider is obstructed.</summary>
        /// <returns>Scene loading, collision injection during truck approach, fault handling and cleanup frames.</returns>
        [UnityTest]
        public IEnumerator StopsAutomaticMotionWhenMachineOverlapsTruckCollider()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            GameObject probe = null;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);

                float approachDeadline = Time.realtimeSinceStartup + 40f;
                while ((autonomy.State != AutonomousExcavationState.SwingToTruck || !machine.IsBusy) &&
                       autonomy.IsRunning && Time.realtimeSinceStartup < approachDeadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.SwingToTruck), autonomy.Status);
                Assert.That(machine.IsBusy, Is.True);

                Collider machineCollider = machine.GetComponentInChildren<Collider>(true);
                Assert.That(machineCollider, Is.Not.Null);
                Transform truckRoot = receiver.transform.parent != null
                    ? receiver.transform.parent
                    : receiver.transform;
                probe = new GameObject("Runtime collision safety probe");
                probe.transform.SetParent(truckRoot, true);
                probe.transform.position = machineCollider.bounds.center;
                probe.AddComponent<BoxCollider>().size = Vector3.one * .25f;
                Physics.SyncTransforms();

                float stopDeadline = Time.realtimeSinceStartup + 3f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < stopDeadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Fault));
                Assert.That(autonomy.FaultReason, Does.Contain("卡车实体发生穿透"));
                Assert.That(machine.IsBusy, Is.False);
            }
            finally
            {
                if (probe != null) Object.Destroy(probe);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Reset from the truck-side dump pose and verify safe retreat plus clean soil ledgers.</summary>
        /// <returns>Scene load, truck-side reset motion, ledger checks and cleanup frames.</returns>
        [UnityTest]
        public IEnumerator ResetRestoresSoilAndReceiverLedgers()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                var unloader = cutter.GetComponent<BucketSoilUnloader>();
                Assert.That(autonomy.StartAutonomousJob(.02), Is.True);
                float truckApproachDeadline = Time.realtimeSinceStartup + 45;
                while (autonomy.State != AutonomousExcavationState.Dump && autonomy.IsRunning &&
                       Time.realtimeSinceStartup < truckApproachDeadline)
                    yield return null;
                Assert.That(cutter.ExcavatedVolumeM3, Is.GreaterThan(.001));
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Dump), autonomy.Status);
                Assert.That(unloader.enabled, Is.False,
                    "The controller temporarily pauses unloading while positioning the bucket.");

                autonomy.ResetAutonomousJob();
                float deadline = Time.realtimeSinceStartup + 20;
                while (autonomy.State != AutonomousExcavationState.Idle &&
                       Time.realtimeSinceStartup < deadline)
                {
                    Assert.That(autonomy.CurrentMotionAxis, Is.Not.EqualTo(AutonomousMotionAxis.None),
                        "Reset must move one articulation axis at a time.");
                    string truckPenetration = FindTruckPenetration(machine, receiver);
                    Assert.That(truckPenetration, Is.Null,
                        $"Reset motion penetrated the truck; axis={autonomy.CurrentMotionAxis}; {truckPenetration}");
                    yield return new WaitForFixedUpdate();
                }

                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Idle));
                Assert.That(machine.IsPoseWithinTolerance(machine.parkedPose, 3f), Is.True,
                    "Reset must finish in the compact parked pose rather than cutting-ready pose.");
                Assert.That(unloader.enabled, Is.True,
                    "Resetting from Dump must restore the unloader after stopping the job coroutine.");
                Assert.That(cutter.VolumeM3, Is.Zero.Within(1e-8));
                Assert.That(receiver.VolumeM3, Is.Zero.Within(1e-8));
                Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(.0005));
                Assert.That(cutter.ExcavatedVolumeM3, Is.Zero.Within(1e-8));

                Assert.That(autonomy.StartAutonomousJob(.02), Is.True,
                    "A clean reset must leave the same site ready for another job.");
                float restartDeadline = Time.realtimeSinceStartup + 45f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < restartDeadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(autonomy.IsRunning, Is.False,
                    $"The restarted job must finish instead of stalling: {autonomy.State}/{autonomy.Status}");
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Completed), autonomy.Status);
                Assert.That(receiver.VolumeM3, Is.GreaterThan(.005));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Reset after unloading has emitted airborne soil and verify every active soil ledger is cleared.</summary>
        /// <returns>Scene startup, active-flight reset motion, ledger checks and scene cleanup.</returns>
        [UnityTest]
        public IEnumerator ResetDuringActiveSoilFlowClearsFlightAndRestoresUnloader()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                var unloader = cutter.GetComponent<BucketSoilUnloader>();
                Assert.That(unloader.enabled, Is.True);
                Assert.That(autonomy.StartAutonomousJob(.32), Is.True);

                float flowDeadline = Time.realtimeSinceStartup + 60f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < flowDeadline &&
                       !(autonomy.State == AutonomousExcavationState.Dump && unloader.enabled &&
                         unloader.AirborneVolumeM3 > 1e-5))
                    yield return new WaitForFixedUpdate();

                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Dump), autonomy.Status);
                Assert.That(unloader.enabled, Is.True, "The unloader must be active while airborne soil is emitted.");
                Assert.That(unloader.AirborneVolumeM3, Is.GreaterThan(1e-5),
                    "The reset must occur after a real soil parcel leaves the bucket.");
                autonomy.ResetAutonomousJob();

                float resetDeadline = Time.realtimeSinceStartup + 20f;
                while (autonomy.State != AutonomousExcavationState.Idle &&
                       Time.realtimeSinceStartup < resetDeadline)
                {
                    Assert.That(autonomy.CurrentMotionAxis, Is.Not.EqualTo(AutonomousMotionAxis.None),
                        "Reset must continue to report its active articulation axis.");
                    Assert.That(FindTruckPenetration(machine, receiver), Is.Null,
                        "Reset from active soil flow must still maintain truck clearance.");
                    yield return new WaitForFixedUpdate();
                }

                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Idle));
                Assert.That(unloader.enabled, Is.True,
                    "Reset during active soil flow must retain the unloader's original enabled state.");
                Assert.That(unloader.AirborneVolumeM3, Is.Zero.Within(1e-8));
                Assert.That(unloader.SurfacePendingVolumeM3, Is.Zero.Within(1e-8));
                Assert.That(cutter.VolumeM3, Is.Zero.Within(1e-8));
                Assert.That(receiver.VolumeM3, Is.Zero.Within(1e-8));
                Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(.0005));
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Disabling autonomy mid-job must release its owned unloading and keyboard states.</summary>
        /// <returns>Scene setup, an interrupted job, state checks and scene cleanup.</returns>
        [UnityTest]
        public IEnumerator DisablingAutonomyRestoresOwnedControls()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var unloader = FindInScene<BucketSoilUnloader>(scene);
                var keyboard = FindInScene<Zx200KeyboardController>(scene);
                Assert.That(unloader.enabled, Is.True);
                Assert.That(autonomy.StartAutonomousJob(.32), Is.True);
                Assert.That(unloader.enabled, Is.False);
                Assert.That(keyboard.AutomaticControlActive, Is.True);
                autonomy.enabled = false;
                yield return null;
                Assert.That(autonomy.IsRunning, Is.False);
                Assert.That(unloader.enabled, Is.True);
                Assert.That(keyboard.AutomaticControlActive, Is.False);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>Manual takeover during empty-cut clearance must release temporary cutting suppression.</summary>
        /// <returns>Scene setup, empty-cut recovery, takeover assertions and cleanup.</returns>
        [UnityTest]
        public IEnumerator ManualTakeoverDuringEmptyCutRestoresCutting()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
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
                autonomy.cutEntryDepthMeters = 100f;
                Assert.That(autonomy.StartAutonomousJob(.32), Is.True);
                float deadline = Time.realtimeSinceStartup + 45f;
                while (autonomy.IsRunning &&
                    !(autonomy.State == AutonomousExcavationState.EmptyCut && cutter.IsCuttingSuppressed) &&
                    Time.realtimeSinceStartup < deadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.EmptyCut), autonomy.Status);
                Assert.That(cutter.IsCuttingSuppressed, Is.True,
                    "The empty-cut clearance must own temporary cutting suppression.");
                autonomy.RequestManualTakeover();
                yield return null;
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.ManualTakeover));
                Assert.That(cutter.IsCuttingSuppressed, Is.False,
                    "Manual takeover must release the cutter after the recovery coroutine exits.");
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if (unload != null) yield return unload;
        }

        /// <summary>A timed-out cut must not advance to loaded lift with an uncurled bucket.</summary>
        /// <returns>Scene setup, a shortened cut, fault checks and scene cleanup.</returns>
        [UnityTest]
        public IEnumerator ShortCutTimeoutDoesNotLiftHalfOpenBucket()
        {
            Scene previous = SceneManager.GetActiveScene();
            ExpectFirstSceneLoadMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(
                ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var autonomy = FindInScene<AutonomousExcavationController>(scene);
                var machine = FindInScene<Zx200DigController>(scene);
                autonomy.cutTimeoutSeconds = 1.5f;
                Assert.That(autonomy.StartAutonomousJob(.32), Is.True);
                yield return WaitForJob(autonomy, 45f);
                Assert.That(autonomy.IsRunning, Is.False, autonomy.Status);
                Assert.That(autonomy.State, Is.EqualTo(AutonomousExcavationState.Fault),
                    "A shortened cut with material must stop rather than carry an open bucket.");
                Assert.That(autonomy.Status, Does.Contain("未完成安全收斗"));
                Assert.That(autonomy.StateHistory, Has.None.EqualTo(AutonomousExcavationState.Lift));
                Assert.That(machine.IsBusy, Is.False);
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

        private static void CaptureHandoffFrame(Camera camera, string stage)
        {
            var target = new RenderTexture(1280, 900, 24);
            var image = new Texture2D(1280, 900, TextureFormat.RGB24, false);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1280, 900), 0, 0);
                image.Apply();
                Directory.CreateDirectory("Diagnostics");
                File.WriteAllBytes("Diagnostics/manual-auto-" + stage + ".png", image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(target);
            }
        }

        private static void ExpectFirstSceneLoadMessages()
        {
            if (expectedInitialSceneWarnings) return;
            expectedInitialSceneWarnings = true;

            LogAssert.Expect(LogType.Error, "Tree prefab at index 0 is missing.");
            LogAssert.Expect(LogType.Error, "Tree prefab at index 1 is missing.");
            LogAssert.Expect(LogType.Error, "Tree prefab at index 2 is missing.");
            LogAssert.Expect(LogType.Warning,
                "Couldn't create a Convex Mesh from source mesh, within the maximum polygons limit (256). " +
                "The partial hull will be used. Consider simplifying your mesh. Source mesh name: body");

            string configuredDirectory = System.Environment.GetEnvironmentVariable("CESIUM_TILESET_DIR");
            string tilesetDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
                ? Path.GetFullPath(Path.Combine(Application.dataPath, "../../tilesets/osgb_small_005_004"))
                : Path.GetFullPath(configuredDirectory);
            string tilesetPath = Path.Combine(tilesetDirectory, "tileset.json");
            if (!File.Exists(tilesetPath))
            {
                string expected = $"Cesium local map is missing: {tilesetPath}.{CesiumTilesetMissingHint}";
                for (int i = 0; i < 3; i++) LogAssert.Expect(LogType.Warning, expected);
            }
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

        private static bool ContainsOrderedCycle(IList<AutonomousExcavationState> states)
        {
            AutonomousExcavationState[] required =
            {
                AutonomousExcavationState.AcquirePoint,
                AutonomousExcavationState.LowerAndEnter,
                AutonomousExcavationState.FillBucket,
                AutonomousExcavationState.Lift,
                AutonomousExcavationState.SwingToTruck,
                AutonomousExcavationState.Dump,
                AutonomousExcavationState.ConfirmDump,
                AutonomousExcavationState.ReturnToDig,
                AutonomousExcavationState.Completed
            };
            int next = 0;
            foreach (AutonomousExcavationState state in states)
                if (state == required[next] && ++next == required.Length)
                    return true;
            return false;
        }

        private static bool ContainsStateInOrder(
            IReadOnlyList<AutonomousExcavationState> states,
            AutonomousExcavationState firstState,
            AutonomousExcavationState secondState)
        {
            bool firstSeen = false;
            for (int i = 0; i < states.Count; i++)
            {
                AutonomousExcavationState current = states[i];
                if (!firstSeen && current == firstState) firstSeen = true;
                else if (firstSeen && current == secondState) return true;
            }
            return false;
        }

        private static string FindTruckPenetration(
            Zx200DigController machine,
            SoilReceivingZone receiver,
            bool recoveringTruckClearance = false)
        {
            Transform truckRoot = receiver.transform.parent != null
                ? receiver.transform.parent
                : receiver.transform;
            Collider[] machineColliders = machine.GetComponentsInChildren<Collider>(true);
            Collider[] truckColliders = truckRoot.GetComponentsInChildren<Collider>(true);
            foreach (Collider machineCollider in machineColliders)
            {
                if (!machineCollider.enabled || !machineCollider.gameObject.activeInHierarchy || machineCollider.isTrigger)
                    continue;
                foreach (Collider truckCollider in truckColliders)
                {
                    if (!truckCollider.enabled || !truckCollider.gameObject.activeInHierarchy || truckCollider.isTrigger)
                        continue;
                    bool penetrating = Physics.ComputePenetration(
                        machineCollider,
                        machineCollider.transform.position,
                        machineCollider.transform.rotation,
                        truckCollider,
                        truckCollider.transform.position,
                        truckCollider.transform.rotation,
                        out Vector3 direction,
                        out float distance);
                    if (penetrating)
                    {
                        float allowedClearanceRecoveryOverlap = recoveringTruckClearance ? .03f : .01f;
                        if (distance <= allowedClearanceRecoveryOverlap) continue;
                        Vector3 bucketInBed = receiver.transform.InverseTransformPoint(
                            machineCollider.bounds.center);
                        BucketSoilCutter cutter = machine.GetComponentInChildren<BucketSoilCutter>(true);
                        Zx200JointPose pose = machine.ReadActualPose();
                        Vector3 tipInBed = cutter != null
                            ? receiver.transform.InverseTransformPoint(cutter.TipWorldPosition)
                            : Vector3.zero;
                        return $"{machineCollider.name} 穿入 {truckCollider.name}，" +
                            $"碰撞体中心车厢坐标={bucketInBed}，斗尖坐标={tipInBed}，" +
                            $"实测姿态=({pose.swing:F1},{pose.boom:F1},{pose.arm:F1},{pose.bucket:F1})，" +
                            $"分离方向={direction}，穿入深度={distance:F3}m";
                    }
                }
            }
            return null;
        }
    }
}
