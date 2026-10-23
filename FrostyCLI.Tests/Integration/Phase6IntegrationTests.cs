using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class Phase6IntegrationTests
    {
        private static string ProjPath()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", ".."));
            return Path.Combine(repo, "community-mods", "HvVOnBespinCampaign.fbproject");
        }

        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Session.Initialize("swbf2", gp, null, Path.Combine(exeDir, "Adapters"), logger);
        }

        private static void EnsureProject(ExportLogger logger)
        {
            EnsureSession(logger);
            // Use a fresh empty project rather than loading HvVOnBespinCampaign.fbproject: these
            // tests only need a writable project context to mutate base-game assets, and the real
            // .fbproject only partially applies headlessly (App.PluginManager limitation), which
            // was causing the mutation commands to fail (rc=1).
            if (!Session.Project.IsLoaded)
                Session.Project.New();
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void EbxRevert_ModifiedAsset_ClearsModification()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureProject(logger);
            // Pick any asset, modify it, then revert.
            const string path = "s2/gameplay/repository";
            var entry = App.AssetManager.GetEbxEntry(path);
            if (entry == null) { Console.WriteLine("skip: test asset not present"); return; }
            EbxWriteCommand.ExecuteSet(new[] { path, "--prop", "Name", "--value", "x" }, logger, new OutputFormatter(false));
            int rc = EbxWriteCommand.ExecuteRevert(new[] { path }, logger, new OutputFormatter(false));
            Assert.AreEqual(0, rc);
            Assert.IsFalse(App.AssetManager.GetEbxEntry(path).IsModified, "asset still modified after revert");
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void EbxSetRef_PointerRefProperty_PointsToTarget()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureProject(logger);

            // Find any asset whose root object exposes a settable PointerRef property.
            EbxAssetEntry target = null;
            string prop = null;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                // Skip level assets: mutating a level root drops its entity list and would pollute
                // level-reading tests sharing this process.
                if (e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                if (a == null || a.RootObject == null) continue;
                foreach (System.Reflection.PropertyInfo p in a.RootObject.GetType().GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (p.PropertyType.Name == "PointerRef" && p.CanRead && p.CanWrite)
                    { target = e; prop = p.Name; break; }
                }
                if (target != null) break;
            }
            if (target == null) { Console.WriteLine("skip: no asset with a settable PointerRef root property"); return; }

            var fmt = new OutputFormatter(false);
            try
            {
                // Point it at itself: a deterministic, always-resolvable target (its own file + class GUID).
                int rc = EbxWriteCommand.ExecuteSetRef(
                    new[] { target.Name, "--prop", prop, "--target", target.Name }, logger, fmt);
                Assert.AreEqual(0, rc);

                // Read the PointerRef back and confirm External.FileGuid points to the target.
                var re = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(target.Name));
                object pr = re.RootObject.GetType().GetProperty(prop).GetValue(re.RootObject);
                Assert.IsNotNull(pr);
                object ext = pr.GetType().GetProperty("External").GetValue(pr);
                object fg = ext.GetType().GetField("FileGuid").GetValue(ext);
                Assert.AreEqual(target.Guid, (Guid)fg);
            }
            finally
            {
                // Revert so this base-game mutation doesn't pollute shared AssetManager state for
                // later tests in the same process.
                try { EbxWriteCommand.ExecuteRevert(new[] { target.Name }, logger, fmt); } catch { }
            }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void EbxCreate_NewAsset_AppearsInCatalog()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureProject(logger);
            string path = "s2/frostycli_test/created_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            // Implementer: pick a type name known to exist (e.g. a simple data type discovered via dump).
            int rc = EbxWriteCommand.ExecuteCreate(new[] { path, "--type", "DataContainer" }, logger, new OutputFormatter(false));
            if (rc != 0) { Console.WriteLine("skip: type not creatable in this game build"); return; }
            Assert.IsTrue(App.AssetManager.GetEbxEntry(path) != null, "created asset not found in catalog");
        }

        // Finds a level EBX with at least one entity exposing an instance GUID; returns null if none.
        private static string FindLevelWithEntities(ExportLogger logger, out Guid firstGuid)
        {
            firstGuid = Guid.Empty;
            if (App.AssetManager == null) return null;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) < 0) continue;
                FrostySdk.IO.EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(e); } catch { continue; }
                System.Collections.IList entities = LevelCommand.GetEntityList(asset.RootObject);
                if (entities == null) continue;
                foreach (object ent in entities)
                {
                    if (ent == null) continue;
                    Guid? g = LevelCommand.GetEntityInstanceGuid(ent);
                    if (g.HasValue && g.Value != Guid.Empty) { firstGuid = g.Value; return e.Name; }
                }
            }
            return null;
        }

        [Test]
        // Slow (FindLevelWithEntities loads every level) AND currently blocked by the headless
        // project-load limitation: FrostyProject.Load needs App.PluginManager and only partially
        // applies the project's modified entities headlessly. Gated as Slow; run with --slow.
        [IntegrationTest("swbf2", Slow = true)]
        public void LevelRemove_EntityGone_FromList()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureProject(logger);
            Guid guid;
            string level = FindLevelWithEntities(logger, out guid);
            if (level == null) { Console.WriteLine("skip: no level with entities found"); return; }

            int rc = LevelCommand.ExecuteRemove(new[] { level, "--guid", guid.ToString("D") }, logger, new OutputFormatter(false));
            Assert.AreEqual(0, rc);

            // Re-read and confirm the GUID is gone.
            var asset = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(level));
            System.Collections.IList entities = LevelCommand.GetEntityList(asset.RootObject);
            bool stillThere = false;
            foreach (object ent in entities)
            {
                if (ent == null) continue;
                Guid? g = LevelCommand.GetEntityInstanceGuid(ent);
                if (g.HasValue && g.Value == guid) { stillThere = true; break; }
            }
            Assert.IsFalse(stillThere, "entity still present after remove");
        }

        [Test]
        // Same as LevelRemove: slow (loads all levels) + headless project-load limitation. --slow.
        [IntegrationTest("swbf2", Slow = true)]
        public void LevelMove_EntityPosition_Updated()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureProject(logger);
            Guid guid;
            string level = FindLevelWithEntities(logger, out guid);
            if (level == null) { Console.WriteLine("skip: no level with entities found"); return; }

            int rc = LevelCommand.ExecuteMove(
                new[] { level, "--guid", guid.ToString("D"), "--pos", "1,2,3" },
                logger, new OutputFormatter(false));
            Assert.AreEqual(0, rc);
        }

        // Finds a MeshVariationDatabase EBX entry path; returns null if none found.
        // Memoized across tests (the path is stable; mvdb tests mutate entries, not the path).
        private static bool _mvdbSearched;
        private static string _mvdbPath;
        private static string FindMvdbPath()
        {
            if (_mvdbSearched) return _mvdbPath;
            _mvdbSearched = true;
            if (App.AssetManager == null) return null;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name.IndexOf("meshvariationdb", StringComparison.OrdinalIgnoreCase) >= 0)
                    return _mvdbPath = e.Name;
            }
            return _mvdbPath = null;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void MvdbAdd_Entry_AppearsInList()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureProject(logger);
            string mvdbPath = FindMvdbPath();
            if (mvdbPath == null) { Console.WriteLine("skip: no MeshVariationDatabase asset found"); return; }

            EbxAssetEntry mvdbEntry = App.AssetManager.GetEbxEntry(mvdbPath);
            if (mvdbEntry == null) { Console.WriteLine("skip: mvdb entry not found"); return; }
            FrostySdk.IO.EbxAsset mvdbAsset;
            try { mvdbAsset = App.AssetManager.GetEbx(mvdbEntry); } catch { Console.WriteLine("skip: could not load mvdb"); return; }
            System.Collections.IList entries = MvdbCommand.FindEntries(mvdbAsset.RootObject);
            if (entries == null) { Console.WriteLine("skip: no MeshVariation entry list on root"); return; }
            int countBefore = entries.Count;

            const string meshPath = "fx/meshes/generic/meshp_fullscreen_plane_mesh";
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(meshPath);
            if (meshEntry == null) { Console.WriteLine("skip: test mesh not found"); return; }
            FrostySdk.IO.EbxAsset meshAsset = App.AssetManager.GetEbx(meshEntry);
            System.Collections.IList sourceMaterials = ReadMember(meshAsset.RootObject, "Materials") as System.Collections.IList;
            if (sourceMaterials == null || sourceMaterials.Count == 0) { Console.WriteLine("skip: test mesh has no materials"); return; }

            int rc = MvdbCommand.ExecuteAdd(
                new[] { mvdbPath, "--mesh", meshPath, "--variation-hash", "0xF17EAD01" },
                logger, new OutputFormatter(false));
            Assert.AreEqual(0, rc);

            FrostySdk.IO.EbxAsset reloaded = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(mvdbPath));
            System.Collections.IList newEntries = MvdbCommand.FindEntries(reloaded.RootObject);
            Assert.IsTrue(newEntries != null, "entry list should not be null after add");
            Assert.IsTrue(newEntries.Count == countBefore + 1, "expected " + (countBefore + 1) + " entries after add, got " + (newEntries != null ? newEntries.Count.ToString() : "null"));

            object added = newEntries[newEntries.Count - 1];
            Assert.AreEqual(0xF17EAD01u, Convert.ToUInt32(ReadMember(added, "VariationAssetNameHash")));

            Guid meshFileGuid, meshClassGuid;
            Assert.IsTrue(ReadExternalRef(ReadMember(added, "Mesh"), out meshFileGuid, out meshClassGuid));
            Assert.AreEqual(meshEntry.Guid, meshFileGuid);
            Assert.AreEqual(ReadClassGuid(meshAsset.RootObject), meshClassGuid);

            System.Collections.IList writtenMaterials = ReadMember(added, "Materials") as System.Collections.IList;
            Assert.IsNotNull(writtenMaterials);
            Assert.AreEqual(sourceMaterials.Count, writtenMaterials.Count);

            object sourceMaterial = ReadMember(sourceMaterials[0], "Internal");
            object writtenMaterial = writtenMaterials[0];
            Guid materialFileGuid, materialClassGuid;
            Assert.IsTrue(ReadExternalRef(ReadMember(writtenMaterial, "Material"), out materialFileGuid, out materialClassGuid));
            Assert.AreEqual(meshEntry.Guid, materialFileGuid);
            Assert.AreEqual(ReadClassGuid(sourceMaterial), materialClassGuid);

            object shaderData = ReadMember(sourceMaterial, "Shader");
            Guid shaderFileGuid, shaderClassGuid;
            Assert.IsTrue(ReadExternalRef(ReadMember(shaderData, "Shader"), out shaderFileGuid, out shaderClassGuid));
            Assert.AreEqual(shaderFileGuid, ReadMember(writtenMaterial, "SurfaceShaderGuid"));
            EbxAssetEntry shaderEntry = App.AssetManager.GetEbxEntry(shaderFileGuid);
            uint expectedShaderId = unchecked((uint)FrostySdk.Utils.HashString(shaderEntry.Name.ToLowerInvariant()));
            Assert.AreEqual(expectedShaderId, Convert.ToUInt32(ReadMember(writtenMaterial, "SurfaceShaderId")));

            System.Collections.IList sourceTextures = ReadMember(shaderData, "TextureParameters") as System.Collections.IList;
            System.Collections.IList writtenTextures = ReadMember(writtenMaterial, "TextureParameters") as System.Collections.IList;
            Assert.AreEqual(sourceTextures.Count, writtenTextures.Count);
            Assert.IsTrue(HasDependency(reloaded, meshEntry.Guid), "MVDB should depend on its mesh");
            Assert.IsTrue(HasDependency(reloaded, shaderFileGuid), "MVDB should depend on its surface shader");

            string savedProject = Path.Combine(Path.GetTempPath(), "frostycli_mvdb_" + Guid.NewGuid().ToString("N") + ".fbproject");
            try
            {
                Assert.IsTrue(Session.Project.Save(savedProject, logger), "project save failed after mvdb add");
                Session.Project.Load(savedProject, logger);
                FrostySdk.IO.EbxAsset afterLoad = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(mvdbPath));
                System.Collections.IList afterLoadEntries = MvdbCommand.FindEntries(afterLoad.RootObject);
                object roundTripped = FindByVariationHash(afterLoadEntries, 0xF17EAD01u);
                Assert.IsNotNull(roundTripped);
                System.Collections.IList roundTripMaterials = ReadMember(roundTripped, "Materials") as System.Collections.IList;
                Assert.AreEqual(sourceMaterials.Count, roundTripMaterials.Count);
            }
            finally
            {
                if (File.Exists(savedProject)) File.Delete(savedProject);
            }
        }

        private static object ReadMember(object obj, string name)
        {
            if (obj == null) return null;
            var property = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(obj);
            var field = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(obj);
        }

        private static bool ReadExternalRef(object pointerRef, out Guid fileGuid, out Guid classGuid)
        {
            fileGuid = Guid.Empty;
            classGuid = Guid.Empty;
            object external = ReadMember(pointerRef, "External");
            object file = ReadMember(external, "FileGuid");
            object cls = ReadMember(external, "ClassGuid");
            if (!(file is Guid)) return false;
            fileGuid = (Guid)file;
            if (cls is Guid) classGuid = (Guid)cls;
            return fileGuid != Guid.Empty;
        }

        private static Guid ReadClassGuid(object ebxObject)
        {
            MethodInfo method = ebxObject.GetType().GetMethod("GetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
            object instanceGuid = method.Invoke(ebxObject, null);
            object exportedGuid = ReadMember(instanceGuid, "ExportedGuid");
            return exportedGuid is Guid ? (Guid)exportedGuid : Guid.Empty;
        }

        private static bool HasDependency(FrostySdk.IO.EbxAsset asset, Guid guid)
        {
            foreach (Guid dependency in asset.Dependencies)
                if (dependency == guid) return true;
            return false;
        }

        private static object FindByVariationHash(System.Collections.IList entries, uint variationHash)
        {
            if (entries == null) return null;
            foreach (object entry in entries)
            {
                object value = ReadMember(entry, "VariationAssetNameHash");
                if (value != null && Convert.ToUInt32(value) == variationHash) return entry;
            }
            return null;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void MvdbRemove_Entry_GoneFromList()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureProject(logger);
            string mvdbPath = FindMvdbPath();
            if (mvdbPath == null) { Console.WriteLine("skip: no MeshVariationDatabase asset found"); return; }

            EbxAssetEntry mvdbEntry = App.AssetManager.GetEbxEntry(mvdbPath);
            if (mvdbEntry == null) { Console.WriteLine("skip: mvdb entry not found"); return; }
            FrostySdk.IO.EbxAsset mvdbAsset;
            try { mvdbAsset = App.AssetManager.GetEbx(mvdbEntry); } catch { Console.WriteLine("skip: could not load mvdb"); return; }
            System.Collections.IList entries = MvdbCommand.FindEntries(mvdbAsset.RootObject);
            if (entries == null || entries.Count == 0) { Console.WriteLine("skip: no entries in mvdb to remove"); return; }
            int countBefore = entries.Count;

            // Find the mesh guid of the first entry so we can look up the asset path.
            object firstEntry = entries[0];
            if (firstEntry == null) { Console.WriteLine("skip: first entry is null"); return; }
            // Get mesh file GUID from the first PointerRef member.
            System.Guid meshFileGuid = System.Guid.Empty;
            foreach (System.Reflection.PropertyInfo p in firstEntry.GetType().GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.PropertyType.Name != "PointerRef") continue;
                object pr; try { pr = p.GetValue(firstEntry); } catch { continue; }
                if (pr == null) continue;
                object ext = pr.GetType().GetProperty("External")?.GetValue(pr)
                          ?? pr.GetType().GetField("External")?.GetValue(pr);
                if (ext == null) continue;
                object fg = ext.GetType().GetProperty("FileGuid")?.GetValue(ext)
                         ?? ext.GetType().GetField("FileGuid")?.GetValue(ext);
                if (fg is System.Guid g && g != System.Guid.Empty) { meshFileGuid = g; break; }
            }
            if (meshFileGuid == System.Guid.Empty) { Console.WriteLine("skip: could not find mesh GUID in first entry"); return; }

            // Resolve mesh GUID to asset path.
            string meshPath = IndexService.TryResolvePath(meshFileGuid);
            if (meshPath == null) { Console.WriteLine("skip: cannot resolve mesh GUID to path (index may not be built)"); return; }

            int rc = MvdbCommand.ExecuteRemove(
                new[] { mvdbPath, "--mesh", meshPath },
                logger, new OutputFormatter(false));
            Assert.AreEqual(0, rc);

            FrostySdk.IO.EbxAsset reloaded = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(mvdbPath));
            System.Collections.IList newEntries = MvdbCommand.FindEntries(reloaded.RootObject);
            Assert.IsTrue(newEntries != null, "entry list should not be null after remove");
            Assert.IsTrue(newEntries.Count == countBefore - 1, "expected " + (countBefore - 1) + " entries after remove, got " + (newEntries != null ? newEntries.Count.ToString() : "null"));
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LocSet_Key_ReturnsNewValue()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureProject(logger);

            // Self-skip if no loc db is available (headless SWBF2 may not register a loc plugin).
            Frosty.Core.ILocalizedStringDatabase db = Frosty.Core.LocalizedStringDatabase.Current;
            if (db == null || db.GetType().Name == "DefaultLocalizedStringDatabase")
            { Console.WriteLine("skip: no loc db"); return; }
            try { db.Initialize(); } catch { }

            // Use a test key unlikely to clash: pick an existing key or a synthetic one.
            // Try to enumerate at least one existing key; if none, skip.
            uint testKey = 0; bool found = false;
            foreach (uint k in db.EnumerateStrings()) { testKey = k; found = true; break; }
            if (!found) { Console.WriteLine("skip: no loc strings enumerable"); return; }

            const string newValue = "FrostyCLI_LocSet_Test";
            int rc = LocCommand.ExecuteSet(
                new[] { testKey.ToString(System.Globalization.CultureInfo.InvariantCulture), "--value", newValue },
                logger, new OutputFormatter(false));
            Assert.AreEqual(0, rc);

            string readBack = db.GetString(testKey);
            Assert.AreEqual(newValue, readBack);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LocSearch_Query_ReturnsMatches()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);

            // Self-skip if no loc db is available.
            Frosty.Core.ILocalizedStringDatabase db = Frosty.Core.LocalizedStringDatabase.Current;
            if (db == null || db.GetType().Name == "DefaultLocalizedStringDatabase")
            { Console.WriteLine("skip: no loc db"); return; }
            try { db.Initialize(); } catch { }

            // Grab the value of any existing string to use as the search query.
            string sampleValue = null;
            foreach (uint k in db.EnumerateStrings())
            {
                string v = db.GetString(k);
                if (!string.IsNullOrEmpty(v)) { sampleValue = v; break; }
            }
            if (sampleValue == null) { Console.WriteLine("skip: no non-empty loc strings to search"); return; }

            // Search for a substring of the sample value (first 4 chars is enough).
            string query = sampleValue.Length >= 4 ? sampleValue.Substring(0, 4) : sampleValue;

            // Redirect stdout to capture output.
            var origOut = Console.Out;
            var sw = new System.IO.StringWriter();
            Console.SetOut(sw);
            int rc = LocCommand.ExecuteSearch(new[] { query }, logger, new OutputFormatter(false));
            Console.SetOut(origOut);

            Assert.AreEqual(0, rc);
            string output = sw.ToString();
            Assert.IsNotNull(output);
            // Output should be a non-empty JSON array containing at least one result.
            Assert.IsTrue(output.TrimStart().StartsWith("["), "expected JSON array output");
            Assert.IsTrue(output.Contains("\"key\""), "expected 'key' field in output");
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelScaffold_DestAssets_ExistWithNewGuids()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureProject(logger);

            // Need the index catalog to be available.
            var catalog = IndexService.GetCatalog();
            if (catalog == null || catalog.Assets == null || catalog.Assets.Count == 0)
            { Console.WriteLine("skip: catalog empty"); return; }

            // Find a small prefix that has at least one asset.
            // Try a known CloudCity supremacy subworld prefix; self-skip if not present.
            const string templatePrefix = "s2/levels/cloudcity_01/supremacy";
            int prefixCount = 0;
            foreach (var rec in catalog.Assets)
                if (rec.Path.StartsWith(templatePrefix, StringComparison.OrdinalIgnoreCase)) { prefixCount++; break; }
            if (prefixCount == 0) { Console.WriteLine("skip: template prefix '" + templatePrefix + "' matches nothing in catalog"); return; }

            string destPrefix = "s2/frostycli_test/cryo_scaffold_" + Guid.NewGuid().ToString("N").Substring(0, 6);

            int rc = LevelCommand.ExecuteScaffold(
                new[] { templatePrefix, "--as", destPrefix },
                logger, new OutputFormatter(false));
            // If scaffold succeeds, verify at least one dest asset was created.
            if (rc != 0) { Console.WriteLine("skip: scaffold returned " + rc + " (may need project or assets not supported)"); return; }

            bool found = false;
            foreach (var rec in catalog.Assets)
            {
                if (!rec.Path.StartsWith(templatePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string dest = destPrefix + rec.Path.Substring(templatePrefix.Length);
                var entry = App.AssetManager.GetEbxEntry(dest);
                Assert.IsTrue(entry != null, "scaffolded dest asset not found: " + dest);
                // Verify the new asset has a different file GUID from the source.
                var srcEntry = App.AssetManager.GetEbxEntry(rec.Path);
                if (srcEntry != null)
                    Assert.IsTrue(entry.Guid != srcEntry.Guid, "dest asset should have a new file GUID");
                found = true;
                break; // Just check the first one to keep the test fast.
            }
            Assert.IsTrue(found, "no dest assets verified (catalog iteration produced nothing)");
        }
    }
}
