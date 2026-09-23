using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    public class ConstructionSiteSetupTests
    {
        [Test]
        public void LayoutSafety_RejectsTruckOverlappingExpandedSoilBounds()
        {
            Bounds soil = new Bounds(new Vector3(330, 8, 405), new Vector3(6, 1, 6));
            Bounds overlappingTruck = new Bounds(new Vector3(332, 8, 403), new Vector3(3.24f, 3, 6.3f));
            Bounds separatedTruck = new Bounds(new Vector3(337, 8, 402), new Vector3(3.24f, 3, 6.3f));

            Assert.That(ExcavationSiteLayoutSafety.HasRequiredClearance(soil, overlappingTruck, 2f), Is.False);
            Assert.That(ExcavationSiteLayoutSafety.HasRequiredClearance(soil, separatedTruck, 2f), Is.True);
        }

        [Test]
        public void ConstructionSite_UsesRightFrontTruckParkingOffset()
        {
            Type builder = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Zx200.Editor.Zx200ConstructionSiteSetup"))
                .FirstOrDefault(type => type != null);
            Assert.That(builder, Is.Not.Null);
            FieldInfo offsetField = builder.GetField("TruckParkingOffsetLocal", BindingFlags.Public | BindingFlags.Static);
            Assert.That(offsetField, Is.Not.Null);
            Vector3 offset = (Vector3)offsetField.GetValue(null);

            Assert.That(offset.x, Is.EqualTo(7f).Within(.001f));
            Assert.That(offset.z, Is.EqualTo(4f).Within(.001f));
        }

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
