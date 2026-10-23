using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Tests.Integration
{
    // G4: handler-based mod merge. The headless mod-build PluginManager stub now POPULATES its custom
    // action handler dictionaries from loaded plugin assemblies' [RegisterCustomHandler] attributes
    // (instead of leaving them empty), so FrostyModWriter runs per-type handlers during mod build.
    // The only custom handler in the CLI's loaded plugin set is MeshSetPlugin's ShaderBlockDepot
    // Res handler -- validated here both by registration AND by a full end-to-end mod build that runs it.
    public class ModBuildHandlerTests
    {
        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string adaptersDir = Path.Combine(exeDir, "Adapters");
            if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);
            Session.Initialize("swbf2", gp, null, adaptersDir, logger);
        }

        [Test]
        public void HeadlessPluginManager_RegistersShaderBlockDepotHandler()
        {
            // Ensure the handler-registering assembly is loaded so the attribute scan sees it.
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string dll = Path.Combine(dir, "MeshSetPlugin.dll");
            if (!File.Exists(dll)) { Console.WriteLine("skip: MeshSetPlugin.dll not next to tests"); return; }
            try { Assembly.LoadFrom(dll); } catch { /* may already be loaded */ }

            var pm = HeadlessProject.CreateHeadlessPluginManager();
            Assert.IsNotNull(pm);

            // Registered + instantiable: GetCustomHandler does Activator.CreateInstance on the type.
            var handler = pm.GetCustomHandler(ResourceType.ShaderBlockDepot);
            Assert.IsNotNull(handler);
            Assert.AreEqual("ShaderBlockDepotCustomActionHandler", handler.GetType().Name);
        }

        // A res type with no registered handler must still return null (so plain res mods keep using
        // the default serialization path -- no regression for non-handler assets).
        [Test]
        public void HeadlessPluginManager_UnhandledResType_ReturnsNull()
        {
            var pm = HeadlessProject.CreateHeadlessPluginManager();
            Assert.IsNotNull(pm);
            Assert.IsNull(pm.GetCustomHandler(ResourceType.Texture));
        }

        // End-to-end: manufacture a modified ShaderBlockDepot (flag a sub-resource IsModified + ModifyRes,
        // exactly what the editor does), then build a .fbmod. FrostyModWriter must run the ShaderBlockDepot
        // handler's SaveToMod on the resulting ModifiedShaderBlockDepot DataObject -- without the handler
        // (the old empty-dict stub) that non-byte DataObject can't be serialized and the build fails. So a
        // successful, non-empty build proves the handler ran end to end.
        [Test]
        [IntegrationTest("swbf2")]
        public void ModBuild_ShaderBlockDepotHandler_RunsEndToEnd()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            ResAssetEntry sbdEntry = null; ShaderBlockDepot sbd = null;
            int scanned = 0;
            foreach (ResAssetEntry re in App.AssetManager.EnumerateRes((uint)ResourceType.ShaderBlockDepot))
            {
                if (++scanned > 200) break;
                try { sbd = App.AssetManager.GetResAs<ShaderBlockDepot>(re); } catch { continue; }
                if (sbd != null && sbd.GetResource(0) != null) { sbdEntry = re; break; }
            }
            if (sbdEntry == null) { Console.WriteLine("skip: no usable ShaderBlockDepot resource found"); return; }

            string fbmod = Path.Combine(Path.GetTempPath(), "g4_sbd_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".fbmod");
            try
            {
                // Touch a sub-resource so SaveModifiedResource yields a non-empty ModifiedShaderBlockDepot.
                sbd.GetResource(0).IsModified = true;
                App.AssetManager.ModifyRes(sbd.ResourceId, sbd);

                var me = App.AssetManager.GetResEntry(sbdEntry.Name).ModifiedEntry;
                Assert.IsNotNull(me);
                Assert.AreEqual("ModifiedShaderBlockDepot", me.DataObject.GetType().Name);

                bool ok = Session.Project.Build(fbmod, new ModSettings { Name = "g4_sbd" }, logger);
                Assert.IsTrue(ok, "mod build with a ModifiedShaderBlockDepot should succeed (handler ran)");
                Assert.IsTrue(File.Exists(fbmod) && new FileInfo(fbmod).Length > 0, "built .fbmod should be non-empty");

                // Decisive: the ShaderBlockDepot handler writes its magic hash (0x89EF2205) into the mod
                // resource record. Its presence in the .fbmod proves SaveToMod actually ran during build.
                byte[] mod = File.ReadAllBytes(fbmod);
                byte[] magic = BitConverter.GetBytes(0x89EF2205u); // little-endian on x64
                bool found = false;
                for (int i = 0; i + 4 <= mod.Length && !found; i++)
                    if (mod[i] == magic[0] && mod[i + 1] == magic[1] && mod[i + 2] == magic[2] && mod[i + 3] == magic[3])
                        found = true;
                Assert.IsTrue(found, "built .fbmod must contain the ShaderBlockDepot handler hash (handler resource written)");
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(sbdEntry.Name)); } catch { }
                try { File.Delete(fbmod); } catch { }
            }
        }

        // P1: `mod apply` of a handler-based resource. Build a ShaderBlockDepot .fbmod (handler ran on
        // build), revert the res so the project is clean, then `mod apply` the .fbmod and assert the res
        // entry comes back modified with a ModifiedShaderBlockDepot DataObject -- i.e. ExecuteApply
        // resolved + ran the ShaderBlockDepot handler instead of skipping it.
        [Test]
        [IntegrationTest("swbf2")]
        public void ModApply_ShaderBlockDepotHandler_Applies()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            ResAssetEntry sbdEntry = null; ShaderBlockDepot sbd = null;
            int scanned = 0;
            foreach (ResAssetEntry re in App.AssetManager.EnumerateRes((uint)ResourceType.ShaderBlockDepot))
            {
                if (++scanned > 200) break;
                try { sbd = App.AssetManager.GetResAs<ShaderBlockDepot>(re); } catch { continue; }
                if (sbd != null && sbd.GetResource(0) != null) { sbdEntry = re; break; }
            }
            if (sbdEntry == null) { Console.WriteLine("skip: no usable ShaderBlockDepot resource found"); return; }

            string fbmod = Path.Combine(Path.GetTempPath(), "p1_sbd_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".fbmod");
            try
            {
                sbd.GetResource(0).IsModified = true;
                App.AssetManager.ModifyRes(sbd.ResourceId, sbd);
                bool ok = Session.Project.Build(fbmod, new ModSettings { Name = "p1_sbd" }, logger);
                Assert.IsTrue(ok, "mod build with a ModifiedShaderBlockDepot should succeed");

                // Revert -> the res is clean; apply must re-create the modification via the handler.
                App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(sbdEntry.Name));
                Assert.IsFalse(App.AssetManager.GetResEntry(sbdEntry.Name).IsModified, "res should be clean after revert");

                int rc = ModBuildCommand.ExecuteApply(new[] { "apply", fbmod }, logger, fmt);
                Assert.AreEqual(0, rc);

                var me = App.AssetManager.GetResEntry(sbdEntry.Name).ModifiedEntry;
                Assert.IsNotNull(me);
                Assert.IsTrue(me.DataObject != null && me.DataObject.GetType().Name == "ModifiedShaderBlockDepot",
                    "apply should run the ShaderBlockDepot handler (not skip it)");
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(sbdEntry.Name)); } catch { }
                try { File.Delete(fbmod); } catch { }
            }
        }
    }
}
