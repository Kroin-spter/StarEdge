using System;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class SoilDepositionTests
    {
        private TerrainData source;
        private GameObject ground;
        private DeformableSoilTerrain soil;
        private TerrainData Data => ground.GetComponent<Terrain>().terrainData;

        /// <summary>Create a translated real Terrain with an untouched source asset.</summary>
        [SetUp]
        public void SetUp()
        {
            source = new TerrainData { heightmapResolution = 33, size = new Vector3(4, 2, 4) };
            var heights = new float[33, 33];
            for (int z = 0; z < 33; z++)
                for (int x = 0; x < 33; x++) heights[z, x] = .5f;
            source.SetHeights(0, 0, heights);
            ground = Terrain.CreateTerrainGameObject(source);
            ground.transform.position = new Vector3(10, 3, -5);
            soil = ground.AddComponent<DeformableSoilTerrain>();
            soil.Initialize(ground.GetComponent<Terrain>());
        }

        /// <summary>Release the runtime clone and source data after each test.</summary>
        [TearDown]
        public void TearDown()
        {
            if (ground != null) UnityEngine.Object.DestroyImmediate(ground);
            if (source != null) UnityEngine.Object.DestroyImmediate(source);
        }

        /// <summary>Catch fictitious volume, biased mounds, and edits outside the local footprint.</summary>
        [Test]
        public void DepositRaisesRadialMoundAndReturnsIndependentlyMeasuredVolume()
        {
            double added = Deposit(new Vector3(12, 4, -3), .75f, .12);
            Assert.That(added, Is.GreaterThan(.11).And.LessThanOrEqualTo(.12));
            Assert.That(added, Is.EqualTo(IndependentAddedVolume()).Within(1e-8));
            Assert.That(soil.RecalculateRemovedVolumeM3(), Is.EqualTo(-added).Within(1e-8));
            Assert.That(Data.GetHeight(16, 16), Is.GreaterThan(Data.GetHeight(19, 16)));
            Assert.That(Data.GetHeight(19, 16), Is.EqualTo(Data.GetHeight(16, 19)).Within(.0002f));
            Assert.That(Data.GetHeight(23, 16), Is.EqualTo(source.GetHeight(23, 16)));
            Assert.That(source.GetHeight(16, 16), Is.EqualTo(source.GetHeight(0, 0)));
            Assert.That(soil.SoilCollider.terrainData, Is.SameAs(Data));
            Assert.That(Data, Is.Not.SameAs(source));
        }

        /// <summary>Catch deposition that cannot be cut again or corrupts reset accounting.</summary>
        [Test]
        public void CutDepositRecutAndResetConserveSignedTerrainVolume()
        {
            Vector3 point = new Vector3(12, 3.6f, -3);
            double first = soil.CutSweep(point, point, .75f, 1f);
            double deposited = Deposit(point, .75f, .12);
            Assert.That(deposited, Is.GreaterThan(.1));
            double second = soil.CutSweep(point, point, .75f, 1f);
            Assert.That(second, Is.GreaterThan(.08));
            Assert.That(first + second - deposited, Is.EqualTo(-IndependentAddedVolume()).Within(1e-7));
            Assert.That(soil.RecalculateRemovedVolumeM3(), Is.EqualTo(-IndependentAddedVolume()).Within(1e-7));
            soil.ResetSoil();
            Assert.That(IndependentAddedVolume(), Is.Zero.Within(1e-10));
        }

        /// <summary>Catch rim mutation, height overflow, or over-reporting at finite capacity.</summary>
        [Test]
        public void DepositClampsHeightPreservesRimAndStopsAtCapacity()
        {
            double added = Deposit(new Vector3(10, 4, -5), .75f, 100);
            Assert.That(added, Is.GreaterThan(0).And.LessThan(100));
            Assert.That(added, Is.EqualTo(IndependentAddedVolume()).Within(1e-8));
            for (int i = 0; i < 33; i++)
            {
                Assert.That(Data.GetHeight(i, 0), Is.EqualTo(source.GetHeight(i, 0)));
                Assert.That(Data.GetHeight(0, i), Is.EqualTo(source.GetHeight(0, i)));
            }
            Assert.That(Data.GetHeight(1, 1), Is.InRange(1.99f, 2f));
            Assert.That(Deposit(new Vector3(10, 4, -5), .75f, 100), Is.Zero);
        }

        /// <summary>Catch invalid inputs and requests below terrain quantization changing the terrain.</summary>
        [Test]
        public void InvalidOutsideAndTinyRequestsDoNotCreateSoil()
        {
            Vector3 point = new Vector3(12, 4, -3);
            Assert.That(Deposit(point, .75f, 1e-15), Is.Zero);
            Assert.That(Deposit(point, .75f, 0), Is.Zero);
            Assert.That(Deposit(point, .75f, -1), Is.Zero);
            Assert.That(Deposit(point, .75f, double.NaN), Is.Zero);
            Assert.That(Deposit(point, .75f, double.PositiveInfinity), Is.Zero);
            Assert.That(Deposit(point, float.NaN, 1), Is.Zero);
            Assert.That(Deposit(point, -1, 1), Is.Zero);
            Assert.That(Deposit(new Vector3(float.NaN, 0, 0), 1, 1), Is.Zero);
            Assert.That(Deposit(new Vector3(20, 4, 20), .75f, 1), Is.Zero);
            Assert.That(IndependentAddedVolume(), Is.Zero);
        }

        private double Deposit(Vector3 point, float radius, double volume)
        {
            var method = typeof(DeformableSoilTerrain).GetMethod("Deposit", new[] { typeof(Vector3), typeof(float), typeof(double) });
            Assert.That(method, Is.Not.Null, "Terrain needs bounded, measured soil deposition.");
            return Convert.ToDouble(method.Invoke(soil, new object[] { point, radius, volume }));
        }

        // Integrate actual cell corner heights, independently of production vertex accounting.
        private double IndependentAddedVolume()
        {
            double volume = 0;
            for (int z = 0; z < 32; z++)
                for (int x = 0; x < 32; x++)
                    for (int dz = 0; dz < 2; dz++)
                        for (int dx = 0; dx < 2; dx++)
                            volume += (Data.GetHeight(x + dx, z + dz) - source.GetHeight(x + dx, z + dz)) * .00390625;
            return volume;
        }
    }
}
