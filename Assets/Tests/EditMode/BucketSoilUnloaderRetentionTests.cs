using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class BucketSoilUnloaderRetentionTests
    {
        private GameObject ground, bucket, receiverObject;
        private TerrainData data;
        private BucketSoilCutter cutter;
        private BucketSoilFill fill;
        private BucketSoilUnloader unloader;

        /// <summary>Create a real excavated load using the calibrated ZX200 fill cavity.</summary>
        [SetUp]
        public void SetUp()
        {
            data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++) heights[z, x] = .5f;
            data.SetHeights(0, 0, heights);
            ground = Terrain.CreateTerrainGameObject(data);
            var soil = ground.AddComponent<DeformableSoilTerrain>();
            soil.Initialize(ground.GetComponent<Terrain>());
            bucket = new GameObject("retention regression bucket");
            cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;

            var visual = new GameObject("retention fill");
            visual.transform.SetParent(bucket.transform, false);
            fill = visual.AddComponent<BucketSoilFill>();
            cutter.fillVisual = visual.transform;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .3f;
            cutter.SamplePose();
            Assert.That(cutter.VolumeM3, Is.GreaterThan(.02));

            unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.openingNormalLocal = Vector3.down;
            bucket.transform.position = new Vector3(10, 20, 0);
            Physics.SyncTransforms();
        }

        /// <summary>Restore soil and destroy all fixture-owned objects and runtime inventories.</summary>
        [TearDown]
        public void TearDown()
        {
            if (cutter != null) cutter.ResetExcavation();
            Object.DestroyImmediate(receiverObject);
            Object.DestroyImmediate(bucket);
            Object.DestroyImmediate(ground);
            Object.DestroyImmediate(data);
        }

        /// <summary>An open angle must discharge only excess above the actual fill's lip capacity.</summary>
        [Test]
        public void OpenGateDischargesExcessButKeepsGeometricallyRetainedSoil()
        {
            // Only this analytic retention case substitutes a .04 m3 prism.
            // Its two lips at Z=.5 retain exactly .02 m3 below a forward surface.
            fill.innerHalfWidth = .02f;
            fill.cavityProfile = new[] {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, .5f),
                new Vector2(1, 1), new Vector2(0, 1), new Vector2(0, .5f)
            };
            bucket.transform.rotation = Quaternion.Euler(60, 0, 0);
            fill.SetSurfaceNormal(Vector3.forward);
            Assert.That(fill.RetainedCapacityM3(fill.SurfaceNormalLocal), Is.EqualTo(.02).Within(1e-8));
            unloader.dwellSeconds = 0;
            unloader.flowM3PerSecond = 10;
            double initial = cutter.VolumeM3;

            double emitted = unloader.Step(.1f);

            Assert.That(emitted, Is.EqualTo(initial - .02).Within(1e-8));
            Assert.That(cutter.VolumeM3, Is.EqualTo(.02).Within(1e-8));
            Assert.That(unloader.Step(.1f), Is.Zero);
            AssertConserved();
        }

        /// <summary>A fully tipped bucket should start visible discharge within 100 ms with serialized dwell.</summary>
        [Test]
        public void LargeOpeningStartsFlowBeforeLegacyDwellExpires()
        {
            fill.SetSurfaceNormal(-fill.OpeningNormalLocal);
            unloader.dwellSeconds = .25f;

            Assert.That(unloader.Step(.1f), Is.GreaterThan(.001),
                "An open downward mouth must not wait through the legacy quarter-second gate.");
            AssertConserved();
        }

        /// <summary>The final small residue must clear in bounded time without fractional leftovers.</summary>
        [Test]
        public void DownwardMouthClearsSmallTailWithinTwoHundredMilliseconds()
        {
            receiverObject = new GameObject("tail setup receiver");
            receiverObject.transform.position = new Vector3(30, 0, 0);
            var receiver = receiverObject.AddComponent<SoilReceivingZone>();
            cutter.TransferTo(receiver, cutter.VolumeM3 - .00008);
            Assert.That(cutter.VolumeM3, Is.EqualTo(.00008).Within(1e-10));
            fill.SetSurfaceNormal(-fill.OpeningNormalLocal);
            unloader.dwellSeconds = 0;

            for (int i = 0; i < 10; i++)
            {
                unloader.Step(.02f);
                AssertConserved();
            }

            Assert.That(cutter.VolumeM3, Is.Zero, "The tail must terminate, not keep shrinking geometrically.");
            Assert.That(unloader.State, Is.EqualTo(BucketSoilUnloader.UnloadingState.Empty));
        }

        /// <summary>The real gravity/repose update must permit a complete near-down dump.</summary>
        /// <param name="angleFromDown">World-space mouth angle in degrees from gravity.</param>
        /// <param name="stepSeconds">Elapsed seconds per fill and discharge update.</param>
        [TestCase(0f, .02f)]
        [TestCase(20f, .02f)]
        [TestCase(0f, .01f)]
        [TestCase(20f, .01f)]
        public void GravityDrivenCalibratedFillEmptiesNearDown(float angleFromDown, float stepSeconds)
        {
            unloader.openingNormalLocal = fill.OpeningNormalLocal;
            unloader.enterAngleFromDown = 70f;
            bucket.transform.rotation = Quaternion.AngleAxis(angleFromDown, Vector3.right)
                * Quaternion.FromToRotation(fill.OpeningNormalLocal, Vector3.down);
            var fixedUpdate = typeof(BucketSoilFill).GetMethod("FixedUpdate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(fixedUpdate, Is.Not.Null);

            float previousFixedDeltaTime = Time.fixedDeltaTime;
            try
            {
                Time.fixedDeltaTime = stepSeconds;
                for (int i = 0; i < Mathf.CeilToInt(3f / stepSeconds); i++)
                {
                    fixedUpdate.Invoke(fill, null);
                    unloader.Step(stepSeconds);
                    AssertConserved();
                }
            }
            finally { Time.fixedDeltaTime = previousFixedDeltaTime; }

            Assert.That(cutter.VolumeM3, Is.Zero,
                "The actual repose surface must not trap residue during a deliberate near-down dump.");
            Assert.That(unloader.State, Is.EqualTo(BucketSoilUnloader.UnloadingState.Empty));
        }

        /// <summary>Mild tilts retain the carried load even if the free-surface approximation predicts spill.</summary>
        [Test]
        public void MildTiltProtectsLoadFromGeometricSpill()
        {
            bucket.transform.rotation = Quaternion.Euler(75, 0, 0);
            fill.SetSurfaceNormal(-fill.OpeningNormalLocal);
            unloader.dwellSeconds = 0;
            double initial = cutter.VolumeM3;

            Assert.That(unloader.Step(1), Is.Zero);
            Assert.That(cutter.VolumeM3, Is.EqualTo(initial));
            AssertConserved();
        }

        /// <summary>A blocked mouth must keep its inventory, then resume when the obstruction is removed.</summary>
        [Test]
        public void FullMouthObstructionPreventsDebitAndRemovalRestartsFlow()
        {
            fill.SetSurfaceNormal(-fill.OpeningNormalLocal);
            unloader.dwellSeconds = 0;
            var obstruction = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                obstruction.transform.position = cutter.TipWorldPosition;
                obstruction.transform.localScale = new Vector3(2, .2f, .2f);
                Physics.SyncTransforms();
                double initial = cutter.VolumeM3;

                Assert.That(unloader.Step(.1f), Is.Zero);
                Assert.That(cutter.VolumeM3, Is.EqualTo(initial));
                AssertConserved();
            }
            finally { Object.DestroyImmediate(obstruction); }
            Physics.SyncTransforms();

            Assert.That(unloader.Step(.1f), Is.GreaterThan(0));
            AssertConserved();
        }

        private void AssertConserved()
        {
            double pending = unloader.AirborneVolumeM3 + unloader.SurfacePendingVolumeM3;
            Assert.That(cutter.VolumeM3 + pending + cutter.DeliveredVolumeM3,
                Is.EqualTo(cutter.ExcavatedVolumeM3).Within(1e-8));
            Assert.That(cutter.MassKg + pending * cutter.SoilDensityKgPerM3 + cutter.DeliveredMassKg,
                Is.EqualTo(cutter.ExcavatedVolumeM3 * cutter.SoilDensityKgPerM3).Within(1e-5));
        }
    }
}
