using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace ExcavatorControls.Tests
{
    public class CockpitWebExporterTests
    {
        private string root;
        private string source;
        private string frontend;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "cockpit-export-test-" + Guid.NewGuid().ToString("N"));
            source = Path.Combine(root, "export");
            frontend = Path.Combine(root, "cockpitWeb");
            Directory.CreateDirectory(Path.Combine(source, "Build"));
            foreach (string suffix in new[] { "loader.js", "data", "framework.js", "wasm" })
                File.WriteAllText(Path.Combine(source, "Build", "AZ_demo_cesium_fixed." + suffix), "fresh-" + suffix);
            Directory.CreateDirectory(Path.Combine(source, "maps", "osgb_small_005_004"));
            File.WriteAllText(Path.Combine(source, "maps", "osgb_small_005_004", "tileset.json"), "{}");
            Directory.CreateDirectory(Path.Combine(frontend, "public", "unity-webgl"));
            File.WriteAllText(Path.Combine(frontend, "package.json"), "{\"name\":\"cockpit-web\"}");
            File.WriteAllText(Path.Combine(frontend, "public", "unity-webgl", "index.html"), "createUnityInstance ReceiveControlTopic custom-bridge");
            File.WriteAllText(Path.Combine(frontend, "public", "unity-webgl", "delivery.json"), "old-manifest");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private void Sync()
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CockpitWebExporter"))
                .FirstOrDefault(t => t != null);
            Assert.That(type, Is.Not.Null, "Export-to-frontend implementation is required.");
            type.GetMethod("SyncExport").Invoke(null, new object[] { source, frontend, "verified-code" });
        }

        [Test]
        public void Sync_CopiesFreshBuildAndMapButPreservesFrontendBridge()
        {
            Sync();
            string target = Path.Combine(frontend, "public", "unity-webgl");
            Assert.That(File.ReadAllText(Path.Combine(target, "Build", "AZ_demo_cesium_fixed.wasm")), Is.EqualTo("fresh-wasm"));
            Assert.That(File.ReadAllText(Path.Combine(target, "index.html")), Is.EqualTo("createUnityInstance ReceiveControlTopic custom-bridge"));
            Assert.That(File.ReadAllText(Path.Combine(target, "delivery.json")), Does.Contain("verified-code"));
            Assert.That(File.Exists(Path.Combine(target, "maps", "osgb_small_005_004", "tileset.json")), Is.True);
        }

        [Test]
        public void Sync_MissingWasmLeavesCurrentDeliveryUntouched()
        {
            File.Delete(Path.Combine(source, "Build", "AZ_demo_cesium_fixed.wasm"));
            Assert.Throws<System.Reflection.TargetInvocationException>(() => Sync());
            Assert.That(File.ReadAllText(Path.Combine(frontend, "public", "unity-webgl", "delivery.json")), Is.EqualTo("old-manifest"));
        }

        [Test]
        public void Sync_RejectsNonFrontendTarget()
        {
            File.Delete(Path.Combine(frontend, "package.json"));
            Assert.Throws<System.Reflection.TargetInvocationException>(() => Sync());
            Assert.That(File.ReadAllText(Path.Combine(frontend, "public", "unity-webgl", "delivery.json")), Is.EqualTo("old-manifest"));
        }

        [Test]
        public void Sync_RemovesStaleBuildArtifactsWithoutDeletingOtherFrontendFiles()
        {
            string build = Path.Combine(frontend, "public", "unity-webgl", "Build");
            Directory.CreateDirectory(build);
            File.WriteAllText(Path.Combine(build, "old.wasm"), "stale");
            File.WriteAllText(Path.Combine(frontend, "public", "keep.txt"), "keep");
            Sync();
            Assert.That(File.Exists(Path.Combine(build, "old.wasm")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(frontend, "public", "keep.txt")), Is.EqualTo("keep"));
        }
    }
}
