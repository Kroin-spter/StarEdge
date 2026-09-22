using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public sealed class BladeSweepTests
    {
        private TerrainData source;
        private GameObject ground;
        private DeformableSoilTerrain soil;
        private TerrainData Data => ground.GetComponent<Terrain>().terrainData;

        /// <summary>Create a flat, real Terrain and initialize its runtime excavation copy.</summary>
        [SetUp]
        public void SetUp()
        {
            source = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++) heights[z, x] = .5f;
            source.SetHeights(0, 0, heights);
            ground = Terrain.CreateTerrainGameObject(source);
            soil = ground.AddComponent<DeformableSoilTerrain>();
            soil.Initialize(ground.GetComponent<Terrain>());
        }

        /// <summary>Destroy the runtime Terrain clone and its untouched source data.</summary>
        [TearDown]
        public void TearDown()
        {
            if (ground != null) UnityEngine.Object.DestroyImmediate(ground);
            if (source != null) UnityEngine.Object.DestroyImmediate(source);
        }

        /// <summary>Catch omission or signature drift of the geometric blade-sweep API.</summary>
        [Test]
        public void ExposesBladeSweepContract()
        {
            Assert.That(BladeSweepMethod(), Is.Not.Null,
                "Terrain cutting needs the eight-corner geometric sweep API.");
        }

        /// <summary>Catch restoration of the old radial halo ahead of the physical blade proxy.</summary>
        [Test]
        public void HorizontalSweepCutsOnlyInsideSweptBoxWithoutForwardHalo()
        {
            Vector3[] previous = Box(new Vector3(1, .7f, 2), new Vector3(.15f, .1f, .25f), Quaternion.identity);
            Vector3[] current = Box(new Vector3(2, .7f, 2), new Vector3(.15f, .1f, .25f), Quaternion.identity);

            Assert.That(Cut(previous, current, 10), Is.GreaterThan(0));
            Assert.That(Data.GetHeight(12, 16), Is.LessThan(.8f));
            Assert.That(Data.GetHeight(16, 16), Is.LessThan(.8f));
            Assert.That(Data.GetHeight(18, 16), Is.EqualTo(source.GetHeight(18, 16)));
            Assert.That(Data.GetHeight(16, 19), Is.EqualTo(source.GetHeight(16, 19)));
        }

        /// <summary>Catch projected-line implementations that lose a finite blade during vertical motion.</summary>
        [Test]
        public void PureVerticalSweepCutsInsideFiniteBoxFootprint()
        {
            Vector3[] previous = Box(new Vector3(2, 1.3f, 2), new Vector3(.4f, .1f, .3f),
                Quaternion.Euler(0, 0, 78));
            Vector3[] current = Box(new Vector3(2, .7f, 2), new Vector3(.4f, .1f, .3f),
                Quaternion.Euler(0, 0, 78));

            Assert.That(Cut(previous, current, 10), Is.GreaterThan(0));
            Assert.That(Data.GetHeight(16, 16), Is.LessThan(.8f));
            Assert.That(Data.GetHeight(22, 16), Is.EqualTo(source.GetHeight(22, 16)));
        }

        /// <summary>Catch sweeps that ignore endpoint rotation and rasterize translation alone.</summary>
        [Test]
        public void EndpointRotationAddsTheRotatedBoxFootprint()
        {
            Vector3 center = new Vector3(2, .7f, 2);
            Vector3 halfSize = new Vector3(.7f, .1f, .15f);
            Vector3[] previous = Box(center, halfSize, Quaternion.identity);
            Vector3[] current = Box(center, halfSize, Quaternion.Euler(0, 90, 0));

            Assert.That(Cut(previous, current, 10), Is.GreaterThan(0));
            Assert.That(Data.GetHeight(16, 20), Is.LessThan(.8f));
            Assert.That(Data.GetHeight(22, 22), Is.EqualTo(source.GetHeight(22, 22)));
        }

        /// <summary>Catch capacity overshoot and volume reports that differ from Terrain read-back.</summary>
        [Test]
        public void CapacityAndReturnedVolumeMatchAuthoritativeTerrainReadback()
        {
            Vector3[] box = Box(new Vector3(2, .6f, 2), new Vector3(.75f, .1f, .75f), Quaternion.identity);

            double removed = Cut(box, box, .03f);

            Assert.That(removed, Is.GreaterThan(0).And.LessThanOrEqualTo(.0300001));
            Assert.That(removed, Is.EqualTo(IndependentRemovedVolume()).Within(1e-8));
            Assert.That(source.GetHeight(16, 16), Is.EqualTo(1f));
        }

        /// <summary>Catch repeated excavation, malformed corners, and invalid capacity mutating terrain.</summary>
        [Test]
        public void RepeatedAndInvalidSweepsReturnZeroWithoutExtraTerrainChange()
        {
            Vector3[] box = Box(new Vector3(2, .7f, 2), new Vector3(.4f, .1f, .4f), Quaternion.identity);
            Assert.That(Cut(box, box, 10), Is.GreaterThan(0));
            double removed = IndependentRemovedVolume();
            Assert.That(Cut(box, box, 10), Is.Zero.Within(1e-8));
            Assert.That(Cut(null, box, 10), Is.Zero);
            Assert.That(Cut(new Vector3[7], box, 10), Is.Zero);
            var invalid = (Vector3[])box.Clone();
            invalid[3].x = float.NaN;
            Assert.That(Cut(box, invalid, 10), Is.Zero);
            Assert.That(Cut(box, box, 0), Is.Zero);
            Assert.That(Cut(box, box, float.PositiveInfinity), Is.Zero);
            Assert.That(IndependentRemovedVolume(), Is.EqualTo(removed).Within(1e-8));
        }

        private double Cut(Vector3[] previous, Vector3[] current, float capacity)
        {
            MethodInfo method = BladeSweepMethod();
            Assert.That(method, Is.Not.Null, "Terrain cutting needs the eight-corner geometric sweep API.");
            return Convert.ToDouble(method.Invoke(soil, new object[] { previous, current, capacity }));
        }

        private static MethodInfo BladeSweepMethod()
        {
            return typeof(DeformableSoilTerrain).GetMethod("CutBladeSweep",
                new[] { typeof(Vector3[]), typeof(Vector3[]), typeof(float) });
        }

        // Corner bit 0 selects local X, bit 1 local Y, and bit 2 local Z.
        private static Vector3[] Box(Vector3 center, Vector3 halfSize, Quaternion rotation)
        {
            var corners = new Vector3[8];
            for (int i = 0; i < corners.Length; i++)
            {
                var local = new Vector3(
                    (i & 1) == 0 ? -halfSize.x : halfSize.x,
                    (i & 2) == 0 ? -halfSize.y : halfSize.y,
                    (i & 4) == 0 ? -halfSize.z : halfSize.z);
                corners[i] = center + rotation * local;
            }
            return corners;
        }

        // Integrate cell corner heights independently from production vertex accounting.
        private double IndependentRemovedVolume()
        {
            double volume = 0;
            for (int z = 0; z < 32; z++)
                for (int x = 0; x < 32; x++)
                    for (int dz = 0; dz < 2; dz++)
                        for (int dx = 0; dx < 2; dx++)
                            volume += (source.GetHeight(x + dx, z + dz) - Data.GetHeight(x + dx, z + dz)) * .00390625;
            return volume;
        }
    }
}
