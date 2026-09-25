using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public sealed class MoundFloorTests
    {
        private TerrainData source;
        private GameObject ground;
        private DeformableSoilTerrain soil;
        private float[,] mapGround;
        private float[,] mound;
        private TerrainData Data => ground.GetComponent<Terrain>().terrainData;

        /// <summary>Create a mound above sloping map ground using a runtime Terrain copy.</summary>
        [SetUp]
        public void SetUp()
        {
            source = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            ground = Terrain.CreateTerrainGameObject(source);
            ground.transform.position = new Vector3(10, 7, 20);
            soil = ground.AddComponent<DeformableSoilTerrain>();
            soil.maximumCutDepth = 2;
            mapGround = new float[33, 33];
            mound = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++)
                {
                    mapGround[z, x] = .2f + x * .005f + z * .002f;
                    mound[z, x] = mapGround[z, x] + .2f;
                }
            soil.PrepareMoundSurface(mound, mapGround);
        }

        /// <summary>Release the Terrain clone and the original source.</summary>
        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(ground);
            UnityEngine.Object.DestroyImmediate(source);
        }

        /// <summary>Both cut algorithms must stop above the sampled map at every affected vertex.</summary>
        /// <param name="blade">Whether to exercise the box blade instead of the radial sweep.</param>
        [TestCase(false)]
        [TestCase(true)]
        public void DeepSweepCannotCutBelowSlopingGround(bool blade)
        {
            Vector3 center = ground.transform.position + new Vector3(2, -1, 2);
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
                corners[i] = center + new Vector3((i & 1) == 0 ? -1 : 1,
                    (i & 2) == 0 ? -.1f : .1f, (i & 4) == 0 ? -1 : 1);
            double removed = blade ? soil.CutBladeSweep(corners, corners, 100) :
                soil.CutSweep(center, center, 1, 100);
            Assert.That(removed, Is.GreaterThan(0));
            float[,] actual = Data.GetHeights(0, 0, 33, 33);
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++)
                    Assert.That(actual[z, x], Is.GreaterThanOrEqualTo(mapGround[z, x]));
        }

        /// <summary>Reset restores the prepared mound while source data and holes remain intact.</summary>
        [Test]
        public void ResetRestoresMoundWithoutMutatingSourceOrExpandingHoles()
        {
            float[,] prepared = Data.GetHeights(0, 0, 33, 33);
            bool[,] holes = Data.GetHoles(0, 0, 32, 32);
            holes[0, 0] = false;
            Data.SetHoles(0, 0, holes);
            soil.CutSweep(ground.transform.position + new Vector3(2, 0, 2),
                ground.transform.position + new Vector3(2, 0, 2), 1, 100);
            mound[16, 16] = 0;
            mapGround[16, 16] = 0;
            soil.ResetSoil();
            Assert.That(Data.GetHeights(0, 0, 33, 33), Is.EqualTo(prepared));
            Assert.That(source.GetHeight(16, 16), Is.Zero);
            Assert.That(source.IsHole(0, 0), Is.False);
            Assert.That(Data.IsHole(0, 0), Is.True);
            Assert.That(soil.GetFloorWorldY(ground.transform.position + new Vector3(2, 0, 2)),
                Is.EqualTo(7 + (.2f + 16 * .007f) * 2).Within(.00001));
        }

        /// <summary>The guard query interpolates the floor in world coordinates between grid vertices.</summary>
        [Test]
        public void FloorQueryAndResistanceUseLocalGround()
        {
            Vector3 point = ground.transform.position + new Vector3(2.0625f, 0, 1.3125f);
            float expected = 7 + (.2f + 16.5f * .005f + 10.5f * .002f) * 2;
            Assert.That(soil.GetFloorWorldY(point), Is.EqualTo(expected).Within(.00001));
            var bucket = new GameObject("mound floor guard");
            try
            {
                var cutter = bucket.AddComponent<BucketSoilCutter>();
                cutter.soil = soil;
                cutter.tipLocalPosition = Vector3.zero;
                var resistance = bucket.AddComponent<BucketSoilResistance>();
                resistance.maximumPenetration = 2;
                point.y = expected + .01f;
                bucket.transform.position = point;
                resistance.EvaluateContact();
                Assert.That(resistance.IsBlocked, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(bucket); }
        }

        /// <summary>Contact feedback must disappear over a hole even when the hidden heightmap is above the blade.</summary>
        [Test]
        public void ResistanceIgnoresHiddenCells()
        {
            bool[,] holes = Data.GetHoles(0, 0, 32, 32);
            holes[16, 16] = false;
            Data.SetHoles(0, 0, holes);
            var bucket = new GameObject("hole contact probe");
            try
            {
                var cutter = bucket.AddComponent<BucketSoilCutter>();
                cutter.soil = soil;
                cutter.tipLocalPosition = Vector3.zero;
                var resistance = bucket.AddComponent<BucketSoilResistance>();
                bucket.transform.position = ground.transform.position + new Vector3(2.0625f, .2f, 2.0625f);
                resistance.EvaluateContact();
                Assert.That(resistance.IsContacting, Is.False);
                Assert.That(resistance.IsBlocked, Is.False);
                bucket.transform.position += new Vector3(.25f, 0, 0);
                resistance.EvaluateContact();
                Assert.That(resistance.IsContacting, Is.True);
                Assert.That(resistance.IsBlocked, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(bucket); }
        }

        /// <summary>The shared surface query rejects disabled collision, inactive objects and invalid coordinates.</summary>
        [Test]
        public void SurfaceQueryRequiresActiveColliderAndValidBounds()
        {
            Vector3 point = ground.transform.position + new Vector3(2, 0, 2);
            Assert.That(soil.HasSurfaceAt(point), Is.True);
            soil.SoilCollider.enabled = false;
            Assert.That(soil.HasSurfaceAt(point), Is.False);
            soil.SoilCollider.enabled = true;
            ground.SetActive(false);
            Assert.That(soil.HasSurfaceAt(point), Is.False);
            ground.SetActive(true);
            Assert.That(soil.HasSurfaceAt(point), Is.True);
            Assert.That(soil.HasSurfaceAt(point + Vector3.right * 10), Is.False);
            Assert.That(soil.HasSurfaceAt(new Vector3(float.NaN, 0, 0)), Is.False);
        }

        /// <summary>Hidden Terrain cannot supply or accept material through either cutting algorithm or deposition.</summary>
        /// <param name="operation">Zero for radial cutting, one for blade cutting, two for deposition.</param>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void FullyHoledTerrainTransfersNoVolume(int operation)
        {
            Data.SetHoles(0, 0, new bool[32, 32]);
            float[,] before = Data.GetHeights(0, 0, 33, 33);
            Assert.That(Transfer(operation), Is.Zero);
            Assert.That(Data.GetHeights(0, 0, 33, 33), Is.EqualTo(before));
            Assert.That(soil.RecalculateRemovedVolumeM3(), Is.Zero);
            Assert.That(source.IsHole(16, 16), Is.False);
        }

        /// <summary>All four vertices of a hole remain fixed while nearby fully visible cells still transfer soil.</summary>
        /// <param name="operation">Zero for radial cutting, one for blade cutting, two for deposition.</param>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void HoleBoundaryVerticesRemainFixed(int operation)
        {
            bool[,] holes = Data.GetHoles(0, 0, 32, 32);
            holes[16, 16] = false;
            Data.SetHoles(0, 0, holes);
            float[,] before = Data.GetHeights(0, 0, 33, 33);
            Assert.That(Transfer(operation), Is.GreaterThan(0));
            float[,] after = Data.GetHeights(0, 0, 33, 33);
            for (int z = 16; z <= 17; z++)
                for (int x = 16; x <= 17; x++)
                    Assert.That(after[z, x], Is.EqualTo(before[z, x]));
            Assert.That(after[15, 15], Is.Not.EqualTo(before[15, 15]));
            Assert.That(Data.IsHole(16, 16), Is.True);
        }

        private double Transfer(int operation)
        {
            Vector3 center = ground.transform.position + new Vector3(2, -1, 2);
            if (operation == 2) return soil.Deposit(center, 1, .2);
            if (operation == 0) return soil.CutSweep(center, center, 1, 100);
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
                corners[i] = center + new Vector3((i & 1) == 0 ? -1 : 1,
                    (i & 2) == 0 ? -.1f : .1f, (i & 4) == 0 ? -1 : 1);
            return soil.CutBladeSweep(corners, corners, 100);
        }

        /// <summary>Reject malformed or inverted ground without changing the prepared runtime surface.</summary>
        [Test]
        public void InvalidPreparationLeavesMoundIntact()
        {
            float[,] before = Data.GetHeights(0, 0, 33, 33);
            Assert.Throws<ArgumentException>(() => soil.PrepareMoundSurface(mound, new float[2, 2]));
            mapGround[1, 1] = float.NaN;
            Assert.Throws<ArgumentException>(() => soil.PrepareMoundSurface(mound, mapGround));
            mapGround[1, 1] = .9f;
            Assert.Throws<ArgumentException>(() => soil.PrepareMoundSurface(mound, mapGround));
            Assert.That(Data.GetHeights(0, 0, 33, 33), Is.EqualTo(before));
        }

        /// <summary>A normal-thickness soil layer must remain equally thick on flat and sloped map faces.</summary>
        [TestCase(1f, 0f, 0f, .5f)]
        [TestCase(.7071068f, 0f, .7071068f, .7071068f)]
        public void NormalThicknessUsesCorrectVerticalHeightOffset(
            float normalY, float normalX, float normalZ, float expectedVerticalOffset)
        {
            MethodInfo method = typeof(CesiumMoundSite).GetMethod(
                "GetVerticalOffsetForNormalThickness", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null,
                "A slope-normal layer needs a shared height-field conversion routine.");

            float verticalOffset = (float)method.Invoke(null, new object[] {
                .5f, new Vector3(normalX, normalY, normalZ), .2f });

            Assert.That(verticalOffset, Is.EqualTo(expectedVerticalOffset).Within(.0001f));
            Assert.That(new Vector3(normalX, normalY, normalZ).normalized.y * verticalOffset,
                Is.EqualTo(.5f).Within(.0001f));
        }

        /// <summary>Near-vertical faces must be rejected rather than creating an unbounded Terrain ridge.</summary>
        [TestCase(0f)]
        [TestCase(.1f)]
        public void NormalThicknessRejectsFacesOutsideHeightFieldLimit(float normalY)
        {
            MethodInfo method = typeof(CesiumMoundSite).GetMethod(
                "GetVerticalOffsetForNormalThickness", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null,
                "A slope-normal layer needs a shared height-field conversion routine.");

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object[] { .5f, new Vector3(0f, normalY, 1f), .2f }));

            Assert.That(exception.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
        }

        /// <summary>Support-pad blending must never lower the Cesium height used as the cut floor.</summary>
        [TestCase(2f, 1f, .5f, 2f)]
        [TestCase(1f, 2f, .5f, 1.5f)]
        [TestCase(1f, 2f, 1f, 2f)]
        public void SupportPadBlendNeverLowersSampledCesiumHeight(
            float sampledHeight, float supportTarget, float blend, float expectedHeight)
        {
            MethodInfo method = typeof(CesiumMoundSite).GetMethod(
                "BlendSupportHeightWithoutLowering", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null,
                "Support grading needs a one-way blend that preserves the sampled map as a minimum floor.");

            float result = (float)method.Invoke(null,
                new object[] { sampledHeight, supportTarget, blend });

            Assert.That(result, Is.EqualTo(expectedHeight).Within(.0001f));
            Assert.That(result, Is.GreaterThanOrEqualTo(sampledHeight));
        }

        /// <summary>Incomplete candidate coverage must wait for Cesium tiles, while complete no-face data may stop.</summary>
        [TestCase(0, 0, 20, 100, true)]
        [TestCase(2, 0, 80, 100, true)]
        [TestCase(0, 0, 100, 100, false)]
        [TestCase(2, 0, 100, 100, false)]
        [TestCase(2, 3, 80, 100, false)]
        public void WorkFaceSearchRetriesOnlyWhenMoreMapCoverageCanHelp(
            int coveredPositions, int validFaces, int validSamples, int totalSamples, bool expectedRetry)
        {
            MethodInfo method = typeof(CesiumMoundSite).GetMethod(
                "ShouldRetryWorkFaceSearch", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null,
                "Work-face selection needs to distinguish missing Cesium coverage from an actually unusable slope.");

            bool shouldRetry = (bool)method.Invoke(null,
                new object[] { coveredPositions, validFaces, validSamples, totalSamples });

            Assert.That(shouldRetry, Is.EqualTo(expectedRetry));
        }
    }
}
