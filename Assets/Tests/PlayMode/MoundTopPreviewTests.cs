using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public sealed class MoundTopPreviewTests
    {
        private static bool loadedLegacyTerrain;

        /// <summary>Verify a relatively open manual mouth can enter and collect using coordinated hand controls.</summary>
        /// <returns>Real manual motion through the saved mound.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator SavedPreviewManualOpenAngleCollects() => ManualAngledScoop(25);

        /// <summary>Verify a more curled manual mouth can also collect without teleporting or injected soil.</summary>
        /// <returns>Real manual motion through the saved mound.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator SavedPreviewManualCurledAngleCollects() => ManualAngledScoop(45);

        /// <summary>A closed starting posture can open into a real cut instead of staying locked in shell contact.</summary>
        /// <returns>Real manual motion, volume checks and withdrawal.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator SavedPreviewManualClosedAngleOpensAndCollects() => ManualAngledScoop(65);

        private static IEnumerator ManualAngledScoop(float bucketAngle)
        {
            const string path = "Assets/Scenes/sence_test.unity";
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var automatic = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<AutonomousExcavationController>(true)).Single();
                var machine = automatic.machine;
                var cutter = automatic.cutter;
                var resistance = cutter.GetComponent<BucketSoilResistance>();
                float maximumShellDepth = 0;
                Camera camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                    .First(candidate => candidate.CompareTag("MainCamera"));
                for (int i = 0; i < 300; i++)
                {
                    float difference = Mathf.DeltaAngle(machine.ReadActualPose().bucket, bucketAngle);
                    if (Mathf.Abs(difference) < 1) break;
                    machine.AdjustManualPose(0, 0, 0, Mathf.Clamp(difference, -.25f, .25f));
                    yield return new WaitForFixedUpdate();
                }
                for (int i = 0; i < 300; i++)
                {
                    Vector3 tip = cutter.TipWorldPosition;
                    float surface = cutter.soil.Terrain.SampleHeight(tip) + cutter.soil.transform.position.y;
                    if (tip.y <= surface - .08f) break;
                    machine.AdjustManualPose(0, 6 * Time.fixedDeltaTime, 0, 0);
                    yield return new WaitForFixedUpdate();
                    maximumShellDepth = Mathf.Max(maximumShellDepth, resistance.ShellPenetrationM);
                }
                BladeMainSceneTests.CaptureClose(cutter, camera, false, $"manual-angle-{bucketAngle}-entry");
                for (int i = 0; i < 250 && cutter.VolumeM3 < .08; i++)
                {
                    // A near-closed mouth needs opening, not another 20 degrees of
                    // closure. These are explicit manual inputs, not automatic assistance.
                    float bucketSpeed = bucketAngle >= 60 ? -4 : 4;
                    machine.AdjustManualPose(0, 0, 6 * Time.fixedDeltaTime, bucketSpeed * Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                    maximumShellDepth = Mathf.Max(maximumShellDepth, resistance.ShellPenetrationM);
                    if (bucketAngle == 65 && i % 50 == 0)
                    {
                        var pose = machine.ReadActualPose();
                        Debug.Log($"steep-step={i} arm={pose.arm:F3}/{machine.armJoint.xDrive.target:F3} bucket={pose.bucket:F3}/{machine.bucketJoint.xDrive.target:F3} tipDepth={resistance.PenetrationM:F4} shell={resistance.ShellPenetrationM:F4} reason={resistance.BlockReason}");
                    }
                }
                Debug.Log($"多角度手挖 {bucketAngle}°：切除={cutter.ExcavatedVolumeM3:F5}，进斗={cutter.VolumeM3:F5}，斗壳最大穿入={maximumShellDepth:F4}m，{cutter.Status}，{resistance.BlockReason}");
                BladeMainSceneTests.CaptureClose(cutter, camera, false, $"manual-angle-{bucketAngle}-curl");
                Assert.That(cutter.ExcavatedVolumeM3, Is.GreaterThan(.01), cutter.Status + " / " + resistance.BlockReason);
                Assert.That(cutter.VolumeM3, Is.GreaterThan(.01), "Changing the manual bucket angle must still allow actual intake.");
                Assert.That(maximumShellDepth, Is.LessThan(.15f), "Do not pay for intake by burying the shell.");
                float beforeWithdrawal = cutter.TipWorldPosition.y;
                for (int i = 0; i < 150; i++)
                {
                    machine.AdjustManualPose(0, -10 * Time.fixedDeltaTime, 0, 0);
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(cutter.TipWorldPosition.y, Is.GreaterThan(beforeWithdrawal + .3f));
                ManualSoilTestMotion.AssertBalanceIncludingPiles(cutter, 0, 2e-5);
            }
            finally { if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene); }
        }

        /// <summary>Exercise manual keys through contact, curl and withdrawal in the unchanged preview scene.</summary>
        /// <returns>Real joint steps, soil accounting, screenshots and cleanup.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator SavedPreviewManualCutCurlAndWithdraw()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var automatic = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<AutonomousExcavationController>(true)).Single();
                var machine = automatic.machine;
                var cutter = automatic.cutter;
                var resistance = cutter.GetComponent<BucketSoilResistance>();
                machine.AdjustManualPose(0, 0, .1f, 0);
                float maximumShellDepth = 0;
                yield return ManualSoilTestMotion.Scoop(machine, cutter, .04,
                    () => maximumShellDepth = Mathf.Max(maximumShellDepth, resistance.ShellPenetrationM));
                // The legacy helper closes to about 84 degrees before pressing down. A real
                // shell can contact first at this posture. Back out and reopen using only keys.
                if (cutter.VolumeM3 < .01)
                {
                    Assert.That(maximumShellDepth, Is.LessThan(.15f), "Closed-bucket pushing must stop, not tunnel.");
                    for (int i = 0; i < 120; i++)
                    {
                        machine.AdjustManualPose(0, -10 * Time.fixedDeltaTime, 0, 0);
                        yield return new WaitForFixedUpdate();
                    }
                    for (int i = 0; i < 300; i++)
                    {
                        float difference = Mathf.DeltaAngle(machine.ReadActualPose().bucket, 25);
                        if (Mathf.Abs(difference) < 1) break;
                        machine.AdjustManualPose(0, 0, 0, Mathf.Clamp(difference, -.25f, .25f));
                        yield return new WaitForFixedUpdate();
                    }
                    for (int i = 0; i < 300; i++)
                    {
                        Vector3 tip = cutter.TipWorldPosition;
                        float surface = cutter.soil.Terrain.SampleHeight(tip) + cutter.soil.transform.position.y;
                        if (tip.y <= surface - .08f) break;
                        machine.AdjustManualPose(0, 6 * Time.fixedDeltaTime, 0, 0);
                        yield return new WaitForFixedUpdate();
                        maximumShellDepth = Mathf.Max(maximumShellDepth, resistance.ShellPenetrationM);
                    }
                    for (int i = 0; i < 250 && cutter.VolumeM3 < .04; i++)
                    {
                        machine.AdjustManualPose(0, 0, 6 * Time.fixedDeltaTime, 4 * Time.fixedDeltaTime);
                        yield return new WaitForFixedUpdate();
                        maximumShellDepth = Mathf.Max(maximumShellDepth, resistance.ShellPenetrationM);
                    }
                }
                Debug.Log($"手动切削：切除={cutter.ExcavatedVolumeM3:F5}，进斗={cutter.VolumeM3:F5}，{cutter.Status}，{resistance.BlockReason}");
                Camera camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                    .First(candidate => candidate.CompareTag("MainCamera"));
                BladeMainSceneTests.CaptureClose(cutter, camera, false, "manual-improved-entry");
                Assert.That(cutter.ExcavatedVolumeM3, Is.GreaterThan(.01), cutter.Status + " / " + resistance.BlockReason);
                Assert.That(cutter.VolumeM3, Is.GreaterThan(.01), "Real manual input must collect soil, not just displace it.");
                for (int i = 0; i < 100; i++)
                {
                    machine.AdjustManualPose(0, 0, 5 * Time.fixedDeltaTime, 8 * Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                    maximumShellDepth = Mathf.Max(maximumShellDepth, resistance.ShellPenetrationM);
                }
                BladeMainSceneTests.CaptureClose(cutter, camera, false, "manual-improved-curl");
                Debug.Log($"手动斗壳最大穿入={maximumShellDepth:F4}m，当前={resistance.ShellPenetrationM:F4}m，{resistance.BlockReason}");
                Assert.That(maximumShellDepth, Is.LessThan(.15f), "The physical shell must not ratchet into uncut ground.");
                float entryY = cutter.TipWorldPosition.y;
                for (int i = 0; i < 150; i++)
                {
                    machine.AdjustManualPose(0, -10 * Time.fixedDeltaTime, 0, 0);
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(cutter.TipWorldPosition.y, Is.GreaterThan(entryY + .3f), resistance.BlockReason);
                ManualSoilTestMotion.AssertBalanceIncludingPiles(cutter, 0, 2e-5);
                Debug.Log($"手动退出完成：上升={cutter.TipWorldPosition.y-entryY:F3}m，切除={cutter.ExcavatedVolumeM3:F5}，进斗={cutter.VolumeM3:F5}");
            }
            finally { if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene); }
        }

        /// <summary>Entry soil must remain a valid load even when the later scoop cannot remove more soil.</summary>
        /// <returns>Real entry, interrupted cutting, transfer and cleanup.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator SavedPreviewTransfersEntryLoadWhenScoopAddsNoSoil()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var automatic = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<AutonomousExcavationController>(true)).Single();
                automatic.cutTimeoutSeconds = .2f;
                double received = automatic.receiver.VolumeM3;
                Assert.That(automatic.StartAutonomousJob(.01), Is.True, automatic.Status);
                float deadline = Time.realtimeSinceStartup + 100f;
                while (automatic.IsRunning && automatic.State != AutonomousExcavationState.FillBucket &&
                    Time.realtimeSinceStartup < deadline) yield return new WaitForFixedUpdate();
                Assert.That(automatic.State, Is.EqualTo(AutonomousExcavationState.FillBucket), automatic.FaultReason);
                Assert.That(automatic.cutter.VolumeM3, Is.GreaterThan(.01));
                automatic.cutter.SetCuttingSuppressed(true);
                while (automatic.IsRunning && Time.realtimeSinceStartup < deadline)
                    yield return new WaitForFixedUpdate();
                Assert.That(automatic.State, Is.EqualTo(AutonomousExcavationState.Completed), automatic.FaultReason);
                Assert.That(automatic.receiver.VolumeM3 - received, Is.GreaterThanOrEqualTo(.01));
            }
            finally { if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene); }
        }

        /// <summary>Entry must open a real cut below the original surface before the scoop phase.</summary>
        /// <returns>Saved scene entry, geometry assertions and scene cleanup.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator SavedPreviewCutsIntoSoilBeforeScooping()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            TerrainData original = null;
            try
            {
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var automatic = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<AutonomousExcavationController>(true)).Single();
                Terrain terrain = automatic.cutter.soil.Terrain;
                original = Object.Instantiate(terrain.terrainData);
                Assert.That(automatic.StartAutonomousJob(.1), Is.True, automatic.Status);
                float deadline = Time.realtimeSinceStartup + 100f;
                while (automatic.IsRunning && automatic.State != AutonomousExcavationState.FillBucket &&
                    Time.realtimeSinceStartup < deadline) yield return new WaitForFixedUpdate();
                Assert.That(automatic.State, Is.EqualTo(AutonomousExcavationState.FillBucket), automatic.FaultReason);
                Vector3 tip = automatic.cutter.TipWorldPosition;
                Vector3 local = tip - terrain.transform.position;
                float originalSurface = terrain.transform.position.y + original.GetInterpolatedHeight(
                    local.x / original.size.x, local.z / original.size.z);
                float depth = originalSurface - tip.y;
                Debug.Log($"实际切入验收：原面切深={depth:F3}m，入口切除={automatic.cutter.ExcavatedVolumeM3:F5}m³，斗内={automatic.cutter.VolumeM3:F5}m³");
                Camera camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                    .First(candidate => candidate.CompareTag("MainCamera"));
                Capture(camera, tip + automatic.machine.transform.right * 5f + Vector3.up * 1f,
                    tip + Vector3.up * .5f, "Diagnostics/cut-entry-side.png");
                Assert.That(depth, Is.GreaterThanOrEqualTo(.24f), "厚土层应让斗口切入，而不只轻触表面。");
                Assert.That(automatic.cutter.ExcavatedVolumeM3, Is.GreaterThan(.001), "下压切入必须已经开槽，不能先穿入再补挖。");
                Assert.That(tip.y, Is.GreaterThan(automatic.cutter.soil.GetFloorWorldY(tip) + .03f));
                for (int i = 0; i < 75 && automatic.State == AutonomousExcavationState.FillBucket; i++)
                    yield return new WaitForFixedUpdate();
                tip = automatic.cutter.TipWorldPosition;
                Capture(camera, tip + automatic.machine.transform.right * 5f + Vector3.up,
                    tip + Vector3.up * .5f, "Diagnostics/cut-scoop-side.png");
            }
            finally
            {
                if (original != null) Object.Destroy(original);
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
        }

        private static void ExpectLegacyTerrainMessages()
        {
            // Unity validates the cached legacy Terrain tree prototypes only on its first load.
            if (loadedLegacyTerrain) return;
            loadedLegacyTerrain = true;
            for (int i = 0; i < 3; i++) LogAssert.Expect(LogType.Error, $"Tree prefab at index {i} is missing.");
        }

        /// <summary>Measure one real loading cycle by simulation time, independent of map startup and batch rendering speed.</summary>
        /// <returns>Saved scene preparation, physical cycle timing and cleanup.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator SavedPreviewMeasuresOneLoadingCycle()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var automatic = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<AutonomousExcavationController>(true)).Single();
                double before = automatic.receiver.VolumeM3;
                automatic.targetVolumeM3 = .1f;
                var command = typeof(AutonomousExcavationController).GetMethod("ReceiveAutonomousCommand");
                Assert.That(command, Is.Not.Null, "The WebGL button must use the same autonomous entry as P.");
                command.Invoke(automatic, new object[] { "start" });
                Assert.That(automatic.IsRunning, Is.True, automatic.FaultReason);
                var state = automatic.State;
                float started = Time.time, cycleStarted = Time.time;
                float deadline = Time.realtimeSinceStartup + 120f;
                while (automatic.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    if (state != automatic.State)
                    {
                        Debug.Log($"单斗阶段计时：{state}={Time.time - started:F2}模拟秒");
                        state = automatic.State;
                        started = Time.time;
                    }
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(automatic.State, Is.EqualTo(AutonomousExcavationState.Completed), automatic.FaultReason);
                Assert.That(automatic.receiver.VolumeM3 - before, Is.GreaterThanOrEqualTo(.1));
                Debug.Log($"单斗总计时：{Time.time - cycleStarted:F2}模拟秒，实际装入={automatic.receiver.VolumeM3 - before:F5}m³");
            }
            finally { if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene); }
        }

        /// <summary>Verify live AZ boom input drives the saved machine and expires without further manual writes.</summary>
        /// <returns>Saved scene initialization, held-input replay and timeout verification.</returns>
        [UnityTest]
        public IEnumerator SavedAzInputMovesAndStopsOnTimeout()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var keyboard = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<Zx200KeyboardController>(true)).Single();
                var machine = keyboard.digController;
                float deadline = Time.time + 5f;
                while (machine.IsBusy && Time.time < deadline) yield return null;
                Assert.That(machine.IsBusy, Is.False);
                uint before = machine.ManualPoseRevision;
                const string held = "{\"action1\":128,\"action2\":128,\"action3\":128,\"action4\":128,\"action5\":128,\"action6\":0}";
                for (int i = 0; i < 20; i++)
                {
                    keyboard.ReceiveControlTopic(held);
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(machine.ManualPoseRevision, Is.GreaterThan(before), "Held AZ input must drive the actual manual controller.");
                Assert.That(keyboard.ManualInputActive, Is.True);
                yield return new WaitForSeconds(.65f);
                uint stopped = machine.ManualPoseRevision;
                Assert.That(keyboard.HasActiveControlTopic(), Is.False);
                Assert.That(keyboard.ManualInputActive, Is.False);
                yield return new WaitForSeconds(.15f);
                Assert.That(machine.ManualPoseRevision, Is.EqualTo(stopped), "Expired input must not continue writing joint targets.");
            }
            finally { if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene); }
        }

        /// <summary>Drive the saved preview chassis downhill and back using real articulation forces.</summary>
        /// <returns>Scene preparation, travel posture, manual descent and ascent, then teardown.</returns>
        [UnityTest]
        public IEnumerator SavedMoundAllowsDownhillAndReturn()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var dig = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<Zx200DigController>(true)).Single();
                var keyboard = dig.GetComponent<Zx200KeyboardController>();
                Assert.That(dig.StartAutomaticPose(dig.carryPose), Is.True);
                for (int i = 0; i < 360 && dig.IsBusy; i++) yield return new WaitForFixedUpdate();
                Assert.That(dig.LastAutomaticMotionSucceeded, Is.True);
                keyboard.fixedBase = false;
                keyboard.enabled = false;
                keyboard.GetComponent<Zx200FixedBasePhysics>().Configure();
                for (int i = 0; i < 100; i++) yield return new WaitForFixedUpdate();
                Vector3 start = keyboard.articulationRoot.transform.position;
                var drive = keyboard.GetComponent<Zx200TrackDrive>();
                var physics = keyboard.GetComponent<Zx200FixedBasePhysics>();
                Debug.Log($"实际底盘配置：惯量={keyboard.articulationRoot.inertiaTensor}，重心={keyboard.articulationRoot.worldCenterOfMass}，上车重心={physics.swingBody.worldCenterOfMass}，起始角度={keyboard.articulationRoot.transform.eulerAngles}");
                float heading = keyboard.articulationRoot.transform.eulerAngles.y;
                for (int i = 0; i < 200; i++)
                {
                    keyboard.MoveArticulationRoot(-1f, 0f, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                    if (i % 50 == 49) Debug.Log($"DOWN {i}: position={keyboard.articulationRoot.transform.position}, angle={keyboard.articulationRoot.transform.eulerAngles}, angularVelocity={keyboard.articulationRoot.angularVelocity}, contacts={drive.GroundContactCount}");
                }
                // Capture the lower position only after the gradual brake has actually stopped the descent.
                for (int i = 0; i < 100; i++)
                {
                    keyboard.MoveArticulationRoot(0f, 0f, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                }
                Vector3 lower = keyboard.articulationRoot.transform.position;
                for (int i = 0; i < 350; i++)
                {
                    keyboard.MoveArticulationRoot(1f, 0f, Time.fixedDeltaTime);
                    yield return new WaitForFixedUpdate();
                    if (i % 50 == 49)
                    {
                        int[] contacts = (int[])typeof(Zx200TrackDrive).GetField("trackContactCounts",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(drive);
                        Debug.Log($"UP {i}: position={keyboard.articulationRoot.transform.position}, angle={keyboard.articulationRoot.transform.eulerAngles}, angularVelocity={keyboard.articulationRoot.angularVelocity}, contacts={drive.GroundContactCount}, sides={string.Join(",", contacts ?? new int[0])}");
                    }
                }
                Vector3 finish = keyboard.articulationRoot.transform.position;
                Debug.Log($"实际土堆往返：start={start} lower={lower} finish={finish}");
                Assert.That(Vector3.Distance(start, lower), Is.GreaterThan(1f));
                Assert.That(lower.y, Is.LessThan(start.y - .1f), "应真正下坡而非仅在顶面移动");
                Assert.That(finish.y, Is.GreaterThan(lower.y + .1f), "实际土堆上应能重新爬升");
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(heading, keyboard.articulationRoot.transform.eulerAngles.y)),
                    Is.LessThan(10f), "直行往返不应因两侧接地点不对称而自行大幅转向");
                Assert.That(Vector3.Dot(keyboard.articulationRoot.transform.up, Vector3.up), Is.GreaterThan(.8f));
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
        }

        /// <summary>Verify that a real hand-dug bucket can reach the closer parked bed without direct soil transfer.</summary>
        /// <returns>Saved scene startup, manual cutting, loaded automatic handoff and unloading.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator CloserTruckReceivesHandDugBucket()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                var site = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<CesiumMoundSite>(true)).Single();
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var automatic = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<AutonomousExcavationController>(true)).Single();
                var dig = automatic.machine;
                var cutter = automatic.cutter;
                var wheels = site.truck.GetComponentsInChildren<Collider>().Where(candidate =>
                    candidate.name.ToLowerInvariant().Contains("wheel") && candidate.name.Contains("collision")).ToArray();
                Assert.That(wheels.Length, Is.GreaterThanOrEqualTo(4), "轮胎碰撞体必须保留。");
                foreach (Collider wheel in wheels)
                {
                    var box = wheel as BoxCollider;
                    Assert.That(box, Is.Not.Null, "当前矿卡采用实体轮胎盒碰撞体。");
                    float closestGap = float.PositiveInfinity;
                    for (int x = -1; x <= 1; x++)
                        for (int z = -1; z <= 1; z++)
                        {
                            Vector3 bottom = box.transform.TransformPoint(box.center +
                                new Vector3(x * box.size.x * .5f, -box.size.y * .5f, z * box.size.z * .5f));
                            float surface = site.soil.Terrain.SampleHeight(bottom) + site.soil.transform.position.y;
                            closestGap = Mathf.Min(closestGap, bottom.y - surface);
                        }
                    Debug.Log($"真实轮胎接地：{wheel.name}，最小间隙={closestGap:F4}m");
                    Assert.That(closestGap, Is.InRange(-.02f, .15f), "轮胎实体不得穿入地面或明显悬空：" + wheel.name);
                }
                foreach (BoxCollider box in site.truck.GetComponentsInChildren<BoxCollider>())
                {
                    if (!box.enabled) continue;
                    if (Physics.ComputePenetration(box, box.transform.position, box.transform.rotation,
                        site.soil.Terrain.GetComponent<TerrainCollider>(), site.soil.transform.position,
                        site.soil.transform.rotation, out _, out float depth))
                        Assert.That(depth, Is.LessThanOrEqualTo(.02f), "矿卡底盘或车体不应穿入地形：" + box.name);
                }
                double before = automatic.receiver.VolumeM3;
                // A valid manual joint input releases the compact startup hold without changing saved poses.
                dig.AdjustManualPose(0f, 0f, .1f, 0f);
                yield return ManualSoilTestMotion.Scoop(dig, cutter, .03);
                Debug.Log($"近距装车手挖检查：斗量={cutter.VolumeM3:F5}，切削状态={cutter.Status}，斗尖={cutter.TipWorldPosition}");
                Assert.That(cutter.VolumeM3, Is.GreaterThan(.005), "必须先实际挖到土，不能伪造斗内土量。");
                Assert.That(automatic.StartAutonomousJob(.005), Is.True);
                float deadline = Time.realtimeSinceStartup + 90f;
                bool capturedDump = false;
                Camera camera = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null
                    ? null : scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                        .First(candidate => candidate.CompareTag("MainCamera"));
                while (automatic.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    if (camera != null && !capturedDump && automatic.State == AutonomousExcavationState.Dump &&
                        cutter.VolumeM3 < .015)
                    {
                        Capture(camera, site.bakedSite.machinePosition + new Vector3(-9f, 7f, -10f),
                            (site.bakedSite.machinePosition + site.truck.position) * .5f + Vector3.up,
                            "Diagnostics/near-truck-dump.png");
                        capturedDump = true;
                    }
                    yield return null;
                }
                Assert.That(automatic.receiver.VolumeM3 - before, Is.GreaterThan(.005), automatic.Status);
                Assert.That(automatic.State, Is.EqualTo(AutonomousExcavationState.Completed), automatic.FaultReason);
                Debug.Log($"近距装车完成：车厢增量={automatic.receiver.VolumeM3 - before:F5}m³，完成斗数={automatic.CompletedCycles}");
                if (camera != null)
                    Capture(camera, site.bakedSite.machinePosition + new Vector3(-9f, 7f, -10f),
                        (site.bakedSite.machinePosition + site.truck.position) * .5f + Vector3.up,
                        "Diagnostics/near-truck-completed.png");
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
        }

        /// <summary>Run the entire one-cubic-metre job so a successful first few buckets cannot conceal a later stall.</summary>
        /// <returns>Saved scene startup, repeated physical cutting and receipt checks, then cleanup.</returns>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator SavedPreviewCompletesContinuousLoadingGoal()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var automatic = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<AutonomousExcavationController>(true)).Single();
                double initial = automatic.receiver.VolumeM3;
                Assert.That(automatic.StartAutonomousJob(1.0), Is.True, automatic.Status);
                float deadline = Time.realtimeSinceStartup + 480f;
                int cycles = 0;
                double receipt = initial;
                double peakBucketLoad = 0;
                double lastSideSpill = automatic.cutter.DisplacedVolumeM3;
                var fill = automatic.cutter.fillVisual.GetComponent<BucketSoilFill>();
                Vector3 previousSurface = fill.SurfaceNormalLocal;
                double previousLoad = 0;
                float maximumSurfaceStep = 0;
                AutonomousExcavationState measuredState = automatic.State;
                float stateStarted = Time.time;
                while (automatic.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    peakBucketLoad = System.Math.Max(peakBucketLoad, automatic.cutter.VolumeM3);
                    if (previousLoad > .001 && automatic.cutter.VolumeM3 > .001)
                        maximumSurfaceStep = Mathf.Max(maximumSurfaceStep,
                            Vector3.Angle(previousSurface, fill.SurfaceNormalLocal));
                    previousLoad = automatic.cutter.VolumeM3;
                    previousSurface = fill.SurfaceNormalLocal;
                    if (automatic.State != measuredState)
                    {
                        Debug.Log($"阶段耗时：斗数={cycles}，阶段={measuredState}，模拟秒={Time.time - stateStarted:F2}");
                        measuredState = automatic.State;
                        stateStarted = Time.time;
                    }
                    if (automatic.CompletedCycles > cycles)
                    {
                        Assert.That(automatic.DumpDeploymentAttempts, Is.LessThanOrEqualTo(2),
                            "每斗只允许一次联合定位及一次测量修正，不能重复试探卸料。");
                        Assert.That(automatic.receiver.VolumeM3, Is.GreaterThan(receipt + .005), "每斗都必须实际装入车厢。");
                        cycles = automatic.CompletedCycles;
                        receipt = automatic.receiver.VolumeM3;
                        Debug.Log($"完整持续装车：斗数={cycles}，车厢增量={receipt - initial:F5}m³");
                        Debug.Log($"单斗填充与侧落：峰值={peakBucketLoad:F5}/{automatic.cutter.EffectiveCapacityM3:F5}m³，" +
                            $"侧落增量={automatic.cutter.DisplacedVolumeM3 - lastSideSpill:F5}m³");
                        peakBucketLoad = 0;
                        lastSideSpill = automatic.cutter.DisplacedVolumeM3;
                    }
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(automatic.State, Is.EqualTo(AutonomousExcavationState.Completed), automatic.FaultReason + " / " + automatic.Status);
                Assert.That(automatic.CompletedCycles, Is.GreaterThanOrEqualTo(3));
                Assert.That(maximumSurfaceStep, Is.LessThan(15f), "装土、回转和启用卸料不能突然重置土面。");
                Debug.Log($"连续土面验收：最大单物理步法线变化={maximumSurfaceStep:F3}°");
                Assert.That(automatic.receiver.VolumeM3 - initial, Is.GreaterThanOrEqualTo(1.0), "必须装到目标量而非提前停止。");
                Debug.Log($"完整持续装车通过：斗数={automatic.CompletedCycles}，车厢增量={automatic.receiver.VolumeM3 - initial:F5}m³");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    Camera camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                        .First(candidate => candidate.CompareTag("MainCamera"));
                    var site = automatic.GetComponent<CesiumMoundSite>() ?? scene.GetRootGameObjects()
                        .SelectMany(root => root.GetComponentsInChildren<CesiumMoundSite>(true)).Single();
                    Vector3 machine = site.bakedSite.machinePosition;
                    Capture(camera, machine + new Vector3(-9f, 3f, -10f),
                        (machine + site.truck.position) * .5f,
                        "Diagnostics/continuous-loading-ground.png");
                }
            }
            finally { if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene); }
        }

        /// <summary>Require three actual excavation receipts from the saved preview, without injected soil.</summary>
        /// <returns>Scene startup, physical multi-bucket job and cleanup.</returns>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator SavedPreviewCompletesThreeAutomaticLoads()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            try
            {
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                var automatic = scene.GetRootGameObjects().SelectMany(root =>
                    root.GetComponentsInChildren<AutonomousExcavationController>(true)).Single();
                double initial = automatic.receiver.VolumeM3;
                Assert.That(automatic.StartAutonomousJob(1.0), Is.True, automatic.Status);
                float deadline = Time.realtimeSinceStartup + 480f;
                int observedCycles = 0;
                double lastReceipt = initial;
                AutonomousExcavationState previousStage = automatic.State;
                float stageStarted = Time.time;
                while (automatic.IsRunning && automatic.CompletedCycles < 3 && Time.realtimeSinceStartup < deadline)
                {
                    if (automatic.State != previousStage)
                    {
                        float elapsed = Time.time - stageStarted;
                        Debug.Log($"连续搬运阶段：{previousStage}，耗时={elapsed:F2}s");
                        if (previousStage == AutonomousExcavationState.Lift)
                            Assert.That(elapsed, Is.LessThan(8f), "携土抬升不能在固定姿态处悬停等待。");
                        if (automatic.State == AutonomousExcavationState.Dump)
                            Assert.That(automatic.DumpDeploymentAttempts, Is.LessThanOrEqualTo(1),
                                "回转已规划卸料落点，到车旁最多允许一次实际偏差修正。");
                        previousStage = automatic.State;
                        stageStarted = Time.time;
                    }
                    if (automatic.CompletedCycles > observedCycles)
                    {
                        Assert.That(automatic.receiver.VolumeM3, Is.GreaterThan(lastReceipt + .005),
                            "每斗必须实际进入车厢。");
                        observedCycles = automatic.CompletedCycles;
                        lastReceipt = automatic.receiver.VolumeM3;
                        Debug.Log($"连续装车验收：完成斗数={observedCycles}，车厢={lastReceipt:F5}，斗内={automatic.cutter.VolumeM3:F5}");
                    }
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(automatic.CompletedCycles, Is.GreaterThanOrEqualTo(3), automatic.FaultReason + " / " + automatic.Status);
                Assert.That(automatic.receiver.VolumeM3, Is.GreaterThan(initial + .015));
                if (observedCycles < 3)
                    Assert.That(automatic.receiver.VolumeM3, Is.GreaterThan(lastReceipt + .005),
                        "第三斗也必须有实际车厢增量。");
                Debug.Log($"三斗最终验收：斗数={automatic.CompletedCycles}，车厢增量={automatic.receiver.VolumeM3 - initial:F5}m³");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    Camera camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                        .First(candidate => candidate.CompareTag("MainCamera"));
                    Vector3 machinePosition = automatic.machine.transform.position;
                    Capture(camera, machinePosition + new Vector3(-9f, 7f, -10f),
                        (machinePosition + automatic.receiver.transform.position) * .5f + Vector3.up,
                        "Diagnostics/three-loads-completed.png");
                }
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
        }

        /// <summary>Verify real baked startup and manual Terrain cutting, then render the saved preview layout.</summary>
        /// <returns>Scene loading, support readiness, cutting assertions, preview capture and scene teardown.</returns>
        [UnityTest]
        public IEnumerator MoundSupportsTracksAndFrontSoilCanBeCut()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            // The unchanged legacy Terrain elsewhere in sence_test contains three missing tree references.
            ExpectLegacyTerrainMessages();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            CesiumMoundSite site = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<CesiumMoundSite>(true)).Single();
            try
            {
                Assert.That(site.bakedSite.supportsTracksOnTerrain, Is.True);
                Assert.That(site.bakedSite.replacesCesiumSurface, Is.True);
                float deadline = Time.realtimeSinceStartup + 12f;
                while (!site.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(site.IsReady, Is.True, site.Status);
                Assert.That(site.machine.transform.position.x, Is.EqualTo(site.bakedSite.machinePosition.x).Within(.2f));
                Assert.That(site.soil.HasSurfaceAt(site.bakedSite.machinePosition), Is.True);
                TerrainData data = site.soil.Terrain.terrainData;
                Assert.That(data, Is.Not.SameAs(site.bakedSite.terrainData));
                Vector3 front = site.bakedSite.machinePosition + site.bakedSite.machineRotation * new Vector3(0f, 0f, 5f);
                float surface = site.soil.Terrain.SampleHeight(front) + site.soil.transform.position.y;
                front.y = surface - .15f;
                double removed = site.soil.CutSweep(front, front + Vector3.right * .3f, .45f, .3f);
                Assert.That(removed, Is.GreaterThan(.01), "前方斜坡必须有真实可切土量，而不只是影像");
                Assert.That(site.soil.Terrain.SampleHeight(front) + site.soil.transform.position.y,
                    Is.LessThan(surface));
                site.soil.ResetSoil();
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    // Streaming uses real time; a fast batch editor can render 240 frames before tiles settle.
                    yield return new WaitForSecondsRealtime(10f);
                    for (int frame = 0; frame < 240; frame++) yield return null;
                    Assert.That(site.MapSurfacePrepared, Is.True, "施工区校验通过后才能裁剪原始扫描网格。");
                    var clip = site.tileset.GetComponent<ConstructionSiteMapClip>();
                    Assert.That(clip != null && clip.isActiveAndEnabled, Is.True);
                    Assert.That(site.tileset.GetComponentsInChildren<MeshFilter>(true).Any(filter =>
                        filter.sharedMesh != null && filter.sharedMesh.name.Contains("work area clipped")), Is.True,
                        "必须实际替换已加载的 Cesium 网格，不能仅检查组件开关。");
                    Camera camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true))
                        .First(candidate => candidate.CompareTag("MainCamera"));
                    Capture(camera, site.bakedSite.machinePosition + new Vector3(-18f, 16f, -19f),
                        site.bakedSite.machinePosition + Vector3.forward * 2f, "Diagnostics/mound-top-preview.png");
                    Capture(camera, site.bakedSite.machinePosition + new Vector3(-9f, 7f, -11f),
                        site.bakedSite.machinePosition + Vector3.forward * 2f, "Diagnostics/mound-top-close.png");
                    Capture(camera, site.bakedSite.machinePosition + new Vector3(14f, 12f, 17f),
                        site.bakedSite.machinePosition, "Diagnostics/mound-top-front.png");
                    Capture(camera, site.bakedSite.machinePosition + new Vector3(3f, 2f, -8f),
                        (site.bakedSite.machinePosition + site.truck.position) * .5f,
                        "Diagnostics/loading-ground-low-view.png");
                }
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
        }

        private static void Capture(Camera camera, Vector3 position, Vector3 lookAt, string path)
        {
            var target = new RenderTexture(1400, 1000, 24);
            var image = new Texture2D(1400, 1000, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active, previousTarget = camera.targetTexture;
            try
            {
                camera.transform.position = position;
                camera.transform.LookAt(lookAt);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1400, 1000), 0, 0);
                image.Apply();
                Directory.CreateDirectory("Diagnostics");
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previous;
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(target);
            }
        }
    }
}
