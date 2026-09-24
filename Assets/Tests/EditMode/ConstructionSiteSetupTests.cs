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
        public void ConstructionSite_TruckParkingStaysReachableAndClearOfSoil()
        {
            Type builder = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Zx200.Editor.Zx200ConstructionSiteSetup"))
                .FirstOrDefault(type => type != null);
            Assert.That(builder, Is.Not.Null);
            FieldInfo offsetField = builder.GetField("TruckParkingOffsetLocal", BindingFlags.Public | BindingFlags.Static);
            Assert.That(offsetField, Is.Not.Null);
            Vector3 offset = (Vector3)offsetField.GetValue(null);

            Assert.That(new Vector2(offset.x, offset.z).magnitude, Is.LessThanOrEqualTo(8.8f));

            Bounds soil = new Bounds(new Vector3(0, 0, 6.7f), new Vector3(13, 1, 2));
            Bounds truck = new Bounds(offset, new Vector3(6.3f, 3, 4.24f));
            Assert.That(ExcavationSiteLayoutSafety.HasRequiredClearance(soil, truck, 2f), Is.True);
        }

        [Test]
        public void TruckBed_ContainsObservedDumpTipOffsetsWithWallMargin()
        {
            Type builder = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Zx200.Editor.Zx200ConstructionSiteSetup"))
                .FirstOrDefault(type => type != null);
            MethodInfo create = builder.GetMethod("CreateTruck", BindingFlags.Public | BindingFlags.Static);
            GameObject truck = (GameObject)create.Invoke(null, new object[] { Vector3.zero, null, null, null });
            try
            {
                SoilReceivingZone receiver = truck.GetComponentInChildren<SoilReceivingZone>();
                truck.transform.rotation = Quaternion.Euler(0, 45, 0);
                // These end offsets were captured from separate physical dump attempts at the two planned dig points.
                Vector3[] observedTipOffsets =
                {
                    new Vector3(1.89f, 0, 1.12f),
                    new Vector3(1.78f, 0, -.27f)
                };
                const float wallMargin = .1f;
                foreach (Vector3 offset in observedTipOffsets)
                {
                    Vector3 local = truck.transform.InverseTransformVector(offset);
                    Assert.That(Mathf.Abs(local.x), Is.LessThanOrEqualTo(receiver.size.x * .5f - wallMargin));
                    Assert.That(Mathf.Abs(local.z), Is.LessThanOrEqualTo(receiver.size.y * .5f - wallMargin));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(truck); }
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
                Assert.That(receiver.size, Is.EqualTo(new Vector2(3.6f, 4.5f)));
                Assert.That(receiver.transform.position.y, Is.EqualTo(8.8168f).Within(.001f));
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
                    Is.EqualTo(receiver.transform.position.y - .002f).Within(.001f));
                foreach (string side in new[] { "Left bed wall", "Right bed wall" })
                {
                    Transform wall = truck.transform.Find(side);
                    BoxCollider wallCollider = wall.GetComponent<BoxCollider>();
                    float scaledWallHalfWidth = wallCollider.size.x * wall.localScale.x * .5f;
                    float innerFace = Mathf.Abs(wall.localPosition.x) - scaledWallHalfWidth;
                    Assert.That(innerFace, Is.GreaterThan(receiver.size.x * .5f + .15f),
                        "The widened physical walls must leave the full calibrated receiver width plus bucket clearance.");
                }
                Assert.That(truck.transform.Find("Cab"), Is.Not.Null);
                Assert.That(truck.transform.Find("Windshield"), Is.Not.Null);
                Assert.That(truck.GetComponentsInChildren<Transform>().Count(t => t.name.StartsWith("Wheel ")), Is.EqualTo(6));
                Assert.That(receiver.TryGetLanding(receiver.transform.position + Vector3.up, 3, out _), Is.True);
                Assert.That(receiver.TryGetLanding(receiver.transform.position + new Vector3(1.9f, 1, 0), 3, out _), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(truck); }
        }
    }
}
