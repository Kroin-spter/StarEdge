using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class Zx200FixedBasePhysicsTests
    {
        private GameObject robot;
        private Zx200FixedBasePhysics physics;

        [SetUp]
        public void SetUp()
        {
            robot = new GameObject("zx200");
            ArticulationBody track = CreateBody("track_link", robot.transform);
            ArticulationBody swing = CreateBody("body_link", track.transform);
            ArticulationBody boom = CreateBody("boom_link", swing.transform);
            ArticulationBody arm = CreateBody("arm_link", boom.transform);
            CreateBody("bucket_link", arm.transform);
            physics = robot.AddComponent<Zx200FixedBasePhysics>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(robot);
        }

        /// <summary>重复配置只生成一个真实后部配重，不改变上车体原有质量。</summary>
        [Test]
        public void Configure_CreatesOneRearCounterweight()
        {
            Assert.That(physics.Configure(), Is.True);
            Assert.That(physics.Configure(), Is.True);
            var weight = physics.swingBody.transform.Find("counterweight_link");
            Assert.That(weight, Is.Not.Null);
            var body = weight.GetComponent<ArticulationBody>();
            Assert.That(body.jointType, Is.EqualTo(ArticulationJointType.FixedJoint));
            Assert.That(body.mass, Is.EqualTo(2000f));
            Assert.That(body.useGravity, Is.True);
            Assert.That(weight.localPosition, Is.EqualTo(physics.counterweightLocalPosition));
            Assert.That(physics.swingBody.mass, Is.EqualTo(8000f));
            int count = 0;
            foreach (Transform child in physics.swingBody.transform)
                if (child.name == "counterweight_link") count++;
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Configure_AppliesFixedBaseProxyMassesAndGravity()
        {
            bool configured = physics.Configure();

            Assert.That(configured, Is.True);
            Assert.That(physics.rootBody.immovable, Is.True);
            Assert.That(physics.rootBody.mass, Is.EqualTo(7000f));
            Assert.That(physics.swingBody.mass, Is.EqualTo(8000f));
            Assert.That(physics.boomBody.mass, Is.EqualTo(2000f));
            Assert.That(physics.armBody.mass, Is.EqualTo(1200f));
            Assert.That(physics.bucketBody.mass, Is.EqualTo(800f));
            Assert.That(physics.swingBody.useGravity, Is.True);
            Assert.That(physics.boomBody.useGravity, Is.True);
            Assert.That(physics.armBody.useGravity, Is.True);
            Assert.That(physics.bucketBody.useGravity, Is.True);
            Assert.That(physics.ValidateConfiguration(out string error), Is.True, error);
        }

        [Test]
        public void Configure_IgnoresAdjacentCollisionWithoutDisablingColliders()
        {
            BoxCollider trackCollider = physics.transform
                .Find("track_link").gameObject.AddComponent<BoxCollider>();
            BoxCollider swingCollider = physics.transform
                .Find("track_link/body_link").gameObject.AddComponent<BoxCollider>();
            BoxCollider boomCollider = physics.transform
                .Find("track_link/body_link/boom_link").gameObject.AddComponent<BoxCollider>();

            physics.Configure();

            Assert.That(Physics.GetIgnoreCollision(trackCollider, swingCollider), Is.True);
            Assert.That(Physics.GetIgnoreCollision(swingCollider, boomCollider), Is.True);
            Assert.That(trackCollider.enabled, Is.True);
            Assert.That(swingCollider.enabled, Is.True);
            Assert.That(boomCollider.enabled, Is.True);
        }

        [Test]
        public void Configure_ReenablesColliderDisabledByLegacyVisualSetup()
        {
            Collider bucketCollider = physics.transform
                .Find("track_link/body_link/boom_link/arm_link/bucket_link")
                .gameObject.AddComponent<BoxCollider>();
            bucketCollider.enabled = false;

            bool configured = physics.Configure();

            Assert.That(configured, Is.True);
            Assert.That(bucketCollider.enabled, Is.True);
        }

        [TestCase(1f, true)]
        [TestCase(0f, false)]
        [TestCase(-1f, false)]
        [TestCase(float.NaN, false)]
        [TestCase(float.PositiveInfinity, false)]
        public void IsFinitePositive_RejectsInvalidPhysicsValues(float value, bool expected)
        {
            Assert.That(Zx200FixedBasePhysics.IsFinitePositive(value), Is.EqualTo(expected));
        }

        [Test]
        public void Configure_RejectsInvalidProxyMass()
        {
            physics.bucketMass = float.NaN;

            bool configured = physics.Configure();

            Assert.That(configured, Is.False);
            Assert.That(physics.ValidateConfiguration(out string error), Is.False);
            StringAssert.Contains("bucketMass", error);
        }

        [TestCase("linearDamping", float.NaN)]
        [TestCase("angularDamping", float.PositiveInfinity)]
        [TestCase("jointFriction", -0.01f)]
        public void Configure_RejectsInvalidStabilityValue(string fieldName, float value)
        {
            switch (fieldName)
            {
                case "linearDamping": physics.linearDamping = value; break;
                case "angularDamping": physics.angularDamping = value; break;
                case "jointFriction": physics.jointFriction = value; break;
            }

            bool configured = physics.Configure();

            Assert.That(configured, Is.False);
            Assert.That(physics.ValidateConfiguration(out string error), Is.False);
            StringAssert.Contains(fieldName, error);
        }

        private static ArticulationBody CreateBody(string name, Transform parent)
        {
            var link = new GameObject(name);
            link.transform.SetParent(parent);
            return link.AddComponent<ArticulationBody>();
        }
    }
}
