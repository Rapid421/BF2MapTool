using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `level set-variations`: per-instance object variations on static-group members (InstanceObjectVariation). A
    /// variation recolours the member's mesh by binding to its MeshMaterialVariation sub-objects, so it must be made for
    /// the mesh the member draws, and the level's variation database needs a record for (mesh, variation NameHash) --
    /// `level validate` checks both (member-variation-not-in-mvdb). The list is kept exactly InstanceCount long (BFII
    /// reads it per instance; a short list runs off the end, game+0x6E6ED6A); unset instances stay 0 (the base look). Each entry is
    /// the variation's NameHash (BFII: List&lt;uint&gt;), resolved at load through the variation database record (mesh, hash).
    /// The Hoth 2015 port lost every 2015 variation in the group transplant (DICE moved them to ObjectSets/...): the
    /// floor boards came out without FloorBoards_01_Hoth (white) and the red wall lights glowed blue.
    /// <code>
    /// level set-variations &lt;level-ebx&gt; --set &lt;group&gt;:&lt;member&gt;=&lt;variation&gt;@&lt;i,j-k,...|all&gt;... [--from-file f]
    /// </code>
    /// --from-file: one set per line, the same &lt;group&gt;:&lt;member&gt;=&lt;variation&gt;@&lt;instances&gt; form ('#' comments).
    /// --register &lt;MeshVariationDatabase&gt;: make each (mesh, variation) usable by the level -- a missing database record is
    /// copied from a database that has one (shipped variations: DICE's own level databases), and the variation asset, its depot
    /// for the mesh ("&lt;variation&gt;/&lt;mesh file&gt;_&lt;MeshSet name hash&gt;/shaderblocks_variation/blocks", found by name) and the
    /// textures its record binds go into every bundle of that database. Without the depot in a loaded bundle the variation
    /// changes nothing, silently (`level validate`: member-variation-not-bundled / member-variation-depot-not-bundled).
    /// </summary>
    public static class LevelSetVariations
    {
        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = null, register = null;
            var sets = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--set" && i + 1 < args.Length) sets.Add(args[++i]);
                else if (args[i] == "--register" && i + 1 < args.Length) register = args[++i];
                else if (args[i] == "--from-file" && i + 1 < args.Length)
                {
                    foreach (string line in File.ReadAllLines(args[++i]))
                    {
                        string t = line.Trim();
                        if (t.Length > 0 && !t.StartsWith("#")) sets.Add(t);
                    }
                }
                else if (!args[i].StartsWith("--")) level = args[i];
            }
            if (level == null || sets.Count == 0)
            {
                Console.Error.WriteLine("Usage: frostycli level set-variations <level-ebx> --set <group>:<member>=<variation>@<i,j-k,...|all>... [--from-file f] [--register <MeshVariationDatabase>]");
                return 1;
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("level set-variations: AssetManager not initialized."); return 1; }
            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry(level);
            if (levelEntry == null) { Console.Error.WriteLine("level set-variations: level not found: " + level); return 1; }
            EbxAsset asset = App.AssetManager.GetEbx(levelEntry);
            EbxAssetEntry mvdb = null;
            if (register != null)
            {
                mvdb = App.AssetManager.GetEbxEntry(register);
                if (mvdb == null || mvdb.Type != "MeshVariationDatabase") { Console.Error.WriteLine("level set-variations: not a MeshVariationDatabase: " + register); return 1; }
            }
            var registered = new HashSet<string>();

            int members = 0, instances = 0, rows = 0;
            foreach (string s in sets)
            {
                int colon = s.IndexOf(':'), eq = s.IndexOf('='), at = s.LastIndexOf('@');
                if (colon < 0 || eq < colon || at < eq) { Console.Error.WriteLine("level set-variations: bad set (want <group>:<member>=<variation>@<instances>): " + s); return 1; }
                int g = int.Parse(s.Substring(0, colon), CultureInfo.InvariantCulture);
                int m = int.Parse(s.Substring(colon + 1, eq - colon - 1), CultureInfo.InvariantCulture);
                string variation = s.Substring(eq + 1, at - eq - 1);
                string which = s.Substring(at + 1);

                string err;
                object group = LevelCommand.FindStaticGroup(asset.RootObject, g, out err);
                if (group == null) { Console.Error.WriteLine("level set-variations: " + err); return 1; }
                IList list;
                object member = LevelCommand.FindMember(group, m, out list, out err);
                if (member == null) { Console.Error.WriteLine("level set-variations: group " + g + " member " + m + ": " + err); return 1; }
                EbxAssetEntry ve = App.AssetManager.GetEbxEntry(variation);
                if (ve == null || ve.Type != "ObjectVariation") { Console.Error.WriteLine("level set-variations: not an ObjectVariation: " + variation); return 1; }

                int count = LevelCommand.ConvertToInt(LevelCommand.ReadMember(member, "InstanceCount"));
                IList vars = LevelCommand.ReadList(member, "InstanceObjectVariation");
                if (vars == null) { Console.Error.WriteLine("level set-variations: group " + g + " member " + m + " has no InstanceObjectVariation list"); return 1; }
                Type vt = PropertyPathWriter.ListElementType(vars);
                while (vars.Count < count) vars.Add(Activator.CreateInstance(vt));
                while (vars.Count > count) vars.RemoveAt(vars.Count - 1);

                var idx = ParseInstances(which, count, out err);
                if (idx == null) { Console.Error.WriteLine("level set-variations: group " + g + " member " + m + ": " + err); return 1; }
                if (mvdb != null)
                {
                    Guid meshGuid = PointerFileGuid(LevelCommand.ReadMember(member, "MeshAsset"));
                    EbxAssetEntry me = meshGuid != Guid.Empty ? App.AssetManager.GetEbxEntry(meshGuid) : null;
                    if (me == null) { Console.Error.WriteLine("level set-variations: group " + g + " member " + m + " has no MeshAsset"); return 1; }
                    if (registered.Add(me.Guid + ":" + ve.Guid))
                    {
                        string rerr = Register(mvdb, me, ve, logger, out bool copied);
                        if (rerr != null) { Console.Error.WriteLine("level set-variations: " + rerr); return 1; }
                        if (copied) rows++;
                    }
                }
                // BFII keeps the variation's NameHash per instance (List<uint>; 0 = the base look); a PointerRef list is
                // handled too for profiles that reference the asset instead
                object value;
                if (vt == typeof(uint))
                {
                    try { value = Convert.ToUInt32(LevelCommand.ReadMember(App.AssetManager.GetEbx(ve).RootObject, "NameHash"), CultureInfo.InvariantCulture); }
                    catch (Exception ex) { Console.Error.WriteLine("level set-variations: " + ve.Name + ": no NameHash (" + ex.Message + ")"); return 1; }
                }
                else value = new PointerRef(new EbxImportReference { FileGuid = ve.Guid, ClassGuid = LevelCommand.ResolveExportedGuid(ve) });
                foreach (int k in idx) vars[k] = value;
                if (member.GetType().IsValueType) list[m] = member;
                asset.AddDependency(ve.Guid);
                members++; instances += idx.Count;
                logger.Log("level set-variations: group {0} member {1}: {2} of {3} instance(s) -> {4}", g, m, idx.Count, count, ve.Name);
            }
            if (!EbxWriteCommand.CallModifyEbx(level, asset, logger)) return 1;
            formatter.WriteResult("OK", "level set-variations", new Dictionary<string, string>
            {
                { "level", level },
                { "members", members.ToString(CultureInfo.InvariantCulture) },
                { "instances", instances.ToString(CultureInfo.InvariantCulture) },
                { "records_copied", rows.ToString(CultureInfo.InvariantCulture) },
            });
            return 0;
        }

        /// <summary>
        /// Makes (mesh, variation) usable through <paramref name="mvdb"/>: copies the database record from the first database that
        /// has it when this one does not, then bundles the variation asset, its depot for the mesh and every texture the record binds
        /// (with their res and chunks) into each of the database's bundles.
        /// </summary>
        internal static string Register(EbxAssetEntry mvdb, EbxAssetEntry mesh, EbxAssetEntry ve, ExportLogger logger, out bool copied)
        {
            copied = false;
            uint hash;
            try { hash = Convert.ToUInt32(LevelCommand.ReadMember(App.AssetManager.GetEbx(ve).RootObject, "NameHash"), CultureInfo.InvariantCulture); }
            catch (Exception ex) { return ve.Name + ": no NameHash (" + ex.Message + ")"; }
            object row = FindRow(mvdb, mesh.Guid, hash);
            if (row == null)
            {
                EbxAssetEntry donor = null;
                foreach (EbxAssetEntry db in App.AssetManager.EnumerateEbx("MeshVariationDatabase"))
                    if (db != mvdb && db.ContainsDependency(ve.Guid) && FindRow(db, mesh.Guid, hash) != null) { donor = db; break; }
                if (donor == null) return "no MeshVariationDatabase has a record for " + ve.Name + " on " + mesh.Name + " (build one with mesh variation-dupe)";
                string cerr = MvdbCommand.CopyEntry(mvdb, donor, mesh, hash, logger, out int materials);
                if (cerr != null) return cerr;
                copied = true;
                row = FindRow(mvdb, mesh.Guid, hash);
                logger.Log("level set-variations: {0} on {1}: record copied from {2} ({3} material(s))", ve.Name, mesh.Name, donor.Name, materials);
            }
            string depotName = LevelCommand.VariationDepotName(ve.Name, mesh);
            ResAssetEntry depot = depotName != null ? App.AssetManager.GetResEntry(depotName) : null;
            if (depot == null) return ve.Name + " has no depot for " + mesh.Name + " (" + depotName + "); it would change nothing";
            var textures = new List<EbxAssetEntry>();
            foreach (object mat in (IList)LevelCommand.ReadMember(row, "Materials") ?? new ArrayList())
                foreach (object tp in (IList)LevelCommand.ReadMember(mat, "TextureParameters") ?? new ArrayList())
                {
                    Guid tg = PointerFileGuid(LevelCommand.ReadMember(tp, "Value"));
                    EbxAssetEntry te = tg != Guid.Empty ? App.AssetManager.GetEbxEntry(tg) : null;
                    if (te != null && !textures.Contains(te)) textures.Add(te);
                }
            var texRes = new List<Tuple<EbxAssetEntry, List<ulong>, List<Guid>>>();
            foreach (EbxAssetEntry te in textures)
            {
                BundleCommand.GatherSubResources(te, out List<ulong> rids, out List<Guid> chunks);
                BundleCommand.LinkSubResources(te, rids, chunks);
                texRes.Add(Tuple.Create(te, rids, chunks));
            }
            int bundles = 0;
            foreach (int b in mvdb.EnumerateBundles())
            {
                bundles++;
                ve.AddToBundle(b);
                depot.AddToBundle(b);
                foreach (var t in texRes)
                {
                    t.Item1.AddToBundle(b);
                    foreach (ulong rid in t.Item2) App.AssetManager.GetResEntry(rid)?.AddToBundle(b);
                    foreach (Guid c in t.Item3) App.AssetManager.GetChunkEntry(c)?.AddToBundle(b);
                }
            }
            if (bundles == 0) return mvdb.Name + " is in no bundle";
            logger.Log("level set-variations: {0} on {1}: variation, depot and {2} texture(s) bundled with {3}", ve.Name, mesh.Name, textures.Count, mvdb.Name);
            return null;
        }

        static object FindRow(EbxAssetEntry db, Guid meshGuid, uint hash)
        {
            IList entries = MvdbCommand.FindEntries(App.AssetManager.GetEbx(db).RootObject);
            if (entries == null) return null;
            foreach (object e in entries)
                if (PointerFileGuid(LevelCommand.ReadMember(e, "Mesh")) == meshGuid
                    && Convert.ToUInt32(LevelCommand.ReadMember(e, "VariationAssetNameHash"), CultureInfo.InvariantCulture) == hash) return e;
            return null;
        }

        static Guid PointerFileGuid(object pointerRef)
        {
            if (!(pointerRef is PointerRef pr) || pr.Type != PointerRefType.External) return Guid.Empty;
            return pr.External.FileGuid;
        }

        /// <summary>"all" or a comma list of indices and inclusive ranges (3,7-9); every index below <paramref name="count"/>.</summary>
        public static List<int> ParseInstances(string which, int count, out string err)
        {
            err = null;
            var r = new List<int>();
            if (which.Equals("all", StringComparison.OrdinalIgnoreCase)) { for (int i = 0; i < count; i++) r.Add(i); return r; }
            foreach (string part in which.Split(','))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int dash = p.IndexOf('-');
                int a, b;
                if (dash > 0) { a = int.Parse(p.Substring(0, dash), CultureInfo.InvariantCulture); b = int.Parse(p.Substring(dash + 1), CultureInfo.InvariantCulture); }
                else a = b = int.Parse(p, CultureInfo.InvariantCulture);
                if (a < 0 || b >= count || b < a) { err = "instance range " + p + " is outside 0.." + (count - 1); return null; }
                for (int i = a; i <= b; i++) r.Add(i);
            }
            return r;
        }
    }
}
