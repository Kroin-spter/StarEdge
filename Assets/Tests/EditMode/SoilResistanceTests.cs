using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class SoilResistanceTests
    {
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
