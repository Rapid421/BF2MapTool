using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FrostyCLI.Core;
using FrostyCLI.Adapters.Mesh;
using FrostySdk;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Commands
{
    // mesh radiosity-dump / radiosity-apply: move a mesh's lightmap UV channel between games.
    // See RadiosityUvPort for why the ported clones have an empty RadiosityTexCoord.
    public class MeshCommand : ICommand
    {
        public string Name => "mesh";

        // mesh clone <src mesh> --as <new mesh path> [--bundle <bundle>]
        // A brand-new mesh asset with the source's geometry: MeshCloner duplicates the EBX, the MeshSet res, every LOD
        // chunk (as NEW, mod-added chunks) and the shader block depot. A shipped mesh whose high LODs are
        // superbundle-resident in a superbundle the target level does not mount (SP-only props, e.g. the BFII
        // HangarMediumBlastdoor_Left_01) has no loadable LOD0/LOD1 there; the clone carries its own chunks, which
        // --bundle links (with the res and depot) into the target's bundle.
        private static int CloneMesh(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string src = null, dst = null, bundle = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--as" && i + 1 < args.Length) dst = args[++i];
                else if (args[i] == "--bundle" && i + 1 < args.Length) bundle = args[++i];
                else if (!args[i].StartsWith("--")) src = args[i];
            }
            if (src == null || dst == null) { Console.Error.WriteLine("mesh clone: usage <src mesh> --as <new mesh path> [--bundle <bundle>]"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("mesh clone: no project loaded."); return 1; }
            FrostySdk.Managers.EbxAssetEntry made;
            string err = MeshCloner.Clone(src, dst, out made);
            if (err != null) { Console.Error.WriteLine("mesh clone: " + err); return 1; }
            string occNote;
            string oerr = CloneOccluder(made, logger, out occNote);
            if (oerr != null) { Console.Error.WriteLine("mesh clone: " + oerr); return 1; }
            if (bundle != null)
            {
                int rc = FrostyCLI.Core.WriteOpBus.Execute("bundle-add", new[] { made.Name, "--to", bundle, "--with-resources" }, logger, formatter);
                if (rc != 0) return rc;
                string depot = made.Name.ToLowerInvariant() + "_mesh/blocks";
                FrostyCLI.Core.WriteOpBus.Execute("bundle-add", new[] { depot, "--to", bundle }, logger, formatter);
            }
            formatter.WriteResult("OK", "mesh clone", new Dictionary<string, string> { { "from", src }, { "to", made.Name }, { "note", MeshCloner.LastCloneNote ?? "" }, { "occluder", occNote ?? "none" } });
            return 0;
        }

        // The template's occluder mesh as the clone's own res (<clone>_occludermesh, linked so --with-resources ships it).
        // The EBX clone keeps the template's OccluderMeshResource, a res only the template's bundles carry: placed where
        // those bundles are not loaded, the reference resolves to -1 and the first entity using the mesh faults
        // (game+0x6E6ED6A -- the BFII SP HangarMediumBlastdoor_Left_01 mesh in the Hoth port, MISS_D1/D3).
        // mesh offset <mesh> [--dx m] [--dy m] [--dz m]: translate every vertex of the mesh (all LODs). Writes the
        // mesh's own chunks, so use it on a `mesh clone`, never on a shipped mesh other assets share.
        private static int OffsetMesh(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string mesh = null; float dx = 0, dy = 0, dz = 0, k = 1; float[] c = new float[3]; bool boxesOnly = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--boxes-only") { boxesOnly = true; continue; }
                if (args[i] == "--scale" && i + 1 < args.Length) { k = float.Parse(args[++i], CultureInfo.InvariantCulture); continue; }
                if (args[i] == "--about" && i + 1 < args.Length)
                {
                    string[] parts = args[++i].Split(',');
                    for (int j = 0; j < 3 && j < parts.Length; j++) c[j] = float.Parse(parts[j], CultureInfo.InvariantCulture);
                    continue;
                }
                if (args[i] == "--dx" && i + 1 < args.Length) dx = float.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--dy" && i + 1 < args.Length) dy = float.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--dz" && i + 1 < args.Length) dz = float.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (!args[i].StartsWith("--")) mesh = args[i];
            }
            if (mesh == null) { Console.Error.WriteLine("mesh offset: usage <mesh> [--dx m] [--dy m] [--dz m] [--scale k --about x,y,z] [--boxes-only]"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("mesh offset: no project loaded."); return 1; }
            var entry = Frosty.Core.App.AssetManager.GetEbxEntry(mesh);
            if (entry == null) { Console.Error.WriteLine("mesh offset: not found: " + mesh); return 1; }
            if (!entry.IsAdded) logger.LogWarning("mesh offset: " + mesh + " is a shipped mesh; its chunks are shared, so every user of it moves too.");
            int moved;
            string err = MeshCloner.TransformVertices(entry, k, c, new[] { dx, dy, dz }, boxesOnly, out moved);
            if (err != null) { Console.Error.WriteLine("mesh offset: " + err); return 1; }
            formatter.WriteResult("OK", "mesh offset", new Dictionary<string, string> { { "mesh", entry.Name }, { "vertices", moved.ToString() },
                { "offset", dx.ToString(CultureInfo.InvariantCulture) + "," + dy.ToString(CultureInfo.InvariantCulture) + "," + dz.ToString(CultureInfo.InvariantCulture) } });
            return 0;
        }

        internal static string CloneOccluder(FrostySdk.Managers.EbxAssetEntry made, ExportLogger logger, out string note)
        {
            note = null;
            FrostySdk.IO.EbxAsset asset = Frosty.Core.App.AssetManager.GetEbx(made);
            dynamic root = asset.RootObject;
            ulong rid;
            try { rid = (ulong)root.OccluderMeshResource; } catch { return null; }
            if (rid == 0) return null;
            FrostySdk.Managers.ResAssetEntry src = Frosty.Core.App.AssetManager.GetResEntry(rid);
            if (src == null) return "the template's occluder resource 0x" + rid.ToString("X16") + " is not in the catalog";
            byte[] bytes;
            using (var st = Frosty.Core.App.AssetManager.GetRes(src)) using (var ms = new System.IO.MemoryStream()) { st.CopyTo(ms); bytes = ms.ToArray(); }
            string herr = OccluderMeshResource.Check(bytes);
            if (herr != null) return "the template's occluder " + src.Name + ": " + herr;
            string name = made.Name.ToLowerInvariant() + "_occludermesh";
            if (Frosty.Core.App.AssetManager.GetResEntry(name) == null)
                Frosty.Core.App.AssetManager.AddRes(name, (FrostySdk.Managers.ResourceType)src.ResType, (byte[])(src.ResMeta ?? new byte[16]).Clone(), bytes);
            FrostySdk.Managers.ResAssetEntry re = Frosty.Core.App.AssetManager.GetResEntry(name);
            if (re == null) return "AddRes did not create " + name;
            root.OccluderMeshResource = re.ResRid;
            try { made.LinkAsset(re); } catch { }
            if (!EbxWriteCommand.CallModifyEbx(made.Name, asset, logger)) return "could not save the clone's occluder reference";
            note = name + " (" + OccluderMeshResource.Describe(bytes) + ")";
            return null;
        }

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: frostycli mesh <radiosity-dump [--triangles]|radiosity-apply|radiosity-info|radiosity-dump-batch|radiosity-apply-batch> ...");
                return 1;
            }
            string[] sub = new string[Math.Max(0, args.Length - 1)];
            Array.Copy(args, 1, sub, 0, sub.Length);
            switch (args[0].ToLowerInvariant())
            {
                case "radiosity-dump":  return Dump(sub, logger, formatter);
                case "radiosity-apply": return Apply(sub, logger, formatter);
                case "radiosity-info":  return Info(sub, logger, formatter);
                case "radiosity-dump-batch":  return DumpBatch(sub, logger, formatter);
                case "radiosity-apply-batch": return ApplyBatch(sub, logger, formatter);
                case "radiosity-dump-lods":  return DumpLods(sub, logger, formatter);
                case "radiosity-apply-lods": return ApplyLods(sub, logger, formatter);
                case "radiosity-from-texcoord": return RadiosityFromTexCoord(sub, logger, formatter);
                case "clone-radiosity": return MeshCloneRadiosity.Execute(sub, logger, formatter);
                case "shaders-batch": return ShadersBatch(sub, logger, formatter);
                case "layout": return Layout(sub, logger, formatter);
                case "bounds": return Bounds(sub, logger, formatter);
                case "donors": return Donors(sub, logger, formatter);
                case "geo-dump": return GeoDump(sub, logger, formatter);
                case "geo-parts": return GeoParts(sub, logger, formatter);
                case "geo-obj": return GeoObj(sub, logger, formatter);
                case "geo-info": return GeoInfo(sub, logger, formatter);
                case "geo-verify": return GeoVerify(sub, logger, formatter);
                case "clone": return CloneMesh(sub, logger, formatter);
                case "offset": return OffsetMesh(sub, logger, formatter);
                case "depot":
                {
                    if (sub.Length < 1) { Console.Error.WriteLine("mesh depot: usage <mesh>"); return 1; }
                    var e = Frosty.Core.App.AssetManager.GetEbxEntry(sub[0]);
                    if (e == null) { Console.Error.WriteLine("mesh depot: not found: " + sub[0]); return 1; }
                    List<string> lines; string err = MeshCloner.DescribeDepot(e, out lines);
                    if (err != null) { Console.Error.WriteLine("mesh depot: " + err); return 1; }
                    foreach (string l in lines) Console.WriteLine(l);
                    return 0;
                }
                case "bones":
                {
                    if (sub.Length < 1) { Console.Error.WriteLine("mesh bones: usage <mesh>"); return 1; }
                    var e = Frosty.Core.App.AssetManager.GetEbxEntry(sub[0]);
                    if (e == null) { Console.Error.WriteLine("mesh bones: not found: " + sub[0]); return 1; }
                    List<string> lines; string err = MeshCloner.DescribeBones(e, out lines);
                    if (err != null) { Console.Error.WriteLine("mesh bones: " + err); return 1; }
                    foreach (string l in lines) Console.WriteLine(l);
                    return 0;
                }
                case "variation-dupe": return MeshVariationDupe.Execute(sub, logger, formatter);
                case "variation-plugin-run": return PluginVariationRun.Execute(sub, logger, formatter);
                case "depot-dump": return MeshDepotDump.Execute(sub, logger, formatter);
                case "aabb": return MeshAabbCommand.Execute(sub, logger, formatter);
                default:
                    Console.Error.WriteLine("mesh: unknown subcommand '" + args[0] + "'");
                    return 1;
            }
        }

        // mesh layout <mesh>... [--names a,b,c]: per LOD the section table (both geometry declarations, primitive
        // type, private per-section fields), subset categories, and the shader block depot as the runtime pairs it
        // with the sections (mesh-parameter block, holder, RVM surface reference). Read-only.
        private int Layout(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string extra = Opt(args, "--names");
            var names = extra != null ? new List<string>(extra.Split(',')) : null;
            int rc = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--names") { i++; continue; }
                if (args[i].StartsWith("--")) continue;
                var e = Frosty.Core.App.AssetManager.GetEbxEntry(args[i]);
                if (e == null) { Console.Error.WriteLine("mesh layout: not found: " + args[i]); rc = 1; continue; }
                List<string> lines;
                string err = MeshLayoutDump.Describe(e, names, out lines);
                foreach (string l in lines) Console.WriteLine(l);
                if (err != null) { Console.Error.WriteLine("mesh layout: " + err); rc = 1; }
            }
            return rc;
        }

        // mesh bounds <mesh>... [--list <file>] [--tolerance <m>] [--all]: every section's stored bounding box against the
        // positions it draws, and the MeshSet box against LOD0's (the engine culls by the MeshSet box; BFII stores no
        // section boxes -- all zero on shipped meshes -- so those print "not stored"). --all also gives every LOD's
        // per-section extents, which is how a port is compared with its 2015 source. Prints boxes whose vertices reach more than --tolerance
        // (default 0.05 m) outside their box (--all: every section). Read-only; exit 2 when any section is outside.
        private int Bounds(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string listFile = Opt(args, "--list"), tolArg = Opt(args, "--tolerance");
            bool all = Array.IndexOf(args, "--all") >= 0;
            float tol = tolArg != null ? float.Parse(tolArg, CultureInfo.InvariantCulture) : 0.05f;
            var meshes = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--list" || args[i] == "--tolerance") { i++; continue; }
                if (args[i].StartsWith("--")) continue;
                meshes.Add(args[i]);
            }
            if (listFile != null)
                foreach (string line in System.IO.File.ReadAllLines(listFile))
                {
                    string t = line.Split('	')[0].Trim();
                    if (t.Length > 0 && !t.StartsWith("#")) meshes.Add(t);
                }
            if (meshes.Count == 0) { Console.Error.WriteLine("mesh bounds: no meshes (give paths or --list <file>)"); return 1; }
            int rc = 0, badMeshes = 0, badSections = 0, unread = 0;
            foreach (string path in meshes)
            {
                var e = Frosty.Core.App.AssetManager.GetEbxEntry(path);
                if (e == null) { Console.Error.WriteLine("mesh bounds: not found: " + path); rc = 1; continue; }
                List<MeshCloner.SectionBoundsRow> rows;
                string err = MeshCloner.SectionBounds(e, out rows);
                if (err != null) { Console.Error.WriteLine("mesh bounds: " + path + ": " + err); rc = 1; continue; }
                var shown = new List<string>();
                int bad = 0;
                foreach (var r in rows)
                {
                    if (r.ActualMin == null && r.Lod >= 0 && r.Why != "no vertices") unread++;
                    bool outside = r.Overhang > tol;
                    if (outside && r.Lod >= 0) bad++;
                    if (!outside && !all) continue;
                    shown.Add("  " + (r.Lod < 0 ? "mesh" : "lod" + r.Lod + "/sec" + r.Section) + " '" + r.Name + "' box " + (r.Stored ? Box(r.StoredMin, r.StoredMax) : "(not stored)") +
                              " verts " + (r.ActualMin != null ? Box(r.ActualMin, r.ActualMax) : "(" + r.Why + ")") +
                              (outside ? " OUTSIDE by " + r.Overhang.ToString("0.###", CultureInfo.InvariantCulture) + " m" : ""));
                }
                if (bad > 0) { badMeshes++; badSections += bad; }
                if (shown.Count > 0) { Console.WriteLine(e.Name + (bad > 0 ? "  (" + bad + " section(s) outside their box)" : "")); foreach (string l in shown) Console.WriteLine(l); }
            }
            Console.WriteLine("mesh bounds: " + meshes.Count + " mesh(es), " + badMeshes + " with " + badSections + " section(s) drawing outside their bounding box" +
                              (unread > 0 ? ", " + unread + " section(s) unreadable" : ""));
            return rc != 0 ? rc : (badSections > 0 ? 2 : 0);
        }

        private static string Box(float[] mn, float[] mx)
        {
            if (mn == null || mx == null) return "(none)";
            Func<float, string> f = v => v.ToString("0.##", CultureInfo.InvariantCulture);
            return "(" + f(mn[0]) + "," + f(mn[1]) + "," + f(mn[2]) + ")..(" + f(mx[0]) + "," + f(mx[1]) + "," + f(mx[2]) + ")";
        }

        // mesh donors <shader>... [--resident <file>] [--max N] [--all]: every shipped mesh section drawn with the shader, grouped by
        // (named/unnamed, primitive type, vertex declaration hash) -- the layouts the game compiled that surface for.
        private int Donors(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string residentFile = Opt(args, "--resident"), maxArg = Opt(args, "--max");
            bool all = Array.IndexOf(args, "--all") >= 0;
            int max = maxArg != null ? int.Parse(maxArg, CultureInfo.InvariantCulture) : 400;
            HashSet<string> resident = null;
            if (residentFile != null)
            {
                resident = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string l in System.IO.File.ReadAllLines(residentFile)) { string t = l.Trim().ToLowerInvariant(); if (t.Length > 0) resident.Add(t); }
            }
            var index = FrostyCLI.Core.IndexService.GetRefIndex();
            Func<string, List<string>> users = g => { List<string> u; return index.Refs.TryGetValue(g, out u) ? u : null; };
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--resident" || args[i] == "--max") { i++; continue; }
                if (args[i].StartsWith("--")) continue;
                var se = Frosty.Core.App.AssetManager.GetEbxEntry(args[i]);
                if (se == null) { Console.Error.WriteLine("mesh donors: shader not found: " + args[i]); continue; }
                List<SurfaceDonors.Donor> donors = SurfaceDonors.Scan(se, resident, max, users);
                Console.WriteLine("== " + se.Name + " (" + se.Guid + "): " + donors.Count + " section(s)");
                var groups = new SortedDictionary<string, List<SurfaceDonors.Donor>>();
                foreach (var d in donors)
                {
                    string key = (d.Named ? "named" : "unnamed") + " " + d.Prim + " decl=" + d.DeclHash + " stride=" + d.Stride + " rvm=" + d.RvmRef + " cats=0x" + d.Categories.ToString("X");
                    List<SurfaceDonors.Donor> l;
                    if (!groups.TryGetValue(key, out l)) groups[key] = l = new List<SurfaceDonors.Donor>();
                    l.Add(d);
                }
                foreach (var kv in groups)
                {
                    int res = 0; foreach (var d in kv.Value) if (d.Resident) res++;
                    Console.WriteLine("  " + kv.Key + " x" + kv.Value.Count + " (resident " + res + ") [" + kv.Value[0].Decl + "] e.g. " + kv.Value[0].Mesh + " lod" + kv.Value[0].Lod + "/sec" + kv.Value[0].Section);
                    if (all) foreach (var d in kv.Value) Console.WriteLine("     " + d);
                }
            }
            return 0;
        }

        // mesh geo-parts <source mesh> --into <file.meshgeo> [--into ...] | --list <file: source mesh TAB meshgeo>: writes a composite
        // source's parts (part transforms and part boxes, the PARTS1 trailer) into an existing geometry dump without touching
        // its LODs -- a package dumped intact-state-only keeps its geometry and gains the part list a composite port needs.
        // Run under the source game's profile. Rigid sources write no parts.
        private int GeoParts(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            var jobs = new List<KeyValuePair<string, string>>();
            string list = Opt(args, "--list");
            if (list != null)
                foreach (string raw in System.IO.File.ReadAllLines(list))
                {
                    string[] parts = raw.Trim().Split('	');
                    if (parts.Length >= 2 && !parts[0].StartsWith("#")) jobs.Add(new KeyValuePair<string, string>(parts[0].Trim(), parts[1].Trim()));
                }
            string src = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--list") { i++; continue; }
                if (args[i] == "--into" && i + 1 < args.Length) { if (src != null) jobs.Add(new KeyValuePair<string, string>(src, args[++i])); else i++; continue; }
                if (!args[i].StartsWith("--")) src = args[i];
            }
            if (jobs.Count == 0) { Console.Error.WriteLine("mesh geo-parts: need <source mesh> --into <file.meshgeo>, or --list"); return 1; }
            int ok = 0, fail = 0, rigid = 0;
            foreach (var job in jobs)
            {
                var e = Frosty.Core.App.AssetManager.GetEbxEntry(job.Key);
                GeoMesh source = null, target;
                string err = e == null ? "mesh not found" : GeoMesh.Read(e, out source);
                if (err == null) err = GeoMesh.Load(job.Value, out target); else target = null;
                if (err != null) { logger.LogWarning("geo-parts {0}: {1}", job.Key, err); fail++; continue; }
                if (source.PartBoxes.Count == 0) { logger.Log("geo-parts {0}: not a composite (no parts)", job.Key); rigid++; continue; }
                target.PartTransforms = source.PartTransforms;
                target.PartBoxes = source.PartBoxes;
                target.Save(job.Value);
                logger.Log("geo-parts {0}: {1} part(s) -> {2}", job.Key, source.PartBoxes.Count, job.Value);
                ok++;
            }
            formatter.WriteResult(fail == 0 ? "OK" : "WARN", "mesh geo-parts", new Dictionary<string, string>
            { { "written", ok.ToString(CultureInfo.InvariantCulture) }, { "not_composite", rigid.ToString(CultureInfo.InvariantCulture) }, { "failed", fail.ToString(CultureInfo.InvariantCulture) } });
            return fail == 0 ? 0 : 1;
        }

        // mesh geo-dump (<mesh> | --list <file: mesh [TAB out-name]>) --outdir <dir>: MESHGEO1 files -- every LOD and section with
        // its declaration, raw vertex bytes and indices (MeshGeo.cs). Runs under either profile; the source game's for a port.
        // mesh geo-obj <mesh> [<mesh> ...] --outdir <dir> [--lod N]: a shipped mesh's geometry as a plain OBJ (positions,
        // TexCoord0, one group + usemtl per section named after the section), decoded with the CLI's own VertexCodec.
        // Frosty's MeshSetPlugin.FBXExporter throws a NullReferenceException headless (it needs the editor's task window),
        // so `export` / `export-batch` cannot write meshes; this is the reusable route (CoruscantBF3 base-game traffic craft).
        private int GeoObj(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string outDir = Opt(args, "--outdir"); string lodArg = Opt(args, "--lod");
            if (outDir == null) { Console.Error.WriteLine("mesh geo-obj: need --outdir <dir>"); return 1; }
            int lodIx = lodArg == null ? 0 : int.Parse(lodArg, CultureInfo.InvariantCulture);
            System.IO.Directory.CreateDirectory(outDir);
            int ok = 0, fail = 0;
            for (int a = 0; a < args.Length; a++)
            {
                if (args[a] == "--outdir" || args[a] == "--lod") { a++; continue; }
                if (args[a].StartsWith("--")) continue;
                var e = Frosty.Core.App.AssetManager.GetEbxEntry(args[a]);
                GeoMesh geo = null;
                string err = e == null ? "mesh not found" : GeoMesh.Read(e, out geo);
                if (err == null && geo.Lods.Count <= lodIx) err = "mesh has " + geo.Lods.Count + " LODs";
                if (err != null) { logger.LogWarning("geo-obj {0}: {1}", args[a], err); fail++; continue; }
                var ci = CultureInfo.InvariantCulture;
                // Composite meshes (vehicle landmarks: wings, cockpit, droid ...) store each part's vertices relative to
                // the part; the part = the vertex's first bone index, its placement = the LOD's PartTransforms entry.
                List<LinearTransform> parts = null;
                try
                {
                    ulong rid = (ulong)((dynamic)Frosty.Core.App.AssetManager.GetEbx(e).RootObject).MeshSetResource;
                    var ms = Frosty.Core.App.AssetManager.GetResAs<MeshSet>(Frosty.Core.App.AssetManager.GetResEntry(rid));
                    if (ms != null && ms.Lods.Count > lodIx && ms.Lods[lodIx].PartTransforms != null && ms.Lods[lodIx].PartTransforms.Count > 0)
                        parts = new List<LinearTransform>(ms.Lods[lodIx].PartTransforms);
                }
                catch { parts = null; }
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# " + e.Name + " LOD " + lodIx + " (FrostyCLI mesh geo-obj)" + (parts != null ? ", " + parts.Count + " part transforms applied" : ""));
                long baseV = 1; int tris = 0;
                foreach (GeoSection s in geo.Lods[lodIx].Sections)
                {
                    if (s.VertexCount == 0 || s.Prim != PrimitiveType.PrimitiveType_TriangleList) continue;
                    GeoElement pe = null, te = null, be = null;
                    foreach (GeoElement el in s.Elements)
                    {
                        if (el.Usage == VertexElementUsage.Pos && pe == null) pe = el;
                        else if (el.Usage == VertexElementUsage.TexCoord0 && te == null) te = el;
                        else if (el.Usage == VertexElementUsage.BoneIndices && be == null) be = el;
                    }
                    if (pe == null || !VertexCodec.Supported(pe.Format)) continue;
                    for (int v = 0; v < s.VertexCount; v++)
                    {
                        float[] p = VertexCodec.Decode(s.Vertices, v * s.Stride + pe.Offset, pe.Format);
                        if (parts != null && be != null)
                        {
                            int at = v * s.Stride + be.Offset;
                            int part = be.Format == VertexElementFormat.UByte4 ? s.Vertices[at] : (int)Math.Round(VertexCodec.Decode(s.Vertices, at, be.Format)[0]);
                            if (part >= 0 && part < parts.Count)
                            {
                                LinearTransform t0 = parts[part];
                                float x = p[0], y = p[1], z = p[2];
                                p = new float[] { x * t0.right.x + y * t0.up.x + z * t0.forward.x + t0.trans.x,
                                                  x * t0.right.y + y * t0.up.y + z * t0.forward.y + t0.trans.y,
                                                  x * t0.right.z + y * t0.up.z + z * t0.forward.z + t0.trans.z };
                            }
                        }
                        sb.Append("v ").Append(p[0].ToString("R", ci)).Append(' ').Append(p[1].ToString("R", ci)).Append(' ').Append(p[2].ToString("R", ci)).AppendLine();
                        float[] t = te != null && VertexCodec.Supported(te.Format) ? VertexCodec.Decode(s.Vertices, v * s.Stride + te.Offset, te.Format) : new float[] { 0, 0 };
                        sb.Append("vt ").Append(t[0].ToString("R", ci)).Append(' ').Append((1f - t[1]).ToString("R", ci)).AppendLine();
                    }
                    string sn = "m" + s.MaterialId.ToString(CultureInfo.InvariantCulture) + "_" + (s.Name.Length > 0 ? s.Name : "section");
                    sb.AppendLine("g " + sn).AppendLine("usemtl " + sn);
                    for (int i = 0; i + 2 < s.Indices.Length; i += 3)
                    {
                        sb.AppendFormat(ci, "f {0}/{0} {1}/{1} {2}/{2}", baseV + s.Indices[i], baseV + s.Indices[i + 1], baseV + s.Indices[i + 2]).AppendLine();
                        tris++;
                    }
                    baseV += s.VertexCount;
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, System.IO.Path.GetFileName(e.Name) + ".obj"), sb.ToString());
                logger.Log("geo-obj {0}: {1} vertices, {2} triangles", e.Name, baseV - 1, tris);
                ok++;
            }
            formatter.WriteResult(fail == 0 ? "OK" : "WARN", "mesh geo-obj", new Dictionary<string, string>
            { { "written", ok.ToString(CultureInfo.InvariantCulture) }, { "failed", fail.ToString(CultureInfo.InvariantCulture) }, { "outdir", outDir } });
            return ok == 0 && fail > 0 ? 1 : 0;
        }

        private int GeoDump(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string list = Opt(args, "--list"), outDir = Opt(args, "--outdir");
            if (outDir == null) { Console.Error.WriteLine("mesh geo-dump: need --outdir <dir>"); return 1; }
            var jobs = new List<KeyValuePair<string, string>>();
            if (list != null)
                foreach (string raw in System.IO.File.ReadAllLines(list))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string[] parts = line.Split('	');
                    jobs.Add(new KeyValuePair<string, string>(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null));
                }
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--list" || args[i] == "--outdir") { i++; continue; }
                if (!args[i].StartsWith("--")) jobs.Add(new KeyValuePair<string, string>(args[i], null));
            }
            System.IO.Directory.CreateDirectory(outDir);
            int ok = 0, fail = 0;
            foreach (var job in jobs)
            {
                var e = Frosty.Core.App.AssetManager.GetEbxEntry(job.Key);
                GeoMesh geo = null;
                string err = e == null ? "mesh not found" : GeoMesh.Read(e, out geo);
                if (err != null) { logger.LogWarning("geo-dump {0}: {1}", job.Key, err); fail++; continue; }
                string outName = job.Value ?? (System.IO.Path.GetFileName(e.Name) + ".meshgeo");
                geo.Save(System.IO.Path.Combine(outDir, outName));
                ok++;
            }
            formatter.WriteResult(fail == 0 ? "OK" : "WARN", "mesh geo-dump", new Dictionary<string, string>
            { { "dumped", ok.ToString(CultureInfo.InvariantCulture) }, { "failed", fail.ToString(CultureInfo.InvariantCulture) }, { "outdir", outDir } });
            return ok == 0 && fail > 0 ? 1 : 0;
        }

        // mesh geo-verify --list <file: mesh TAB source.meshgeo>: each ported mesh against its source dump (MeshGeoPort.Verify).
        private int GeoVerify(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string list = Opt(args, "--list");
            if (list == null) { Console.Error.WriteLine("mesh geo-verify: need --list <file>"); return 1; }
            int exact = 0, differ = 0, fail = 0; long values = 0;
            foreach (string raw in System.IO.File.ReadAllLines(list))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] parts = line.Split('\t');
                GeoMesh src, dst = null;
                string err = GeoMesh.Load(parts[1].Trim(), out src);
                var e = err == null ? Frosty.Core.App.AssetManager.GetEbxEntry(parts[0].Trim()) : null;
                if (err == null && e == null) err = "mesh not found";
                if (err == null) err = GeoMesh.Read(e, out dst);
                if (err != null) { Console.WriteLine("FAIL  " + parts[0] + ": " + err); fail++; continue; }
                int n;
                List<string> issues = MeshGeoPort.Verify(src, dst, out n);
                values += n;
                if (issues.Count == 0) { exact++; Console.WriteLine("EXACT " + parts[0] + " (" + n + " values)"); }
                else { differ++; Console.WriteLine("DIFF  " + parts[0] + ": " + string.Join("; ", issues.Take(8)) + (issues.Count > 8 ? " ... +" + (issues.Count - 8) : "")); }
            }
            formatter.WriteResult(differ == 0 && fail == 0 ? "OK" : "WARN", "mesh geo-verify", new Dictionary<string, string>
            { { "exact", exact.ToString() }, { "differ", differ.ToString() }, { "failed", fail.ToString() }, { "values_checked", values.ToString() } });
            return differ == 0 && fail == 0 ? 0 : 2;
        }

        // mesh geo-info <file.meshgeo> [--vertices N]: the dump's layout and, with --vertices, decoded element values.
        private int GeoInfo(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string file = Positional(args), nArg = Opt(args, "--vertices");
            int n = nArg != null ? int.Parse(nArg, CultureInfo.InvariantCulture) : 0;
            GeoMesh geo;
            string err = GeoMesh.Load(file, out geo);
            if (err != null) { Console.Error.WriteLine("mesh geo-info: " + err); return 1; }
            Console.WriteLine("meshgeo " + geo.Path + " (" + geo.SourceGame + ") lods=" + geo.Lods.Count);
            for (int l = 0; l < geo.Lods.Count; l++)
            {
                GeoLod lod = geo.Lods[l];
                var cats = new List<string>();
                for (int c = 0; c < lod.Categories.Count; c++) cats.Add(c + ":[" + string.Join(",", lod.Categories[c]) + "]");
                Console.WriteLine("lod" + l + " flags=0x" + lod.Flags.ToString("X") + " cats " + string.Join(" ", cats));
                for (int s = 0; s < lod.Sections.Count; s++)
                {
                    GeoSection sec = lod.Sections[s];
                    Console.WriteLine("  sec" + s + " '" + sec.Name + "' mat=" + sec.MaterialId + " " + sec.Prim + " v=" + sec.VertexCount + " p=" + sec.PrimitiveCount + " ratios=" + string.Join(",", sec.TexCoordRatios) + " [" + sec.DeclString() + "]");
                    if (Array.IndexOf(args, "--stats") >= 0 && sec.VertexCount > 0)
                        foreach (GeoElement e in sec.Elements)
                        {
                            int k = VertexCodec.Components(e.Format);
                            var mn = new float[4]; var mx = new float[4];
                            for (int i = 0; i < 4; i++) { mn[i] = float.MaxValue; mx[i] = float.MinValue; }
                            var distinct = new HashSet<string>();
                            for (int v = 0; v < sec.VertexCount; v++)
                            {
                                float[] x = VertexCodec.Decode(sec.Vertices, v * sec.Stride + e.Offset, e.Format);
                                for (int i = 0; i < k; i++) { if (x[i] < mn[i]) mn[i] = x[i]; if (x[i] > mx[i]) mx[i] = x[i]; }
                                if (distinct.Count < 5) distinct.Add(string.Join(",", Array.ConvertAll(x, f => f.ToString("0.###", CultureInfo.InvariantCulture))));
                            }
                            var rng = new List<string>();
                            for (int i = 0; i < k; i++) rng.Add(mn[i].ToString("0.###", CultureInfo.InvariantCulture) + ".." + mx[i].ToString("0.###", CultureInfo.InvariantCulture));
                            bool constant = true; for (int i = 0; i < k; i++) if (mn[i] != mx[i]) constant = false;
                            Console.WriteLine("      " + e.Usage + " " + (constant ? "CONSTANT " : "") + string.Join(" ", rng));
                        }
                    for (int v = 0; v < n && v < sec.VertexCount; v++)
                    {
                        var parts = new List<string>();
                        foreach (GeoElement e in sec.Elements)
                        {
                            float[] x = VertexCodec.Decode(sec.Vertices, v * sec.Stride + e.Offset, e.Format);
                            int k = VertexCodec.Components(e.Format);
                            var comps = new List<string>();
                            for (int i = 0; i < k; i++) comps.Add(x[i].ToString("0.#####", CultureInfo.InvariantCulture));
                            parts.Add(e.Usage + "=(" + string.Join(",", comps) + ")");
                        }
                        Console.WriteLine("     v" + v + " " + string.Join(" ", parts));
                    }
                }
            }
            return 0;
        }

        private static string Positional(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
                if (!args[i].StartsWith("--")) return args[i];
            return null;
        }

        private static string Opt(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        private int Info(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string mesh = Positional(args);
            if (mesh == null) { Console.Error.WriteLine("mesh radiosity-info: missing <mesh-ebx-path>"); return 1; }
            var verts = new List<RadiosityUvPort.Vertex>();
            bool hasRad; string note;
            string err = RadiosityUvPort.Read(mesh, verts, out hasRad, out note);
            if (err != null) { Console.Error.WriteLine("mesh radiosity-info: " + err); return 1; }
            int nonZero = 0, inRange = 0;
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            foreach (RadiosityUvPort.Vertex v in verts)
            {
                if (Math.Abs(v.RU) > 1e-5f || Math.Abs(v.RV) > 1e-5f) nonZero++;
                if (v.RU >= -0.001f && v.RU <= 1.001f && v.RV >= -0.001f && v.RV <= 1.001f) inRange++;
                if (v.RU < minU) minU = v.RU; if (v.RU > maxU) maxU = v.RU;
                if (v.RV < minV) minV = v.RV; if (v.RV > maxV) maxV = v.RV;
            }
            formatter.WriteResult("OK", "mesh radiosity-info", new Dictionary<string, string>
            {
                { "mesh", mesh },
                { "detail", note },
                { "vertices", verts.Count.ToString(CultureInfo.InvariantCulture) },
                { "radiosity_nonzero", nonZero.ToString(CultureInfo.InvariantCulture) },
                { "radiosity_in_0_1", inRange.ToString(CultureInfo.InvariantCulture) },
                { "u_range", verts.Count == 0 ? "-" : minU.ToString("0.###") + " .. " + maxU.ToString("0.###") },
                { "v_range", verts.Count == 0 ? "-" : minV.ToString("0.###") + " .. " + maxV.ToString("0.###") },
            });
            return 0;
        }


        // radiosity-dump-lods --list <file: mesh [TAB out-name]> --outdir <dir>: every LOD's sections (RADUV3: vertices,
        // TexCoord0, lightmap UVs, triangles). Runs under either game profile.
        private int DumpLods(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string list = Opt(args, "--list"), outDir = Opt(args, "--outdir");
            if (list == null || outDir == null) { Console.Error.WriteLine("mesh radiosity-dump-lods: need --list <file> --outdir <dir>"); return 1; }
            if (!System.IO.File.Exists(list)) { Console.Error.WriteLine("list not found: " + list); return 1; }
            System.IO.Directory.CreateDirectory(outDir);
            int ok = 0, fail = 0;
            foreach (string raw in System.IO.File.ReadAllLines(list))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] parts = line.Split('\t');
                string mesh = parts[0].Trim();
                string outName = parts.Length > 1 ? parts[1].Trim() : mesh.Replace('/', '_') + ".raduv3";
                var lods = new List<List<RadiosityUvPort.LodSection>>();
                string note;
                string err = RadiosityUvPort.ReadAllLods(mesh, lods, out note);
                if (err != null) { logger.LogWarning("dump-lods {0}: {1}", mesh, err); fail++; continue; }
                RadiosityUvPort.SaveLods(lods, System.IO.Path.Combine(outDir, outName));
                ok++;
            }
            formatter.WriteResult("OK", "mesh radiosity-dump-lods", new Dictionary<string, string>
            { { "dumped", ok.ToString(CultureInfo.InvariantCulture) }, { "failed", fail.ToString(CultureInfo.InvariantCulture) } });
            return fail > 0 && ok == 0 ? 1 : 0;
        }

        // radiosity-apply-lods --list <file: mesh TAB raduv3> [--check-positions]: write each file's lightmap UVs onto its
        // mesh by vertex index, every LOD (see RadiosityUvPort.ApplyLodsByIndex). The files are prepared in the target's
        // vertex order (a port-import clone keeps its source's order; a re-cut piece carries a source index map).
        // radiosity-from-texcoord <mesh>... | --list <file> [--channel 1] [--restore-from 0|-1]: RadiosityTexCoord :=
        // TexCoord<channel> on every LOD section that has both, then TexCoord<channel> := TexCoord<restore-from> (default 0;
        // -1 keeps it). For meshes imported from FBX with the lightmap uvs as the second uv map: the importer writes
        // RadiosityTexCoord as zeros but fills TexCoord1 (see RadiosityUvPort.CopyTexCoordToRadiosity).
        private int RadiosityFromTexCoord(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            int channel = 1, restore = 0;
            var meshes = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--channel" && i + 1 < args.Length) channel = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--restore-from" && i + 1 < args.Length) restore = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--list" && i + 1 < args.Length)
                {
                    string list = args[++i];
                    if (!System.IO.File.Exists(list)) { Console.Error.WriteLine("list not found: " + list); return 1; }
                    foreach (string l in System.IO.File.ReadAllLines(list)) { string t = l.Trim(); if (t.Length > 0 && !t.StartsWith("#")) meshes.Add(t); }
                }
                else if (!args[i].StartsWith("--")) meshes.Add(args[i]);
            }
            if (meshes.Count == 0) { Console.Error.WriteLine("Usage: frostycli mesh radiosity-from-texcoord <mesh>... | --list <file> [--channel 1] [--restore-from 0|-1]"); return 1; }
            int ok = 0, fail = 0, written = 0, secs = 0, skipped = 0, oor = 0;
            foreach (string m in meshes)
            {
                int w, s, sk, o; string note;
                string err = RadiosityUvPort.CopyTexCoordToRadiosity(m, channel, restore, out w, out s, out sk, out o, out note);
                if (err != null) { logger.LogWarning("radiosity-from-texcoord {0}: {1}", m, err); fail++; continue; }
                ok++; written += w; secs += s; skipped += sk; oor += o;
                if (note != null || o > 0) logger.LogWarning("radiosity-from-texcoord {0}: {1} vertices, {2} outside 0..1{3}", m, w, o, note != null ? "; " + note : "");
            }
            formatter.WriteResult(fail == 0 ? "OK" : "WARN", "mesh radiosity-from-texcoord", new Dictionary<string, string>
            {
                { "meshes", ok.ToString(CultureInfo.InvariantCulture) }, { "failed", fail.ToString(CultureInfo.InvariantCulture) },
                { "sections", secs.ToString(CultureInfo.InvariantCulture) }, { "sections_skipped", skipped.ToString(CultureInfo.InvariantCulture) },
                { "vertices_written", written.ToString(CultureInfo.InvariantCulture) }, { "vertices_outside_0_1", oor.ToString(CultureInfo.InvariantCulture) },
            });
            return fail > 0 && ok == 0 ? 1 : 0;
        }

        private int ApplyLods(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string list = Opt(args, "--list");
            bool check = Array.IndexOf(args, "--check-positions") >= 0;
            // --tolerance <m>: position check threshold (default 1 mm). A port-import clone stores positions at the
            // template's precision (half floats on some Hoth templates: up to ~4 mm off 2015's floats); a reordered
            // or foreign vertex is metres off, so a centimetre still proves "same vertex, same order".
            float tol = 1e-3f;
            string tolArg = Opt(args, "--tolerance");
            if (tolArg != null) tol = float.Parse(tolArg, CultureInfo.InvariantCulture);
            if (list == null) { Console.Error.WriteLine("mesh radiosity-apply-lods: need --list <file>"); return 1; }
            if (!System.IO.File.Exists(list)) { Console.Error.WriteLine("list not found: " + list); return 1; }
            int meshes = 0, fail = 0, partial = 0, written = 0, total = 0;
            foreach (string raw in System.IO.File.ReadAllLines(list))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 2) { logger.LogWarning("apply-lods: '{0}' has no RADUV3 file", line); fail++; continue; }
                var src = new List<List<RadiosityUvPort.LodSection>>();
                string err = RadiosityUvPort.LoadLods(parts[1].Trim(), src);
                if (err == null)
                {
                    int w, t, skipped; string note;
                    err = RadiosityUvPort.ApplyLodsByIndex(parts[0].Trim(), src, check, out w, out t, out skipped, out note, tol);
                    if (err == null)
                    {
                        meshes++; written += w; total += t;
                        if (skipped > 0) { partial++; logger.LogWarning("apply-lods {0}: {1}/{2} vertices written, {3} section(s) skipped: {4}", parts[0], w, t, skipped, note); }
                        continue;
                    }
                }
                logger.LogWarning("apply-lods {0}: {1}", parts[0], err); fail++;
            }
            formatter.WriteResult("OK", "mesh radiosity-apply-lods", new Dictionary<string, string>
            {
                { "meshes", meshes.ToString(CultureInfo.InvariantCulture) },
                { "partial", partial.ToString(CultureInfo.InvariantCulture) },
                { "failed", fail.ToString(CultureInfo.InvariantCulture) },
                { "vertices_written", written.ToString(CultureInfo.InvariantCulture) },
                { "vertices_with_channel", total.ToString(CultureInfo.InvariantCulture) },
            });
            return fail > 0 ? 1 : 0;
        }

        // Loading a game profile costs ~8s and a big project ~20s, so 111 meshes have to run in one
        // process rather than one invocation each.
        private int DumpBatch(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string list = Opt(args, "--list"), outDir = Opt(args, "--outdir");
            if (list == null || outDir == null)
            { Console.Error.WriteLine("mesh radiosity-dump-batch: need --list <file> --outdir <dir>"); return 1; }
            if (!System.IO.File.Exists(list)) { Console.Error.WriteLine("list not found: " + list); return 1; }
            if (!System.IO.Directory.Exists(outDir)) System.IO.Directory.CreateDirectory(outDir);
            bool triangles = Array.IndexOf(args, "--triangles") >= 0;
            int ok = 0, fail = 0, noRad = 0, triTotal = 0;
            foreach (string raw in System.IO.File.ReadAllLines(list))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] parts = line.Split('	');
                string mesh = parts[0].Trim();
                string outName = parts.Length > 1 ? parts[1].Trim() : mesh.Replace('/', '_') + (triangles ? ".raduv2" : ".raduv");
                var verts = new List<RadiosityUvPort.Vertex>();
                var sections = new List<RadiosityUvPort.Section>();
                bool hasRad; string note;
                string err = triangles
                    ? RadiosityUvPort.ReadWithTriangles(mesh, verts, sections, out hasRad, out note)
                    : RadiosityUvPort.Read(mesh, verts, out hasRad, out note);
                if (err != null) { logger.LogWarning("dump {0}: {1}", mesh, err); fail++; continue; }
                if (!hasRad) { logger.LogWarning("dump {0}: no RadiosityTexCoord channel", mesh); noRad++; continue; }
                string outFile = System.IO.Path.Combine(outDir, outName);
                if (triangles)
                {
                    RadiosityUvPort.SaveWithTriangles(verts, sections, outFile);
                    foreach (RadiosityUvPort.Section s in sections) triTotal += s.Triangles.Length / 3;
                }
                else RadiosityUvPort.Save(verts, outFile);
                ok++;
            }
            var result = new Dictionary<string, string>
            { { "dumped", ok.ToString(CultureInfo.InvariantCulture) },
              { "no_radiosity", noRad.ToString(CultureInfo.InvariantCulture) },
              { "failed", fail.ToString(CultureInfo.InvariantCulture) } };
            if (triangles) result["triangles"] = triTotal.ToString(CultureInfo.InvariantCulture);
            formatter.WriteResult("OK", "mesh radiosity-dump-batch", result);
            return 0;
        }

        private int ApplyBatch(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string list = Opt(args, "--list"), tolArg = Opt(args, "--tolerance"), scaleArg = Opt(args, "--scale");
            if (list == null) { Console.Error.WriteLine("mesh radiosity-apply-batch: need --list <file>"); return 1; }
            if (!System.IO.File.Exists(list)) { Console.Error.WriteLine("list not found: " + list); return 1; }
            float tol = 0.02f, scale = 0f;
            if (tolArg != null) float.TryParse(tolArg, NumberStyles.Float, CultureInfo.InvariantCulture, out tol);
            if (scaleArg != null) float.TryParse(scaleArg, NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
            int done = 0, fail = 0, partial = 0, totalMatched = 0, totalVerts = 0;
            foreach (string raw in System.IO.File.ReadAllLines(list))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] parts = line.Split('	');
                if (parts.Length < 2) { logger.LogWarning("apply: bad line '{0}'", line); fail++; continue; }
                string mesh = parts[0].Trim(), from = parts[1].Trim();
                var src = new List<RadiosityUvPort.Vertex>();
                string lerr = RadiosityUvPort.Load(from, src);
                if (lerr != null) { logger.LogWarning("apply {0}: {1}", mesh, lerr); fail++; continue; }
                int matched, total; string note;
                string err = RadiosityUvPort.Apply(mesh, src, scale, tol, out matched, out total, out note);
                if (err != null) { logger.LogWarning("apply {0}: {1}", mesh, err); fail++; continue; }
                totalMatched += matched; totalVerts += total;
                if (matched < total) { partial++; logger.LogWarning("apply {0}: only {1}/{2} vertices matched ({3})", mesh, matched, total, note); }
                done++;
            }
            formatter.WriteResult("OK", "mesh radiosity-apply-batch", new Dictionary<string, string>
            { { "meshes", done.ToString(CultureInfo.InvariantCulture) },
              { "partial", partial.ToString(CultureInfo.InvariantCulture) },
              { "failed", fail.ToString(CultureInfo.InvariantCulture) },
              { "vertices_matched", totalMatched.ToString(CultureInfo.InvariantCulture) },
              { "vertices_total", totalVerts.ToString(CultureInfo.InvariantCulture) } });
            return 0;
        }


        // Prints mesh -> material shader paths for a list of meshes, in ONE process. The ported clones
        // inherited whatever shader their donor mesh used, so a metallic 2015 prop can end up on
        // SS_PropsNonMetallicPreset (or, for ContainerMedium_01, on the FLOOR shader) -- which is why
        // the port reads matte and unlike the reference no matter what the lighting does.
        private int ShadersBatch(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string list = Opt(args, "--list"), outPath = Opt(args, "--out");
            if (list == null || outPath == null)
            { Console.Error.WriteLine("mesh shaders-batch: need --list <file> --out <file>"); return 1; }
            var lines = new List<string>();
            int ok = 0, miss = 0;
            foreach (string raw in System.IO.File.ReadAllLines(list))
            {
                string mesh = raw.Trim();
                if (mesh.Length == 0 || mesh.StartsWith("#")) continue;
                var entry = Frosty.Core.App.AssetManager.GetEbxEntry(mesh);
                if (entry == null) { lines.Add(mesh + "	<missing>"); miss++; continue; }
                FrostySdk.IO.EbxAsset asset;
                try { asset = Frosty.Core.App.AssetManager.GetEbx(entry); }
                catch (Exception ex) { lines.Add(mesh + "	<error:" + ex.Message + ">"); miss++; continue; }
                var names = new List<string>();
                // Materials is a list of PointerRefs to MeshMaterial; each one's Shader is a
                // SurfaceShaderInstanceDataStruct whose own Shader field holds the real reference.
                // Walking asset.Objects for "MeshMaterial" found nothing, which is why the first
                // version of this reported every column empty.
                try
                {
                    dynamic root = asset.RootObject;
                    System.Collections.IList mats = root.Materials as System.Collections.IList;
                    if (mats != null)
                        foreach (object mslot in mats)
                        {
                            object mat = mslot;
                            try { dynamic pr = mslot; mat = pr.Internal; } catch { }
                            if (mat == null) continue;
                            try
                            {
                                dynamic m = mat;
                                dynamic shStruct = m.Shader;
                                dynamic shRef = shStruct.Shader;
                                Guid fg = (Guid)shRef.External.FileGuid;
                                var se = Frosty.Core.App.AssetManager.GetEbxEntry(fg);
                                names.Add(se != null ? se.Name : fg.ToString());
                            }
                            catch (Exception ex2) { names.Add("<err:" + ex2.GetType().Name + ">"); }
                        }
                    else names.Add("<no Materials list>");
                }
                catch (Exception ex3) { names.Add("<root err:" + ex3.GetType().Name + ">"); }
                lines.Add(mesh + "	" + string.Join(",", names.ToArray()));
                ok++;
            }
            System.IO.File.WriteAllLines(outPath, lines.ToArray());
            formatter.WriteResult("OK", "mesh shaders-batch", new Dictionary<string, string>
            { { "meshes", ok.ToString(CultureInfo.InvariantCulture) }, { "missing", miss.ToString(CultureInfo.InvariantCulture) },
              { "out", outPath } });
            return 0;
        }

        private int Dump(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string mesh = Positional(args);
            string outPath = Opt(args, "--out");
            if (mesh == null)    { Console.Error.WriteLine("mesh radiosity-dump: missing <mesh-ebx-path>"); return 1; }
            if (outPath == null) { Console.Error.WriteLine("mesh radiosity-dump: missing --out"); return 1; }
            bool triangles = Array.IndexOf(args, "--triangles") >= 0;
            var verts = new List<RadiosityUvPort.Vertex>();
            var sections = new List<RadiosityUvPort.Section>();
            bool hasRad; string note;
            string err = triangles
                ? RadiosityUvPort.ReadWithTriangles(mesh, verts, sections, out hasRad, out note)
                : RadiosityUvPort.Read(mesh, verts, out hasRad, out note);
            if (err != null) { Console.Error.WriteLine("mesh radiosity-dump: " + err); return 1; }
            if (!hasRad) { Console.Error.WriteLine("mesh radiosity-dump: " + mesh + " has no RadiosityTexCoord channel"); return 1; }
            if (triangles) RadiosityUvPort.SaveWithTriangles(verts, sections, outPath);
            else RadiosityUvPort.Save(verts, outPath);
            formatter.WriteResult("OK", "mesh radiosity-dump", new Dictionary<string, string>
            { { "mesh", mesh }, { "out", outPath }, { "detail", note } });
            return 0;
        }

        private int Apply(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string mesh = Positional(args);
            string from = Opt(args, "--from");
            string scaleArg = Opt(args, "--scale");
            string tolArg = Opt(args, "--tolerance");
            if (mesh == null) { Console.Error.WriteLine("mesh radiosity-apply: missing <mesh-ebx-path>"); return 1; }
            if (from == null) { Console.Error.WriteLine("mesh radiosity-apply: missing --from <file from radiosity-dump>"); return 1; }
            float scale = 0f, tol = 0.02f;
            if (scaleArg != null) float.TryParse(scaleArg, NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
            if (tolArg != null) float.TryParse(tolArg, NumberStyles.Float, CultureInfo.InvariantCulture, out tol);

            var src = new List<RadiosityUvPort.Vertex>();
            string lerr = RadiosityUvPort.Load(from, src);
            if (lerr != null) { Console.Error.WriteLine("mesh radiosity-apply: " + lerr); return 1; }

            int matched, total; string note;
            string err = RadiosityUvPort.Apply(mesh, src, scale, tol, out matched, out total, out note);
            if (err != null) { Console.Error.WriteLine("mesh radiosity-apply: " + err); return 1; }
            formatter.WriteResult("OK", "mesh radiosity-apply", new Dictionary<string, string>
            {
                { "mesh", mesh },
                { "source_vertices", src.Count.ToString(CultureInfo.InvariantCulture) },
                { "matched", matched.ToString(CultureInfo.InvariantCulture) },
                { "target_vertices", total.ToString(CultureInfo.InvariantCulture) },
                { "detail", note },
            });
            return matched == 0 ? 1 : 0;
        }
    }
}
