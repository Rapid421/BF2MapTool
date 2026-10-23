using System;
using System.IO;
using FrostyCLI.Core;
using Frosty.Core;
using FrostySdk.Managers;

namespace FrostyCLI.Commands
{
    public class ImportCommand : ICommand
    {
        public string Name => "import";

        // Seam: the real importer. Returns null on success, else an error message.
        // Args: ebxPath, filePath, formatOverride (may be null), generateMipmaps.
        internal static Func<string, string, string, bool, string> ImportTextureFn = RealImport;

        private static string RealImport(string ebx, string file, string fmt, bool mips)
        {
            if (App.AssetManager == null) return "AssetManager not initialized.";
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebx);
            if (entry == null) return "asset not found: " + ebx;
            return FrostyCLI.Adapters.Texture.HeadlessTextureImporter.Import(entry, file, fmt, mips);
        }

        // Seam: the real mesh importer. Returns null on success, else an error message.
        // Args: ebxPath, filePath, scale.
        internal static Func<string, string, float, string, string> ImportMeshFn = RealImportMesh;

        private static string RealImportMesh(string ebx, string file, float scale, string skeleton)
        {
            if (App.AssetManager == null) return "AssetManager not initialized.";
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebx);
            if (entry == null) return "asset not found: " + ebx;
            // Surface FBX load diagnostics on stderr (the plugin logs load failures via ILogger).
            return FrostyCLI.Adapters.Mesh.HeadlessMeshImporter.Import(entry, file, scale, new ExportLogger(false), skeleton);
        }

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1) { Console.Error.WriteLine("Usage: frostycli import <texture|mesh|sound|resource> <file> --as <ebx-path> [options]"); return 1; }
            switch (args[0].ToLowerInvariant())
            {
                case "texture":  return ExecuteTexture(Sub(args), logger, formatter);
                case "mesh":     return ExecuteMesh(Sub(args), logger, formatter);
                case "sound":    return ExecuteSound(Sub(args), logger, formatter);
                case "resource": return ExecuteResource(Sub(args), logger, formatter);
                default: Console.Error.WriteLine("import: unknown subcommand '" + args[0] + "'"); return 1;
            }
        }
        private static string[] Sub(string[] a) { var r = new string[a.Length - 1]; Array.Copy(a, 1, r, 0, r.Length); return r; }

        internal sealed class Parsed
        {
            public string File; public string Ebx; public string Format; public bool GenerateMipmaps = true;
            public string Template = ""; // with --template, create --as as a NEW texture cloned from this, then import
        }

        // Returns null + sets parsed on success, else returns an error message.
        internal static string ParseTextureArgs(string[] args, out Parsed parsed)
        {
            parsed = new Parsed();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--as" && i + 1 < args.Length) parsed.Ebx = args[++i];
                else if (args[i] == "--format" && i + 1 < args.Length) parsed.Format = args[++i];
                else if (args[i] == "--template" && i + 1 < args.Length) parsed.Template = args[++i];
                else if (args[i] == "--no-mipmaps") parsed.GenerateMipmaps = false;
                else if (!args[i].StartsWith("--")) parsed.File = args[i];
            }
            if (parsed.File == null) return "import texture: missing <file>";
            if (parsed.Ebx == null)  return "import texture: missing --as <ebx-path>";
            if (!System.IO.File.Exists(parsed.File)) return "import texture: file not found: " + parsed.File;
            if (parsed.Format == null)
            {
                string ext = Path.GetExtension(parsed.File).TrimStart('.').ToUpperInvariant();
                if (ext != "DDS" && ext != "PNG" && ext != "TGA" && ext != "HDR")
                    return "import texture: unsupported file type '." + ext + "' (use DDS/PNG/TGA/HDR or pass --format)";
                parsed.Format = ext;
            }
            else
            {
                string f = parsed.Format.ToUpperInvariant();
                if (f != "DDS" && f != "PNG" && f != "TGA" && f != "HDR")
                    return "import texture: invalid --format '" + parsed.Format + "'";
                parsed.Format = f;
            }
            return null;
        }

        public static int ExecuteTexture(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            Parsed p;
            string err = ParseTextureArgs(args, out p);
            if (err != null) { Console.Error.WriteLine(err); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("import texture: no project loaded."); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "import texture into {0} from {1}{2}", p.Ebx, p.File, string.IsNullOrEmpty(p.Template) ? "" : " (new from template " + p.Template + ")"); return 0; }

            // --template: create --as as a brand-new texture asset cloned from the template (its own
            // EBX + res + chunk), then import the image into it -- a true new texture, not a replace.
            if (!string.IsNullOrEmpty(p.Template))
            {
                string cloneErr = FrostyCLI.Adapters.Texture.TextureCloner.Clone(p.Template, p.Ebx, out _);
                if (cloneErr != null) { Console.Error.WriteLine("import texture: clone from template failed: " + cloneErr); return 1; }
            }

            string importErr = ImportTextureFn(p.Ebx, p.File, p.Format, p.GenerateMipmaps);
            if (importErr != null) { Console.Error.WriteLine("import texture: " + importErr); return 1; }
            logger.Log("import texture: {0} {1} from {2}", string.IsNullOrEmpty(p.Template) ? "replaced" : "created", p.Ebx, p.File);
            return 0;
        }

        internal sealed class ParsedMesh
        {
            public string File; public string Ebx; public float Scale = 1.0f; public string Skeleton = "";
            public string Template = "";   // when set, create --as as a NEW mesh cloned from this template, then import
            public int Sections = 0;       // with --template: grow each LOD to this many sections before import (for FBX with more sections)
        }

        // Returns null + sets parsed on success, else returns an error message.
        internal static string ParseMeshArgs(string[] args, out ParsedMesh parsed)
        {
            parsed = new ParsedMesh();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--as" && i + 1 < args.Length) parsed.Ebx = args[++i];
                else if (args[i] == "--scale" && i + 1 < args.Length)
                {
                    if (!float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float s) || s <= 0f)
                        return "import mesh: --scale must be a positive number";
                    parsed.Scale = s; i++;
                }
                else if (args[i] == "--skeleton" && i + 1 < args.Length) parsed.Skeleton = args[++i];
                else if (args[i] == "--template" && i + 1 < args.Length) parsed.Template = args[++i];
                else if (args[i] == "--sections" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[i + 1], out int n) || n <= 0)
                        return "import mesh: --sections must be a positive integer";
                    parsed.Sections = n; i++;
                }
                else if (!args[i].StartsWith("--")) parsed.File = args[i];
            }
            if (parsed.File == null) return "import mesh: missing <file>";
            if (parsed.Ebx == null)  return "import mesh: missing --as <ebx-path>";
            if (parsed.Sections > 0 && string.IsNullOrEmpty(parsed.Template))
                return "import mesh: --sections requires --template (it grows the new cloned mesh)";
            if (!System.IO.File.Exists(parsed.File)) return "import mesh: file not found: " + parsed.File;
            string ext = Path.GetExtension(parsed.File).TrimStart('.').ToUpperInvariant();
            if (ext != "FBX") return "import mesh: unsupported file type '." + ext + "' (only FBX is supported)";
            return null;
        }

        public static int ExecuteMesh(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            ParsedMesh p;
            string err = ParseMeshArgs(args, out p);
            if (err != null) { Console.Error.WriteLine(err); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("import mesh: no project loaded."); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "import mesh into {0} from {1}{2}", p.Ebx, p.File, string.IsNullOrEmpty(p.Template) ? "" : " (new from template " + p.Template + ")"); return 0; }

            // --template: create --as as a brand-new mesh asset cloned from the template (its own EBX +
            // res + chunks), then import the FBX into it -- a true new mesh, not a replace.
            if (!string.IsNullOrEmpty(p.Template))
            {
                string cloneErr = FrostyCLI.Adapters.Mesh.MeshCloner.Clone(p.Template, p.Ebx, out var newEntry);
                if (cloneErr != null) { Console.Error.WriteLine("import mesh: clone from template failed: " + cloneErr); return 1; }

                // --sections: grow the new mesh's LODs so an FBX with more sections than the template
                // can be imported (the extra sections are filled by ProcessLod, or left empty).
                if (p.Sections > 0)
                {
                    string growErr = FrostyCLI.Adapters.Mesh.MeshCloner.GrowNewMeshSections(newEntry, p.Sections);
                    if (growErr != null) { Console.Error.WriteLine("import mesh: " + growErr); return 1; }
                    int grownBlocks;
                    string depotErr = FrostyCLI.Adapters.Mesh.MeshCloner.GrowDepotSections(newEntry, null, out grownBlocks);
                    if (depotErr != null) { Console.Error.WriteLine("import mesh: " + depotErr); return 1; }
                    if (grownBlocks > 0) logger.Log("import mesh: {0} shader block section(s) added to the depot", grownBlocks);
                }
                {
                    int residentLods;
                    string lerr = FrostyCLI.Adapters.Mesh.MeshCloner.MarkAllLodsResident(newEntry, out residentLods);
                    if (lerr != null) { Console.Error.WriteLine("import mesh: " + lerr); return 1; }
                    if (residentLods > 0) logger.Log("import mesh: {0} LOD(s) marked bundle-resident (IsBaseLod)", residentLods);
                }
            }

            string importErr = ImportMeshFn(p.Ebx, p.File, p.Scale, p.Skeleton);
            if (importErr != null) { Console.Error.WriteLine("import mesh: " + importErr); return 1; }
            logger.Log("import mesh: {0} {1} from {2}", string.IsNullOrEmpty(p.Template) ? "replaced" : "created", p.Ebx, p.File);
            return 0;
        }

        internal sealed class ParsedSound { public string File; public string Ebx; public int Variation = 0; public string Encoding = "auto"; public bool Flatten; }

        internal static string ParseSoundArgs(string[] args, out ParsedSound parsed)
        {
            parsed = new ParsedSound();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--as" && i + 1 < args.Length) parsed.Ebx = args[++i];
                else if (args[i] == "--variation" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[i + 1], out int v) || v < 0) return "import sound: --variation must be a non-negative integer";
                    parsed.Variation = v; i++;
                }
                else if (args[i] == "--ealayer3") parsed.Encoding = "ealayer3";
                else if (args[i] == "--pcm") parsed.Encoding = "pcm";
                else if (args[i] == "--flatten") parsed.Flatten = true;
                else if (!args[i].StartsWith("--")) parsed.File = args[i];
                else return "import sound: unknown or incomplete option " + args[i];
            }
            if (parsed.File == null) return "import sound: missing <file>";
            if (parsed.Ebx == null)  return "import sound: missing --as <ebx-path>";
            if (!System.IO.File.Exists(parsed.File)) return "import sound: file not found: " + parsed.File;
            string ext = Path.GetExtension(parsed.File).TrimStart('.').ToUpperInvariant();
            if (ext != "WAV") return "import sound: unsupported file type '." + ext + "' (requires an uncompressed 16-bit PCM WAV)";
            return null;
        }

        public static int ExecuteSound(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            ParsedSound p;
            string err = ParseSoundArgs(args, out p);
            if (err != null) { Console.Error.WriteLine(err); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("import sound: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("import sound: AssetManager not initialized."); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(p.Ebx);
            if (entry == null) { Console.Error.WriteLine("import sound: asset not found: " + p.Ebx); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "import sound into {0} (variation {1}, {2}) from {3}", p.Ebx, p.Variation, p.Encoding, p.File); return 0; }
            string importErr = FrostyCLI.Core.HeadlessSoundImporter.Import(entry, p.File, p.Variation, logger, p.Encoding, p.Flatten);
            if (importErr != null) { Console.Error.WriteLine(importErr); return 1; }
            logger.Log("import sound: imported {0} into {1} (variation {2}, {3})", p.File, p.Ebx, p.Variation, p.Encoding);
            return 0;
        }

        internal sealed class ParsedResource { public string File; public string As; public string Template = ""; }

        internal static string ParseResourceArgs(string[] args, out ParsedResource parsed)
        {
            parsed = new ParsedResource();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--as" && i + 1 < args.Length) parsed.As = args[++i];
                else if (args[i] == "--template" && i + 1 < args.Length) parsed.Template = args[++i];
                else if (!args[i].StartsWith("--")) parsed.File = args[i];
            }
            if (parsed.File == null) return "import resource: missing <file>";
            if (parsed.As == null)   return "import resource: missing --as <asset-or-res-path>";
            if (!System.IO.File.Exists(parsed.File)) return "import resource: file not found: " + parsed.File;
            return null;
        }

        // Replaces the underlying res blob of a resource-backed asset (AtlasTexture, SvgImage,
        // IesResource, ...) with raw bytes from <file>. Resolves --as to a res by: (1) direct res path,
        // or (2) a res-backed EBX whose root object exposes a `Resource` ResRid. With --template <src>,
        // first clones src into a brand-new asset (EBX + res) via AssetCloner, then writes into it.
        // The bytes are the decompressed res payload (what `res export` produces); ModifyRes compresses.
        public static int ExecuteResource(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            ParsedResource p;
            string err = ParseResourceArgs(args, out p);
            if (err != null) { Console.Error.WriteLine(err); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("import resource: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("import resource: AssetManager not initialized."); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "import resource into {0} from {1}{2}", p.As, p.File, string.IsNullOrEmpty(p.Template) ? "" : " (new from template " + p.Template + ")"); return 0; }

            ResAssetEntry target = null;
            bool created = false;
            if (!string.IsNullOrEmpty(p.Template))
            {
                EbxAssetEntry newEbx;
                string cloneErr = FrostyCLI.Core.AssetCloner.Clone(p.Template, p.As, out newEbx);
                if (cloneErr != null) { Console.Error.WriteLine("import resource: clone from template failed: " + cloneErr); return 1; }
                target = ResolveResViaEbx(newEbx);
                created = true;
                if (target == null) { Console.Error.WriteLine("import resource: cloned asset has no resolvable res."); return 1; }
            }

            if (target == null) target = ResolveRes(p.As);
            if (target == null)
            {
                Console.Error.WriteLine("import resource: could not resolve a res for '" + p.As +
                    "' (pass a res path, a res-backed EBX name, or --template <src> to create a new asset).");
                return 1;
            }

            byte[] data;
            try { data = System.IO.File.ReadAllBytes(p.File); }
            catch (Exception ex) { Console.Error.WriteLine("import resource: read failed: " + ex.Message); return 1; }

            try { App.AssetManager.ModifyRes(target.Name, data); }
            catch (Exception ex) { Console.Error.WriteLine("import resource: modify failed: " + ex.Message); return 1; }
            if (!App.AssetManager.GetResEntry(target.Name).IsModified)
            { Console.Error.WriteLine("import resource: ModifyRes did not take effect for " + target.Name); return 1; }

            logger.Log("import resource: {0} {1} (res {2}) from {3}", created ? "created" : "replaced", p.As, target.Name, p.File);
            return 0;
        }

        // res path directly, else a res-backed EBX whose root exposes a `Resource` ResRid.
        private static ResAssetEntry ResolveRes(string name)
        {
            ResAssetEntry direct = App.AssetManager.GetResEntry(name);
            if (direct != null) return direct;
            EbxAssetEntry ebx = App.AssetManager.GetEbxEntry(name);
            if (ebx == null) return null;
            return ResolveResViaEbx(ebx);
        }

        private static ResAssetEntry ResolveResViaEbx(EbxAssetEntry ebx)
        {
            try
            {
                // `Resource` is a ResourceRef struct -> ulong via its implicit operator (which the DLR
                // honors but Convert.ToUInt64 does NOT). Assets without a Resource throw -> caught.
                dynamic root = App.AssetManager.GetEbx(ebx).RootObject;
                ulong rid = (ulong)root.Resource;
                if (rid == 0) return null;
                return App.AssetManager.GetResEntry(rid);
            }
            catch { return null; }
        }
    }
}
