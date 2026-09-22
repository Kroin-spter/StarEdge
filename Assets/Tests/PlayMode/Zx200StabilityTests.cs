using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    /// <summary>验证动态底盘驻车时，关节运动不会造成明显侧倾和底盘反转。</summary>
    public class Zx200StabilityTests
    {
        private GameObject robot;
        private GameObject ground;
        private Zx200DigController dig;
        private ArticulationBody root;

        private IEnumerator Prepare()
        {
            ground = new GameObject("稳定性测试地面");
            ground.transform.position = new Vector3(0, -.5f, 0);
            ground.AddComponent<BoxCollider>().size = new Vector3(100, 1, 100);
            robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Robotics/zx200_description/Prefabs/zx200.prefab"));
            var keyboard = robot.GetComponent<Zx200KeyboardController>();
            keyboard.fixedBase = false;
            keyboard.enabled = false;
            robot.GetComponent<Zx200FixedBasePhysics>().Configure();
            dig = robot.GetComponent<Zx200DigController>();
            root = keyboard.articulationRoot;
            for (int i = 0; i < 250; i++) yield return new WaitForFixedUpdate();
        }

        /// <summary>手动回转及大小臂、铲斗运动时记录倾角和底盘偏航。</summary>
        /// <returns>初始化、关节运动与稳定阶段的物理帧。</returns>
        [UnityTest]
        public IEnumerator ManualMotionKeepsParkedBaseStable()
        {
            yield return Prepare();
            float yaw = root.transform.eulerAngles.y;
            float tiltMax = 0, yawMax = 0, angularMax = 0;
            for (int i = 0; i < 500; i++)
            {
                if (i < 100) dig.AdjustManualPose(15 * Time.fixedDeltaTime, 0, 0, 0);
                else if (i < 200) dig.AdjustManualPose(0, 10 * Time.fixedDeltaTime, 5 * Time.fixedDeltaTime, 10 * Time.fixedDeltaTime);
                else if (i < 300) dig.AdjustManualPose(-15 * Time.fixedDeltaTime, -10 * Time.fixedDeltaTime, -5 * Time.fixedDeltaTime, -10 * Time.fixedDeltaTime);
                yield return new WaitForFixedUpdate();
                tiltMax = Mathf.Max(tiltMax, Vector3.Angle(root.transform.up, Vector3.up));
                yawMax = Mathf.Max(yawMax, Mathf.Abs(Mathf.DeltaAngle(yaw, root.transform.eulerAngles.y)));
                angularMax = Mathf.Max(angularMax, root.angularVelocity.magnitude);
            }
            Debug.Log($"驻车手动动作：最大倾角={tiltMax:F4}度，最大偏航={yawMax:F4}度，最大角速度={angularMax:F4}rad/s");
            Assert.That(root.immovable, Is.False);
            Assert.That(tiltMax, Is.LessThan(3));
            Assert.That(yawMax, Is.LessThan(2));
            var physics = robot.GetComponent<Zx200FixedBasePhysics>();
            Assert.That(Vector3.Distance(physics.counterweightBody.transform.localPosition,
                physics.counterweightLocalPosition), Is.LessThan(.01f));
        }

        /// <summary>已有预设动作循环结束后能回到待机位，且动态底盘驻车稳定。</summary>
        /// <returns>初始化和完整预设循环的物理帧。</returns>
        [UnityTest]
        public IEnumerator AutomaticCycleKeepsMobileBaseStable()
        {
            yield return Prepare();
            float yaw = root.transform.eulerAngles.y;
            float tiltMax = 0, yawMax = 0;
            Assert.That(dig.PlayDigCycle(), Is.True);
            for (int i = 0; i < 1500 && dig.IsBusy; i++)
            {
                yield return new WaitForFixedUpdate();
                tiltMax = Mathf.Max(tiltMax, Vector3.Angle(root.transform.up, Vector3.up));
                yawMax = Mathf.Max(yawMax, Mathf.Abs(Mathf.DeltaAngle(yaw, root.transform.eulerAngles.y)));
            }
            Debug.Log($"驻车预设循环：最大倾角={tiltMax:F4}度，最大偏航={yawMax:F4}度");
            Assert.That(dig.IsBusy, Is.False);
            Assert.That(dig.IsPoseWithinTolerance(dig.workReadyPose, dig.jointToleranceDegrees), Is.True);
            Assert.That(root.immovable, Is.False);
            Assert.That(tiltMax, Is.LessThan(3));
            Assert.That(yawMax, Is.LessThan(2));
        }

        /// <summary>销毁测试设备和地面。</summary>
        [TearDown]
        public void Cleanup()
        {
            if (robot != null) Object.DestroyImmediate(robot);
            if (ground != null) Object.DestroyImmediate(ground);
        }
    }
}
