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
                AutonomousExcavationState measuredState = automatic.State;
                float stateStarted = Time.time;
                while (automatic.IsRunning && Time.realtimeSinceStartup < deadline)
                {
                    if (automatic.State != measuredState)
                    {
                        Debug.Log($"阶段耗时：斗数={cycles}，阶段={measuredState}，模拟秒={Time.time - stateStarted:F2}");
                        measuredState = automatic.State;
                        stateStarted = Time.time;
                    }
                    if (automatic.CompletedCycles > cycles)
                    {
                        Assert.That(automatic.receiver.VolumeM3, Is.GreaterThan(receipt + .005), "每斗都必须实际装入车厢。");
                        cycles = automatic.CompletedCycles;
                        receipt = automatic.receiver.VolumeM3;
                        Debug.Log($"完整持续装车：斗数={cycles}，车厢增量={receipt - initial:F5}m³");
                    }
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(automatic.State, Is.EqualTo(AutonomousExcavationState.Completed), automatic.FaultReason + " / " + automatic.Status);
                Assert.That(automatic.CompletedCycles, Is.GreaterThanOrEqualTo(3));
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
                while (automatic.IsRunning && automatic.CompletedCycles < 3 && Time.realtimeSinceStartup < deadline)
                {
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
