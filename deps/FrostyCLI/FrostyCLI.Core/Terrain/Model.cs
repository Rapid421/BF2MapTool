#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainFormat\Model.cs (sha256:551ACCDEDD0B7490).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;

namespace Bfii.TerrainFormat
{
    /// <summary>
    /// BFII raster tree type ids.
    /// <para>
    /// Provenance: BFII_EXACT — W01 §4.4 and W06 §3.4, both from the BFII-branching reader
    /// (<c>TerrainStreamingTree.cs:14-21</c>). BFII has <b>five</b> types with compacted ids.
    /// </para>
    /// <para>
    /// <b>Do not substitute FIFA's ids.</b> Master plan §10 C-01: FIFA 2019 has eight types
    /// (<c>BakedVTAlbedo=3, BakedVTNormal=4, Material=5, DestructionDepth=6, EnlightenAlbedo=7</c>).
    /// BFII has no baked virtual texture and no Enlighten albedo tree at all. A writer built on the
    /// FIFA ids emits a resource the game cannot parse. Types 5-7 must never appear in BFII data;
    /// the parser records it as a diagnostic if they do.
    /// </para>
    /// </summary>
    public enum RasterTreeType : byte
    {
        Heightfield = 0,
        Mask = 1,
        Color = 2,
        Material = 3,
        DestructionDepth = 4,

        /// <summary>Terminator byte for the raster-tree record list. Not a tree type.</summary>
        EndOfList = 0xFF,
    }

    /// <summary>
    /// Which heightfield node-record flow to use. Master plan §10 C-03 is <b>UNRESOLVED</b>: the two
    /// documented readers disagree for at least two node shapes, and no BFII byte has been read to
    /// settle it. Both are implemented; X1 reports which one consumes exactly
    /// <c>rasterTreeLoadSize</c>.
    /// </summary>
    public enum HeightfieldNodeFlow
    {
        /// <summary>
        /// Primary hypothesis. The merge of Glacier's early return and the C# reader's
        /// skipped-node min/max, as the FIFA writer emits it (plan §10 C-03, W02 §4.7
        /// <c>writeNodesRecursive</c>):
        /// <code>
        /// [disabled -> node ends]; skipped; hasData;
        /// if (!hasData &amp;&amp; !skipped) node ends;
        /// containsHoles; persistent;
        /// if (hasData &amp;&amp; persistent) full payload  else if (skipped) minMax only;
        /// hasChildren
        /// </code>
        /// Provenance of the shape: FIFA21_SOURCE. Provenance of applying it to BFII: INFERRED.
        /// </summary>
        Merged = 0,

        /// <summary>
        /// The Frosty C# reader flow, which has run inside Frosty against BFII data (W01 §4.4.1):
        /// <code>
        /// [disabled -> node ends]; pruned; hasData; unknown/containsHoles; persistent;
        /// if (pruned) minMax only;
        /// if (hasData &amp;&amp; persistent) full payload;
        /// hasChildren
        /// </code>
        /// It differs from <see cref="Merged"/> for a node that is neither disabled, skipped nor
        /// data-bearing (Merged stops after three flags, this reads five and continues), and for a
        /// node that is both pruned and persistently data-bearing (this reads min/max <i>and</i> the
        /// full payload, Merged reads only the payload).
        /// Provenance: BFII_EXACT that this reader exists and runs; UNKNOWN that it is BFII's record.
        /// </summary>
        AlwaysReadFive = 1,
    }

    // ---------------------------------------------------------------------------------------
    // Top-level resource
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Parsed <c>TerrainStreamingTree</c> RES payload.
    /// ResourceType 587107016 (<c>0x22FE8AC8</c>). Provenance: FROSTY_1063 (W01 §4.3).
    /// </summary>
    public sealed class TerrainStreamingTreeResource
    {
        public StreamingTreeHeader Header = new StreamingTreeHeader();

        /// <summary>Raster tree records in the exact order they appear in the payload.</summary>
        public List<RasterTreeRecord> RasterTrees { get; } = new List<RasterTreeRecord>();

        /// <summary>Terminator byte actually read (expected 0xFF). Preserved for identity.</summary>
        public byte Terminator = (byte)RasterTreeType.EndOfList;

        public StreamingNode? StreamingRoot;

        /// <summary>
        /// Bytes present after the streaming-node list. Assertion B14 requires this to be empty;
        /// it is preserved so a re-serialise is byte-identical even when B14 fails, which is what
        /// makes a failing round-trip diagnosable instead of merely red.
        /// </summary>
        public byte[] TrailingBytes = Array.Empty<byte>();

        /// <summary>Set when the input carried a 16-byte <c>resMeta</c> prefix (plan §10 C-07).</summary>
        public byte[]? ResMetaPrefix;

        public HeightfieldTree? Heightfield =>
            FindTree(RasterTreeType.Heightfield) as HeightfieldTree;

        public MaskTree? Mask => FindTree(RasterTreeType.Mask) as MaskTree;

        public ColorTree? Color => FindTree(RasterTreeType.Color) as ColorTree;

        public MaterialTree? Material => FindTree(RasterTreeType.Material) as MaterialTree;

        public DestructionDepthTree? DestructionDepth =>
            FindTree(RasterTreeType.DestructionDepth) as DestructionDepthTree;

        public RasterTreeBody? FindTree(RasterTreeType type)
        {
            foreach (var r in RasterTrees)
                if (r.Type == type) return r.Body;
            return null;
        }

        public IEnumerable<StreamingNode> EnumerateStreamingNodes()
        {
            if (StreamingRoot is null) yield break;
            var stack = new Stack<StreamingNode>();
            stack.Push(StreamingRoot);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }
    }

    /// <summary>
    /// Top-level header, 8 fields, offsets 0x00-0x19, total 0x1A bytes.
    /// Provenance: BFII_EXACT — W01 §4.4; two independent reverse-engineerings agree field for field.
    /// </summary>
    public sealed class StreamingTreeHeader
    {
        /// <summary>0x00</summary>
        public uint UnblurredSamplesPerNodeSidePot;

        /// <summary>0x04, u8 bool. Raw byte preserved for identity.</summary>
        public byte TrackTextureDetailFalloffRaw;

        /// <summary>0x05</summary>
        public F32 InvisibleDetailReductionFactor;

        /// <summary>0x09</summary>
        public F32 OccludedDetailReductionFactor;

        /// <summary>
        /// 0x0D. <b>Semantics UNKNOWN</b> — master plan §10 C-04. Glacier names it
        /// <c>skip_levels</c>; the LevelEditor reader names it <c>ResourceBlurriness</c>.
        /// Layout is BFII_EXACT, meaning is not. Discriminator: FIFA's blurriness is a 0..2 field,
        /// so any observed value &gt; 2 falsifies the blurriness reading outright.
        /// </summary>
        public uint Field0x0D;

        /// <summary>
        /// 0x11. <b>Semantics UNKNOWN</b> — master plan §10 C-04. Glacier names it
        /// <c>baked_levels</c>; the LevelEditor reader leaves it unnamed. FIFA has three u32s here
        /// (<c>additionalBlurriness, skipLevels, bakedLevels</c>) where BFII has two, so BFII dropped
        /// exactly one of them — which one is INFERRED, not observed.
        /// </summary>
        public uint Field0x11;

        /// <summary>0x15 — streaming node count.</summary>
        public uint NodeCount;

        /// <summary>0x19, u8 bool. Raw byte preserved for identity.</summary>
        public byte FreeStreamingEnableRaw;

        public const int SizeBytes = 0x1A;

        public bool TrackTextureDetailFalloff => TrackTextureDetailFalloffRaw != 0;
        public bool FreeStreamingEnable => FreeStreamingEnableRaw != 0;
    }

    /// <summary>
    /// One length-framed raster tree record: <c>[u8 type][u32 rasterTreeLoadSize][payload]</c>.
    /// Provenance: BFII_EXACT — W01 §4.4. A reader must seek to <c>startPos + size</c> and a
    /// conforming parser must assert exact consumption (assertion B1).
    /// </summary>
    public sealed class RasterTreeRecord
    {
        public RasterTreeType Type;

        /// <summary>The declared byte length, as read. Re-serialised verbatim.</summary>
        public uint DeclaredSize;

        public RasterTreeBody? Body;

        /// <summary>
        /// Bytes between where parsing stopped and <c>startPos + DeclaredSize</c>. Assertion B1
        /// requires this to be empty for every record. Preserved so the round-trip is still exact
        /// when it is not, which turns "B1 failed" into "B1 failed and here are the N bytes".
        /// </summary>
        public byte[] Slack = Array.Empty<byte>();

        /// <summary>Bytes actually consumed by the body parse (excludes <see cref="Slack"/>).</summary>
        public int ParsedBytes;
    }

    /// <summary>Base for a parsed raster-tree payload.</summary>
    public abstract class RasterTreeBody
    {
        public abstract RasterTreeType Type { get; }
    }

    /// <summary>
    /// A raster tree carried verbatim.
    /// <para>
    /// Since 2026-09-23 every BFII tree type (0-4) has a structured reader that consumes all 39 shipped
    /// streaming trees exactly, so this only appears when a record fails its structured parse (an unknown
    /// colour texture format, say). The failure is then recorded in <see cref="ParseDiagnostics.Rejected"/>.
    /// </para>
    /// <para>
    /// Opaque carriage is the only honest round-trip for an unparsed payload (W01 §6.1). It is also
    /// exact, so a resource containing one still passes D1.
    /// </para>
    /// </summary>
    public sealed class OpaqueRasterTree : RasterTreeBody
    {
        private readonly RasterTreeType _type;
        public byte[] Payload = Array.Empty<byte>();

        public OpaqueRasterTree(RasterTreeType type) => _type = type;
        public override RasterTreeType Type => _type;
    }

    // ---------------------------------------------------------------------------------------
    // Heightfield tree (type 0)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Heightfield raster tree. Header is 20 fields in identical order in both independent readers.
    /// Provenance: BFII_EXACT — W01 §4.4.1.
    /// </summary>
    public sealed class HeightfieldTree : RasterTreeBody
    {
        public override RasterTreeType Type => RasterTreeType.Heightfield;

        public uint NodeSamplesPerSide;
        public uint ResourceAtlasSampleCountX;
        public uint ResourceAtlasSampleCountY;

        /// <summary>Stored as a log2 exponent; consumers use <c>1 &lt;&lt; value</c>.</summary>
        public int BlurrinessLog2;

        public F32 WorldSizeY;
        public F32 PhysicsMetersPerSample;
        public int PhysicsCropWidth;

        /// <summary>u8 bool, name UNKNOWN in both readers. Raw byte preserved.</summary>
        public byte Unknown0x1DRaw;

        /// <summary>f32, name UNKNOWN in both readers.</summary>
        public F32 Unknown0x1E;

        public uint MinMaxStackDepth;
        public uint OccluderGridStackDepth;
        public uint DensityMapNodeSamplesPerSide;
        public uint DensityMapBorderWidth;
        public uint DensityMapNodeSamplesPerSidePot;
        public F32 DensityMapResolutionRatio;
        public uint NodeCount;
        public uint PersistentNodeCount;
        public uint PersistentDedicatedServerNodeCount;
        public uint SkippedNodeCount;
        public uint NodeBorderWidth;

        public HeightfieldNode? Root;

        /// <summary>Which flow produced <see cref="Root"/>. Recorded per terrain by X1.</summary>
        public HeightfieldNodeFlow Flow;

        public const int HeaderSizeBytes = 77; // 19 x 4 + 1

        /// <summary>
        /// Count of u16 elements in a node's min/max stack.
        /// <c>minMaxWidth = 1 &lt;&lt; (depth-1)</c>; <c>size = SUM (minMaxWidth &gt;&gt; i)^2 * 2</c>.
        /// Provenance: BFII_EXACT — both readers compute this identically (W01 §4.4.1).
        /// The factor 2 is the (min,max) pair per cell.
        /// </summary>
        public uint MinMaxStackSize => ComputeMinMaxStackSize(MinMaxStackDepth);

        /// <summary>
        /// Count of u16 elements in a node's occluder grid stack.
        /// <c>occW = 1 &lt;&lt; (depth-1)</c>; <c>size = SUM ((occW &gt;&gt; i) + 1)^2</c>.
        /// Provenance: BFII_EXACT — W01 §4.4.1; exact match against the shipped derivation.
        /// </summary>
        public uint OccluderGridStackSize => ComputeOccluderGridStackSize(OccluderGridStackDepth);

        public static uint ComputeMinMaxStackSize(uint depth)
        {
            if (depth == 0) return 0;
            uint width = 1u << (int)(depth - 1);
            uint total = 0;
            for (int i = 0; i < depth; i++)
            {
                uint b = width >> i;
                total += 2 * b * b;
            }
            return total;
        }

        public static uint ComputeOccluderGridStackSize(uint depth)
        {
            if (depth == 0) return 0;
            uint width = 1u << (int)(depth - 1);
            uint total = 0;
            for (int i = 0; i < depth; i++)
            {
                uint b = width >> i;
                total += (b + 1) * (b + 1);
            }
            return total;
        }

        public int AtlasBytes => checked((int)(2 * NodeSamplesPerSide * NodeSamplesPerSide));
        public int MinMaxBytes => checked((int)(MinMaxStackSize * 2));
        public int OccluderBytes => checked((int)(OccluderGridStackSize * 2));
        public int DensityBytes => checked((int)(DensityMapNodeSamplesPerSide * DensityMapNodeSamplesPerSide));

        /// <summary>
        /// <c>nodeDataSize</c> — the per-node block size, and the offset at which a LOD0 chunk's mask
        /// tiles begin. Provenance: BFII_EXACT — W01 §4.4.1 and §4.5.
        /// </summary>
        public int NodeDataSize => AtlasBytes + MinMaxBytes + OccluderBytes + DensityBytes;

        /// <summary>World height of a sample. Provenance: BFII_EXACT — W01 §4.4.1.</summary>
        public double SampleToWorldHeight(ushort sample) => sample * (WorldSizeY.Value / 65535.0);

        public IEnumerable<HeightfieldNode> EnumerateNodes()
        {
            if (Root is null) yield break;
            var stack = new Stack<HeightfieldNode>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }
    }

    /// <summary>One heightfield node record. All flags are carried as raw bytes for identity.</summary>
    public sealed class HeightfieldNode
    {
        public QuadtreeNodeId Id;
        public AxisAlignedBox BoundingBox;

        public byte DisabledRaw;
        public bool FlagsRead;        // false when the node ended at DisabledRaw
        public byte SkippedRaw;       // "nodeHasBeenPruned" in the C# reader
        public byte HasDataRaw;
        public bool TailFlagsRead;    // false when Merged returned after HasDataRaw
        public byte ContainsHolesRaw; // "nodeUnknown" in the C# reader
        public byte PersistentRaw;

        /// <summary>Present when the node carried a min/max-only block (skipped/pruned node).</summary>
        public byte[]? SkippedMinMax;

        /// <summary>Present when <c>hasData &amp;&amp; persistent</c>: the inline payload.</summary>
        public HeightfieldNodePayload? Payload;

        public bool HasChildrenFlagRead;
        public byte HasChildrenRaw;
        public List<HeightfieldNode> Children { get; } = new List<HeightfieldNode>();

        public bool Disabled => DisabledRaw != 0;
        public bool Skipped => SkippedRaw != 0;
        public bool HasData => HasDataRaw != 0;
        public bool ContainsHoles => ContainsHolesRaw != 0;
        public bool Persistent => PersistentRaw != 0;
        public bool HasChildren => HasChildrenRaw != 0;

        /// <summary>Byte offset of this record's start within the raster-tree payload window.</summary>
        public int RecordOffset;
    }

    /// <summary>
    /// The inline per-node data block, in stream order.
    /// Provenance: BFII_EXACT — W01 §4.4.1. Order is fixed:
    /// <c>atlasData -&gt; minMaxStack -&gt; occluderGrid -&gt; densityData</c>.
    /// </summary>
    public sealed class HeightfieldNodePayload
    {
        /// <summary>u16 height samples, <c>nodeSamplesPerSide^2</c> of them. Carried as raw bytes.</summary>
        public byte[] AtlasData = Array.Empty<byte>();
        public byte[] MinMaxStackData = Array.Empty<byte>();
        public byte[] OccluderGridStackData = Array.Empty<byte>();
        public byte[] DensityData = Array.Empty<byte>();

        public int TotalBytes =>
            AtlasData.Length + MinMaxStackData.Length + OccluderGridStackData.Length + DensityData.Length;
    }

    // ---------------------------------------------------------------------------------------
    // Mask tree (type 1)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Mask raster tree — where mask tiles and layer indices live.
    /// Provenance: BFII_EXACT — W01 §4.4.2.
    /// </summary>
    public sealed class MaskTree : RasterTreeBody
    {
        public override RasterTreeType Type => RasterTreeType.Mask;

        public uint TileSamplesPerSide;
        public uint TilesPerNodeSide;

        /// <summary>
        /// log2(<see cref="TilesPerNodeSide"/>): 2 on 38 of 39 shipped terrains, 1 on the one with 2 tiles per side.
        /// FIFA writes the asset's <c>SubtileRelativeLevel</c> here (FIFA21_SOURCE); BFII_EXACT that it matches.
        /// </summary>
        public uint SubtileRelativeLevel;

        public uint ResourceAtlasSampleCountX;
        public uint ResourceAtlasSampleCountY;
        public int BlurrinessLog2;
        public AxisAlignedBox2 TreeCoverage;
        public uint NodeCount;
        public uint SubtileCount;
        public uint PersistentNonTrivialSubtileCount;

        /// <summary>
        /// The coverage-grid stack level count <c>L</c> that sizes every subtile's grid (FIFA:
        /// <c>highResolutionCoverageGridStackLevelCount</c>). BFII_EXACT, 2026-09-23: equals the stride level that
        /// consumes the record exactly on all 39 shipped terrains (4 on 35, 6 on 4), so the parser reads it rather
        /// than searching.
        /// </summary>
        public uint CoverageGridLevelCount;

        /// <summary>The coverage-grid stack level count the parse used. See <see cref="MaskStride"/>.</summary>
        public int FittedStrideLevel;

        public int Stride => MaskStride.ForLevel(FittedStrideLevel);
        public int SubtileRecordBytes => MaskStride.SubtileRecordBytes(FittedStrideLevel);

        public MaskNode? Root;

        public const int HeaderSizeBytes = 56; // 5x4 + 4 + 16 + 3x4 + 4

        public uint AtlasTileCountX => TileSamplesPerSide == 0 ? 0 : ResourceAtlasSampleCountX / TileSamplesPerSide;
        public uint AtlasTileCountY => TileSamplesPerSide == 0 ? 0 : ResourceAtlasSampleCountY / TileSamplesPerSide;

        public int TileBytes => checked((int)(TileSamplesPerSide * TileSamplesPerSide));

        public IEnumerable<MaskNode> EnumerateNodes()
        {
            if (Root is null) yield break;
            var stack = new Stack<MaskNode>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }

        /// <summary>
        /// <c>max(terrainLayerIndex) + 1</c> over every subtile. Assertion B7 checks this against
        /// <c>TerrainData.TerrainLayers.Count</c>. Provenance: BFII_EXACT — W01 §4.4.2.
        /// </summary>
        public int MaxLayerCount
        {
            get
            {
                int max = -1;
                foreach (var n in EnumerateNodes())
                    foreach (var s in n.Subtiles)
                        if (s.TerrainLayerIndex > max) max = s.TerrainLayerIndex;
                return max + 1;
            }
        }
    }

    public sealed class MaskNode
    {
        public QuadtreeNodeId Id;
        public AxisAlignedBox2 NodeCoverage;

        public byte PrunedRaw;
        public ushort SubtileCount;
        public ushort NonTrivialSubtileCount;
        public ushort NonTrivialPersistentSubtileCountRecursive;

        public List<MaskSubtile> Subtiles { get; } = new List<MaskSubtile>();

        /// <summary>
        /// True when the node body (subtiles, persistence, children) was present.
        /// The reader emits nothing after the four header fields when
        /// <c>pruned || subtileCount == 0</c>.
        /// Provenance: BFII_EXACT — W01 §4.4.2.
        /// </summary>
        public bool BodyPresent;

        public byte PersistentRaw;

        /// <summary>
        /// Byte count of the inline tiles, written <b>before</b> them (FIFA's writer back-patches it there).
        /// BFII_EXACT, 2026-09-23: equals <c>tiles * tileBytes</c> on all 2,999 shipped persistent nodes with tiles and
        /// is 0 on the 110 without. Readers before that date took it for a trailing u32 and decoded every tile 4 bytes
        /// off (byte round trips were still exact, tile contents were not).
        /// </summary>
        public uint TileDataBytes;

        /// <summary>One tile per subtile with <c>(flags &amp; 2) == 0</c>, in subtile order.</summary>
        public List<byte[]> Tiles { get; } = new List<byte[]>();

        public byte HasChildrenRaw;
        public List<MaskNode> Children { get; } = new List<MaskNode>();

        public bool Pruned => PrunedRaw != 0;
        public bool Persistent => PersistentRaw != 0;
        public bool HasChildren => HasChildrenRaw != 0;

        public int RecordOffset;

        /// <summary>Count of subtiles that carry a tile, i.e. <c>(flags &amp; 2) == 0</c>.</summary>
        public int DerivedNonTrivialSubtileCount
        {
            get
            {
                int n = 0;
                foreach (var s in Subtiles) if ((s.Flags & 2) == 0) n++;
                return n;
            }
        }
    }

    /// <summary>
    /// Subtile record: <c>u8 layerIndex + AABB2 (16 B) + u8 isLeaf + u8 isMaskDataFull + u8[STRIDE]</c>
    /// = <c>19 + STRIDE</c> bytes. Provenance: BFII_EXACT — W01 §4.4.2; the flag names are FIFA's.
    /// <para>
    /// A subtile is one terrain layer over one of the node's <c>tilesPerNodeSide^2</c> subcells. A "full" subtile
    /// (<see cref="MaskDataFullRaw"/>) has weight 255 everywhere and carries no tile; every other subtile carries a
    /// <c>tileSamplesPerSide^2</c> weight tile.
    /// </para>
    /// </summary>
    public sealed class MaskSubtile
    {
        /// <summary>
        /// Index into <c>TerrainData.TerrainLayers</c>. A layer has no name and no mask reference in
        /// BFII EBX — it is identified purely by this index, which is why reordering
        /// <c>TerrainLayers</c> silently remaps every subtile (W01 §3.4, plan §7 M5).
        /// </summary>
        public byte TerrainLayerIndex;

        public AxisAlignedBox2 WorldCoverage;
        public byte IsLeafRaw;

        /// <summary>FIFA <c>isMaskDataFull</c>: the subtile carries no tile.</summary>
        public byte MaskDataFullRaw;

        public byte[] HighResolutionCoverageGridStack = Array.Empty<byte>();

        /// <summary>Composed as <c>isLeaf | (isMaskDataFull &lt;&lt; 1)</c>, matching the reference reader.</summary>
        public byte Flags => (byte)((IsLeafRaw != 0 ? 1 : 0) | (MaskDataFullRaw != 0 ? 2 : 0));

        public bool IsTrivial => (Flags & 2) != 0;
    }

    // ---------------------------------------------------------------------------------------
    // Material tree (type 3)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Material raster tree: per node a 4-bit physics-material index raster, RLE-compressed per line
    /// (<see cref="Rle4"/>).
    /// <para>
    /// Provenance: BFII_EXACT, 2026-09-23 — the record below consumes all 39 shipped streaming trees
    /// exactly (2,254 data nodes, 645,376 RLE lines, every line re-encodes byte-identically). The shape
    /// is FIFA's <c>TerrainMaterialTreePseudoPipeline</c> writer (FIFA21_SOURCE) with one BFII
    /// difference: a data node carries a <c>u8</c> (<see cref="MaterialNode.EncodingRaw"/>, 0 in all
    /// 2,254) between <c>persistent</c> and the RLE size. That byte is why the earlier reader failed on
    /// every shipped terrain.
    /// </para>
    /// <para>
    /// Note the id: BFII's material tree is type <b>3</b>, not FIFA's 5 (plan §10 C-01).
    /// </para>
    /// </summary>
    public sealed class MaterialTree : RasterTreeBody
    {
        public override RasterTreeType Type => RasterTreeType.Material;

        /// <summary>256 on every shipped terrain (the raster is independent of the heightfield tile size).</summary>
        public uint NodeSamplesPerSide;
        public int BlurrinessLog2;

        /// <summary>Four f32 read as (min.x, min.y, max.x, max.y).</summary>
        public AxisAlignedBox2 TreeCoverage;

        public uint NodeCount;
        public uint PersistentNodeCount;
        public uint LevelMax;

        public MaterialNode? Root;

        /// <summary>
        /// One packed <c>MaterialDecl</c> per material index the raster may hold (FIFA: the asset's
        /// <c>Materials</c> list, each turned into a material declaration). BFII_EXACT: 0..13 entries.
        /// </summary>
        public List<uint> MaterialDecls { get; } = new List<uint>();

        /// <summary>
        /// Packed <c>MaterialDecl</c> for ground outside every data node (FIFA: <c>BackgroundMaterial</c>).
        /// BFII_EXACT: <c>0x800</c> on 35 of 39 terrains.
        /// </summary>
        public uint BackgroundMaterialDecl;

        public const int HeaderSizeBytes = 36; // 4 + 4 + 16 + 12

        /// <summary>Bytes per RLE line once decoded: two 4-bit samples per byte.</summary>
        public int PackedLineBytes => (int)NodeSamplesPerSide / 2;

        public IEnumerable<MaterialNode> EnumerateNodes()
        {
            if (Root is null) yield break;
            var stack = new Stack<MaterialNode>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }
    }

    /// <summary>
    /// A node of an RLE raster tree (material, destruction depth). A node without data may still have
    /// children with data, so the walk never stops at <c>!hasData</c> (FIFA21_SOURCE, confirmed by the
    /// exact consumption above).
    /// </summary>
    public abstract class RleRasterNode
    {
        public QuadtreeNodeId Id;
        public byte HasDataRaw;
        public byte PersistentRaw;

        /// <summary>Declared RLE byte count, as read.</summary>
        public int DeclaredRleSize;

        /// <summary>All lines' RLE bytes, concatenated.</summary>
        public byte[] RleData = Array.Empty<byte>();

        /// <summary>
        /// One u16 byte count per line (<c>nodeSamplesPerSide</c> lines); they sum to
        /// <see cref="DeclaredRleSize"/> on every shipped node.
        /// </summary>
        public ushort[] LineSizes = Array.Empty<ushort>();

        public byte HasChildrenRaw;

        public bool HasData => HasDataRaw != 0;
        public bool Persistent => PersistentRaw != 0;
        public bool HasChildren => HasChildrenRaw != 0;

        /// <summary>True when the RLE payload is inline in this record.</summary>
        public bool CarriesData => HasData && Persistent;

        public int RecordOffset;
    }

    public sealed class MaterialNode : RleRasterNode
    {
        /// <summary>
        /// The u8 BFII writes between <c>persistent</c> and the RLE size of a data node. 0 on all 2,254
        /// shipped data nodes; name UNKNOWN. Preserved for identity.
        /// </summary>
        public byte EncodingRaw;

        public List<MaterialNode> Children { get; } = new List<MaterialNode>();
    }

    // ---------------------------------------------------------------------------------------
    // Colour tree (type 2)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Colour raster tree: per node one block-compressed colour tile (the baked terrain colour).
    /// <para>
    /// Provenance: BFII_EXACT, 2026-09-23 — consumes all 35 shipped colour records exactly (4 of the 39
    /// terrains ship no colour tree at all, so the record is optional). Shape: FIFA's
    /// <c>TerrainColorTreePseudoPipeline</c> writer (FIFA21_SOURCE). Tile 260 = 256 + 2*2 border;
    /// <see cref="TextureFormat"/> 54 = BC1 (8 bytes/block), 60 = BC3 (16 bytes/block), both measured by
    /// exact consumption and by decoding a tile to a coherent image. Colour is stored B,G,R: FIFA's
    /// writer swaps red and blue, and the Tatooine tile only reads as desert with the swap undone.
    /// </para>
    /// </summary>
    public sealed class ColorTree : RasterTreeBody
    {
        public override RasterTreeType Type => RasterTreeType.Color;

        public const uint FormatBc1 = 54;
        public const uint FormatBc3 = 60;

        public uint TileSamplesPerSide;
        public uint TileBorderWidth;
        public uint ResourceAtlasSampleCountX;
        public uint ResourceAtlasSampleCountY;
        public int Blurriness;
        public AxisAlignedBox2 TreeCoverage;
        public uint TextureFormat;
        public uint ChannelCount;
        public uint NodeCount;
        public uint PersistentNodeCount;

        public ColorNode? Root;

        public const int HeaderSizeBytes = 52; // 5x4 + 16 + 4x4

        public static int BytesPerBlock(uint format) =>
            format == FormatBc1 ? 8 : format == FormatBc3 ? 16 : 0;

        /// <summary>Bytes of one node tile; 0 for a format this reader does not know.</summary>
        public int TileBytes
        {
            get
            {
                int bpb = BytesPerBlock(TextureFormat);
                long blocks = (TileSamplesPerSide + 3) / 4;
                return bpb == 0 ? 0 : checked((int)(blocks * blocks * bpb));
            }
        }

        public IEnumerable<ColorNode> EnumerateNodes()
        {
            if (Root is null) yield break;
            var stack = new Stack<ColorNode>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }
    }

    /// <summary>
    /// Colour node: <c>u8 skipped, u8 hasData</c>; a node that is neither ends there (the runtime treats
    /// it as disabled); otherwise <c>u8 persistent</c>, the tile when persistent and data-bearing, and
    /// <c>u8 hasChildren</c>.
    /// </summary>
    public sealed class ColorNode
    {
        public QuadtreeNodeId Id;
        public byte SkippedRaw;
        public byte HasDataRaw;
        public bool TailRead;
        public byte PersistentRaw;
        public byte[]? Tile;
        public byte HasChildrenRaw;
        public List<ColorNode> Children { get; } = new List<ColorNode>();

        public bool Skipped => SkippedRaw != 0;
        public bool HasData => HasDataRaw != 0;
        public bool Persistent => PersistentRaw != 0;
        public bool HasChildren => HasChildrenRaw != 0;

        public int RecordOffset;
    }

    // ---------------------------------------------------------------------------------------
    // Destruction depth tree (type 4)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Destruction depth raster tree: per node a 4-bit depth raster, RLE-compressed per line.
    /// <para>
    /// Provenance: BFII_EXACT, 2026-09-23 — consumes all 39 shipped records exactly (1,331 data nodes,
    /// 259,594 lines, every line re-encodes byte-identically). Shape: FIFA's
    /// <c>DestructionDepthTreePseudoPipeline</c> writer (FIFA21_SOURCE) with no BFII difference: the
    /// header carries the node border width, and a data node has no extra byte. A decoded line holds
    /// <c>nodeSamplesPerSide + 1</c> samples (138 for a 137 tile: the writer over-fetches one column).
    /// </para>
    /// </summary>
    public sealed class DestructionDepthTree : RasterTreeBody
    {
        public override RasterTreeType Type => RasterTreeType.DestructionDepth;

        public uint NodeSamplesPerSide;
        public int BlurrinessLog2;
        public AxisAlignedBox2 TreeCoverage;
        public uint NodeCount;
        public uint PersistentNodeCount;
        public uint LevelMax;
        public uint NodeBorderWidth;

        public DestructionDepthNode? Root;

        public const int HeaderSizeBytes = 40; // 4 + 4 + 16 + 16

        /// <summary>Bytes per RLE line once decoded: (samples + 1) / 2.</summary>
        public int PackedLineBytes => ((int)NodeSamplesPerSide + 1) / 2;

        public IEnumerable<DestructionDepthNode> EnumerateNodes()
        {
            if (Root is null) yield break;
            var stack = new Stack<DestructionDepthNode>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
            }
        }
    }

    public sealed class DestructionDepthNode : RleRasterNode
    {
        public List<DestructionDepthNode> Children { get; } = new List<DestructionDepthNode>();
    }

    // ---------------------------------------------------------------------------------------
    // Streaming nodes
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// One streaming node. The GUIDs are Frostbite <b>chunk ids</b>; together they are the terrain's
    /// subordinate chunk closure. Provenance: BFII_EXACT — W01 §3.6, §4.4.
    /// <para>
    /// Glacier's parser stops before this section entirely, so anything built on Glacier alone never
    /// sees the chunk closure (W01 evidence row 52).
    /// </para>
    /// </summary>
    public sealed class StreamingNode
    {
        public QuadtreeNodeId Id;

        public uint Lod0Size;
        public Guid Lod0Id;

        public byte Lod1EnabledRaw;
        public uint Lod1Size;
        public Guid Lod1Guid;

        public byte PersistentDedicatedServerRaw;
        public byte HasChildrenRaw;

        public List<StreamingNode> Children { get; } = new List<StreamingNode>();

        public bool Lod1Enabled => Lod1EnabledRaw != 0;
        public bool PersistentDedicatedServer => PersistentDedicatedServerRaw != 0;
        public bool HasChildren => HasChildrenRaw != 0;

        public int RecordOffset;
    }
}
