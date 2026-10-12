using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class Zx200DigControllerTests
    {
        private GameObject root;
        private Zx200DigController controller;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("zx200");
            ArticulationBody track = CreateJoint("track_link", 0f, 0f, root.transform);
            ArticulationBody swing = CreateJoint("body_link", -179f, 179f, track.transform);
            ArticulationBody boom = CreateJoint(
                "boom_link", -65.1453f, -5.729578f, swing.transform);
            ArticulationBody arm = CreateJoint(
                "arm_link", 34.033695f, 153.95377f, boom.transform);
            CreateJoint("bucket_link", -35.637974f, 135.9056f, arm.transform);
            root.AddComponent<Zx200FixedBasePhysics>();
            controller = root.AddComponent<Zx200DigController>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        /// <summary>Manual cutting remains disabled during site preparation, but works outside work-ready once ready.</summary>
        /// <param name="sitePreparing">Whether an actual construction-site component is still unready.</param>
        [TestCase(false)]
        [TestCase(true)]
        public void ManualCuttingUnlockRequiresReadySiteAndCutsRealTerrain(bool sitePreparing)
        {
            controller.ResolveJoints();
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++) heights[z, x] = .5f;
            data.SetHeights(0, 0, heights);
            GameObject ground = Terrain.CreateTerrainGameObject(data);
            GameObject siteRoot = null;
            try
            {
                if (sitePreparing)
                {
                    siteRoot = new GameObject("Unready construction site");
                    siteRoot.AddComponent<CesiumMoundSite>().enabled = false;
                    ground.transform.SetParent(siteRoot.transform);
                }
                var soil = ground.AddComponent<DeformableSoilTerrain>();
                var cutter = controller.bucketJoint.gameObject.AddComponent<BucketSoilCutter>();
                cutter.soil = soil;
                cutter.tipLocalPosition = Vector3.zero;
                cutter.cutDirectionLocal = Vector3.forward;
                cutter.transform.position = new Vector3(2, .9f, 1.5f);
                cutter.SamplePose();
                Assert.That(cutter.StartupReady, Is.False);
                controller.AdjustManualPose(0, 0, 0, 0);
                cutter.SamplePose();
                Assert.That(cutter.StartupReady, Is.False);
                Assert.That(controller.AdjustManualPose(0, 0, 1f, 0), Is.True);
                cutter.SamplePose();
                Assert.That(cutter.StartupReady, Is.EqualTo(!sitePreparing));
                cutter.transform.position += Vector3.forward * .3f;
                cutter.SamplePose();
                if (sitePreparing)
                    Assert.That(cutter.ExcavatedVolumeM3, Is.Zero);
                else
                {
                    Assert.That(cutter.VolumeM3, Is.GreaterThan(.001));
                    Assert.That(soil.RecalculateRemovedVolumeM3(),
                        Is.EqualTo(cutter.ExcavatedVolumeM3).Within(.00001));
                }
            }
            finally
            {
                Object.DestroyImmediate(controller.bucketJoint.GetComponent<BucketSoilCutter>());
                Object.DestroyImmediate(ground);
                if (siteRoot != null) Object.DestroyImmediate(siteRoot);
                Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void Defaults_UseNormalWorkReadyPose()
        {
            Assert.That(controller.workReadyPose.swing, Is.EqualTo(0f));
            Assert.That(controller.workReadyPose.boom, Is.EqualTo(-48f));
            Assert.That(controller.workReadyPose.arm, Is.EqualTo(68f));
            Assert.That(controller.workReadyPose.bucket, Is.EqualTo(25f));
        }

        [Test]
        public void Defaults_KeepCompactParkedPoseSeparateFromCuttingReadiness()
        {
            FieldInfo parkedField = typeof(Zx200DigController).GetField("parkedPose");
            Assert.That(parkedField, Is.Not.Null,
                "The startup/park pose must not reuse the articulation's cutting-ready pose.");
            Zx200JointPose parked = (Zx200JointPose)parkedField.GetValue(controller);

            Assert.That(parked.swing, Is.EqualTo(0f));
            Assert.That(parked.arm, Is.GreaterThan(controller.workReadyPose.arm + 15f),
                "The parked stick must visibly retract without changing the cutting-ready trajectory.");
            Assert.That(parked.bucket, Is.GreaterThan(controller.workReadyPose.bucket + 10f),
                "The parked bucket must be tucked inward rather than hanging open.");
            Assert.That(controller.workReadyPose.boom, Is.EqualTo(-48f),
                "The work-ready preset remains independent for the cutter's settled-pose gate.");
        }

        [Test]
        public void ApplyParkedPose_ChangesDriveTargetsWithoutOverwritingSolverPositions()
        {
            FieldInfo parkedField = typeof(Zx200DigController).GetField("parkedPose");
            MethodInfo applyParkedPose = typeof(Zx200DigController).GetMethod("ApplyParkedPose");
            Assert.That(parkedField, Is.Not.Null);
            Assert.That(applyParkedPose, Is.Not.Null);
            controller.ResolveJoints();
            Zx200JointPose parked = (Zx200JointPose)parkedField.GetValue(controller);
            Zx200JointPose before = controller.ReadActualPose();

            applyParkedPose.Invoke(controller, null);

            Assert.That(controller.swingJoint.xDrive.target, Is.EqualTo(parked.swing));
            Assert.That(controller.boomJoint.xDrive.target, Is.EqualTo(parked.boom));
            Assert.That(controller.armJoint.xDrive.target, Is.EqualTo(parked.arm));
            Assert.That(controller.bucketJoint.xDrive.target, Is.EqualTo(parked.bucket));
            Zx200JointPose after = controller.ReadActualPose();
            Assert.That(after.swing, Is.EqualTo(before.swing));
            Assert.That(after.boom, Is.EqualTo(before.boom));
            Assert.That(after.arm, Is.EqualTo(before.arm));
            Assert.That(after.bucket, Is.EqualTo(before.bucket));
        }

        [Test]
        public void Defaults_UseCalibratedDigAndRightDumpTrajectory()
        {
            Assert.That(controller.approachPose.boom, Is.EqualTo(-60f));
            Assert.That(controller.curlPose.arm, Is.EqualTo(92f));
            Assert.That(controller.curlPose.bucket, Is.EqualTo(105f));
            Assert.That(controller.liftPose.boom, Is.EqualTo(-25f));
            Assert.That(controller.swingToDumpPose.swing, Is.EqualTo(60f));
            Assert.That(controller.dumpPreparePose.bucket, Is.EqualTo(80f));
            Assert.That(controller.dumpPose.bucket, Is.EqualTo(-25f));
            Assert.That(controller.curlHoldDuration, Is.EqualTo(0.25f));
            Assert.That(controller.dumpHoldDuration, Is.EqualTo(0.35f));
        }

        [Test]
        public void ResolveJoints_FindsAllFourNamedArticulations()
        {
            bool resolved = controller.ResolveJoints();

            Assert.That(resolved, Is.True);
            Assert.That(controller.swingJoint.name, Is.EqualTo("body_link"));
            Assert.That(controller.boomJoint.name, Is.EqualTo("boom_link"));
            Assert.That(controller.armJoint.name, Is.EqualTo("arm_link"));
            Assert.That(controller.bucketJoint.name, Is.EqualTo("bucket_link"));
        }

        [TestCase(-100f, -65f, -5f, -65f)]
        [TestCase(20f, -65f, -5f, -5f)]
        [TestCase(-35f, -65f, -5f, -35f)]
        public void ClampTarget_RestrictsTargetToDriveLimits(
            float target, float lower, float upper, float expected)
        {
            Assert.That(Zx200DigController.ClampTarget(target, lower, upper),
                Is.EqualTo(expected));
        }

        [Test]
        public void ApplyWorkReadyPose_UpdatesEveryDriveWithinLimits()
        {
            controller.ResolveJoints();

            controller.ApplyWorkReadyPose();

            Assert.That(controller.swingJoint.xDrive.target, Is.EqualTo(0f));
            Assert.That(controller.boomJoint.xDrive.target, Is.EqualTo(-48f));
            Assert.That(controller.armJoint.xDrive.target, Is.EqualTo(68f));
            Assert.That(controller.bucketJoint.xDrive.target, Is.EqualTo(25f));
        }

        [Test]
        public void ApplyWorkReadyPose_DoesNotOverwriteSolverJointPositions()
        {
            controller.ResolveJoints();
            Zx200JointPose before = controller.ReadActualPose();

            controller.ApplyWorkReadyPose();

            Zx200JointPose after = controller.ReadActualPose();
            Assert.That(after.swing, Is.EqualTo(before.swing));
            Assert.That(after.boom, Is.EqualTo(before.boom));
            Assert.That(after.arm, Is.EqualTo(before.arm));
            Assert.That(after.bucket, Is.EqualTo(before.bucket));
        }

        [TestCase(-1f, 0f)]
        [TestCase(0.5f, 0.5f)]
        [TestCase(2f, 1f)]
        public void SmoothProgress_ClampsAndSmooths(float input, float expected)
        {
            Assert.That(Zx200DigController.SmoothProgress(input),
                Is.EqualTo(expected).Within(0.0001f));
        }

        [Test]
        public void PrepareForPhysics_KeepsGravityAndCollidersEnabled()
        {
            BoxCollider collider = root.AddComponent<BoxCollider>();

            bool prepared = controller.PrepareForPhysics();

            Assert.That(prepared, Is.True);
            Assert.That(controller.fixedBasePhysics.rootBody.immovable, Is.True);
            Assert.That(controller.swingJoint.useGravity, Is.True);
            Assert.That(controller.boomJoint.useGravity, Is.True);
            Assert.That(controller.armJoint.useGravity, Is.True);
            Assert.That(controller.bucketJoint.useGravity, Is.True);
            Assert.That(collider.enabled, Is.True);
        }

        [Test]
        public void PrepareForPhysics_UnlocksZeroRangeSwingJoint()
        {
            controller.ResolveJoints();
            ArticulationDrive drive = controller.swingJoint.xDrive;
            drive.lowerLimit = 0f;
            drive.upperLimit = 0f;
            controller.swingJoint.xDrive = drive;
            controller.swingJoint.twistLock = ArticulationDofLock.LockedMotion;

            bool ready = controller.PrepareForPhysics();

            Assert.That(ready, Is.True);
            Assert.That(controller.swingJoint.twistLock,
                Is.EqualTo(ArticulationDofLock.LimitedMotion));
            Assert.That(controller.swingJoint.xDrive.lowerLimit, Is.LessThan(-70f));
            Assert.That(controller.swingJoint.xDrive.upperLimit, Is.GreaterThan(70f));
        }

        [TestCase("stiffness", float.NaN)]
        [TestCase("damping", float.PositiveInfinity)]
        [TestCase("forceLimit", 0f)]
        [TestCase("jointToleranceDegrees", -0.1f)]
        [TestCase("settleTimeoutSeconds", 0f)]
        public void PrepareForPhysics_RejectsInvalidDriveSetting(string fieldName, float value)
        {
            typeof(Zx200DigController).GetField(fieldName)?.SetValue(controller, value);

            Assert.That(controller.PrepareForPhysics(), Is.False, fieldName);
        }

        [Test]
        public void PrepareForPhysics_RejectsNonFinitePoseTarget()
        {
            controller.workReadyPose = new Zx200JointPose(float.NaN, -48f, 68f, 25f, 1f);

            Assert.That(controller.PrepareForPhysics(), Is.False);
        }

        [Test]
        public void PrepareForPhysics_RejectsNonFiniteParkedPose()
        {
            FieldInfo parkedField = typeof(Zx200DigController).GetField("parkedPose");
            Assert.That(parkedField, Is.Not.Null);
            parkedField.SetValue(controller, new Zx200JointPose(0f, float.NaN, 100f, 45f, 1f));

            Assert.That(controller.PrepareForPhysics(), Is.False);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(-0.1f)]
        public void PrepareForPhysics_RejectsInvalidStageDuration(float duration)
        {
            controller.approachPose = new Zx200JointPose(0f, -60f, 52f, 5f, duration);

            Assert.That(controller.PrepareForPhysics(), Is.False);
        }

        [Test]
        public void StartAutomaticPose_RejectsRequestsOutsidePlayMode()
        {
            controller.ResolveJoints();
            Assert.That(controller.StartAutomaticPose(controller.workReadyPose), Is.False);
            Assert.That(controller.IsBusy, Is.False);
        }

        [Test]
        public void StartAutomaticPose_RejectsInvalidTarget()
        {
            controller.ResolveJoints();
            Zx200JointPose invalid = new Zx200JointPose(float.NaN, -48f, 68f, 25f, 1f);

            Assert.That(controller.StartAutomaticPose(invalid), Is.False);
            Assert.That(controller.LastAutomaticMotionSucceeded, Is.False);
        }

        [Test]
        public void StopAutomaticMotion_IsSafeWhenIdle()
        {
            controller.StopAutomaticMotion();

            Assert.That(controller.IsBusy, Is.False);
            Assert.That(controller.LastAutomaticMotionSucceeded, Is.False);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(-0.1f)]
        public void PrepareForPhysics_RejectsInvalidHoldDuration(float duration)
        {
            controller.curlHoldDuration = duration;

            Assert.That(controller.PrepareForPhysics(), Is.False);
        }

        [TestCase(10f, 10.4f, 0.5f, true)]
        [TestCase(10f, 10.6f, 0.5f, false)]
        [TestCase(-179f, 179f, 3f, true)]
        public void IsAngleWithinTolerance_UsesShortestAngularDistance(
            float actual, float target, float tolerance, bool expected)
        {
            Assert.That(Zx200DigController.IsAngleWithinTolerance(actual, target, tolerance),
                Is.EqualTo(expected));
        }

        [Test]
        public void IsPoseWithinTolerance_UsesDriveLimitedTarget()
        {
            controller.ResolveJoints();
            SetLimits(controller.swingJoint, -10f, 10f);
            SetLimits(controller.boomJoint, -10f, 10f);
            SetLimits(controller.armJoint, -10f, 10f);
            SetLimits(controller.bucketJoint, -10f, 10f);

            bool reached = controller.IsPoseWithinTolerance(
                new Zx200JointPose(20f, 0f, 0f, 0f, 1f), 10.1f);

            Assert.That(reached, Is.True);
        }

        [Test]
        public void AnimateTo_ZeroDurationStillWaitsForSolverConvergence()
        {
            controller.ResolveJoints();
            MethodInfo animateTo = typeof(Zx200DigController).GetMethod(
                "AnimateTo", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(animateTo, Is.Not.Null);
            var movement = animateTo.Invoke(
                controller,
                new object[] { new Zx200JointPose(20f, -48f, 68f, 25f, 0f) }) as IEnumerator;

            Assert.That(movement, Is.Not.Null);
            Assert.That(movement.MoveNext(), Is.True);
            Assert.That(movement.Current, Is.TypeOf<WaitForFixedUpdate>());
        }

        [Test]
        public void AdjustManualPose_AppliesSimultaneousClampedDeltas()
        {
            controller.ResolveJoints();
            controller.ApplyWorkReadyPose();

            bool changed = controller.AdjustManualPose(10f, -30f, 20f, -100f);

            Assert.That(changed, Is.True);
            Assert.That(controller.swingJoint.xDrive.target, Is.EqualTo(10f));
            Assert.That(controller.boomJoint.xDrive.target, Is.EqualTo(-65.1453f).Within(0.001f));
            Assert.That(controller.armJoint.xDrive.target, Is.EqualTo(88f));
            Assert.That(controller.bucketJoint.xDrive.target, Is.EqualTo(-35.637974f).Within(0.001f));
        }

        private static ArticulationBody CreateJoint(
            string name, float lower, float upper, Transform parent)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent);
            ArticulationBody body = child.AddComponent<ArticulationBody>();
            if (parent.GetComponentInParent<ArticulationBody>() != null)
            {
                body.jointType = ArticulationJointType.RevoluteJoint;
            }
            ArticulationDrive drive = body.xDrive;
            drive.lowerLimit = lower;
            drive.upperLimit = upper;
            body.xDrive = drive;
            return body;
        }

        private static void SetLimits(ArticulationBody body, float lower, float upper)
        {
            ArticulationDrive drive = body.xDrive;
            drive.lowerLimit = lower;
            drive.upperLimit = upper;
            body.xDrive = drive;
        }
    }
}
