using System.Collections;
using System.Collections.Generic;
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

        /// <summary>Run multiple automatic dig, load and return cycles using actual landing volume.</summary>
        /// <returns>Scene load, Cesium readiness, repeated autonomous cycles and cleanup frames.</returns>
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
                var machine = FindInScene<Zx200DigController>(scene);
                var cutter = FindInScene<BucketSoilCutter>(scene);
                var receiver = FindInScene<SoilReceivingZone>(scene);
                Assert.That(autonomy, Is.Not.Null);
                Assert.That(machine, Is.Not.Null);
                Assert.That(cutter, Is.Not.Null);
                Assert.That(receiver, Is.Not.Null);
                Assert.That(receiver.size, Is.EqualTo(new Vector2(3.6f, 4.5f)),
                    "The serialized truck bed must match the verified loading-bed geometry.");

                double receiverBefore = receiver.VolumeM3;
                double groundPileBefore = ManualSoilTestMotion.GroundPileVolumeFor(cutter);
                Zx200KeyboardController keyboard = machine.GetComponent<Zx200KeyboardController>();
                ArticulationBody trackBase = keyboard.articulationRoot;
                Assert.That(keyboard.fixedBase, Is.True,
                    "The open-pit worksite is a parked excavator demo; only its upper structure should slew.");
                Assert.That(trackBase.immovable, Is.True,
                    "The track articulation root must be physically immovable during stationary bench work.");
                Vector3 trackBaseAtSpawn = trackBase.transform.position;
                for (int i = 0; i < 100; i++) yield return new WaitForFixedUpdate();
                Vector3 trackBaseAtStart = trackBase.transform.position;
                Debug.Log($"TRACK BASE IDLE spawn={trackBaseAtSpawn}, settled={trackBaseAtStart}, " +
                    $"delta={trackBaseAtStart - trackBaseAtSpawn}, velocity={trackBase.velocity}");
                double maximumObservedBucketLoad = cutter.VolumeM3;
                float maximumTrackBaseDrift = 0f;
                Vector3 trackBaseAtMaximumDrift = trackBaseAtStart;
                AutonomousExcavationState stateAtMaximumTrackBaseDrift = autonomy.State;
                Assert.That(autonomy.StartAutonomousJob(.32), Is.True);
                var observedStates = new List<AutonomousExcavationState>();
                float deadline = Time.realtimeSinceStartup + 150;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    maximumObservedBucketLoad = System.Math.Max(maximumObservedBucketLoad, cutter.VolumeM3);
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
                    $"velocity={trackBase.velocity}");
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
                Assert.That(autonomy.CompletedCycles, Is.GreaterThanOrEqualTo(2));
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
                float deadline = Time.realtimeSinceStartup + 60f;
                while (autonomy.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    Zx200JointPose currentCutPose = default;
                    bool readingCoordinatedCut = autonomy.State == AutonomousExcavationState.FillBucket &&
                        autonomy.machine.IsBusy;
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

        /// <summary>Verify the production motion monitor stops automatically when a truck collider is obstructed.</summary>
        /// <returns>Scene loading, collision injection during truck approach, fault handling and cleanup frames.</returns>
        [UnityTest]
        public IEnumerator StopsAutomaticMotionWhenMachineOverlapsTruckCollider()
        {
            Scene previous = SceneManager.GetActiveScene();
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
