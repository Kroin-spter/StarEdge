using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    /// <summary>Validates autonomous job boundaries without loading Cesium or running physics.</summary>
    public class AutonomousExcavationControllerTests
    {
        [Test]
        public void NewController_StartsIdle()
        {
            GameObject root = new GameObject("autonomous");
            try
            {
                var controller = root.AddComponent<AutonomousExcavationController>();

                Assert.That(controller.State, Is.EqualTo(AutonomousExcavationState.Idle));
                Assert.That(controller.IsRunning, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void StartAutonomousJob_RejectsMissingDependencies()
        {
            GameObject root = new GameObject("autonomous");
            try
            {
                var controller = root.AddComponent<AutonomousExcavationController>();

                Assert.That(controller.StartAutonomousJob(), Is.False);
                Assert.That(controller.State, Is.EqualTo(AutonomousExcavationState.Fault));
                Assert.That(controller.FaultReason, Does.Contain("依赖"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [TestCase(0.02, 0.02, true)]
        [TestCase(0.01999, 0.02, false)]
        [TestCase(1.0, 0.02, true)]
        public void HasReachedTarget_UsesDeliveredVolume(double delivered, double target, bool expected)
        {
            Assert.That(AutonomousExcavationController.HasReachedTarget(delivered, target),
                Is.EqualTo(expected));
        }

        [TestCase(0.0, 0.01, true)]
        [TestCase(0.00999, 0.01, true)]
        [TestCase(0.01001, 0.01, false)]
        public void IsReceiverFull_StopsAtMinimumTransferCapacity(
            double available, double minimumTransfer, bool expected)
        {
            Assert.That(AutonomousExcavationController.IsReceiverFull(available, minimumTransfer),
                Is.EqualTo(expected));
        }
    }
}
