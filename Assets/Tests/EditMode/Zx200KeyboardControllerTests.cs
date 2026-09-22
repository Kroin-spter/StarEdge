using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class Zx200KeyboardControllerTests
    {
        [Test]
        public void CalculateMotion_UsesLocalForwardAndWorldYaw()
        {
            var root = new GameObject("zx200");
            try
            {
                var controller = root.AddComponent<Zx200KeyboardController>();
                controller.moveSpeed = 2f;
                controller.turnSpeed = 30f;

                controller.CalculateMotion(1f, -1f, 0.5f,
                    out Vector3 translation, out float yaw);

                Assert.That(translation, Is.EqualTo(Vector3.forward));
                Assert.That(yaw, Is.EqualTo(-15f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void MoveArticulationRoot_QueuesInputWithoutTeleporting()
        {
            var outer = new GameObject("zx200");
            var track = new GameObject("track_link");
            track.transform.SetParent(outer.transform);
            ArticulationBody articulationRoot = track.AddComponent<ArticulationBody>();
            articulationRoot.immovable = true;
            try
            {
                var controller = outer.AddComponent<Zx200KeyboardController>();
                controller.articulationRoot = articulationRoot;
                controller.moveSpeed = 2f;
                controller.fixedBase = false;

                bool moved = controller.MoveArticulationRoot(1f, 0f, 0.5f);

                Assert.That(moved, Is.True);
                Assert.That(outer.transform.position, Is.EqualTo(Vector3.zero));
                Assert.That(articulationRoot.transform.position.z,
                    Is.EqualTo(0f).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(outer);
            }
        }

        [Test]
        public void MoveArticulationRoot_FixedBaseDoesNotMoveArticulationRoot()
        {
            var outer = new GameObject("zx200");
            var track = new GameObject("track_link");
            track.transform.SetParent(outer.transform);
            track.transform.SetPositionAndRotation(
                new Vector3(3f, 2f, 1f), Quaternion.Euler(0f, 25f, 0f));
            ArticulationBody articulationRoot = track.AddComponent<ArticulationBody>();
            articulationRoot.immovable = true;
            try
            {
                var controller = outer.AddComponent<Zx200KeyboardController>();
                controller.articulationRoot = articulationRoot;
                controller.fixedBase = true;
                Vector3 originalPosition = track.transform.position;
                Quaternion originalRotation = track.transform.rotation;

                bool moved = controller.MoveArticulationRoot(1f, 1f, 0.5f);

                Assert.That(moved, Is.False);
                Assert.That(track.transform.position, Is.EqualTo(originalPosition));
                Assert.That(track.transform.rotation, Is.EqualTo(originalRotation));
            }
            finally
            {
                Object.DestroyImmediate(outer);
            }
        }

        [TestCase(127f, 0f)]
        [TestCase(128f, 0f)]
        [TestCase(126f, 0f)]
        [TestCase(129f, 0f)]
        [TestCase(125f, -0.019608f)]
        [TestCase(130f, 0.019763f)]
        [TestCase(254f, 1f)]
        [TestCase(0f, -1f)]
        public void NormalizeAction_CentersAzUnsignedValue(float raw, float expected)
        {
            Assert.That(Zx200KeyboardController.NormalizeAction(raw), Is.EqualTo(expected).Within(0.001f));
        }

        [Test]
        public void HasControlTopic_KeepsReceivedFrameAvailable()
        {
            var root = new GameObject("zx200");
            try
            {
                var controller = root.AddComponent<Zx200KeyboardController>();

                Assert.That(controller.HasControlTopic(), Is.False);

                controller.ReceiveControlTopic("{\"action6\":254}");

                Assert.That(controller.HasControlTopic(), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CalculateTrackMotion_EqualTracksMoveStraight()
        {
            Zx200KeyboardController.CalculateTrackMotion(1f, 1f, out float forward, out float steering);
            Assert.That(forward, Is.EqualTo(1f));
            Assert.That(steering, Is.EqualTo(0f));
        }

        [Test]
        public void CalculateTrackMotion_OneTrackCreatesTurn()
        {
            Zx200KeyboardController.CalculateTrackMotion(0f, 1f, out float forward, out float steering);
            Assert.That(forward, Is.EqualTo(0.5f));
            Assert.That(steering, Is.EqualTo(-0.5f));
        }

        [Test]
        public void CalculateTrackMotion_OppositeTracksTurnInPlace()
        {
            Zx200KeyboardController.CalculateTrackMotion(-1f, 1f, out float forward, out float steering);
            Assert.That(forward, Is.EqualTo(0f));
            Assert.That(steering, Is.EqualTo(-1f));
        }

        [TestCase(true, false, true, 0.75f, -1f)]
        [TestCase(false, true, true, -0.75f, 1f)]
        [TestCase(true, true, true, 0.75f, 0f)]
        [TestCase(false, false, true, 0.75f, 0.75f)]
        [TestCase(false, false, false, 0.75f, 0f)]
        public void ResolveAxisInput_KeyboardOverridesTopicWhileHeld(
            bool negativePressed,
            bool positivePressed,
            bool hasTopic,
            float topicInput,
            float expected)
        {
            MethodInfo resolve = typeof(Zx200KeyboardController).GetMethod(
                "ResolveAxisInput", BindingFlags.Public | BindingFlags.Static);
            Assert.That(resolve, Is.Not.Null);

            float actual = (float)resolve.Invoke(
                null,
                new object[] { negativePressed, positivePressed, hasTopic, topicInput });

            Assert.That(actual, Is.EqualTo(expected));
        }
    }
}
