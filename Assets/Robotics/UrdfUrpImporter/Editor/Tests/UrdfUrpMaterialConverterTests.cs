using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UrdfUrpImporter.Tests
{
    public class UrdfUrpMaterialConverterTests
    {
        private const string OutputDirectory =
            "Assets/Robotics/UrdfUrpImporter/Editor/Tests/GeneratedMaterials";
        private const string TexturePath =
            "Assets/Robotics/zx200_description/meshes/bake.png";

        [SetUp]
        public void SetUp() => AssetDatabase.DeleteAsset(OutputDirectory);

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(OutputDirectory);

        [Test]
        public void Convert_Texture_UsesUrpLitAndBaseMap()
        {
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);

            Material material = UrdfUrpMaterialConverter.Convert(texture, OutputDirectory);

            Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"));
            Assert.That(material.GetTexture("_BaseMap"), Is.SameAs(texture));
            Assert.That(material.GetColor("_BaseColor").a, Is.EqualTo(1.0f));
        }

        [Test]
        public void Convert_SameTexture_ReusesPersistentMaterial()
        {
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);

            Material first = UrdfUrpMaterialConverter.Convert(texture, OutputDirectory);
            Material second = UrdfUrpMaterialConverter.Convert(texture, OutputDirectory);

            Assert.That(AssetDatabase.GetAssetPath(first), Is.Not.Empty);
            Assert.That(AssetDatabase.GetAssetPath(second),
                Is.EqualTo(AssetDatabase.GetAssetPath(first)));
        }

        [Test]
        public void Convert_NoTexture_CreatesOpaqueGrayMaterial()
        {
            Material material = UrdfUrpMaterialConverter.Convert(null, OutputDirectory);

            Assert.That(material.GetTexture("_BaseMap"), Is.Null);
            Color color = material.GetColor("_BaseColor");
            Assert.That(color.r, Is.EqualTo(0.33f).Within(0.001f));
            Assert.That(color.g, Is.EqualTo(0.33f).Within(0.001f));
            Assert.That(color.b, Is.EqualTo(0.33f).Within(0.001f));
            Assert.That(color.a, Is.EqualTo(1.0f).Within(0.001f));
            Assert.That(material.GetFloat("_Surface"), Is.EqualTo(0.0f));
        }
    }
}
