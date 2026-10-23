using System;
using System.Collections.Generic;
using System.IO;
using Bfii.TerrainDerive;
using Bfii.TerrainFormat;

namespace FrostyCLI.Core
{
    // Terrain streaming-tree editing, on top of the vendored Bfii.TerrainFormat / Bfii.TerrainDerive
    // parsers in Terrain\. Nothing here touches Frosty: the commands hand these functions payload
    // bytes and get payload bytes back.
    //
    // A terrain payload is a 26-byte header, then [u8 tag][u32 LE length][payload] records terminated
    // by 0xFF, then the streaming-node list. Tag 0 is the heightfield (elevations + derived arrays),
    // 1 the mask, 2 colour, 3 material, 4 the layer/decal tree.
    //
    // The editing model is deliberately narrow, because the alternatives were measured and do not
    // work (projects\terrain-port\EXPERIMENT-LOG.md, 2026-09-06, 18 in-game builds):
    //
    //   * Splicing a foreign heightfield tree into a donor payload never renders. loadNodes walks
    //     the streaming tree and the raster trees in lockstep asserting rasterTreeNode->m_id ==
    //     newNode->getId(), so a donor's streaming tree cannot index a heightfield with different
    //     topology. T3, T5 and T13 all failed this way.
    //   * Converting a whole 2015 payload to BFII's structure loads only if the material tree is
    //     replaced by a BFII donor's (2015's tag 3 crashes BFII outright, and tag 3 cannot be
    //     dropped) -- and even then renders nothing.
    //
    // What does work, and is what this file implements: keep a BFII donor's payload byte for byte
    // and overwrite only the u16 elevation samples, the min/max and occluder blocks they drive, and
    // each node's bounding-box Y. Tag 0's length is unchanged, so every other byte in the resource
    // is preserved exactly.
    public sealed class TerrainRecord
    {
        public int Tag;
        public int Start;   // offset of the [tag][len] header
        public int End;     // offset one past the record body
        public int Length;  // declared body length, excluding the 5-byte header

        public TerrainRecord(int tag, int start, int end, int length)
        { Tag = tag; Start = start; End = end; Length = length; }
    }

    public static class TerrainPayload
    {
        public const int HeaderLength = 0x1A;   // StreamingTreeHeader.SizeBytes
        public const byte Terminator = 0xFF;

        public const int TagHeightfield = 0;
        public const int TagMask        = 1;
        public const int TagColor       = 2;
        public const int TagMaterial    = 3;
        public const int TagLayer       = 4;

        public static string TagName(int tag)
        {
            switch (tag)
            {
                case TagHeightfield: return "heightfield";
                case TagMask:        return "mask";
                case TagColor:       return "colour";
                case TagMaterial:    return "material";
                case TagLayer:       return "destruction";   // tag 4: the destruction-depth tree (SPEC §6)
                default:             return "tag" + tag;
            }
        }

        // Walks the record list. `terminator` receives the offset of the 0xFF byte; everything after
        // it is the streaming-node list.
        public static List<TerrainRecord> Records(byte[] data, out int terminator)
        {
            if (data == null) throw new ArgumentNullException("data");
            var records = new List<TerrainRecord>();
            int offset = HeaderLength;
            while (offset < data.Length)
            {
                if (data[offset] == Terminator) { terminator = offset; return records; }
                if (offset + 5 > data.Length)
                    throw new InvalidDataException("truncated record header at offset " + offset);
                int length = BitConverter.ToInt32(data, offset + 1);
                if (length < 0 || offset + 5 + (long)length > data.Length)
                    throw new InvalidDataException("record at offset " + offset + " declares " + length +
                        " bytes, past the end of a " + data.Length + "-byte payload");
                records.Add(new TerrainRecord(data[offset], offset, offset + 5 + length, length));
                offset += 5 + length;
            }
            throw new InvalidDataException("no 0xFF record terminator in a " + data.Length + "-byte payload");
        }

        public static TerrainRecord Find(List<TerrainRecord> records, int tag)
        {
            foreach (TerrainRecord r in records) if (r.Tag == tag) return r;
            return null;
        }

        // Builds a payload carrying exactly one raster-tree record, so a parse never reaches the
        // others. Needed because a full parse still fails on every shipped BFII material tree
        // (material-RLE sizing), which would otherwise make the heightfield unreadable too.
        // The header and the streaming-node list are kept, so node ids still resolve.
        public static byte[] Isolate(byte[] data, int tag, out TerrainRecord record)
        {
            int terminator;
            List<TerrainRecord> records = Records(data, out terminator);
            record = Find(records, tag);
            if (record == null) return null;

            var ms = new MemoryStream();
            ms.Write(data, 0, HeaderLength);
            ms.Write(data, record.Start, record.End - record.Start);
            ms.WriteByte(Terminator);
            ms.Write(data, terminator + 1, data.Length - terminator - 1);
            return ms.ToArray();
        }

        // Replaces one record's bytes in `donor` with the same-tagged record out of `rebuilt`.
        // Refuses a length change: a different tag-0 length would shift every following record and
        // the streaming-node list with it, and nothing in the recipe should ever resize the record.
        public static byte[] Splice(byte[] donor, int tag, byte[] rebuilt, out int oldLength, out int newLength)
        {
            int donorTerm, rebuiltTerm;
            TerrainRecord target = Find(Records(donor, out donorTerm), tag);
            TerrainRecord source = Find(Records(rebuilt, out rebuiltTerm), tag);
            if (target == null) throw new InvalidDataException("donor payload has no tag-" + tag + " record");
            if (source == null) throw new InvalidDataException("rebuilt payload has no tag-" + tag + " record");

            oldLength = target.Length;
            newLength = source.Length;
            if (oldLength != newLength)
                throw new InvalidDataException("rebuilt tag-" + tag + " record is " + newLength +
                    " bytes against the donor's " + oldLength + "; an in-place splice needs them equal");

            var ms = new MemoryStream(donor.Length);
            ms.Write(donor, 0, target.Start);
            ms.Write(rebuilt, source.Start, source.End - source.Start);
            ms.Write(donor, target.End, donor.Length - target.End);
            return ms.ToArray();
        }

        // Rebuilds `target` with the records of the given tags taken from `donor`; record lengths may change. Records are
        // length-framed and the streaming-node list that follows the terminator carries no offsets into them (all zero
        // apart from hasChildren on every shipped MP terrain), so nothing else needs rewriting.
        public static byte[] Graft(byte[] target, byte[] donor, int[] tags)
        {
            int targetTerminator, donorTerminator;
            List<TerrainRecord> targetRecords = Records(target, out targetTerminator);
            List<TerrainRecord> donorRecords = Records(donor, out donorTerminator);
            var ms = new MemoryStream(target.Length);
            ms.Write(target, 0, HeaderLength);
            foreach (TerrainRecord r in targetRecords)
            {
                if (Array.IndexOf(tags, r.Tag) < 0) { ms.Write(target, r.Start, r.End - r.Start); continue; }
                TerrainRecord from = Find(donorRecords, r.Tag);
                if (from == null) throw new InvalidDataException("graft donor has no tag-" + r.Tag + " record");
                ms.Write(donor, from.Start, from.End - from.Start);
            }
            ms.WriteByte(Terminator);
            ms.Write(target, targetTerminator + 1, target.Length - targetTerminator - 1);
            return ms.ToArray();
        }

        public static ushort[] ToU16(byte[] bytes)
        {
            var o = new ushort[bytes.Length / 2];
            for (int i = 0; i < o.Length; i++) o[i] = (ushort)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
            return o;
        }

        public static byte[] FromU16(ushort[] samples)
        {
            var o = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            { o[2 * i] = (byte)(samples[i] & 0xFF); o[2 * i + 1] = (byte)(samples[i] >> 8); }
            return o;
        }
    }

    // A source of ground height in world metres, sampled at a world XZ in the *donor's* space.
    public interface ITerrainHeightSource
    {
        double SampleMetres(double x, double z);
        // True when the source marks this point as a terrain hole (height sample 0).
        bool IsHole(double x, double z);
        // Bounds (source XZ) of every hole marker; false when the source has none.
        bool HoleBounds(out double minX, out double minZ, out double maxX, out double maxZ);
        double MinMetres { get; }
        double MaxMetres { get; }
        string Describe();
    }

    // Samples another terrain's heightfield tree. World XZ is taken as-is, so a source covering a
    // different world extent than the donor is stretched to fit rather than cropped.
    public sealed class TerrainHeightSource : ITerrainHeightSource
    {
        private readonly HeightfieldTree _tree;
        private readonly int _side, _border, _interior;
        private readonly double _worldY;
        private readonly Dictionary<HeightfieldNode, ushort[]> _cache = new Dictionary<HeightfieldNode, ushort[]>();
        private readonly double _min, _max;
        private readonly int _nodes;

        public TerrainHeightSource(HeightfieldTree tree)
        {
            if (tree == null) throw new ArgumentNullException("tree");
            _tree = tree;
            _side = (int)tree.NodeSamplesPerSide;
            _border = (int)tree.NodeBorderWidth;
            _interior = _side - 2 * _border - 1;
            _worldY = tree.WorldSizeY.Value;

            ushort lo = ushort.MaxValue, hi = 0;
            foreach (HeightfieldNode n in tree.EnumerateNodes())
            {
                if (n.Payload == null) continue;
                _nodes++;
                foreach (ushort v in TerrainPayload.ToU16(n.Payload.AtlasData))
                { if (v < lo) lo = v; if (v > hi) hi = v; }
            }
            if (lo == ushort.MaxValue) lo = 0;
            _min = lo * _worldY / 65535.0;
            _max = hi * _worldY / 65535.0;
        }

        public double MinMetres { get { return _min; } }
        public double MaxMetres { get { return _max; } }
        public int NodesWithData { get { return _nodes; } }

        public string Describe()
        {
            return _side + "/border" + _border + " worldY=" + _worldY.ToString("0.##") +
                   " nodesWithData=" + _nodes;
        }

        // Deepest node whose bounding box contains the point and that carries samples. Parents are
        // remembered on the way down so a point inside a childless region still resolves.
        private HeightfieldNode Deepest(double x, double z)
        {
            HeightfieldNode current = _tree.Root, best = null;
            while (current != null)
            {
                if (current.Payload != null) best = current;
                HeightfieldNode next = null;
                foreach (HeightfieldNode c in current.Children)
                {
                    AxisAlignedBox b = c.BoundingBox;
                    if (x >= b.MinX.Value && x <= b.MaxX.Value && z >= b.MinZ.Value && z <= b.MaxZ.Value)
                    { next = c; break; }
                }
                current = next;
            }
            return best;
        }

        private ushort[] Samples(HeightfieldNode n)
        {
            ushort[] s;
            if (!_cache.TryGetValue(n, out s))
            { s = TerrainPayload.ToU16(n.Payload.AtlasData); _cache[n] = s; }
            return s;
        }

        public double SampleMetres(double x, double z)
        {
            double h;
            return TrySampleMetres(x, z, out h) ? h : _min;
        }

        /// <summary>
        /// Height at (x, z), or false where no ground sample surrounds the point (inside a hole, whose samples are the
        /// 0 hole marker, or where no node carries data): SampleMetres then answers the tree's minimum, which inside
        /// Hoth's base-entrance holes is 0 m -- a spike 350 m down for anything that meshes the hole rim.
        /// </summary>
        public bool TrySampleMetres(double x, double z, out double metres)
        {
            metres = _min;
            HeightfieldNode n = Deepest(x, z);
            if (n == null || n.Payload == null) return false;

            AxisAlignedBox b = n.BoundingBox;
            double u = (x - b.MinX.Value) / Math.Max(1e-6, b.MaxX.Value - b.MinX.Value);
            double v = (z - b.MinZ.Value) / Math.Max(1e-6, b.MaxZ.Value - b.MinZ.Value);
            if (u < 0) u = 0; else if (u > 1) u = 1;
            if (v < 0) v = 0; else if (v > 1) v = 1;

            // The bounding box spans the node's interior, so sample space starts at the border.
            double fx = _border + u * _interior, fz = _border + v * _interior;
            int x0 = Clamp((int)Math.Floor(fx), 0, _side - 1);
            int z0 = Clamp((int)Math.Floor(fz), 0, _side - 1);
            int x1 = Math.Min(x0 + 1, _side - 1), z1 = Math.Min(z0 + 1, _side - 1);
            double tx = fx - x0, tz = fz - z0;

            ushort[] s = Samples(n);
            // Sample 0 is a hole marker, not ground at 0 m: blend only the corners that are ground, so the
            // terrain around a hole keeps its height instead of sloping down into a pit.
            ushort c00 = s[z0 * _side + x0], c10 = s[z0 * _side + x1], c01 = s[z1 * _side + x0], c11 = s[z1 * _side + x1];
            double w00 = c00 != 0 ? (1 - tx) * (1 - tz) : 0, w10 = c10 != 0 ? tx * (1 - tz) : 0;
            double w01 = c01 != 0 ? (1 - tx) * tz : 0, w11 = c11 != 0 ? tx * tz : 0;
            double w = w00 + w10 + w01 + w11;
            if (w <= 1e-9) return false;
            double h = (c00 * w00 + c10 * w10 + c01 * w01 + c11 * w11) / w;
            metres = h * _worldY / 65535.0;
            return true;
        }

        private double[] _holeBounds;
        private bool _holeBoundsDone;

        public bool HoleBounds(out double minX, out double minZ, out double maxX, out double maxZ)
        {
            if (!_holeBoundsDone)
            {
                _holeBoundsDone = true;
                double x0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, z1 = double.MinValue;
                foreach (HeightfieldNode n in _tree.EnumerateNodes())
                {
                    if (n.Payload == null) continue;
                    ushort[] s = Samples(n);
                    AxisAlignedBox b = n.BoundingBox;
                    double dx = (b.MaxX.Value - b.MinX.Value) / _interior, dz = (b.MaxZ.Value - b.MinZ.Value) / _interior;
                    for (int j = 0; j < _side; j++)
                        for (int i = 0; i < _side; i++)
                        {
                            if (s[j * _side + i] != 0) continue;
                            double x = b.MinX.Value + (i - _border) * dx, z = b.MinZ.Value + (j - _border) * dz;
                            if (x < x0) x0 = x;
                            if (x > x1) x1 = x;
                            if (z < z0) z0 = z;
                            if (z > z1) z1 = z;
                        }
                }
                if (x0 != double.MaxValue) _holeBounds = new[] { x0, z0, x1, z1 };
            }
            if (_holeBounds == null) { minX = minZ = maxX = maxZ = 0; return false; }
            minX = _holeBounds[0]; minZ = _holeBounds[1]; maxX = _holeBounds[2]; maxZ = _holeBounds[3];
            return true;
        }

        public bool IsHole(double x, double z)
        {
            HeightfieldNode n = Deepest(x, z);
            if (n == null || n.Payload == null) return false;
            AxisAlignedBox b = n.BoundingBox;
            double u = (x - b.MinX.Value) / Math.Max(1e-6, b.MaxX.Value - b.MinX.Value);
            double v = (z - b.MinZ.Value) / Math.Max(1e-6, b.MaxZ.Value - b.MinZ.Value);
            if (u < 0) u = 0; else if (u > 1) u = 1;
            if (v < 0) v = 0; else if (v > 1) v = 1;
            int xi = Clamp((int)Math.Round(_border + u * _interior), 0, _side - 1);
            int zi = Clamp((int)Math.Round(_border + v * _interior), 0, _side - 1);
            return Samples(n)[zi * _side + xi] == 0;
        }

        internal static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // A regular 16-bit grid over a known world rectangle -- what `terrain export-heights` writes and
    // `terrain import-heights` reads, so a heightmap can be edited in any terrain tool.
    public sealed class HeightmapSource : ITerrainHeightSource
    {
        private readonly ushort[] _grid;
        private readonly int _size;
        private readonly double _x0, _z0, _x1, _z1, _lo, _hi;

        public HeightmapSource(ushort[] grid, int size, double minX, double minZ, double maxX, double maxZ,
                               double loMetres, double hiMetres)
        {
            if (grid == null || grid.Length != size * size)
                throw new ArgumentException("heightmap must hold size*size samples");
            _grid = grid; _size = size;
            _x0 = minX; _z0 = minZ; _x1 = maxX; _z1 = maxZ;
            _lo = loMetres; _hi = hiMetres;
        }

        public double MinMetres { get { return _lo; } }
        public double MaxMetres { get { return _hi; } }
        public string Describe() { return _size + "x" + _size + " grid, " + _lo.ToString("0.#") + ".." + _hi.ToString("0.#") + " m"; }
        // A band-relative grid has no hole marker: its 0 is simply the lowest ground.
        public bool IsHole(double x, double z) { return false; }
        public bool HoleBounds(out double minX, out double minZ, out double maxX, out double maxZ) { minX = minZ = maxX = maxZ = 0; return false; }

        public double SampleMetres(double x, double z)
        {
            double u = (x - _x0) / Math.Max(1e-6, _x1 - _x0);
            double v = (z - _z0) / Math.Max(1e-6, _z1 - _z0);
            if (u < 0) u = 0; else if (u > 1) u = 1;
            if (v < 0) v = 0; else if (v > 1) v = 1;
            double fx = u * (_size - 1), fz = v * (_size - 1);
            int x0 = TerrainHeightSource.Clamp((int)Math.Floor(fx), 0, _size - 1);
            int z0 = TerrainHeightSource.Clamp((int)Math.Floor(fz), 0, _size - 1);
            int x1 = Math.Min(x0 + 1, _size - 1), z1 = Math.Min(z0 + 1, _size - 1);
            double tx = fx - x0, tz = fz - z0;
            double n = _grid[z0 * _size + x0] * (1 - tx) * (1 - tz)
                     + _grid[z0 * _size + x1] * tx * (1 - tz)
                     + _grid[z1 * _size + x0] * (1 - tx) * tz
                     + _grid[z1 * _size + x1] * tx * tz;
            return _lo + (n / 65535.0) * (_hi - _lo);
        }
    }

    // Every sample the same height. Diagnostic: it answers "is anything about this terrain rendering
    // at all" without any resampling in the way.
    public sealed class FlatHeightSource : ITerrainHeightSource
    {
        private readonly double _m;
        public FlatHeightSource(double metres) { _m = metres; }
        public double SampleMetres(double x, double z) { return _m; }
        public bool IsHole(double x, double z) { return false; }
        public bool HoleBounds(out double minX, out double minZ, out double maxX, out double maxZ) { minX = minZ = maxX = maxZ = 0; return false; }
        public double MinMetres { get { return _m; } }
        public double MaxMetres { get { return _m; } }
        public string Describe() { return "flat " + _m.ToString("0.##") + " m"; }
    }

    public sealed class TerrainRewriteOptions
    {
        public ITerrainHeightSource Source;

        // Remap the source's own height band into these host metres. This is what makes a ported
        // landform land where the host level's spawns and props are: Sullust spans 0-1024 m and
        // Endor's playable ground is a flat ~199.75 m, so an unremapped port renders correctly but
        // hundreds of metres above the player, which is indistinguishable from not rendering.
        public double? BandMin;
        public double? BandMax;

        // Translation applied to the source landform, in host metres: the source is sampled at
        // (x - OffsetX, z - OffsetZ) and OffsetY is added after any band remap. Matches the offset
        // a layer's props were placed with, so terrain and props stay in one frame.
        public double OffsetX, OffsetY, OffsetZ;

        // Leave min/max, occluder and bounding boxes exactly as shipped. Diagnostic only: shipped
        // bounding boxes do not enclose rewritten samples, so the cull job discards the nodes.
        public bool KeepDerived;

        // Write the source's holes as ground (the old behaviour) instead of as holes.
        public bool FillHoles;

        // Graft the mask, colour, material and destruction records from this payload (normally the host's own shipped
        // terrain), so mask layer indices agree with the host's TerrainData and its baked layer combinations.
        public byte[] MaskDonor;

        // Paint this mask layer as the renderer's hole layer wherever the source has holes; -1 leaves the mask alone.
        public int HoleLayer = -1;

        // Grow painted holes by this many metres so the steep triangles next to a hole marker are cut away too.
        public double HoleDilate = 1.0;
    }

    public sealed class TerrainRewriteResult
    {
        public byte[] Payload;
        public int NodesRewritten;
        public long SamplesChanged;
        public long SamplesTotal;
        public int BoundingBoxesRecomputed;
        public int RecordLength;
        public string MinMaxLayout = "(not rebuilt)";
        public string OccluderLayout = "(not rebuilt)";
        public int OccluderGridsFloored;
        public double OutMinMetres;
        public double OutMaxMetres;
        public int HoleNodes;
        public int HoleFlagsChanged;
        public string MaskReport;
    }

    // Paints the renderer's terrain hole layer into the mask tree, in place. Sample 0 only makes Havok see a hole; the
    // renderer cuts holes from the mask of the non-physical terrain layer (TerrainTextureGenerator holesLayerIndex).
    // Measured: the hole layer is the last terrain layer (Endor_01 and SWBF 2015 Volcanic_01: 10, Hoth_01: 8), weight
    // ~180-230 at hole samples against ~2 on ground. Nothing is added or removed, so the record keeps its length: an
    // existing hole-layer subtile over a spot is repainted, otherwise the faintest tiled subtile of that coverage box
    // (never layer 0) is repurposed. Each painted box's layer set is reported; it must be contained in one of the host's
    // baked layer combinations or that ground silently falls back to the default terrain shader.
    public static class TerrainHolePainter
    {
        public static byte[] Paint(byte[] payload, ITerrainHeightSource source, double offsetX, double offsetZ, int holeLayer,
                                   double dilate, TerrainRewriteResult result)
        {
            TerrainRecord record;
            byte[] isolated = TerrainPayload.Isolate(payload, TerrainPayload.TagMask, out record);
            if (isolated == null) { result.MaskReport = "no mask record; holes not painted"; return payload; }
            ParseResult parsed = TerrainStreamingTreeParser.Parse(isolated);
            MaskTree mask = parsed.Resource != null ? parsed.Resource.Mask : null;
            if (mask == null) { result.MaskReport = "mask did not parse (" + parsed.Failure + "); holes not painted"; return payload; }

            int tsp = (int)mask.TileSamplesPerSide;
            int pot = 1;
            while (pot * 2 <= tsp) pot *= 2;
            int border = (tsp - pot) / 2;
            double hx0, hz0, hx1, hz1;
            bool haveHoles = source.HoleBounds(out hx0, out hz0, out hx1, out hz1);
            double pad = dilate + 2.0;
            hx0 += offsetX - pad; hx1 += offsetX + pad; hz0 += offsetZ - pad; hz1 += offsetZ + pad;

            int cleared = 0, painted = 0, repurposed = 0, unsortedLeft = 0, noTile = 0;
            var paintedSubtiles = new List<KeyValuePair<int, MaskSubtile>>();   // node level, subtile
            var sets = new SortedSet<string>(StringComparer.Ordinal);
            foreach (MaskNode node in mask.EnumerateNodes())
            {
                if (!node.Persistent || node.Tiles.Count == 0) continue;
                var tileOf = new Dictionary<MaskSubtile, byte[]>();
                int k = 0;
                foreach (MaskSubtile s in node.Subtiles)
                    if ((s.Flags & 2) == 0 && k < node.Tiles.Count) tileOf[s] = node.Tiles[k++];
                bool wasSorted = true;
                for (int i = 1; i < node.Subtiles.Count; i++)
                    if (node.Subtiles[i].TerrainLayerIndex < node.Subtiles[i - 1].TerrainLayerIndex) { wasSorted = false; break; }

                // The donor's own holes belong to its own map.
                foreach (MaskSubtile s in node.Subtiles)
                {
                    byte[] t;
                    if (s.TerrainLayerIndex != holeLayer || !tileOf.TryGetValue(s, out t)) continue;
                    bool any = false;
                    for (int i = 0; i < t.Length; i++) if (t[i] != 0) { t[i] = 0; any = true; }
                    if (any) cleared++;
                }
                if (!haveHoles) continue;

                var boxes = new Dictionary<string, List<MaskSubtile>>(StringComparer.Ordinal);
                foreach (MaskSubtile s in node.Subtiles)
                {
                    AxisAlignedBox2 c = s.WorldCoverage;
                    if (c.MaxX.Value < hx0 || c.MinX.Value > hx1 || c.MaxY.Value < hz0 || c.MinY.Value > hz1) continue;
                    string key = c.MinX.Value.ToString("R") + "," + c.MinY.Value.ToString("R") + "," + c.MaxX.Value.ToString("R") + "," + c.MaxY.Value.ToString("R");
                    List<MaskSubtile> list;
                    if (!boxes.TryGetValue(key, out list)) { list = new List<MaskSubtile>(); boxes[key] = list; }
                    list.Add(s);
                }

                bool moved = false;
                foreach (List<MaskSubtile> box in boxes.Values)
                {
                    byte[] weights = HoleTile(source, offsetX, offsetZ, box[0].WorldCoverage, tsp, pot, border, dilate);
                    if (weights == null) continue;

                    MaskSubtile target = null;
                    foreach (MaskSubtile s in box)
                        if (s.TerrainLayerIndex == holeLayer && tileOf.ContainsKey(s)) { target = s; break; }
                    if (target == null)
                    {
                        long best = long.MaxValue;
                        foreach (MaskSubtile s in box)
                        {
                            byte[] t;
                            if (s.TerrainLayerIndex == 0 || s.TerrainLayerIndex == holeLayer || !tileOf.TryGetValue(s, out t)) continue;
                            long sum = 0;
                            foreach (byte b in t) sum += b;
                            if (sum < best) { best = sum; target = s; }
                        }
                        if (target == null) { noTile++; continue; }
                        target.TerrainLayerIndex = (byte)holeLayer;
                        repurposed++;
                        moved = true;
                    }
                    byte[] tile = tileOf[target];
                    for (int i = 0; i < tile.Length && i < weights.Length; i++) tile[i] = weights[i];
                    SetHoleCoverage(target.HighResolutionCoverageGridStack, tile, mask.FittedStrideLevel, tsp, pot, border);
                    painted++;
                    paintedSubtiles.Add(new KeyValuePair<int, MaskSubtile>(node.Id.Level, target));
                    var layers = new SortedSet<int>();
                    foreach (MaskSubtile s in box) layers.Add(s.TerrainLayerIndex);
                    sets.Add("{" + string.Join(",", layers) + "}");
                }

                if (moved)
                {
                    if (!wasSorted) { unsortedLeft++; continue; }
                    var order = new List<KeyValuePair<int, MaskSubtile>>();
                    for (int i = 0; i < node.Subtiles.Count; i++) order.Add(new KeyValuePair<int, MaskSubtile>(i, node.Subtiles[i]));
                    order.Sort(delegate (KeyValuePair<int, MaskSubtile> a, KeyValuePair<int, MaskSubtile> b)
                    {
                        int c = a.Value.TerrainLayerIndex.CompareTo(b.Value.TerrainLayerIndex);
                        return c != 0 ? c : a.Key.CompareTo(b.Key);
                    });
                    node.Subtiles.Clear();
                    foreach (KeyValuePair<int, MaskSubtile> kv in order) node.Subtiles.Add(kv.Value);
                    node.Tiles.Clear();
                    foreach (MaskSubtile s in node.Subtiles) { byte[] t; if (tileOf.TryGetValue(s, out t)) node.Tiles.Add(t); }
                }
            }

            // Leaf flags: keep a painted hole-layer subtile pointing at finer data only when every quadrant of its box that
            // holds a hole has a tiled hole-layer subtile one level down; otherwise the renderer would descend into a level
            // with no hole data there and draw the ground up close.
            var holeBoxes = new HashSet<string>(StringComparer.Ordinal);
            foreach (MaskNode node in mask.EnumerateNodes())
                foreach (MaskSubtile s in node.Subtiles)
                    if (s.TerrainLayerIndex == holeLayer && (s.Flags & 2) == 0)
                        holeBoxes.Add(BoxKey(node.Id.Level, s.WorldCoverage.MinX.Value, s.WorldCoverage.MinY.Value,
                                             s.WorldCoverage.MaxX.Value, s.WorldCoverage.MaxY.Value));
            int leafSet = 0, leafCleared = 0;
            foreach (KeyValuePair<int, MaskSubtile> p in paintedSubtiles)
            {
                MaskSubtile s = p.Value;
                byte[] tile = null;
                foreach (MaskNode node in mask.EnumerateNodes())
                {
                    if (node.Id.Level != p.Key) continue;
                    int k = 0;
                    foreach (MaskSubtile other in node.Subtiles)
                    {
                        if ((other.Flags & 2) != 0) continue;
                        if (ReferenceEquals(other, s)) { tile = k < node.Tiles.Count ? node.Tiles[k] : null; break; }
                        k++;
                    }
                    if (tile != null) break;
                }
                if (tile == null) continue;
                double x0 = s.WorldCoverage.MinX.Value, z0 = s.WorldCoverage.MinY.Value, x1 = s.WorldCoverage.MaxX.Value, z1 = s.WorldCoverage.MaxY.Value;
                double mx = (x0 + x1) / 2, mz = (z0 + z1) / 2;
                bool anyHoleQuadrant = false, allCovered = true;
                for (int q = 0; q < 4; q++)
                {
                    int qx = q % 2, qz = q / 2;
                    if (!QuadrantHasWeight(tile, qx, qz, tsp, pot, border)) continue;
                    anyHoleQuadrant = true;
                    string child = BoxKey(p.Key + 1, qx == 0 ? x0 : mx, qz == 0 ? z0 : mz, qx == 0 ? mx : x1, qz == 0 ? mz : z1);
                    if (!holeBoxes.Contains(child)) { allCovered = false; break; }
                }
                byte leaf = (byte)(anyHoleQuadrant && allCovered ? 0 : 1);
                if (s.IsLeafRaw != leaf)
                {
                    if (leaf == 1) leafSet++; else leafCleared++;
                    s.IsLeafRaw = leaf;
                }
            }

            byte[] rebuilt = TerrainStreamingTreeSerializer.Serialize(parsed.Resource, SerializeOptions.Mutating);
            int oldLength, newLength;
            byte[] output = TerrainPayload.Splice(payload, TerrainPayload.TagMask, rebuilt, out oldLength, out newLength);
            result.MaskReport = "layer " + holeLayer + ": cleared " + cleared + " donor hole tile(s), painted " + painted +
                " tile(s) (" + repurposed + " subtile(s) repurposed" + (unsortedLeft > 0 ? ", " + unsortedLeft + " node(s) left unsorted" : "") +
                (noTile > 0 ? ", " + noTile + " hole box(es) with no tiled subtile" : "") +
                ", leaf flags set on " + leafSet + " and cleared on " + leafCleared + ")" +
                (sets.Count > 0 ? "; painted layer sets " + string.Join(" ", sets) : "") + (haveHoles ? "" : "; the source has no holes");
            return output;
        }

        private static string BoxKey(int level, double minX, double minZ, double maxX, double maxZ)
        {
            return level.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                   Math.Round(minX, 2).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   Math.Round(minZ, 2).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   Math.Round(maxX, 2).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," +
                   Math.Round(maxZ, 2).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool QuadrantHasWeight(byte[] tile, int qx, int qz, int tsp, int pot, int border)
        {
            int half = pot / 2;
            int z0 = border + qz * half, x0 = border + qx * half;
            for (int z = z0; z < z0 + half; z++)
                for (int x = x0; x < x0 + half; x++)
                    if (tile[z * tsp + x] != 0) return true;
            return false;
        }

        // Coverage grid, decoded against shipped Endor_01 and Volcanic_01 tiles: levels coarse to fine (1x1 .. 2^(L-1) per
        // side), row-major over the tile interior, LSB first in each byte; a cell is set when its samples hold any weight.
        // Shipped grids run slightly wider than their tiles, so each set cell is grown by one neighbour.
        private static void SetHoleCoverage(byte[] cov, byte[] tile, int levels, int tsp, int pot, int border)
        {
            Array.Clear(cov, 0, cov.Length);
            int bit = 0;
            for (int l = 0; l < levels; l++)
            {
                int n = 1 << l;
                var occupied = new bool[n * n];
                double cell = (double)pot / n;
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++)
                    {
                        int z0 = border + (int)Math.Round(r * cell), z1 = border + (int)Math.Round((r + 1) * cell);
                        int x0 = border + (int)Math.Round(c * cell), x1 = border + (int)Math.Round((c + 1) * cell);
                        for (int z = z0; z < z1 && !occupied[r * n + c]; z++)
                            for (int x = x0; x < x1; x++)
                                if (tile[z * tsp + x] != 0) { occupied[r * n + c] = true; break; }
                    }
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++, bit++)
                    {
                        bool set = false;
                        for (int dr = -1; dr <= 1 && !set; dr++)
                            for (int dc = -1; dc <= 1; dc++)
                            {
                                int rr = r + dr, cc = c + dc;
                                if (rr >= 0 && rr < n && cc >= 0 && cc < n && occupied[rr * n + cc]) { set = true; break; }
                            }
                        if (set && (bit >> 3) < cov.Length) cov[bit >> 3] |= (byte)(1 << (bit & 7));
                    }
            }
        }

        // Hole weights for one tile: 2x2 sub-samples per tile sample, each a hole when the source has a hole within
        // `dilate` metres. Null when the tile holds no hole at all.
        private static byte[] HoleTile(ITerrainHeightSource source, double offsetX, double offsetZ, AxisAlignedBox2 c,
                                       int tsp, int pot, int border, double dilate)
        {
            double x0 = c.MinX.Value, z0 = c.MinY.Value;
            double cw = (c.MaxX.Value - x0) / pot, ch = (c.MaxY.Value - z0) / pot;
            byte[] w = null;
            for (int tj = 0; tj < tsp; tj++)
                for (int ti = 0; ti < tsp; ti++)
                {
                    double cx = x0 + (ti - border + 0.5) * cw, cz = z0 + (tj - border + 0.5) * ch;
                    int hits = 0;
                    for (int sj = 0; sj < 2; sj++)
                        for (int si = 0; si < 2; si++)
                        {
                            double x = cx + (si - 0.5) * 0.5 * cw - offsetX, z = cz + (sj - 0.5) * 0.5 * ch - offsetZ;
                            if (source.IsHole(x, z) || (dilate > 0 && (source.IsHole(x + dilate, z) || source.IsHole(x - dilate, z) ||
                                                                       source.IsHole(x, z + dilate) || source.IsHole(x, z - dilate))))
                                hits++;
                        }
                    if (hits == 0) continue;
                    if (w == null) w = new byte[tsp * tsp];
                    w[tj * tsp + ti] = (byte)(hits * 255 / 4);
                }
            return w;
        }
    }

    /// <summary>
    /// Conservative occluder grid for a node whose occluder layout could not be detected: every grid sample is set to
    /// the node's lowest real height sample (0 is the hole marker and is skipped; an all-hole node gets 1).
    /// A retarget used to keep the shipped grid in that case, which still described the donor's old ground: Tatooine_01
    /// retargeted to Desert_04 kept an occluder plateau up to 13 m above the new dunes, and from inside a dip it hid
    /// props and terrain until the camera moved. A per-node constant is valid for either level ordering of the stack
    /// and can never occlude anything standing on that node's ground.
    /// </summary>
    public static class OccluderGridFloor
    {
        public static byte[] Apply(byte[] occluderGrid, ushort[] samples, out bool lowered)
        {
            ushort floor = ushort.MaxValue;
            foreach (ushort v in samples) if (v != 0 && v < floor) floor = v;
            if (floor == ushort.MaxValue) floor = 1;
            ushort[] grid = TerrainPayload.ToU16(occluderGrid);
            lowered = false;
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid[i] > floor) lowered = true;
                grid[i] = floor;
            }
            return TerrainPayload.FromU16(grid);
        }
    }

    public static class TerrainHeightWriter
    {
        // Rewrites the elevations of `donorPayload` from `options.Source`, returning a full payload
        // that differs from the donor only inside the tag-0 record.
        public static TerrainRewriteResult Rewrite(byte[] donorPayload, TerrainRewriteOptions options)
        {
            if (donorPayload == null) throw new ArgumentNullException("donorPayload");
            if (options == null || options.Source == null) throw new ArgumentException("a height source is required");

            TerrainRecord donorRecord;
            byte[] isolated = TerrainPayload.Isolate(donorPayload, TerrainPayload.TagHeightfield, out donorRecord);
            if (isolated == null) throw new InvalidDataException("donor payload has no heightfield (tag 0) record");

            ParseResult parsed = TerrainStreamingTreeParser.Parse(isolated);
            if (parsed.Resource == null || parsed.Resource.Heightfield == null)
                throw new InvalidDataException("donor heightfield did not parse" +
                    (parsed.Failure != null ? ": " + parsed.Failure : ""));

            TerrainStreamingTreeResource resource = parsed.Resource;
            HeightfieldTree tree = resource.Heightfield;
            int side = (int)tree.NodeSamplesPerSide;
            int border = (int)tree.NodeBorderWidth;
            int interior = side - 2 * border - 1;
            double worldY = tree.WorldSizeY.Value;
            if (interior <= 0) throw new InvalidDataException("donor tile geometry is degenerate (" + side + "/border" + border + ")");

            var result = new TerrainRewriteResult();

            // The derived-array layouts are not stated by the format, so they are voted on from the
            // donor's own shipped bytes. A flat node matches every variant, so only the most varied
            // nodes get a vote and a split vote means "leave the block alone".
            MinMaxLayout minMaxLayout = null;
            OccluderLayout occluderLayout = null;
            if (!options.KeepDerived)
                DetectLayouts(tree, side, border, out minMaxLayout, out occluderLayout, result);

            double bandMin = options.BandMin ?? 0.0, bandMax = options.BandMax ?? 0.0;
            bool remap = options.BandMin.HasValue && options.BandMax.HasValue;
            double srcLo = options.Source.MinMetres, srcHi = options.Source.MaxMetres;
            double outLo = double.MaxValue, outHi = double.MinValue;

            var ownRange = new Dictionary<HeightfieldNode, SampleRange>();
            foreach (HeightfieldNode node in tree.EnumerateNodes())
            {
                if (node.Payload == null) continue;
                AxisAlignedBox box = node.BoundingBox;
                var samples = new ushort[side * side];
                int zeros = 0;

                for (int j = 0; j < side; j++)
                {
                    double z = box.MinZ.Value + (j - border) * (box.MaxZ.Value - box.MinZ.Value) / interior;
                    for (int i = 0; i < side; i++)
                    {
                        double x = box.MinX.Value + (i - border) * (box.MaxX.Value - box.MinX.Value) / interior;
                        if (!options.FillHoles && options.Source.IsHole(x - options.OffsetX, z - options.OffsetZ))
                        {
                            samples[j * side + i] = 0;      // keep the source's hole (HoleHeightMarkerSample)
                            zeros++;
                            continue;
                        }
                        double metres = options.Source.SampleMetres(x - options.OffsetX, z - options.OffsetZ);
                        if (remap)
                        {
                            double t = (metres - srcLo) / Math.Max(1e-6, srcHi - srcLo);
                            metres = bandMin + t * (bandMax - bandMin);
                        }
                        metres += options.OffsetY;
                        if (metres < outLo) outLo = metres;
                        if (metres > outHi) outHi = metres;

                        // Sample value 0 is the hole marker (HoleGuard.HoleHeightMarkerSample), so
                        // ground at exactly 0 m is floored to 1 rather than punched through.
                        double raw = Math.Round(metres * 65535.0 / worldY);
                        if (raw < 1.0) raw = 1.0; else if (raw > 65535.0) raw = 65535.0;
                        samples[j * side + i] = (ushort)raw;
                    }
                }

                ushort[] previous = TerrainPayload.ToU16(node.Payload.AtlasData);
                var range = new SampleRange();
                for (int k = 0; k < samples.Length; k++)
                {
                    result.SamplesTotal++;
                    if (k < previous.Length && samples[k] != previous[k]) result.SamplesChanged++;
                    range.Add(samples[k]);
                }

                node.Payload.AtlasData = TerrainPayload.FromU16(samples);
                result.NodesRewritten++;
                // containsHoles follows the node's own samples, as in every shipped tree measured (Endor_01, Hoth_01,
                // Volcanic_01: exactly the nodes holding sample 0 carry it).
                if (node.TailFlagsRead)
                {
                    byte holeFlag = (byte)(zeros > 0 ? 1 : 0);
                    if (node.ContainsHolesRaw != holeFlag) result.HoleFlagsChanged++;
                    node.ContainsHolesRaw = holeFlag;
                }
                if (zeros > 0) result.HoleNodes++;
                if (options.KeepDerived) continue;

                if (minMaxLayout != null)
                    node.Payload.MinMaxStackData = TerrainPayload.FromU16(
                        MinMaxPyramid.Build(samples, side, border, (int)tree.MinMaxStackDepth, minMaxLayout));
                if (occluderLayout != null)
                    node.Payload.OccluderGridStackData = TerrainPayload.FromU16(
                        OccluderGrid.Build(samples, side, border, (int)tree.OccluderGridStackDepth, occluderLayout));
                else if (node.Payload.OccluderGridStackData.Length > 0)
                {
                    // Undetected layout: a stale grid describes the donor's old ground, so floor it (OccluderGridFloor).
                    bool lowered;
                    node.Payload.OccluderGridStackData = OccluderGridFloor.Apply(node.Payload.OccluderGridStackData, samples, out lowered);
                    if (lowered) result.OccluderGridsFloored++;
                }

                ownRange[node] = range;
            }

            if (!options.KeepDerived && tree.Root != null)
                result.BoundingBoxesRecomputed = PropagateBoxes(tree.Root, ownRange, worldY);

            result.OutMinMetres = outLo == double.MaxValue ? 0 : outLo;
            result.OutMaxMetres = outHi == double.MinValue ? 0 : outHi;

            byte[] reserialised = TerrainStreamingTreeSerializer.Serialize(resource, SerializeOptions.Mutating);
            int oldLength, newLength;
            result.Payload = TerrainPayload.Splice(donorPayload, TerrainPayload.TagHeightfield,
                                                   reserialised, out oldLength, out newLength);
            result.RecordLength = newLength;

            if (options.MaskDonor != null)
                result.Payload = TerrainPayload.Graft(result.Payload, options.MaskDonor,
                    new[] { TerrainPayload.TagMask, TerrainPayload.TagColor, TerrainPayload.TagMaterial, TerrainPayload.TagLayer });
            if (options.HoleLayer >= 0 && !options.FillHoles)
                result.Payload = TerrainHolePainter.Paint(result.Payload, options.Source, options.OffsetX, options.OffsetZ,
                                                          options.HoleLayer, options.HoleDilate, result);
            return result;
        }

        private static void DetectLayouts(HeightfieldTree tree, int side, int border,
                                          out MinMaxLayout minMax, out OccluderLayout occluder,
                                          TerrainRewriteResult result)
        {
            minMax = null; occluder = null;

            var probes = new List<KeyValuePair<int, HeightfieldNode>>();
            foreach (HeightfieldNode n in tree.EnumerateNodes())
            {
                if (n.Payload == null) continue;
                ushort[] s = TerrainPayload.ToU16(n.Payload.AtlasData);
                ushort lo = ushort.MaxValue, hi = 0;
                foreach (ushort v in s) { if (v < lo) lo = v; if (v > hi) hi = v; }
                probes.Add(new KeyValuePair<int, HeightfieldNode>(hi - lo, n));
            }
            probes.Sort(delegate (KeyValuePair<int, HeightfieldNode> a, KeyValuePair<int, HeightfieldNode> b)
            { return b.Key.CompareTo(a.Key); });

            var minMaxVotes = new Dictionary<string, int>();
            var occluderVotes = new Dictionary<string, int>();
            MinMaxLayout minMaxPick = null; OccluderLayout occluderPick = null;

            int probed = Math.Min(24, probes.Count);
            for (int i = 0; i < probed; i++)
            {
                HeightfieldNode n = probes[i].Value;
                ushort[] s = TerrainPayload.ToU16(n.Payload.AtlasData);

                MinMaxPyramid.DetectionResult m = MinMaxPyramid.DetectLayout(
                    s, side, border, (int)tree.MinMaxStackDepth, TerrainPayload.ToU16(n.Payload.MinMaxStackData));
                if (m.MatchCount == 1) { Bump(minMaxVotes, m.UniqueMatch.ToString()); minMaxPick = m.UniqueMatch; }

                OccluderGrid.DetectionResult o = OccluderGrid.DetectLayout(
                    s, side, border, (int)tree.OccluderGridStackDepth, TerrainPayload.ToU16(n.Payload.OccluderGridStackData));
                if (o.MatchCount == 1) { Bump(occluderVotes, o.UniqueMatch.ToString()); occluderPick = o.UniqueMatch; }
            }

            // One unanimous layout is adopted; a split vote means the block is left as shipped,
            // because writing the wrong layout is worse than writing a stale one.
            if (minMaxVotes.Count == 1) { minMax = minMaxPick; result.MinMaxLayout = minMaxPick.ToString(); }
            else result.MinMaxLayout = minMaxVotes.Count == 0 ? "(undetected, left as shipped)"
                                                             : "(ambiguous: " + Describe(minMaxVotes) + ", left as shipped)";

            if (occluderVotes.Count == 1) { occluder = occluderPick; result.OccluderLayout = occluderPick.ToString(); }
            else result.OccluderLayout = occluderVotes.Count == 0 ? "(undetected, floored to each node's lowest sample)"
                                                                  : "(ambiguous: " + Describe(occluderVotes) + ", floored to each node's lowest sample)";
        }

        private static void Bump(Dictionary<string, int> votes, string key)
        { int c; votes[key] = votes.TryGetValue(key, out c) ? c + 1 : 1; }

        private static string Describe(Dictionary<string, int> votes)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in votes) parts.Add(kv.Key + " x" + kv.Value);
            return string.Join(", ", parts.ToArray());
        }

        // A node's bounding box spans its whole subtree, not just its own samples -- verified against
        // shipped Crait data, where parents disagree with their own sample range by up to 199 m.
        // Fitting each box to its own node leaves parents too tight and the cull job drops the subtree.
        private static int PropagateBoxes(HeightfieldNode root, Dictionary<HeightfieldNode, SampleRange> own, double worldY)
        {
            int count = 0;
            Walk(root, own, worldY, ref count);
            return count;
        }

        private static SampleRange Walk(HeightfieldNode node, Dictionary<HeightfieldNode, SampleRange> own,
                                        double worldY, ref int count)
        {
            var range = new SampleRange();
            SampleRange mine;
            if (own.TryGetValue(node, out mine)) range.Merge(mine);

            foreach (HeightfieldNode child in node.Children)
                range.Merge(Walk(child, own, worldY, ref count));

            if (!range.HasValue) return range;

            AxisAlignedBox box = node.BoundingBox;
            box.MinY = F32.FromValue((float)(range.Lo * worldY / 65535.0));
            box.MaxY = F32.FromValue((float)(range.Hi * worldY / 65535.0));
            node.BoundingBox = box;
            count++;
            return range;
        }

        internal struct SampleRange
        {
            public ushort Lo, Hi;
            public bool HasValue;

            public void Add(ushort v)
            {
                if (!HasValue) { Lo = v; Hi = v; HasValue = true; return; }
                if (v < Lo) Lo = v;
                if (v > Hi) Hi = v;
            }

            public void Merge(SampleRange other)
            {
                if (!other.HasValue) return;
                Add(other.Lo);
                Add(other.Hi);
            }
        }
    }
}
