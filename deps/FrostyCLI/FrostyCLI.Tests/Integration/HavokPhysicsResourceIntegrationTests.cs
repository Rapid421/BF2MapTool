using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Tests.Integration
{
    // Round-trips a shipped Battlefront II group physics resource through HavokPhysicsResource:
    // an unedited write must reproduce the shipped bytes and cookie exactly, and an appended
    // instance must come back through the independent HavokTransformReader as one more transform
    // with structurally valid trees in every slot.
    public class HavokPhysicsResourceIntegrationTests
    {
        private const string EndorPhysics = "levels/mp/endor_01/endor_01/staticmodelgroup_physics_win32";

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

        private static byte[] ReadRes(ResAssetEntry entry)
        {
            MethodInfo m = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
            object raw = m.Invoke(App.AssetManager, new object[] { entry });
            if (raw is byte[] bytes) return bytes;
            Stream stream = raw as Stream;
            using (var ms = new MemoryStream()) { stream.CopyTo(ms); return ms.ToArray(); }
        }

        private static byte[] ResMeta(ResAssetEntry entry)
        {
            Type t = entry.GetType();
            PropertyInfo p = t.GetProperty("ResMeta");
            if (p != null) return p.GetValue(entry) as byte[];
            FieldInfo f = t.GetField("ResMeta");
            return f != null ? f.GetValue(entry) as byte[] : null;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void UneditedWrite_ReproducesShippedBytesAndCookie()
        {
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            ResAssetEntry entry = App.AssetManager.GetResEntry(EndorPhysics);
            Assert.IsNotNull(entry);
            byte[] shipped = ReadRes(entry);

            HavokPhysicsResource res = HavokPhysicsResource.Parse(shipped);
            byte[] rewritten = res.Write();
            Assert.AreEqual(shipped.Length, rewritten.Length);
            for (int i = 0; i < shipped.Length; i++)
                if (shipped[i] != rewritten[i]) throw new Exception("byte " + i + " differs: " + shipped[i] + " vs " + rewritten[i]);

            byte[] meta = ResMeta(entry);
            if (meta != null && meta.Length == 16)
            {
                byte[] cookie = res.Meta;
                for (int i = 0; i < 16; i++)
                    if (meta[i] != cookie[i]) throw new Exception("cookie byte " + i + " differs: " + BitConverter.ToString(meta) + " vs " + BitConverter.ToString(cookie));
            }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void AppendInstance_GrowsEverySlotAndKeepsTreesValid()
        {
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            byte[] shipped = ReadRes(App.AssetManager.GetResEntry(EndorPhysics));

            List<List<HavokTransformReader.Transform>> before;
            string err;
            Assert.IsTrue(HavokTransformReader.TryReadInstanceArrays(shipped, out before, out err), err);
            int placements = before[0].Count;

            HavokPhysicsResource res = HavokPhysicsResource.Parse(shipped);
            int parts = res.PartCount;
            HavokTransformReader.Transform sib = before[0][0];
            var xform = new LinearTransformData
            {
                Right = sib.Right, Up = sib.Up, Forward = sib.Forward,
                Trans = new[] { sib.Translation[0] + 5f, sib.Translation[1], sib.Translation[2] }
            };
            string detail;
            int index = res.AppendInstance(0, xform, out detail);
            Assert.IsTrue(index > 0 && index <= placements, "insertion index " + index);
            Assert.AreEqual(parts + 1, res.PartCount);

            byte[] grown = res.Write();
            Assert.IsTrue(grown.Length > shipped.Length);
            byte[] cookie = res.Meta;
            Assert.AreEqual((int)BitConverter.ToUInt16(cookie, 0) + BitConverter.ToInt32(cookie, 4) + BitConverter.ToInt32(cookie, 8) + BitConverter.ToInt32(cookie, 12), grown.Length);

            List<List<HavokTransformReader.Transform>> after;
            List<HavokTransformReader.CompoundTree> trees;
            Assert.IsTrue(HavokTransformReader.TryReadCompounds(grown, out after, out trees, out err), err);
            int grownSlots = 0;
            foreach (List<HavokTransformReader.Transform> arr in after) if (arr.Count == placements + 1) grownSlots++;
            Assert.IsTrue(grownSlots >= 2, "expected both level slots to grow, got " + grownSlots);
            HavokTransformReader.Transform added = HavokTransformReader.SelectPlacements(after, placements + 1)[index];
            Assert.IsTrue(Math.Abs(added.Translation[0] - xform.Trans[0]) < 1e-3f);
            string treeDetail;
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(after, trees, out treeDetail), treeDetail);

            // A second parse of the written bytes must itself round-trip cleanly.
            byte[] again = HavokPhysicsResource.Parse(grown).Write();
            Assert.AreEqual(grown.Length, again.Length);
            for (int i = 0; i < grown.Length; i++)
                if (grown[i] != again[i]) throw new Exception("re-written byte " + i + " differs");
        }
        private const string CoverPhysics = "levels/mp/endor_01/objects/endorbase/endorbase_cover_2x3_01_physics_win32";

        [Test]
        [IntegrationTest("swbf2")]
        public void AppendMember_TransplantsPropShapesIntoEveryProvidedSlot()
        {
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            byte[] group = ReadRes(App.AssetManager.GetResEntry(EndorPhysics));
            byte[] prop = ReadRes(App.AssetManager.GetResEntry(CoverPhysics));

            HavokPhysicsResource.PropInfo info = HavokPhysicsResource.InspectProp(prop);
            Assert.AreEqual(1, info.PartCount);
            Assert.AreEqual(2, info.RootCount);
            Assert.IsTrue(info.BoxMax[0] > info.BoxMin[0]);

            List<List<HavokTransformReader.Transform>> before;
            string err;
            Assert.IsTrue(HavokTransformReader.TryReadInstanceArrays(group, out before, out err), err);
            int placements = before[0].Count;

            HavokPhysicsResource res = HavokPhysicsResource.Parse(group);
            int partsBefore = res.PartCount;
            var xform = new LinearTransformData { Trans = new[] { 470f, 196f, 770f } };
            int parts;
            string detail;
            int firstPart = res.AppendMember(prop, xform, out parts, out detail);
            Assert.AreEqual(partsBefore, firstPart);
            Assert.AreEqual(1, parts);
            Assert.AreEqual(partsBefore + 1, res.PartCount);

            byte[] grown = res.Write();
            List<List<HavokTransformReader.Transform>> after;
            List<HavokTransformReader.CompoundTree> trees;
            Assert.IsTrue(HavokTransformReader.TryReadCompounds(grown, out after, out trees, out err), err);
            int grownSlots = 0;
            foreach (List<HavokTransformReader.Transform> arr in after) if (arr.Count == placements + 1) grownSlots++;
            Assert.AreEqual(2, grownSlots);
            HavokTransformReader.Transform added = HavokTransformReader.SelectPlacements(after, placements + 1)[placements];
            Assert.IsTrue(Math.Abs(added.Translation[2] - 770f) < 1e-3f);
            string treeDetail;
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(after, trees, out treeDetail), treeDetail);

            byte[] again = HavokPhysicsResource.Parse(grown).Write();
            Assert.AreEqual(grown.Length, again.Length);
            for (int i = 0; i < grown.Length; i++)
                if (grown[i] != again[i]) throw new Exception("re-written byte " + i + " differs");
        }
        private const string FlightPodPhysics = "objects/props/objectsets/kamino/flightpod/o_kam_flightpod_02_physics_win32";

        [Test]
        [IntegrationTest("swbf2")]
        public void AppendMember_GrowsClassTableForUnseenShapeClass()
        {
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            byte[] group = ReadRes(App.AssetManager.GetResEntry(EndorPhysics));
            byte[] prop = ReadRes(App.AssetManager.GetResEntry(FlightPodPhysics));
            Assert.IsTrue(Array.IndexOf(System.Text.Encoding.ASCII.GetString(group).Split(' '), "hknpSphereShape") < 0, "Endor already names hknpSphereShape");

            HavokPhysicsResource res = HavokPhysicsResource.Parse(group);
            int parts;
            string detail;
            res.AppendMember(prop, new LinearTransformData { Trans = new[] { 478f, 196f, 778f } }, out parts, out detail);
            Assert.IsTrue(detail.IndexOf("1 class name(s) added", StringComparison.Ordinal) >= 0, detail);

            byte[] grown = res.Write();
            List<List<HavokTransformReader.Transform>> after;
            List<HavokTransformReader.CompoundTree> trees;
            string err;
            Assert.IsTrue(HavokTransformReader.TryReadCompounds(grown, out after, out trees, out err), err);
            string treeDetail;
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(after, trees, out treeDetail), treeDetail);

            HavokPhysicsResource again = HavokPhysicsResource.Parse(grown);
            byte[] rewritten = again.Write();
            Assert.AreEqual(grown.Length, rewritten.Length);
            for (int i = 0; i < grown.Length; i++)
                if (grown[i] != rewritten[i]) throw new Exception("re-written byte " + i + " differs");
        }
        private const string ComputerPhysics = "objects/props/objectsets/_rebelalliance/computer_l_01/computer_l_01_physics_win32";

        [Test]
        [IntegrationTest("swbf2")]
        public void AppendMember_MultiPartProp_AddsEveryPartAndReusesCollisionRootForRaycast()
        {
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            byte[] group = ReadRes(App.AssetManager.GetResEntry(EndorPhysics));
            byte[] prop = ReadRes(App.AssetManager.GetResEntry(ComputerPhysics));

            HavokPhysicsResource.PropInfo info = HavokPhysicsResource.InspectProp(prop);
            Assert.AreEqual(2, info.PartCount);
            Assert.AreEqual(1, info.RootCount);
            Assert.AreEqual(2, info.PartBoxes.Length);

            HavokPhysicsResource res = HavokPhysicsResource.Parse(group);
            int partsBefore = res.PartCount;
            int parts;
            string detail;
            int firstPart = res.AppendMember(prop, new LinearTransformData { Trans = new[] { 480f, 196f, 780f } }, out parts, out detail);
            Assert.AreEqual(2, parts);
            Assert.AreEqual(partsBefore, firstPart);
            Assert.AreEqual(partsBefore + 2, res.PartCount);
            Assert.IsTrue(detail.IndexOf("slot 1 reuses the collision root", StringComparison.Ordinal) >= 0, detail);

            byte[] grown = res.Write();
            List<List<HavokTransformReader.Transform>> after;
            List<HavokTransformReader.CompoundTree> trees;
            string err;
            Assert.IsTrue(HavokTransformReader.TryReadCompounds(grown, out after, out trees, out err), err);
            string treeDetail;
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(after, trees, out treeDetail), treeDetail);
        }
        [Test]
        [IntegrationTest("swbf2")]
        public void MoveAndRemoveInstance_UpdateEverySlotAndKeepTreesValid()
        {
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            byte[] shipped = ReadRes(App.AssetManager.GetResEntry(EndorPhysics));
            List<List<HavokTransformReader.Transform>> before;
            string err;
            Assert.IsTrue(HavokTransformReader.TryReadInstanceArrays(shipped, out before, out err), err);
            int placements = before[0].Count;

            HavokPhysicsResource res = HavokPhysicsResource.Parse(shipped);
            string detail;
            res.MoveInstance(3, new LinearTransformData { Trans = new[] { 300f, 200f, 300f } }, out detail);
            byte[] moved = res.Write();
            List<List<HavokTransformReader.Transform>> after;
            List<HavokTransformReader.CompoundTree> trees;
            Assert.IsTrue(HavokTransformReader.TryReadCompounds(moved, out after, out trees, out err), err);
            int slots = 0;
            foreach (List<HavokTransformReader.Transform> arr in after)
                if (arr.Count == placements && Math.Abs(arr[3].Translation[0] - 300f) < 1e-3f) slots++;
            Assert.AreEqual(2, slots);
            string treeDetail;
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(after, trees, out treeDetail), treeDetail);

            HavokPhysicsResource again = HavokPhysicsResource.Parse(moved);
            int partsBefore = again.PartCount;
            int parts = again.RemoveInstance(3, out detail);
            Assert.AreEqual(1, parts);
            Assert.AreEqual(partsBefore - 1, again.PartCount);
            byte[] removed = again.Write();
            Assert.IsTrue(HavokTransformReader.TryReadCompounds(removed, out after, out trees, out err), err);
            int shrunk = 0;
            foreach (List<HavokTransformReader.Transform> arr in after) if (arr.Count == placements - 1) shrunk++;
            Assert.AreEqual(2, shrunk);
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(after, trees, out treeDetail), treeDetail);
            byte[] rewritten = HavokPhysicsResource.Parse(removed).Write();
            Assert.AreEqual(removed.Length, rewritten.Length);
        }
        // Appending three instances on one parsed resource must equal three parse/append/write
        // rounds byte for byte: the batched layer import relies on AppendInstance being re-entrant.
        [Test]
        [IntegrationTest("swbf2")]
        public void AppendInstance_IsReentrant()
        {
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            byte[] shipped = ReadRes(App.AssetManager.GetResEntry(EndorPhysics));
            var xforms = new[]
            {
                new LinearTransformData { Trans = new[] { 480f, 196f, 780f } },
                new LinearTransformData { Trans = new[] { 481f, 196f, 781f } },
                new LinearTransformData { Trans = new[] { 482f, 196f, 782f } }
            };
            HavokPhysicsResource batch = HavokPhysicsResource.Parse(shipped);
            string detail;
            foreach (LinearTransformData x in xforms) batch.AppendInstance(0, x, out detail);
            byte[] batched = batch.Write();
            byte[] stepwise = shipped;
            foreach (LinearTransformData x in xforms)
            {
                HavokPhysicsResource one = HavokPhysicsResource.Parse(stepwise);
                one.AppendInstance(0, x, out detail);
                stepwise = one.Write();
            }
            AssertSamePlacements(stepwise, batched);
            // The batched file carries no dead blocks: the stepwise chain re-parsed its appends as source
            // bytes and kept them, so it can only be larger.
            Assert.IsTrue(batched.Length <= stepwise.Length, "batched " + batched.Length + " > stepwise " + stepwise.Length);
        }

        // Compares what the game reads from two resources: every slot's placement records (transform
        // and part tag) and the validity of every tree. Byte equality is not expected: each rebuild
        // re-quantises the leaf boxes, and compaction changes the block layout.
        private static void AssertSamePlacements(byte[] expected, byte[] actual)
        {
            List<List<HavokTransformReader.Transform>> a, b;
            List<HavokTransformReader.CompoundTree> ta, tb;
            string err;
            Assert.IsTrue(HavokTransformReader.TryReadCompounds(expected, out a, out ta, out err), err);
            Assert.IsTrue(HavokTransformReader.TryReadCompounds(actual, out b, out tb, out err), err);
            Assert.AreEqual(a.Count, b.Count);
            for (int s = 0; s < a.Count; s++)
            {
                Assert.AreEqual(a[s].Count, b[s].Count);
                for (int i = 0; i < a[s].Count; i++)
                {
                    HavokTransformReader.Transform x = a[s][i], y = b[s][i];
                    for (int c = 0; c < 3; c++)
                    {
                        Assert.AreEqual(x.Right[c], y.Right[c]); Assert.AreEqual(x.Up[c], y.Up[c]);
                        Assert.AreEqual(x.Forward[c], y.Forward[c]); Assert.AreEqual(x.Translation[c], y.Translation[c]);
                    }
                }
            }
            string treeDetail;
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(b, tb, out treeDetail), treeDetail);
            HavokPhysicsResource pa = HavokPhysicsResource.Parse(expected), pb = HavokPhysicsResource.Parse(actual);
            Assert.AreEqual(pa.Placement.InstanceCount, pb.Placement.InstanceCount);
            byte[] ma = pa.Meta, mb = pb.Meta;
            Assert.AreEqual(BitConverter.ToInt32(ma, 4), BitConverter.ToInt32(mb, 4));   // 32-bit pack untouched by both
        }

        // AppendInstances (one tree rebuild for the whole batch) must place exactly what the same
        // transforms appended one at a time place.
        [Test]
        [IntegrationTest("swbf2")]
        public void AppendInstances_MatchesSequentialAppends()
        {
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            byte[] shipped = ReadRes(App.AssetManager.GetResEntry(EndorPhysics));
            var xforms = new List<LinearTransformData>();
            for (int i = 0; i < 7; i++)
                xforms.Add(new LinearTransformData { Right = new[] { 0f, 0f, 1f }, Up = new[] { 0f, 1f, 0f }, Forward = new[] { -1f, 0f, 0f }, Trans = new[] { 470f + i * 1.5f, 196f, 770f + i } });
            HavokPhysicsResource many = HavokPhysicsResource.Parse(shipped);
            string detail;
            many.AppendInstances(3, xforms, out detail);
            byte[] batched = many.Write();
            HavokPhysicsResource one = HavokPhysicsResource.Parse(shipped);
            foreach (LinearTransformData x in xforms) one.AppendInstance(3, x, out detail);
            byte[] sequential = one.Write();
            AssertSamePlacements(sequential, batched);
            Assert.AreEqual(sequential.Length, batched.Length);   // same live blocks after compaction
        }

        // Each parse/append/write round used to leave the previous round's arrays in the file (a
        // 150 MB group resource after a full room). The growth per round must stay about one record
        // per slot plus a few tree nodes.
        [Test]
        [IntegrationTest("swbf2")]
        public void RepeatedRounds_DoNotAccumulateDeadBytes()
        {
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            byte[] cur = ReadRes(App.AssetManager.GetResEntry(EndorPhysics));
            var sizes = new List<int>();
            string detail;
            for (int round = 0; round < 6; round++)
            {
                HavokPhysicsResource r = HavokPhysicsResource.Parse(cur);
                r.AppendInstance(0, new LinearTransformData { Trans = new[] { 500f + round, 196f, 800f } }, out detail);
                cur = r.Write();
                sizes.Add(cur.Length);
            }
            int first = sizes[1] - sizes[0], last = sizes[5] - sizes[4];
            Assert.IsTrue(last <= first + 64, "growth per round went from " + first + " to " + last + " bytes: dead arrays are accumulating");
            Assert.IsTrue(last < 3 * 0x80 + 3 * 64 + 256, "a single append grew the file by " + last + " bytes");
            List<List<HavokTransformReader.Transform>> after;
            List<HavokTransformReader.CompoundTree> trees;
            string err;
            Assert.IsTrue(HavokTransformReader.TryReadCompounds(cur, out after, out trees, out err), err);
            string treeDetail;
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(after, trees, out treeDetail), treeDetail);
        }

        // A transplanted convex root's record must carry the shape object's own size in its
        // col2.w lane (shipped groups: every polytope record equals its object size; a fixed 400
        // crashed Endor on load), while compound and mesh roots keep 208 and 160.
        [Test]
        [IntegrationTest("swbf2")]
        public void AppendMember_RecordCarriesTheConvexShapeObjectSize()
        {
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            byte[] group = ReadRes(App.AssetManager.GetResEntry(EndorPhysics));
            ResAssetEntry propEntry = App.AssetManager.GetResEntry("objects/props/objectsets/bespin/cryochamber_pipe_01/cryochamber_metalpipe_s64_01_physics_win32");
            if (propEntry == null) { Console.WriteLine("  skip: Bespin pipe resource not found"); return; }
            byte[] prop = ReadRes(propEntry);
            HavokPhysicsResource res = HavokPhysicsResource.Parse(group);
            int parts; string detail;
            res.AppendMember(prop, new LinearTransformData { Trans = new[] { 490f, 197f, 792f } }, out parts, out detail);
            byte[] grown = res.Write();
            HavokPhysicsResource re = HavokPhysicsResource.Parse(grown);
            int[] sizes = re.RecordShapeSizes(re.Placement);
            int[] objSizes = re.RecordShapeObjectSizes(re.Placement);
            int last = sizes.Length - 1;
            Assert.AreEqual(624, sizes[last]);
            Assert.AreEqual(objSizes[last], sizes[last]);
        }

        // Cross-game check: a Battlefront (2015) prop resource given by FROSTYCLI_TEST_PROP_FILE is
        // transplanted into the Endor_01 group. Skipped silently when the variable is unset.
        [Test]
        [IntegrationTest("swbf2")]
        public void AppendMember_AcceptsAPropResourceFromFile()
        {
            string file = Environment.GetEnvironmentVariable("FROSTYCLI_TEST_PROP_FILE");
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, new ExportLogger(quiet: true));
            byte[] group = ReadRes(App.AssetManager.GetResEntry(EndorPhysics));
            byte[] prop = File.ReadAllBytes(file);
            HavokPhysicsResource.PropInfo info = HavokPhysicsResource.InspectProp(prop);
            HavokPhysicsResource res = HavokPhysicsResource.Parse(group);
            int parts;
            string detail;
            res.AppendMember(prop, new LinearTransformData { Trans = new[] { 490f, 196f, 790f } }, out parts, out detail);
            Console.WriteLine("  prop file: parts " + info.PartCount + " roots " + info.RootCount + " -> " + detail);
            byte[] grown = res.Write();
            List<List<HavokTransformReader.Transform>> after;
            List<HavokTransformReader.CompoundTree> trees;
            string err;
            Assert.IsTrue(HavokTransformReader.TryReadCompounds(grown, out after, out trees, out err), err);
            string treeDetail;
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(after, trees, out treeDetail), treeDetail);
            byte[] again = HavokPhysicsResource.Parse(grown).Write();
            Assert.AreEqual(grown.Length, again.Length);
            File.WriteAllBytes(file + ".grown.hkx", grown);
        }
    }
}
