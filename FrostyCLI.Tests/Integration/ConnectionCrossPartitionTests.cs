using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    // Authoring connections whose endpoints live in ANOTHER partition.
    //
    // Kashyyyk's mode graph is the reference case: 63 event connections on the SubWorldData of
    // Levels/MP/Kashyyyk_01/GameModes, most of which reach into Levels/MP/Kashyyyk_01/GameModes_Flow.
    // Both directions occur, and so does external-to-external. Until these pass, a logic graph can be
    // read but not extended -- and an endpoint written as an internal ref when it should be external
    // resolves to nothing in game without erroring anywhere.
    public class ConnectionCrossPartitionTests
    {
        private const string GameModes = "Levels/MP/Kashyyyk_01/GameModes";
        private const string Flow      = "Levels/MP/Kashyyyk_01/GameModes_Flow";

        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string adaptersDir = Path.Combine(exeDir, "Adapters");
            if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);
            Session.Initialize("swbf2", gp, null, adaptersDir, logger);
        }

        // The acceptance case: author a connection with an EXTERNAL source and an internal target,
        // and have every field match the shipped connections beside it -- the two event ids, the
        // realm enum, and both PointerRefs.
        [Test]
        [IntegrationTest("swbf2")]
        public void ConnectionAdd_ExternalSource_MatchesTheShippedShape()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry modes = App.AssetManager.GetEbxEntry(GameModes);
            EbxAssetEntry flow  = App.AssetManager.GetEbxEntry(Flow);
            if (modes == null || flow == null) { Console.WriteLine("skip: Kashyyyk assets not found"); return; }

            IList conns = EventConnections(App.AssetManager.GetEbx(modes));
            if (conns == null || conns.Count == 0) { Console.WriteLine("skip: no shipped event connections"); return; }
            int before = conns.Count;

            // Copy a shipped connection's own endpoints, so "did we write what the game writes?" is
            // decidable by comparing against that exact record rather than against a guess.
            object model = FindExternalSourceConnection(conns);
            if (model == null) { Console.WriteLine("skip: no shipped external-source connection"); return; }
            Guid modelFile  = ExternalFileGuid(Read(model, "Source"));
            Guid modelClass = ExternalClassGuid(Read(model, "Source"));
            object modelTargetObj = Read(Read(model, "Target"), "Internal");
            Guid modelTargetGuid = PointerRefUtil.InstanceGuid(modelTargetObj);
            if (modelTargetGuid == Guid.Empty) { Console.WriteLine("skip: model target is not exported"); return; }

            int rc = ConnectionCommand.ExecuteAdd(new[]
            {
                GameModes, "--kind", "event",
                "--source-ebx", Flow, "--source", modelClass.ToString(), "--source-field", "OnTrue",
                "--target", modelTargetGuid.ToString(), "--target-field", "StreamIn",
                "--realm", "Server"
            }, logger, fmt);
            Assert.AreEqual(0, rc);

            try
            {
                FrostySdk.IO.EbxAsset reloaded = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(GameModes));
                IList after = EventConnections(reloaded);
                Assert.AreEqual(before + 1, after.Count);
                object added = after[after.Count - 1];

                // Source: an EXTERNAL ref naming the other partition and the endpoint's exported guid.
                object src = Read(added, "Source");
                Assert.AreEqual("External", Read(src, "Type").ToString());
                Assert.AreEqual(flow.Guid, ExternalFileGuid(src));
                Assert.AreEqual(modelClass, ExternalClassGuid(src));
                Assert.AreEqual(modelFile, ExternalFileGuid(src));

                // Target: still an internal ref to the object in this partition.
                object tgt = Read(added, "Target");
                Assert.AreEqual("Internal", Read(tgt, "Type").ToString());
                Assert.AreEqual(modelTargetGuid, PointerRefUtil.InstanceGuid(Read(tgt, "Internal")));

                // Port ids and realm must equal the shipped record's.
                Assert.AreEqual(EventId(Read(model, "SourceEvent")), EventId(Read(added, "SourceEvent")));
                Assert.AreEqual(unchecked((int)0xD27BC17E), EventId(Read(added, "TargetEvent")));
                Assert.AreEqual(EventId(Read(model, "TargetEvent")), EventId(Read(added, "TargetEvent")));
                Assert.AreEqual(Read(model, "TargetType").ToString(), Read(added, "TargetType").ToString());

                // Bundling walks Dependencies; without the other partition listed there the ref
                // survives the save and the asset it points at can be left out of the bundle.
                bool declared = false;
                foreach (Guid g in reloaded.Dependencies) if (g == flow.Guid) { declared = true; break; }
                Assert.IsTrue(declared, "the external endpoint's asset must be registered as a dependency");
            }
            finally
            {
                ConnectionCommand.ExecuteRemove(new[]
                { GameModes, "--kind", "event", "--index", before.ToString() }, logger, fmt);
            }
        }

        // `connection ls` has to render an authored connection exactly like the shipped one it copies,
        // or "does this match shipped data?" cannot be answered from the tool's own output.
        [Test]
        [IntegrationTest("swbf2")]
        public void ConnectionAdd_ExternalSource_RendersLikeItsShippedNeighbour()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry modes = App.AssetManager.GetEbxEntry(GameModes);
            if (modes == null) { Console.WriteLine("skip: Kashyyyk assets not found"); return; }
            IList conns = EventConnections(App.AssetManager.GetEbx(modes));
            object model = FindExternalSourceConnection(conns);
            if (model == null) { Console.WriteLine("skip: no shipped external-source connection"); return; }
            int before = conns.Count;

            Guid modelClass = ExternalClassGuid(Read(model, "Source"));
            Guid modelTargetGuid = PointerRefUtil.InstanceGuid(Read(Read(model, "Target"), "Internal"));
            if (modelTargetGuid == Guid.Empty) { Console.WriteLine("skip: model target is not exported"); return; }

            int rc = ConnectionCommand.ExecuteAdd(new[]
            {
                GameModes, "--kind", "event",
                "--source-ebx", Flow, "--source", modelClass.ToString(), "--source-field", "OnTrue",
                "--target", modelTargetGuid.ToString(), "--target-field", "StreamIn",
                "--realm", "Server"
            }, logger, fmt);
            Assert.AreEqual(0, rc);

            try
            {
                List<string> lines = LsLines();
                string authored = lines[lines.Count - 1];
                lines.RemoveAt(lines.Count - 1);
                Assert.IsTrue(lines.Contains(authored),
                    "the authored connection should render exactly like a shipped one; got: " + authored);
            }
            finally
            {
                ConnectionCommand.ExecuteRemove(new[]
                { GameModes, "--kind", "event", "--index", before.ToString() }, logger, fmt);
            }
        }

        // An external ref addresses its target by exported guid. Wiring a non-exported object
        // produces a reference to nothing that authors and saves without complaint.
        [Test]
        [IntegrationTest("swbf2")]
        public void ConnectionAdd_ExternalEndpointNotExported_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            if (App.AssetManager.GetEbxEntry(GameModes) == null)
            { Console.WriteLine("skip: Kashyyyk assets not found"); return; }

            // Find a real non-exported object in some OTHER asset. Not every partition has one
            // (GameModes_Flow does not), so search the level folder rather than assuming.
            string otherAsset = null, unexportedType = null;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name == null || !e.Name.StartsWith("Levels/MP/Kashyyyk_01/", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(e.Name, GameModes, StringComparison.OrdinalIgnoreCase)) continue;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                foreach (object o in a.Objects)
                {
                    if (o == null || PointerRefUtil.InstanceGuid(o) != Guid.Empty) continue;
                    // Must be addressable by type name, or the selector cannot reach it at all.
                    otherAsset = e.Name; unexportedType = o.GetType().Name; break;
                }
                if (otherAsset != null) break;
            }
            if (otherAsset == null) { Console.WriteLine("skip: no non-exported object found under Levels/MP/Kashyyyk_01/"); return; }
            Console.WriteLine("using non-exported " + unexportedType + " in " + otherAsset);

            int rc = ConnectionCommand.ExecuteAdd(new[]
            {
                GameModes, "--kind", "event",
                "--source-ebx", otherAsset, "--source", unexportedType, "--source-field", "OnTrue",
                "--target", "SubWorldReferenceObjectData", "--target-field", "StreamIn"
            }, logger, fmt);
            Assert.AreEqual(1, rc);
        }

        // `new:` creates the endpoint object in the asset being edited. Combined with --source-ebx it
        // would have to create it in an asset this command never saves, so the object would be lost
        // and the reference left pointing at nothing.
        [Test]
        [IntegrationTest("swbf2")]
        public void ConnectionAdd_NewObjectInAnotherAsset_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();
            if (App.AssetManager.GetEbxEntry(GameModes) == null) { Console.WriteLine("skip: asset not found"); return; }

            string err;
            int rc = RunCapturingStderr(new[]
            {
                GameModes, "--kind", "event",
                "--source-ebx", Flow, "--source", "new:BoolEntityData", "--source-field", "OnTrue",
                "--target", "SubWorldReferenceObjectData", "--target-field", "StreamIn"
            }, logger, fmt, out err);
            Assert.AreEqual(1, rc);
            // Assert on the reason, not just the code: without the guard this still fails, but as a
            // baffling "not found in <asset>: new:BoolEntityData" instead of naming the real problem.
            Assert.IsTrue(err.IndexOf("new:", StringComparison.Ordinal) >= 0 &&
                          err.IndexOf("-ebx", StringComparison.Ordinal) >= 0,
                "the error should explain that new: cannot be combined with --*-ebx; got: " + err);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ConnectionAdd_UnknownExternalAsset_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();
            if (App.AssetManager.GetEbxEntry(GameModes) == null) { Console.WriteLine("skip: asset not found"); return; }

            string err;
            int rc = RunCapturingStderr(new[]
            {
                GameModes, "--kind", "event",
                "--source-ebx", "Levels/MP/NoSuchLevel/NoSuchAsset", "--source", "BoolEntityData",
                "--source-field", "OnTrue",
                "--target", "SubWorldReferenceObjectData", "--target-field", "StreamIn"
            }, logger, fmt, out err);
            Assert.AreEqual(1, rc);
            // Specifically "there is no such asset", not "the selector missed inside it" -- those are
            // different faults and only the first is what this test is about.
            Assert.IsTrue(err.IndexOf("asset not found", StringComparison.Ordinal) >= 0 &&
                          err.IndexOf("Levels/MP/NoSuchLevel/NoSuchAsset", StringComparison.Ordinal) >= 0,
                "the error should say the --source-ebx asset does not exist; got: " + err);
        }

        // An unknown option used to be skipped while its VALUE became the asset path, so an older
        // build given --source-ebx wrote the connection into the wrong file and reported success.
        [Test]
        [IntegrationTest("swbf2")]
        public void ConnectionAdd_UnknownOption_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();
            if (App.AssetManager.GetEbxEntry(GameModes) == null) { Console.WriteLine("skip: asset not found"); return; }

            string err;
            int rc = RunCapturingStderr(new[]
            {
                GameModes, "--kind", "event",
                "--source-abx", Flow, "--source", "SubWorldReferenceObjectData", "--source-field", "OnTrue",
                "--target", "SubWorldReferenceObjectData", "--target-field", "StreamIn"
            }, logger, fmt, out err);
            Assert.AreEqual(1, rc);
            Assert.IsTrue(err.IndexOf("--source-abx", StringComparison.Ordinal) >= 0,
                "the error should name the unknown option; got: " + err);
        }

        // ---------------- helpers ----------------

        // Refusals are only useful if they say WHY, so the tests assert on the message. Several of
        // these arguments are rejected by more than one rule; without the text, a test passes on
        // whichever rule fires first and stops proving anything about the one it was written for.
        private static int RunCapturingStderr(string[] args, ExportLogger logger, OutputFormatter fmt,
                                              out string stderr)
        {
            var sb = new StringBuilder();
            System.IO.TextWriter previous = Console.Error;
            Console.SetError(new StringWriter(sb));
            try { return ConnectionCommand.ExecuteAdd(args, logger, fmt); }
            finally { Console.SetError(previous); stderr = sb.ToString(); }
        }

        // One "source -> target" string per rendered connection, in list order.
        private static List<string> LsLines()
        {
            var sb = new StringBuilder();
            var cap = new OutputFormatter(json: false, output: new StringWriter(sb));
            ConnectionCommand.ExecuteLs(new[] { GameModes, "--kind", "event" },
                                        new ExportLogger(quiet: true), cap);
            var lines = new List<string>();
            string pendingSource = null;
            foreach (string raw in sb.ToString().Split('\n'))
            {
                string s = raw.Trim();
                if (s.StartsWith("source:", StringComparison.Ordinal)) pendingSource = s;
                else if (s.StartsWith("target:", StringComparison.Ordinal) && pendingSource != null)
                {
                    lines.Add(pendingSource + " -> " + s);
                    pendingSource = null;
                }
            }
            return lines;
        }

        private static IList EventConnections(FrostySdk.IO.EbxAsset asset)
        {
            foreach (object o in asset.Objects)
            {
                if (o == null) continue;
                PropertyInfo pi = o.GetType().GetProperty("EventConnections", BindingFlags.Public | BindingFlags.Instance);
                if (pi == null) continue;
                IList list;
                try { list = pi.GetValue(o) as IList; } catch { continue; }
                if (list != null && list.Count > 0) return list;
            }
            return null;
        }

        // A shipped connection whose Source is external and whose Target is an internal exported object.
        private static object FindExternalSourceConnection(IList conns)
        {
            foreach (object c in conns)
            {
                object src = Read(c, "Source");
                object tgt = Read(c, "Target");
                if (src == null || tgt == null) continue;
                object srcType = Read(src, "Type");
                object tgtType = Read(tgt, "Type");
                if (srcType == null || tgtType == null) continue;
                if (srcType.ToString() != "External" || tgtType.ToString() != "Internal") continue;
                if (PointerRefUtil.InstanceGuid(Read(tgt, "Internal")) == Guid.Empty) continue;
                return c;
            }
            return null;
        }

        private static Guid ExternalFileGuid(object pointerRef)
        {
            object ext = Read(pointerRef, "External");
            object g = ext == null ? null : Read(ext, "FileGuid");
            return g is Guid ? (Guid)g : Guid.Empty;
        }

        private static Guid ExternalClassGuid(object pointerRef)
        {
            object ext = Read(pointerRef, "External");
            object g = ext == null ? null : Read(ext, "ClassGuid");
            return g is Guid ? (Guid)g : Guid.Empty;
        }

        private static int EventId(object eventSpec)
        {
            object id = eventSpec == null ? null : Read(eventSpec, "Id");
            return id == null ? 0 : Convert.ToInt32(id);
        }

        // EbxImportReference exposes FileGuid/ClassGuid as public FIELDS, so a property-only read
        // returns null and every external endpoint looks empty.
        private static object Read(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(o);
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f == null ? null : f.GetValue(o);
        }
    }
}
