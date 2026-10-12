using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public sealed class MoundTopExcavationProfileTests
    {
        private static Vector2 Evaluate(float x, float z)
        {
            Type type = typeof(CesiumMoundSite).Assembly.GetType("MoundTopExcavationProfile");
            Assert.That(type, Is.Not.Null, "缺少堆顶连续可挖土堆生成器");
            return (Vector2)type.GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { new Vector2(x, z), -2f, 1.3f });
        }

        /// <summary>Removing the support floor protection must fail this test.</summary>
        [Test]
        public void TracksStandOnFlatProtectedCore()
        {
            Vector2 center = Evaluate(0, 0), track = Evaluate(1.5f, 1.7f);
            Assert.That(center.x, Is.EqualTo(1.3f).Within(.001f));
            Assert.That(track.x, Is.EqualTo(center.x).Within(.001f));
            Assert.That(center.y, Is.EqualTo(center.x).Within(.001f));
            Assert.That(track.y, Is.EqualTo(track.x).Within(.001f));
        }

        /// <summary>Replacing the continuous mound with a narrow strip must fail this test.</summary>
        [TestCase(0f, 5f)]
        [TestCase(4f, 4f)]
        [TestCase(-4f, 4f)]
        public void FrontAndCornerHaveLowerCuttableSoil(float x, float z)
        {
            Vector2 sample = Evaluate(x, z);
            Assert.That(sample.x, Is.LessThan(1.3f));
            Assert.That(sample.x, Is.GreaterThan(-2f));
            Assert.That(sample.x - sample.y, Is.GreaterThan(.5f));
        }

        /// <summary>A cliff at the authoring boundary must fail this test.</summary>
        [Test]
        public void DistantFootReachesLowerBench()
        {
            Vector2 foot = Evaluate(12, 12);
            Assert.That(foot.x, Is.EqualTo(-2f).Within(.001f));
            Assert.That(foot.y, Is.LessThanOrEqualTo(foot.x));
        }

        /// <summary>A parking pad that supports the root but misses an angled front wheel must fail.</summary>
        [TestCase(-9.47f, -2.43f)]
        [TestCase(-7.733f, -3.39f)]
        [TestCase(-5.725f, .619f)]
        [TestCase(-7.156f, 1.412f)]
        public void AngledTruckWheelsStandOnLowerLevelPad(float x, float z)
        {
            Assert.That(Evaluate(x, z).x, Is.EqualTo(-2f).Within(.005f));
        }
    }
}
