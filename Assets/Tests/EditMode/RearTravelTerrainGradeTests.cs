using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public sealed class RearTravelTerrainGradeTests
    {
        private const int Resolution = 41;
        private static readonly Vector3 Origin = new Vector3(-10f, 0f, -10f);
        private static readonly Vector3 Size = new Vector3(20f, 10f, 20f);
        private static readonly Vector2[] Boundary = {
            new Vector2(-9f, -9f), new Vector2(9f, -9f),
            new Vector2(9f, 9f), new Vector2(-9f, 9f)
        };

        [Test]
        public void RearRampReducesLedgeButKeepsCoreFrontAndMapBoundary()
        {
            var before = new float[Resolution, Resolution];
            for (int z = 0; z < Resolution; z++)
                for (int x = 0; x < Resolution; x++)
                {
                    float wx = Origin.x + x * .5f, wz = Origin.z + z * .5f;
                    before[z, x] = Mathf.Sqrt(wx * wx + wz * wz) < 3.5f ? .7f : .3f;
                }

            float[,] after = RearTravelTerrainGrade.Grade(before, Origin, Size, Vector2.zero,
                Quaternion.identity, Boundary, out bool[,] scope);
            float peakBefore = RearTravelTerrainGrade.MaxNeighborSlope(before, Size, scope);
            float peakAfter = RearTravelTerrainGrade.MaxNeighborSlope(after, Size, scope);
            Assert.That(peakAfter, Is.LessThan(peakBefore * .75f));
            Assert.That(after[12, 20], Is.GreaterThan(after[4, 20])); // connected descent, not a flat rear island
            for (int z = 0; z < Resolution; z++)
                for (int x = 0; x < Resolution; x++)
                {
                    float wx = Origin.x + x * .5f, wz = Origin.z + z * .5f;
                    if (wx * wx + wz * wz <= 2.5f * 2.5f || wz >= 1f ||
                        Mathf.Abs(wx) >= 8.5f || Mathf.Abs(wz) >= 8.5f)
                        Assert.That(after[z, x], Is.EqualTo(before[z, x]).Within(1e-6f));
                }
        }
    }
}
