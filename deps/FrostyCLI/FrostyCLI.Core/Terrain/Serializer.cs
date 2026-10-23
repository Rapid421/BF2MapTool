#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainFormat\Serializer.cs (sha256:39B9954DA35E9930).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;

namespace Bfii.TerrainFormat
{
    public sealed class SerializeOptions
    {
        /// <summary>
        /// When true, <c>rasterTreeLoadSize</c> is back-patched from what was actually written
        /// instead of echoing the parsed <c>DeclaredSize</c>. Identity round-trip (D1) uses false;
        /// any mutation that changes a length must use true.
        /// <para>
        /// With false the writer still <i>checks</i> the recomputed value against the declared one
        /// when <see cref="Strict"/> is set, which is assertion B1 enforced from the write side.
        /// </para>
        /// </summary>
        public bool RecomputeRasterTreeSizes { get; set; }

        /// <summary>
        /// Throw when the model is internally inconsistent (declared counts disagreeing with list
        /// lengths, a declared record size disagreeing with the bytes written). On by default: a
        /// silent disagreement here is exactly the class of bug the round-trip exists to catch.
        /// </summary>
        public bool Strict { get; set; } = true;

        /// <summary>
        /// Re-emit the 16-byte <c>resMeta</c> prefix when the parsed resource carried one, so the
        /// output can be compared against the input file as given (plan §10 C-07).
        /// </summary>
        public bool IncludeResMetaPrefix { get; set; } = true;

        public static SerializeOptions Identity => new SerializeOptions();

        public static SerializeOptions Mutating =>
            new SerializeOptions { RecomputeRasterTreeSizes = true };
    }

    /// <summary>
    /// Serializer for the BFII <c>TerrainStreamingTree</c> RES payload. Exact inverse of
    /// <see cref="TerrainStreamingTreeParser"/>: every branch mirrors the parse branch, and the raw
    /// flag bytes and float bit patterns the parser preserved are written back unmodified.
    /// </summary>
    public static class TerrainStreamingTreeSerializer
    {
        public static byte[] Serialize(TerrainStreamingTreeResource res, SerializeOptions? options = null)
        {
            if (res is null) throw new ArgumentNullException(nameof(res));
            options ??= SerializeOptions.Identity;

            var w = new ByteWriter(1 << 16);

            if (options.IncludeResMetaPrefix && res.ResMetaPrefix is { Length: BfiiProfile.ResMetaSize })
                w.WriteBytes(res.ResMetaPrefix);

            var h = res.Header;
            w.WriteUInt32(h.UnblurredSamplesPerNodeSidePot);
            w.WriteByte(h.TrackTextureDetailFalloffRaw);
            w.WriteF32(h.InvisibleDetailReductionFactor);
            w.WriteF32(h.OccludedDetailReductionFactor);
            w.WriteUInt32(h.Field0x0D);
            w.WriteUInt32(h.Field0x11);
            w.WriteUInt32(h.NodeCount);
            w.WriteByte(h.FreeStreamingEnableRaw);

            foreach (var record in res.RasterTrees)
            {
                w.WriteByte((byte)record.Type);
                int sizeOffset = w.Length;
                w.WriteUInt32(record.DeclaredSize);
                int bodyStart = w.Length;

                WriteRasterTreeBody(w, record, options);
                w.WriteBytes(record.Slack);

                uint written = (uint)(w.Length - bodyStart);
                if (options.RecomputeRasterTreeSizes)
                {
                    w.PatchUInt32(sizeOffset, written);
                }
                else if (options.Strict && written != record.DeclaredSize)
                {
                    throw new TerrainFormatException(
                        $"raster tree {record.Type} wrote {written} byte(s) but declares " +
                        $"{record.DeclaredSize}; re-serialise is not the inverse of the parse " +
                        "(assertion B1 fails from the write side)");
                }
            }

            w.WriteByte(res.Terminator);

            if (res.StreamingRoot != null) WriteStreamingNode(w, res.StreamingRoot);
            w.WriteBytes(res.TrailingBytes);

            return w.ToArray();
        }

        private static void WriteRasterTreeBody(ByteWriter w, RasterTreeRecord record, SerializeOptions o)
        {
            switch (record.Body)
            {
                case HeightfieldTree hf: WriteHeightfieldTree(w, hf, o); return;
                case MaskTree mask: WriteMaskTree(w, mask, o); return;
                case ColorTree col: WriteColorTree(w, col, o); return;
                case MaterialTree mat: WriteMaterialTree(w, mat, o); return;
                case DestructionDepthTree dd: WriteDestructionDepthTree(w, dd, o); return;
                case OpaqueRasterTree op: w.WriteBytes(op.Payload); return;
                case null:
                    throw new TerrainFormatException(
                        $"raster tree record of type {record.Type} has no body to serialise");
                default:
                    throw new TerrainFormatException(
                        $"unhandled raster tree body {record.Body.GetType().Name}");
            }
        }

        // -----------------------------------------------------------------------------------

        private static void WriteHeightfieldTree(ByteWriter w, HeightfieldTree t, SerializeOptions o)
        {
            w.WriteUInt32(t.NodeSamplesPerSide);
            w.WriteUInt32(t.ResourceAtlasSampleCountX);
            w.WriteUInt32(t.ResourceAtlasSampleCountY);
            w.WriteInt32(t.BlurrinessLog2);
            w.WriteF32(t.WorldSizeY);
            w.WriteF32(t.PhysicsMetersPerSample);
            w.WriteInt32(t.PhysicsCropWidth);
            w.WriteByte(t.Unknown0x1DRaw);
            w.WriteF32(t.Unknown0x1E);
            w.WriteUInt32(t.MinMaxStackDepth);
            w.WriteUInt32(t.OccluderGridStackDepth);
            w.WriteUInt32(t.DensityMapNodeSamplesPerSide);
            w.WriteUInt32(t.DensityMapBorderWidth);
            w.WriteUInt32(t.DensityMapNodeSamplesPerSidePot);
            w.WriteF32(t.DensityMapResolutionRatio);
            w.WriteUInt32(t.NodeCount);
            w.WriteUInt32(t.PersistentNodeCount);
            w.WriteUInt32(t.PersistentDedicatedServerNodeCount);
            w.WriteUInt32(t.SkippedNodeCount);
            w.WriteUInt32(t.NodeBorderWidth);

            if (t.Root != null) WriteHeightfieldNode(w, t, t.Root, t.Flow, o);
        }

        private static void WriteHeightfieldNode(ByteWriter w, HeightfieldTree t, HeightfieldNode n,
            HeightfieldNodeFlow flow, SerializeOptions o)
        {
            w.WriteBox(n.BoundingBox);

            w.WriteByte(n.DisabledRaw);
            if (n.Disabled) return;

            w.WriteByte(n.SkippedRaw);
            w.WriteByte(n.HasDataRaw);

            if (flow == HeightfieldNodeFlow.Merged && !n.HasData && !n.Skipped) return;

            w.WriteByte(n.ContainsHolesRaw);
            w.WriteByte(n.PersistentRaw);

            if (flow == HeightfieldNodeFlow.Merged)
            {
                if (n.HasData && n.Persistent) WritePayload(w, t, n, o);
                else if (n.Skipped) WriteSkippedMinMax(w, t, n, o);
            }
            else
            {
                if (n.Skipped) WriteSkippedMinMax(w, t, n, o);
                if (n.HasData && n.Persistent) WritePayload(w, t, n, o);
            }

            w.WriteByte(n.HasChildrenRaw);
            if (!n.HasChildren) return;

            if (o.Strict && n.Children.Count != 4)
                throw new TerrainFormatException(
                    $"heightfield node {n.Id} sets hasChildren but carries {n.Children.Count} child(ren), not 4");

            foreach (var c in n.Children) WriteHeightfieldNode(w, t, c, flow, o);
        }

        private static void WriteSkippedMinMax(ByteWriter w, HeightfieldTree t, HeightfieldNode n,
            SerializeOptions o)
        {
            var block = n.SkippedMinMax ?? Array.Empty<byte>();
            if (o.Strict && block.Length != t.MinMaxBytes)
                throw new TerrainFormatException(
                    $"heightfield node {n.Id} skipped min/max block is {block.Length} byte(s), " +
                    $"header implies {t.MinMaxBytes}");
            w.WriteBytes(block);
        }

        private static void WritePayload(ByteWriter w, HeightfieldTree t, HeightfieldNode n,
            SerializeOptions o)
        {
            var p = n.Payload;
            if (p is null)
                throw new TerrainFormatException(
                    $"heightfield node {n.Id} is data-bearing and persistent but has no payload");

            if (o.Strict)
            {
                Check(p.AtlasData.Length, t.AtlasBytes, "atlasData", n.Id);
                Check(p.MinMaxStackData.Length, t.MinMaxBytes, "minMaxStackData", n.Id);
                Check(p.OccluderGridStackData.Length, t.OccluderBytes, "occluderGridStackData", n.Id);
                Check(p.DensityData.Length, t.DensityBytes, "densityData", n.Id);
            }

            w.WriteBytes(p.AtlasData);
            w.WriteBytes(p.MinMaxStackData);
            w.WriteBytes(p.OccluderGridStackData);
            w.WriteBytes(p.DensityData);

            static void Check(int actual, int expected, string what, QuadtreeNodeId id)
            {
                if (actual != expected)
                    throw new TerrainFormatException(
                        $"heightfield node {id} {what} is {actual} byte(s), header implies {expected}");
            }
        }

        // -----------------------------------------------------------------------------------

        private static void WriteMaskTree(ByteWriter w, MaskTree t, SerializeOptions o)
        {
            w.WriteUInt32(t.TileSamplesPerSide);
            w.WriteUInt32(t.TilesPerNodeSide);
            w.WriteUInt32(t.SubtileRelativeLevel);
            w.WriteUInt32(t.ResourceAtlasSampleCountX);
            w.WriteUInt32(t.ResourceAtlasSampleCountY);
            w.WriteInt32(t.BlurrinessLog2);
            w.WriteBox2(t.TreeCoverage);
            w.WriteUInt32(t.NodeCount);
            w.WriteUInt32(t.SubtileCount);
            w.WriteUInt32(t.PersistentNonTrivialSubtileCount);
            w.WriteUInt32(t.CoverageGridLevelCount);

            if (t.Root != null) WriteMaskNode(w, t, t.Root, o);
        }

        private static void WriteMaskNode(ByteWriter w, MaskTree t, MaskNode n, SerializeOptions o)
        {
            w.WriteByte(n.PrunedRaw);
            w.WriteUInt16(n.SubtileCount);
            w.WriteUInt16(n.NonTrivialSubtileCount);
            w.WriteUInt16(n.NonTrivialPersistentSubtileCountRecursive);

            if (n.Pruned || n.SubtileCount == 0) return;

            if (o.Strict && n.Subtiles.Count != n.SubtileCount)
                throw new TerrainFormatException(
                    $"mask node {n.Id} declares {n.SubtileCount} subtile(s) but carries {n.Subtiles.Count}");

            int stride = t.Stride;
            foreach (var s in n.Subtiles)
            {
                if (o.Strict && s.HighResolutionCoverageGridStack.Length != stride)
                    throw new TerrainFormatException(
                        $"mask node {n.Id} subtile coverage stack is " +
                        $"{s.HighResolutionCoverageGridStack.Length} byte(s), fitted stride is {stride}");

                w.WriteByte(s.TerrainLayerIndex);
                w.WriteBox2(s.WorldCoverage);
                w.WriteByte(s.IsLeafRaw);
                w.WriteByte(s.MaskDataFullRaw);
                w.WriteBytes(s.HighResolutionCoverageGridStack);
            }

            w.WriteByte(n.PersistentRaw);
            if (n.Persistent)
            {
                w.WriteUInt32(n.TileDataBytes);
                if (o.Strict && n.Tiles.Count != n.DerivedNonTrivialSubtileCount)
                    throw new TerrainFormatException(
                        $"mask node {n.Id} carries {n.Tiles.Count} tile(s) but " +
                        $"{n.DerivedNonTrivialSubtileCount} subtile(s) are non-trivial");

                int tileBytes = t.TileBytes;
                foreach (var tile in n.Tiles)
                {
                    if (o.Strict && tile.Length != tileBytes)
                        throw new TerrainFormatException(
                            $"mask node {n.Id} tile is {tile.Length} byte(s), header implies {tileBytes}");
                    w.WriteBytes(tile);
                }
                if (o.Strict && n.TileDataBytes != (long)n.Tiles.Count * tileBytes)
                    throw new TerrainFormatException(
                        $"mask node {n.Id} declares {n.TileDataBytes} tile byte(s) but carries {n.Tiles.Count} tile(s)");
            }

            w.WriteByte(n.HasChildrenRaw);
            if (!n.HasChildren) return;

            if (o.Strict && n.Children.Count != 4)
                throw new TerrainFormatException(
                    $"mask node {n.Id} sets hasChildren but carries {n.Children.Count} child(ren), not 4");

            foreach (var c in n.Children) WriteMaskNode(w, t, c, o);
        }

        // -----------------------------------------------------------------------------------

        private static void WriteMaterialTree(ByteWriter w, MaterialTree t, SerializeOptions o)
        {
            w.WriteUInt32(t.NodeSamplesPerSide);
            w.WriteInt32(t.BlurrinessLog2);
            w.WriteBox2(t.TreeCoverage);
            w.WriteUInt32(t.NodeCount);
            w.WriteUInt32(t.PersistentNodeCount);
            w.WriteUInt32(t.LevelMax);

            if (t.Root != null) WriteMaterialNode(w, t, t.Root, o);

            w.WriteUInt32((uint)t.MaterialDecls.Count);
            foreach (uint v in t.MaterialDecls) w.WriteUInt32(v);
            w.WriteUInt32(t.BackgroundMaterialDecl);
        }

        private static void WriteMaterialNode(ByteWriter w, MaterialTree t, MaterialNode n,
            SerializeOptions o)
        {
            w.WriteByte(n.HasDataRaw);
            w.WriteByte(n.PersistentRaw);

            if (n.CarriesData)
            {
                w.WriteByte(n.EncodingRaw);
                WriteRleBody(w, n, t.NodeSamplesPerSide, "material", o);
            }

            w.WriteByte(n.HasChildrenRaw);
            if (!n.HasChildren) return;

            if (o.Strict && n.Children.Count != 4)
                throw new TerrainFormatException(
                    $"material node {n.Id} sets hasChildren but carries {n.Children.Count} child(ren), not 4");

            foreach (var c in n.Children) WriteMaterialNode(w, t, c, o);
        }

        private static void WriteRleBody(ByteWriter w, RleRasterNode n, uint lines, string what, SerializeOptions o)
        {
            if (o.Strict && n.DeclaredRleSize != n.RleData.Length)
                throw new TerrainFormatException(
                    $"{what} node {n.Id} declares {n.DeclaredRleSize} RLE byte(s) but carries {n.RleData.Length}");
            if (o.Strict && n.LineSizes.Length != lines)
                throw new TerrainFormatException(
                    $"{what} node {n.Id} carries {n.LineSizes.Length} line size(s), header implies {lines}");

            w.WriteInt32(n.DeclaredRleSize);
            w.WriteBytes(n.RleData);
            foreach (ushort v in n.LineSizes) w.WriteUInt16(v);
        }

        // -----------------------------------------------------------------------------------

        private static void WriteColorTree(ByteWriter w, ColorTree t, SerializeOptions o)
        {
            w.WriteUInt32(t.TileSamplesPerSide);
            w.WriteUInt32(t.TileBorderWidth);
            w.WriteUInt32(t.ResourceAtlasSampleCountX);
            w.WriteUInt32(t.ResourceAtlasSampleCountY);
            w.WriteInt32(t.Blurriness);
            w.WriteBox2(t.TreeCoverage);
            w.WriteUInt32(t.TextureFormat);
            w.WriteUInt32(t.ChannelCount);
            w.WriteUInt32(t.NodeCount);
            w.WriteUInt32(t.PersistentNodeCount);

            if (t.Root != null) WriteColorNode(w, t, t.Root, o);
        }

        private static void WriteColorNode(ByteWriter w, ColorTree t, ColorNode n, SerializeOptions o)
        {
            w.WriteByte(n.SkippedRaw);
            w.WriteByte(n.HasDataRaw);
            if (!n.HasData && !n.Skipped) return;

            w.WriteByte(n.PersistentRaw);
            if (n.Persistent && n.HasData)
            {
                var tile = n.Tile ?? Array.Empty<byte>();
                if (o.Strict && tile.Length != t.TileBytes)
                    throw new TerrainFormatException(
                        $"colour node {n.Id} tile is {tile.Length} byte(s), header implies {t.TileBytes}");
                w.WriteBytes(tile);
            }

            w.WriteByte(n.HasChildrenRaw);
            if (!n.HasChildren) return;

            if (o.Strict && n.Children.Count != 4)
                throw new TerrainFormatException(
                    $"colour node {n.Id} sets hasChildren but carries {n.Children.Count} child(ren), not 4");

            foreach (var c in n.Children) WriteColorNode(w, t, c, o);
        }

        // -----------------------------------------------------------------------------------

        private static void WriteDestructionDepthTree(ByteWriter w, DestructionDepthTree t, SerializeOptions o)
        {
            w.WriteUInt32(t.NodeSamplesPerSide);
            w.WriteInt32(t.BlurrinessLog2);
            w.WriteBox2(t.TreeCoverage);
            w.WriteUInt32(t.NodeCount);
            w.WriteUInt32(t.PersistentNodeCount);
            w.WriteUInt32(t.LevelMax);
            w.WriteUInt32(t.NodeBorderWidth);

            if (t.Root != null) WriteDestructionDepthNode(w, t, t.Root, o);
        }

        private static void WriteDestructionDepthNode(ByteWriter w, DestructionDepthTree t, DestructionDepthNode n,
            SerializeOptions o)
        {
            w.WriteByte(n.HasDataRaw);
            w.WriteByte(n.PersistentRaw);
            if (n.CarriesData) WriteRleBody(w, n, t.NodeSamplesPerSide, "destruction depth", o);

            w.WriteByte(n.HasChildrenRaw);
            if (!n.HasChildren) return;

            if (o.Strict && n.Children.Count != 4)
                throw new TerrainFormatException(
                    $"destruction depth node {n.Id} sets hasChildren but carries {n.Children.Count} child(ren), not 4");

            foreach (var c in n.Children) WriteDestructionDepthNode(w, t, c, o);
        }

        // -----------------------------------------------------------------------------------

        private static void WriteStreamingNode(ByteWriter w, StreamingNode n)
        {
            w.WriteUInt32(n.Lod0Size);
            w.WriteGuid(n.Lod0Id);

            w.WriteByte(n.Lod1EnabledRaw);
            if (n.Lod1Enabled)
            {
                w.WriteUInt32(n.Lod1Size);
                w.WriteGuid(n.Lod1Guid);
            }

            w.WriteByte(n.PersistentDedicatedServerRaw);
            w.WriteByte(n.HasChildrenRaw);

            if (!n.HasChildren) return;
            foreach (var c in n.Children) WriteStreamingNode(w, c);
        }
    }
}
