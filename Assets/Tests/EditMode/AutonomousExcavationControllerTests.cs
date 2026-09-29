using NUnit.Framework;
using System.Reflection;
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

        /// <summary>Expose whether normal cycles fall back to the full-bucket lift-then-close recovery.</summary>
        [Test]
        public void NewController_TracksFullBucketRecoveryCount()
        {
            PropertyInfo recoveryCount = typeof(AutonomousExcavationController).GetProperty(
                "FullBucketRecoveryCount", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(recoveryCount, Is.Not.Null,
                "The HMI test diagnostics must reveal when a cycle uses the exceptional full-bucket recovery.");

            GameObject root = new GameObject("autonomous");
            try
            {
                var controller = root.AddComponent<AutonomousExcavationController>();
                Assert.That((int)recoveryCount.GetValue(controller), Is.Zero);
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

        /// <summary>预测当前土量在铲斗安全收拢前按实测进料速率继续增长的比例。</summary>
        [Test]
        public void PredictScoopFillFraction_IncludesMaterialCollectedDuringCurl()
        {
            MethodInfo predict = typeof(AutonomousExcavationController).GetMethod(
                "PredictScoopFillFraction", BindingFlags.Public | BindingFlags.Static);
            Assert.That(predict, Is.Not.Null,
                "The scoop controller must expose its bounded close-time fill prediction.");

            object result = predict.Invoke(null, new object[]
            {
                .12, .30, .06, 30f, 30f, 30f
            });

            Assert.That((float)result, Is.EqualTo(.60f).Within(.001f));
        }

        /// <summary>限制预测收斗速度并忽略异常负进料速率，避免闭斗时间发散。</summary>
        [Test]
        public void PredictScoopFillFraction_BoundsCurlSpeedAndIgnoresNegativeInflow()
        {
            MethodInfo predict = typeof(AutonomousExcavationController).GetMethod(
                "PredictScoopFillFraction", BindingFlags.Public | BindingFlags.Static);
            Assert.That(predict, Is.Not.Null);

            float bounded = (float)predict.Invoke(null, new object[]
                { .10, .30, .10, 30f, 100f, 40f });
            float decreasing = (float)predict.Invoke(null, new object[]
                { .20, .30, -.10, 30f, 20f, 40f });

            Assert.That(bounded, Is.EqualTo(.583333f).Within(.001f));
            Assert.That(decreasing, Is.EqualTo(2f / 3f).Within(.001f));
        }

        /// <summary>土量采样窗口内体积下降或时间异常时，不会制造负向进料速度。</summary>
        [Test]
        public void EstimateScoopInflowRate_ClampsNegativeAndInvalidSamples()
        {
            MethodInfo estimate = typeof(AutonomousExcavationController).GetMethod(
                "EstimateScoopInflowRate", BindingFlags.Public | BindingFlags.Static);
            Assert.That(estimate, Is.Not.Null);

            double increasing = (double)estimate.Invoke(null, new object[] { .04, .10, .30f });
            double decreasing = (double)estimate.Invoke(null, new object[] { .10, .04, .30f });
            double invalidWindow = (double)estimate.Invoke(null, new object[] { .04, .10, 0f });

            Assert.That(increasing, Is.EqualTo(.2).Within(.0001));
            Assert.That(decreasing, Is.Zero);
            Assert.That(invalidWindow, Is.Zero);
        }

        /// <summary>Thin local soil reduces entry depth to preserve the effective floor and contact margins.</summary>
        [Test]
        public void CalculateAdaptiveCutEntryDepth_RespectsLocalLayerAndFloorClearance()
        {
            MethodInfo calculate = typeof(AutonomousExcavationController).GetMethod(
                "CalculateAdaptiveCutEntryDepth", BindingFlags.Public | BindingFlags.Static);
            Assert.That(calculate, Is.Not.Null,
                "Cut entry must be bounded by measured local soil thickness, not only a fixed plunge depth.");

            float thick = (float)calculate.Invoke(null, new object[] { .12f, .5f, .045f });
            float thin = (float)calculate.Invoke(null, new object[] { .12f, .15f, .045f });
            float insufficient = (float)calculate.Invoke(null, new object[] { .12f, .04f, .045f });

            Assert.That(thick, Is.EqualTo(.12f).Within(.0001f));
            Assert.That(thin, Is.EqualTo(.105f).Within(.0001f));
            Assert.That(insufficient, Is.Zero);
        }

        /// <summary>满度或闭斗前预测满度增加时，提升收斗权重并减少斗杆推进。</summary>
        [Test]
        public void CalculateAdaptiveScoopMotionCommand_CurlsEarlierAsProjectedLoadRises()
        {
            MethodInfo calculate = typeof(AutonomousExcavationController).GetMethod(
                "CalculateAdaptiveScoopMotionCommand", BindingFlags.Public | BindingFlags.Static);
            Assert.That(calculate, Is.Not.Null,
                "The controller must compute continuous arm, curl and lift weights from fill feedback.");

            object early = calculate.Invoke(null, new object[] { .10f, .10f });
            object predicted = calculate.Invoke(null, new object[] { .30f, .60f });
            object nearFull = calculate.Invoke(null, new object[] { .78f, .88f });
            float earlyArm = ReadCommandScale(early, "armAdvanceScale");
            float earlyCurl = ReadCommandScale(early, "bucketCurlScale");
            float predictedArm = ReadCommandScale(predicted, "armAdvanceScale");
            float predictedCurl = ReadCommandScale(predicted, "bucketCurlScale");
            float nearFullArm = ReadCommandScale(nearFull, "armAdvanceScale");
            float nearFullCurl = ReadCommandScale(nearFull, "bucketCurlScale");

            Assert.That(earlyArm, Is.GreaterThan(predictedArm));
            Assert.That(predictedArm, Is.GreaterThan(nearFullArm));
            Assert.That(earlyCurl, Is.LessThan(predictedCurl));
            Assert.That(predictedCurl, Is.LessThanOrEqualTo(nearFullCurl));
            Assert.That(nearFullArm, Is.Zero);
            Assert.That(nearFullCurl, Is.InRange(1f, 1.25f));
        }

        /// <summary>Choose a bucket joint angle that points the real mouth normal upward.</summary>
        [Test]
        public void CalculateBucketAngleForUpwardMouth_UsesWorldAxisAndJointLimits()
        {
            MethodInfo calculate = typeof(AutonomousExcavationController).GetMethod(
                "CalculateBucketAngleForUpwardMouth", BindingFlags.Public | BindingFlags.Static);
            Assert.That(calculate, Is.Not.Null,
                "Carry angle must be solved from the current world mouth direction, not a fixed curl-pose offset.");

            float angle = (float)calculate.Invoke(null, new object[]
            {
                Vector3.forward, Vector3.right, 0f, -120f, 120f
            });
            Vector3 resultingMouth = Quaternion.AngleAxis(angle, Vector3.right) * Vector3.forward;

            Assert.That(angle, Is.EqualTo(-90f).Within(1f));
            Assert.That(Vector3.Dot(resultingMouth.normalized, Vector3.up), Is.GreaterThan(.99f));
            Assert.That(angle, Is.InRange(-120f, 120f));
        }

        private static float ReadCommandScale(object command, string fieldName)
        {
            FieldInfo field = command.GetType().GetField(fieldName);
            Assert.That(field, Is.Not.Null, $"Adaptive scoop command is missing {fieldName}.");
            return (float)field.GetValue(command);
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
