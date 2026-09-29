using NUnit.Framework;

namespace ExcavatorControls.Tests
{
    public sealed class ConstructionSiteSlopeMaskTests
    {
        /// <summary>Follow a diagonal first wall and leave both benches and a second wall on Cesium.</summary>
        [Test]
        public void FirstWallTracksChangingCrestWithoutCoatingFlatGround()
        {
            var ground = new float[100, 12];
            for (int x = 0; x < 12; x++)
                for (int z = 0; z < 100; z++)
                {
                    int foot = 20 + x * 2;
                    ground[z, x] = z < foot ? 0f : z < foot + 10 ? (z - foot) * .4f : 4f;
                    if (z >= 80) ground[z, x] += (z - 80) * .4f;
                }

            float[,] mask = ConstructionSiteSlopeMask.Build(ground, .5f, 0f);
            Assert.That(mask[25, 0], Is.GreaterThan(.5f));
            Assert.That(mask[47, 11], Is.GreaterThan(.5f));
            Assert.That(mask[10, 0], Is.Zero);
            Assert.That(mask[60, 0], Is.Zero);
            Assert.That(mask[90, 0], Is.Zero);
        }
    }
}
