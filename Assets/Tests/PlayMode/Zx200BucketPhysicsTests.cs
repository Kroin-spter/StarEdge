using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public class Zx200BucketPhysicsTests
    {
        private const string PrefabPath = "Assets/Robotics/zx200_description/Prefabs/zx200.prefab";

        /// <summary>Normal phase transitions retain measured solver velocity, while emergency cancellation zeros it.</summary>
        /// <returns>A frame for the actual articulated prefab to initialize.</returns>
        [UnityTest]
        public IEnumerator NormalHandoffPreservesPhysicalJointVelocityButEmergencyStopDoesNot()
        {
            GameObject robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            try
            {
                yield return null;
                var machine = robot.GetComponent<Zx200DigController>();
                machine.StopAutomaticMotion();
                Assert.That(machine.StartContinuousPoseControl(machine.ReadActualPose()), Is.True);
                machine.bucketJoint.jointVelocity = new ArticulationReducedSpace(.4f);
                machine.StopContinuousPoseControl(true, false);
                Assert.That(machine.bucketJoint.jointVelocity[0], Is.EqualTo(.4f).Within(.0001f),
                    "正常切削结束不能直接清零求解器速度。");
                machine.StopAutomaticMotion();
                Assert.That(machine.bucketJoint.jointVelocity[0], Is.Zero.Within(.0001f),
                    "人工接管和紧急取消仍必须立即停止。");
            }
            finally { Object.DestroyImmediate(robot); }
        }

        /// <summary>Verifies that the bucket's visible cavity does not contain solid collision.</summary>
        /// <returns>One frame to initialize the imported articulation.</returns>
        [UnityTest]
        public IEnumerator SphereFitsInsideBucketCavity()
        {
            GameObject robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            robot.GetComponent<Zx200KeyboardController>().fixedBase = true;
            robot.GetComponent<Zx200FixedBasePhysics>().Configure();
            GameObject ball = new GameObject("Cavity probe");
            try
            {
                yield return null;
                var bucket = robot.GetComponent<Zx200DigController>().bucketJoint;
                var sphere = ball.AddComponent<SphereCollider>();
                sphere.radius = 0.225f;
                ball.transform.position = bucket.transform.TransformPoint(new Vector3(0f, 0.65f, 0.6f));
                foreach (Collider collider in bucket.GetComponentsInChildren<Collider>())
                {
                    if (collider.attachedArticulationBody != bucket || !collider.enabled) continue;
                    bool overlaps = Physics.ComputePenetration(sphere, ball.transform.position, Quaternion.identity,
                        collider, collider.transform.position, collider.transform.rotation, out _, out float depth);
                    Assert.That(overlaps, Is.False, $"Cavity blocked by {collider.name}, penetration={depth}");
                }
            }
            finally { Object.Destroy(ball); Object.Destroy(robot); }
        }

        /// <summary>Checks real rigidbody entry, carrying, and release without attaching the ball.</summary>
        /// <returns>Physics steps for the loaded articulation and sphere.</returns>
        [UnityTest]
        public IEnumerator BucketAcceptsCarriesAndReleasesDynamicSphere()
        {
            GameObject robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            robot.GetComponent<Zx200KeyboardController>().fixedBase = true;
            robot.GetComponent<Zx200FixedBasePhysics>().Configure();
            GameObject ball = null;
            try
            {
                robot.GetComponent<Zx200KeyboardController>().enabled = false;
                var dig = robot.GetComponent<Zx200DigController>();
                yield return null;
                dig.AdjustManualPose(0f, 0f, 0f, 85f);
                for (int i = 0; i < 250; i++) yield return new WaitForFixedUpdate();
                Transform bucket = dig.bucketJoint.transform;
                ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.name = "Dynamic scoop test ball";
                // Match the user scene sphere: diameter 0.45 m and mass 1 kg.
                ball.transform.localScale = Vector3.one * 0.45f;
                ball.transform.position = bucket.TransformPoint(new Vector3(0f, 0.65f, 0.6f)) + Vector3.up * 0.8f;
                var body = ball.AddComponent<Rigidbody>();
                body.mass = 1f;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                for (int i = 0; i < 150; i++) yield return new WaitForFixedUpdate();
                AssertInBucket(bucket, ball.transform);
                float loadedHeight = body.position.y;
                // Use continuous manual-style commands while carrying a free body.
                for (int i = 0; i < 100; i++)
                {
                    dig.AdjustManualPose(0f, -5f * Time.fixedDeltaTime, 0f, 0f);
                    yield return new WaitForFixedUpdate();
                }
                for (int i = 0; i < 50; i++) yield return new WaitForFixedUpdate();
                AssertInBucket(bucket, ball.transform);
                Assert.That(body.position.y - loadedHeight, Is.GreaterThan(0.1f), "Ball must lift with bucket");
                // Ramp the loaded swing at 5 degrees/second rather than applying a
                // discontinuous target jump that can throw a freely rolling sphere.
                for (int i = 0; i < 200; i++)
                {
                    dig.AdjustManualPose(5f * Time.fixedDeltaTime, 0f, 0f, 0f);
                    yield return new WaitForFixedUpdate();
                }
                for (int i = 0; i < 50; i++) yield return new WaitForFixedUpdate();
                AssertInBucket(bucket, ball.transform);
                Assert.That(body.isKinematic, Is.False);
                Assert.That(ball.transform.parent, Is.Null);
                dig.AdjustManualPose(0f, 0f, 0f, -85f);
                for (int i = 0; i < 150; i++) yield return new WaitForFixedUpdate();
                Assert.That(body.position.y, Is.LessThan(bucket.position.y - 2f), "Ball must fall out when bucket opens");
            }
            finally { if (ball != null) Object.Destroy(ball); Object.Destroy(robot); }
        }

        private static void AssertInBucket(Transform bucket, Transform ball)
        {
            Vector3 local = bucket.InverseTransformPoint(ball.position);
            Debug.Log($"Ball position in bucket: {local}");
            Assert.That(Mathf.Abs(local.x), Is.LessThan(0.5f));
            Assert.That(local.y, Is.InRange(0.55f, 1.25f));
            Assert.That(local.z, Is.InRange(0.15f, 1.05f));
        }
    }
}
