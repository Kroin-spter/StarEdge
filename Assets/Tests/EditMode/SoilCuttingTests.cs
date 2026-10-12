using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class SoilCuttingTests
    {
        private TerrainData source;
        private GameObject ground;
        private Component soil;
        private GameObject bucket;

        [SetUp]
        public void SetUp()
        {
            source = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++) heights[z, x] = .5f;
            source.SetHeights(0, 0, heights);
            ground = Terrain.CreateTerrainGameObject(source);
            Type type = typeof(Zx200DigController).Assembly.GetType("DeformableSoilTerrain");
            Assert.That(type, Is.Not.Null, "缺少真实地形切削实现");
            soil = ground.AddComponent(type);
            Call(soil, "Initialize", ground.GetComponent<Terrain>());
        }

        [TearDown]
        public void TearDown()
        {
            if (bucket != null) UnityEngine.Object.DestroyImmediate(bucket);
            if (ground != null) UnityEngine.Object.DestroyImmediate(ground);
            if (source != null) UnityEngine.Object.DestroyImmediate(source);
        }

        [Test]
        public void BucketCannotRelocateLoosePileFromTwelveMetresAway()
        {
            var cutter = CreateLoosePileSource();
            var pileObject = new GameObject("landed loose soil");
            try
            {
                var pile = FillLoosePile(pileObject, cutter, new Vector3(2, 1, 2));
                Vector3 anchor = pile.transform.position;
                double held = cutter.VolumeM3, emitted = cutter.EmittedVolumeM3;
                bucket.transform.position = anchor + new Vector3(12, .03f, 0);
                cutter.SamplePose(); // Position jump establishes a new baseline without cutting.
                bucket.transform.position += Vector3.forward * .1f;
                cutter.SamplePose();
                Assert.That(pile.VolumeM3, Is.EqualTo(.02).Within(1e-9));
                Assert.That(pile.transform.position, Is.EqualTo(anchor));
                Assert.That(cutter.VolumeM3, Is.EqualTo(held).Within(1e-9));
                Assert.That(cutter.EmittedVolumeM3, Is.EqualTo(emitted).Within(1e-9));
            }
            finally { UnityEngine.Object.DestroyImmediate(pileObject); }
        }

        [Test]
        public void ActualBladeWidthCanReexcavatePileMissedByToothCentre()
        {
            var cutter = CreateLoosePileSource();
            var pileObject = new GameObject("off-centre loose soil");
            try
            {
                var pile = FillLoosePile(pileObject, cutter, new Vector3(2.34f, 1, 2));
                double held = cutter.VolumeM3;
                double emitted = cutter.EmittedVolumeM3;
                bucket.transform.position = new Vector3(2, 1.05f, 1.8f);
                cutter.SamplePose();
                bucket.transform.position += Vector3.forward * .4f;
                cutter.SamplePose();
                Assert.That(pile.VolumeM3, Is.LessThan(.02), "Actual steel width must participate in loose-soil contact.");
                Assert.That(cutter.VolumeM3 - held, Is.EqualTo(.02 - pile.VolumeM3).Within(1e-9));
                Assert.That(cutter.EmittedVolumeM3, Is.EqualTo(emitted).Within(1e-9));
            }
            finally { UnityEngine.Object.DestroyImmediate(pileObject); }
        }

        private BucketSoilCutter CreateLoosePileSource()
        {
            bucket = new GameObject("loose-soil cutting tool");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = (DeformableSoilTerrain)soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .3f;
            cutter.SamplePose();
            return cutter;
        }

        private static SoilReceivingZone FillLoosePile(GameObject target, BucketSoilCutter cutter, Vector3 position)
        {
            target.transform.position = position;
            var pile = target.AddComponent<SoilReceivingZone>();
            pile.isGroundPile = true;
            pile.size = new Vector2(100, 100);
            pile.capacityM3 = 100000;
            pile.maximumFillHeight = 100;
            Assert.That(cutter.TransferTo(pile, .02), Is.EqualTo(.02).Within(1e-9));
            return pile;
        }

        [Test]
        public void LooseGroundPileBaseFollowsSlopingSoilSurface()
        {
            var terrain = (DeformableSoilTerrain)soil;
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++) heights[z, x] = .8f - .3f * z / 32f;
            terrain.SetPreparedSurface(heights);
            bucket = new GameObject("slope pile source");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = terrain;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            bucket.transform.position = new Vector3(2, 1.1f, 1.5f);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .3f;
            cutter.SamplePose();
            Assert.That(cutter.VolumeM3, Is.GreaterThan(.01));
            var pile = new GameObject("slope pile");
            try
            {
                pile.transform.position = new Vector3(2,
                    terrain.Terrain.SampleHeight(new Vector3(2, 0, 2)), 2);
                var zone = pile.AddComponent<SoilReceivingZone>();
                zone.isGroundPile = true;
                zone.size = new Vector2(100, 100);
                zone.capacityM3 = 100000;
                Assert.That(cutter.TransferTo(zone, .02), Is.GreaterThan(.005));
                Mesh mesh = pile.GetComponent<MeshFilter>().sharedMesh;
                for (int i = 2; i < mesh.vertexCount; i++)
                {
                    Vector3 world = pile.transform.TransformPoint(mesh.vertices[i]);
                    float terrainY = terrain.Terrain.SampleHeight(world) + terrain.transform.position.y;
                    Assert.That(Mathf.Abs(world.y - terrainY), Is.LessThan(.015f),
                        "Loose soil rim must rest on the actual slope instead of a flat floating disc");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(pile); }
        }

        [Test]
        public void PreparedSurfacePreservesSourceAndBecomesResetAndVolumeBaseline()
        {
            var terrain = (DeformableSoilTerrain)soil;
            float[,] heights = source.GetHeights(0, 0, 33, 33);
            heights[0, 0] = .3f;
            terrain.SetPreparedSurface(heights);
            Assert.That(terrain.RecalculateRemovedVolumeM3(), Is.Zero);
            Assert.That(source.GetHeights(0, 0, 1, 1)[0, 0], Is.EqualTo(.5f).Within(.0001));
            double cut = terrain.CutSweep(new Vector3(2, .6f, 1), new Vector3(2, .6f, 3), .3f, .2f);
            Assert.That(cut, Is.GreaterThan(0));
            Assert.Throws<InvalidOperationException>(() => terrain.SetPreparedSurface(heights));
            terrain.ResetSoil();
            Assert.That(terrain.RecalculateRemovedVolumeM3(), Is.Zero);
            Assert.That(terrain.Terrain.terrainData.GetHeights(0, 0, 1, 1)[0, 0], Is.EqualTo(.3f).Within(.0001));
        }

        [Test]
        public void CollectedSoilFollowsBucketWithoutLossAndResetClearsBothRepresentations()
        {
            bucket = new GameObject("loaded bucket");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = (DeformableSoilTerrain)soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            var visual = new GameObject("bucket soil");
            visual.transform.SetParent(bucket.transform, false);
            cutter.fillVisual = visual.transform;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            for (int i = 0; i < 6; i++)
            {
                bucket.transform.position += Vector3.forward * .25f;
                cutter.SamplePose();
            }
            double volume = cutter.VolumeM3;
            Assert.That(volume, Is.GreaterThan(.02).And.LessThanOrEqualTo(cutter.EffectiveCapacityM3));
            Assert.That(volume, Is.EqualTo(IndependentVolume()).Within(1e-5));
            var filter = visual.GetComponent<MeshFilter>();
            Vector3[] vertices = filter.sharedMesh.vertices;
            bucket.transform.position += Vector3.up * 3;
            cutter.SamplePose();
            for (int i = 0; i < 20; i++)
            {
                bucket.transform.rotation = Quaternion.Euler(i * 3, i * 10, i);
                cutter.SamplePose();
            }
            Assert.That(cutter.VolumeM3, Is.EqualTo(volume));
            CollectionAssert.AreEqual(vertices, filter.sharedMesh.vertices);
            Assert.That(visual.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(visual.transform.localScale, Is.EqualTo(Vector3.one));
            Assert.That(visual.GetComponentsInChildren<Rigidbody>(), Is.Empty);
            cutter.ResetExcavation();
            Assert.That(cutter.VolumeM3, Is.Zero);
            Assert.That(IndependentVolume(), Is.Zero.Within(1e-6));
            Assert.That(visual.activeSelf, Is.False);
        }

        [Test]
        public void MostlySidewaysBucketTravelDoesNotRemoveOrCollectSoil()
        {
            bucket = new GameObject("side scrape");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = (DeformableSoilTerrain)soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            bucket.transform.position += new Vector3(.3f, 0, .1f);
            cutter.SamplePose();
            Assert.That(IndependentVolume(), Is.Zero);
            Assert.That(cutter.VolumeM3, Is.Zero);
        }

        [Test]
        public void BucketMouthClipsSideSoilBeforeVolumeIsCounted()
        {
            var method = soil.GetType().GetMethod("CutBucketSweep");
            Assert.That(method, Is.Not.Null, "Bucket cutting must respect its actual mouth width.");
            double removed = Convert.ToDouble(method.Invoke(soil, new object[] {
                new Vector3(2, .6f, 1), new Vector3(2, .6f, 3), .6f, 10f, Vector3.right, .15f }));
            var data = ground.GetComponent<Terrain>().terrainData;
            Assert.That(data.GetHeight(16, 16), Is.LessThan(.7f));
            Assert.That(data.GetHeight(19, 16), Is.EqualTo(source.GetHeight(19, 16)));
            Assert.That(removed, Is.EqualTo(IndependentVolume()).Within(1e-5));
        }

        [Test]
        public void Cut_LowersRealTerrain_AndConservesMeasuredVolume()
        {
            double volume = Cut(new Vector3(1, .6f, 2), new Vector3(3, .6f, 2), .4f, 10f);
            Assert.That(volume, Is.GreaterThan(.1));
            Assert.That(ground.GetComponent<Terrain>().terrainData.GetHeight(16, 16), Is.LessThan(.65f));
            Assert.That(volume, Is.EqualTo(IndependentVolume()).Within(1e-5));
        }

        [Test]
        public void Capacity_PreventsOverfill_AndRepeatedSweepDoesNotCreateSoil()
        {
            double first = Cut(new Vector3(1, .6f, 2), new Vector3(3, .6f, 2), .4f, .03f);
            Assert.That(first, Is.GreaterThan(0).And.LessThanOrEqualTo(.030001));
            Assert.That(Cut(new Vector3(1, .6f, 2), new Vector3(3, .6f, 2), .4f, 0), Is.Zero);
            Call(soil, "ResetSoil");
            Cut(new Vector3(1, .6f, 2), new Vector3(3, .6f, 2), .4f, 10f);
            Assert.That(Cut(new Vector3(1, .6f, 2), new Vector3(3, .6f, 2), .4f, 10f), Is.LessThan(1e-5));
        }

        [Test]
        public void BoundaryAndFloor_RejectOutsideSweep_AndNeverCutBelowFloor()
        {
            Assert.That(Cut(new Vector3(-2, .6f, -2), new Vector3(-1, .6f, -1), .2f, 10f), Is.Zero);
            Cut(new Vector3(1, -2, 2), new Vector3(3, -2, 2), .4f, 10f);
            Assert.That(ground.GetComponent<Terrain>().terrainData.GetHeight(16, 16), Is.GreaterThanOrEqualTo(.049f));
            Assert.That(ground.GetComponent<Terrain>().terrainData.GetHeight(0, 0), Is.EqualTo(source.GetHeight(0, 0)));
        }

        [Test]
        public void Reset_RestoresTerrain_WithoutMutatingSourceAsset()
        {
            float original = source.GetHeight(16, 16);
            Cut(new Vector3(1, .6f, 2), new Vector3(3, .6f, 2), .4f, 10f);
            Assert.That(source.GetHeight(16, 16), Is.EqualTo(original));
            Call(soil, "ResetSoil");
            Assert.That(IndependentVolume(), Is.Zero.Within(1e-6));
            UnityEngine.Object.DestroyImmediate(soil);
            Assert.That(ground.GetComponent<Terrain>().terrainData, Is.SameAs(source));
        }

        [Test]
        public void ActualBucketPose_DigsForward_ButLiftAndJitterDoNotDig()
        {
            bucket = new GameObject("actual bucket transform");
            Type type = typeof(Zx200DigController).Assembly.GetType("BucketSoilCutter");
            Assert.That(type, Is.Not.Null);
            Component cutter = bucket.AddComponent(type);
            type.GetField("soil").SetValue(cutter, soil);
            type.GetField("tipLocalPosition").SetValue(cutter, Vector3.zero);
            type.GetField("cutDirectionLocal").SetValue(cutter, Vector3.forward);
            bucket.transform.position = new Vector3(2, .6f, 1);
            Call(cutter, "SamplePose");
            bucket.transform.position += new Vector3(0, .2f, .1f);
            Call(cutter, "SamplePose");
            Assert.That(IndependentVolume(), Is.Zero);
            bucket.transform.position += Vector3.forward * .001f;
            Call(cutter, "SamplePose");
            Assert.That(IndependentVolume(), Is.Zero);
            bucket.transform.position += Vector3.forward * .3f;
            Call(cutter, "SamplePose");
            Assert.That(IndependentVolume(), Is.GreaterThan(0));
            Assert.That(Convert.ToDouble(type.GetProperty("VolumeM3").GetValue(cutter)), Is.EqualTo(IndependentVolume()).Within(1e-5));
        }

        [Test]
        public void ContinuousSlowBucketTravel_AccumulatesUntilCutThreshold()
        {
            bucket = new GameObject("slow bucket");
            Type type = typeof(Zx200DigController).Assembly.GetType("BucketSoilCutter");
            Assert.That(type, Is.Not.Null);
            Component cutter = bucket.AddComponent(type);
            type.GetField("soil").SetValue(cutter, soil);
            type.GetField("tipLocalPosition").SetValue(cutter, Vector3.zero);
            type.GetField("cutDirectionLocal").SetValue(cutter, Vector3.forward);
            bucket.transform.position = new Vector3(2, .6f, 1);
            Call(cutter, "SamplePose");
            for (int i = 0; i < 10; i++)
            {
                bucket.transform.position += Vector3.forward * .004f;
                Call(cutter, "SamplePose");
            }
            Assert.That(IndependentVolume(), Is.GreaterThan(0), "低速连续切削不能因单帧位移小而永远失效");
        }

        [Test]
        public void CollisionException_IsLimitedToSoil_AndRestoredWhenCutterIsDestroyed()
        {
            bucket = new GameObject("collision bucket");
            var bucketCollider = bucket.AddComponent<BoxCollider>();
            var other = new GameObject("other solid ground");
            try
            {
                var otherCollider = other.AddComponent<BoxCollider>();
                Type type = typeof(Zx200DigController).Assembly.GetType("BucketSoilCutter");
                Assert.That(type, Is.Not.Null);
                Component cutter = bucket.AddComponent(type);
                type.GetField("soil").SetValue(cutter, soil);
                Call(cutter, "SamplePose");
                var terrainCollider = ground.GetComponent<TerrainCollider>();
                Assert.That(Physics.GetIgnoreCollision(bucketCollider, terrainCollider), Is.True);
                Assert.That(Physics.GetIgnoreCollision(bucketCollider, otherCollider), Is.False);
                UnityEngine.Object.DestroyImmediate(cutter);
                Assert.That(Physics.GetIgnoreCollision(bucketCollider, terrainCollider), Is.False,
                    "销毁切削器必须恢复该铲斗与土区的原碰撞状态");
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }

        [Test]
        public void VerticalPressCutsAndSeparatesCollectedFromDisplacedVolume()
        {
            bucket = new GameObject("vertical blade");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = (DeformableSoilTerrain)soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            bucket.transform.rotation = Quaternion.Euler(90, 0, 0);
            bucket.transform.position = new Vector3(2, .9f, 2);
            cutter.SamplePose();
            bucket.transform.position += Vector3.down * .2f;
            cutter.SamplePose();
            Assert.That(cutter.ExcavatedVolumeM3, Is.GreaterThan(0));
            Assert.That(cutter.VolumeM3 + cutter.DisplacedVolumeM3, Is.EqualTo(cutter.ExcavatedVolumeM3).Within(1e-8));
            Assert.That(IndependentVolume(), Is.EqualTo(cutter.ExcavatedVolumeM3).Within(1e-5));
        }

        [Test]
        public void DownwardOpenBucketCannotCollectWhileSlidingForward()
        {
            bucket = new GameObject("downward mouth");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = (DeformableSoilTerrain)soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            var unloader = bucket.AddComponent<BucketSoilUnloader>();
            unloader.openingNormalLocal = Vector3.down;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .3f;
            cutter.SamplePose();
            Assert.That(cutter.VolumeM3, Is.Zero);
            Assert.That(cutter.DisplacedVolumeM3, Is.GreaterThan(0));
            Assert.That(IndependentVolume(), Is.EqualTo(cutter.DisplacedVolumeM3).Within(1e-5));
        }

        [Test]
        public void GrazingMouthCollectsLessThanAlignedScoopAndAccountsForRemainder()
        {
            bucket = new GameObject("grazing blade");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = (DeformableSoilTerrain)soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = new Vector3(0, 1, .2f).normalized;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .2f;
            cutter.SamplePose();
            Assert.That(cutter.CollectionEfficiency, Is.InRange(0.01f, .5f));
            Assert.That(cutter.DisplacedVolumeM3, Is.GreaterThan(cutter.VolumeM3));
            double firstVolume = cutter.VolumeM3;
            cutter.cutDirectionLocal = Vector3.forward;
            bucket.transform.position += Vector3.forward * .2f;
            cutter.SamplePose();
            Assert.That(cutter.VolumeM3, Is.GreaterThan(firstVolume));
            Assert.That(cutter.CollectionEfficiency, Is.EqualTo(1));
            Assert.That(IndependentVolume(), Is.EqualTo(cutter.VolumeM3 + cutter.DisplacedVolumeM3).Within(1e-5));
        }

        [Test]
        public void SmallJointOscillationDoesNotEraseSlowNetScoopingTravel()
        {
            bucket = new GameObject("slow physical scoop");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = (DeformableSoilTerrain)soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Vector3.forward;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            for (int i = 0; i < 20; i++)
            {
                bucket.transform.position += Vector3.forward * .004f;
                cutter.SamplePose();
                bucket.transform.position -= Vector3.forward * .001f;
                cutter.SamplePose();
            }
            Assert.That(cutter.VolumeM3, Is.GreaterThan(0), "Sub-millimetre reversal must not erase all accumulated cutting travel");
            Assert.That(IndependentVolume(), Is.EqualTo(cutter.VolumeM3).Within(1e-5));
        }

        [TestCase(0f)]
        [TestCase(8f)]
        public void VerticalCutCollectionUsesCurrentMotionRatherThanStaleForwardTravel(float downwardMouthDegrees)
        {
            bucket = new GameObject("forward then press");
            var cutter = bucket.AddComponent<BucketSoilCutter>();
            cutter.soil = (DeformableSoilTerrain)soil;
            cutter.tipLocalPosition = Vector3.zero;
            cutter.cutDirectionLocal = Quaternion.Euler(downwardMouthDegrees, 0, 0) * Vector3.forward;
            bucket.transform.position = new Vector3(2, .6f, 1);
            cutter.SamplePose();
            bucket.transform.position += Vector3.forward * .014f;
            cutter.SamplePose();
            bucket.transform.position += Vector3.down * .014f;
            cutter.SamplePose();
            Assert.That(cutter.ExcavatedVolumeM3, Is.GreaterThan(0));
            Assert.That(cutter.CollectionEfficiency, Is.EqualTo(cutter.EstimateCollectionEfficiency(Vector3.down)).Within(1e-6));
            Assert.That(cutter.VolumeM3, Is.LessThan(cutter.ExcavatedVolumeM3 * .25));
            Assert.That(IndependentVolume(), Is.EqualTo(cutter.VolumeM3 + cutter.DisplacedVolumeM3).Within(1e-5));
        }

        private double Cut(Vector3 from, Vector3 to, float radius, float capacity) =>
            Convert.ToDouble(Call(soil, "CutSweep", from, to, radius, capacity));

        private static object Call(Component component, string method, params object[] args) =>
            component.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance).Invoke(component, args);

        // 对四角均值的每个实际网格单元独立求体积，不调用生产统计函数。
        private double IndependentVolume()
        {
            var data = ground.GetComponent<Terrain>().terrainData;
            double volume = 0;
            for (int z = 0; z < 32; z++)
                for (int x = 0; x < 32; x++)
                    for (int dz = 0; dz < 2; dz++)
                        for (int dx = 0; dx < 2; dx++)
                            volume += (source.GetHeight(x + dx, z + dz) - data.GetHeight(x + dx, z + dz)) * .00390625;
            return volume;
        }
    }
}
