using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public class Zx200KeyboardPhysicsPlayModeTests
    {
        private const string PrefabPath =
            "Assets/Robotics/zx200_description/Prefabs/zx200.prefab";

        /// <summary>Checks that gravity does not pin the loaded joints at their limits.</summary>
        /// <returns>Physics steps for all four axes and their reverse commands.</returns>
        [UnityTest]
        public IEnumerator AllAxesRespondAfterStartup()
        {
            yield return CheckAllAxes(false);
        }

        /// <summary>Checks that ground contact does not block manual operation.</summary>
        /// <returns>Physics steps with a flat ground collider.</returns>
        [UnityTest]
        public IEnumerator AllAxesRespondWithGround()
        {
            yield return CheckAllAxes(true);
        }

        private static IEnumerator CheckAllAxes(bool includeGround)
        {
            GameObject ground = null;
            GameObject robot = null;
            try
            {
                if (includeGround)
                {
                    ground = new GameObject("Ground collision");
                    ground.transform.position = new Vector3(0f, -0.5f, 0f);
                    ground.AddComponent<BoxCollider>().size = new Vector3(100f, 1f, 100f);
                }
                robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
                var dig = robot.GetComponent<Zx200DigController>();
                var keyboard = robot.GetComponent<Zx200KeyboardController>();
                keyboard.fixedBase = true;
                robot.GetComponent<Zx200FixedBasePhysics>().Configure();
                keyboard.enabled = false;
                Vector3 basePosition = keyboard.articulationRoot.transform.position;
                Quaternion baseRotation = keyboard.articulationRoot.transform.rotation;
                yield return null;
                for (int step = 0; step < 250; step++) yield return new WaitForFixedUpdate();
                var joints = new[] { dig.swingJoint, dig.boomJoint, dig.armJoint, dig.bucketJoint };
                foreach (var joint in joints)
                    AssertAtTarget(joint);

                for (int axis = 0; axis < joints.Length; axis++)
                {
                    var joint = joints[axis];
                    float initial = joint.jointPosition[0] * Mathf.Rad2Deg;
                    float[] delta = new float[4];
                    delta[axis] = 15f;
                    Assert.That(dig.AdjustManualPose(delta[0], delta[1], delta[2], delta[3]), Is.True);
                    for (int step = 0; step < 150; step++) yield return new WaitForFixedUpdate();
                    float positive = joint.jointPosition[0] * Mathf.Rad2Deg;
                    AssertAtTarget(joint);
                    delta[axis] = -30f;
                    Assert.That(dig.AdjustManualPose(delta[0], delta[1], delta[2], delta[3]), Is.True);
                    for (int step = 0; step < 150; step++) yield return new WaitForFixedUpdate();
                    float negative = joint.jointPosition[0] * Mathf.Rad2Deg;
                    Debug.Log($"AXIS {joint.name}: {initial:F2} -> {positive:F2} -> {negative:F2} degrees; ground={includeGround}");
                    AssertAtTarget(joint);
                    Assert.That(positive - initial, Is.GreaterThan(3f), joint.name + " positive response");
                    Assert.That(positive - negative, Is.GreaterThan(3f), joint.name + " negative response");
                }
                Assert.That(Vector3.Distance(basePosition, keyboard.articulationRoot.transform.position), Is.LessThan(0.001f));
                Assert.That(Quaternion.Angle(baseRotation, keyboard.articulationRoot.transform.rotation), Is.LessThan(0.01f));
            }
            finally
            {
                if (robot != null) Object.Destroy(robot);
                if (ground != null) Object.Destroy(ground);
            }
        }

        private static void AssertAtTarget(ArticulationBody joint)
        {
            float actual = joint.jointPosition[0] * Mathf.Rad2Deg;
            Assert.That(float.IsNaN(actual) || float.IsInfinity(actual), Is.False, joint.name);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(actual, joint.xDrive.target)),
                Is.LessThan(1.5f), $"{joint.name}: actual={actual}, target={joint.xDrive.target}");
        }

        [UnityTest]
        public IEnumerator PositiveSwingKeyPathMovesPhysicalJointAndKeepsBaseFixed()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(prefab, Is.Not.Null);
            GameObject robot = Object.Instantiate(prefab);
            try
            {
                Zx200KeyboardController keyboard = robot.GetComponent<Zx200KeyboardController>();
                Zx200DigController dig = robot.GetComponent<Zx200DigController>();
                Assert.That(keyboard, Is.Not.Null);
                Assert.That(dig, Is.Not.Null);

                keyboard.fixedBase = true;
                robot.GetComponent<Zx200FixedBasePhysics>().Configure();
                yield return null;
                yield return new WaitForFixedUpdate();
                keyboard.fixedBase = true;
                robot.GetComponent<Zx200FixedBasePhysics>().Configure();
                keyboard.enabled = false;
                Vector3 basePosition = keyboard.articulationRoot.transform.position;
                float initialAngle = dig.ReadActualPose().swing;
                float initialTarget = dig.swingJoint.xDrive.target;
                float keyInput = Zx200KeyboardController.ResolveAxisInput(
                    false, true, false, 0f);

                bool accepted = dig.AdjustManualPose(keyInput * 15f, 0f, 0f, 0f);

                Assert.That(accepted, Is.True);
                Assert.That(dig.swingJoint.xDrive.target,
                    Is.EqualTo(initialTarget).Within(0.01f), "新指令应经过物理步平滑，不能立即跳变");
                Assert.That(dig.swingJoint.xDrive.stiffness, Is.EqualTo(dig.stiffness));
                Assert.That(dig.swingJoint.xDrive.damping, Is.EqualTo(dig.damping));
                Assert.That(dig.swingJoint.xDrive.forceLimit, Is.EqualTo(dig.forceLimit));
                for (int i = 0; i < 50; i++) yield return new WaitForFixedUpdate();

                float finalAngle = dig.ReadActualPose().swing;
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(initialAngle, finalAngle)),
                    Is.GreaterThan(0.5f));
                Assert.That(keyboard.articulationRoot.transform.position,
                    Is.EqualTo(basePosition));
            }
            finally
            {
                Object.Destroy(robot);
            }
        }
    }
}
