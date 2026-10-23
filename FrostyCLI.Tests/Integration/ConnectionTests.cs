using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    // P6: `connection ls` -- inspect a blueprint's Property/Link/Event connection graph (the Frosty
    // Connection Editor, read side) -- plus authoring endpoints that live in the SAME partition.
    // Endpoints in another asset are covered by ConnectionCrossPartitionTests.
    public class ConnectionTests
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

        // The field id on each side of a connection is a hashed name, not a name. These four are
        // read straight off shipped data (PF_IndexObjectiveIconSelect), so if the hash ever stops
        // matching, authoring silently wires connections to fields that do not exist.
        [Test]
        public void Connection_FieldIds_HashFromNames()
        {
            uint id;
            Assert.IsTrue(ConnectionCommand.TryResolveFieldId("Value", out id));
            Assert.AreEqual(0x0d6ef36eu, id);
            Assert.IsTrue(ConnectionCommand.TryResolveFieldId("StringId", out id));
            Assert.AreEqual(0xd2c4765du, id);
            Assert.IsTrue(ConnectionCommand.TryResolveFieldId("In0", out id));
            Assert.AreEqual(0x0b87d372u, id);
            Assert.IsTrue(ConnectionCommand.TryResolveFieldId("In1", out id));
            Assert.AreEqual(0x0b87d373u, id);
        }

        // An explicit id has to survive untouched, so a field whose name is unknown can still be wired.
        [Test]
        public void Connection_FieldIds_AcceptExplicitIds()
        {
            uint id;
            Assert.IsTrue(ConnectionCommand.TryResolveFieldId("0xd067e234", out id));
            Assert.AreEqual(0xd067e234u, id);
            Assert.IsTrue(ConnectionCommand.TryResolveFieldId("12345", out id));
            Assert.AreEqual(12345u, id);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ConnectionAdd_WiresTwoObjects_AndRemoveUndoesIt()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            const string blueprint = "Addons/Mode1/Mode1/UI/InGame/Hud/Shared/PF_IndexObjectiveIconSelect";
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(blueprint);
            if (entry == null) { Console.WriteLine("skip: test blueprint not found"); return; }
            FrostySdk.IO.EbxAsset asset = App.AssetManager.GetEbx(entry);

            IList connections = FindPropertyConnections(asset);
            if (connections == null) { Console.WriteLine("skip: no PropertyConnections list"); return; }
            int before = connections.Count;

            // Wire two objects that already exist in the partition. Which two does not matter; that
            // the endpoints resolve and the ids land does.
            string a = null, b = null;
            foreach (object o in asset.Objects)
            {
                string g = InstanceGuidOf(o);
                if (g == null) continue;
                if (a == null) a = g;
                else if (b == null && g != a) { b = g; break; }
            }
            if (a == null || b == null) { Console.WriteLine("skip: fewer than two addressable objects"); return; }

            int rc = ConnectionCommand.ExecuteAdd(new[]
            {
                blueprint, "--source", a, "--source-field", "Value",
                "--target", b, "--target-field", "In0"
            }, logger, fmt);
            Assert.AreEqual(0, rc);

            IList after = FindPropertyConnections(App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(blueprint)));
            Assert.AreEqual(before + 1, after.Count);

            // Both representations have to agree: the signed id and the "0x…" CString beside it.
            object added = after[after.Count - 1];
            Assert.AreEqual(unchecked((int)0x0d6ef36e), Convert.ToInt32(ReadAny(added, "SourceFieldId")));
            Assert.AreEqual(unchecked((int)0x0b87d372), Convert.ToInt32(ReadAny(added, "TargetFieldId")));
            Assert.AreEqual("0x0d6ef36e", ReadAny(added, "SourceField").ToString());
            Assert.AreEqual("0x0b87d372", ReadAny(added, "TargetField").ToString());
            // 1 == realm ClientAndServer, the documented default since --realm was introduced. This
            // expectation said 2 (Client), which was the default when it was written -- Client is
            // what shipped data uses most (51.8% of 285,632 PropertyConnections, against 27.0%
            // Server and 21.2% ClientAndServer), but the default was deliberately changed to the
            // realm that behaves on BOTH sides, so a caller who has not thought about realm gets a
            // connection that runs rather than one that is silently inert. Assert the default that
            // TryBuildFlags actually documents.
            Assert.AreEqual(1, Convert.ToInt32(ReadAny(added, "Flags")));
            Assert.AreEqual(a, InstanceGuidOf(ResolveInternalRef(ReadAny(added, "Source"))));
            Assert.AreEqual(b, InstanceGuidOf(ResolveInternalRef(ReadAny(added, "Target"))));

            // Removing the connection we just made must put the list back exactly as it was.
            int rr = ConnectionCommand.ExecuteRemove(new[]
            { blueprint, "--index", (after.Count - 1).ToString() }, logger, fmt);
            Assert.AreEqual(0, rr);
            IList restored = FindPropertyConnections(App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(blueprint)));
            Assert.AreEqual(before, restored.Count);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ConnectionAdd_UnknownEndpoint_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            const string blueprint = "Addons/Mode1/Mode1/UI/InGame/Hud/Shared/PF_IndexObjectiveIconSelect";
            if (App.AssetManager.GetEbxEntry(blueprint) == null) { Console.WriteLine("skip: test blueprint not found"); return; }
            int rc = ConnectionCommand.ExecuteAdd(new[]
            {
                blueprint, "--source", "ffffffff-ffff-ffff-ffff-ffffffffffff", "--source-field", "Value",
                "--target", "ffffffff-ffff-ffff-ffff-fffffffffffe", "--target-field", "In0"
            }, logger, fmt);
            Assert.AreEqual(1, rc);
        }

        private static IList FindPropertyConnections(FrostySdk.IO.EbxAsset asset)
        {
            foreach (object o in asset.Objects)
            {
                if (o == null) continue;
                PropertyInfo pi = o.GetType().GetProperty("PropertyConnections", BindingFlags.Public | BindingFlags.Instance);
                if (pi == null) continue;
                IList list;
                try { list = pi.GetValue(o) as IList; } catch { continue; }
                if (list != null) return list;
            }
            return null;
        }

        private static string InstanceGuidOf(object o)
        {
            if (o == null) return null;
            try
            {
                MethodInfo gig = o.GetType().GetMethod("GetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                object g = gig == null ? null : gig.Invoke(o, null);
                return g == null ? null : g.ToString();
            }
            catch { return null; }
        }

        private static object ReadAny(object o, string name)
        {
            if (o == null) return null;
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(o);
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f == null ? null : f.GetValue(o);
        }

        private static object ResolveInternalRef(object pointerRef)
        {
            return ReadAny(pointerRef, "Internal");
        }

        [Test]
        public void Connection_MissingArg_Returns1()
        {
            int rc = new ConnectionCommand().Execute(new string[0], new ExportLogger(quiet: true), new OutputFormatter(false));
            Assert.AreEqual(1, rc);
        }

        [Test]
        public void Connection_UnknownSub_Returns1()
        {
            int rc = new ConnectionCommand().Execute(new[] { "frobnicate" }, new ExportLogger(quiet: true), new OutputFormatter(false));
            Assert.AreEqual(1, rc);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void Connection_Ls_ListsBlueprintConnections()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);

            // Find an asset with a non-empty "*Connection*" list on ANY of its objects. Narrow by type
            // name first (Blueprint/Prefab/Logic/Graph) to avoid loading every EBX.
            EbxAssetEntry target = null; int scanned = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Type == null) continue;
                if (e.Type.IndexOf("Blueprint", StringComparison.OrdinalIgnoreCase) < 0
                    && e.Type.IndexOf("Prefab", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++scanned > 12000) break;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                foreach (object o in a.Objects) if (HasConnections(o)) { target = e; break; }
                if (target != null) break;
            }
            if (target == null) { Console.WriteLine("skip: no blueprint with connections found in scan budget"); return; }

            var sb = new StringBuilder();
            var capFmt = new OutputFormatter(json: false, output: new StringWriter(sb));
            int rc = ConnectionCommand.ExecuteLs(new[] { target.Name }, logger, capFmt);
            Assert.AreEqual(0, rc);
            string outp = sb.ToString();
            Assert.IsTrue(outp.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0,
                "connection ls should print at least one connection's source/target for " + target.Name);
        }

        private static bool HasConnections(object root)
        {
            foreach (PropertyInfo pi in root.GetType().GetProperties())
            {
                if (pi.Name.IndexOf("Connection", StringComparison.OrdinalIgnoreCase) < 0) continue;
                IList list = null;
                try { list = pi.GetValue(root) as IList; } catch { }
                if (list != null && list.Count > 0) return true;
            }
            return false;
        }
    }
}
