using NUnit.Framework;

namespace UrdfUrpImporter.Tests
{
    public class UrdfAssetResolverTests
    {
        [Test]
        public void Resolve_Hr35Layout_UsesRobotDirectoryAsUriRoot()
        {
            UrdfAssetLayout layout = UrdfAssetResolver.Resolve(
                "Assets/Robotics/HR35/ix35e.urdf");

            Assert.That(layout.PackageDirectory, Is.EqualTo("Assets/Robotics/HR35"));
            Assert.That(layout.UriRootDirectory, Is.EqualTo("Assets/Robotics/HR35"));
            Assert.That(layout.RobotName, Is.EqualTo("ix35e"));
        }

        [Test]
        public void Resolve_Zx200Layout_UsesParentOfNamedPackageAsUriRoot()
        {
            UrdfAssetLayout layout = UrdfAssetResolver.Resolve(
                "Assets/Robotics/zx200_description/urdf/zx200.urdf");

            Assert.That(layout.PackageDirectory,
                Is.EqualTo("Assets/Robotics/zx200_description"));
            Assert.That(layout.UriRootDirectory, Is.EqualTo("Assets/Robotics"));
            Assert.That(layout.RobotName, Is.EqualTo("zx200"));
        }

        [Test]
        public void FindDaeTexture_Zx200Mesh_ReturnsBakeTexture()
        {
            string texturePath = UrdfAssetResolver.FindDaeTexture(
                "Assets/Robotics/zx200_description/meshes/zx200-body.dae");

            Assert.That(texturePath,
                Is.EqualTo("Assets/Robotics/zx200_description/meshes/bake.png"));
        }
    }
}
