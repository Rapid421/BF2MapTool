using System;
using System.Collections.Generic;
using Bfii.TerrainFormat;

namespace FrostyCLI.Core
{
    // Read-only measurement of a terrain streaming-tree payload, for `terrain info` / `terrain
    // validate`. Frosty-free so it can be run against a payload from either game straight off disk.
    public sealed class TerrainSurvey
    {
        public int PayloadBytes;
        public bool FullParseOk;
        public string FullParseFailure;

        // Header (0x00-0x19). Field0x0D / Field0x11 are named UNKNOWN by the format contract; the
        // names below are what a 15-payload survey across both games measured them to be, and they
        // are reported as measurements rather than as format facts.
        public uint UnblurredSamplesPerNodeSidePot;
        public bool TrackTextureDetailFalloff;
        public float InvisibleDetailReductionFactor;
        public float OccludedDetailReductionFactor;
        public uint ResourceBlurriness;    // Field0x0D -- 0 in every payload measured so far
        public uint SkipLevels;            // Field0x11 -- 1 in SWBF 2015, 2 in every BFII terrain
        public uint StreamingNodeCount;
        public bool FreeStreamingEnable;

        public List<RecordInfo> Records = new List<RecordInfo>();

        // Heightfield (tag 0)
        public bool HasHeightfield;
        public uint NodeSamplesPerSide;
        public uint NodeBorderWidth;
        public int InteriorPot;
        public float WorldSizeY;
        public uint AtlasSampleCountX;
        public uint AtlasSampleCountY;
        public uint MinMaxStackDepth;
        public uint OccluderGridStackDepth;
        public uint HeightfieldNodeCount;
        public uint PersistentNodeCount;
        public uint SkippedNodeCount;
        public int NodesWithData;
        public int NodeDataSize;
        public double MinHeightMetres;
        public double MaxHeightMetres;
        public double WorldMinX, WorldMinZ, WorldMaxX, WorldMaxZ;
        public long ZeroSamples;
        public long TotalSamples;
        public List<LevelCensus> Levels = new List<LevelCensus>();

        // Atlas capacity: every MP terrain in both games ships zero chunk GUIDs, so nothing streams
        // and every persistent node must fit in the heightfield atlas at once. All 13 shipped BFII
        // terrains satisfy this; 2015's Volcanic_01 is 2.9x short, which is why it cannot be used
        // as a payload directly.
        public int AtlasColumns, AtlasRows, AtlasSlots;
        public bool AtlasFits;

        public sealed class RecordInfo
        {
            public int Tag;
            public string Name;
            public int Length;
            public bool Parsed;
            public string Failure;
        }

        public sealed class LevelCensus
        {
            public int Level;
            public int Nodes;
            public int WithData;
        }

        public static TerrainSurvey Measure(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            var s = new TerrainSurvey();
            s.PayloadBytes = payload.Length;

            int terminator;
            List<TerrainRecord> records = TerrainPayload.Records(payload, out terminator);

            // A full parse is attempted first because it is the strongest statement available, but
            // it currently fails on every shipped BFII material tree, so each record is then read on
            // its own. That per-record result is what tells a caller which tree is unreadable.
            ParseResult full = TerrainStreamingTreeParser.Parse(payload);
            s.FullParseOk = full.Success;
            s.FullParseFailure = full.Failure;

            foreach (TerrainRecord r in records)
            {
                var info = new RecordInfo { Tag = r.Tag, Name = TerrainPayload.TagName(r.Tag), Length = r.Length };
                try
                {
                    TerrainRecord ignored;
                    byte[] isolated = TerrainPayload.Isolate(payload, r.Tag, out ignored);
                    ParseResult one = TerrainStreamingTreeParser.Parse(isolated);
                    info.Parsed = one.Success;
                    info.Failure = one.Failure;
                }
                catch (Exception ex) { info.Parsed = false; info.Failure = ex.Message; }
                s.Records.Add(info);
            }

            TerrainRecord heightfieldRecord;
            byte[] hfPayload = TerrainPayload.Isolate(payload, TerrainPayload.TagHeightfield, out heightfieldRecord);
            ParseResult hf = hfPayload != null ? TerrainStreamingTreeParser.Parse(hfPayload) : null;
            TerrainStreamingTreeResource resource = hf != null ? hf.Resource : full.Resource;
            if (resource == null) return s;

            StreamingTreeHeader h = resource.Header;
            s.UnblurredSamplesPerNodeSidePot = h.UnblurredSamplesPerNodeSidePot;
            s.TrackTextureDetailFalloff = h.TrackTextureDetailFalloff;
            s.InvisibleDetailReductionFactor = h.InvisibleDetailReductionFactor.Value;
            s.OccludedDetailReductionFactor = h.OccludedDetailReductionFactor.Value;
            s.ResourceBlurriness = h.Field0x0D;
            s.SkipLevels = h.Field0x11;
            s.StreamingNodeCount = h.NodeCount;
            s.FreeStreamingEnable = h.FreeStreamingEnable;

            HeightfieldTree tree = resource.Heightfield;
            if (tree == null) return s;

            s.HasHeightfield = true;
            s.NodeSamplesPerSide = tree.NodeSamplesPerSide;
            s.NodeBorderWidth = tree.NodeBorderWidth;
            s.InteriorPot = (int)tree.NodeSamplesPerSide - 2 * (int)tree.NodeBorderWidth - 1;
            s.WorldSizeY = tree.WorldSizeY.Value;
            s.AtlasSampleCountX = tree.ResourceAtlasSampleCountX;
            s.AtlasSampleCountY = tree.ResourceAtlasSampleCountY;
            s.MinMaxStackDepth = tree.MinMaxStackDepth;
            s.OccluderGridStackDepth = tree.OccluderGridStackDepth;
            s.HeightfieldNodeCount = tree.NodeCount;
            s.PersistentNodeCount = tree.PersistentNodeCount;
            s.SkippedNodeCount = tree.SkippedNodeCount;
            s.NodeDataSize = tree.NodeDataSize;

            if (tree.Root != null)
            {
                AxisAlignedBox b = tree.Root.BoundingBox;
                s.WorldMinX = b.MinX.Value; s.WorldMaxX = b.MaxX.Value;
                s.WorldMinZ = b.MinZ.Value; s.WorldMaxZ = b.MaxZ.Value;
            }

            var byLevel = new Dictionary<int, LevelCensus>();
            ushort lo = ushort.MaxValue, hi = 0;
            foreach (HeightfieldNode n in tree.EnumerateNodes())
            {
                LevelCensus census;
                int level = n.Id.Level;
                if (!byLevel.TryGetValue(level, out census))
                { census = new LevelCensus { Level = level }; byLevel[level] = census; }
                census.Nodes++;
                if (n.Payload == null) continue;
                census.WithData++;
                s.NodesWithData++;

                foreach (ushort v in TerrainPayload.ToU16(n.Payload.AtlasData))
                {
                    s.TotalSamples++;
                    if (v == 0) s.ZeroSamples++;
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
            }
            if (lo == ushort.MaxValue) lo = 0;
            s.MinHeightMetres = lo * s.WorldSizeY / 65535.0;
            s.MaxHeightMetres = hi * s.WorldSizeY / 65535.0;

            var levels = new List<LevelCensus>(byLevel.Values);
            levels.Sort(delegate (LevelCensus a, LevelCensus b) { return a.Level.CompareTo(b.Level); });
            s.Levels = levels;

            int side = (int)tree.NodeSamplesPerSide;
            if (side > 0)
            {
                s.AtlasColumns = (int)(tree.ResourceAtlasSampleCountX / side);
                s.AtlasRows = (int)(tree.ResourceAtlasSampleCountY / side);
                s.AtlasSlots = s.AtlasColumns * s.AtlasRows;
                s.AtlasFits = s.AtlasSlots >= tree.PersistentNodeCount;
            }
            return s;
        }

        // Structural checks worth failing a build over. Each one is a measured invariant, not a
        // preference; the message says what was measured.
        public List<string> Problems()
        {
            var problems = new List<string>();
            if (!HasHeightfield) { problems.Add("no heightfield (tag 0) record"); return problems; }

            if (!AtlasFits)
                problems.Add("heightfield atlas holds " + AtlasSlots + " node slot(s) (" + AtlasColumns + "x" +
                    AtlasRows + ") but " + PersistentNodeCount + " node(s) are persistent; nothing streams in a " +
                    "terrain with no chunk GUIDs, so every persistent node must fit at once");

            if (InteriorPot <= 0 || (InteriorPot & (InteriorPot - 1)) != 0)
                problems.Add("tile interior is " + InteriorPot + " samples (" + NodeSamplesPerSide + " - 2*" +
                    NodeBorderWidth + " - 1); every shipped terrain in both games is a power of two");

            foreach (RecordInfo r in Records)
                if (!r.Parsed && r.Tag != TerrainPayload.TagMaterial)
                    problems.Add("tag " + r.Tag + " (" + r.Name + ") did not parse: " + (r.Failure ?? "unknown"));

            if (WorldMaxX <= WorldMinX || WorldMaxZ <= WorldMinZ)
                problems.Add("root bounding box has no XZ extent");

            return problems;
        }

        // Things worth saying that are not faults in this payload.
        public List<string> Notes()
        {
            var notes = new List<string>();

            // The material tree does not parse on any shipped BFII terrain -- a known gap in the
            // format's material-RLE sizing, not damage. Editing elevations never reads or rewrites
            // it, so it does not block anything this command does; it is reported so that an
            // unreadable tag 3 is never mistaken for a new problem.
            foreach (RecordInfo r in Records)
                if (!r.Parsed && r.Tag == TerrainPayload.TagMaterial)
                    notes.Add("the material tree (tag 3) does not parse: " + (r.Failure ?? "unknown") +
                              ". Every shipped BFII terrain reads this way; elevation edits do not touch it.");

            if (HasHeightfield && PersistentNodeCount == 0)
                notes.Add("no node is marked persistent, so this terrain has nothing resident to overwrite; " +
                          "it is not usable as a donor");

            if (HasHeightfield && NodesWithData == 0)
                notes.Add("no node carries sample data");

            return notes;
        }

        // Differences that matter when this payload is used as a donor for, or a source of, a port.
        public List<string> CompareTo(TerrainSurvey other, string mine, string theirs)
        {
            var notes = new List<string>();
            if (!HasHeightfield || !other.HasHeightfield) return notes;

            if (NodeSamplesPerSide != other.NodeSamplesPerSide || NodeBorderWidth != other.NodeBorderWidth)
                notes.Add("tile geometry differs: " + mine + " " + NodeSamplesPerSide + "/border" + NodeBorderWidth +
                          " vs " + theirs + " " + other.NodeSamplesPerSide + "/border" + other.NodeBorderWidth +
                          " (harmless when resampling; fatal when splicing)");
            if (SkipLevels != other.SkipLevels)
                notes.Add("skipLevels differs: " + mine + " " + SkipLevels + " vs " + theirs + " " + other.SkipLevels +
                          " (BFII ships 2 and leaves its top two levels data-free; SWBF 2015 ships 1)");
            if (Math.Abs(WorldSizeY - other.WorldSizeY) > 0.5f)
                notes.Add("worldSizeY differs: " + mine + " " + WorldSizeY + " m vs " + theirs + " " + other.WorldSizeY +
                          " m (sample 65535 means a different height in each)");
            if (NodesWithData != other.NodesWithData)
                notes.Add("resolution differs: " + mine + " has " + NodesWithData + " node(s) with data vs " +
                          theirs + "'s " + other.NodesWithData + "; the port lands at the donor's resolution");
            return notes;
        }
    }
}
