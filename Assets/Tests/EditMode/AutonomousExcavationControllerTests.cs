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

        /// <summary>The autonomous start key must not share the excavator's A/D track steering keys.</summary>
        [Test]
        public void NewController_UsesDedicatedStartKey()
        {
            GameObject root = new GameObject("autonomous");
            try
            {
                var controller = root.AddComponent<AutonomousExcavationController>();

                Assert.That(controller.AutonomousStartKey, Is.EqualTo(KeyCode.P));
                Assert.That(controller.AutonomousStartKey, Is.Not.EqualTo(KeyCode.A));
                Assert.That(controller.AutonomousStartKey, Is.Not.EqualTo(KeyCode.D));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void NewController_UsesPositiveDumpClearanceMargin()
        {
            GameObject root = new GameObject("autonomous");
            try
            {
                var controller = root.AddComponent<AutonomousExcavationController>();

                Assert.That(controller.DumpClearanceMarginMeters, Is.EqualTo(.25f).Within(.0001f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void OneCubicMetreTargetRequiresFourBucketsAtCurrentCapacity()
        {
            const double target = 1.0;
            const double bucket = 0.303886;

            Assert.That(System.Math.Ceiling(target / bucket), Is.EqualTo(4));
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

        [TestCase(.05, .005, .08, false)]
        [TestCase(.079, .005, .08, false)]
        [TestCase(.08, .005, .08, true)]
        [TestCase(.081, .005, .08, true)]
        public void HasCapacityForNextLoad_ReservesTheWholeBucket(
            double available, double minimumTransfer, double loadCapacity, bool expected)
        {
            Assert.That(AutonomousExcavationController.HasCapacityForNextLoad(
                available, minimumTransfer, loadCapacity), Is.EqualTo(expected));
        }

        [TestCase(AutonomousMotionAxis.Bucket, 15f, 10f, 20f, 30f, 15f)]
        [TestCase(AutonomousMotionAxis.Boom, 10f, 25f, 20f, 30f, 25f)]
        [TestCase(AutonomousMotionAxis.Arm, 10f, 15f, 35f, 30f, 35f)]
        [TestCase(AutonomousMotionAxis.Swing, 10f, 15f, 20f, 45f, 45f)]
        public void CreateSingleAxisWaypoint_HoldsOtherMeasuredJoints(
            AutonomousMotionAxis axis,
            float swing,
            float boom,
            float arm,
            float bucket,
            float target)
        {
            var actual = new Zx200JointPose(swing, boom, arm, bucket, .25f);

            Zx200JointPose waypoint = AutonomousExcavationController.CreateSingleAxisWaypoint(
                actual, axis, target, 1.25f);

            Assert.That(waypoint.swing, Is.EqualTo(axis == AutonomousMotionAxis.Swing ? target : swing));
            Assert.That(waypoint.boom, Is.EqualTo(axis == AutonomousMotionAxis.Boom ? target : boom));
            Assert.That(waypoint.arm, Is.EqualTo(axis == AutonomousMotionAxis.Arm ? target : arm));
            Assert.That(waypoint.bucket, Is.EqualTo(axis == AutonomousMotionAxis.Bucket ? target : bucket));
            Assert.That(waypoint.duration, Is.EqualTo(1.25f));
        }

        [Test]
        public void CreateSingleAxisWaypoint_RejectsNoAxisMotion()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                AutonomousExcavationController.CreateSingleAxisWaypoint(
                    new Zx200JointPose(0f, 0f, 0f, 0f, 1f),
                    AutonomousMotionAxis.None,
                    0f,
                    1f));
        }

        [Test]
        public void CreatePreSwingApproachPose_PreservesLiftedBoomInsteadOfConfiguredDumpBoom()
        {
            var lifted = new Zx200JointPose(-1f, -27.6f, 98.8f, 104.4f, 1f);
            var configuredDump = new Zx200JointPose(60f, -40f, 50f, -25f, 1.2f);

            Zx200JointPose approach = AutonomousExcavationController.CreatePreSwingApproachPose(
                lifted, configuredDump, 108.6f);

            Assert.That(approach.swing, Is.EqualTo(lifted.swing));
            Assert.That(approach.boom, Is.EqualTo(lifted.boom),
                "The approach must not lower the boom to a dump-only configured angle.");
            Assert.That(approach.arm, Is.EqualTo(configuredDump.arm));
            Assert.That(approach.bucket, Is.EqualTo(108.6f));
            Assert.That(approach.duration, Is.EqualTo(configuredDump.duration));
        }

        [TestCase(1f, -1f)]
        [TestCase(-1f, 1f)]
        public void HeightCorrectionSign_ChoosesBoomRotationThatRaisesTip(float leverZ, float expectedSign)
        {
            float sign = AutonomousExcavationController.GetHeightImprovingRotationSign(
                Vector3.right,
                new Vector3(0f, 0f, leverZ));

            Assert.That(sign, Is.EqualTo(expectedSign));
        }

        [Test]
        public void HeightCorrectionSign_ReturnsZeroWhenBoomCannotChangeTipHeight()
        {
            float sign = AutonomousExcavationController.GetHeightImprovingRotationSign(
                Vector3.up,
                Vector3.right);

            Assert.That(sign, Is.Zero);
        }
    }
}
