using System;
using System.Collections.Generic;
using System.IO;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    // Covers the record-list plumbing and the height sources, which are the parts of the terrain
    // path that can be exercised without a game install. The format parse/serialise itself is
    // covered by round-tripping every shipped terrain: `terrain validate <res>` reports
    // "byte-identical", and all 12 shipped Battlefront II terrains pass.
    public class TerrainResourceTests
    {
        private static ExportLogger Logger() { return new ExportLogger(quiet: true); }
        private static OutputFormatter Fmt() { return new OutputFormatter(json: false); }

        // header (26 B) + [tag][u32 len][body]... + 0xFF + trailing streaming-node bytes
        private static byte[] Payload(int trailing, params KeyValuePair<int, int>[] records)
        {
            var ms = new MemoryStream();
            for (int i = 0; i < TerrainPayload.HeaderLength; i++) ms.WriteByte((byte)i);
            foreach (KeyValuePair<int, int> r in records)
            {
                ms.WriteByte((byte)r.Key);
                ms.Write(BitConverter.GetBytes(r.Value), 0, 4);
                for (int i = 0; i < r.Value; i++) ms.WriteByte((byte)(r.Key * 16 + (i & 0x0F)));
            }
            ms.WriteByte(TerrainPayload.Terminator);
            for (int i = 0; i < trailing; i++) ms.WriteByte(0xAB);
            return ms.ToArray();
        }

        private static KeyValuePair<int, int> Rec(int tag, int length)
        { return new KeyValuePair<int, int>(tag, length); }

        [Test]
        public void Records_WalksEveryRecordAndFindsTheTerminator()
        {
            byte[] payload = Payload(7, Rec(0, 32), Rec(1, 16), Rec(3, 8));
            int terminator;
            List<TerrainRecord> records = TerrainPayload.Records(payload, out terminator);

            Assert.AreEqual(3, records.Count);
            Assert.AreEqual(0, records[0].Tag);
            Assert.AreEqual(32, records[0].Length);
            Assert.AreEqual(TerrainPayload.HeaderLength, records[0].Start);
            Assert.AreEqual(TerrainPayload.HeaderLength + 5 + 32, records[0].End);
            Assert.AreEqual(3, records[2].Tag);
            Assert.AreEqual(payload.Length - 7 - 1, terminator);
            Assert.AreEqual(TerrainPayload.Terminator, payload[terminator]);
        }

        [Test]
        public void Records_RejectsALengthPastTheEndOfThePayload()
        {
            byte[] payload = Payload(0, Rec(0, 16));
            // Overstate the record length: a walk that trusted it would read out of bounds.
            byte[] bogus = BitConverter.GetBytes(1 << 20);
            Array.Copy(bogus, 0, payload, TerrainPayload.HeaderLength + 1, 4);
            Assert.Throws<InvalidDataException>(delegate { int t; TerrainPayload.Records(payload, out t); });
        }

        [Test]
        public void Records_RejectsAPayloadWithNoTerminator()
        {
            byte[] payload = Payload(0, Rec(0, 8));
            var truncated = new byte[payload.Length - 1];      // drops the 0xFF
            Array.Copy(payload, truncated, truncated.Length);
            Assert.Throws<InvalidDataException>(delegate { int t; TerrainPayload.Records(truncated, out t); });
        }

        [Test]
        public void Isolate_KeepsTheHeaderTheOneRecordAndTheStreamingNodes()
        {
            byte[] payload = Payload(5, Rec(0, 32), Rec(1, 16), Rec(3, 8));
            TerrainRecord record;
            byte[] isolated = TerrainPayload.Isolate(payload, TerrainPayload.TagMask, out record);

            Assert.AreEqual(1, record.Tag);
            Assert.AreEqual(TerrainPayload.HeaderLength + 5 + 16 + 1 + 5, isolated.Length);

            int terminator;
            List<TerrainRecord> records = TerrainPayload.Records(isolated, out terminator);
            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(1, records[0].Tag);
            Assert.AreEqual(16, records[0].Length);
            Assert.AreEqual(5, isolated.Length - terminator - 1);   // trailing bytes survive
            for (int i = 0; i < TerrainPayload.HeaderLength; i++) Assert.AreEqual(payload[i], isolated[i]);
        }

        [Test]
        public void Isolate_ReturnsNullForAMissingTag()
        {
            TerrainRecord record;
            Assert.IsNull(TerrainPayload.Isolate(Payload(0, Rec(0, 8)), TerrainPayload.TagColor, out record));
        }

        [Test]
        public void Splice_ReplacesOneRecordAndLeavesEveryOtherByteAlone()
        {
            byte[] donor = Payload(4, Rec(0, 24), Rec(1, 16), Rec(3, 8));
            byte[] rebuilt = Payload(4, Rec(0, 24));
            // Make the rebuilt tag-0 body distinguishable from the donor's.
            for (int i = 0; i < 24; i++) rebuilt[TerrainPayload.HeaderLength + 5 + i] = 0x5A;

            int oldLength, newLength;
            byte[] spliced = TerrainPayload.Splice(donor, TerrainPayload.TagHeightfield, rebuilt, out oldLength, out newLength);

            Assert.AreEqual(24, oldLength);
            Assert.AreEqual(24, newLength);
            Assert.AreEqual(donor.Length, spliced.Length);

            int start = TerrainPayload.HeaderLength, end = start + 5 + 24;
            for (int i = 0; i < start; i++) Assert.AreEqual(donor[i], spliced[i]);
            for (int i = end; i < donor.Length; i++) Assert.AreEqual(donor[i], spliced[i]);
            for (int i = 0; i < 24; i++) Assert.AreEqual((byte)0x5A, spliced[start + 5 + i]);
        }

        [Test]
        public void Splice_RefusesARecordOfADifferentLength()
        {
            // A resized tag 0 would shift every later record and the streaming-node list with it.
            byte[] donor = Payload(0, Rec(0, 24), Rec(1, 16));
            byte[] rebuilt = Payload(0, Rec(0, 32));
            Assert.Throws<InvalidDataException>(delegate
            {
                int a, b;
                TerrainPayload.Splice(donor, TerrainPayload.TagHeightfield, rebuilt, out a, out b);
            });
        }

        [Test]
        public void U16RoundTripsThroughBytesLittleEndian()
        {
            var samples = new ushort[] { 0, 1, 0x1234, 0xFFFF, 0x00FF, 0xFF00 };
            byte[] bytes = TerrainPayload.FromU16(samples);
            Assert.AreEqual((byte)0x34, bytes[4]);
            Assert.AreEqual((byte)0x12, bytes[5]);
            ushort[] back = TerrainPayload.ToU16(bytes);
            Assert.AreEqual(samples.Length, back.Length);
            for (int i = 0; i < samples.Length; i++) Assert.AreEqual(samples[i], back[i]);
        }

        [Test]
        public void TagNamesCoverEveryRasterTree()
        {
            Assert.AreEqual("heightfield", TerrainPayload.TagName(0));
            Assert.AreEqual("mask", TerrainPayload.TagName(1));
            Assert.AreEqual("colour", TerrainPayload.TagName(2));
            Assert.AreEqual("material", TerrainPayload.TagName(3));
            Assert.AreEqual("destruction", TerrainPayload.TagName(4));   // tag 4 is the destruction-depth tree
        }

        [Test]
        public void HeightmapSource_MapsCornersAndTheHeightRange()
        {
            // 2x2 grid over 0..100 in both axes, spanning 10..20 m.
            var grid = new ushort[] { 0, 0, 65535, 65535 };   // row 0 low, row 1 high
            var source = new HeightmapSource(grid, 2, 0, 0, 100, 100, 10, 20);

            Assert.AreEqual(10.0, Math.Round(source.SampleMetres(0, 0), 6));
            Assert.AreEqual(20.0, Math.Round(source.SampleMetres(0, 100), 6));
            Assert.AreEqual(15.0, Math.Round(source.SampleMetres(50, 50), 6));
            Assert.AreEqual(10.0, source.MinMetres);
            Assert.AreEqual(20.0, source.MaxMetres);
        }

        [Test]
        public void HeightmapSource_ClampsOutsideItsWorldRectangle()
        {
            var grid = new ushort[] { 0, 0, 65535, 65535 };
            var source = new HeightmapSource(grid, 2, 0, 0, 100, 100, 10, 20);
            Assert.AreEqual(10.0, Math.Round(source.SampleMetres(-9999, -9999), 6));
            Assert.AreEqual(20.0, Math.Round(source.SampleMetres(9999, 9999), 6));
        }

        [Test]
        public void HeightmapSource_RejectsAGridThatIsNotSizeSquared()
        {
            Assert.Throws<ArgumentException>(delegate { new HeightmapSource(new ushort[3], 2, 0, 0, 1, 1, 0, 1); });
        }

        [Test]
        public void FlatHeightSource_IsTheSameHeightEverywhere()
        {
            var source = new FlatHeightSource(210);
            Assert.AreEqual(210.0, source.SampleMetres(0, 0));
            Assert.AreEqual(210.0, source.SampleMetres(-4096, 4096));
            Assert.AreEqual(210.0, source.MinMetres);
            Assert.AreEqual(210.0, source.MaxMetres);
        }

        [Test]
        public void Rewrite_RequiresAHeightSource()
        {
            Assert.Throws<ArgumentException>(delegate
            { TerrainHeightWriter.Rewrite(Payload(0, Rec(0, 8)), new TerrainRewriteOptions()); });
        }

        // -----------------------------------------------------------------------------------
        // Command argument handling
        // -----------------------------------------------------------------------------------

        [Test]
        public void Terrain_NoSubcommand_Returns1()
        {
            Assert.AreEqual(1, new TerrainCommand().Execute(new string[0], Logger(), Fmt()));
        }

        [Test]
        public void Terrain_UnknownSubcommand_Returns1()
        {
            Assert.AreEqual(1, new TerrainCommand().Execute(new[] { "resample" }, Logger(), Fmt()));
        }

        [Test]
        public void TerrainInfo_MissingTerrain_Returns1()
        {
            Assert.AreEqual(1, new TerrainCommand().Execute(new[] { "info" }, Logger(), Fmt()));
        }

        [Test]
        public void TerrainExportHeights_MissingOut_Returns1()
        {
            Assert.AreEqual(1, new TerrainCommand().Execute(new[] { "export-heights", "x" }, Logger(), Fmt()));
        }

        [Test]
        public void TerrainExportHeights_RejectsAnOutOfRangeSize()
        {
            Assert.AreEqual(1, new TerrainCommand().Execute(
                new[] { "export-heights", "x", "--out", "y.r16", "--size", "1" }, Logger(), Fmt()));
        }

        [Test]
        public void TerrainImportHeights_MissingFrom_Returns1()
        {
            Assert.AreEqual(1, new TerrainCommand().Execute(new[] { "import-heights", "x" }, Logger(), Fmt()));
        }

        [Test]
        public void TerrainImportHeights_MissingHeightmapFile_Returns1()
        {
            Assert.AreEqual(1, new TerrainCommand().Execute(
                new[] { "import-heights", "x", "--from", "no-such-file.r16" }, Logger(), Fmt()));
        }

        [Test]
        public void TerrainRetarget_MissingSource_Returns1()
        {
            Assert.AreEqual(1, new TerrainCommand().Execute(new[] { "retarget", "x" }, Logger(), Fmt()));
        }

        // A retarget whose occluder-grid layout is undetected used to keep the shipped grid. That grid still
        // described the donor's old ground; on Tatooine_01 retargeted to Desert_04 it floated up to 13 m above
        // the new dunes and hid props and terrain behind it. The floor never rises above the node's lowest
        // real sample, whatever the grid's level ordering.
        [Test]
        public void OccluderGridFloor_LowersEveryGridSampleToTheLowestHeightSample()
        {
            ushort[] samples = { 0, 900, 700, 1200, 0, 800 };           // 0 = hole marker, ignored
            byte[] grid = TerrainPayload.FromU16(new ushort[] { 1500, 650, 1400, 700, 2000 });

            bool lowered;
            ushort[] floored = TerrainPayload.ToU16(OccluderGridFloor.Apply(grid, samples, out lowered));

            Assert.AreEqual(5, floored.Length);
            foreach (ushort v in floored) Assert.AreEqual(700, (int)v);
            Assert.AreEqual(true, lowered);
        }

        [Test]
        public void OccluderGridFloor_AllHoleNodeFloorsToTheLowestValidSample()
        {
            ushort[] samples = { 0, 0, 0, 0 };
            byte[] grid = TerrainPayload.FromU16(new ushort[] { 300, 300 });

            bool lowered;
            ushort[] floored = TerrainPayload.ToU16(OccluderGridFloor.Apply(grid, samples, out lowered));

            Assert.AreEqual(2, floored.Length);
            foreach (ushort v in floored) Assert.AreEqual(1, (int)v);
            Assert.AreEqual(true, lowered);
        }
    }
}
