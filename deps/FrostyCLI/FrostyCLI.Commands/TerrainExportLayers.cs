using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bfii.TerrainBuild;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    // `terrain export-layers` -- a terrain's layer masks, colour map and material map as images: one grey PNG per painted
    // layer (its alpha in the renderer's ordered stack), colour.png from the colour tree, material.png (4-bit material
    // indices, the material tree's own cells), and a layers.json that `terrain create` reads back ("weights": "alpha",
    // the tree depths). Layers have no names in BFII -- a layer is its index in TerrainData.TerrainLayers -- so this is
    // how you find out which index is sand and which is rock before painting your own. At --size = the mask's native
    // resolution (2^(level + 2) * 64 across) the pixels are the mask tiles' own texel centres, and the json's rectangle
    // is the rectangle of those centres, so `terrain create` samples every texel back where it came from.
    public partial class TerrainCommand
    {
        private static int ExecuteExportLayers(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            const string label = "terrain export-layers";
            string spec = null, outDir = null;
            int size = 1024, level = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
                else if (args[i] == "--size" && i + 1 < args.Length) int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                else if (args[i] == "--level" && i + 1 < args.Length) int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out level);
                else if (!args[i].StartsWith("--")) spec = args[i];
            }
            if (outDir == null) { Console.Error.WriteLine("usage: frostycli terrain export-layers <terrain> --out <dir> [--size 1024] [--level L]"); return 1; }
            if (size < 16 || size > 8192) { Console.Error.WriteLine(label + ": --size must be 16..8192"); return 1; }

            ResAssetEntry entry; string source;
            byte[] payload = Load(label, spec, out entry, out source);
            if (payload == null) return 1;
            var parsed = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(payload);
            if (!parsed.Success || parsed.Resource.Mask == null) { Console.Error.WriteLine(label + ": no mask tree: " + parsed.Failure); return 1; }
            var res = parsed.Resource;
            var reader = new MaskReader(res.Mask);
            if (level < 0) level = reader.DeepestLevel;
            if (DryRun.Active) { DryRun.Report(logger, "export {0} layer(s) at {1}x{1} to {2}", reader.Layers.Count, size, outDir); return 0; }
            Directory.CreateDirectory(outDir);

            var cov = res.Mask.TreeCoverage;
            double x0 = cov.MinX.Value, z0 = cov.MinY.Value, x1 = cov.MaxX.Value, z1 = cov.MaxY.Value;
            double halfX = (x1 - x0) / size / 2, halfZ = (z1 - z0) / size / 2;   // pixel centres (a vertex grid for create)
            var maps = reader.Render(reader.Layers, size, level);
            var layersJson = new JArray();
            var summary = new List<string>();
            foreach (int layer in reader.Layers)
            {
                byte[] m = maps[layer];
                string file = "layer_" + layer.ToString("D2", CultureInfo.InvariantCulture) + ".png";
                PngWriter.Write(Path.Combine(outDir, file), size, size, 1, m);
                long painted = m.LongCount(v => v > 0);
                double pct = 100.0 * painted / m.Length;
                summary.Add(layer + ":" + pct.ToString("0.#", CultureInfo.InvariantCulture) + "%");
                var l = new JObject { ["name"] = "layer" + layer, ["hostLayer"] = layer, ["coveragePercent"] = Math.Round(pct, 2) };
                if (layersJson.Count > 0) l["mask"] = file;     // the lowest layer is the opaque base
                layersJson.Add(l);
            }
            string colourFile = null;
            if (res.Color != null)
            {
                colourFile = "colour.png";
                PngWriter.Write(Path.Combine(outDir, colourFile), size, size, 3, ColorReader.Render(res.Color, size));
            }
            int colorLevel = -1;
            if (res.Color != null)
                foreach (var cn in res.Color.EnumerateNodes()) if (cn.HasData && cn.Tile != null) colorLevel = Math.Max(colorLevel, cn.Id.Level);
            string materialFile = null; int materialSize = 0;
            if (res.Material != null && res.Material.Root != null)
            {
                materialSize = MaterialReader.NativeSize(res.Material);
                if (materialSize >= 16 && materialSize <= 16384)
                {
                    materialFile = "material.png";
                    PngWriter.Write(Path.Combine(outDir, materialFile), materialSize, materialSize, 1, MaterialReader.Render(res.Material, materialSize));
                }
            }
            var j = new JObject
            {
                ["source"] = source,
                ["weights"] = "alpha",
                ["worldMinX"] = x0 + halfX, ["worldMaxX"] = x1 - halfX, ["worldMinZ"] = z0 + halfZ, ["worldMaxZ"] = z1 - halfZ,
                ["rowOrder"] = "min-z-first",
                ["maskLevel"] = level,
                ["layers"] = layersJson,
            };
            if (colourFile != null) { j["colormap"] = colourFile; if (colorLevel >= 0) j["colorLevel"] = colorLevel; }
            if (materialFile != null)
            {
                var mc = res.Material.TreeCoverage;
                j["materials"] = materialFile;
                j["materialsRect"] = new JArray(mc.MinX.Value, mc.MinY.Value, mc.MaxX.Value, mc.MaxY.Value);
                j["materialLevel"] = (int)res.Material.LevelMax;
            }
            File.WriteAllText(Path.Combine(outDir, "layers.json"), j.ToString(Formatting.Indented));

            formatter.WriteResult("OK", label, new Dictionary<string, string>
            {
                { "dir", outDir },
                { "layers", string.Join(" ", summary) },
                { "size", size + "x" + size + " over " + (x1 - x0).ToString("0.##", CultureInfo.InvariantCulture) + " m (mask level " + level + ")" },
                { "colour", colourFile == null ? "(no colour tree)" : colourFile + " (tree to L" + colorLevel + ")" },
                { "material", materialFile == null ? "(no material tree)" : materialFile + " " + materialSize + "x" + materialSize + " (L" + res.Material.LevelMax + ", " + res.Material.MaterialDecls.Count + " declarations)" },
            });
            return 0;
        }
    }
}
