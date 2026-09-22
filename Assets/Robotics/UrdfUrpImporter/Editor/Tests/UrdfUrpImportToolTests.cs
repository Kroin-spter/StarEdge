using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UrdfUrpImporter.Tests
{
    public class UrdfUrpImportToolTests
    {
        [Test]
        public void ImportMethod_IsPublicAndAvailable()
        {
            MethodInfo method = typeof(UrdfUrpImportTool).GetMethod(
                "Import", BindingFlags.Public | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);
        }

        [Test]
        public void Zx200Prefab_AllRendererMaterialsUseUrpAndBakeTexture()
        {
            GameObject prefab = UrdfUrpImportTool.Import(
                "Assets/Robotics/zx200_description/urdf/zx200.urdf");

            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            Assert.That(renderers, Is.Not.Empty);
            foreach (Renderer renderer in renderers)
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    Assert.That(material, Is.Not.Null, renderer.name);
                    Assert.That(material.shader.name,
                        Is.EqualTo("Universal Render Pipeline/Lit"), renderer.name);
                    Assert.That(material.GetTexture("_BaseMap")?.name,
                        Is.EqualTo("bake"), renderer.name);
                }
            }
        }

        [Test]
        public void Hr35Prefab_AllRendererMaterialsUseUrpAndPreserveTextures()
        {
            GameObject prefab = UrdfUrpImportTool.Import(
                "Assets/Robotics/HR35/ix35e.urdf");

            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            Assert.That(renderers, Is.Not.Empty);
            int texturedSlots = 0;
            foreach (Renderer renderer in renderers)
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    Assert.That(material, Is.Not.Null, renderer.name);
                    Assert.That(material.shader.name,
                        Is.EqualTo("Universal Render Pipeline/Lit"), renderer.name);
                    if (material.GetTexture("_BaseMap") != null) texturedSlots++;
                }
            }

            Assert.That(texturedSlots, Is.GreaterThan(0));
        }
    }
}
