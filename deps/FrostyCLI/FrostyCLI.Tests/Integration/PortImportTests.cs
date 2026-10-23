using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Commands;
using FrostyCLI.Core;

namespace FrostyCLI.Tests.Integration
{
    // `port import` against a real package directory (produced by `port export` on the other game).
    // FROSTYCLI_TEST_PORT_PACKAGE names the directory; the test is skipped silently when unset.
    public class PortImportTests
    {
        private static void EnsureSession(string gp, ExportLogger logger)
        {
            if (!Session.IsInitialized)
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string adaptersDir = Path.Combine(exeDir, "Adapters");
                if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);
                Session.Initialize("swbf2", gp, null, adaptersDir, logger);
            }
        }

        [Test]
        [IntegrationTest("swbf2", Slow = true)]
        public void Import_CreatesBlueprintMeshAndPhysicsFromPackage()
        {
            string pkg = Environment.GetEnvironmentVariable("FROSTYCLI_TEST_PORT_PACKAGE");
            if (string.IsNullOrEmpty(pkg) || !File.Exists(Path.Combine(pkg, "manifest.json"))) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            var logger = new ExportLogger(quiet: true);
            EnsureSession(gp, logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            string bp = "Objects/Props/ObjectSets/Bespin/PortTest_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "/PortTest";
            int rc = PortCommand.ExecuteImport(new[] { pkg, "--as", bp }, logger, new OutputFormatter(false));
            Assert.AreEqual(0, rc);

            EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(bp);
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(bp + "_Mesh");
            Assert.IsTrue(bpEntry != null, "blueprint not created");
            Assert.IsTrue(meshEntry != null, "mesh not created");

            EbxAsset bpAsset = App.AssetManager.GetEbx(bpEntry);
            dynamic entity = ((dynamic)bpAsset.RootObject).Object.Internal;
            Guid meshRef = (Guid)entity.Mesh.External.FileGuid;
            Assert.AreEqual(meshEntry.Guid, meshRef);

            PortManifest m = PortManifest.Load(pkg);
            EbxAsset meshAsset = App.AssetManager.GetEbx(meshEntry);
            dynamic meshRoot = meshAsset.RootObject;
            Assert.AreEqual(m.Mesh.Materials.Count, (int)meshRoot.Materials.Count);
            Assert.IsTrue((ulong)meshRoot.MeshSetResource != 0, "mesh has no geometry resource");
            // The clone must carry its own name inside the MeshSet (the editor resolves the EBX entry
            // from it) and the EBX NameHash must be the hash of that name.
            uint msHash;
            string msName = FrostyCLI.Adapters.Mesh.MeshCloner.ReadMeshSetName(meshEntry, out msHash);
            Assert.AreEqual((bp + "_Mesh").ToLowerInvariant(), msName);
            Assert.AreEqual((uint)Frosty.Hash.Fnv1.HashString((bp + "_Mesh").ToLowerInvariant()), msHash);
            Assert.AreEqual(msHash, (uint)meshRoot.NameHash);
            int lods; System.Collections.Generic.List<System.Collections.Generic.List<string>> names; System.Collections.Generic.List<System.Collections.Generic.List<int>> mats;
            Assert.IsTrue(FrostyCLI.Adapters.Mesh.MeshCloner.DescribeSections(meshEntry, out lods, out names, out mats) == null);
            Assert.AreEqual(m.Mesh.LodCount, lods);
            for (int s = 0; s < m.Mesh.SectionNames[0].Count && s < names[0].Count; s++)
                Assert.AreEqual(m.Mesh.SectionNames[0][s], names[0][s]);

            if (m.Physics != null)
            {
                EbxAssetEntry phys = App.AssetManager.GetEbxEntry(bp + "_Physics_Win32");
                Assert.IsTrue(phys != null, "physics asset not created");
                ResAssetEntry res = App.AssetManager.GetResEntry(bp.ToLowerInvariant() + "_physics_win32");
                Assert.IsTrue(res != null, "physics res not created under the name level add expects");
                if (!string.IsNullOrEmpty(m.Physics.MetaHex))
                {
                    byte[] expectedMeta = Enumerable.Range(0, m.Physics.MetaHex.Length / 2)
                        .Select(i => Convert.ToByte(m.Physics.MetaHex.Substring(i * 2, 2), 16)).ToArray();
                    byte[] actualMeta = res.ModifiedEntry != null && res.ModifiedEntry.ResMeta != null
                        ? res.ModifiedEntry.ResMeta : res.ResMeta;
                    Assert.IsTrue(expectedMeta.SequenceEqual(actualMeta),
                        "physics import must preserve the source resource cookie exactly");
                }
            }

            string saved = Path.Combine(Path.GetTempPath(), "frostycli_port_" + Guid.NewGuid() + ".fbproject");
            try
            {
                Assert.IsTrue(Session.Project.Save(saved, logger), "project save failed after port import");
                Assert.IsTrue(Session.Project.IsComplete, "project was saved while incomplete");
            }
            finally { try { File.Delete(saved); } catch { } }
        }
    }
}
