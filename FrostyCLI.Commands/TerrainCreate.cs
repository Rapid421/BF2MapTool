using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bfii.TerrainBuild;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    // `terrain create` -- a complete new TerrainStreamingTree from a heightmap (and optionally layer weight maps, a
    // colour map and holes), built by Bfii.TerrainBuild (vendored under FrostyCLI.Core\Terrain). Every rule the
    // generator applies is measured on the 39 shipped terrains; see F:\Frosty\projects\terrain-authoring\SPEC.md.
    //
    // The terrain renders with a host's TerrainData (its layers, layer combinations and visual terrain): the mask
    // stores host layer indices, the material tree indexes the host's material declarations. With a host the frame is
    // snapped to the host's quadtree (LOG R3: the level's baked GI lightmaps are cut along it, and a terrain patch
    // that straddles two lightmap tiles gets no indirect light -- black wherever the sun does not reach).
    public partial class TerrainCommand
    {
        private static int ExecuteCreate(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            const string label = "terrain create";
            string heightsPath = null, layersPath = null, colormapPath = null, holesPath = null, hostSpec = null;
            string outFile = null, asRes = null, reportPath = null, sizeArg = null, centerArg = null, originArg = null;
            string rangeArg = null, rowsArg = null, rawSizeArg = null, colorArg = null, hostVisualPath = null, materialsPath = null;
            double spacing = 0, worldY = 0, scale = double.NaN, offset = double.NaN, holeBelow = double.NaN, tolerance = 0.05;
            int budget = 1400, maskLevel = -1, colorLevel = -1, materialLevel = -1, maskTolerance = 2, maskBudget = 8090;
            bool noAlign = false, anyLayer = false, baseHost = false, clearDecals = false, allowSmallFrame = false;
            int holeLayerArg = -1; double holeDilate = 1.0, skirtFade = -1;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--heights": heightsPath = next; i++; break;
                    case "--layers": layersPath = next; i++; break;
                    case "--colormap": colormapPath = next; i++; break;
                    case "--color": colorArg = next; i++; break;
                    case "--holes": holesPath = next; i++; break;
                    case "--hole-below": holeBelow = Num(next); i++; break;
                    case "--host": hostSpec = next; i++; break;
                    case "--no-align": noAlign = true; break;
                    case "--allow-small-frame": allowSmallFrame = true; break;
                    case "--skirt-fade": skirtFade = Num(next); i++; break;
                    case "--any-layer": anyLayer = true; break;
                    case "--base-host": baseHost = true; break;
                    case "--clear-decals": clearDecals = true; break;
                    case "--host-visual": hostVisualPath = next; i++; break;
                    case "--hole-layer": holeLayerArg = (int)Num(next); i++; break;
                    case "--hole-dilate": holeDilate = Num(next); i++; break;
                    case "--out": outFile = next; i++; break;
                    case "--as": asRes = next; i++; break;
                    case "--report": reportPath = next; i++; break;
                    case "--size": sizeArg = next; i++; break;
                    case "--center": centerArg = next; i++; break;
                    case "--origin": originArg = next; i++; break;
                    case "--height-range": rangeArg = next; i++; break;
                    case "--height-scale": scale = Num(next); i++; break;
                    case "--height-offset": offset = Num(next); i++; break;
                    case "--rows": rowsArg = next; i++; break;
                    case "--raw-size": rawSizeArg = next; i++; break;
                    case "--spacing": spacing = Num(next); i++; break;
                    case "--world-y": worldY = Num(next); i++; break;
                    case "--tolerance": tolerance = Num(next); i++; break;
                    case "--budget": budget = (int)Num(next); i++; break;
                    case "--mask-level": maskLevel = (int)Num(next); i++; break;
                    case "--mask-tolerance": maskTolerance = (int)Num(next); i++; break;
                    case "--mask-budget": maskBudget = (int)Num(next); i++; break;
                    case "--color-level": colorLevel = (int)Num(next); i++; break;
                    case "--material-level": materialLevel = (int)Num(next); i++; break;
                    case "--materials": materialsPath = next; i++; break;
                    default:
                        if (heightsPath == null && !a.StartsWith("--")) { heightsPath = a; break; }
                        Console.Error.WriteLine(label + ": unknown option '" + a + "'");
                        return 1;
                }
            }
            if (heightsPath == null) { Console.Error.WriteLine(CreateUsage); return 1; }
            if (!File.Exists(heightsPath)) { Console.Error.WriteLine(label + ": heightmap not found: " + heightsPath); return 1; }
            if (outFile == null && asRes == null) { Console.Error.WriteLine(label + ": give --out <file.res> and/or --as <res name>"); return 1; }
            if (clearDecals && asRes == null) { Console.Error.WriteLine(label + ": --clear-decals empties the decals of the terrain named by --as"); return 1; }
            if (double.IsNaN(spacing) || double.IsNaN(worldY) || double.IsNaN(tolerance)) { Console.Error.WriteLine(label + ": a numeric option did not parse"); return 1; }

            try
            {
                // ---- heights ------------------------------------------------------------------------------
                int rawW = 0, rawH = 0;
                JObject side = ReadSidecar(heightsPath);
                if (rawSizeArg != null) ParseWxH(rawSizeArg, out rawW, out rawH);
                else if (side != null && side["size"] != null) { rawW = rawH = (int)side["size"]; }
                if (side != null && side["width"] != null) rawW = (int)side["width"];
                if (side != null && side["height"] != null) rawH = (int)side["height"];
                Raster heights = RasterFile.Read(heightsPath, rawW, rawH);

                var place = new HeightmapPlacement();
                // world rectangle: --size/--center/--origin, else the sidecar's, else the host's square
                double sizeX = 0, sizeZ = 0;
                if (sizeArg != null) ParseSize(sizeArg, out sizeX, out sizeZ);
                else if (side != null && side["worldMinX"] != null)
                {
                    sizeX = (double)side["worldMaxX"] - (double)side["worldMinX"];
                    sizeZ = (double)side["worldMaxZ"] - (double)side["worldMinZ"];
                }

                // host
                byte[] hostPayload = null; string hostSource = null; ResAssetEntry hostEntry = null;
                Bfii.TerrainFormat.TerrainStreamingTreeResource host = null;
                TerrainFrame hostFrame = null;
                if (hostSpec != null)
                {
                    hostPayload = Load(label + " --host", hostSpec, out hostEntry, out hostSource);
                    if (hostPayload == null) return 1;
                    var hp = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(hostPayload);
                    if (!hp.Success || hp.Resource.Heightfield == null) { Console.Error.WriteLine(label + ": host did not parse: " + hp.Failure); return 1; }
                    host = hp.Resource;
                    hostFrame = TreeHeightSource.FrameOf(host.Heightfield);
                }
                else if (asRes != null && App.AssetManager != null && App.AssetManager.GetResEntry(asRes) != null)
                {
                    // replacing an existing terrain: it is the host
                    hostPayload = Load(label, asRes, out hostEntry, out hostSource);
                    if (hostPayload == null) return 1;
                    var hp = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(hostPayload);
                    if (hp.Success && hp.Resource.Heightfield != null) { host = hp.Resource; hostFrame = TreeHeightSource.FrameOf(host.Heightfield); }
                }

                // the host's VisualTerrain: the renderer's layer count and its hole layer (HoleLayer.FromVisualTerrain)
                byte[] hostVisual = null;
                if (hostVisualPath != null) hostVisual = File.ReadAllBytes(hostVisualPath);
                else if (hostEntry != null && hostEntry.Name.EndsWith(ResSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    ResAssetEntry ve = App.AssetManager.GetResEntry(hostEntry.Name.Substring(0, hostEntry.Name.Length - ResSuffix.Length));
                    if (ve != null) hostVisual = ReadRes(ve);
                }
                else if (hostSpec != null && File.Exists(hostSpec) && hostSpec.IndexOf(".streamingtree_win32", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string sibling = System.Text.RegularExpressions.Regex.Replace(hostSpec, @"\.streamingtree_win32", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (File.Exists(sibling)) hostVisual = File.ReadAllBytes(sibling);
                }
                int visualLayers = 0; int? visualHole = null;
                bool haveVisual = hostVisual != null && HoleLayer.FromVisualTerrain(hostVisual, out visualLayers, out visualHole);

                if (sizeX <= 0 && hostFrame != null) sizeX = sizeZ = hostFrame.Extent;
                if (sizeX <= 0) { Console.Error.WriteLine(label + ": give --size <metres> (the heightmap's world width), or a --host to take its size from"); return 1; }
                place.SizeX = sizeX; place.SizeZ = sizeZ;
                if (originArg != null) { double ox, oz; ParsePair(originArg, out ox, out oz); place.MinX = ox; place.MinZ = oz; }
                else if (centerArg != null) { double cx, cz; ParsePair(centerArg, out cx, out cz); place.MinX = cx - sizeX / 2; place.MinZ = cz - sizeZ / 2; }
                else if (side != null && side["worldMinX"] != null) { place.MinX = (double)side["worldMinX"]; place.MinZ = (double)side["worldMinZ"]; }
                else if (hostFrame != null)
                {
                    place.MinX = hostFrame.MinX + hostFrame.Extent / 2 - sizeX / 2;
                    place.MinZ = hostFrame.MinZ + hostFrame.Extent / 2 - sizeZ / 2;
                }
                else { place.MinX = -sizeX / 2; place.MinZ = -sizeZ / 2; }

                // row order: --rows, else the sidecar's, else min Z first -- what a top-down image of a Blender scene
                // gives (Frostbite is right-handed Y-up: Blender (x, y, z) = Frostbite (x, z, -y)) and what
                // `terrain export-heights` writes
                string rows = rowsArg ?? (string)side?["rowOrder"];
                if (rows == null || IsAny(rows, "min-z-first", "minZFirst", "south-first")) place.RowZeroIsMaxZ = false;
                else if (IsAny(rows, "max-z-first", "maxZFirst", "north-first")) place.RowZeroIsMaxZ = true;
                else { Console.Error.WriteLine(label + ": --rows is min-z-first or max-z-first"); return 1; }

                // value -> metres
                if (rangeArg != null)
                {
                    double lo, hi;
                    if (!ParseBand(rangeArg, out lo, out hi)) { Console.Error.WriteLine(label + ": --height-range wants lo:hi in metres"); return 1; }
                    if (heights.IsFloat)
                    {
                        // float data: map its own min..max onto lo..hi
                        float mn = float.MaxValue, mx = float.MinValue;
                        foreach (float v in heights.Channel(0)) if (!float.IsNaN(v)) { if (v < mn) mn = v; if (v > mx) mx = v; }
                        double span = Math.Max(1e-9, mx - mn);
                        place.Scale = (hi - lo) / span; place.Offset = lo - mn * place.Scale;
                    }
                    else { place.Scale = hi - lo; place.Offset = lo; }
                }
                else if (!heights.IsFloat && side != null && side["minMetres"] != null)
                {
                    double lo = (double)side["minMetres"], hi = (double)side["maxMetres"];
                    place.Scale = hi - lo; place.Offset = lo;
                }
                else if (!heights.IsFloat)
                {
                    Console.Error.WriteLine(label + ": " + Path.GetFileName(heightsPath) + " stores " + heights.BitDepth +
                        "-bit integers; say what they mean with --height-range lo:hi (metres at 0 and at full scale)");
                    return 1;
                }
                if (!double.IsNaN(scale)) place.Scale = scale;
                if (!double.IsNaN(offset)) place.Offset = offset;
                place.HoleBelow = holeBelow;

                // holes from a mask image: value > 0.5 is a hole
                if (holesPath != null)
                {
                    Raster holes = RasterFile.Read(holesPath);
                    if (holes.Width != heights.Width || holes.Height != heights.Height)
                    { Console.Error.WriteLine(label + ": --holes must match the heightmap's " + heights.Width + "x" + heights.Height); return 1; }
                    var hv = holes.Channel(0);
                    for (long k = 0; k < hv.Length; k++)
                        if (hv[k] > 0.5f) heights.Data[k * heights.Channels] = float.NaN;
                }

                // ---- frame ---------------------------------------------------------------------------------
                // measure the metres range on a provisional frame to size WorldSizeY
                var probeFrame = new TerrainFrame { MinX = place.MinX, MinZ = place.MinZ, Extent = Math.Max(sizeX, sizeZ), WorldSizeY = 65535, MaxLevel = 2 };
                var probe = new RasterHeightSource(probeFrame, heights, place);
                if (probe.MinMetres < 0)
                {
                    Console.Error.WriteLine(label + ": the heightmap reaches " + probe.MinMetres.ToString("0.##", CultureInfo.InvariantCulture) +
                        " m; terrain heights are 0..world-Y. Shift it up with --height-offset or --height-range.");
                    return 1;
                }
                float wy = worldY > 0 ? (float)worldY : TerrainFramePlanner.WorldSizeYFor(probe.MaxMetres);
                if (probe.MaxMetres > wy) { Console.Error.WriteLine(label + ": --world-y " + wy + " is below the heightmap's top " + probe.MaxMetres.ToString("0.##", CultureInfo.InvariantCulture) + " m"); return 1; }
                // the host's hole layer: the first layer with physics disabled, which the renderer clips through
                int? holeLayer = null;
                if (probe.HoleSamples > 0)
                {
                    string holeNote = "";
                    if (holeLayerArg >= 0) holeLayer = holeLayerArg;
                    else if (haveVisual) { holeLayer = visualHole; holeNote = visualHole != null ? "the host's VisualTerrain: its last layer has holesEnabled" : "the host's VisualTerrain has no layer with holesEnabled"; }
                    else if (host != null) holeLayer = HoleLayer.FromHostHoles(host, out holeNote);
                    if (holeLayer == null)
                        logger.LogWarning(label + ": the heightmap has holes but no hole layer is known (" + (host == null ? "no --host" : holeNote) +
                            "); the physics hole is cut but the ground still draws there. Pass --hole-layer <n>.");
                    else logger.Log(label + ": hole layer " + holeLayer + (holeNote.Length > 0 ? " (" + holeNote + ")" : ""));
                }

                if (baseHost)
                    return CreateOverHost(label, host, hostFrame, hostSource, heights, place, probe, holeLayer, holeDilate,
                                          layersPath != null || colormapPath != null || colorArg != null, outFile, asRes, clearDecals, reportPath, logger, formatter);

                if (spacing <= 0) spacing = Math.Min(sizeX / (heights.Width - 1), sizeZ / (heights.Height - 1));
                // never a square smaller than the host's: such terrains render black (TA_S/TA_S2, LOG); a small
                // heightmap takes the host's square and is refined only where it has detail
                var plan = TerrainFramePlanner.Choose(place, spacing, noAlign ? null : hostFrame, wy, 137, allowSmallFrame ? -8 : 0);
                TerrainFrame frame = plan.Frame;
                var heightSource = new RasterHeightSource(frame, heights, place);
                // outside the heightmap the skirt settles to the mean edge height instead of extruding the edge across
                // the frame (which spends the node budget on the extrusion: TA_S3)
                heightSource.SkirtFadeMetres = skirtFade >= 0 ? skirtFade : Math.Max(place.SizeX, place.SizeZ) / 4;

                // ---- layers, colour, material ---------------------------------------------------------------
                ILayerSource layers = new BaseOnlyLayerSource();
                IMaterialSource material = new ConstantMaterialSource(0);
                IColorSource color = null;
                HeightmapPlacement colormapPlace = null;          // a layers.json with its own rectangle places the colour map too
                string layerNote = "base layer 0 only";
                LayersFile lf = null;
                if (layersPath != null)
                {
                    lf = ReadLayers(label, layersPath, place);
                    if (lf == null) return 1;
                    WeightMapLayerSource wl = lf.Source;
                    colormapPlace = lf.MaskPlace;
                    layers = wl;
                    // tree depths a `terrain export-layers` file records, unless given on the command line
                    if (maskLevel < 0 && lf.MaskLevel != null) maskLevel = lf.MaskLevel.Value;
                    if (colorLevel < 0 && lf.ColorLevel != null) colorLevel = lf.ColorLevel.Value;
                    if (materialLevel < 0 && lf.MaterialLevel != null) materialLevel = lf.MaterialLevel.Value;
                    // A patch whose layer set no layer combination contains draws nothing (SPEC §8), and a TerrainData
                    // can list more layers than its combinations cover (Tatooine: 17 listed, 0-10 combined). The
                    // layers the host itself paints are covered by construction.
                    if (haveVisual && !anyLayer)
                    {
                        var beyond = wl.Layers.Where(l => l.HostLayer >= visualLayers).Select(l => l.HostLayer).ToList();
                        if (beyond.Count > 0)
                        {
                            Console.Error.WriteLine(label + ": host layer(s) " + string.Join(", ", beyond) + " do not exist at run time: the host's VisualTerrain has " +
                                visualLayers + " layer(s) (0-" + (visualLayers - 1) + "). --any-layer uses them anyway.");
                            return 1;
                        }
                    }
                    else if (host != null && host.Mask != null && !anyLayer)
                    {
                        var painted = new HashSet<int>(host.Mask.EnumerateNodes().SelectMany(n => n.Subtiles).Select(st => (int)st.TerrainLayerIndex));
                        var unpainted = wl.Layers.Where(l => !painted.Contains(l.HostLayer)).Select(l => l.HostLayer).ToList();
                        if (unpainted.Count > 0)
                        {
                            Console.Error.WriteLine(label + ": host layer(s) " + string.Join(", ", unpainted) + " are never painted on the host terrain, " +
                                "so its layer combinations may not cover them (such ground draws nothing). The host paints layers " +
                                string.Join(", ", painted.OrderBy(v => v)) + ". --any-layer uses them anyway.");
                            return 1;
                        }
                    }
                    if (materialLevel < 0) materialLevel = 3;
                    material = new DominantLayerMaterialSource(wl, frame.NodeSize(materialLevel) / 256);
                    if (colormapPath == null) colormapPath = lf.Colormap;
                    layerNote = wl.Layers.Count + " layer(s): " + string.Join(", ", wl.Layers.Select(l => (l.Name.Length > 0 ? l.Name : "layer") + "=" + l.HostLayer));
                }
                if (materialLevel < 0) materialLevel = 3;
                // a material index map (4-bit indices into the host's material declarations) instead of each layer's material
                string matFile = materialsPath ?? lf?.Materials;
                string materialNote = lf != null ? "dominant layer's" : "index 0";
                if (matFile != null)
                {
                    Raster mr = RasterFile.Read(matFile);
                    var mPlace = materialsPath == null && lf?.MaterialsPlace != null ? lf.MaterialsPlace
                        : new HeightmapPlacement { MinX = place.MinX, MinZ = place.MinZ, SizeX = place.SizeX, SizeZ = place.SizeZ, RowZeroIsMaxZ = place.RowZeroIsMaxZ };
                    material = new RectMaterialSource(mr, mPlace);
                    materialNote = Path.GetFileName(matFile) + " " + mr.Width + "x" + mr.Height;
                }
                if (colormapPath != null)
                {
                    Raster cm = RasterFile.Read(colormapPath);
                    var cmPlace = colormapPlace ?? new HeightmapPlacement { MinX = place.MinX, MinZ = place.MinZ, SizeX = place.SizeX, SizeZ = place.SizeZ, RowZeroIsMaxZ = place.RowZeroIsMaxZ };
                    color = new RasterColorSource(cm, cmPlace);
                }
                else if (colorArg != null)
                {
                    var p = colorArg.Split(',');
                    if (p.Length != 3) { Console.Error.WriteLine(label + ": --color wants r,g,b (0..255)"); return 1; }
                    color = new ConstantColorSource(byte.Parse(p[0]), byte.Parse(p[1]), byte.Parse(p[2]));
                }
                else if (host != null && host.Color == null) color = null;   // the host ships no colour tree (4 SP terrains, the test range): nor do we
                else color = new ConstantColorSource(128, 128, 128);

                if (holeLayer != null)
                {
                    var holeGrid = new WorldGrid(heightSource.HoleMask(holeDilate), heights.Width, heights.Height,
                        new HeightmapPlacement { MinX = place.MinX, MinZ = place.MinZ, SizeX = place.SizeX, SizeZ = place.SizeZ, RowZeroIsMaxZ = false });
                    layers = new HoleLayerSource(layers, holeGrid, holeLayer.Value);
                    layerNote += "; hole layer " + holeLayer;
                }

                var decls = new List<uint>();
                uint background = 0x800;
                if (host != null && host.Material != null) { decls.AddRange(host.Material.MaterialDecls); background = host.Material.BackgroundMaterialDecl; }

                var input = new TerrainGeneratorInput
                {
                    Frame = frame,
                    Heights = heightSource,
                    HeightOptions = new HeightfieldBuildOptions { RefineToleranceMetres = tolerance, MaxDataNodes = budget },
                    Layers = layers,
                    // adaptive depth, as shipped masks are: deeper nodes only where a layer has finer detail
                    MaskOptions = new MaskBuildOptions { MaxLevel = maskLevel >= 0 ? maskLevel : Math.Min(4, frame.MaxLevel), RefineTolerance = Math.Max(0, maskTolerance), MaxTiledSubtiles = maskBudget },
                    Color = color,
                    ColorOptions = new ColorBuildOptions { MaxLevel = colorLevel >= 0 ? colorLevel : 3 },
                    Material = material,
                    MaterialLevel = materialLevel,
                    MaterialDecls = decls,
                    BackgroundMaterialDecl = background,
                };

                if (DryRun.Active)
                {
                    DryRun.Report(logger, "build a {0} m terrain ({1}) at {2:0.###} m spacing", frame.Extent, plan.Note, frame.Spacing);
                    return 0;
                }
                TerrainGeneratorReport rep;
                byte[] payload = TerrainGenerator.Generate(input, out rep);

                // ---- write ----------------------------------------------------------------------------------
                var fields = new Dictionary<string, string>
                {
                    { "heights", Path.GetFileName(heightsPath) + " " + heights.Width + "x" + heights.Height + " (" + heights.Format + ")" },
                    { "placed", F(place.MinX) + ".." + F(place.MaxX) + " x " + F(place.MinZ) + ".." + F(place.MaxZ) + " m, " + F(probe.MinMetres) + ".." + F(probe.MaxMetres) + " m high" },
                    { "frame", F(frame.Extent) + " m from (" + F(frame.MinX) + ", " + F(frame.MinZ) + "), " + plan.Note + ", max level " + frame.MaxLevel + ", " + frame.Spacing.ToString("0.###", CultureInfo.InvariantCulture) + " m spacing, world Y " + frame.WorldSizeY },
                    { "heightfield", rep.Heightfield.DataNodes + " data nodes, " + rep.Heightfield.HoleNodes + " with holes, atlas " + rep.Heightfield.Atlas },
                    { "mask", rep.Mask.Nodes + " nodes to L" + rep.Mask.MaxLevelUsed + ", " + rep.Mask.TiledSubtiles + " tiled subtiles, " + layerNote },
                    { "material", materialNote + " at L" + materialLevel + (color == null ? "; no colour tree" : "; colour to L" + (colorLevel >= 0 ? colorLevel : 3)) },
                    { "payload", payload.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes" },
                };
                if (hostSource != null) fields["host"] = hostSource;
                if (probe.HoleSamples > 0) fields["holes"] = probe.HoleSamples + " heightmap samples";

                if (reportPath != null)
                {
                    var j = new JObject();
                    foreach (var kv in fields) j[kv.Key] = kv.Value;
                    j["frameMinX"] = frame.MinX; j["frameMinZ"] = frame.MinZ; j["frameExtent"] = frame.Extent; j["maxLevel"] = frame.MaxLevel;
                    j["worldSizeY"] = frame.WorldSizeY; j["dataNodes"] = rep.Heightfield.DataNodes; j["payloadBytes"] = payload.Length;
                    File.WriteAllText(reportPath, j.ToString(Formatting.Indented));
                }

                return Emit(label, payload, outFile, asRes, clearDecals, fields, formatter);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException || ex is IOException || ex is FormatException || ex is InvalidOperationException)
            {
                Console.Error.WriteLine(label + ": " + ex.Message);
                return 1;
            }
        }

        // --base-host: edit a region of the host terrain. Heights inside the heightmap's rectangle come from the heightmap,
        // everywhere else from the host's own samples; the frame is the host's; the heightfield keeps the host's node
        // structure and gains full-depth nodes (in complete families of four) over the region; mask, colour, material
        // and destruction depth are the host's byte for byte, except that holes paint the host's hole layer in place.
        private static int CreateOverHost(string label, Bfii.TerrainFormat.TerrainStreamingTreeResource host, TerrainFrame hostFrame,
            string hostSource, Raster heights, HeightmapPlacement place, RasterHeightSource probe, int? holeLayer, double holeDilate,
            bool paintRequested, string outFile, string asRes, bool clearDecals, string reportPath, ExportLogger logger, OutputFormatter formatter)
        {
            if (host == null || hostFrame == null) { Console.Error.WriteLine(label + ": --base-host needs --host (or --as an existing terrain)"); return 1; }
            if (paintRequested)
            { Console.Error.WriteLine(label + ": --base-host keeps the host's layers and colour; --layers/--colormap/--color are not combined with it"); return 1; }
            if (probe.MaxMetres > hostFrame.WorldSizeY)
            { Console.Error.WriteLine(label + ": the heightmap reaches " + F(probe.MaxMetres) + " m, above the host's world Y " + hostFrame.WorldSizeY); return 1; }
            if (place.MinX < hostFrame.MinX || place.MinZ < hostFrame.MinZ || place.MaxX > hostFrame.MinX + hostFrame.Extent || place.MaxZ > hostFrame.MinZ + hostFrame.Extent)
            { Console.Error.WriteLine(label + ": the heightmap's rectangle leaves the host's square"); return 1; }

            var hf0 = host.Heightfield;
            var region = new RasterHeightSource(hostFrame, heights, place);
            var src = new RegionOverHostHeightSource(hostFrame, new TreeHeightSource(hostFrame, hf0), region, place);
            int skip = (int)host.Header.Field0x11;
            var nodes = new HashSet<Bfii.TerrainFormat.QuadtreeNodeId>(hf0.EnumerateNodes().Where(n => n.Payload != null).Select(n => n.Id));
            int hostNodes = nodes.Count;
            for (int level = skip; level <= hostFrame.MaxLevel; level++)
            {
                double size = hostFrame.NodeSize(level);
                int i0 = Math.Max(0, (int)Math.Floor((place.MinX - hostFrame.MinX) / size)), i1 = Math.Min((1 << level) - 1, (int)Math.Floor((place.MaxX - hostFrame.MinX) / size));
                int j0 = Math.Max(0, (int)Math.Floor((place.MinZ - hostFrame.MinZ) / size)), j1 = Math.Min((1 << level) - 1, (int)Math.Floor((place.MaxZ - hostFrame.MinZ) / size));
                bool family = level > skip;
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        // the whole family of four, so no sibling is left without data
                        int pi = family ? i & ~1 : i, pj = family ? j & ~1 : j;
                        for (int dj = 0; dj < (family ? 2 : 1); dj++)
                            for (int di = 0; di < (family ? 2 : 1); di++)
                                nodes.Add(new Bfii.TerrainFormat.QuadtreeNodeId((ushort)(pi + di), (ushort)(pj + dj), (byte)level));
                    }
            }
            var opts = new HeightfieldBuildOptions
            {
                SkipLevels = skip,
                DataNodes = nodes,
                AtlasSampleCountX = (int)hf0.ResourceAtlasSampleCountX,
                AtlasSampleCountYMin = 0,
                AtlasHeadroom = 0.1,
                BaseTree = hf0,
                EditRegion = new[] { place.MinX, place.MinZ, place.MaxX, place.MaxZ },
            };
            HeightfieldBuildReport hrep;
            var hf = HeightfieldBuilder.Build(src, hostFrame, opts, out hrep);

            string maskNote = "the host's";
            if (holeLayer != null && host.Mask != null)
            {
                var holeGrid = new WorldGrid(region.HoleMask(holeDilate), heights.Width, heights.Height,
                    new HeightmapPlacement { MinX = place.MinX, MinZ = place.MinZ, SizeX = place.SizeX, SizeZ = place.SizeZ, RowZeroIsMaxZ = false });
                var box = new Bfii.TerrainFormat.AxisAlignedBox2
                {
                    MinX = Bfii.TerrainFormat.F32.FromValue((float)place.MinX), MinY = Bfii.TerrainFormat.F32.FromValue((float)place.MinZ),
                    MaxX = Bfii.TerrainFormat.F32.FromValue((float)place.MaxX), MaxY = Bfii.TerrainFormat.F32.FromValue((float)place.MaxZ),
                };
                var mrep = MaskEditor.Paint(host.Mask, holeLayer.Value, (x, z, t) =>
                {
                    double v = Math.Floor(holeGrid.Sample(x, z, t) * 255 + 0.5);
                    return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                }, box);
                maskNote = "the host's + hole layer " + holeLayer + " painted (" + mrep.SubtilesAdded + " subtiles added, " + mrep.SubtilesChanged + " changed, " + mrep.NodesTouched + " nodes)";
            }
            TerrainAssembler.ReplaceTree(host, hf);
            byte[] payload = TerrainAssembler.Serialize(host);
            var check = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(payload);
            if (!check.Success) { Console.Error.WriteLine(label + ": the edited terrain does not re-parse: " + check.Failure); return 1; }

            var fields = new Dictionary<string, string>
            {
                { "heights", heights.Width + "x" + heights.Height + " (" + heights.Format + ") over " + F(place.MinX) + ".." + F(place.MaxX) + " x " + F(place.MinZ) + ".." + F(place.MaxZ) + " m" },
                { "frame", "the host's: " + F(hostFrame.Extent) + " m from (" + F(hostFrame.MinX) + ", " + F(hostFrame.MinZ) + "), max level " + hostFrame.MaxLevel + ", world Y " + hostFrame.WorldSizeY },
                { "heightfield", hrep.DataNodes + " data nodes (host " + hostNodes + "), " + hrep.HoleNodes + " with holes, atlas " + hrep.Atlas },
                { "mask", maskNote },
                { "payload", payload.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes" },
                { "host", hostSource },
            };
            if (probe.HoleSamples > 0) fields["holes"] = probe.HoleSamples + " heightmap samples";
            if (reportPath != null)
            {
                var j = new JObject();
                foreach (var kv in fields) j[kv.Key] = kv.Value;
                File.WriteAllText(reportPath, j.ToString(Formatting.Indented));
            }
            return Emit(label, payload, outFile, asRes, clearDecals, fields, formatter);
        }

        private static int Emit(string label, byte[] payload, string outFile, string asRes, bool clearDecals, Dictionary<string, string> fields, OutputFormatter formatter)
        {
            if (outFile != null)
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(outFile, payload);
                fields["file"] = outFile;
            }
            if (asRes != null)
            {
                if (App.AssetManager == null || !Session.Project.IsLoaded)
                { Console.Error.WriteLine(label + ": --as needs a loaded project (project new/load)"); return 1; }
                ResAssetEntry target = App.AssetManager.GetResEntry(asRes);
                if (target != null)
                {
                    if (!CheckResType(label, target)) return 1;
                    byte[] meta = target.ModifiedEntry != null && target.ModifiedEntry.ResMeta != null ? target.ModifiedEntry.ResMeta : target.ResMeta;
                    ResWriter.Modify(target.Name, payload, meta);
                    fields["res"] = target.Name + " (replaced)";
                }
                else
                {
                    // a new streaming tree (a cloned TerrainData points at it): meta is zero on all 39 shipped trees
                    App.AssetManager.AddRes(asRes.ToLowerInvariant(), (ResourceType)StreamingTreeResType, new byte[16], payload);
                    if (App.AssetManager.GetResEntry(asRes.ToLowerInvariant()) == null) { Console.Error.WriteLine(label + ": AddRes did not create " + asRes); return 1; }
                    fields["res"] = asRes.ToLowerInvariant() + " (added)";
                }
                if (clearDecals && ClearDecals(label, asRes, fields) < 0) return 1;
            }
            formatter.WriteResult("OK", label, fields);
            return 0;
        }

        private const string CreateUsage =
            "Usage: frostycli terrain create --heights <h.r16|h.r32|h.png|h.exr> (--out <file.res> | --as <res>)\n" +
            "         [--size <m>|<x>x<z>] [--center x,z | --origin x,z] [--height-range lo:hi] [--height-scale s] [--height-offset o]\n" +
            "         [--rows min-z-first|max-z-first] [--raw-size WxH] [--spacing m] [--world-y m]\n" +
            "         [--host <terrain>] [--no-align] [--allow-small-frame] [--skirt-fade m] [--layers layers.json] [--any-layer] [--colormap rgb.png | --color r,g,b]\n" +
            "         [--holes mask.png] [--hole-below m] [--hole-layer n] [--hole-dilate m] [--base-host] [--clear-decals]\n" +
            "         [--tolerance m] [--budget nodes] [--mask-level L] [--mask-tolerance a] [--mask-budget tiles] [--color-level L]\n" +
            "         [--materials idx.png] [--material-level L]\n" +
            "         [--report report.json]";

        private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static bool IsAny(string s, params string[] names) => names.Any(n => n.Equals(s, StringComparison.OrdinalIgnoreCase));

        private static double Num(string s)
        {
            double v;
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        private static JObject ReadSidecar(string path)
        {
            foreach (string p in new[] { path + ".json", Path.ChangeExtension(path, ".json") })
                if (File.Exists(p))
                {
                    try { return JObject.Parse(File.ReadAllText(p)); }
                    catch (Exception ex) { throw new InvalidDataException("unreadable sidecar " + p + ": " + ex.Message); }
                }
            return null;
        }

        private static void ParseWxH(string s, out int w, out int h)
        {
            var p = s.ToLowerInvariant().Split('x');
            if (p.Length != 2 || !int.TryParse(p[0], out w) || !int.TryParse(p[1], out h)) throw new ArgumentException("--raw-size wants WxH");
        }

        private static void ParseSize(string s, out double x, out double z)
        {
            var p = s.ToLowerInvariant().Split('x');
            x = Num(p[0]); z = p.Length > 1 ? Num(p[1]) : x;
            if (double.IsNaN(x) || double.IsNaN(z) || x <= 0 || z <= 0) throw new ArgumentException("--size wants metres, or <x>x<z>");
        }

        private static void ParsePair(string s, out double a, out double b)
        {
            var p = s.Split(',');
            a = p.Length == 2 ? Num(p[0]) : double.NaN; b = p.Length == 2 ? Num(p[1]) : double.NaN;
            if (double.IsNaN(a) || double.IsNaN(b)) throw new ArgumentException("wanted x,z in metres, got '" + s + "'");
        }

        // layers.json:
        // { "weights": "splat" | "alpha",
        //   "layers": [ { "name": "sand", "hostLayer": 0 }, { "name": "rock", "hostLayer": 5, "mask": "rock.png", "material": 1 } ],
        //   "colormap": "colour.png" }
        // Masks cover the heightmap's rectangle, rows in the heightmap's order; paths are relative to the json.
        private sealed class LayersFile
        {
            public WeightMapLayerSource Source;
            public string Colormap, Materials;
            public HeightmapPlacement MaskPlace, MaterialsPlace;
            public int? MaskLevel, ColorLevel, MaterialLevel;
        }

        private static LayersFile ReadLayers(string label, string path, HeightmapPlacement heightsPlace)
        {
            if (!File.Exists(path)) { Console.Error.WriteLine(label + ": layers file not found: " + path); return null; }
            JObject j;
            try { j = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": " + path + ": " + ex.Message); return null; }
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(path));
            Func<string, string> resolve = p => p == null ? null : Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);
            bool splat = !"alpha".Equals((string)j["weights"], StringComparison.OrdinalIgnoreCase);
            var file = new LayersFile();
            // masks cover the heightmap's rectangle in its row order, unless the json says otherwise
            // (`terrain export-layers` writes the rectangle of its pixel centres)
            var place = heightsPlace;
            if (j["worldMinX"] != null || j["rowOrder"] != null)
            {
                place = new HeightmapPlacement { MinX = heightsPlace.MinX, MinZ = heightsPlace.MinZ, SizeX = heightsPlace.SizeX, SizeZ = heightsPlace.SizeZ, RowZeroIsMaxZ = heightsPlace.RowZeroIsMaxZ };
                if (j["worldMinX"] != null)
                {
                    place.MinX = (double)j["worldMinX"]; place.MinZ = (double)j["worldMinZ"];
                    place.SizeX = (double)j["worldMaxX"] - place.MinX; place.SizeZ = (double)j["worldMaxZ"] - place.MinZ;
                }
                string ro = (string)j["rowOrder"];
                if (ro != null) place.RowZeroIsMaxZ = IsAny(ro, "max-z-first", "maxZFirst", "north-first");
                file.MaskPlace = place;
            }
            var list = new List<LayerInput>();
            var arr = j["layers"] as JArray;
            if (arr == null || arr.Count == 0) { Console.Error.WriteLine(label + ": " + path + " has no \"layers\" array"); return null; }
            foreach (JObject l in arr)
            {
                var li = new LayerInput
                {
                    Name = (string)l["name"] ?? "",
                    HostLayer = l["hostLayer"] != null ? (int)l["hostLayer"] : -1,
                    Material = l["material"] != null ? (int)l["material"] : 0,
                };
                if (li.HostLayer < 0) { Console.Error.WriteLine(label + ": layer '" + li.Name + "' needs \"hostLayer\" (index into the host's TerrainLayers)"); return null; }
                string mask = resolve((string)l["mask"]);
                if (mask != null)
                {
                    Raster r = RasterFile.Read(mask);
                    li.Weights = WorldGrid.FromRaster(r, 0, place);
                }
                list.Add(li);
            }
            file.Source = new WeightMapLayerSource(list, splat);
            file.Colormap = resolve((string)j["colormap"]);
            // a material index map; "materialsRect" [minX, minZ, maxX, maxZ] is the square its pixels tile (cells, not
            // vertices: `terrain export-layers` writes the material tree's own cells)
            file.Materials = resolve((string)j["materials"]);
            if (file.Materials != null)
            {
                if (j["materialsRect"] is JArray mr && mr.Count == 4)
                    file.MaterialsPlace = new HeightmapPlacement
                    {
                        MinX = (double)mr[0], MinZ = (double)mr[1], SizeX = (double)mr[2] - (double)mr[0], SizeZ = (double)mr[3] - (double)mr[1],
                        RowZeroIsMaxZ = place.RowZeroIsMaxZ,
                    };
                else file.MaterialsPlace = place;
            }
            if (j["maskLevel"] != null) file.MaskLevel = (int)j["maskLevel"];
            if (j["colorLevel"] != null) file.ColorLevel = (int)j["colorLevel"];
            if (j["materialLevel"] != null) file.MaterialLevel = (int)j["materialLevel"];
            return file;
        }
    }
}
