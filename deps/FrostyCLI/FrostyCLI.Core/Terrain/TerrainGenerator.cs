#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\TerrainGenerator.cs (sha256:140C25E4F2E679D7).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Linq;
using Bfii.TerrainFormat;

namespace Bfii.TerrainBuild
{
    public sealed class TerrainGeneratorInput
    {
        public TerrainFrame Frame = new TerrainFrame();
        public IHeightSource Heights = null!;
        public HeightfieldBuildOptions HeightOptions = new HeightfieldBuildOptions();

        /// <summary>Layer alphas by the host's layer index (the host's TerrainData and layer combinations own the look).</summary>
        public ILayerSource Layers = new BaseOnlyLayerSource();
        public MaskBuildOptions MaskOptions = new MaskBuildOptions();

        /// <summary>Null writes no colour tree (4 shipped SP terrains ship none).</summary>
        public IColorSource? Color = new ConstantColorSource(128, 128, 128);
        public ColorBuildOptions ColorOptions = new ColorBuildOptions();

        public IMaterialSource Material = new ConstantMaterialSource(0);
        /// <summary>Material tree data level; the raster is 256 indices per node side.</summary>
        public int MaterialLevel = 3;
        public List<uint> MaterialDecls = new List<uint>();
        public uint BackgroundMaterialDecl = 0x800;
    }

    public sealed class TerrainGeneratorReport
    {
        public HeightfieldBuildReport Heightfield = new HeightfieldBuildReport();
        public MaskBuildReport Mask = new MaskBuildReport();
        public int ColorNodes, MaterialNodes, StreamingNodes;
        public int PayloadBytes;

        public override string ToString()
        {
            var hf = Heightfield;
            return $"heightfield: {hf.DataNodes} data nodes (" +
                   string.Join(" ", hf.DataNodesPerLevel.Select((c, i) => (c, i)).Where(x => x.c > 0).Select(x => $"L{x.i}={x.c}")) +
                   $"), skipped {hf.SkippedNodes}, holes {hf.HoleNodes}, atlas {hf.Atlas}\n" +
                   $"mask: {Mask.Nodes} nodes to L{Mask.MaxLevelUsed}, {Mask.Subtiles} subtiles ({Mask.TiledSubtiles} tiled, {Mask.FullSubtiles} full), atlas {Mask.Atlas}, layer sets {string.Join(" ", Mask.LayerSets)}\n" +
                   $"colour nodes {ColorNodes}, material nodes {MaterialNodes}, streaming nodes {StreamingNodes}, payload {PayloadBytes:N0} B";
        }
    }

    /// <summary>
    /// A complete <c>TerrainStreamingTree</c> from source rasters: heightfield, mask, colour, material and an empty
    /// destruction-depth tree, assembled with the streaming tree as the union (SPEC §1-§7).
    /// </summary>
    public static class TerrainGenerator
    {
        public static byte[] Generate(TerrainGeneratorInput input, out TerrainGeneratorReport report)
        {
            var rep = new TerrainGeneratorReport();
            report = rep;
            var f = input.Frame;
            f.Validate();

            var hf = HeightfieldBuilder.Build(input.Heights, f, input.HeightOptions, out rep.Heightfield);
            var mask = MaskBuilder.Build(input.Layers, f, input.MaskOptions, out rep.Mask);
            ColorTree? color = input.Color == null ? null : ColorBuilder.Build(input.Color, f, input.ColorOptions);
            var decls = input.MaterialDecls.Count > 0 ? input.MaterialDecls : new List<uint> { input.BackgroundMaterialDecl };
            var material = MaterialBuilder.Build(input.Material, f, input.MaterialLevel, decls, input.BackgroundMaterialDecl);
            var dd = DestructionDepthBuilder.Empty(f);

            var res = TerrainAssembler.Assemble(hf, mask, color, material, dd);
            rep.ColorNodes = color == null ? 0 : (int)color.NodeCount;
            rep.MaterialNodes = (int)material.NodeCount;
            rep.StreamingNodes = (int)res.Header.NodeCount;
            var bytes = TerrainAssembler.Serialize(res);
            rep.PayloadBytes = bytes.Length;

            // The output must parse back structurally, every record exact.
            var check = TerrainStreamingTreeParser.Parse(bytes, new ParseOptions { Framing = PayloadFraming.Bare });
            if (!check.Success) throw new InvalidOperationException("generated payload does not re-parse: " + check.Failure);
            foreach (var r in check.Resource!.RasterTrees)
                if (r.Body is OpaqueRasterTree || r.Slack.Length != 0)
                    throw new InvalidOperationException("generated " + r.Type + " tree does not re-parse exactly");
            return bytes;
        }
    }
}
