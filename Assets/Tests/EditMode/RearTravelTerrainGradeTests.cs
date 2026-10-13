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

        [Test]
        public void AccessPatchSoftensLocalRidgeWithoutFlatteningDescentOrMovingProtectedGround()
        {
            var before = new float[Resolution, Resolution];
            for (int z = 0; z < Resolution; z++)
                for (int x = 0; x < Resolution; x++)
                {
                    float worldZ = Origin.z + z * .5f;
                    before[z, x] = .6f + worldZ * .02f;
                }
            before[12, 20] += .2f; // A sharp 2 m ledge on an otherwise descending rear access slope.

            Vector2 center = new Vector2(0f, -4f);
            Vector2 truck = new Vector2(-5f, -4f);
            float[,] after = RearTravelTerrainGrade.SmoothAccessPatch(before, Origin, Size,
                center, 5f, Vector2.zero, truck, Quaternion.identity, Boundary, out bool[,] scope);

            Assert.That(after[12, 20], Is.LessThan(before[12, 20] - .05f));
            Assert.That(after[12, 20], Is.GreaterThan(before[8, 20])); // Retain the broad downhill trend.
            Assert.That(after[12, 20], Is.LessThan(before[16, 20]));
            Assert.That(after[12, 20], Is.GreaterThan(before[12, 20] - .2f));
            Assert.That(after[20, 20], Is.EqualTo(before[20, 20]).Within(1e-6f)); // Machine core.
            Assert.That(after[12, 10], Is.EqualTo(before[12, 10]).Within(1e-6f)); // Truck center.
            Assert.That(after[24, 20], Is.EqualTo(before[24, 20]).Within(1e-6f)); // Forward face.
            Assert.That(after[12, 2], Is.EqualTo(before[12, 2]).Within(1e-6f)); // Polygon edge.
            Assert.That(after[2, 20], Is.EqualTo(before[2, 20]).Within(1e-6f)); // Patch exterior.

            // Include the feather and its immediate neighbours so a new seam cannot hide outside scope.
            float peakBefore = 0f, peakAfter = 0f;
            for (int z = 1; z < Resolution - 1; z++)
                for (int x = 1; x < Resolution - 1; x++)
                {
                    if (!scope[z, x] && !scope[z, x + 1] && !scope[z + 1, x]) continue;
                    peakBefore = Mathf.Max(peakBefore,
                        Mathf.Abs(before[z, x] - before[z, x + 1]),
                        Mathf.Abs(before[z, x] - before[z + 1, x]));
                    peakAfter = Mathf.Max(peakAfter,
                        Mathf.Abs(after[z, x] - after[z, x + 1]),
                        Mathf.Abs(after[z, x] - after[z + 1, x]));
                }
            Assert.That(scope[12, 20], Is.True);
            Assert.That(peakAfter, Is.LessThan(peakBefore * .75f));
            Assert.That(before[12, 20], Is.EqualTo(.72f).Within(1e-6f)); // Input was not mutated.
        }
    }
}
