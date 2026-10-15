using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class SoilUnloadingTests
    {
        private GameObject ground, bucket, receiver;
        private TerrainData data;
        private BucketSoilCutter cutter;

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
            bucket = new GameObject("source bucket");
            cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .3f;
            cutter.SamplePose();
            Assert.That(cutter.VolumeM3, Is.GreaterThan(.02));
            receiver = new GameObject("receiving bed");
        }

        [TearDown]
        public void TearDown()
        {
            if (cutter != null) cutter.ResetExcavation();
            Object.DestroyImmediate(receiver);
            Object.DestroyImmediate(bucket);
            Object.DestroyImmediate(ground);
            Object.DestroyImmediate(data);
        }

        [Test]
        public void EmptyLandedPilesAreReusedWhenDiggingMovesAcrossTheSite()
        {
            var flight = bucket.GetComponent<SoilFlightTransport>();
            if (flight == null) flight = bucket.AddComponent<SoilFlightTransport>();
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            typeof(SoilFlightTransport).GetField("cutter", flags).SetValue(flight, cutter);
            var deposit = typeof(SoilFlightTransport).GetMethod("DepositPile", flags);
            for (int i = 0; i < 50; i++)
            {
                Vector3 anchor = new Vector3(10 + i, 0, 10);
                deposit.Invoke(flight, new object[] { anchor, .01 });
                var piles = (System.Collections.Generic.List<SoilReceivingZone>)
                    typeof(SoilFlightTransport).GetField("piles", flags).GetValue(flight);
                var active = piles.Find(pile => pile.VolumeM3 > 0);
                Assert.That(active.transform.position, Is.EqualTo(anchor));
                Assert.That(active.ExcavateLoose(anchor + new Vector3(.4f, .01f, 0),
                    Vector3.right * .8f, .01), Is.EqualTo(.01).Within(1e-9));
                Assert.That(piles.Count, Is.EqualTo(1), "Empty soil objects must be reused, not accumulate.");
            }
        }

        [Test]
        public void LoosePileSettlesOnlyAcceptedVolumeWithoutCountingDeliveryTwice()
        {
            var pile = CreateLoosePile();
            double delivered = cutter.DeliveredVolumeM3;
            double terrainRemovedBefore = cutter.soil.RecalculateRemovedVolumeM3();
            var settle = typeof(SoilReceivingZone).GetMethod("SettleIntoTerrain",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(settle, Is.Not.Null);
            double moved = (double)settle.Invoke(pile, new object[] { cutter.soil, .005 });
            Assert.That(moved, Is.GreaterThan(0).And.LessThanOrEqualTo(.005));
            Assert.That(pile.VolumeM3 + moved, Is.EqualTo(.02).Within(1e-8));
            Assert.That(pile.Records[0].VolumeM3, Is.EqualTo(pile.VolumeM3).Within(1e-8));
            Assert.That(cutter.DeliveredVolumeM3, Is.EqualTo(delivered));
            Assert.That(terrainRemovedBefore - cutter.soil.RecalculateRemovedVolumeM3(),
                Is.EqualTo(moved).Within(1e-6), "减少的松土必须真实进入地形。");
            Assert.That((double)settle.Invoke(pile, new object[] { null, .005 }), Is.Zero);
            pile.isGroundPile = false;
            Assert.That((double)settle.Invoke(pile, new object[] { cutter.soil, .005 }), Is.Zero,
                "矿车内的土不能使用地面松土沉积路径。");
        }

        [Test]
        public void LoosePileWaitsUntilBucketHasLeftBeforeSettling()
        {
            var flight = bucket.GetComponent<SoilFlightTransport>() ?? bucket.AddComponent<SoilFlightTransport>();
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            typeof(SoilFlightTransport).GetField("cutter", flags).SetValue(flight, cutter);
            typeof(SoilFlightTransport).GetMethod("DepositPile", flags).Invoke(flight,
                new object[] { new Vector3(2, 1, 2), .02 });
            var piles = (System.Collections.Generic.List<SoilReceivingZone>)
                typeof(SoilFlightTransport).GetField("piles", flags).GetValue(flight);
            var advance = typeof(SoilFlightTransport).GetMethod("Advance", flags);
            for (int i = 0; i < 60; i++)
                advance.Invoke(flight, new object[] { .25f, null, (LayerMask)0 });
            Assert.That(piles[0].VolumeM3, Is.EqualTo(.02).Within(1e-9));
            bucket.transform.position = new Vector3(8, 4, 8);
            for (int i = 0; i < 28; i++)
                advance.Invoke(flight, new object[] { .25f, null, (LayerMask)0 });
            Assert.That(piles[0].VolumeM3, Is.EqualTo(.02).Within(1e-9));
            for (int i = 0; i < 8; i++)
                advance.Invoke(flight, new object[] { .25f, null, (LayerMask)0 });
            Assert.That(piles[0].VolumeM3, Is.GreaterThan(0).And.LessThan(.02));
        }

        [Test]
        public void ElevatedLoosePileCannotAssimilateIntoTerrainBelowIt()
        {
            var pile = CreateLoosePile();
            pile.transform.position += Vector3.up;
            var settle = typeof(SoilReceivingZone).GetMethod("SettleIntoTerrain",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            double before = cutter.soil.RecalculateRemovedVolumeM3();
            Assert.That((double)settle.Invoke(pile, new object[] { cutter.soil, .005 }), Is.Zero);
            Assert.That(pile.VolumeM3, Is.EqualTo(.02).Within(1e-9));
            Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.EqualTo(before).Within(1e-9));
        }

        [Test]
        public void RepeatedGroundReceiptsDoNotGrowWithParticleCount()
        {
            var pile = CreateLoosePile();
            for (int i = 0; i < 1000; i++)
                Assert.That(cutter.TransferTo(pile, .00001), Is.EqualTo(.00001).Within(1e-9));
            Assert.That(pile.VolumeM3, Is.EqualTo(.03).Within(1e-9));
            Assert.That(pile.Records.Count, Is.EqualTo(1),
                "Ground soil needs remaining inventory per source, not one receipt per particle.");
        }

        [Test]
        public void RemovingUnrelatedSourceDoesNotRestoreReexcavatedGroundSoil()
        {
            var pile = CreateLoosePile();
            Vector3 end = pile.transform.position + new Vector3(.6f, .03f, 0);
            Assert.That(pile.ExcavateLoose(end, Vector3.right * 1.2f, .005),
                Is.EqualTo(.005).Within(1e-9));
            typeof(SoilReceivingZone).GetMethod("RemoveSource",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(pile, new object[] { null, null });
            Assert.That(pile.VolumeM3, Is.EqualTo(.015).Within(1e-9));
            Assert.That(pile.MassKg, Is.EqualTo(.015 * cutter.SoilDensityKgPerM3).Within(1e-6));
        }

        [Test]
        public void LoosePileCannotBeExcavatedFromDistantBucketPosition()
        {
            var pile = CreateLoosePile();
            double before = pile.VolumeM3;
            Vector3 anchor = pile.transform.position;
            double removed = pile.ExcavateLoose(anchor + new Vector3(12, .03f, 0),
                Vector3.forward * .1f, .01);
            Assert.That(removed, Is.Zero, "The 100m accounting bed is not the visible pile footprint.");
            Assert.That(pile.VolumeM3, Is.EqualTo(before));
            Assert.That(pile.transform.position, Is.EqualTo(anchor));
        }

        [Test]
        public void LoosePileRequiresActualCuttingTravel()
        {
            var pile = CreateLoosePile();
            double before = pile.VolumeM3;
            Assert.That(pile.ExcavateLoose(pile.transform.position + Vector3.up * .03f,
                Vector3.zero, .01), Is.Zero);
            Assert.That(pile.VolumeM3, Is.EqualTo(before));
        }

        [Test]
        public void LoosePileSweepMustTouchRenderedSoilNotItsBoundingCorner()
        {
            var pile = CreateLoosePile();
            Vector3 maximum = receiver.GetComponent<MeshFilter>().sharedMesh.bounds.max;
            Vector3 point = pile.transform.position + new Vector3(maximum.x * .9f, .03f, maximum.z * .9f);
            double before = pile.VolumeM3;
            Assert.That(pile.ExcavateLoose(point, Vector3.forward * .01f, .01), Is.Zero,
                "A cone's empty bounding-box corner is not soil.");
            Assert.That(pile.VolumeM3, Is.EqualTo(before));
        }

        [Test]
        public void LoosePileCanBeReexcavatedBySweepCrossingItsVisibleBody()
        {
            var pile = CreateLoosePile();
            double before = pile.VolumeM3;
            double massBefore = pile.MassKg;
            Vector3 end = pile.transform.position + new Vector3(.6f, .03f, 0);
            double removed = pile.ExcavateLoose(end, Vector3.right * 1.2f, .005);
            Assert.That(removed, Is.GreaterThan(0));
            Assert.That(removed, Is.LessThanOrEqualTo(.005));
            Assert.That(before - pile.VolumeM3, Is.EqualTo(removed).Within(1e-9));
            Assert.That(massBefore - pile.MassKg,
                Is.EqualTo(removed * cutter.SoilDensityKgPerM3).Within(.00001));
        }

        [Test]
        public void TinyPileAboveLowerTerrainDoesNotStretchItsBaseIntoAColumn()
        {
            receiver.transform.position = new Vector3(2, 3, 2);
            var pile = receiver.AddComponent<SoilReceivingZone>();
            pile.isGroundPile = true;
            cutter.TransferTo(pile, .002);
            var bounds = receiver.GetComponent<MeshFilter>().sharedMesh.bounds;
            Assert.That(bounds.size.y, Is.LessThan(.3f), "A small deposit must not bridge a two metre support gap.");
        }

        private SoilReceivingZone CreateLoosePile()
        {
            receiver.transform.position = new Vector3(2, 1, 2);
            var pile = receiver.AddComponent<SoilReceivingZone>();
            pile.isGroundPile = true;
            pile.size = new Vector2(100, 100);
            pile.capacityM3 = 100000;
            pile.maximumFillHeight = 100;
            Assert.That(cutter.TransferTo(pile, .02), Is.EqualTo(.02).Within(1e-9));
            return pile;
        }

        [Test]
        public void ReceivingBedFormsSlopingHeapWithConservedMeshVolume()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            cutter.TransferTo(zone, .02);
            var mesh = receiver.GetComponent<MeshFilter>().sharedMesh;
            float min = float.PositiveInfinity, max = 0;
            foreach (var v in mesh.vertices)
                if (v.y > .000001f) { min = Mathf.Min(min, v.y); max = Mathf.Max(max, v.y); }
            Assert.That(max - min, Is.GreaterThan(.001f), "First load must form a mound, not a flat slab.");
            double volume = 0;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
                volume += Vector3.Dot(vertices[triangles[i]], Vector3.Cross(
                    vertices[triangles[i + 1]], vertices[triangles[i + 2]])) / 6.0;
            Assert.That(System.Math.Abs(volume), Is.EqualTo(zone.VolumeM3).Within(.000001));
            Assert.That(zone.TryGetLanding(new Vector3(0, 2, 0), 3, out var landing), Is.True);
            Assert.That(landing.y, Is.EqualTo(max).Within(.0001f), "Reception must use the rendered heap, not its average height.");
        }

        [Test]
        public void ObliqueFlightHitsHeapEvenWhenBothEndpointsAreAboveSoil()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            cutter.TransferTo(zone, .02);
            var method = typeof(SoilReceivingZone).GetMethod("TryIntersectSegment",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object[] args = { new Vector3(-1.4f, .004f, 0), new Vector3(1.4f, .003f, 0), Vector3.zero };
            Assert.That((bool)method.Invoke(zone, args), Is.True);
            Assert.That(((Vector3)args[2]).x, Is.LessThan(0), "Use the first contact, not the far side of the heap.");
        }

        /// <summary>Nearby stream parcels combine without losing their tracked soil volume.</summary>
        [Test]
        public void AdjacentSmallEmissionsCoalesceWithoutLosingSoil()
        {
            bucket.transform.position = new Vector3(10, 20, 0);
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.openingNormalLocal = Vector3.down;
            unloader.flowM3PerSecond = .01f;
            unloader.dwellSeconds = 0;
            double before = cutter.VolumeM3;
            double airborne = bucket.GetComponent<SoilFlightTransport>()?.AirborneVolumeM3 ?? 0;
            for (int i = 0; i < 100; i++) unloader.Step(.001f);
            var flight = bucket.GetComponent<SoilFlightTransport>();
            Assert.That(flight.ParcelCount, Is.LessThan(80), "Small colocated emissions must not create hundreds of persistent parcels.");
            Assert.That(cutter.VolumeM3 + flight.AirborneVolumeM3, Is.EqualTo(before + airborne).Within(1e-9));
        }

        [Test]
        public void ShallowTiltKeepsCollectedSoilUntilDumpGateOpens()
        {
            var visual = new GameObject("bucket fill");
            visual.transform.SetParent(bucket.transform, false);
            cutter.fillVisual = visual.transform;
            visual.AddComponent<BucketSoilFill>();
            cutter.SamplePose();
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.openingNormalLocal = Vector3.down;
            unloader.dwellSeconds = 0;
            bucket.transform.position = new Vector3(10, 2, 0);
            bucket.transform.rotation = Quaternion.Euler(75, 0, 0);
            double retained = cutter.VolumeM3;
            Assert.That(retained, Is.GreaterThan(0));
            Assert.That(unloader.Step(1), Is.Zero);
            Assert.That(cutter.VolumeM3, Is.EqualTo(retained).Within(1e-10));
            Assert.That(unloader.AirborneVolumeM3, Is.Zero);
        }

        /// <summary>Disabling soil emission must neither freeze leveling nor reset the carried surface on enable.</summary>
        [Test]
        public void DisabledUnloaderDoesNotFreezeOrSnapCarriedFillSurface()
        {
            var visual = new GameObject("carried bucket fill");
            visual.transform.SetParent(bucket.transform, false);
            var fill = visual.AddComponent<BucketSoilFill>();
            cutter.fillVisual = visual.transform;
            double carriedVolume = cutter.VolumeM3;
            fill.SetVolume(carriedVolume);
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.enabled = false;
            bucket.transform.rotation = Quaternion.Euler(-45f, 0f, 0f);

            Vector3 before = fill.SurfaceNormalLocal;
            Vector3 localUp = visual.transform.InverseTransformDirection(-Physics.gravity.normalized);
            Vector3 equilibrium = Vector3.RotateTowards(localUp, unloader.openingNormalLocal.normalized,
                unloader.reposeAngle * Mathf.Deg2Rad, 0f);
            typeof(BucketSoilFill).GetMethod("FixedUpdate", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)?.Invoke(fill, null);
            Vector3 duringCarry = fill.SurfaceNormalLocal;

            Assert.That(Vector3.Angle(duringCarry, equilibrium),
                Is.LessThan(Vector3.Angle(before, equilibrium)), "Carried soil must keep leveling while unloading is disabled.");
            Assert.That(Vector3.Angle(duringCarry, equilibrium), Is.GreaterThan(1f),
                "The first carry step should preserve the surface response smoothing.");
            Assert.That(cutter.VolumeM3, Is.EqualTo(carriedVolume).Within(1e-10));

            unloader.enabled = true;
            Assert.That(unloader.Step(Time.fixedDeltaTime), Is.Zero);
            Assert.That(Vector3.Angle(fill.SurfaceNormalLocal, duringCarry), Is.LessThan(.001f),
                "The unloader must not reinitialize or update the surface again in the same step.");
            Assert.That(cutter.VolumeM3, Is.EqualTo(carriedVolume).Within(1e-10));
        }

        /// <summary>A rotated visual child uses the bucket mouth direction transformed into its own frame.</summary>
        [Test]
        public void RotatedFillUsesOpeningDirectionInItsOwnLocalCoordinates()
        {
            var visual = new GameObject("rotated bucket fill");
            visual.transform.SetParent(bucket.transform, false);
            visual.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            var fill = visual.AddComponent<BucketSoilFill>();
            fill.SetVolume(cutter.VolumeM3);
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.enabled = false;
            bucket.transform.rotation = Quaternion.Euler(-45f, 0f, 0f);

            Vector3 before = fill.SurfaceNormalLocal;
            Vector3 localUp = visual.transform.InverseTransformDirection(-Physics.gravity.normalized);
            Vector3 openingInFill = visual.transform.InverseTransformDirection(
                unloader.transform.TransformDirection(unloader.openingNormalLocal)).normalized;
            Vector3 equilibrium = Vector3.RotateTowards(localUp, openingInFill,
                unloader.reposeAngle * Mathf.Deg2Rad, 0f);
            float blend = 1 - Mathf.Exp(-Time.fixedDeltaTime / unloader.surfaceResponseSeconds);
            Vector3 expected = Vector3.Slerp(before, equilibrium, blend).normalized;

            typeof(BucketSoilFill).GetMethod("FixedUpdate", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)?.Invoke(fill, null);

            Assert.That(Vector3.Angle(fill.SurfaceNormalLocal, expected), Is.LessThan(.01f));
        }

        [Test]
        public void TipInsideExternalSolidCannotUnloadThroughIt()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            receiver.transform.position = new Vector3(10, 0, 0);
            bucket.transform.position = new Vector3(10, 2, 0);
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.receivers = new[] { zone };
            unloader.openingNormalLocal = Vector3.down;
            unloader.dwellSeconds = 0;
            var obstruction = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                obstruction.transform.position = bucket.transform.position;
                Physics.SyncTransforms();
                double initial = cutter.VolumeM3;
                Assert.That(unloader.Step(1), Is.Zero);
                Assert.That(cutter.VolumeM3, Is.EqualTo(initial));
                Assert.That(zone.VolumeM3, Is.Zero);
            }
            finally { Object.DestroyImmediate(obstruction); }
        }

        [Test]
        public void MultipleReceiversAndSharedTerrainResetDoNotDuplicateMaterial()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            var secondReceiver = new GameObject("second receiver");
            var secondBucket = new GameObject("same terrain bucket");
            try
            {
                var otherZone = secondReceiver.AddComponent<SoilReceivingZone>();
                var other = secondBucket.AddComponent<BucketSoilCutter>();
                other.soil = cutter.soil;
                other.tipLocalPosition = Vector3.zero;
                other.cutDirectionLocal = Vector3.forward;
                secondBucket.transform.position = new Vector3(1, .6f, 1);
                other.SamplePose();
                secondBucket.transform.position += Vector3.forward * .3f;
                other.SamplePose();
                cutter.TransferTo(zone, .01);
                cutter.TransferTo(otherZone, .01);
                other.TransferTo(otherZone, .01);
                Assert.That(cutter.soil.RecalculateRemovedVolumeM3(),
                    Is.EqualTo(cutter.VolumeM3 + other.VolumeM3 + zone.VolumeM3 + otherZone.VolumeM3).Within(1e-5));
                secondBucket.SetActive(false);
                cutter.ResetExcavation();
                Assert.That(other.VolumeM3 + zone.VolumeM3 + otherZone.VolumeM3, Is.Zero);
                Assert.That(other.DeliveredVolumeM3 + cutter.DeliveredVolumeM3, Is.Zero);
                Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(1e-6));
            }
            finally { Object.DestroyImmediate(secondBucket); Object.DestroyImmediate(secondReceiver); }
        }

        [Test]
        public void ReceivingFootprintHeightAndOrientationRejectInvalidTargets()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            Assert.That(zone.TryGetLanding(new Vector3(0, 2, 0), 3, out _), Is.True);
            Assert.That(zone.TryGetLanding(new Vector3(4, 2, 0), 3, out _), Is.False);
            Assert.That(zone.TryGetLanding(new Vector3(0, -.1f, 0), 3, out _), Is.False);
            Assert.That(zone.TryGetLanding(new Vector3(0, 4, 0), 3, out _), Is.False);
            receiver.transform.rotation = Quaternion.Euler(30, 0, 0);
            Assert.That(zone.TryGetLanding(new Vector3(0, 2, 0), 3, out _), Is.False);
            receiver.transform.rotation = Quaternion.identity;
            receiver.transform.localScale = Vector3.one * 2;
            Assert.That(zone.TryGetLanding(new Vector3(0, 2, 0), 3, out _), Is.False);
            double before = cutter.VolumeM3;
            Assert.That(cutter.TransferTo(zone, double.NaN), Is.Zero);
            Assert.That(cutter.TransferTo(zone, double.PositiveInfinity), Is.Zero);
            Assert.That(cutter.TransferTo(zone, -1), Is.Zero);
            Assert.That(cutter.TransferTo(null, 1), Is.Zero);
            Assert.That(cutter.VolumeM3, Is.EqualTo(before));
        }

        /// <summary>Retain pose hysteresis and dwell while allowing discharge outside a receiver footprint.</summary>
        [Test]
        public void ContinuousGravityFlowAndFreeDischargeConserveSoil()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            receiver.transform.position = new Vector3(10, 0, 0);
            bucket.transform.position = new Vector3(10, 2, 0);
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.receivers = new[] { zone };
            unloader.openingNormalLocal = Vector3.down;
            unloader.dwellSeconds = .25f;
            unloader.flowM3PerSecond = .01f;
            double initial = cutter.VolumeM3;
            bucket.transform.rotation = Quaternion.Euler(180, 0, 0);
            Assert.That(unloader.Step(1), Is.Zero);
            bucket.transform.rotation = Quaternion.identity;
            double first = unloader.Step(.2f);
            Assert.That(first, Is.GreaterThan(0));
            Assert.That(unloader.State, Is.EqualTo(BucketSoilUnloader.UnloadingState.Dumping));
            double emitted = unloader.Step(.1f);
            Assert.That(emitted, Is.GreaterThan(0).And.LessThanOrEqualTo(.0018),
                "Flow stays rate limited even without the old quarter-second pause");
            Assert.That(unloader.AirborneVolumeM3, Is.EqualTo(first + emitted).Within(1e-10));
            Assert.That(zone.VolumeM3, Is.Zero, "Emission must not teleport soil into the bed");
            bucket.transform.rotation = Quaternion.Euler(70, 0, 0);
            Assert.That(unloader.Step(.1f), Is.GreaterThan(0), "The transition band must allow gradual spilling");
            bucket.transform.position += Vector3.right * 5;
            Assert.That(unloader.Step(1), Is.GreaterThan(0), "Missing a receiver must not close the gravity gate");
            Assert.That(cutter.VolumeM3 + unloader.AirborneVolumeM3 + LandedVolume(),
                Is.EqualTo(initial).Within(1e-10));
            bucket.transform.position -= Vector3.right * 5;
            Assert.That(unloader.Step(.1f), Is.GreaterThan(0), "Receiver reentry must not restart pose dwell");
            bucket.transform.rotation = Quaternion.Euler(85, 0, 0);
            Assert.That(unloader.Step(1), Is.Zero);
            bucket.transform.rotation = Quaternion.Euler(70, 0, 0);
            Assert.That(unloader.Step(.1f), Is.GreaterThan(0), "Return to transition band restarts gradual spilling");
            bucket.transform.rotation = Quaternion.identity;
            Assert.That(unloader.Step(.2f), Is.GreaterThan(0), "Fully opening must restart promptly");
            Assert.That(unloader.Step(.1f), Is.GreaterThan(0));
            Assert.That(cutter.VolumeM3 + unloader.AirborneVolumeM3 + LandedVolume(),
                Is.EqualTo(initial).Within(1e-10));
        }

        /// <summary>Solids below the outlet catch falling soil, and every emitted parcel remains accounted for.</summary>
        [Test]
        public void EmptyTerminationInvalidTimeAndBlockersDoNotLoseSoil()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            receiver.transform.position = new Vector3(10, 0, 0);
            bucket.transform.position = new Vector3(10, 2, 0);
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.receivers = new[] { zone };
            unloader.openingNormalLocal = Vector3.down;
            unloader.dwellSeconds = 0;
            double initial = cutter.VolumeM3;
            Assert.That(unloader.Step(float.NaN), Is.Zero);
            Assert.That(unloader.Step(float.PositiveInfinity), Is.Zero);
            Assert.That(unloader.Step(-1), Is.Zero);
            Assert.That(unloader.Step(0), Is.Zero);
            Assert.That(unloader.Step(2), Is.Zero);
            Assert.That(cutter.VolumeM3, Is.EqualTo(initial));
            Assert.That(unloader.AirborneVolumeM3 + zone.VolumeM3, Is.Zero);
            double caught = 0;
            var obstruction = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                obstruction.transform.position = new Vector3(10, 1, 0);
                Physics.SyncTransforms();
                double emitted = unloader.Step(.1f);
                Assert.That(emitted, Is.GreaterThan(0), "A solid below the tip must not suppress emission");
                Assert.That(unloader.AirborneVolumeM3, Is.EqualTo(emitted).Within(1e-10));
                Assert.That(zone.VolumeM3, Is.Zero);
                bucket.transform.rotation = Quaternion.Euler(180, 0, 0);
                Assert.That(unloader.Step(1), Is.Zero);
                caught = LandedVolume(true);
                Assert.That(caught, Is.EqualTo(emitted).Within(1e-10), "The nearest solid must catch the flight as a ground pile");
                Assert.That(unloader.AirborneVolumeM3, Is.Zero);
                Assert.That(zone.VolumeM3, Is.Zero, "The receiver below the solid cannot receive through it");
                Assert.That(cutter.VolumeM3 + caught, Is.EqualTo(initial).Within(1e-10));
            }
            finally { Object.DestroyImmediate(obstruction); }
            Physics.SyncTransforms();
            bucket.transform.rotation = Quaternion.identity;
            for (int i = 0; i < 100; i++)
            {
                unloader.Step(.1f);
                Assert.That(cutter.VolumeM3 + unloader.AirborneVolumeM3 + LandedVolume(),
                    Is.EqualTo(initial).Within(1e-10));
            }
            Assert.That(cutter.VolumeM3, Is.Zero);
            Assert.That(unloader.State, Is.EqualTo(BucketSoilUnloader.UnloadingState.Empty));
            // Emptying the bucket does not instantly land the final independently moving parcels.
            for (int i = 0; i < 50 && unloader.AirborneVolumeM3 > 0; i++)
            {
                unloader.Step(.1f);
                Assert.That(cutter.VolumeM3 + unloader.AirborneVolumeM3 + LandedVolume(),
                    Is.EqualTo(initial).Within(1e-10));
            }
            Assert.That(unloader.AirborneVolumeM3, Is.Zero);
            Assert.That(zone.VolumeM3, Is.EqualTo(initial - caught).Within(1e-10));
            Assert.That(LandedVolume(), Is.EqualTo(initial).Within(1e-10));
            Assert.That(cutter.DeliveredVolumeM3, Is.EqualTo(initial).Within(1e-10));
            Assert.That(zone.Records.Count, Is.GreaterThan(1));
            Assert.That(zone.Records[0].Bucket, Is.SameAs(cutter));
            double signed = 0;
            var mesh = receiver.GetComponent<MeshFilter>().sharedMesh;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
                signed += Vector3.Dot(vertices[triangles[i]], Vector3.Cross(vertices[triangles[i+1]], vertices[triangles[i+2]])) / 6.0;
            Assert.That(signed, Is.EqualTo(zone.VolumeM3).Within(1e-6));
        }

        private double LandedVolume(bool onlyGroundPiles = false)
        {
            double volume = 0;
            foreach (var zone in Object.FindObjectsByType<SoilReceivingZone>(FindObjectsSortMode.None))
            {
                if (onlyGroundPiles && !zone.isGroundPile) continue;
                foreach (var record in zone.Records)
                    if (record.Bucket == cutter) volume += record.VolumeM3;
            }
            return volume;
        }

        [Test]
        public void ResetPreservesOtherTerrainContributionsAndCapturedDensity()
        {
            var zone = receiver.AddComponent<SoilReceivingZone>();
            var otherGround = Terrain.CreateTerrainGameObject(data);
            var otherBucket = new GameObject("other source");
            try
            {
                var otherSoil = otherGround.AddComponent<DeformableSoilTerrain>();
                otherSoil.Initialize(otherGround.GetComponent<Terrain>());
                var other = otherBucket.AddComponent<BucketSoilCutter>();
                other.soil = otherSoil;
                other.densityKgPerM3 = 1200;
                other.tipLocalPosition = Vector3.zero;
                other.cutDirectionLocal = Vector3.forward;
                otherBucket.transform.position = new Vector3(2, .6f, 1);
                other.SamplePose();
                otherBucket.transform.position += Vector3.forward * .3f;
                other.SamplePose();
                double foreign = other.TransferTo(zone, other.VolumeM3);
                cutter.densityKgPerM3 = 999;
                double own = cutter.TransferTo(zone, .01);
                Assert.That(zone.MassKg, Is.EqualTo(foreign * 1200 + own * 1600).Within(1e-8));
                receiver.SetActive(false);
                cutter.ResetExcavation();
                Assert.That(zone.VolumeM3, Is.EqualTo(foreign).Within(1e-10));
                Assert.That(zone.MassKg, Is.EqualTo(foreign * 1200).Within(1e-8));
                Assert.That(zone.Records.Count, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(otherBucket); Object.DestroyImmediate(otherGround); }
        }

        [Test]
        public void TransferCapsAtReceiverCapacityAndResetRestoresConservation()
        {
            var type = typeof(BucketSoilCutter).Assembly.GetType("SoilReceivingZone");
            Assert.That(type, Is.Not.Null);
            var zone = receiver.AddComponent(type);
            type.GetField("capacityM3").SetValue(zone, .01f);
            var transfer = typeof(BucketSoilCutter).GetMethod("TransferTo");
            Assert.That(transfer, Is.Not.Null);
            double initial = cutter.VolumeM3;
            double moved = (double)transfer.Invoke(cutter, new object[] { zone, initial });
            Assert.That(moved, Is.EqualTo(.01).Within(1e-8));
            Assert.That(cutter.VolumeM3 + moved, Is.EqualTo(initial).Within(1e-10));
            Assert.That((double)type.GetProperty("MassKg").GetValue(zone), Is.EqualTo(moved * 1600).Within(1e-6));
            Assert.That((double)transfer.Invoke(cutter, new object[] { zone, initial }), Is.Zero);
            cutter.ResetExcavation();
            Assert.That(cutter.VolumeM3, Is.Zero);
            Assert.That((double)type.GetProperty("VolumeM3").GetValue(zone), Is.Zero);
            Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(1e-6));
        }
        [Test]
        public void UnloadingComponentsExist()
        {
            var assembly = typeof(BucketSoilCutter).Assembly;
            Assert.That(assembly.GetType("BucketSoilUnloader"), Is.Not.Null);
            Assert.That(assembly.GetType("SoilReceivingZone"), Is.Not.Null);
        }
    }
}
