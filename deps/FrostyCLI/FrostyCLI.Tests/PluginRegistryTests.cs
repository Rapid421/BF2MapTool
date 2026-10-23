using System;
using System.IO;
using System.Reflection;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class PluginRegistryTests
    {
        private string GetFakesDllPath()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(dir, "FrostyCLI.Tests.Fakes.dll");
        }

        [Test]
        public void Discover_LoadsFakeAssetDefinitions()
        {
            string fakesDll = GetFakesDllPath();
            Assert.IsTrue(File.Exists(fakesDll), "FrostyCLI.Tests.Fakes.dll not found at " + fakesDll);

            string tempDir = Path.Combine(Path.GetTempPath(), "FrostyCLI_PluginTest_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                File.Copy(fakesDll, Path.Combine(tempDir, "FrostyCLI.Tests.Fakes.dll"));
                var registry = new PluginRegistry();
                registry.Discover(tempDir, null, null);

                var handler = registry.GetHandler("FakeAsset");
                Assert.IsNotNull(handler);

                var handler2 = registry.GetHandler("AnotherFakeAsset");
                Assert.IsNotNull(handler2);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Test]
        public void GetHandler_CaseInsensitive()
        {
            var registry = new PluginRegistry();
            registry.RegisterAssembly(typeof(FrostyCLI.Tests.Fakes.FakeAssetDefinition).Assembly, null);
            Assert.IsNotNull(registry.GetHandler("fakeasset"));
            Assert.IsNotNull(registry.GetHandler("FAKEASSET"));
            Assert.IsNotNull(registry.GetHandler("FakeAsset"));
        }

        [Test]
        public void GetHandler_UnknownType_ReturnsNull()
        {
            var registry = new PluginRegistry();
            Assert.IsNull(registry.GetHandler("NonExistentAssetType"));
        }

        [Test]
        public void GetHandler_NullType_ReturnsNull()
        {
            var registry = new PluginRegistry();
            Assert.IsNull(registry.GetHandler((string)null));
        }

        [Test]
        public void Discover_AdaptersDir_WinsOverPluginsDir()
        {
            string fakesDll = GetFakesDllPath();
            Assert.IsTrue(File.Exists(fakesDll));

            string pluginsDir = Path.Combine(Path.GetTempPath(), "FrostyCLI_PluginsDir_" + Guid.NewGuid());
            string adaptersDir = Path.Combine(Path.GetTempPath(), "FrostyCLI_AdaptersDir_" + Guid.NewGuid());
            Directory.CreateDirectory(pluginsDir);
            Directory.CreateDirectory(adaptersDir);
            try
            {
                File.Copy(fakesDll, Path.Combine(pluginsDir, "FrostyCLI.Tests.Fakes.dll"));
                File.Copy(fakesDll, Path.Combine(adaptersDir, "FrostyCLI.Tests.Fakes.dll"));

                var registry = new PluginRegistry();
                registry.Discover(pluginsDir, adaptersDir, null);

                Assert.IsNotNull(registry.GetHandler("FakeAsset"));
            }
            finally
            {
                Directory.Delete(pluginsDir, recursive: true);
                Directory.Delete(adaptersDir, recursive: true);
            }
        }
    }
}
