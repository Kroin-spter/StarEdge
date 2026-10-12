using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    /// <summary>使用完整预制体验证底盘受力移动、接地和障碍约束。</summary>
    public class Zx200TrackPhysicsTests
    {
        private GameObject robot;
        private GameObject ground;
        private GameObject wall;
        private Zx200KeyboardController keyboard;
        private ArticulationBody body;
        private TerrainData unevenTerrain;

        private IEnumerator CreateRobot(bool withGround, float slope = 0f)
        {
            if (withGround)
            {
                ground = new GameObject("测试地面");
                ground.transform.position = new Vector3(0f, -0.5f, 0f);
                ground.transform.rotation = Quaternion.Euler(-slope, 0f, 0f);
                ground.AddComponent<BoxCollider>().size = new Vector3(100f, 1f, 100f);
            }
            robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Robotics/zx200_description/Prefabs/zx200.prefab"));
            keyboard = robot.GetComponent<Zx200KeyboardController>();
            keyboard.fixedBase = false;
            keyboard.enabled = false;
            robot.GetComponent<Zx200FixedBasePhysics>().Configure();
            body = keyboard.articulationRoot;
            yield return null;
            for (int i = 0; i < 200; i++) yield return new WaitForFixedUpdate();
        }

        /// <summary>验证油门经物理推进，反向指令能倒车，松开后能制动。</summary>
        /// <returns>接地、前进、制动与倒车的物理帧。</returns>
        [UnityTest]
        public IEnumerator DrivesBrakesAndReversesOnGround()
        {
            yield return CreateRobot(true);
            Assert.That(body.immovable, Is.False);
            Vector3 start = body.transform.position;
            yield return Drive(1f, 0f, 150);
            float forward = body.transform.position.z - start.z;
            Debug.Log($"底盘前进距离：{forward:F3}，位置：{body.transform.position}");
            Assert.That(forward, Is.GreaterThan(1f));
            yield return Drive(0f, 0f, 100);
            Assert.That(Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up).magnitude, Is.LessThan(0.25f));
            Vector3 stopped = body.transform.position;
            yield return Drive(-1f, 0f, 150);
            Assert.That(body.transform.position.z, Is.LessThan(stopped.z - 1f));
            Assert.That(Vector3.Dot(body.transform.up, Vector3.up), Is.GreaterThan(0.9f));
        }

        /// <summary>验证左右原地转向指令产生相反的实际底盘偏航。</summary>
        /// <returns>左右转向及制动的物理帧。</returns>
        [UnityTest]
        public IEnumerator TurnsBothWays()
        {
            yield return CreateRobot(true);
            float start = body.transform.eulerAngles.y;
            yield return Drive(0f, 1f, 150);
            float right = body.transform.eulerAngles.y;
            Debug.Log($"底盘右转角度：{Mathf.DeltaAngle(start, right):F3}");
            Assert.That(Mathf.DeltaAngle(start, right), Is.GreaterThan(10f));
            yield return Drive(0f, 0f, 100);
            right = body.transform.eulerAngles.y;
            yield return Drive(0f, -1f, 150);
            Assert.That(Mathf.DeltaAngle(right, body.transform.eulerAngles.y), Is.LessThan(-10f));
        }

        /// <summary>验证障碍会阻挡底盘，不会被位置写入穿过。</summary>
        /// <returns>驶向静态墙体的物理帧。</returns>
        [UnityTest]
        public IEnumerator WallBlocksDriving()
        {
            yield return CreateRobot(true);
            Vector3 start = body.transform.position;
            wall = new GameObject("测试墙体");
            wall.transform.position = new Vector3(0f, 4f, start.z + 10f);
            wall.AddComponent<BoxCollider>().size = new Vector3(30f, 8f, 1f);
            Physics.SyncTransforms();
            foreach (Collider collider in robot.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger) continue;
                Assert.That(Physics.ComputePenetration(collider, collider.transform.position,
                    collider.transform.rotation, wall.GetComponent<Collider>(), wall.transform.position,
                    wall.transform.rotation, out _, out _), Is.False, "墙体不能与初始设备重叠");
            }
            yield return Drive(1f, 0f, 400);
            Assert.That(body.transform.position.z, Is.GreaterThan(start.z + 0.1f));
            Assert.That(body.transform.position.z, Is.LessThan(start.z + 9f));
            Assert.That(body.transform.position.y, Is.LessThan(start.y + 1f));
        }

        /// <summary>验证无地面时受重力下落，油门不能提供空中牵引。</summary>
        /// <returns>无地面情况下的物理帧。</returns>
        [UnityTest]
        public IEnumerator FallsWithoutAirTraction()
        {
            yield return CreateRobot(false);
            Vector3 start = body.transform.position;
            Vector3 velocity = body.linearVelocity;
            yield return Drive(1f, 1f, 50);
            Assert.That(body.transform.position.y, Is.LessThan(start.y - 1f));
            Assert.That(Vector3.ProjectOnPlane(body.linearVelocity - velocity, Vector3.up).magnitude, Is.LessThan(0.3f));
        }

        /// <summary>验证缓坡上行驶时高度由地面接触产生，而非射线吸附。</summary>
        /// <returns>缓坡接地及上坡的物理帧。</returns>
        [UnityTest]
        public IEnumerator ClimbsGentleSlope()
        {
            yield return CreateRobot(true, 8f);
            Vector3 start = body.transform.position;
            yield return Drive(1f, 0f, 150);
            Assert.That(body.transform.position.z - start.z, Is.GreaterThan(1f));
            Assert.That(body.transform.position.y - start.y, Is.GreaterThan(0.1f));
            Assert.That(Vector3.Dot(body.transform.up, ground.transform.up), Is.GreaterThan(0.95f));
        }

        /// <summary>Descending a usable soil slope must not strand the physical chassis at its lower end.</summary>
        /// <returns>Settling, downhill driving, braking and an uphill return under real ground collision.</returns>
        [UnityTest]
        public IEnumerator DescendsAndReturnsUpWorkingSlope()
        {
            yield return CreateRobot(true, 20f);
            Vector3 start = body.transform.position;
            yield return Drive(-1f, 0f, 150);
            Vector3 lower = body.transform.position;
            Assert.That(lower.z, Is.LessThan(start.z - 1f), "必须先真实下坡，不能原地声称往返");
            Assert.That(lower.y, Is.LessThan(start.y - .3f));
            yield return Drive(0f, 0f, 60);
            Vector3 stopped = body.transform.position;
            yield return Drive(0f, 0f, 60);
            Assert.That(Vector3.Distance(body.transform.position, stopped), Is.LessThan(.3f), "松开油门应保持稳定");
            yield return Drive(1f, 0f, 220);
            Debug.Log($"SLOPE ROUNDTRIP start={start} lower={lower} returned={body.transform.position} " +
                $"contacts={robot.GetComponent<Zx200TrackDrive>().GroundContactCount}");
            Assert.That(body.transform.position.z, Is.GreaterThan(start.z - .5f), "下坡后必须能靠履带牵引回到起点附近");
            Assert.That(body.transform.position.y, Is.GreaterThan(start.y - .2f));
            Assert.That(Vector3.Dot(body.transform.up, ground.transform.up), Is.GreaterThan(.9f));
        }

        /// <summary>Small soil ridges must not remove traction when the solid track hull still has support.</summary>
        /// <returns>Physical settling and an uphill run across a twenty-degree undulating Terrain ramp.</returns>
        [UnityTest]
        public IEnumerator ClimbsUnevenWorkingSlope()
        {
            unevenTerrain = new TerrainData { heightmapResolution = 257, size = new Vector3(40f, 18f, 40f) };
            var heights = new float[257, 257];
            for (int z = 0; z < 257; z++)
                for (int x = 0; x < 257; x++)
                {
                    float worldZ = -20f + z * 40f / 256f;
                    // Start on a level bench like the saved scene, then enter the rough ramp naturally.
                    float rampZ = Mathf.Max(0f, worldZ - 3f);
                    float height = Mathf.Tan(20f * Mathf.Deg2Rad) * rampZ +
                        .16f * Mathf.SmoothStep(0f, 1f, rampZ) * Mathf.Cos(rampZ * Mathf.PI / 1.5f);
                    heights[z, x] = (height + 9f) / 18f;
                }
            unevenTerrain.SetHeights(0, 0, heights);
            ground = Terrain.CreateTerrainGameObject(unevenTerrain);
            ground.transform.position = new Vector3(-20f, -9f, -20f);
            yield return CreateRobot(false);
            // Isolate track travel from a lowered bucket bulldozing into the rising ground.
            keyboard.fixedBase = true;
            robot.GetComponent<Zx200FixedBasePhysics>().Configure();
            var dig = robot.GetComponent<Zx200DigController>();
            Assert.That(dig.StartAutomaticPose(dig.carryPose), Is.True);
            for (int frame = 0; frame < 240 && dig.IsBusy; frame++) yield return new WaitForFixedUpdate();
            Assert.That(dig.LastAutomaticMotionSucceeded, Is.True, "行驶前机械臂应收至运输姿态");
            keyboard.fixedBase = false;
            robot.GetComponent<Zx200FixedBasePhysics>().Configure();
            for (int frame = 0; frame < 60; frame++) yield return new WaitForFixedUpdate();
            Vector3 start = body.transform.position;
            yield return Drive(1f, 0f, 220);
            Debug.Log($"UNEVEN SLOPE start={start} finish={body.transform.position} contacts={robot.GetComponent<Zx200TrackDrive>().GroundContactCount}");
            Assert.That(body.transform.position.z, Is.GreaterThan(start.z + 4.5f), "必须进入起伏坡面，而非只在初始平地移动");
            Assert.That(body.transform.position.y, Is.GreaterThan(start.y + .5f));
            Assert.That(Vector3.Dot(body.transform.up, Vector3.up), Is.GreaterThan(.8f));
        }

        /// <summary>验证输入中断后不会持续加速，固定模式能重新锁定底盘。</summary>
        /// <returns>输入中断、制动及固定模式的物理帧。</returns>
        [UnityTest]
        public IEnumerator StaleCommandBrakesAndFixedModeHolds()
        {
            yield return CreateRobot(true);
            yield return Drive(1f, 0f, 100);
            for (int i = 0; i < 150; i++) yield return new WaitForFixedUpdate();
            Assert.That(Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up).magnitude, Is.LessThan(0.25f));
            keyboard.fixedBase = true;
            yield return new WaitForFixedUpdate();
            Vector3 fixedPosition = body.transform.position;
            Assert.That(body.immovable, Is.True);
            Assert.That(keyboard.MoveArticulationRoot(1f, 1f, 0.02f), Is.False);
            for (int i = 0; i < 50; i++) yield return new WaitForFixedUpdate();
            Assert.That(Vector3.Distance(body.transform.position, fixedPosition), Is.LessThan(0.001f));
        }

        /// <summary>验证停用组件恢复原摩擦材质，再启用时重新应用履带材质。</summary>
        /// <returns>创建完整设备所需的物理帧。</returns>
        [UnityTest]
        public IEnumerator DisablingDriveRestoresColliderMaterial()
        {
            yield return CreateRobot(true);
            var drive = robot.GetComponent<Zx200TrackDrive>();
            Collider trackCollider = null;
            foreach (Collider collider in body.GetComponentsInChildren<Collider>())
                if (collider.attachedArticulationBody == body) { trackCollider = collider; break; }
            Assert.That(trackCollider, Is.Not.Null);
            PhysicsMaterial activeMaterial = trackCollider.sharedMaterial;
            Assert.That(activeMaterial, Is.Not.Null);
            drive.enabled = false;
            Assert.That(trackCollider.sharedMaterial, Is.Not.SameAs(activeMaterial));
            drive.enabled = true;
            Assert.That(trackCollider.sharedMaterial, Is.SameAs(activeMaterial));
        }

        private IEnumerator Drive(float forward, float turn, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Assert.That(keyboard.MoveArticulationRoot(forward, turn, Time.fixedDeltaTime), Is.True);
                yield return new WaitForFixedUpdate();
            }
        }

        /// <summary>清理每项测试创建的场景对象。</summary>
        [TearDown]
        public void Cleanup()
        {
            if (robot != null) Object.DestroyImmediate(robot);
            if (ground != null) Object.DestroyImmediate(ground);
            if (wall != null) Object.DestroyImmediate(wall);
            if (unevenTerrain != null) Object.DestroyImmediate(unevenTerrain);
        }
    }
}
