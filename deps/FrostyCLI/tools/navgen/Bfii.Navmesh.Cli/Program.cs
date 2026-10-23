// VENDORED from BfiiMapTools src\Bfii.Navmesh\tools\Bfii.Navmesh.Cli\Program.cs (sha256:337DEF054A54D028).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Bfii.Navmesh.Compile;
using Bfii.Navmesh.Diagnostics;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Islands;
using Bfii.Navmesh.Obj;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Cli
{
    /// <summary>
    /// Command line front end. Reads and writes files only; it never touches an editor, a project,
    /// a game install or a mod.
    /// </summary>
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitUsage = 2;
        private const int ExitFailed = 1;

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return ExitUsage;
            }

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "inspect": return Inspect(args);
                    case "roundtrip": return RoundTrip(args);
                    case "export-obj": return ExportObj(args);
                    case "compile": return CompileCommand(args);
                    case "islands": return Islands(args);
                    case "make-fixture": return MakeFixture(args);
                    case "make-donor": return MakeDonor(args);
                    case "help":
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return ExitOk;
                    default:
                        Console.Error.WriteLine("unknown command: " + args[0]);
                        PrintUsage();
                        return ExitUsage;
                }
            }
            catch (NavCompileException error)
            {
                Console.Error.WriteLine("COMPILE FAILED: " + error.Message);
                for (int i = 0; i < error.Details.Count; i++)
                {
                    Console.Error.WriteLine("  " + error.Details[i]);
                }

                return ExitFailed;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                return ExitFailed;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("navmesh - BFII pathfinding blob reader, writer and OBJ compiler");
            Console.WriteLine();
            Console.WriteLine("  inspect <blob>");
            Console.WriteLine("      Parse a blob and report its header, graphs, island histogram");
            Console.WriteLine("      and checksum calibration. Read only.");
            Console.WriteLine();
            Console.WriteLine("  roundtrip <blob> [--reserialize] [--out <path>]");
            Console.WriteLine("      Parse and write back, then compare byte for byte.");
            Console.WriteLine();
            Console.WriteLine("  export-obj <blob> <out.obj> [--graph N]");
            Console.WriteLine("      Export one graph as a Wavefront OBJ.");
            Console.WriteLine();
            Console.WriteLine("  compile <donor-blob> <mesh.obj> <out-blob> [--graph N]");
            Console.WriteLine("                                            [--legacy-island-sentinel]");
            Console.WriteLine("                                            [--island-base 0|1]");
            Console.WriteLine("      Compile a manifold OBJ into one graph of a donor blob and write");
            Console.WriteLine("      the result to a new file. The donor is never modified in place.");
            Console.WriteLine();
            Console.WriteLine("  islands <blob> [--verify] [--relabel <out-blob>] [--island-base 0|1]");
            Console.WriteLine("      Report, check or rewrite island numbering.");
            Console.WriteLine();
            Console.WriteLine("  make-fixture <out-dir>");
            Console.WriteLine("      Write the two-island and one-island OBJ test fixtures.");
            Console.WriteLine();
            Console.WriteLine("  make-donor <out-blob> [--graphs N] [--big-endian] [--pointer32]");
            Console.WriteLine("      Write a SYNTHETIC blob for smoke testing this tool with no game");
            Console.WriteLine("      data present. Its header values are invented. NOT VALID CONTENT.");
            Console.WriteLine();
            Console.WriteLine("Nothing here launches an editor, opens a project or writes to a game");
            Console.WriteLine("install. Output blobs are plain files; getting one into a project is a");
            Console.WriteLine("separate, deliberate step.");
        }

        private static int Inspect(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: navmesh inspect <blob>");
                return ExitUsage;
            }

            byte[] payload = File.ReadAllBytes(args[1]);
            NavMeshImage image = NavMeshImage.Read(payload);

            Console.WriteLine("file            " + Path.GetFullPath(args[1]));
            Console.WriteLine("bytes           " + payload.Length.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("byte order      " + image.Order);
            Console.WriteLine("resource ver    " + image.ResourceVersion);
            Console.WriteLine("section id      0x" + image.SectionId.ToString("X", CultureInfo.InvariantCulture));
            Console.WriteLine("pointer code    " + image.PointerSizeCode +
                              (image.PointerSizeCode == 1 ? "  (64-bit pointers)" : "  (32-bit pointers)"));
            Console.WriteLine("runtime flags   0x" + image.RuntimeFlags.ToString("X8", CultureInfo.InvariantCulture));
            Console.WriteLine("constant flags  0x" + image.ConstantFlags.ToString("X8", CultureInfo.InvariantCulture));
            Console.WriteLine("image size      " + image.SourceImageSize);
            Console.WriteLine("stored checksum 0x" + image.SourceChecksum.ToString("X8", CultureInfo.InvariantCulture));

            if (image.ChecksumCalibration != null)
            {
                Console.WriteLine("checksum calib  " + image.ChecksumCalibration);
            }

            Console.WriteLine("nav set version " + image.NavSet.Version);
            Console.WriteLine("graphs          " + image.NavSet.Graphs.Count);

            bool anyZeroIsland = false;
            bool anySentinel = false;

            for (int i = 0; i < image.NavSet.Graphs.Count; i++)
            {
                NavGraph graph = image.NavSet.Graphs[i];
                Console.WriteLine();
                Console.WriteLine("graph " + i);
                Console.WriteLine("  image version " + graph.Version +
                                  (graph.Version == NavGraph.BfiiImageVersion ? "  (the version BFII is understood to read)" : ""));
                Console.WriteLine("  layer         " + graph.Layer);
                Console.WriteLine("  areas         " + graph.Areas.Count);
                Console.WriteLine("  build scale   " + graph.BuildScale.ToString("R", CultureInfo.InvariantCulture));
                Console.WriteLine("  voxel size    " + graph.VoxelSize.ToString("R", CultureInfo.InvariantCulture));
                Console.WriteLine("  agent radius  " + graph.AgentRadius.ToString("R", CultureInfo.InvariantCulture));
                Console.WriteLine("  step height   " + graph.StepHeight.ToString("R", CultureInfo.InvariantCulture));
                Console.WriteLine("  agent height  " + graph.AgentHeight.ToString("R", CultureInfo.InvariantCulture));
                Console.WriteLine("  build up axis " + graph.BuildUpAxis);
                Console.WriteLine("  bounds        " + graph.Bounds);
                Console.WriteLine("  link fields   record=" + graph.RawLinkRecordBytes +
                                  " data=" + graph.RawLinkDataBytes +
                                  " datumSize=" + graph.SerializedLinkDatumSize +
                                  " datumCount=" + graph.RawLinkDatumCount);
                Console.WriteLine("  kd tree       " + graph.KdTree.NodeCount + " node(s), depth " + graph.KdTree.Depth);
                Console.WriteLine("  diagnostics   unresolvedAdjacency=" + graph.Diagnostics.UnresolvedAdjacencyPointers +
                                  " unresolvedKdLeaves=" + graph.Diagnostics.UnresolvedKdLeafOffsets);

                IslandHistogram histogram = IslandLabeler.Describe(graph);
                Console.WriteLine("  islands       " + histogram);

                IslandVerification verification = IslandLabeler.Verify(graph);
                Console.WriteLine("  connectivity  " + verification);

                anyZeroIsland |= histogram.UsesIdZero;
                anySentinel |= histogram.SentinelAreas > 0;
            }

            Console.WriteLine();
            Console.WriteLine("OPEN QUESTIONS THIS FILE CAN SETTLE");
            Console.WriteLine("  island id 0 used in this file : " + (anyZeroIsland ? "YES" : "no"));
            Console.WriteLine("      If this file is shipped DICE data and the answer is YES, then 0 is a");
            Console.WriteLine("      valid island id and IslandLabelOptions.ZeroBased is safe to use.");
            Console.WriteLine("  saturated sentinel present     : " + (anySentinel ? "YES" : "no"));
            Console.WriteLine("      In shipped DICE data a YES would mean the sentinel is normal and");
            Console.WriteLine("      master plan risk R-06 is benign. In a community-imported blob it just");
            Console.WriteLine("      means the blob was built by the unfixed importer.");

            if (image.ChecksumCalibration != null && image.ChecksumCalibration.IsDecisive)
            {
                Console.WriteLine("  checksum word order            : " + image.ChecksumCalibration.DecidedOrder +
                                  "  (settled by this file)");
            }

            return ExitOk;
        }

        private static int RoundTrip(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: navmesh roundtrip <blob> [--reserialize] [--out <path>]");
                return ExitUsage;
            }

            bool reserialize = HasFlag(args, "--reserialize");
            string? outPath = GetOption(args, "--out");

            byte[] payload = File.ReadAllBytes(args[1]);
            NavRoundTripResult result = NavRoundTrip.Run(payload, reserialize);

            Console.WriteLine("mode            " + (reserialize ? "reserialize" : "preserving"));
            Console.WriteLine(result.ToString());

            if (!result.IsByteExact)
            {
                Console.WriteLine("differing offsets (first " + result.DifferingOffsets.Count + "):");
                for (int i = 0; i < result.DifferingOffsets.Count; i++)
                {
                    Console.WriteLine("  0x" + result.DifferingOffsets[i].ToString("X", CultureInfo.InvariantCulture));
                }
            }

            if (outPath != null)
            {
                File.WriteAllBytes(outPath, result.Image.Write());
                Console.WriteLine("wrote           " + Path.GetFullPath(outPath));
            }

            return result.IsByteExact ? ExitOk : ExitFailed;
        }

        private static int ExportObj(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: navmesh export-obj <blob> <out.obj> [--graph N]");
                return ExitUsage;
            }

            int graphIndex = GetIntOption(args, "--graph", 0);
            NavMeshImage image = NavMeshImage.Read(File.ReadAllBytes(args[1]));

            if (graphIndex < 0 || graphIndex >= image.NavSet.Graphs.Count)
            {
                Console.Error.WriteLine("graph " + graphIndex + " is out of range; the image holds " +
                                        image.NavSet.Graphs.Count);
                return ExitUsage;
            }

            NavGraph graph = image.NavSet.Graphs[graphIndex];
            ObjWriter.WriteFile(args[2], graph);

            Console.WriteLine("exported graph " + graphIndex + ": " + graph.Areas.Count + " area(s) -> " +
                              Path.GetFullPath(args[2]));
            return ExitOk;
        }

        private static int CompileCommand(string[] args)
        {
            if (args.Length < 4)
            {
                Console.Error.WriteLine(
                    "usage: navmesh compile <donor-blob> <mesh.obj> <out-blob> [--graph N] " +
                    "[--legacy-island-sentinel] [--island-base 0|1]");
                return ExitUsage;
            }

            string donorPath = args[1];
            string objPath = args[2];
            string outPath = args[3];

            if (string.Equals(Path.GetFullPath(donorPath), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("refusing to overwrite the donor in place; choose a different output path");
                return ExitUsage;
            }

            NavCompileOptions options = new NavCompileOptions
            {
                GraphIndex = GetIntOption(args, "--graph", 0),
                UseLegacyIslandSentinel = HasFlag(args, "--legacy-island-sentinel"),
                Islands = new IslandLabelOptions { FirstIslandId = GetIntOption(args, "--island-base", 1) },
            };

            byte[] donorPayload = File.ReadAllBytes(donorPath);
            NavMeshImage image = NavMeshImage.Read(donorPayload);

            NavChecksum.WordOrder wordOrder =
                image.ChecksumCalibration != null && image.ChecksumCalibration.DecidedOrder.HasValue
                    ? image.ChecksumCalibration.DecidedOrder.Value
                    : NavChecksum.DefaultWordOrder;

            Console.WriteLine("donor           " + Path.GetFullPath(donorPath));
            Console.WriteLine("donor graphs    " + image.NavSet.Graphs.Count +
                              ", target graph " + options.GraphIndex);

            if (image.ChecksumCalibration != null)
            {
                Console.WriteLine("checksum        " + image.ChecksumCalibration);
            }

            NavGraph target = image.NavSet.Graphs[options.GraphIndex];
            Console.WriteLine("inherited hdr   version=" + target.Version +
                              " buildScale=" + target.BuildScale.ToString("R", CultureInfo.InvariantCulture) +
                              " voxSize=" + target.VoxelSize.ToString("R", CultureInfo.InvariantCulture) +
                              " radius=" + target.AgentRadius.ToString("R", CultureInfo.InvariantCulture) +
                              " step=" + target.StepHeight.ToString("R", CultureInfo.InvariantCulture) +
                              " height=" + target.AgentHeight.ToString("R", CultureInfo.InvariantCulture));

            ObjMesh mesh = ObjParser.ParseFile(objPath);
            Console.WriteLine("mesh            " + mesh.Vertices.Count + " vertex/vertices, " +
                              mesh.Faces.Count + " face(s), " + mesh.WeldedVertexCount + " welded");

            NavCompileResult result = NavGraphCompiler.Compile(image, mesh, options);
            Console.WriteLine("compiled        " + result);

            for (int i = 0; i < result.Warnings.Count; i++)
            {
                Console.WriteLine("WARNING         " + result.Warnings[i]);
            }

            IslandVerification verification = IslandLabeler.Verify(target);
            Console.WriteLine("connectivity    " + verification);

            byte[] output = image.Write(wordOrder);
            File.WriteAllBytes(outPath, output);
            Console.WriteLine("wrote           " + Path.GetFullPath(outPath) + " (" + output.Length + " bytes)");

            // Immediate self-check: the file just written must parse back and re-serialize to
            // itself. Cheap, and it catches a writer regression before anything reaches a project.
            NavRoundTripResult verify = NavRoundTrip.Run(output);
            Console.WriteLine("self-check      " + verify);

            if (!verify.IsByteExact)
            {
                Console.Error.WriteLine("SELF-CHECK FAILED: the written blob does not round-trip. Do not use it.");
                return ExitFailed;
            }

            Console.WriteLine();
            Console.WriteLine("Blob size changed from " + donorPayload.Length + " to " + output.Length + " bytes.");
            Console.WriteLine("Whatever imports this must update the blob size and the chunk size array to");
            Console.WriteLine("match, or the runtime will read a truncated image.");

            return ExitOk;
        }

        private static int Islands(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine(
                    "usage: navmesh islands <blob> [--verify] [--relabel <out-blob>] [--island-base 0|1]");
                return ExitUsage;
            }

            string? relabelPath = GetOption(args, "--relabel");
            int islandBase = GetIntOption(args, "--island-base", 1);

            byte[] payload = File.ReadAllBytes(args[1]);
            NavMeshImage image = NavMeshImage.Read(payload);

            bool allConsistent = true;

            for (int i = 0; i < image.NavSet.Graphs.Count; i++)
            {
                NavGraph graph = image.NavSet.Graphs[i];
                Console.WriteLine("graph " + i + " stored : " + IslandLabeler.Describe(graph));

                IslandVerification verification = IslandLabeler.Verify(graph);
                Console.WriteLine("graph " + i + " actual : " + verification);
                allConsistent &= verification.IsConsistent;

                if (relabelPath != null)
                {
                    IslandLabelResult labelled = IslandLabeler.Label(
                        graph, new IslandLabelOptions { FirstIslandId = islandBase });
                    Console.WriteLine("graph " + i + " relabel: " + labelled);
                }
            }

            if (relabelPath != null)
            {
                NavChecksum.WordOrder wordOrder =
                    image.ChecksumCalibration != null && image.ChecksumCalibration.DecidedOrder.HasValue
                        ? image.ChecksumCalibration.DecidedOrder.Value
                        : NavChecksum.DefaultWordOrder;

                byte[] output = image.Write(wordOrder);
                File.WriteAllBytes(relabelPath, output);
                Console.WriteLine("wrote " + Path.GetFullPath(relabelPath) + " (" + output.Length + " bytes)");

                // Relabelling touches only bits inside an existing word, so the blob length must be
                // unchanged. If it is not, something else moved and that is a bug worth stopping on.
                if (output.Length != payload.Length)
                {
                    Console.Error.WriteLine(
                        "UNEXPECTED: relabelling changed the blob length from " + payload.Length +
                        " to " + output.Length + ". Island ids are packed into an existing field and " +
                        "must not move anything. Do not use this output.");
                    return ExitFailed;
                }
            }

            if (HasFlag(args, "--verify"))
            {
                return allConsistent ? ExitOk : ExitFailed;
            }

            return ExitOk;
        }

        private static int MakeFixture(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: navmesh make-fixture <out-dir>");
                return ExitUsage;
            }

            string dir = args[1];
            Directory.CreateDirectory(dir);

            string twoIslands = Path.Combine(dir, "two-island.obj");
            string oneIsland = Path.Combine(dir, "one-island.obj");

            File.WriteAllText(twoIslands, FixtureMeshes.TwoIsland());
            File.WriteAllText(oneIsland, FixtureMeshes.OneIsland());

            Console.WriteLine("wrote " + Path.GetFullPath(twoIslands));
            Console.WriteLine("wrote " + Path.GetFullPath(oneIsland));

            // Compile both against a synthetic donor purely to report what they produce. The
            // synthetic donor is NOT valid game content and is not written anywhere.
            Report(twoIslands, "two-island.obj");
            Report(oneIsland, "one-island.obj");

            return ExitOk;
        }

        private static int MakeDonor(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: navmesh make-donor <out-blob> [--graphs N] [--big-endian] [--pointer32]");
                return ExitUsage;
            }

            int graphs = GetIntOption(args, "--graphs", 1);
            Bfii.Navmesh.Io.ByteOrder order = HasFlag(args, "--big-endian")
                ? Bfii.Navmesh.Io.ByteOrder.Big
                : Bfii.Navmesh.Io.ByteOrder.Little;
            uint pointerCode = HasFlag(args, "--pointer32") ? 0u : 1u;

            NavMeshImage image = graphs > 1
                ? SyntheticImage.MultiGraph(graphs, 4, 4, order, pointerCode)
                : SyntheticImage.Grid(4, 4, 1f, order, pointerCode);

            byte[] payload = image.Write();
            File.WriteAllBytes(args[1], payload);

            Console.WriteLine("wrote " + Path.GetFullPath(args[1]) + " (" + payload.Length + " bytes, " +
                              graphs + " graph(s), " + order + ", " +
                              (pointerCode == 1 ? "64-bit" : "32-bit") + " pointers)");
            Console.WriteLine();
            Console.WriteLine("THIS IS NOT A VALID BFII DONOR AND MUST NEVER BE SHIPPED AS CONTENT.");
            Console.WriteLine("Its build scale, voxel size, agent radius, step height, agent height and");
            Console.WriteLine("252-byte header tail are invented, not observed. It exists so this tool can");
            Console.WriteLine("be exercised end to end with no game data present. Real work uses a shipped");
            Console.WriteLine("blob as the donor, because that is what carries the correct header and");
            Console.WriteLine("therefore the correct image version.");

            return ExitOk;
        }

        private static void Report(string objPath, string label)
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            ObjMesh mesh = ObjParser.ParseFile(objPath);
            NavCompileResult result = NavGraphCompiler.Compile(donor, mesh);

            Console.WriteLine("  " + label + ": " + mesh.Vertices.Count + " vertices, " +
                              mesh.Faces.Count + " faces -> " + result);
        }

        private static bool HasFlag(string[] args, string name)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? GetOption(string[] args, string name)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        private static int GetIntOption(string[] args, string name, int fallback)
        {
            string? raw = GetOption(args, name);
            if (raw == null)
            {
                return fallback;
            }

            int value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                throw new ArgumentException(name + " needs an integer, got '" + raw + "'");
            }

            return value;
        }
    }
}
