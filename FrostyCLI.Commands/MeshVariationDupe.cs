using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `mesh variation-dupe`: duplicate a mesh variation (ObjectVariation) the way the game ships one, optionally with
    /// different textures / vector parameters, and optionally point placements at it.
    ///
    /// A BFII variation is four linked parts (shipped reference: BespinContainer_01_A_AdministratorPalace in Cloud City):
    /// <list type="number">
    /// <item>the ObjectVariation EBX -- NameHash = Fnv1 of its own lowercased name, one MeshMaterialVariation per material;</item>
    /// <item>per mesh, a ShaderBlockDepot res "&lt;variation&gt;/&lt;mesh file&gt;_&lt;MeshSet name hash&gt;/shaderblocks_variation/blocks"
    /// whose per-LOD ShaderBlockEntry / ShaderBlockMeshVariationEntry hashes are CityHash64(meshNameHash, variationHash, lod)
    /// and whose persistent blocks carry the textures (by instance guid) and vectors the renderer actually uses;</item>
    /// <item>per MeshVariationDatabase that registers (mesh, source variation), a row (mesh, new hash) whose
    /// Materials[i].MaterialVariation points at the NEW asset's i-th sub-object;</item>
    /// <item>placements: StaticModelGroup members pick a variation per instance in InstanceObjectVariation (the hash),
    /// standalone ReferenceObjectData through ObjectVariation (a reference).</item>
    /// </list>
    /// Why earlier duplicators gave "crash or no change" (measured on this tool's own path, 2026-09-27):
    /// <list type="bullet">
    /// <item>A depot edited in place (ModifyRes) is stored as a ModifiedShaderBlockDepot delta KEYED BY BLOCK HASH, and
    /// applied by swapping in blocks whose hash matches the base data. Every block a variation needs changed -- the
    /// re-keyed per-LOD entries and the salted parameter blocks -- has a NEW hash, so nothing matches and the whole edit is
    /// dropped: the copy keeps the template's keys (runtime finds no entries for the new variation) and its old values.
    /// This command writes the finished depot as the raw bytes of a NEW res (AddRes), never through the delta.</item>
    /// <item>The runtime caches shader parameter blocks by hash; a block edited but not re-hashed shows the cached
    /// original ("my colour change did nothing"). Every persistent/holder/section block of the copy is salted.</item>
    /// <item>Depot texture slots bind the texture's ROOT INSTANCE guid; a file guid there is game+0x1522B97.</item>
    /// <item>Copying sub-objects keeps their instance guids; two loaded assets sharing instance guids collide. The copy's
    /// objects get fresh deterministic guids and the database rows are re-pointed to them.</item>
    /// </list>
    /// </summary>
    public static class MeshVariationDupe
    {
        // --add-missing: a vector the base material does not bind is appended (keyed by the name's ParamDb hash, which
        // ParameterEntry computes) instead of being skipped -- the shader reads it only if its graph declares it
        [ThreadStatic] static bool s_addMissing;

        const string Usage = "Usage: frostycli mesh variation-dupe (--source <ObjectVariation> | --base <mesh>) --as <new ObjectVariation path> " +
            "[--mesh <mesh>]... [--texture <param>=<texture asset>]... [--vector <param>=x,y,z,w]... [--mvdb <database>]... " +
            "[--repoint <partition>]... [--repoint-all] [--skin <DataContainerCollectionBlueprint>]... [--report <file.json>] " +
            "[--shader <material>=<donor mesh>:<donor material>[@<donor variation>] | <material>=@<shader asset>]... (--shader: --base only) " +
            "[--add-missing]   (--texture #<8-hex name hash>=<texture> addresses a slot whose name is unknown)";

        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string source = null, target = null, reportPath = null, baseMesh = null;
            s_addMissing = false;   // per invocation: a pipeline runs many in one process
            var meshes = new List<string>();
            var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var vectors = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            var mvdbs = new List<string>();
            var repoint = new List<string>();
            var skins = new List<string>();
            var shaders = new List<string>();
            bool repointAll = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--source" && i + 1 < args.Length) source = args[++i];
                else if (a == "--base" && i + 1 < args.Length) baseMesh = args[++i];
                else if (a == "--as" && i + 1 < args.Length) target = args[++i];
                else if (a == "--mesh" && i + 1 < args.Length) meshes.Add(args[++i]);
                else if (a == "--mvdb" && i + 1 < args.Length) mvdbs.Add(args[++i]);
                else if (a == "--repoint" && i + 1 < args.Length) repoint.Add(args[++i]);
                else if (a == "--repoint-all") repointAll = true;
                else if (a == "--add-missing") s_addMissing = true;
                else if (a == "--skin" && i + 1 < args.Length) skins.Add(args[++i]);
                else if (a == "--shader" && i + 1 < args.Length) shaders.Add(args[++i]);
                else if (a == "--report" && i + 1 < args.Length) reportPath = args[++i];
                else if ((a == "--texture" || a == "--vector") && i + 1 < args.Length)
                {
                    string kv = args[++i]; int eq = kv.IndexOf('=');
                    if (eq <= 0) { Console.Error.WriteLine("mesh variation-dupe: " + a + " takes <param>=<value>"); return 1; }
                    string k = kv.Substring(0, eq), v = kv.Substring(eq + 1);
                    if (a == "--texture") textures[k] = v;
                    else
                    {
                        float[] f;
                        if (!TryParseVector(v, out f)) { Console.Error.WriteLine("mesh variation-dupe: bad vector " + v); return 1; }
                        vectors[k] = f;
                    }
                }
                else { Console.Error.WriteLine("mesh variation-dupe: unknown option " + a + "\n" + Usage); return 1; }
            }
            if ((source == null) == (baseMesh == null) || target == null) { Console.Error.WriteLine(Usage); return 1; }
            if (shaders.Count > 0 && baseMesh == null) { Console.Error.WriteLine("mesh variation-dupe: --shader needs --base"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("mesh variation-dupe: AssetManager not initialized."); return 1; }

            var report = new List<string>();
            Dictionary<string, string> info;
            string err = baseMesh != null
                ? RunFromBase(baseMesh, target, textures, vectors, mvdbs, repoint, repointAll, logger, report, out info, shaders)
                : Run(source, target, meshes, textures, vectors, mvdbs, repoint, repointAll, skins, logger, report, out info);
            foreach (string r in report) logger.Log("mesh variation-dupe: {0}", r);
            if (reportPath != null) File.WriteAllText(reportPath, string.Join(Environment.NewLine, report));
            if (err != null) { Console.Error.WriteLine("mesh variation-dupe: " + err); return 1; }
            formatter.WriteResult("OK", "mesh variation-dupe", info);
            return 0;
        }

        internal static string Run(string source, string target, List<string> meshes, Dictionary<string, string> textures,
            Dictionary<string, float[]> vectors, List<string> mvdbs, List<string> repoint, bool repointAll, List<string> skins,
            ExportLogger logger, List<string> report, out Dictionary<string, string> info)
        {
            info = new Dictionary<string, string>();
            EbxAssetEntry srcEntry = App.AssetManager.GetEbxEntry(source);
            if (srcEntry == null) return "source variation not found: " + source;
            if (srcEntry.Type != "ObjectVariation") return source + " is a " + srcEntry.Type + ", not an ObjectVariation";
            if (App.AssetManager.GetEbxEntry(target) != null) return "destination already exists: " + target;
            uint srcHash = NameHash(srcEntry.Name), newHash = NameHash(target);
            foreach (EbxAssetEntry ov in App.AssetManager.EnumerateEbx("ObjectVariation"))
                if (NameHash(ov.Name) == newHash) return target + " hashes to " + newHash + ", already used by " + ov.Name;

            // textures: resolve every override to (file guid, root instance guid) up front
            var texRefs = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);
            var texInstance = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in textures)
            {
                EbxAssetEntry te = App.AssetManager.GetEbxEntry(kv.Value);
                if (te == null) return "texture not found: " + kv.Value;
                Guid ig = RootInstanceGuid(te);
                if (ig == Guid.Empty) return "could not read the root instance guid of " + kv.Value;
                texRefs[kv.Key] = te; texInstance[kv.Key] = ig;
            }

            // 1. the ObjectVariation copy: fresh file guid, fresh deterministic instance guids, own name + NameHash
            EbxAsset srcAsset = App.AssetManager.GetEbx(srcEntry);
            EbxAsset copy = CloneEbx(srcAsset);
            Guid newFileGuid = DeterministicGuid("file|" + target.ToLowerInvariant());
            copy.SetFileGuid(newFileGuid);
            var guidMap = new Dictionary<Guid, Guid>();   // old exported instance guid -> new
            foreach (object o in copy.Objects)
            {
                AssetClassGuid old = (AssetClassGuid)((dynamic)o).GetInstanceGuid();
                if (!old.IsExported) continue;
                Guid ng = DeterministicGuid("inst|" + target.ToLowerInvariant() + "|" + old.ExportedGuid);
                guidMap[old.ExportedGuid] = ng;
                ((dynamic)o).SetInstanceGuid(new AssetClassGuid(ng, old.InternalId));
            }
            dynamic root = copy.RootObject;
            root.Name = target;
            root.NameHash = newHash;
            // the copy travels with the databases that name it; only with none named (a skin) does it follow the source's bundles --
            // a level's copy put into every shipped level bundle that holds the source just rides along in other maps
            var bundles = mvdbs.Count > 0 ? new HashSet<int>() : new HashSet<int>(srcEntry.EnumerateBundles());
            App.AssetManager.AddEbx(target, copy, bundles.ToArray());
            EbxAssetEntry newEntry = App.AssetManager.GetEbxEntry(target);
            if (newEntry == null) return "AddEbx did not create " + target;
            report.Add(string.Format("variation {0} (hash {1}) from {2} (hash {3}): {4} object(s), {5} bundle(s)",
                target, newHash, srcEntry.Name, srcHash, guidMap.Count, bundles.Count));

            // 2. per mesh: the variation depot. Meshes = those the source has a depot for (or the ones named).
            string srcPrefix = srcEntry.Name.ToLowerInvariant() + "/";
            var depots = App.AssetManager.EnumerateRes().Where(r => r.Name.StartsWith(srcPrefix, StringComparison.OrdinalIgnoreCase)
                                                                  && r.Name.EndsWith("/shaderblocks_variation/blocks", StringComparison.OrdinalIgnoreCase)).ToList();
            var meshEntries = new List<EbxAssetEntry>();
            if (meshes.Count > 0)
                foreach (string m in meshes)
                {
                    EbxAssetEntry me = App.AssetManager.GetEbxEntry(m);
                    if (me == null) return "mesh not found: " + m;
                    meshEntries.Add(me);
                }
            int depotsWritten = 0;
            var meshGuids = new HashSet<Guid>();
            foreach (ResAssetEntry depotEntry in depots)
            {
                // "<variation>/<mesh file>_<meshNameHash>/shaderblocks_variation/blocks"
                string mid = depotEntry.Name.Substring(srcPrefix.Length);
                mid = mid.Substring(0, mid.IndexOf('/'));
                int us = mid.LastIndexOf('_');
                uint meshNameHash;
                if (us < 0 || !uint.TryParse(mid.Substring(us + 1), NumberStyles.None, CultureInfo.InvariantCulture, out meshNameHash))
                { report.Add("skipped depot with an unexpected name: " + depotEntry.Name); continue; }
                string meshFile = mid.Substring(0, us);
                EbxAssetEntry meshEntry = meshEntries.FirstOrDefault(me => MeshFileName(me.Name) == meshFile)
                                          ?? (meshes.Count == 0 ? FindMeshByNameHash(meshFile, meshNameHash) : null);
                if (meshEntry == null)
                {
                    if (meshes.Count == 0) report.Add("no mesh found for depot " + depotEntry.Name + " (file " + meshFile + ", name hash " + meshNameHash + "); skipped");
                    continue;
                }
                string newDepot = target.ToLowerInvariant() + "/" + mid + "/shaderblocks_variation/blocks";
                string derr = WriteDepot(depotEntry, newDepot, meshNameHash, srcHash, newHash, newFileGuid, texInstance, vectors, report, out int rekeyed, out int changed);
                if (derr != null) return derr;
                depotsWritten++;
                meshGuids.Add(meshEntry.Guid);
                report.Add(string.Format("depot {0}: {1} per-LOD entr(y/ies) re-keyed, {2} parameter value(s) set", newDepot, rekeyed, changed));
            }
            if (depotsWritten == 0) return "the source variation has no shaderblocks_variation depot" + (meshes.Count > 0 ? " for the given mesh(es)" : "") + "; nothing to duplicate";

            // 3. database rows: every named database (or every database that registers (mesh, source hash))
            var dbEntries = new List<EbxAssetEntry>();
            if (mvdbs.Count > 0)
                foreach (string d in mvdbs)
                {
                    EbxAssetEntry de = App.AssetManager.GetEbxEntry(d);
                    if (de == null) return "database not found: " + d;
                    dbEntries.Add(de);
                }
            else
                foreach (EbxAssetEntry de in App.AssetManager.EnumerateEbx("MeshVariationDatabase"))
                    if (de.ContainsDependency(srcEntry.Guid)) dbEntries.Add(de);
            // donor rows: a named database that does not register (mesh, source hash) -- a default skin's own bundle, say --
            // takes the row from one that does (the source variation's own skin bundle)
            var donors = new Dictionary<Guid, object>();
            foreach (EbxAssetEntry de in App.AssetManager.EnumerateEbx("MeshVariationDatabase"))
            {
                if (donors.Count == meshGuids.Count) break;
                if (!de.ContainsDependency(srcEntry.Guid)) continue;
                IList es = MvdbCommand.FindEntries(App.AssetManager.GetEbx(de).RootObject);
                if (es == null) continue;
                foreach (object e in es)
                {
                    Guid mf = RefFileGuid(Get(e, "Mesh"));
                    if (meshGuids.Contains(mf) && !donors.ContainsKey(mf)
                        && Convert.ToUInt32(Get(e, "VariationAssetNameHash"), CultureInfo.InvariantCulture) == srcHash)
                        donors[mf] = e;
                }
            }
            int rows = 0;
            var rowTextures = new HashSet<Guid>();   // every texture the new rows bind (file guids)
            foreach (EbxAssetEntry de in dbEntries)
            {
                string rerr = AddRows(de, meshGuids, srcHash, newHash, newFileGuid, guidMap, texRefs, texInstance, donors, rowTextures, report, out int added);
                if (rerr != null) return rerr;
                if (added == 0) continue;
                rows += added;
                // the variation, its depots and its textures travel with every database that now names them
                foreach (int b in de.EnumerateBundles()) bundles.Add(b);
            }
            if (rows == 0) report.Add("WARNING: no database registers (mesh, " + srcHash + ") -- the variation will not load; pass --mvdb");

            // 5. placements
            int repointed = 0;
            foreach (string part in repoint)
            {
                string perr = Repoint(part, meshGuids, srcEntry, srcHash, newFileGuid, RootInstanceGuidOf(copy), newHash, repointAll, logger, out int n);
                if (perr != null) return perr;
                repointed += n;
                report.Add(part + ": " + n + " placement(s) now use " + target);
            }

            foreach (string skin in skins)
            {
                string serr = RepointSkin(skin, meshGuids, newFileGuid, RootInstanceGuidOf(copy), report, out int n);
                if (serr != null) return serr;
                repointed += n;
                // the skin's own bundles must carry the variation too
                foreach (int b in App.AssetManager.GetEbxEntry(skin).EnumerateBundles()) bundles.Add(b);
            }

            // 6. bundles: the new asset, its depots, the override textures AND every texture the copied rows still bind.
            // A copy placed in a bundle its source never lived in (a default skin's) needs the source's textures there too:
            // VARDUPE_C3 bundled only the overrides and died at game+0x1522B97 looking up L_Preq_T_212_Markings_PhaseI,
            // the 501st helmet's marking texture, in the Phase I default skin's bundle.
            var closure = new List<EbxAssetEntry>(texRefs.Values.Distinct());
            foreach (Guid tg in rowTextures)
            {
                EbxAssetEntry te = App.AssetManager.GetEbxEntry(tg);
                if (te != null && !closure.Contains(te)) closure.Add(te);
            }
            var newRes = App.AssetManager.EnumerateRes().Where(r => r.Name.StartsWith(target.ToLowerInvariant() + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            var texRes = new List<Tuple<EbxAssetEntry, List<ulong>, List<Guid>>>();
            foreach (EbxAssetEntry te in closure)
            {
                BundleCommand.GatherSubResources(te, out List<ulong> rids, out List<Guid> chunks);
                // link res + chunks so the chunks carry H32 / FirstMip into the new bundles: a shipped texture chunk added to a
                // bundle it never lived in reads H32 0 from the catalog, and D3D texture creation then fails with E_INVALIDARG
                // raised at game+0x161C7BD (VARDUPE_C3b/C3c on the clone default skins; finding #38)
                BundleCommand.LinkSubResources(te, rids, chunks);
                texRes.Add(Tuple.Create(te, rids, chunks));
            }
            foreach (int b in bundles)
            {
                newEntry.AddToBundle(b);
                foreach (ResAssetEntry r in newRes) r.AddToBundle(b);
                foreach (var t in texRes)
                {
                    t.Item1.AddToBundle(b);
                    foreach (ulong rid in t.Item2) App.AssetManager.GetResEntry(rid)?.AddToBundle(b);
                    foreach (Guid c in t.Item3) App.AssetManager.GetChunkEntry(c)?.AddToBundle(b);
                }
            }
            report.Add(string.Format("bundled the variation, {0} depot(s) and {1} texture(s) into {2} bundle(s)", newRes.Count, closure.Count, bundles.Count));

            info["variation"] = target;
            info["hash"] = newHash.ToString(CultureInfo.InvariantCulture);
            info["source_hash"] = srcHash.ToString(CultureInfo.InvariantCulture);
            info["depots"] = depotsWritten.ToString(CultureInfo.InvariantCulture);
            info["database_rows"] = rows.ToString(CultureInfo.InvariantCulture);
            info["bundles"] = bundles.Count.ToString(CultureInfo.InvariantCulture);
            info["placements_repointed"] = repointed.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>
        /// --base: a NEW variation of a mesh that has none to copy (a ported mesh) -- built from the mesh's own base look, the way
        /// a shipped variation is laid out: an ObjectVariation with one MeshMaterialVariation per material of the mesh's base
        /// database record, the base depot "&lt;mesh&gt;_mesh/blocks" re-keyed to (meshNameHash, new hash, lod) with the overrides
        /// applied and every parameter block salted, and a record (mesh, new hash) per database that registers (mesh, 0), whose
        /// Materials[i].MaterialVariation points at sub-object i. An override only lands where the material already binds the
        /// parameter (what its shader reads); one that lands nowhere is an error, not a silent no-op.
        /// </summary>
        internal static string RunFromBase(string baseMesh, string target, Dictionary<string, string> textures, Dictionary<string, float[]> vectors,
            List<string> mvdbs, List<string> repoint, bool repointAll, ExportLogger logger, List<string> report, out Dictionary<string, string> info,
            List<string> shaders = null)
        {
            info = new Dictionary<string, string>();
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(baseMesh);
            if (meshEntry == null) return "mesh not found: " + baseMesh;
            if (!BundleCommand.IsMeshType(meshEntry.Type)) return baseMesh + " is a " + meshEntry.Type + ", not a mesh";
            if (App.AssetManager.GetEbxEntry(target) != null) return "destination already exists: " + target;
            uint newHash = NameHash(target);
            foreach (EbxAssetEntry ov in App.AssetManager.EnumerateEbx("ObjectVariation"))
                if (NameHash(ov.Name) == newHash) return target + " hashes to " + newHash + ", already used by " + ov.Name;
            if (FrostyCLI.Adapters.Mesh.MeshCloner.ReadMeshSetName(meshEntry, out uint meshNameHash) == null) return "could not read the MeshSet of " + baseMesh;
            ResAssetEntry baseDepot = App.AssetManager.GetResEntry(BundleCommand.DepotResName(meshEntry.Name));
            if (baseDepot == null) return baseMesh + " has no shader block depot (" + BundleCommand.DepotResName(meshEntry.Name) + ")";

            var texRefs = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);
            var texInstance = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in textures)
            {
                EbxAssetEntry te = App.AssetManager.GetEbxEntry(kv.Value);
                if (te == null) return "texture not found: " + kv.Value;
                Guid ig = RootInstanceGuid(te);
                if (ig == Guid.Empty) return "could not read the root instance guid of " + kv.Value;
                texRefs[kv.Key] = te; texInstance[kv.Key] = ig;
            }

            // databases: the named ones, else every one that registers (mesh, 0); the base record fixes the material count
            var meshGuids = new HashSet<Guid> { meshEntry.Guid };
            var dbEntries = new List<EbxAssetEntry>();
            if (mvdbs.Count > 0)
                foreach (string d in mvdbs)
                {
                    EbxAssetEntry de = App.AssetManager.GetEbxEntry(d);
                    if (de == null) return "database not found: " + d;
                    dbEntries.Add(de);
                }
            else
                foreach (EbxAssetEntry de in App.AssetManager.EnumerateEbx("MeshVariationDatabase"))
                    if (de.ContainsDependency(meshEntry.Guid)) dbEntries.Add(de);
            var donors = new Dictionary<Guid, object>();
            foreach (EbxAssetEntry de in dbEntries.Concat(App.AssetManager.EnumerateEbx("MeshVariationDatabase").Where(x => x.ContainsDependency(meshEntry.Guid))))
            {
                IList es = MvdbCommand.FindEntries(App.AssetManager.GetEbx(de).RootObject);
                if (es == null) continue;
                foreach (object e in es)
                    if (RefFileGuid(Get(e, "Mesh")) == meshEntry.Guid && Convert.ToUInt32(Get(e, "VariationAssetNameHash"), CultureInfo.InvariantCulture) == 0)
                    { donors[meshEntry.Guid] = e; break; }
                if (donors.Count > 0) break;
            }
            if (donors.Count == 0) return "no MeshVariationDatabase has a base record (variation 0) for " + baseMesh;
            int materialCount = ((IList)Get(donors[meshEntry.Guid], "Materials")).Count;

            // shader swaps: each takes the donor's compiled RVM shader reference for the material, its record's shader ids and its shader asset
            var swaps = new Dictionary<int, ShaderSwap>();
            foreach (string spec in shaders ?? new List<string>())
            {
                string serr = ResolveShaderSwap(spec, out int mat, out ShaderSwap sw);
                if (serr != null) return "--shader " + spec + ": " + serr;
                if (mat < 0 || mat >= materialCount) return "--shader " + spec + ": " + baseMesh + " has " + materialCount + " material(s)";
                swaps[mat] = sw;
                report.Add(string.Format("material {0} -> shader {1} (rvm {2}/{3}, from {4})", mat, sw.ShaderEntry.Name, sw.RvmGuid, sw.RvmInt, spec.Substring(spec.IndexOf('=') + 1)));
            }
            MeshSet targetSet = null;
            if (swaps.Count > 0)
            {
                try
                {
                    ulong rid = (ulong)((dynamic)App.AssetManager.GetEbx(meshEntry).RootObject).MeshSetResource;
                    targetSet = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                }
                catch (Exception ex) { return "could not read the MeshSet of " + baseMesh + ": " + ex.Message; }
            }

            // 1. the ObjectVariation: root + one MeshMaterialVariation per material, deterministic guids
            Guid newFileGuid = DeterministicGuid("file|" + target.ToLowerInvariant());
            dynamic root = TypeLibrary.CreateObject("ObjectVariation");
            if (root == null) return "the loaded profile has no ObjectVariation type";
            root.SetInstanceGuid(new AssetClassGuid(DeterministicGuid("inst|" + target.ToLowerInvariant() + "|root"), 0));
            root.Name = target;
            root.NameHash = newHash;
            EbxAsset asset = new EbxAsset(new object[] { root });
            asset.SetFileGuid(newFileGuid);
            var materialVariations = new List<Guid>();
            for (int k = 0; k < materialCount; k++)
            {
                dynamic mmv = TypeLibrary.CreateObject("MeshMaterialVariation");
                if (mmv == null) return "the loaded profile has no MeshMaterialVariation type";
                Guid g = DeterministicGuid("inst|" + target.ToLowerInvariant() + "|material|" + k);
                mmv.SetInstanceGuid(new AssetClassGuid(g, k + 1));
                if (swaps.TryGetValue(k, out ShaderSwap sw))
                {
                    dynamic sh = mmv.Shader;
                    sh.Shader = new PointerRef(new EbxImportReference { FileGuid = sw.ShaderEntry.Guid, ClassGuid = RootInstanceGuid(sw.ShaderEntry) });
                    mmv.Shader = sh;
                    asset.AddDependency(sw.ShaderEntry.Guid);
                }
                asset.AddObject(mmv);
                materialVariations.Add(g);
            }
            App.AssetManager.AddEbx(target, asset);
            EbxAssetEntry newEntry = App.AssetManager.GetEbxEntry(target);
            if (newEntry == null) return "AddEbx did not create " + target;
            report.Add(string.Format("variation {0} (hash {1}) from the base look of {2}: {3} material variation(s)", target, newHash, meshEntry.Name, materialCount));

            // 2. the depot: the base depot re-keyed from variation 0
            string newDepot = target.ToLowerInvariant() + "/" + MeshFileName(meshEntry.Name) + "_" + meshNameHash.ToString(CultureInfo.InvariantCulture) + "/shaderblocks_variation/blocks";
            Func<int, int, int> sectionMaterial = null;
            if (targetSet != null)
                sectionMaterial = (lod, sec) => lod < targetSet.Lods.Count && sec < targetSet.Lods[lod].Sections.Count ? targetSet.Lods[lod].Sections[sec].MaterialId : -1;
            string derr = WriteDepot(baseDepot, newDepot, meshNameHash, 0, newHash, newFileGuid, texInstance, vectors, report, out int rekeyed, out int changed,
                swaps.Count > 0 ? swaps : null, sectionMaterial);
            if (derr != null) return derr;
            report.Add(string.Format("depot {0}: {1} per-LOD entr(y/ies) re-keyed, {2} parameter value(s) set", newDepot, rekeyed, changed));
            if (vectors.Count + texInstance.Count + swaps.Count > 0 && changed == 0)
                return "no override landed: " + baseMesh + "'s materials bind none of " + string.Join(", ", vectors.Keys.Concat(texInstance.Keys)) + " (see material dump --names)";

            // 3. records
            int rows = 0;
            var bundles = new HashSet<int>();
            var rowTextures = new HashSet<Guid>();
            foreach (EbxAssetEntry de in dbEntries)
            {
                string rerr = AddRows(de, meshGuids, 0, newHash, newFileGuid, new Dictionary<Guid, Guid>(), texRefs, texInstance, donors, rowTextures, report, out int added, materialVariations, swaps);
                if (rerr != null) return rerr;
                if (added == 0) continue;
                rows += added;
                foreach (int b in de.EnumerateBundles()) bundles.Add(b);
            }
            if (rows == 0) return "no database took a record for (" + baseMesh + ", " + newHash + "); pass --mvdb";

            // 4. placements (static-group members only: a base look has no ObjectVariation reference to follow)
            int repointed = 0;
            foreach (string part in repoint)
            {
                string perr = Repoint(part, meshGuids, null, 0, newFileGuid, RootInstanceGuidOf(asset), newHash, repointAll, logger, out int n);
                if (perr != null) return perr;
                repointed += n;
                report.Add(part + ": " + n + " placement(s) now use " + target);
            }

            // 5. bundles: the asset, its depot and the override textures travel with every database that names them
            var closure = new List<EbxAssetEntry>(texRefs.Values.Distinct());
            foreach (Guid tg in rowTextures)
            {
                EbxAssetEntry te = App.AssetManager.GetEbxEntry(tg);
                if (te != null && !closure.Contains(te)) closure.Add(te);
            }
            ResAssetEntry depotEntry = App.AssetManager.GetResEntry(newDepot);
            // a swapped-in shader and its compiled companions (<shader>_graph/blocks) travel with the variation
            var shaderRes = new List<ResAssetEntry>();
            foreach (ShaderSwap sw in swaps.Values)
            {
                string pre = sw.ShaderEntry.Name.ToLowerInvariant();
                foreach (ResAssetEntry r in App.AssetManager.EnumerateRes())
                    if (r.Name.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && r.Name.Length > pre.Length && "_/.".IndexOf(r.Name[pre.Length]) >= 0) shaderRes.Add(r);
            }
            var texRes = new List<Tuple<EbxAssetEntry, List<ulong>, List<Guid>>>();
            foreach (EbxAssetEntry te in closure)
            {
                BundleCommand.GatherSubResources(te, out List<ulong> rids, out List<Guid> chunks);
                BundleCommand.LinkSubResources(te, rids, chunks);
                texRes.Add(Tuple.Create(te, rids, chunks));
            }
            foreach (int b in bundles)
            {
                newEntry.AddToBundle(b);
                depotEntry.AddToBundle(b);
                foreach (ShaderSwap sw in swaps.Values) sw.ShaderEntry.AddToBundle(b);
                foreach (ResAssetEntry r in shaderRes) r.AddToBundle(b);
                foreach (var t in texRes)
                {
                    t.Item1.AddToBundle(b);
                    foreach (ulong rid in t.Item2) App.AssetManager.GetResEntry(rid)?.AddToBundle(b);
                    foreach (Guid c in t.Item3) App.AssetManager.GetChunkEntry(c)?.AddToBundle(b);
                }
            }
            report.Add(string.Format("bundled the variation, its depot and {0} texture(s) into {1} bundle(s)", closure.Count, bundles.Count));

            info["variation"] = target;
            info["hash"] = newHash.ToString(CultureInfo.InvariantCulture);
            info["base"] = meshEntry.Name;
            info["materials"] = materialCount.ToString(CultureInfo.InvariantCulture);
            info["values_set"] = changed.ToString(CultureInfo.InvariantCulture);
            info["database_rows"] = rows.ToString(CultureInfo.InvariantCulture);
            info["bundles"] = bundles.Count.ToString(CultureInfo.InvariantCulture);
            info["placements_repointed"] = repointed.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        internal sealed class ShaderSwap
        {
            public EbxAssetEntry ShaderEntry;
            public Guid RvmGuid;
            public int RvmInt;
            public int? SetInt;     // @<asset>#<flags>: the target surface's flags (SurfaceShader+0x30), written to the section
            public Guid SurfaceShaderGuid;
            public uint SurfaceShaderId;
            // what the donor's section binds for that shader: parameters/textures ours lacks are added (DICE's _Blend snow
            // piles bind one texture slot the base shader has no use for), and the record's texture entries likewise
            public List<ParameterEntry> Params = new List<ParameterEntry>();
            public List<ParameterEntry> TextureParams = new List<ParameterEntry>();
            public IList RowTextureParameters;
        }

        /// <summary>
        /// "&lt;material&gt;=&lt;donor mesh&gt;:&lt;donor material&gt;[@&lt;donor variation&gt;]": the shader the donor draws that material with -- its
        /// compiled RVM reference (from the donor's depot, LOD 0, first section of that material), the record's SurfaceShaderGuid /
        /// SurfaceShaderId and the shader asset (the variation's MeshMaterialVariation if it names one, else the mesh's material).
        /// </summary>
        static string ResolveShaderSwap(string spec, out int material, out ShaderSwap sw)
        {
            material = -1; sw = null;
            int eq = spec.IndexOf('=');
            if (eq <= 0 || !int.TryParse(spec.Substring(0, eq), NumberStyles.Integer, CultureInfo.InvariantCulture, out material)) return "want <material>=<donor mesh>:<donor material>[@<donor variation>]";
            string rest = spec.Substring(eq + 1), variation = null;
            if (rest.StartsWith("@", StringComparison.Ordinal))
            {
                string target = rest.Substring(1);
                int hashAt = target.LastIndexOf('#');
                int? flags = null;
                if (hashAt > 0)
                {
                    string f = target.Substring(hashAt + 1);
                    flags = f.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? int.Parse(f.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                        : int.Parse(f, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    target = target.Substring(0, hashAt);
                }
                string perr = ResolveShaderByPath(target, out sw);
                if (sw != null) sw.SetInt = flags;
                return perr;
            }
            int at = rest.LastIndexOf('@');
            if (at > 0) { variation = rest.Substring(at + 1); rest = rest.Substring(0, at); }
            int colon = rest.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(rest.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dm)) return "want <donor mesh>:<donor material>";
            EbxAssetEntry donorMesh = App.AssetManager.GetEbxEntry(rest.Substring(0, colon));
            if (donorMesh == null) return "donor mesh not found: " + rest.Substring(0, colon);
            EbxAssetEntry donorVar = null;
            uint varHash = 0;
            if (variation != null)
            {
                donorVar = App.AssetManager.GetEbxEntry(variation);
                if (donorVar == null) return "donor variation not found: " + variation;
                varHash = Convert.ToUInt32(Get(App.AssetManager.GetEbx(donorVar).RootObject, "NameHash"), CultureInfo.InvariantCulture);
            }
            MeshSet set;
            dynamic meshRoot = App.AssetManager.GetEbx(donorMesh).RootObject;
            try { set = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry((ulong)meshRoot.MeshSetResource)); }
            catch (Exception ex) { return "could not read the donor MeshSet: " + ex.Message; }
            string depotName = donorVar == null ? BundleCommand.DepotResName(donorMesh.Name) : LevelCommand.VariationDepotName(donorVar.Name, donorMesh);
            ResAssetEntry depotEntry = depotName != null ? App.AssetManager.GetResEntry(depotName) : null;
            if (depotEntry == null) return "the donor has no depot " + depotName;
            int section = -1;
            for (int k = 0; k < set.Lods[0].Sections.Count; k++) if (set.Lods[0].Sections[k].MaterialId == dm) { section = k; break; }
            if (section < 0) return "the donor's LOD 0 draws no section with material " + dm;
            byte[] meta = (byte[])depotEntry.ResMeta.Clone();
            sw = new ShaderSwap();
            try
            {
                ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(depotEntry);
                ShaderBlockMeshVariationEntry lod0 = null;
                for (int i = 0; i < depot.ResourceCount && lod0 == null; i++)
                    if (depot.GetResource(i) is ShaderBlockMeshVariationEntry mve && mve.Hash == EntryHash(set.NameHash, varHash, 0)) lod0 = mve;
                if (lod0 == null) return depotName + " has no LOD 0 entry for (" + set.NameHash + ", " + varHash + ")";
                sw.RvmGuid = lod0.RvmShaderRefGuids[section];
                sw.RvmInt = lod0.RvmShaderRefInts[section];
                sw.SetInt = sw.RvmInt;      // the donor surface's flags travel with its shader reference
                ShaderBlockEntry e0 = null;
                for (int i = 0; i < depot.ResourceCount && e0 == null; i++)
                    if (depot.GetResource(i) is ShaderBlockEntry sbe && sbe.Hash == lod0.Hash) e0 = sbe;
                if (e0 != null)
                {
                    ShaderPersistentParamDbBlock pb = SafeBlock(() => e0.GetParams(section)), tb = SafeBlock(() => e0.GetTextureParams(section));
                    if (pb != null) sw.Params.AddRange(pb.Parameters);
                    if (tb != null) sw.TextureParams.AddRange(tb.Parameters);
                }
            }
            finally { Array.Copy(meta, depotEntry.ResMeta, Math.Min(meta.Length, depotEntry.ResMeta.Length)); }
            // the record (any database), for the shader ids and -- with a variation -- the material's MeshMaterialVariation
            object row = null;
            foreach (EbxAssetEntry db in App.AssetManager.EnumerateEbx("MeshVariationDatabase"))
            {
                if (!db.ContainsDependency(donorMesh.Guid)) continue;
                IList es = MvdbCommand.FindEntries(App.AssetManager.GetEbx(db).RootObject);
                if (es == null) continue;
                foreach (object e in es)
                    if (RefFileGuid(Get(e, "Mesh")) == donorMesh.Guid && Convert.ToUInt32(Get(e, "VariationAssetNameHash"), CultureInfo.InvariantCulture) == varHash) { row = e; break; }
                if (row != null) break;
            }
            if (row == null) return "no database has a record for (" + donorMesh.Name + ", " + varHash + ")";
            IList mats = (IList)Get(row, "Materials");
            if (dm >= mats.Count) return "the donor record has " + mats.Count + " material(s)";
            sw.SurfaceShaderGuid = (Guid)Get(mats[dm], "SurfaceShaderGuid");
            sw.SurfaceShaderId = Convert.ToUInt32(Get(mats[dm], "SurfaceShaderId"), CultureInfo.InvariantCulture);
            sw.RowTextureParameters = (IList)Get(mats[dm], "TextureParameters");
            object shaderRef = null;
            if (donorVar != null)
            {
                Guid sub = RefClassGuid(Get(mats[dm], "MaterialVariation"));
                foreach (object o in App.AssetManager.GetEbx(donorVar).Objects)
                    if (((AssetClassGuid)((dynamic)o).GetInstanceGuid()).ExportedGuid == sub && sub != Guid.Empty) shaderRef = ((dynamic)o).Shader.Shader;
            }
            if (RefFileGuid(shaderRef) == Guid.Empty)
            {
                IList meshMats = (IList)Get(meshRoot, "Materials");
                object mm = dm < meshMats.Count ? meshMats[dm] : null;
                if (mm is PointerRef pr) mm = pr.Type == PointerRefType.Internal ? pr.Internal : null;   // Materials holds references to the MeshMaterial objects
                if (mm != null) shaderRef = ((dynamic)mm).Shader.Shader;
            }
            if (RefFileGuid(shaderRef) == Guid.Empty) return "the donor material names no shader asset";
            sw.ShaderEntry = App.AssetManager.GetEbxEntry(RefFileGuid(shaderRef));
            if (sw.ShaderEntry == null) return "the donor's shader asset is missing";
            return null;
        }

        /// <summary>
        /// "@&lt;shader asset&gt;[#&lt;flags&gt;]": a shader added to a level's RVM database (a new SurfaceShader + RvmPermutationSet pair cloned
        /// from the surface the material draws now, so every permutation and vertex layout carries over). A mesh binds a
        /// surface by RvmShaderRefGuids[section] = MD5(asset path, lowercased) read as a little-endian Guid; the record's
        /// SurfaceShaderGuid is the ShaderGraph asset's file guid and SurfaceShaderId its lowercase djb2 name hash. The
        /// section's RvmShaderRefInts value is the SURFACE's flags (SurfaceShader+0x30: 0x701 opaque GBuffer, 0x30C transparent
        /// forward -- measured on BFII's own meshes), kept unless #&lt;flags&gt; names the target's: a mesh moved from an opaque
        /// surface onto a transparent one must carry the transparent flags.
        /// </summary>
        static string ResolveShaderByPath(string path, out ShaderSwap sw)
        {
            sw = null;
            EbxAssetEntry shader = App.AssetManager.GetEbxEntry(path);
            if (shader == null) return "shader asset not found: " + path + " (create the ShaderGraph EBX first)";
            byte[] md5;
            using (var h = MD5.Create()) md5 = h.ComputeHash(Encoding.UTF8.GetBytes(shader.Name.ToLowerInvariant()));
            uint v = 5381;
            foreach (char c in shader.Name.ToLowerInvariant()) v = unchecked((v * 33) ^ c);
            sw = new ShaderSwap { ShaderEntry = shader, RvmGuid = new Guid(md5), RvmInt = -1, SurfaceShaderGuid = shader.Guid, SurfaceShaderId = v };
            return null;
        }

        // ---- depot

        internal static string WriteDepot(ResAssetEntry srcDepot, string newName, uint meshNameHash, uint srcHash, uint newHash, Guid salt,
            Dictionary<string, Guid> texInstance, Dictionary<string, float[]> vectors, List<string> report, out int rekeyed, out int changed,
            Dictionary<int, ShaderSwap> swaps = null, Func<int, int, int> sectionMaterial = null)
        {
            rekeyed = 0; changed = 0;
            if (App.AssetManager.GetResEntry(newName) != null) return "res already exists: " + newName;
            // GetResAs hands the depot the ENTRY's meta array, and ToBytes rewrites that array in place: keep a copy and
            // put it back so the shipped entry is untouched.
            byte[] srcMeta = (byte[])srcDepot.ResMeta.Clone();
            ShaderBlockDepot depot;
            byte[] bytes, meta;
            try
            {
                depot = App.AssetManager.GetResAs<ShaderBlockDepot>(srcDepot);
                if (depot == null) return "could not read " + srcDepot.Name;
                // re-key the per-LOD entries: every one must be explained by (meshNameHash, source hash, lod)
                int unexplained = 0;
                for (int i = 0; i < depot.ResourceCount; i++)
                {
                    ShaderBlockResource r = depot.GetResource(i);
                    if (!(r is ShaderBlockEntry) && !(r is ShaderBlockMeshVariationEntry)) continue;
                    int lod = ExplainLod(r.Hash, meshNameHash, srcHash, 32);
                    if (lod < 0) { unexplained++; continue; }
                    if (r is ShaderBlockEntry sbe) sbe.SetHash(meshNameHash, newHash, lod);
                    else
                    {
                        var mve = (ShaderBlockMeshVariationEntry)r;
                        mve.SetHash(meshNameHash, newHash, lod);
                        // a shader swap is the section's RVM shader reference: DICE's _Blend snow piles differ from their base
                        // depot only there (SS_..._08_Underground 8998a58c -> SS_..._08_Underground_Blend ebf0b58c, layout 1793)
                        if (swaps != null)
                            for (int sec = 0; sec < mve.RvmShaderRefGuids.Count; sec++)
                            {
                                int mat = sectionMaterial(lod, sec);
                                if (mat < 0) return srcDepot.Name + ": LOD " + lod + " has " + mve.RvmShaderRefGuids.Count + " shader reference(s), more than the mesh's sections";
                                if (!swaps.TryGetValue(mat, out ShaderSwap sw)) continue;
                                if (sw.SetInt.HasValue) mve.RvmShaderRefInts[sec] = sw.SetInt.Value;
                                else if (sw.RvmInt >= 0 && mve.RvmShaderRefInts[sec] != sw.RvmInt)
                                    return srcDepot.Name + ": LOD " + lod + " section " + sec + " has surface flags " + mve.RvmShaderRefInts[sec] + ", expected " + sw.RvmInt;
                                mve.RvmShaderRefGuids[sec] = sw.RvmGuid;
                                changed++;
                            }
                    }
                    rekeyed++;
                }
                if (unexplained > 0)
                    return srcDepot.Name + ": " + unexplained + " per-LOD entr(y/ies) are not keyed CityHash64(" + meshNameHash + ", " + srcHash +
                           ", lod) -- refusing to write a depot the runtime could not find";
                // parameter overrides, in every section of every LOD that already binds the parameter
                for (int lod = 0; lod < depot.Count; lod++)
                {
                    ShaderBlockEntry e = depot.GetSectionEntry(lod);
                    if (e == null) continue;
                    for (int s = 0; s < e.Resources.Count; s++)
                    {
                        if (swaps != null && swaps.TryGetValue(sectionMaterial(lod, s), out ShaderSwap swp))
                        {
                            ShaderPersistentParamDbBlock stb = SafeBlock(() => e.GetTextureParams(s)), spb = SafeBlock(() => e.GetParams(s));
                            foreach (var pair in new[] { Tuple.Create(stb, swp.TextureParams), Tuple.Create(spb, swp.Params) })
                            {
                                if (pair.Item1 == null) continue;
                                foreach (ParameterEntry pe in pair.Item2)
                                    if (!pair.Item1.Parameters.Any(x => x.NameHash == pe.NameHash)) { pair.Item1.Parameters.Add(pe); pair.Item1.IsModified = true; }
                            }
                        }
                        ShaderPersistentParamDbBlock tb = SafeBlock(() => e.GetTextureParams(s));
                        if (tb != null)
                            foreach (var kv in texInstance)
                            {
                                if (kv.Key.StartsWith("#", StringComparison.Ordinal))
                                {
                                    uint nh = uint.Parse(kv.Key.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                    ParameterEntry pe = tb.Parameters.FirstOrDefault(x => x.NameHash == nh);
                                    if (pe != null) { pe.SetValue(kv.Value); tb.IsModified = true; changed++; }
                                    else if (s_addMissing)
                                    {
                                        // a slot the base never bound (a mesh moved onto another surface): ParameterEntry types it
                                        // from the Guid value; its name is unknown, so the hash is set directly
                                        pe = new ParameterEntry("#", kv.Value) { NameHash = nh };
                                        tb.Parameters.Add(pe); tb.IsModified = true; changed++;
                                    }
                                }
                                else if (tb.GetParameterValue(kv.Key) != null) { tb.SetParameterValue(kv.Key, kv.Value); tb.IsModified = true; changed++; }
                            }
                        ShaderPersistentParamDbBlock pb = SafeBlock(() => e.GetParams(s));
                        if (pb != null)
                            foreach (var kv in vectors)
                            {
                                object cur = pb.GetParameterValue(kv.Key);
                                if (cur == null)
                                {
                                    if (!s_addMissing) continue;
                                    pb.Parameters.Add(new ParameterEntry(kv.Key, kv.Value));
                                    pb.IsModified = true; changed++;
                                    continue;
                                }
                                // a scalar parameter (Float32) takes x; a vector takes all four
                                pb.SetParameterValue(kv.Key, cur is float ? (object)kv.Value[0] : kv.Value);
                                pb.IsModified = true; changed++;
                            }
                    }
                }
                // salt every parameter block, holder and section block: the runtime caches them by hash, so a copy that keeps
                // the source's hashes renders the source's cached values. MeshParamDbBlock is keyed to the mesh: keep it.
                byte[] saltBytes = salt.ToByteArray();
                for (int i = 0; i < depot.ResourceCount; i++)
                {
                    ShaderBlockResource r = depot.GetResource(i);
                    if (r is ShaderBlockEntry || r is ShaderBlockMeshVariationEntry || r is MeshParamDbBlock) continue;
                    if (!(r is ShaderPersistentParamDbBlock) && !(r is ShaderStaticParamDbBlock)) continue;
                    r.Hash = SaltedHash(r.Hash, saltBytes);
                    r.IsModified = true;
                }
                bytes = depot.ToBytes();
                meta = (byte[])depot.ResourceMeta.Clone();
            }
            finally
            {
                Array.Copy(srcMeta, srcDepot.ResMeta, Math.Min(srcMeta.Length, srcDepot.ResMeta.Length));
            }
            // Raw bytes of a NEW res: no ModifiedShaderBlockDepot delta (hash-keyed, would drop every re-keyed block).
            App.AssetManager.AddRes(newName, (ResourceType)srcDepot.ResType, meta, bytes);
            if (App.AssetManager.GetResEntry(newName) == null) return "AddRes did not create " + newName;
            return null;
        }

        static ShaderPersistentParamDbBlock SafeBlock(Func<ShaderPersistentParamDbBlock> f)
        {
            try { return f(); } catch { return null; }
        }

        /// <summary>The lod whose CityHash64(meshNameHash, variationHash, lod) equals <paramref name="hash"/>, else -1.</summary>
        public static int ExplainLod(ulong hash, uint meshNameHash, uint variationHash, int maxLod)
        {
            for (int lod = 0; lod < maxLod; lod++)
                if (EntryHash(meshNameHash, variationHash, lod) == hash) return lod;
            return -1;
        }

        public static ulong EntryHash(uint meshNameHash, uint variationHash, int lod)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(meshNameHash); w.Write(variationHash); w.Write(lod);
                return Frosty.Hash.CityHash.Hash64(ms.ToArray());
            }
        }

        public static ulong SaltedHash(ulong hash, byte[] salt)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(hash); w.Write(salt);
                return Frosty.Hash.CityHash.Hash64(ms.ToArray());
            }
        }

        // ---- database rows

        static string AddRows(EbxAssetEntry db, HashSet<Guid> meshGuids, uint srcHash, uint newHash, Guid newFileGuid, Dictionary<Guid, Guid> guidMap,
            Dictionary<string, EbxAssetEntry> texRefs, Dictionary<string, Guid> texInstance, Dictionary<Guid, object> donors,
            HashSet<Guid> rowTextures, List<string> report, out int added, List<Guid> materialVariations = null, Dictionary<int, ShaderSwap> swaps = null)
        {
            added = 0;
            EbxAsset asset = App.AssetManager.GetEbx(db);
            IList entries = MvdbCommand.FindEntries(asset.RootObject);
            if (entries == null) return db.Name + ": no entry list";
            var fresh = new List<object>();
            var sources = new List<object>();
            var have = new HashSet<Guid>();
            foreach (object e in entries)
            {
                Guid mf = RefFileGuid(Get(e, "Mesh"));
                if (meshGuids.Contains(mf) && Convert.ToUInt32(Get(e, "VariationAssetNameHash"), CultureInfo.InvariantCulture) == srcHash)
                { sources.Add(e); have.Add(mf); }
            }
            // a database that registers the mesh but not the source variation takes the donor's row
            foreach (var kv in donors)
                if (!have.Contains(kv.Key) && MeshRegistered(entries, kv.Key))
                {
                    sources.Add(kv.Value);
                    report.Add(db.Name + ": the row for mesh " + kv.Key + " comes from the source variation's own database");
                }
            foreach (object e in sources)
            {
                Guid mf = RefFileGuid(Get(e, "Mesh"));
                if (MvdbCommand.EntryExists(entries, mf, newHash)) { report.Add(db.Name + ": row for the new hash already there"); continue; }
                object row = MvdbCommand.DeepCopyValue(e);
                Set(row, "VariationAssetNameHash", newHash);
                IList rowMaterials = (IList)Get(row, "Materials");
                if (materialVariations != null)
                {
                    // a base record (variation 0) binds no MaterialVariation: material i takes the new asset's i-th sub-object
                    if (rowMaterials.Count != materialVariations.Count)
                        return db.Name + ": the base record has " + rowMaterials.Count + " material(s), the new variation " + materialVariations.Count;
                    for (int k = 0; k < rowMaterials.Count; k++)
                    {
                        Set(rowMaterials[k], "MaterialVariation", new PointerRef(new EbxImportReference { FileGuid = newFileGuid, ClassGuid = materialVariations[k] }));
                        if (swaps != null && swaps.TryGetValue(k, out ShaderSwap sw))
                        {
                            Set(rowMaterials[k], "SurfaceShaderGuid", sw.SurfaceShaderGuid);
                            Set(rowMaterials[k], "SurfaceShaderId", sw.SurfaceShaderId);
                            IList mine = (IList)Get(rowMaterials[k], "TextureParameters");
                            foreach (object tp in sw.RowTextureParameters ?? new ArrayList())
                            {
                                string pn = Get(tp, "ParameterName")?.ToString();
                                bool bound = false;
                                foreach (object x in mine) if (string.Equals(Get(x, "ParameterName")?.ToString(), pn, StringComparison.Ordinal)) { bound = true; break; }
                                if (!bound) mine.Add(MvdbCommand.DeepCopyValue(tp));
                            }
                        }
                    }
                }
                foreach (object m in rowMaterials)
                {
                    // MaterialVariation -> the new asset's matching sub-object; a null slot stays null
                    object mv = Get(m, "MaterialVariation");
                    Guid oldClass = RefClassGuid(mv);
                    if (oldClass != Guid.Empty && materialVariations == null)
                    {
                        Guid nc;
                        if (!guidMap.TryGetValue(oldClass, out nc)) return db.Name + ": the source row points at " + oldClass + ", which the source variation does not export";
                        Set(m, "MaterialVariation", new PointerRef(new EbxImportReference { FileGuid = newFileGuid, ClassGuid = nc }));
                    }
                    foreach (object tp in (IList)Get(m, "TextureParameters"))
                    {
                        string pn = Get(tp, "ParameterName")?.ToString();
                        EbxAssetEntry te;
                        if (pn == null || !texRefs.TryGetValue(pn, out te)) continue;
                        Set(tp, "Value", new PointerRef(new EbxImportReference { FileGuid = te.Guid, ClassGuid = texInstance[pn] }));
                    }
                }
                foreach (object m in (IList)Get(row, "Materials"))
                    foreach (object tp in (IList)Get(m, "TextureParameters"))
                    {
                        Guid tf = RefFileGuid(Get(tp, "Value"));
                        if (tf != Guid.Empty) rowTextures.Add(tf);
                    }
                fresh.Add(row);
            }
            if (fresh.Count == 0) return null;
            foreach (object row in fresh) entries.Add(row);
            asset.AddDependency(newFileGuid);
            foreach (EbxAssetEntry te in texRefs.Values.Distinct()) asset.AddDependency(te.Guid);
            App.AssetManager.ModifyEbx(db.Name, asset);
            added = fresh.Count;
            report.Add(db.Name + ": " + added + " row(s) added");
            return null;
        }

        static bool MeshRegistered(IList entries, Guid meshGuid)
        {
            foreach (object e in entries) if (RefFileGuid(Get(e, "Mesh")) == meshGuid) return true;
            return false;
        }

        // ---- skins

        /// <summary>
        /// Points a character skin (a DataContainerCollectionBlueprint of VisualUnlockPartAssets) at the new variation.
        /// A part whose OutObjectBlueprint is a MeshAndVariationPair on a duplicated mesh gets its Variation replaced; a part
        /// that points straight at a mesh blueprint (a default skin) gets a new MeshAndVariationPair {mesh, new variation} in
        /// the skin's partition -- the shape the game's own named skins use (Kamino clone assault skin 6, the 501st).
        /// </summary>
        static string RepointSkin(string skinPath, HashSet<Guid> meshGuids, Guid newFileGuid, Guid newRootGuid,
            List<string> report, out int count)
        {
            count = 0;
            EbxAssetEntry se = App.AssetManager.GetEbxEntry(skinPath);
            if (se == null) return "--skin not found: " + skinPath;
            EbxAsset asset = App.AssetManager.GetEbx(se);
            int created = 0;
            foreach (object o in asset.Objects.ToList())
            {
                if (o.GetType().Name != "VisualUnlockPartAsset") continue;
                if (!(Get(o, "OutObjectBlueprint") is PointerRef pr)) continue;
                if (pr.Type == PointerRefType.Internal && pr.Internal != null && pr.Internal.GetType().Name == "MeshAndVariationPair")
                {
                    object pair = pr.Internal;
                    if (!meshGuids.Contains(RefFileGuid(Get(pair, "MeshAsset")))) continue;
                    Set(pair, "Variation", new PointerRef(new EbxImportReference { FileGuid = newFileGuid, ClassGuid = newRootGuid }));
                    count++;
                }
                else if (pr.Type == PointerRefType.External)
                {
                    EbxAssetEntry bp = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                    if (bp == null) continue;
                    string meshPath = LevelCommand.FindBlueprintMesh(App.AssetManager.GetEbx(bp).RootObject);
                    EbxAssetEntry me = meshPath != null ? App.AssetManager.GetEbxEntry(meshPath) : null;
                    if (me == null || !meshGuids.Contains(me.Guid)) continue;
                    dynamic pair = TypeLibrary.CreateObject("MeshAndVariationPair");
                    if (pair == null) return "the loaded profile has no MeshAndVariationPair type";
                    int id = LevelCommand.NextInternalId(asset);
                    pair.SetInstanceGuid(new AssetClassGuid(DeterministicGuid("pair|" + se.Name.ToLowerInvariant() + "|" + me.Name.ToLowerInvariant() + "|" + newFileGuid), id));
                    pair.MeshAsset = new PointerRef(new EbxImportReference { FileGuid = me.Guid, ClassGuid = RootInstanceGuid(me) });
                    pair.Variation = new PointerRef(new EbxImportReference { FileGuid = newFileGuid, ClassGuid = newRootGuid });
                    asset.AddObject(pair);
                    InProcessObjects.Mark(pair);
                    Set(o, "OutObjectBlueprint", new PointerRef(pair));
                    asset.AddDependency(me.Guid);
                    created++; count++;
                }
            }
            if (count == 0) { report.Add(skinPath + ": no part uses a duplicated mesh"); return null; }
            asset.AddDependency(newFileGuid);
            App.AssetManager.ModifyEbx(se.Name, asset);
            report.Add(skinPath + ": " + count + " part(s) now use the new variation" + (created > 0 ? " (" + created + " MeshAndVariationPair(s) created)" : ""));
            return null;
        }

        // ---- placements

        // used by PluginVariationRun (main)
        internal static string RepointPublic(string partition, HashSet<Guid> meshGuids, EbxAssetEntry srcEntry, uint srcHash, Guid newFileGuid, Guid newRootGuid,
            uint newHash, bool all, out int count) => Repoint(partition, meshGuids, srcEntry, srcHash, newFileGuid, newRootGuid, newHash, all, null, out count);

        static string Repoint(string partition, HashSet<Guid> meshGuids, EbxAssetEntry srcEntry, uint srcHash, Guid newFileGuid, Guid newRootGuid,
            uint newHash, bool all, ExportLogger logger, out int count)
        {
            count = 0;
            EbxAssetEntry pe = App.AssetManager.GetEbxEntry(partition);
            if (pe == null) return "--repoint partition not found: " + partition;
            EbxAsset asset = App.AssetManager.GetEbx(pe);
            foreach (object o in asset.Objects)
            {
                string tn = o.GetType().Name;
                if (tn == "StaticModelGroupEntityData")
                {
                    foreach (object member in (IList)Get(o, "MemberDatas") ?? new ArrayList())
                    {
                        if (!meshGuids.Contains(RefFileGuid(Get(member, "MeshAsset")))) continue;
                        IList iov = Get(member, "InstanceObjectVariation") as IList;
                        if (iov == null) continue;
                        int n = Convert.ToInt32(Get(member, "InstanceCount"), CultureInfo.InvariantCulture);
                        // an empty list means "every instance uses the default": fill it before repointing when asked to
                        if (iov.Count == 0 && all) for (int k = 0; k < n; k++) iov.Add(0u);
                        for (int k = 0; k < iov.Count; k++)
                        {
                            uint h = Convert.ToUInt32(iov[k], CultureInfo.InvariantCulture);
                            if (h == srcHash || (all && h == 0)) { iov[k] = newHash; count++; }
                        }
                    }
                }
                else if (tn.EndsWith("ReferenceObjectData", StringComparison.Ordinal))
                {
                    object ov = Get(o, "ObjectVariation");
                    if (ov != null && srcEntry != null && RefFileGuid(ov) == srcEntry.Guid)
                    {
                        Set(o, "ObjectVariation", new PointerRef(new EbxImportReference { FileGuid = newFileGuid, ClassGuid = newRootGuid }));
                        count++;
                    }
                }
            }
            if (count > 0)
            {
                asset.AddDependency(newFileGuid);
                App.AssetManager.ModifyEbx(pe.Name, asset);
            }
            return null;
        }

        // ---- helpers

        /// <summary>ObjectVariation.NameHash: Fnv1 of the lowercased asset name (verified on shipped assets).</summary>
        public static uint NameHash(string assetName) => (uint)Utils.HashString(assetName.ToLowerInvariant());

        /// <summary>The "&lt;mesh file&gt;" part of a variation depot name: the mesh asset's file name, lowercased.</summary>
        public static string MeshFileName(string meshAssetName)
        {
            string n = meshAssetName.ToLowerInvariant();
            return n.Substring(n.LastIndexOf('/') + 1);
        }

        public static Guid DeterministicGuid(string key)
        {
            using (var md5 = MD5.Create()) return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(key)));
        }

        public static bool TryParseVector(string v, out float[] f)
        {
            f = null;
            string[] p = v.Split(',');
            if (p.Length < 1 || p.Length > 4) return false;
            var r = new float[4];
            for (int i = 0; i < p.Length; i++)
                if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i])) return false;
            f = r; return true;
        }

        static EbxAssetEntry FindMeshByNameHash(string meshFile, uint meshNameHash)
        {
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("MeshAsset"))
            {
                if (MeshFileName(e.Name) != meshFile) continue;
                uint h;
                if (FrostyCLI.Adapters.Mesh.MeshCloner.ReadMeshSetName(e, out h) != null && h == meshNameHash) return e;
            }
            return null;
        }

        static Guid RootInstanceGuid(EbxAssetEntry e)
        {
            try { return RootInstanceGuidOf(App.AssetManager.GetEbx(e)); } catch { return Guid.Empty; }
        }

        static Guid RootInstanceGuidOf(EbxAsset a)
        {
            try { return ((AssetClassGuid)((dynamic)a.RootObject).GetInstanceGuid()).ExportedGuid; } catch { return Guid.Empty; }
        }

        static EbxAsset CloneEbx(EbxAsset source)
        {
            using (var writer = EbxBaseWriter.CreateProjectWriter(new MemoryStream(), EbxWriteFlags.DoNotSort | EbxWriteFlags.IncludeTransient))
            {
                writer.WriteAsset(source);
                byte[] buf = writer.ToByteArray();
                using (var reader = EbxReader.CreateProjectReader(new MemoryStream(buf)))
                    return reader.ReadAsset<EbxAsset>();
            }
        }

        static Guid RefFileGuid(object pointerRef)
        {
            if (!(pointerRef is PointerRef pr) || pr.Type != PointerRefType.External) return Guid.Empty;
            return pr.External.FileGuid;
        }

        static Guid RefClassGuid(object pointerRef)
        {
            if (!(pointerRef is PointerRef pr) || pr.Type != PointerRefType.External) return Guid.Empty;
            return pr.External.ClassGuid;
        }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(o);
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f?.GetValue(o);
        }

        static void Set(object o, string name, object value)
        {
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null)
            {
                if (value != null && p.PropertyType != value.GetType() && p.PropertyType.IsPrimitive) value = Convert.ChangeType(value, p.PropertyType, CultureInfo.InvariantCulture);
                p.SetValue(o, value); return;
            }
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null)
            {
                if (value != null && f.FieldType != value.GetType() && f.FieldType.IsPrimitive) value = Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture);
                f.SetValue(o, value);
            }
        }
    }
}
