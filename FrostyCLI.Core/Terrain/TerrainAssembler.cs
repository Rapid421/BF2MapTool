#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\TerrainAssembler.cs (sha256:F1F83F30DED4FD17).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Linq;
using Bfii.TerrainFormat;

namespace Bfii.TerrainBuild
{
    /// <summary>
    /// Puts raster trees together into a complete <c>TerrainStreamingTree</c> resource.
    /// <para>
    /// The streaming tree is the union of the heightfield, mask and colour trees' nodes, every node all-zero (no
    /// chunks: everything persistent). BFII_EXACT on every shipped MP terrain (17/17): the streaming tree equals that
    /// union exactly and every streaming node is zero; the material and destruction-depth trees are not streamed and
    /// may run deeper (SPEC §7). The runtime walks the streaming tree in lockstep with each raster tree, so a raster
    /// subtree the streaming tree does not reach is never used.
    /// </para>
    /// </summary>
    public static class TerrainAssembler
    {
        public static TerrainStreamingTreeResource Assemble(HeightfieldTree heightfield, MaskTree mask,
            ColorTree? color, MaterialTree material, DestructionDepthTree destructionDepth,
            StreamingTreeHeader? headerTemplate = null)
        {
            if (heightfield is null) throw new ArgumentNullException(nameof(heightfield));
            if (mask is null) throw new ArgumentNullException(nameof(mask));
            if (material is null) throw new ArgumentNullException(nameof(material));
            if (destructionDepth is null) throw new ArgumentNullException(nameof(destructionDepth));

            var res = new TerrainStreamingTreeResource();
            var h = res.Header;
            if (headerTemplate != null)
            {
                h.UnblurredSamplesPerNodeSidePot = headerTemplate.UnblurredSamplesPerNodeSidePot;
                h.TrackTextureDetailFalloffRaw = headerTemplate.TrackTextureDetailFalloffRaw;
                h.InvisibleDetailReductionFactor = headerTemplate.InvisibleDetailReductionFactor;
                h.OccludedDetailReductionFactor = headerTemplate.OccludedDetailReductionFactor;
                h.Field0x0D = headerTemplate.Field0x0D;
                h.Field0x11 = headerTemplate.Field0x11;
                h.FreeStreamingEnableRaw = headerTemplate.FreeStreamingEnableRaw;
            }
            else
            {
                // Every shipped streaming tree (39/39) carries these.
                h.UnblurredSamplesPerNodeSidePot = 256;
                h.TrackTextureDetailFalloffRaw = 1;
                h.InvisibleDetailReductionFactor = F32.FromValue(2.0f);
                h.OccludedDetailReductionFactor = F32.FromValue(5.0f);
                h.Field0x0D = 0;       // resourceBlurriness
                h.Field0x11 = 2;       // skipLevels
                h.FreeStreamingEnableRaw = 1;
            }

            res.RasterTrees.Add(Record(heightfield));
            res.RasterTrees.Add(Record(mask));
            if (color != null) res.RasterTrees.Add(Record(color));
            res.RasterTrees.Add(Record(material));
            res.RasterTrees.Add(Record(destructionDepth));

            res.StreamingRoot = BuildStreamingTree(heightfield, mask, color);
            h.NodeCount = (uint)res.EnumerateStreamingNodes().Count();
            return res;
        }

        public static byte[] Serialize(TerrainStreamingTreeResource res) =>
            TerrainStreamingTreeSerializer.Serialize(res, SerializeOptions.Mutating);

        private static RasterTreeRecord Record(RasterTreeBody body) =>
            new RasterTreeRecord { Type = body.Type, Body = body };

        /// <summary>The streaming node tree over the union of the three streamable trees' nodes.</summary>
        public static StreamingNode BuildStreamingTree(HeightfieldTree heightfield, MaskTree mask, ColorTree? color)
        {
            var withChildren = new HashSet<QuadtreeNodeId>();
            var present = new HashSet<QuadtreeNodeId>();
            foreach (var n in heightfield.EnumerateNodes()) { present.Add(n.Id); if (n.HasChildren) withChildren.Add(n.Id); }
            foreach (var n in mask.EnumerateNodes()) { present.Add(n.Id); if (n.HasChildren) withChildren.Add(n.Id); }
            if (color != null)
                foreach (var n in color.EnumerateNodes()) { present.Add(n.Id); if (n.HasChildren) withChildren.Add(n.Id); }
            return Node(new QuadtreeNodeId(0, 0, 0));

            StreamingNode Node(QuadtreeNodeId id)
            {
                var s = new StreamingNode { Id = id };
                if (withChildren.Contains(id))
                {
                    s.HasChildrenRaw = 1;
                    for (int ci = 0; ci < 4; ci++) s.Children.Add(Node(id.Child(ci)));
                }
                return s;
            }
        }

        /// <summary>
        /// Replace one raster tree of an existing resource and rebuild the streaming tree around it. The other trees
        /// keep their bytes; record lengths are recomputed on serialise.
        /// </summary>
        public static TerrainStreamingTreeResource ReplaceTree(TerrainStreamingTreeResource res, RasterTreeBody replacement)
        {
            var rec = res.RasterTrees.FirstOrDefault(r => r.Type == replacement.Type);
            if (rec == null) throw new ArgumentException("the resource has no " + replacement.Type + " tree to replace");
            rec.Body = replacement;
            rec.Slack = Array.Empty<byte>();
            var hf = res.Heightfield ?? throw new InvalidOperationException("no heightfield tree");
            var mask = res.Mask ?? throw new InvalidOperationException("no mask tree");
            res.StreamingRoot = BuildStreamingTree(hf, mask, res.Color);
            res.Header.NodeCount = (uint)res.EnumerateStreamingNodes().Count();
            return res;
        }
    }
}
