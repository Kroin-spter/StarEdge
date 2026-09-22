using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class Zx200CameraControllerTests
    {
        private GameObject root;
        private Zx200CameraController controller;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("zx200");
            controller = root.AddComponent<Zx200CameraController>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        [Test]
        public void DefaultOrbitYaw_PlacesCameraBehindExcavatorForwardAxis()
        {
            // ROS +X is converted to Unity +Z, so the rear camera is on local -Z.
            Assert.That(controller.initialThirdPersonYaw, Is.Zero);
            Assert.That(controller.OrbitYaw, Is.Zero);
            Assert.That(controller.firstPersonForwardEuler, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void ToggleView_ChangesBetweenThirdAndFirstPerson()
        {
            Assert.That(controller.IsThirdPerson, Is.True);

            controller.ToggleView();

            Assert.That(controller.IsThirdPerson, Is.False);
        }

        [Test]
        public void AddLookInput_ClampsPitch()
        {
            controller.AddLookInput(15f, 1000f);

            Assert.That(controller.OrbitYaw, Is.EqualTo(15f));
            Assert.That(controller.OrbitPitch, Is.EqualTo(controller.maxPitch));
        }

        [Test]
        public void AddZoomInput_ClampsDistance()
        {
            controller.AddZoomInput(100f);
            Assert.That(controller.Distance, Is.EqualTo(controller.minDistance));

            controller.AddZoomInput(-100f);
            Assert.That(controller.Distance, Is.EqualTo(controller.maxDistance));
        }
    }
}
