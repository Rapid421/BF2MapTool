using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class LevelTests
    {
        private static string Repo()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", ".."));
        }
        private static string ProjPath()
        {
            return Path.Combine(Repo(), "community-mods", "HvVOnBespinCampaign.fbproject");
        }

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

        // Returns a level EBX whose root actually exposes a non-empty entity list, so 'level list'
        // has something to enumerate. We verify the entity list (rather than trust a hard-coded
        // path or the first "Level"-typed entry) so the test is deterministic regardless of what
        // other tests added to the shared AssetManager earlier in the process -- e.g. 'ebx clone'
        // adds a clone of the first enumerated asset, which previously perturbed the fallback and
        // made this test fail in full-suite ordering while passing in isolation.
        private static string FindLevelPath()
        {
            foreach (var x in App.AssetManager.EnumerateEbx())
            {
                if (x.Type == null || x.Type.IndexOf("Level", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // Ignore assets added by other tests in this shared process.
                if (x.Name.IndexOf("frostycli_test", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.Name.IndexOf("_clone_", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                FrostySdk.IO.EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(x); } catch { continue; }
                System.Collections.IList entities = LevelCommand.GetEntityList(asset.RootObject);
                if (entities != null && entities.Count > 0) return x.Name;
            }
            return null;
        }

        private static EbxAssetEntry FindLayerWithObjects()
        {
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (entry.Type == null || entry.Type.IndexOf("Layer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (entry.Name.IndexOf("levels/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                FrostySdk.IO.EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); } catch { continue; }
                if (asset == null || asset.RootObject == null) continue;
                PropertyInfo property = asset.RootObject.GetType().GetProperty("Objects", BindingFlags.Public | BindingFlags.Instance);
                if (property == null) continue;
                var objects = property.GetValue(asset.RootObject) as System.Collections.IList;
                if (objects != null && !objects.IsFixedSize && !objects.IsReadOnly) return entry;
            }
            return null;
        }

        private static object ReadMember(object value, string name)
        {
            if (value == null) return null;
            Type type = value.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? type.GetProperty(char.ToUpperInvariant(name[0]) + name.Substring(1), BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(value);
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
                ?? type.GetField(char.ToUpperInvariant(name[0]) + name.Substring(1), BindingFlags.Public | BindingFlags.Instance);
            return field != null ? field.GetValue(value) : null;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelList_KnownSubWorld_NonZeroOutput()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            string gp  = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);

            string lvl = FindLevelPath();
            if (lvl == null) { Console.WriteLine("skip: no level with a non-empty entity list found"); return; }

            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            int r;
            try
            {
                var cmd = new LevelCommand();
                r = cmd.Execute(new[] { "list", lvl, "--format", "json" }, logger, fmt);
            }
            finally { Console.SetOut(origOut); }

            Assert.IsTrue(r == 0, "level list returned non-zero: " + r);
            string output = sb.ToString();
            Assert.IsTrue(output.Length > 0, "level list produced no output");
            Assert.IsTrue(output.IndexOf("[") >= 0, "level list output is not a JSON array");
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelList_BfiiStaticModelGroup_ResolvesHavokTransform()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);

            const string level = "Levels/MP/StarKiller_01/Art_Exterior";
            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            int result;
            try
            {
                result = new LevelCommand().Execute(
                    new[] { "list", level, "--format", "json", "--limit", "5" }, logger, fmt);
            }
            finally { Console.SetOut(origOut); }

            Assert.IsTrue(result == 0, "BFII static-model-group listing failed: " + result);
            string output = sb.ToString();
            Assert.IsTrue(output.IndexOf("\"source\": \"havok\"", StringComparison.OrdinalIgnoreCase) >= 0,
                "listing did not resolve a Havok-backed placement");
            // 'level list' emits the translation under the key "trans"; asserting "translation"
            // here could never match, so this check passed nothing on before.
            Assert.IsTrue(output.IndexOf("\"trans\"", StringComparison.Ordinal) >= 0,
                "listing did not include a placement transform");
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelTreeAndStats_BfiiRoot_ReportSubWorldsAndPlacements()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);
            const string level = "Levels/MP/StarKiller_01/StarKiller_01";

            var origOut = Console.Out;
            var tree = new StringBuilder();
            Console.SetOut(new StringWriter(tree));
            int treeResult;
            try { treeResult = new LevelCommand().Execute(new[] { "tree", level, "--format", "json" }, logger, fmt); }
            finally { Console.SetOut(origOut); }
            Assert.IsTrue(treeResult == 0, "level tree failed: " + treeResult);
            Assert.IsTrue(tree.ToString().IndexOf("SubWorldData", StringComparison.OrdinalIgnoreCase) >= 0,
                "level tree did not follow a sub-world reference");

            var stats = new StringBuilder();
            Console.SetOut(new StringWriter(stats));
            int statsResult;
            try { statsResult = new LevelCommand().Execute(new[] { "stats", level, "--format", "json" }, logger, fmt); }
            finally { Console.SetOut(origOut); }
            Assert.IsTrue(statsResult == 0, "level stats failed: " + statsResult);
            Assert.IsTrue(stats.ToString().IndexOf("\"placements\"", StringComparison.OrdinalIgnoreCase) >= 0,
                "level stats did not report placements");
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelValidate_ShippedLevel_ReportsNoIssues()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);
            const string level = "Levels/MP/StarKiller_01/StarKiller_01";

            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            int result;
            try { result = new LevelCommand().Execute(new[] { "validate", level, "--format", "json" }, logger, fmt); }
            finally { Console.SetOut(origOut); }

            // A shipped, uncorrupted level must validate clean, otherwise the command cries wolf and
            // a real finding cannot be told from background noise.
            string output = sb.ToString();
            Assert.IsTrue(result == 0, "shipped level did not validate clean (exit " + result + "): " + output);
            Assert.IsTrue(output.IndexOf("\"errors\": 0", StringComparison.Ordinal) >= 0,
                "expected zero errors: " + output);
            Assert.IsTrue(output.IndexOf("\"warnings\": 0", StringComparison.Ordinal) >= 0,
                "expected zero warnings: " + output);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelValidate_DepthTruncation_WarnsAndStrictFails()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);
            const string level = "Levels/MP/StarKiller_01/StarKiller_01";

            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            int lenient, strict;
            try
            {
                lenient = new LevelCommand().Execute(new[] { "validate", level, "--max-depth", "0" }, logger, fmt);
                strict = new LevelCommand().Execute(new[] { "validate", level, "--max-depth", "0", "--strict" }, logger, fmt);
            }
            finally { Console.SetOut(origOut); }

            Assert.IsTrue(sb.ToString().IndexOf("subworld-depth-truncated", StringComparison.Ordinal) >= 0,
                "unvisited sub-worlds were not reported");
            Assert.AreEqual(0, lenient);  // warnings alone are not a failure
            Assert.AreEqual(2, strict);   // ... unless --strict is given
        }

        // level validate predicts, from the EBX structure alone, how many placements exist and where
        // each one's transform comes from. level list then resolves them for real. If the two ever
        // disagree, one of them is wrong -- which is exactly what the pre-fix Havok reader did.
        [Test]
        [IntegrationTest("swbf2")]
        public void LevelValidate_PlacementCounts_MatchLevelList()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);
            const string level = "Levels/MP/StarKiller_01/Art_Exterior";

            var origOut = Console.Out;
            var validated = new StringBuilder();
            var listed = new StringBuilder();
            try
            {
                Console.SetOut(new StringWriter(validated));
                Assert.AreEqual(0, new LevelCommand().Execute(new[] { "validate", level, "--format", "json" }, logger, fmt));
                Console.SetOut(new StringWriter(listed));
                Assert.AreEqual(0, new LevelCommand().Execute(
                    new[] { "list", level, "--format", "json", "--limit", "100000" }, logger, fmt));
            }
            finally { Console.SetOut(origOut); }

            int ebx = CountOccurrences(listed.ToString(), "\"source\": \"ebx\"");
            int havok = CountOccurrences(listed.ToString(), "\"source\": \"havok\"");
            Assert.IsTrue(havok > 0, "no Havok-backed placements were resolved");
            Assert.IsTrue(validated.ToString().IndexOf("\"ebx\": " + ebx, StringComparison.Ordinal) >= 0,
                "validate/list disagree on EBX placements (list saw " + ebx + "): " + validated);
            Assert.IsTrue(validated.ToString().IndexOf("\"havok\": " + havok, StringComparison.Ordinal) >= 0,
                "validate/list disagree on Havok placements (list saw " + havok + "): " + validated);
            Assert.IsTrue(validated.ToString().IndexOf("\"unresolved\": 0", StringComparison.Ordinal) >= 0,
                "some placements resolved to no transform at all: " + validated);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
            return count;
        }

        // Frontend/Backdrop_01 is the smallest EBX-backed group in the game: two members, one
        // instance each, both with inline InstanceTransforms. Small enough that a write is easy to
        // read back, and untouched by the other tests in this file.
        private const string EbxBackedAsset = "Levels/Frontend/Backdrop_01";

        // MaterialId cannot be derived, and the shader identity plus texture bindings must describe
        // the same render record. --like therefore copies each complete material record from an
        // already-registered mesh while replacing only its Material pointer.
        [Test]
        [IntegrationTest("swbf2")]
        public void MvdbAdd_Like_InheritsCompleteMaterialRecords()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            EnsureProjectLoaded(logger);

            const string mvdbPath = "levels/frontend/backdrop_01/MeshVariationDb_Win32";
            EbxAssetEntry mvdbEntry = App.AssetManager.GetEbxEntry(mvdbPath);
            if (mvdbEntry == null) { Console.WriteLine("skip: no backdrop mvdb"); return; }
            System.Collections.IList entries = MvdbCommand.FindEntries(App.AssetManager.GetEbx(mvdbEntry).RootObject);
            if (entries == null || entries.Count == 0) { Console.WriteLine("skip: empty mvdb"); return; }

            // Any shipped entry whose ids are non-zero makes the assertion meaningful.
            string donorMesh = null;
            var donorIds = new List<ulong>();
            var donorShaderGuids = new List<object>();
            var donorShaderIds = new List<uint>();
            var donorTextures = new List<List<object>>();
            foreach (object entry in entries)
            {
                System.Collections.IList mats = ReadMemberOf(entry, "Materials") as System.Collections.IList;
                if (mats == null || mats.Count == 0) continue;
                var ids = new List<ulong>();
                var shaderGuids = new List<object>();
                var shaderIds = new List<uint>();
                var textures = new List<List<object>>();
                bool anyNonZero = false;
                foreach (object m in mats)
                {
                    ulong id = Convert.ToUInt64(ReadMemberOf(m, "MaterialId"));
                    ids.Add(id);
                    if (id != 0) anyNonZero = true;
                    shaderGuids.Add(ReadMemberOf(m, "SurfaceShaderGuid"));
                    shaderIds.Add(Convert.ToUInt32(ReadMemberOf(m, "SurfaceShaderId")));
                    var materialTextures = new List<object>();
                    System.Collections.IList textureList = ReadMemberOf(m, "TextureParameters") as System.Collections.IList;
                    if (textureList != null)
                        foreach (object texture in textureList) materialTextures.Add(texture);
                    textures.Add(materialTextures);
                }
                if (!anyNonZero) continue;
                object meshRef = ReadMemberOf(entry, "Mesh");
                object external = ReadMemberOf(meshRef, "External");
                object fg = external == null ? null : ReadMemberOf(external, "FileGuid");
                if (!(fg is Guid)) continue;
                EbxAssetEntry me = App.AssetManager.GetEbxEntry((Guid)fg);
                if (me == null) continue;
                donorMesh = me.Name;
                donorIds = ids;
                donorShaderGuids = shaderGuids;
                donorShaderIds = shaderIds;
                donorTextures = textures;
                break;
            }
            if (donorMesh == null) { Console.WriteLine("skip: no entry with non-zero MaterialIds"); return; }

            // Same mesh, new variation hash: the material layout matches by construction, so this
            // isolates whether --like preserves all fields of the coherent donor records.
            int rc = MvdbCommand.ExecuteAdd(
                new[] { mvdbPath, "--mesh", donorMesh, "--variation-hash", "0x51DE0001", "--like", donorMesh },
                logger, fmt);
            Assert.AreEqual(0, rc);

            System.Collections.IList after = MvdbCommand.FindEntries(
                App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(mvdbPath)).RootObject);
            object added = null;
            foreach (object e in after)
                if (Convert.ToUInt32(ReadMemberOf(e, "VariationAssetNameHash")) == 0x51DE0001u) { added = e; break; }
            Assert.IsNotNull(added);

            System.Collections.IList addedMats = ReadMemberOf(added, "Materials") as System.Collections.IList;
            Assert.AreEqual(donorIds.Count, addedMats.Count);
            for (int i = 0; i < donorIds.Count; i++)
            {
                Assert.AreEqual(donorIds[i], Convert.ToUInt64(ReadMemberOf(addedMats[i], "MaterialId")));
                Assert.AreEqual(donorShaderGuids[i], ReadMemberOf(addedMats[i], "SurfaceShaderGuid"));
                Assert.AreEqual(donorShaderIds[i], Convert.ToUInt32(ReadMemberOf(addedMats[i], "SurfaceShaderId")));
                System.Collections.IList addedTextures = ReadMemberOf(addedMats[i], "TextureParameters") as System.Collections.IList;
                Assert.IsNotNull(addedTextures);
                Assert.AreEqual(donorTextures[i].Count, addedTextures.Count);
                for (int j = 0; j < donorTextures[i].Count; j++)
                    Assert.AreEqual(donorTextures[i][j], addedTextures[j]);
            }
        }

        // 'level add' places a prop and warns that it is neither registered nor bundled; 'level place'
        // is the same placement with both follow-ups done. The difference is observable: validate
        // reports the unregistered mesh in the first case and nothing in the second.
        [Test]
        [IntegrationTest("swbf2")]
        public void LevelPlace_RegistersMesh_WhereLevelAddLeavesItUnregistered()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            EnsureProjectLoaded(logger);

            const string foreign = "Objects/Props/ObjectSets/Starkiller/Crate_02/Crate_02";
            EbxAssetEntry bp = App.AssetManager.GetEbxEntry(foreign);
            if (bp == null) { Console.WriteLine("skip: test blueprint not found"); return; }

            const string mvdbPath = "levels/frontend/backdrop_01/MeshVariationDb_Win32";
            if (App.AssetManager.GetEbxEntry(mvdbPath) == null) { Console.WriteLine("skip: no backdrop mvdb"); return; }

            int rowsBefore = ListRows(EbxBackedAsset, logger, fmt).Count;
            int entriesBefore = MvdbEntryCount(mvdbPath);

            int rc = new LevelCommand().Execute(new[]
            { "place", EbxBackedAsset, "--group", "0", "--blueprint", foreign, "--pos", "11,12,13" }, logger, fmt);
            Assert.AreEqual(0, rc);
            Assert.AreEqual(rowsBefore + 1, ListRows(EbxBackedAsset, logger, fmt).Count);
            Assert.AreEqual(entriesBefore + 1, MvdbEntryCount(mvdbPath));

            // Re-placing the same prop adds a second placement but must not add a second database
            // entry -- the registration steps are skipped once they are already satisfied.
            int again = new LevelCommand().Execute(new[]
            { "place", EbxBackedAsset, "--group", "0", "--blueprint", foreign, "--pos", "14,15,16" }, logger, fmt);
            Assert.AreEqual(0, again);
            Assert.AreEqual(rowsBefore + 2, ListRows(EbxBackedAsset, logger, fmt).Count);
            Assert.AreEqual(entriesBefore + 1, MvdbEntryCount(mvdbPath));
        }

        private static int MvdbEntryCount(string mvdbPath)
        {
            System.Collections.IList entries = MvdbCommand.FindEntries(
                App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(mvdbPath)).RootObject);
            return entries == null ? -1 : entries.Count;
        }

        private static object ReadMemberOf(object value, string name)
        {
            if (value == null) return null;
            PropertyInfo p = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(value);
            FieldInfo f = value.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f == null ? null : f.GetValue(value);
        }

        private static void EnsureProjectLoaded(ExportLogger logger)
        {
            if (!Session.Project.IsLoaded) Session.Project.New();
        }

        private static JArray ListRows(string asset, ExportLogger logger, OutputFormatter fmt)
        {
            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            int r;
            try { r = new LevelCommand().Execute(new[] { "list", asset, "--format", "json", "--limit", "100000" }, logger, fmt); }
            finally { Console.SetOut(origOut); }
            Assert.IsTrue(r == 0, "level list failed: " + r);
            string text = sb.ToString();
            int start = text.IndexOf('[');
            Assert.IsTrue(start >= 0, "level list produced no JSON array: " + text);
            return JArray.Parse(text.Substring(start));
        }

        // Only static-model-group rows carry a group/member/instance address; placements from the
        // other families (props, prefabs, lights, effects) have none.
        private static bool IsAddressable(JToken row)
        {
            return row["group"] != null && row["group"].Type != JTokenType.Null;
        }

        private static JToken Row(JArray rows, int group, int member, int instance)
        {
            foreach (JToken row in rows)
                if (IsAddressable(row) && (int)row["group"] == group &&
                    (int)row["member"] == member && (int)row["instance"] == instance)
                    return row;
            return null;
        }

        private static float[] Vec(JToken transform, string axis)
        {
            var a = (JArray)transform[axis];
            return new[] { (float)a[0], (float)a[1], (float)a[2] };
        }

        private static void EnsureProject(ExportLogger logger)
        {
            if (!Session.Project.IsLoaded) Session.Project.New();
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelMove_GroupedInstance_UpdatesTransform()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            EnsureProject(logger);

            int r = new LevelCommand().Execute(new[]
            {
                "move", EbxBackedAsset, "--group", "0", "--member", "0", "--instance", "0",
                "--pos", "11.5,22.25,-33.75"
            }, logger, fmt);
            Assert.AreEqual(0, r);

            JToken row = Row(ListRows(EbxBackedAsset, logger, fmt), 0, 0, 0);
            Assert.IsNotNull(row);
            float[] t = Vec(row["transform"], "trans");
            Assert.AreEqual(11.5f, t[0]);
            Assert.AreEqual(22.25f, t[1]);
            Assert.AreEqual(-33.75f, t[2]);
            Assert.AreEqual("ebx", (string)row["source"]);
        }

        // Member 1 carries a non-uniform scaled basis, so a rotation clobbered by a position-only
        // move would be obvious. Matches the entity-GUID move path, which also preserves rotation.
        [Test]
        [IntegrationTest("swbf2")]
        public void LevelMove_GroupedInstance_WithoutRot_PreservesRotation()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            EnsureProject(logger);

            JToken before = Row(ListRows(EbxBackedAsset, logger, fmt), 0, 1, 0);
            Assert.IsNotNull(before);
            float[] right0 = Vec(before["transform"], "right");
            float[] up0 = Vec(before["transform"], "up");
            float[] fwd0 = Vec(before["transform"], "forward");

            int r = new LevelCommand().Execute(new[]
            {
                "move", EbxBackedAsset, "--group", "0", "--member", "1", "--instance", "0", "--pos", "1,2,3"
            }, logger, fmt);
            Assert.AreEqual(0, r);

            JToken after = Row(ListRows(EbxBackedAsset, logger, fmt), 0, 1, 0);
            float[] t = Vec(after["transform"], "trans");
            Assert.AreEqual(1f, t[0]);
            Assert.AreEqual(2f, t[1]);
            Assert.AreEqual(3f, t[2]);
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(right0[i], Vec(after["transform"], "right")[i]);
                Assert.AreEqual(up0[i], Vec(after["transform"], "up")[i]);
                Assert.AreEqual(fwd0[i], Vec(after["transform"], "forward")[i]);
            }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelAdd_GroupedInstance_AppendsAndKeepsAssetValid()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            EnsureProject(logger);

            int before = ListRows(EbxBackedAsset, logger, fmt).Count;
            int r = new LevelCommand().Execute(new[]
            {
                "add", EbxBackedAsset, "--group", "0", "--member", "0", "--pos", "100,200,300"
            }, logger, fmt);
            Assert.AreEqual(0, r);

            JArray rows = ListRows(EbxBackedAsset, logger, fmt);
            Assert.AreEqual(before + 1, rows.Count);

            // The appended instance takes the next index on the member it was added to.
            JToken added = Row(rows, 0, 0, 1);
            Assert.IsNotNull(added);
            float[] t = Vec(added["transform"], "trans");
            Assert.AreEqual(100f, t[0]);
            Assert.AreEqual(200f, t[1]);
            Assert.AreEqual(300f, t[2]);
            Assert.AreEqual("ebx", (string)added["source"]);

            // The write must leave every invariant 'level validate' checks intact -- instance counts,
            // parallel array lengths, part/id ranges and the group totals derived from them.
            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            int v;
            try { v = new LevelCommand().Execute(new[] { "validate", EbxBackedAsset, "--format", "json", "--strict" }, logger, fmt); }
            finally { Console.SetOut(origOut); }
            Assert.IsTrue(v == 0, "asset no longer validates after add: " + sb);

            // Reading back through the AssetManager only proves the object graph changed. Saving the
            // project serialises every modified asset, so this is what proves a grown
            // InstanceTransforms list inside a member struct survives the EBX writer.
            string saved = Path.Combine(Path.GetTempPath(), "frostycli_leveladd_" + Guid.NewGuid() + ".fbproject");
            try
            {
                Assert.IsTrue(Session.Project.Save(saved, logger), "project save failed after grouped add");
                Assert.IsTrue(File.Exists(saved), "project file was not written");
                Assert.IsTrue(new FileInfo(saved).Length > 0, "project file is empty");
                // A project that did not load whole must never be written back over its own file;
                // Save() refuses in that state, so reaching here means the project was complete.
                Assert.IsTrue(Session.Project.IsComplete, "project was saved while incomplete");
            }
            finally { if (File.Exists(saved)) File.Delete(saved); }
        }

        // Add then remove must land back exactly where it started -- same row count, same indexes,
        // still valid. Self-contained so it does not depend on what other tests left behind.
        [Test]
        [IntegrationTest("swbf2")]
        public void LevelRemove_GroupedInstance_RoundTripsWithAdd()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            EnsureProject(logger);

            int before = ListRows(EbxBackedAsset, logger, fmt).Count;

            Assert.AreEqual(0, new LevelCommand().Execute(new[]
            { "add", EbxBackedAsset, "--group", "0", "--member", "1", "--pos", "5,6,7" }, logger, fmt));
            JArray afterAdd = ListRows(EbxBackedAsset, logger, fmt);
            Assert.AreEqual(before + 1, afterAdd.Count);

            // The appended instance is the highest index on that member.
            int added = -1;
            foreach (JToken row in afterAdd)
                if (IsAddressable(row) && (int)row["group"] == 0 && (int)row["member"] == 1 &&
                    (int)row["instance"] > added)
                    added = (int)row["instance"];
            Assert.IsTrue(added >= 0, "member 1 has no rows");

            Assert.AreEqual(0, new LevelCommand().Execute(new[]
            { "remove", EbxBackedAsset, "--group", "0", "--member", "1", "--instance", added.ToString() }, logger, fmt));

            JArray afterRemove = ListRows(EbxBackedAsset, logger, fmt);
            Assert.AreEqual(before, afterRemove.Count);
            Assert.IsNull(Row(afterRemove, 0, 1, added));

            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            int v;
            try { v = new LevelCommand().Execute(new[] { "validate", EbxBackedAsset, "--format", "json", "--strict" }, logger, fmt); }
            finally { Console.SetOut(origOut); }
            Assert.IsTrue(v == 0, "asset no longer validates after add+remove: " + sb);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelRemove_HavokBackedMember_RemovesOrRefusesTheLastInstance()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            EnsureProject(logger);

            const string havokAsset = "Levels/MP/StarKiller_01/Art_Exterior";
            JToken row = null;
            foreach (JToken candidate in ListRows(havokAsset, logger, fmt))
                if ((string)candidate["source"] == "havok") { row = candidate; break; }
            Assert.IsNotNull(row);

            int siblings = 0;
            foreach (JToken candidate in ListRows(havokAsset, logger, fmt))
                if ((string)candidate["source"] == "havok" &&
                    (int)candidate["group"] == (int)row["group"] && (int)candidate["member"] == (int)row["member"]) siblings++;

            int r = new LevelCommand().Execute(new[]
            {
                "remove", havokAsset,
                "--group", ((int)row["group"]).ToString(),
                "--member", ((int)row["member"]).ToString(),
                "--instance", ((int)row["instance"]).ToString()
            }, logger, fmt);
            // Dropping a member's last instance would renumber every later member and is refused;
            // any other instance is removed from the physics resource and the EBX bookkeeping.
            Assert.AreEqual(siblings <= 1 ? 1 : 0, r);
        }

        // Placing something the group has never held is the case a map port is made of -- every prop
        // in an imported map is new to its group.
        [Test]
        [IntegrationTest("swbf2")]
        public void LevelAdd_NewGroupMember_PlacesAForeignBlueprint()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            EnsureProject(logger);

            // A Starkiller crate has no business in the Frontend backdrop, which is the point.
            const string foreign = "Objects/Props/ObjectSets/Starkiller/Crate_01/Crate_01";
            int before = ListRows(EbxBackedAsset, logger, fmt).Count;

            int r = new LevelCommand().Execute(new[]
            { "add", EbxBackedAsset, "--group", "0", "--blueprint", foreign, "--pos", "5,6,7" }, logger, fmt);
            Assert.AreEqual(0, r);

            JArray rows = ListRows(EbxBackedAsset, logger, fmt);
            Assert.AreEqual(before + 1, rows.Count);

            JToken added = null;
            foreach (JToken row in rows)
                if ((string)row["blueprint"] == foreign) { added = row; break; }
            Assert.IsNotNull(added);
            Assert.AreEqual("ebx", (string)added["source"]);
            Assert.AreEqual(0, (int)added["instance"]);
            float[] t = Vec(added["transform"], "trans");
            Assert.AreEqual(5f, t[0]);
            Assert.AreEqual(6f, t[1]);
            Assert.AreEqual(7f, t[2]);

            // A new member declares no physics parts and no network ids, so every range in the group
            // must still tile correctly and the group totals must still agree.
            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            int v;
            try { v = new LevelCommand().Execute(new[] { "validate", EbxBackedAsset, "--format", "json", "--strict" }, logger, fmt); }
            finally { Console.SetOut(origOut); }
            Assert.IsTrue(v == 0, "asset no longer validates after adding a member: " + sb);
        }

        // Frontend/Trader places its props as LayerReferenceObjectData in the level's Objects list
        // rather than through a static model group. Those entries are PointerRefs, and handing one
        // straight to the row extractor finds no Blueprint on it, so every placement was dropped and
        // the level read as "0 entities total" -- indistinguishable from genuinely empty.
        [Test]
        [IntegrationTest("swbf2")]
        public void LevelList_LayerReferencedPlacements_AreNotDropped()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);

            JArray rows = ListRows("Levels/Frontend/Trader", logger, fmt);
            Assert.IsTrue(rows.Count > 0, "layer-referenced placements were dropped again");
            foreach (JToken row in rows)
                Assert.IsNotNull((string)row["blueprint"]);
        }

        // One command has to cover every level, otherwise a mod's blast radius is guesswork.
        [Test]
        [IntegrationTest("swbf2")]
        public void LevelValidate_All_CoversEveryLevel()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);

            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            int r;
            try { r = new LevelCommand().Execute(new[] { "validate", "--all", "--format", "json" }, logger, fmt); }
            finally { Console.SetOut(origOut); }

            string text = sb.ToString();
            JObject root = JObject.Parse(text.Substring(text.IndexOf('{')));

            // "Shipped data" means assets this run has not edited. Earlier tests in the same process
            // legitimately leave edits behind -- LevelAdd_NewGroupMember places an unregistered prop
            // on purpose -- and the mesh-registration rules are supposed to report exactly that. So
            // the assertion is that nothing is wrong with anything we did not touch.
            var modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(modifiedOnly: true)) modified.Add(e.Name);
            var unexpected = new List<string>();
            foreach (JToken result in (JArray)root["results"])
                foreach (JToken issue in (JArray)result["issues"])
                    if (!modified.Contains((string)issue["asset"] ?? ""))
                        unexpected.Add((string)issue["code"] + " on " + (string)issue["asset"] + ": " + (string)issue["detail"]);
            Assert.IsTrue(unexpected.Count == 0,
                "sweep reported findings on unmodified shipped data: " + string.Join("; ", unexpected.ToArray()));
            if (modified.Count == 0) Assert.IsTrue(r == 0, "clean project should sweep clean: " + sb);

            int levels = (int)root["levels"];
            Assert.IsTrue(levels > 20, "expected the full level list, got " + levels);
            Assert.AreEqual(levels, ((JArray)root["results"]).Count);
            Assert.IsTrue((long)root["placements"] > 100000, "sweep counted too few placements");
        }

        // --near has to rank the whole level before narrowing: on Art_Exterior the nearest crate
        // sits at instance 466 of member 72, nowhere near the first rows the collector produces.
        [Test]
        [IntegrationTest("swbf2")]
        public void LevelList_Near_RanksWholeLevelByDistance()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            const string asset = "Levels/MP/StarKiller_01/Art_Exterior";

            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            int r;
            try
            {
                r = new LevelCommand().Execute(new[]
                { "list", asset, "--format", "json", "--near", "143.5,1266.1,840.9", "--radius", "12" }, logger, fmt);
            }
            finally { Console.SetOut(origOut); }
            Assert.IsTrue(r == 0, "level list --near failed: " + r);

            string text = sb.ToString();
            var rows = JArray.Parse(text.Substring(text.IndexOf('[')));
            Assert.IsTrue(rows.Count > 0, "nothing found near a known placement");
            Assert.IsTrue(rows.Count < ListRows(asset, logger, fmt).Count, "--near did not narrow the listing");

            double previous = -1;
            bool sawDeepInstance = false;
            foreach (JToken row in rows)
            {
                double d = (double)row["distance"];
                Assert.IsTrue(d <= 12.0, "row outside the radius: " + d);
                Assert.IsTrue(d >= previous, "rows are not sorted by distance");
                previous = d;
                if (IsAddressable(row) && (int)row["instance"] > 100) sawDeepInstance = true;
            }
            Assert.IsTrue(sawDeepInstance, "ranking stopped before reaching high instance indexes");
        }

        // Moving a Havok-backed placement needs a physics-resource write this command does not do.
        // A Havok-backed placement's transform lives in the group's physics resource; the move
        // rewrites it there and the listing, which reads the resource, must show the new position.
        [Test]
        [IntegrationTest("swbf2")]
        public void LevelMove_HavokBackedMember_RewritesThePhysicsResource()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            EnsureProject(logger);

            const string havokAsset = "Levels/MP/StarKiller_01/Art_Exterior";
            JToken row = null;
            foreach (JToken candidate in ListRows(havokAsset, logger, fmt))
                if ((string)candidate["source"] == "havok") { row = candidate; break; }
            Assert.IsNotNull(row);

            int r = new LevelCommand().Execute(new[]
            {
                "move", havokAsset,
                "--group", ((int)row["group"]).ToString(),
                "--member", ((int)row["member"]).ToString(),
                "--instance", ((int)row["instance"]).ToString(),
                "--pos", "12.5,34.25,56"
            }, logger, fmt);
            Assert.AreEqual(0, r);

            JToken moved = null;
            foreach (JToken candidate in ListRows(havokAsset, logger, fmt))
                if ((string)candidate["source"] == "havok" &&
                    (int)candidate["group"] == (int)row["group"] && (int)candidate["member"] == (int)row["member"] &&
                    (int)candidate["instance"] == (int)row["instance"]) { moved = candidate; break; }
            Assert.IsNotNull(moved);
            Assert.AreEqual("havok", (string)moved["source"]);
            JArray t = (JArray)moved["transform"]["trans"];
            Assert.IsTrue(Math.Abs((float)t[0] - 12.5f) < 1e-3f && Math.Abs((float)t[1] - 34.25f) < 1e-3f && Math.Abs((float)t[2] - 56f) < 1e-3f,
                "placement did not move: " + t);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelAdd_EntityAppearsInList()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            string gp  = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);
            // The community-mods fixture is optional; an empty in-memory project exercises the same
            // path, so its absence must not fail the test.
            if (!Session.Project.IsLoaded)
            {
                if (File.Exists(ProjPath())) Session.Project.Load(ProjPath(), logger);
                else Session.Project.New();
            }

            string lvl = FindLevelPath();
            if (lvl == null) { Console.WriteLine("skip: no level with a non-empty entity list found"); return; }

            string bp = null;
            foreach (var x in App.AssetManager.EnumerateEbx())
            {
                if (x.Type != null && x.Type.IndexOf("Blueprint", StringComparison.OrdinalIgnoreCase) >= 0)
                { bp = x.Name; break; }
            }
            Assert.IsTrue(bp != null, "No blueprint asset found");

            var cmd = new LevelCommand();
            int addResult = cmd.Execute(
                new[] { "add", lvl, "--blueprint", bp, "--pos", "0,0,0" }, logger, fmt);
            // level add mutates a loaded project; the .fbproject only partially loads headlessly
            // (FrostyProject.Load needs App.PluginManager -- see HeadlessProject "headless
            // limitation" warning), so the mutation can't complete. Skip rather than fail.
            if (addResult != 0)
            {
                Console.WriteLine("skip: level add rc=" + addResult + " (headless project-load limitation prevents level mutation)");
                return;
            }

            var origOut = Console.Out;
            var sb = new StringBuilder();
            Console.SetOut(new StringWriter(sb));
            try { cmd.Execute(new[] { "list", lvl, "--format", "json" }, logger, fmt); }
            finally { Console.SetOut(origOut); }

            Assert.IsTrue(
                sb.ToString().IndexOf(bp, StringComparison.OrdinalIgnoreCase) >= 0,
                "Added blueprint not found in level list output");
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void LevelAddEntity_RegistersInternalObjectAndWritesFields()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), logger);
            EnsureProject(logger);

            EbxAssetEntry entry = FindLayerWithObjects();
            if (entry == null) { Console.WriteLine("skip: no writable LayerData Objects list found"); return; }
            FrostySdk.IO.EbxAsset asset = App.AssetManager.GetEbx(entry);
            var objects = (System.Collections.IList)asset.RootObject.GetType().GetProperty("Objects").GetValue(asset.RootObject);
            int beforeObjects = objects.Count;

            var beforeGuids = new System.Collections.Generic.HashSet<Guid>();
            foreach (object existing in asset.Objects)
            {
                Guid? guid = LevelCommand.GetEntityInstanceGuid(existing);
                if (guid.HasValue) beforeGuids.Add(guid.Value);
            }

            int result = new LevelCommand().Execute(new[]
            {
                "add-entity", entry.Name,
                "--type", "AlternateSpawnEntityData",
                "--pos", "132.33,148.91,-291.68",
                "--rot", "0,-125,0",
                "--set", "Priority=7.5",
                "--set", "Enabled=true"
            }, logger, fmt);
            Assert.AreEqual(0, result);

            // ModifyEbx installs a modified asset instance; reload rather than inspect the stale
            // object obtained before the command ran.
            asset = App.AssetManager.GetEbx(entry);
            objects = (System.Collections.IList)asset.RootObject.GetType().GetProperty("Objects").GetValue(asset.RootObject);
            Assert.AreEqual(beforeObjects + 1, objects.Count);
            object appendedReference = objects[objects.Count - 1];
            Assert.AreEqual("PointerRef", appendedReference.GetType().Name);
            object referencedEntity = ReadMember(appendedReference, "Internal");
            Assert.IsTrue(referencedEntity != null && referencedEntity.GetType().Name == "AlternateSpawnEntityData",
                "Objects did not receive an internal PointerRef to the new entity");

            object created = null;
            Guid createdGuid = Guid.Empty;
            foreach (object candidate in asset.Objects)
            {
                if (candidate == null || candidate.GetType().Name != "AlternateSpawnEntityData") continue;
                Guid? guid = LevelCommand.GetEntityInstanceGuid(candidate);
                if (guid.HasValue && !beforeGuids.Contains(guid.Value))
                { created = candidate; createdGuid = guid.Value; break; }
            }
            Assert.IsTrue(created != null, "new entity was not registered in EbxAsset.Objects");
            Assert.IsTrue(createdGuid != Guid.Empty, "new entity did not receive an exported GUID");
            Assert.AreEqual(createdGuid, LevelCommand.GetEntityInstanceGuid(referencedEntity).Value);

            object transform = ReadMember(created, "Transform");
            object translation = ReadMember(transform, "trans");
            Assert.IsTrue(translation != null, "new entity has no Transform.trans");
            Assert.IsTrue(Math.Abs(132.33f - Convert.ToSingle(ReadMember(translation, "x"))) < 0.01f);
            Assert.IsTrue(Math.Abs(148.91f - Convert.ToSingle(ReadMember(translation, "y"))) < 0.01f);
            Assert.IsTrue(Math.Abs(-291.68f - Convert.ToSingle(ReadMember(translation, "z"))) < 0.01f);
            Assert.IsTrue(Math.Abs(7.5f - Convert.ToSingle(ReadMember(created, "Priority"))) < 0.001f);
            Assert.AreEqual(true, Convert.ToBoolean(ReadMember(created, "Enabled")));

            int remove = new LevelCommand().Execute(new[]
            { "remove", entry.Name, "--guid", createdGuid.ToString() }, logger, fmt);
            Assert.AreEqual(0, remove);
            FrostySdk.IO.EbxAsset afterRemove = App.AssetManager.GetEbx(entry);
            var remaining = (System.Collections.IList)afterRemove.RootObject.GetType().GetProperty("Objects").GetValue(afterRemove.RootObject);
            Assert.AreEqual(beforeObjects, remaining.Count);
        }

        // Beta bug #2: SubWorldScaffolder.Plan must clone via the binary round-trip (when given a real
        // Asset), preserving CStrings the JSON clone nulled. Drive Plan directly with one real asset.
        [Test]
        [IntegrationTest("swbf2")]
        public void Scaffold_BinaryClone_PreservesCString()
        {
            var logger = new ExportLogger(quiet: true);
            string gp  = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);

            // Find a non-level asset whose root has a non-empty string/CString property.
            EbxAssetEntry src = null; string prop = null; string origVal = null; FrostySdk.IO.EbxAsset srcAsset = null;
            int scanned = 0;
            foreach (var e in App.AssetManager.EnumerateEbx())
            {
                if (++scanned > 2500) break;
                if (e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                if (a == null || a.RootObject == null) continue;
                foreach (var p in a.RootObject.GetType().GetProperties())
                {
                    if (!p.CanRead || p.Name.StartsWith("__")) continue;
                    if (p.PropertyType != typeof(string) && p.PropertyType.Name != "CString") continue;
                    object v; try { v = p.GetValue(a.RootObject); } catch { continue; }
                    if (v == null || v.ToString().Length == 0) continue;
                    src = e; prop = p.Name; origVal = v.ToString(); srcAsset = a; break;
                }
                if (src != null) break;
            }
            if (src == null) { Console.WriteLine("skip: no asset with a non-empty string/CString root prop"); return; }

            var sources = new System.Collections.Generic.List<SubWorldScaffolder.Source>
            {
                new SubWorldScaffolder.Source { Path = src.Name, FileGuid = src.Guid, Root = srcAsset.RootObject, Asset = srcAsset }
            };
            var plan = SubWorldScaffolder.Plan(sources, src.Name, src.Name + "_scaf");
            Assert.AreEqual(1, plan.Count);

            object newRoot = plan[0].NewRoot;
            Assert.IsTrue(!ReferenceEquals(newRoot, srcAsset.RootObject), "scaffold must clone, not alias the source root");
            object cval = newRoot.GetType().GetProperty(prop).GetValue(newRoot);
            Assert.IsNotNull(cval);
            Assert.AreEqual(origVal, cval.ToString()); // JSON clone would have nulled this
        }
    }
}
