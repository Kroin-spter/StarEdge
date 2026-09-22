using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class ConstructionSiteSetupTests
    {
        /// <summary>Verify receiving dimensions and real containment geometry, rather than a decorative truck.</summary>
        [Test]
        public void TruckHasSolidFloorFourWallsAndReceiverInsideBed()
        {
            Type builder = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Zx200.Editor.Zx200ConstructionSiteSetup"))
                .FirstOrDefault(type => type != null);
            Assert.That(builder, Is.Not.Null, "A main-scene construction-site builder is required.");
            MethodInfo create = builder.GetMethod("CreateTruck", BindingFlags.Public | BindingFlags.Static);
            Assert.That(create, Is.Not.Null);
            GameObject truck = (GameObject)create.Invoke(null, new object[] { new Vector3(330, 7.8f, 398), null, null, null });
            try
            {
                var receiver = truck.GetComponentInChildren<SoilReceivingZone>();
                Assert.That(receiver, Is.Not.Null);
                Assert.That(receiver.size, Is.EqualTo(new Vector2(3, 4)));
                Assert.That(receiver.transform.position.y, Is.EqualTo(8.8f).Within(.001f));
                Assert.That(receiver.EffectiveCapacityM3, Is.EqualTo(9.6).Within(.001));
                foreach (string name in new[] { "Bed floor", "Left bed wall", "Right bed wall", "Front bed wall", "Tailgate" })
                {
                    Transform part = truck.transform.Find(name);
                    Assert.That(part, Is.Not.Null, name);
                    var collider = part.GetComponent<BoxCollider>();
                    Assert.That(collider, Is.Not.Null, name);
                    Assert.That(collider.isTrigger, Is.False, name);
                }
                Physics.SyncTransforms();
                Assert.That(truck.transform.Find("Bed floor").GetComponent<Collider>().bounds.max.y,
                    Is.EqualTo(receiver.transform.position.y).Within(.001f));
                Assert.That(truck.transform.Find("Cab"), Is.Not.Null);
                Assert.That(truck.transform.Find("Windshield"), Is.Not.Null);
                Assert.That(truck.GetComponentsInChildren<Transform>().Count(t => t.name.StartsWith("Wheel ")), Is.EqualTo(6));
                Assert.That(receiver.TryGetLanding(receiver.transform.position + Vector3.up, 3, out _), Is.True);
                Assert.That(receiver.TryGetLanding(receiver.transform.position + new Vector3(1.6f, 1, 0), 3, out _), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(truck); }
        }
    }
}
