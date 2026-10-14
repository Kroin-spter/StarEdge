using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class SoilResistanceTests
    {
        /// <summary>Tooth-led contact in soft soil gets finite advance instead of a zero-motion deadlock.</summary>
        [Test]
        public void ManualToothContactCanAdvanceWithShallowShellContact()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = .5f;
            data.SetHeights(0, 0, heights);
            var ground = Terrain.CreateTerrainGameObject(data);
            var rig = new GameObject("soft contact fixture");
            var bucket = new GameObject("tooth-led bucket");
            try
            {
                var soil = ground.AddComponent<DeformableSoilTerrain>();
                var cutter = bucket.AddComponent<BucketSoilCutter>();
                cutter.soil = soil;
                cutter.tipLocalPosition = new Vector3(0, -.08f, .2f);
                cutter.cutDirectionLocal = Vector3.down;
                bucket.transform.position = new Vector3(2, 1.08f, 2);
                var shell = bucket.AddComponent<BoxCollider>();
                shell.center = new Vector3(0, -.06f, 0);
                shell.size = new Vector3(.4f, .12f, .3f);
                var resistance = bucket.AddComponent<BucketSoilResistance>();
                var machine = rig.AddComponent<Zx200DigController>();
                var pivot = new GameObject("arm");
                pivot.transform.SetParent(rig.transform);
                pivot.transform.position = bucket.transform.position - Vector3.forward;
                machine.armJoint = pivot.AddComponent<ArticulationBody>();
                machine.armJoint.matchAnchors = false;
                machine.armJoint.anchorRotation = Quaternion.identity;
                var result = resistance.ConstrainPose(machine, new Zx200JointPose(0, 0, .1f, 0, 0), .02f);
                Assert.That(result.arm, Is.GreaterThan(.08f), "Effective tooth contact must get finite soft-soil advance.");
                Assert.That(soil.RecalculateRemovedVolumeM3(), Is.Zero, "A target prediction must never fabricate excavation.");
                var drive = machine.armJoint.xDrive;
                drive.target = .5f; // Existing spring deflection supporting the arm's weight.
                machine.armJoint.xDrive = drive;
                result = resistance.ConstrainPose(machine, new Zx200JointPose(0, 0, .6f, 0, 0), .02f);
                Assert.That(result.arm, Is.GreaterThan(.5f), "Cutting speed limits must advance the drive, not erase its gravity-support offset.");
            }
            finally
            {
                Object.DestroyImmediate(bucket);
                Object.DestroyImmediate(rig);
                Object.DestroyImmediate(ground);
                Object.DestroyImmediate(data);
            }
        }

        /// <summary>Protect the shell even when the tooth is above the soil, without locking reverse withdrawal.</summary>
        [Test]
        public void ManualShellContactLimitsDeeperMotionWithAirborneTipAndAllowsWithdrawal()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++) heights[z, x] = .5f;
            data.SetHeights(0, 0, heights);
            var ground = Terrain.CreateTerrainGameObject(data);
            var rig = new GameObject("manual shell guard fixture");
            var bucket = new GameObject("bucket with buried rear shell");
            try
            {
                var soil = ground.AddComponent<DeformableSoilTerrain>();
                var cutter = bucket.AddComponent<BucketSoilCutter>();
                cutter.soil = soil;
                cutter.tipLocalPosition = Vector3.zero;
                bucket.transform.position = new Vector3(2, 1.3f, 2);
                var shell = bucket.AddComponent<BoxCollider>();
                shell.center = new Vector3(0, -.2f, -.3f);
                shell.size = new Vector3(.4f, .4f, .4f);
                var resistance = bucket.AddComponent<BucketSoilResistance>();
                var machine = rig.AddComponent<Zx200DigController>();
                var pivot = new GameObject("arm");
                pivot.transform.SetParent(rig.transform);
                pivot.transform.position = bucket.transform.position - Vector3.forward;
                machine.armJoint = pivot.AddComponent<ArticulationBody>();
                machine.armJoint.matchAnchors = false;
                machine.armJoint.anchorRotation = Quaternion.identity;
                machine.armJoint.anchorPosition = Vector3.zero;
                var intoSoil = resistance.ConstrainPose(machine, new Zx200JointPose(0, 0, 1, 0, 0), .02f);
                Assert.That(intoSoil.arm, Is.LessThan(.02f), "Airborne teeth do not authorize pushing the buried shell deeper.");
                var withdrawal = resistance.ConstrainPose(machine, new Zx200JointPose(0, 0, -1, 0, 0), .02f);
                Assert.That(withdrawal.arm, Is.EqualTo(-1).Within(.001), "Do not lock an escape direction.");
                var boomPivot = new GameObject("boom withdrawal pivot");
                boomPivot.transform.SetParent(rig.transform);
                boomPivot.transform.position = bucket.transform.position + Vector3.forward;
                machine.boomJoint = boomPivot.AddComponent<ArticulationBody>();
                machine.boomJoint.matchAnchors = false;
                machine.boomJoint.anchorRotation = Quaternion.identity;
                machine.boomJoint.anchorPosition = Vector3.zero;
                var mixed = resistance.ConstrainPose(machine, new Zx200JointPose(0, .5f, 1, 0, 0), .02f);
                Assert.That(mixed.boom, Is.GreaterThan(.45f), "An unsafe arm input must not erase the safe boom withdrawal.");
                Assert.That(mixed.arm, Is.LessThan(1), "The penetrating component still needs limiting.");
                Assert.That(soil.RecalculateRemovedVolumeM3(), Is.Zero, "The shell guard must not excavate on behalf of the blade.");
            }
            finally
            {
                Object.DestroyImmediate(bucket);
                Object.DestroyImmediate(rig);
                Object.DestroyImmediate(ground);
                Object.DestroyImmediate(data);
            }
        }

        /// <summary>Faster measured closing motion must move the floor-braking trigger farther from the floor.</summary>
        [Test]
        public void VelocityBrakeTriggerClearance_IncreasesWithMeasuredTipClosingSpeed()
        {
            var calculate = typeof(BucketSoilResistance).GetMethod(
                "CalculateVelocityBrakeTriggerClearance");
            Assert.That(calculate, Is.Not.Null,
                "The floor brake must include measured tooth stopping distance in its activation threshold.");

            float stationary = (float)calculate.Invoke(null, new object[] { .075f, 0f, .1f });
            float closing = (float)calculate.Invoke(null, new object[] { .075f, .5f, .1f });

            Assert.That(stationary, Is.EqualTo(.075f).Within(.0001f));
            Assert.That(closing, Is.EqualTo(.125f).Within(.0001f));
            Assert.That(closing, Is.GreaterThan(stationary));
        }

        [TestCase(.808f, false, 0f)]
        [TestCase(.808f, false, .2f)]
        [TestCase(.808f, false, 1.25f)]
        [TestCase(.9f, true, 0f)]
        public void ArmCutIsNotLockedByExistingDepthOrAnotherJoint(float tipY, bool rejectedSwing, float forwardOffset)
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33,33];
            for (int z=0; z<33; z++) for(int x=0; x<33; x++) heights[z,x] = .5f;
            data.SetHeights(0,0,heights);
            var ground = Terrain.CreateTerrainGameObject(data);
            var rig = new GameObject("guard command fixture");
            var bucket = new GameObject("blade");
            try
            {
                var soil = ground.AddComponent<DeformableSoilTerrain>();
                var cutter = bucket.AddComponent<BucketSoilCutter>();
                cutter.soil = soil; cutter.tipLocalPosition = Vector3.zero;
                cutter.cutDirectionLocal = Vector3.forward;
                bucket.transform.position = new Vector3(2, tipY, 2);
                var resistance = bucket.AddComponent<BucketSoilResistance>();
                var machine = rig.AddComponent<Zx200DigController>();
                var arm = new GameObject("arm pivot"); arm.transform.SetParent(rig.transform);
                arm.transform.position = bucket.transform.position - Vector3.up * 2 - Vector3.forward * forwardOffset;
                machine.armJoint = arm.AddComponent<ArticulationBody>();
                machine.armJoint.anchorRotation = Quaternion.identity;
                var swing = new GameObject("swing pivot"); swing.transform.SetParent(rig.transform);
                swing.transform.position = bucket.transform.position - Vector3.right;
                machine.swingJoint = swing.AddComponent<ArticulationBody>();
                machine.swingJoint.anchorRotation = Quaternion.Euler(0,0,90);
                var request = new Zx200JointPose(rejectedSwing ? .1f : 0, 0, .1f, 0, 0);
                var result = resistance.ConstrainPose(machine, request, .02f);
                Assert.That(result.arm, Is.GreaterThan(.05f), "A forward, nearly level tooth sweep must remain possible");
                if (rejectedSwing) Assert.That(result.swing, Is.Zero, "Back scraping is still rejected independently");
                // Downward motion must still be held once already too deep.
                bucket.transform.position = new Vector3(2,.808f,2);
                arm.transform.position = bucket.transform.position - Vector3.forward;
                result = resistance.ConstrainPose(machine, request, .02f);
                Assert.That(result.arm, Is.Zero, "Existing excess depth cannot authorize further downward penetration");
            }
            finally
            {
                Object.DestroyImmediate(bucket); Object.DestroyImmediate(rig);
                Object.DestroyImmediate(ground); Object.DestroyImmediate(data);
            }
        }

        [TestCase(-.2f)]
        [TestCase(0f)]
        public void AtSlopeFloorArmWithdrawalCanIncreaseLocalClearanceWithoutRaisingWorldHeight(float boomWithdrawal)
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var surface = new float[33, 33];
            var groundFloor = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++)
                {
                    surface[z, x] = .85f;
                    groundFloor[z, x] = .7f - .1f * z / 32f;
                }
            data.SetHeights(0, 0, surface);
            var ground = Terrain.CreateTerrainGameObject(data);
            var rig = new GameObject("slope floor guard fixture");
            var bucket = new GameObject("blade");
            try
            {
                var soil = ground.AddComponent<DeformableSoilTerrain>();
                soil.PrepareMoundSurface(surface, groundFloor);
                var cutter = bucket.AddComponent<BucketSoilCutter>();
                cutter.soil = soil;
                cutter.tipLocalPosition = Vector3.zero;
                cutter.cutDirectionLocal = Vector3.forward;
                bucket.transform.position = new Vector3(2,
                    boomWithdrawal == 0f ? 1.36f : 1.31f, 2);
                var resistance = bucket.AddComponent<BucketSoilResistance>();
                var machine = rig.AddComponent<Zx200DigController>();
                var boom = new GameObject("boom pivot");
                boom.transform.SetParent(rig.transform);
                boom.transform.position = bucket.transform.position - Vector3.forward;
                machine.boomJoint = boom.AddComponent<ArticulationBody>();
                machine.boomJoint.matchAnchors = false;
                machine.boomJoint.anchorPosition = Vector3.zero;
                machine.boomJoint.anchorRotation = Quaternion.identity;
                ArticulationDrive boomDrive = machine.boomJoint.xDrive;
                boomDrive.lowerLimit = -30f;
                boomDrive.upperLimit = 30f;
                machine.boomJoint.xDrive = boomDrive;
                var arm = new GameObject("arm pivot");
                arm.transform.SetParent(rig.transform);
                arm.transform.position = bucket.transform.position - Vector3.up * 2;
                machine.armJoint = arm.AddComponent<ArticulationBody>();
                machine.armJoint.matchAnchors = false;
                machine.armJoint.anchorPosition = Vector3.zero;
                machine.armJoint.anchorRotation = Quaternion.identity;
                machine.bucketJoint = bucket.AddComponent<ArticulationBody>();
                var requested = new Zx200JointPose(0, boomWithdrawal, .3f, 0, 0);
                Vector3 tip = cutter.TipWorldPosition;
                Vector3 pivot = arm.transform.TransformPoint(machine.armJoint.anchorPosition);
                Vector3 axis = arm.transform.TransformDirection(machine.armJoint.anchorRotation * Vector3.right);
                Vector3 advancedTip = pivot + Quaternion.AngleAxis(.3f, axis) * (tip - pivot);
                Assert.That(advancedTip.y, Is.LessThan(tip.y));
                Assert.That(advancedTip.y - soil.GetFloorWorldY(advancedTip),
                    Is.GreaterThan(tip.y - soil.GetFloorWorldY(tip)));
                var result = resistance.ConstrainPose(machine, requested, .02f);
                Assert.That(result.arm, Is.GreaterThan(.05f),
                    "A shallow downward arm sweep toward a lower floor is a safe withdrawal, not a jam");
                if (boomWithdrawal == 0f)
                    Assert.That(result.boom, Is.Zero.Within(.01f),
                        "Stationary tooth above the floor must not trigger an unsolicited boom lift");
            }
            finally
            {
                Object.DestroyImmediate(bucket);
                Object.DestroyImmediate(rig);
                Object.DestroyImmediate(ground);
                Object.DestroyImmediate(data);
            }
        }

        /// <summary>Depth/floor limits use actual terrain heights; lifting out clears contact without modifying soil.</summary>
        [Test]
        public void ContactReportsDepthAndFloorAndClearsAboveSurface()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33,33];
            for (int z=0; z<33; z++) for(int x=0; x<33; x++) heights[z,x] = .5f;
            data.SetHeights(0,0,heights);
            var ground = Terrain.CreateTerrainGameObject(data);
            var bucket = new GameObject("contact probe");
            try
            {
                var soil = ground.AddComponent<DeformableSoilTerrain>();
                var cutter = bucket.AddComponent<BucketSoilCutter>();
                cutter.soil = soil;
                cutter.tipLocalPosition = Vector3.zero;
                var resistance = bucket.AddComponent<BucketSoilResistance>();
                bucket.transform.position = new Vector3(2,.9f,2);
                resistance.EvaluateContact();
                Assert.That(resistance.IsContacting, Is.True);
                Assert.That(resistance.IsBlocked, Is.False);
                Assert.That(resistance.PenetrationM, Is.EqualTo(.1f).Within(.001));
                bucket.transform.position = new Vector3(2,.7f,2);
                resistance.EvaluateContact();
                Assert.That(resistance.IsBlocked, Is.True);
                StringAssert.Contains("切深", resistance.BlockReason);
                bucket.transform.position = new Vector3(2,.04f,2);
                resistance.EvaluateContact();
                StringAssert.Contains("底部", resistance.BlockReason);
                bucket.transform.position = new Vector3(2,2,2);
                resistance.EvaluateContact();
                Assert.That(resistance.IsBlocked || resistance.IsContacting, Is.False);
                bucket.transform.position = new Vector3(8,0,8);
                resistance.EvaluateContact();
                Assert.That(resistance.IsContacting, Is.False);
                Assert.That(soil.RecalculateRemovedVolumeM3(), Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(bucket);
                Object.DestroyImmediate(ground);
                Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void EarthPressureResistanceIsContinuousWithDepthAndBounded()
        {
            var bucket = new GameObject("soil force estimate");
            try
            {
                bucket.AddComponent<BucketSoilCutter>();
                var resistance = bucket.AddComponent<BucketSoilResistance>();
                var estimate = typeof(BucketSoilResistance).GetMethod("EstimateResistanceForce");
                Assert.That(estimate, Is.Not.Null, "Expose the depth/strength model for independent validation");
                float Force(float depth, float speed, float alignment) =>
                    (float)estimate.Invoke(resistance, new object[] { depth, speed, alignment });
                Assert.That(Force(0, 1, 1), Is.Zero);
                Assert.That(Force(.1f,.1f,0), Is.GreaterThan(0), "Vertical/shearing contacts cannot vanish at a mouth-angle threshold");
                Assert.That(Force(.12f,.1f,.5f), Is.GreaterThan(Force(.06f,.1f,.5f)));
                Assert.That(Mathf.Abs(Force(.1f,.1f,.44f) - Force(.1f,.1f,.46f)), Is.LessThan(100));
                Assert.That(Force(10,100,1), Is.LessThanOrEqualTo(resistance.maximumForceN));
            }
            finally { Object.DestroyImmediate(bucket); }
        }

        [Test]
        public void CuttingInteractionExposesControllerFeedback()
        {
            var type = typeof(BucketSoilCutter).Assembly.GetType("BucketSoilResistance");
            Assert.That(type, Is.Not.Null, "Cutting still has no resistance or penetration feedback");
            Assert.That(type.GetProperty("PenetrationM"), Is.Not.Null);
            Assert.That(type.GetProperty("ResistanceForceN"), Is.Not.Null);
            Assert.That(type.GetProperty("IsBlocked"), Is.Not.Null);
            Assert.That(type.GetMethod("ConstrainPose"), Is.Not.Null);
        }
    }
}
