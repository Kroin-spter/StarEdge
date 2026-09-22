using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    /// <summary>Check the delivered textured truck and its soil containment geometry.</summary>
    public class SmallDumpTruckTests
    {
        /// <summary>Verify atlas materials, bed alignment, solid walls and receiver scale in the delivered prefab.</summary>
        [Test]
        public void TexturedTruckContainsReceiverAndSolidBed()
        {
            var truck = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/ConstructionSite/ParkedDumpTruck.prefab"));
            try
            {
                var visual = truck.transform.Find("Small dump truck visual");
                Assert.That(visual, Is.Not.Null, "Replace primitive truck with the supplied textured model");
                var meshes = visual.GetComponentsInChildren<MeshFilter>();
                Assert.That(meshes.Any(m => m.name == "Dumper Box"), Is.True);
                Assert.That(meshes.Count(m => m.name.Contains("Wheel")), Is.GreaterThanOrEqualTo(6));
                foreach (string name in new[] { "truck", "Dumper Box", "Truck FL Wheel", "Truck Interior" })
                {
                    var material = meshes.Single(m => m.name == name).GetComponent<Renderer>().sharedMaterial;
                    Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"));
                    Assert.That(material.GetTexture("_BaseMap"), Is.Not.Null, name);
                }
                var receiver = truck.GetComponentInChildren<SoilReceivingZone>();
                Assert.That(receiver.transform.lossyScale, Is.EqualTo(Vector3.one));
                Assert.That(receiver.size.x, Is.InRange(1.9f, 2.2f));
                Assert.That(receiver.size.y, Is.InRange(4.5f, 5f));
                Physics.SyncTransforms();
                var floor = truck.transform.Find("Bed floor").GetComponent<BoxCollider>();
                Assert.That(floor.bounds.max.y, Is.EqualTo(receiver.transform.position.y - .002f).Within(.0005f));
                foreach (string name in new[] { "Left bed wall", "Right bed wall", "Front bed wall", "Tailgate" })
                    Assert.That(truck.transform.Find(name).GetComponent<BoxCollider>().isTrigger, Is.False);
                Assert.That(receiver.TryGetLanding(receiver.transform.position + Vector3.up * 2, 3, out _), Is.True);
                Assert.That(receiver.TryGetLanding(receiver.transform.position + new Vector3(receiver.size.x, 2, 0), 3, out _), Is.False);
                var bed = meshes.Single(m => m.name == "Dumper Box");
                var points = bed.sharedMesh.vertices.Select(v => bed.transform.TransformPoint(v)).ToArray();
                Assert.That(points.Any(p => Mathf.Abs(p.y - receiver.transform.position.y) < .01f), Is.True,
                    "Receiving surface must match the visible interior floor");
                float ground = meshes.Where(m => m.name.Contains("Wheel")).Min(m => m.GetComponent<Renderer>().bounds.min.y);
                Assert.That(ground, Is.EqualTo(0).Within(.005f));
            }
            finally { Object.DestroyImmediate(truck); }
        }
    }
}
