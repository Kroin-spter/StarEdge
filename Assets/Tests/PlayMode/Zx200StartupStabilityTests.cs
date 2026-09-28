using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    /// <summary>覆盖原测试忽略的启动阶段和实际键盘速度下的关节反向。</summary>
    public class Zx200StartupStabilityTests
    {
        private GameObject robot;
        private GameObject ground;

        /// <summary>启动直接处于紧凑停放姿态，斗刃离地且底盘保持平稳。</summary>
        /// <returns>从实例创建到停放关节稳定的物理帧。</returns>
        [UnityTest]
        public IEnumerator StartupAlreadyMatchesParkedPose()
        {
            var dig = CreateRobot();
            FieldInfo parkedField = typeof(Zx200DigController).GetField("parkedPose");
            Assert.That(parkedField, Is.Not.Null);
            Zx200JointPose parkedPose = (Zx200JointPose)parkedField.GetValue(dig);
            var root = robot.GetComponent<Zx200KeyboardController>().articulationRoot;
            Vector3 initialTip = dig.bucketJoint.transform.position;
            Vector3 initialRoot = root.transform.position;
            float maximumTravel = 0f, maximumTilt = 0f, maximumAngularSpeed = 0f;
            for (int i = 0; i < 150; i++)
            {
                yield return new WaitForFixedUpdate();
                maximumTravel = Mathf.Max(maximumTravel, Vector3.Distance(initialTip, dig.bucketJoint.transform.position));
                maximumTilt = Mathf.Max(maximumTilt, Vector3.Angle(root.transform.up, Vector3.up));
                maximumAngularSpeed = Mathf.Max(maximumAngularSpeed, root.angularVelocity.magnitude);
            }
            Debug.Log($"STARTUP travel={maximumTravel:F4}m tilt={maximumTilt:F4}deg " +
                $"angular={maximumAngularSpeed:F4}rad/s rootDelta={root.transform.position - initialRoot} " +
                $"pose={dig.ReadActualPose().boom:F1}/{dig.ReadActualPose().arm:F1}/" +
                $"{dig.ReadActualPose().bucket:F1}");
            Assert.That(maximumTravel, Is.LessThan(.2f), "允许重力下的小幅弹性沉降，但不能从零位甩臂");
            Assert.That(maximumTilt, Is.LessThan(1f));
            Assert.That(dig.IsPoseWithinTolerance(parkedPose, 1.5f), Is.True);
            Assert.That(parkedPose.arm, Is.GreaterThan(dig.workReadyPose.arm + 15f));
            Collider[] bucketColliders = dig.bucketJoint.GetComponentsInChildren<Collider>(true);
            float minimumBladeHeight = float.PositiveInfinity;
            foreach (Collider bucketCollider in bucketColliders)
                if (bucketCollider.enabled && !bucketCollider.isTrigger)
                    minimumBladeHeight = Mathf.Min(minimumBladeHeight, bucketCollider.bounds.min.y);
            Assert.That(minimumBladeHeight, Is.Not.EqualTo(float.PositiveInfinity),
                "The bucket prefab must expose a physical collider for clearance validation.");
            Assert.That(minimumBladeHeight, Is.GreaterThan(.4f),
                "The compact bucket must remain visibly clear of the supporting floor.");
            Assert.That(root.immovable, Is.False);
        }

        /// <summary>从紧凑停放姿态仍可平滑进入切削准备姿态，不会锁死启动流程。</summary>
        /// <returns>停放稳定、过渡及物理解算收敛所需的物理帧。</returns>
        [UnityTest]
        public IEnumerator ParkedStartupCanReachWorkReadyPose()
        {
            Zx200DigController dig = CreateRobot();
            for (int i = 0; i < 150; i++) yield return new WaitForFixedUpdate();
            Zx200JointPose parked = dig.ReadActualPose();
            Assert.That(dig.IsPoseWithinTolerance(dig.parkedPose, 1.5f), Is.True);
            Assert.That(dig.StartAutomaticPose(dig.workReadyPose), Is.True,
                "The cutter's work-ready transition must be startable from parked pose.");

            float deadline = Time.realtimeSinceStartup + 12f;
            while (dig.IsBusy && Time.realtimeSinceStartup < deadline)
                yield return new WaitForFixedUpdate();

            Assert.That(dig.IsBusy, Is.False, "The work-ready transition must not deadlock.");
            Assert.That(dig.LastAutomaticMotionSucceeded, Is.True);
            Assert.That(dig.IsPoseWithinTolerance(dig.workReadyPose, dig.jointToleranceDegrees), Is.True);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(parked.arm, dig.ReadActualPose().arm)),
                Is.GreaterThan(10f), "The stick must visibly extend from the parked state.");
            Assert.That(robot.GetComponent<Zx200KeyboardController>().articulationRoot.immovable, Is.False);
        }

        /// <summary>按实际键盘速度回转、动臂并反向，底盘不产生明显摇摆。</summary>
        /// <returns>待机和多轴运动的物理帧。</returns>
        [UnityTest]
        public IEnumerator FullSpeedReversalsRemainStable()
        {
            var dig = CreateRobot();
            var keyboard = robot.GetComponent<Zx200KeyboardController>();
            var root = keyboard.articulationRoot;
            for (int i = 0; i < 200; i++) yield return new WaitForFixedUpdate();
            float tilt = 0f, angular = 0f, yaw = 0f;
            float initialYaw = root.transform.eulerAngles.y;
            for (int i = 0; i < 350; i++)
            {
                if (i < 50) dig.AdjustManualPose(keyboard.swingSpeed * Time.fixedDeltaTime, 0, 0, 0);
                else if (i < 100) dig.AdjustManualPose(-keyboard.swingSpeed * Time.fixedDeltaTime, 0, 0, 0);
                else if (i < 150) dig.AdjustManualPose(0, keyboard.boomSpeed * Time.fixedDeltaTime, 0, 0);
                else if (i < 200) dig.AdjustManualPose(0, -keyboard.boomSpeed * Time.fixedDeltaTime, 0, 0);
                else if (i < 250) dig.AdjustManualPose(0, 0, keyboard.armSpeed * Time.fixedDeltaTime, keyboard.bucketSpeed * Time.fixedDeltaTime);
                else if (i < 300) dig.AdjustManualPose(0, 0, -keyboard.armSpeed * Time.fixedDeltaTime, -keyboard.bucketSpeed * Time.fixedDeltaTime);
                yield return new WaitForFixedUpdate();
                if (i == 49) Assert.That(dig.ReadActualPose().swing, Is.GreaterThan(20f), "稳定不能靠阻止回转实现");
                if (i == 149) Assert.That(dig.ReadActualPose().boom, Is.GreaterThan(-33f), "动臂仍需正常响应");
                if (i == 249) Assert.That(dig.ReadActualPose().arm, Is.GreaterThan(85f), "斗杆仍需正常响应");
                tilt = Mathf.Max(tilt, Vector3.Angle(root.transform.up, Vector3.up));
                angular = Mathf.Max(angular, root.angularVelocity.magnitude);
                yaw = Mathf.Max(yaw, Mathf.Abs(Mathf.DeltaAngle(initialYaw, root.transform.eulerAngles.y)));
            }
            Debug.Log($"REVERSALS tilt={tilt:F4}deg yaw={yaw:F4}deg angular={angular:F4}rad/s");
            Assert.That(tilt, Is.LessThan(1.5f));
            Assert.That(yaw, Is.LessThan(1.5f));
            Assert.That(angular, Is.LessThan(.2f));
            Assert.That(root.immovable, Is.False);
        }

        private Zx200DigController CreateRobot()
        {
            ground = new GameObject("Startup test ground");
            ground.transform.position = new Vector3(0f, -.5f, 0f);
            ground.AddComponent<BoxCollider>().size = new Vector3(100f, 1f, 100f);
            robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Robotics/zx200_description/Prefabs/zx200.prefab"));
            var keyboard = robot.GetComponent<Zx200KeyboardController>();
            keyboard.fixedBase = false;
            keyboard.enabled = false;
            robot.GetComponent<Zx200FixedBasePhysics>().Configure();
            return robot.GetComponent<Zx200DigController>();
        }

        /// <summary>清理每次测试创建的实体。</summary>
        [TearDown]
        public void Cleanup()
        {
            if (robot != null) Object.DestroyImmediate(robot);
            if (ground != null) Object.DestroyImmediate(ground);
        }
    }
}
