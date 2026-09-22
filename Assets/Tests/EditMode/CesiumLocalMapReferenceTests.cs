using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    /// <summary>Verifies portable local Cesium tileset path resolution.</summary>
    public sealed class CesiumLocalMapReferenceTests
    {
        private string originalTilesetDirectory;
        private string temporaryDirectory;

        /// <summary>Preserve the caller's map override before each path-resolution test.</summary>
        [SetUp]
        public void SetUp()
        {
            originalTilesetDirectory = Environment.GetEnvironmentVariable("CESIUM_TILESET_DIR");
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "hmi-cesium-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            Environment.SetEnvironmentVariable("CESIUM_TILESET_DIR", null);
        }

        /// <summary>Restore the caller's map override and remove temporary test data.</summary>
        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("CESIUM_TILESET_DIR", originalTilesetDirectory);
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
        }

        /// <summary>The explicit environment directory takes priority over the project sibling directory.</summary>
        [Test]
        public void EnvironmentDirectoryTakesPriority()
        {
            string expected = Path.Combine(temporaryDirectory, "tileset.json");
            File.WriteAllText(expected, "{}");
            Environment.SetEnvironmentVariable("CESIUM_TILESET_DIR", temporaryDirectory);

            bool resolved = CesiumLocalMapReference.TryResolveTilesetPath("osgb_small_005_004", out string actual);

            Assert.That(resolved, Is.True);
            Assert.That(actual, Is.EqualTo(Path.GetFullPath(expected)));
        }

        /// <summary>A missing map reports the portable project-sibling candidate without throwing.</summary>
        [Test]
        public void MissingMapUsesProjectSiblingCandidate()
        {
            const string mapName = "map-that-is-not-present-in-this-project";

            bool resolved = CesiumLocalMapReference.TryResolveTilesetPath(mapName, out string actual);
            string expected = Path.GetFullPath(Path.Combine(Application.dataPath, "../../tilesets", mapName, "tileset.json"));

            Assert.That(resolved, Is.False);
            Assert.That(actual, Is.EqualTo(expected));
        }

        /// <summary>Local paths are converted to Cesium-compatible file URLs with encoded spaces.</summary>
        [Test]
        public void ToFileUriEncodesLocalPath()
        {
            string path = Path.Combine(temporaryDirectory, "Cesium Map", "tileset.json");

            string uri = CesiumLocalMapReference.ToFileUri(path);

            Assert.That(uri, Does.StartWith("file:///"));
            Assert.That(uri, Does.Contain("Cesium%20Map"));
            Assert.That(uri, Does.EndWith("tileset.json"));
        }
    }
}
