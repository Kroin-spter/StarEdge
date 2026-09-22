using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class SoilMouthFlowTests
    {
        private GameObject ground;
        private GameObject bucket;
        private TerrainData data;
        private BucketSoilCutter cutter;
        private BucketSoilUnloader unloader;

        [SetUp]
        public void SetUp()
        {
            data = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++)
                    heights[z, x] = .5f;
            data.SetHeights(0, 0, heights);
            ground = Terrain.CreateTerrainGameObject(data);
            var soil = ground.AddComponent<DeformableSoilTerrain>();
            soil.Initialize(ground.GetComponent<Terrain>());

            bucket = new GameObject("mouth flow bucket");
            cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            LoadBucket();

            unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.openingNormalLocal = Vector3.down;
            unloader.dwellSeconds = 0;
            unloader.flowM3PerSecond = .1f;
        }

        [TearDown]
        public void TearDown()
        {
            if (cutter != null) cutter.ResetExcavation();
            Object.DestroyImmediate(bucket);
            Object.DestroyImmediate(ground);
            Object.DestroyImmediate(data);
        }

        [Test]
        public void ParcelsInsideHiddenHeightfieldRemainAirborne()
        {
            MoveBucketToDischarge();
            double emitted = unloader.Step(.1f);
            Assert.That(emitted, Is.GreaterThan(0));
            var runtime = cutter.soil.Terrain.terrainData;
            runtime.SetHoles(0, 0, new bool[runtime.holesResolution, runtime.holesResolution]);
            Physics.SyncTransforms();
            var flight = bucket.GetComponent<SoilFlightTransport>();
            var parcels = (IList)typeof(SoilFlightTransport).GetField("parcels", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(flight);
            foreach (var parcel in parcels)
                parcel.GetType().GetField("position", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(parcel, new Vector3(2, .5f, 2));
            typeof(SoilFlightTransport).GetMethod("Advance", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(flight, new object[] { .02f, new SoilReceivingZone[0], (LayerMask)~0 });
            Assert.That(flight.AirborneVolumeM3, Is.EqualTo(emitted).Within(1e-10));
            Assert.That(flight.SurfacePendingVolumeM3, Is.Zero);
            Assert.That(cutter.DeliveredVolumeM3, Is.Zero);
        }

        [Test]
        public void LoadedBucketEmitsSeveralParcelsAcrossWorldScaledMouth()
        {
            cutter.innerMouthHalfWidth = .4f;
            bucket.transform.localScale = new Vector3(1.5f, 1, 1);
            MoveBucketToDischarge();

            double emitted = unloader.Step(.1f);
            List<ParcelSnapshot> parcels = ReadParcels();

            Assert.That(emitted, Is.GreaterThan(0));
            Assert.That(parcels.Count, Is.InRange(3, 7), "A bounded set of outlets must represent the mouth flow.");
            Assert.That(MaxLocalX(parcels) - MinLocalX(parcels), Is.GreaterThan(.6f),
                "Emission must span the scaled 1.2 m mouth instead of starting at one point.");
            Assert.That(SumVolume(parcels), Is.EqualTo(emitted).Within(1e-10));
        }

        [Test]
        public void RolledBucketAllocatesMoreFlowToGravityLowSide()
        {
            MoveBucketToDischarge();
            bucket.transform.rotation = Quaternion.Euler(0, 0, 30);
            Physics.SyncTransforms();

            double emitted = unloader.Step(.1f);
            List<ParcelSnapshot> parcels = ReadParcels();
            double weightedLocalX = 0;
            foreach (ParcelSnapshot parcel in parcels)
                weightedLocalX += bucket.transform.InverseTransformPoint(parcel.position).x * parcel.volume;

            Assert.That(emitted, Is.GreaterThan(0));
            Assert.That(weightedLocalX / emitted, Is.LessThan(-.03),
                "Negative local X is lower when the bucket is rolled +30 degrees around forward.");
            Assert.That(SumVolume(parcels), Is.EqualTo(emitted).Within(1e-10));
        }

        [Test]
        public void SideObstructionBlocksOnlyCoveredOutletBeforeInventoryDebit()
        {
            cutter.innerMouthHalfWidth = .4f;
            MoveBucketToDischarge();
            var obstruction = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                obstruction.name = "right mouth obstruction";
                obstruction.transform.position = cutter.TipWorldPosition + bucket.transform.right * .35f;
                obstruction.transform.localScale = new Vector3(.22f, .22f, .22f);
                Physics.SyncTransforms();
                double initial = cutter.VolumeM3;
                double fullRequest = System.Math.Min(initial,
                    .1 * unloader.flowM3PerSecond * Mathf.Lerp(.3f, 1,
                        Mathf.Clamp01((float)(initial / cutter.EffectiveCapacityM3))));

                double emitted = unloader.Step(.1f);
                List<ParcelSnapshot> parcels = ReadParcels();

                Assert.That(emitted, Is.GreaterThan(0).And.LessThan(fullRequest - 1e-8),
                    "A covered side must reduce this step's debit without suppressing free outlets.");
                Assert.That(cutter.VolumeM3, Is.EqualTo(initial - emitted).Within(1e-10));
                Assert.That(unloader.AirborneVolumeM3, Is.EqualTo(emitted).Within(1e-10));
                Assert.That(SumVolume(parcels), Is.EqualTo(emitted).Within(1e-10));
                foreach (ParcelSnapshot parcel in parcels)
                    Assert.That(obstruction.GetComponent<Collider>().bounds.Contains(parcel.position), Is.False,
                        "No parcel may originate inside the external solid.");
            }
            finally
            {
                Object.DestroyImmediate(obstruction);
            }
        }

        [Test]
        public void NearlyEmptyBucketUsesFewerOutletsAcrossNarrowerBand()
        {
            cutter.innerMouthHalfWidth = .4f;
            MoveBucketToDischarge();
            unloader.Step(.1f);
            List<ParcelSnapshot> fullFlow = ReadParcels();
            float fullSpan = MaxLocalX(fullFlow) - MinLocalX(fullFlow);

            cutter.ResetExcavation();
            cutter.capacityM3 = .00008f;
            unloader.openingNormalLocal = Vector3.forward;
            LoadBucket();
            Assert.That(cutter.VolumeM3, Is.GreaterThan(0).And.LessThan(.0001));
            unloader.openingNormalLocal = Vector3.down;
            MoveBucketToDischarge();
            int before = ReadParcels().Count;

            double emitted = unloader.Step(.1f);
            List<ParcelSnapshot> lateFlow = ReadParcels().GetRange(before, ReadParcels().Count - before);
            float lateSpan = MaxLocalX(lateFlow) - MinLocalX(lateFlow);

            Assert.That(emitted, Is.EqualTo(cutter.EmittedVolumeM3).Within(1e-10));
            Assert.That(lateFlow.Count, Is.LessThan(fullFlow.Count));
            Assert.That(lateSpan, Is.LessThan(fullSpan * .6f), "The last trickle should not occupy the full mouth width.");
            Assert.That(SumVolume(lateFlow), Is.EqualTo(emitted).Within(1e-10));
        }

        [Test]
        public void RisingReceiverSurfaceAcceptsParcelsEngulfedByEarlierLandings()
        {
            var receiverObject = new GameObject("narrow rising receiver");
            try
            {
                var receiver = receiverObject.AddComponent<SoilReceivingZone>();
                receiverObject.transform.position = new Vector3(10, 0, 0);
                receiver.size = new Vector2(.2f, .2f);
                receiver.capacityM3 = .04f;
                cutter.innerMouthHalfWidth = .02f;
                unloader.receivers = new[] { receiver };
                MoveBucketToDischarge();

                double emitted = unloader.Step(.1f);
                unloader.flowM3PerSecond = 0;
                bucket.transform.rotation = Quaternion.Euler(180, 0, 0);
                for (int i = 0; i < 100; i++) unloader.Step(.02f);

                Assert.That(emitted, Is.GreaterThan(0));
                Assert.That(receiver.VolumeM3, Is.EqualTo(emitted).Within(1e-10));
                Assert.That(unloader.AirborneVolumeM3, Is.Zero,
                    "A surface raised by an earlier parcel must not strand another parcel below it.");
                Assert.That(cutter.DeliveredVolumeM3, Is.EqualTo(emitted).Within(1e-10));
            }
            finally
            {
                Object.DestroyImmediate(receiverObject);
            }
        }

        [Test]
        public void DisabledUnloaderStillLandsExistingParcelsInConfiguredReceiver()
        {
            var receiverObject = new GameObject("receiver after unloader disabled");
            try
            {
                var receiver = receiverObject.AddComponent<SoilReceivingZone>();
                receiverObject.transform.position = new Vector3(10, 0, 0);
                unloader.receivers = new[] { receiver };
                MoveBucketToDischarge();
                double emitted = unloader.Step(.1f);
                Assert.That(emitted, Is.GreaterThan(0));
                unloader.enabled = false;
                var flight = bucket.GetComponent<SoilFlightTransport>();
                var step = typeof(SoilFlightTransport).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < 100; i++) step.Invoke(flight, null);
                Assert.That(receiver.VolumeM3, Is.EqualTo(emitted).Within(1e-10));
                Assert.That(flight.AirborneVolumeM3, Is.Zero);
            }
            finally { Object.DestroyImmediate(receiverObject); }
        }

        private void LoadBucket()
        {
            bucket.transform.SetPositionAndRotation(new Vector3(2, .6f, 1), Quaternion.identity);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .3f;
            cutter.SamplePose();
            Assert.That(cutter.VolumeM3, Is.GreaterThan(0));
        }

        private void MoveBucketToDischarge()
        {
            bucket.transform.SetPositionAndRotation(new Vector3(10, 3, 0), Quaternion.identity);
            Physics.SyncTransforms();
        }

        private List<ParcelSnapshot> ReadParcels()
        {
            var flight = bucket.GetComponent<SoilFlightTransport>();
            var field = typeof(SoilFlightTransport).GetField("parcels", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            var snapshots = new List<ParcelSnapshot>();
            foreach (object parcel in (IEnumerable)field.GetValue(flight))
            {
                TypeInfo type = parcel.GetType().GetTypeInfo();
                snapshots.Add(new ParcelSnapshot
                {
                    position = (Vector3)type.GetField("position", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(parcel),
                    volume = (double)type.GetField("volume", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(parcel)
                });
            }
            return snapshots;
        }

        private float MinLocalX(List<ParcelSnapshot> parcels)
        {
            float value = float.PositiveInfinity;
            foreach (ParcelSnapshot parcel in parcels)
                value = Mathf.Min(value, bucket.transform.InverseTransformPoint(parcel.position).x);
            return value;
        }

        private float MaxLocalX(List<ParcelSnapshot> parcels)
        {
            float value = float.NegativeInfinity;
            foreach (ParcelSnapshot parcel in parcels)
                value = Mathf.Max(value, bucket.transform.InverseTransformPoint(parcel.position).x);
            return value;
        }

        private static double SumVolume(List<ParcelSnapshot> parcels)
        {
            double volume = 0;
            foreach (ParcelSnapshot parcel in parcels) volume += parcel.volume;
            return volume;
        }

        private struct ParcelSnapshot
        {
            internal Vector3 position;
            internal double volume;
        }
    }
}
