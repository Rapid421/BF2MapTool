using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Adapters.Mesh;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `mesh clone-radiosity`: give a level its own copy of a shipped mesh that carries another game's lightmap uvs.
    /// DICE kept most 2015 Hoth meshes vertex for vertex in BFII but re-unwrapped their lightmap uvs for BFII's bake, so a
    /// 2015 chart does not fit them; writing 2015's uvs into the shipped mesh would break every BFII level that places it.
    /// The clone (MeshCloner: EBX, MeshSet res, geometry chunks, re-keyed and salted shader block depot) gets 2015's uvs by
    /// vertex index on every LOD section that declares the channel (the same check as radiosity-apply-lods), the donor's
    /// MVDB material records (`mvdb add --like`), a place in the level bundle, and the level's group members that drew the
    /// original are repointed to it (members render their own MeshAsset).
    /// <code>
    /// mesh clone-radiosity --list &lt;file: source mesh TAB new mesh TAB raduv3&gt; --mvdb &lt;db&gt; --bundle &lt;bundle&gt;
    ///                       [--repoint &lt;partition&gt;]... [--tolerance m] [--report out.json]
    /// </code>
    /// </summary>
    public static class MeshCloneRadiosity
    {
        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string list = null, mvdb = null, bundle = null, reportPath = null;
            float tol = 0.01f;
            bool partialOk = false;   // --partial-ok: keep (save) the clones that worked; a second run in a fresh process retries the rest
            var repoint = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--list" && i + 1 < args.Length) list = args[++i];
                else if (args[i] == "--mvdb" && i + 1 < args.Length) mvdb = args[++i];
                else if (args[i] == "--bundle" && i + 1 < args.Length) bundle = args[++i];
                else if (args[i] == "--repoint" && i + 1 < args.Length) repoint.Add(args[++i]);
                else if (args[i] == "--tolerance" && i + 1 < args.Length) tol = float.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--report" && i + 1 < args.Length) reportPath = args[++i];
                else if (args[i] == "--partial-ok") partialOk = true;
            }
            if (list == null || mvdb == null || bundle == null)
            {
                Console.Error.WriteLine("Usage: frostycli mesh clone-radiosity --list <src TAB new TAB raduv3> --mvdb <db> --bundle <bundle> [--repoint <partition>]... [--tolerance m] [--report json]");
                return 1;
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("mesh clone-radiosity: AssetManager not initialized."); return 1; }
            var report = new Newtonsoft.Json.Linq.JArray();
            var map = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);   // source mesh -> clone
            int failures = 0;
            foreach (string raw in File.ReadAllLines(list))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] p = line.Split('\t');
                if (p.Length < 3) { Console.Error.WriteLine("mesh clone-radiosity: need 3 columns: " + line); return 1; }
                string src = p[0].Trim(), dst = p[1].Trim(), raduv = p[2].Trim();
                var r = new Newtonsoft.Json.Linq.JObject { ["source"] = src, ["clone"] = dst };
                EbxAssetEntry made = App.AssetManager.GetEbxEntry(dst);
                bool fresh = made == null;     // a clone an earlier run made already has its MVDB entry and bundle place
                if (made == null)
                {
                    string err = MeshCloner.Clone(src, dst, out made);
                    if (err != null) { Console.Error.WriteLine("mesh clone-radiosity: clone " + src + ": " + err); failures++; continue; }
                    int resident;
                    err = MeshCloner.MarkAllLodsResident(made, out resident);
                    if (err != null) { Console.Error.WriteLine("mesh clone-radiosity: " + dst + ": " + err); failures++; continue; }
                }
                var lods = new List<List<RadiosityUvPort.LodSection>>();
                string lerr = RadiosityUvPort.LoadLods(raduv, lods);
                if (lerr != null) { Console.Error.WriteLine("mesh clone-radiosity: " + lerr); failures++; continue; }
                int written, total, skipped; string note;
                string aerr = RadiosityUvPort.ApplyLodsByIndex(dst, lods, true, out written, out total, out skipped, out note, tol);
                if (aerr != null) { Console.Error.WriteLine("mesh clone-radiosity: " + dst + ": " + aerr); failures++; continue; }
                r["written"] = written; r["channelVertices"] = total; r["skippedSections"] = skipped; r["note"] = note;
                r["complete"] = skipped == 0 && written == total && total > 0;
                if (fresh && WriteOpBus.Execute("mvdb-add", new[] { mvdb, "--mesh", dst, "--like", src }, logger, formatter) != 0)
                { Console.Error.WriteLine("mesh clone-radiosity: mvdb add failed for " + dst); failures++; continue; }
                if (fresh && WriteOpBus.Execute("bundle-add", new[] { dst, "--to", bundle, "--with-resources" }, logger, formatter) != 0)
                { Console.Error.WriteLine("mesh clone-radiosity: bundle add failed for " + dst); failures++; continue; }
                map[src] = made;
                report.Add(r);
                logger.Log("mesh clone-radiosity: {0} -> {1}: {2}/{3} lightmap uvs from 2015{4}", src, dst, written, total,
                    skipped > 0 ? " (" + skipped + " section(s) skipped: " + note + ")" : "");
            }
            // repoint the group members that draw a source mesh
            int repointed = 0;
            foreach (string part in repoint)
            {
                EbxAssetEntry pe = App.AssetManager.GetEbxEntry(part);
                if (pe == null) { Console.Error.WriteLine("mesh clone-radiosity: --repoint partition not found: " + part); return 1; }
                EbxAsset asset = App.AssetManager.GetEbx(pe);
                int before = repointed;
                foreach (object o in asset.Objects)
                {
                    if (o == null || o.GetType().Name != "StaticModelGroupEntityData") continue;
                    IList members = LevelCommand.ReadList(o, "MemberDatas");
                    if (members == null) continue;
                    for (int m = 0; m < members.Count; m++)
                    {
                        object member = LevelCommand.ResolveInternal(members[m]) ?? members[m];
                        object meshRef = LevelCommand.ReadMember(member, "MeshAsset");
                        string meshName = RefName(meshRef);
                        EbxAssetEntry clone;
                        if (meshName == null || !map.TryGetValue(meshName, out clone)) continue;
                        Guid cls = RootGuid(App.AssetManager.GetEbx(clone).RootObject);
                        EbxRefWriter.SetRefByPath(member, "MeshAsset", clone.Guid, cls);
                        if (member.GetType().IsValueType) members[m] = member;
                        asset.AddDependency(clone.Guid);
                        repointed++;
                    }
                }
                if (repointed > before && !EbxWriteCommand.CallModifyEbx(part, asset, logger)) return 1;
            }
            if (reportPath != null) File.WriteAllText(reportPath, report.ToString());
            formatter.WriteResult(failures == 0 ? "OK" : "PARTIAL", "mesh clone-radiosity", new Dictionary<string, string>
            {
                { "clones", map.Count.ToString(CultureInfo.InvariantCulture) },
                { "members_repointed", repointed.ToString(CultureInfo.InvariantCulture) },
                { "failures", failures.ToString(CultureInfo.InvariantCulture) },
            });
            // a clone can fail on the SDK's in-process EBX re-read after other writes (ArgumentOutOfRange in ReadField,
            // seen on corridor_01_doorsideentrance and hangarsystemroofcables_512_01; both clone cleanly in a fresh process)
            return failures == 0 || (partialOk && map.Count > 0) ? 0 : 1;
        }

        static string RefName(object pr)
        {
            if (pr == null) return null;
            Type t = pr.GetType();
            string type = (t.GetField("Type")?.GetValue(pr) ?? t.GetProperty("Type")?.GetValue(pr))?.ToString();
            if (type != "External") return null;
            object ext = t.GetField("External")?.GetValue(pr) ?? t.GetProperty("External")?.GetValue(pr);
            object fg = ext?.GetType().GetField("FileGuid")?.GetValue(ext) ?? ext?.GetType().GetProperty("FileGuid")?.GetValue(ext);
            return fg is Guid g ? App.AssetManager.GetEbxEntry(g)?.Name : null;
        }

        static Guid RootGuid(object root)
        {
            try
            {
                object g = root.GetType().GetMethod("GetInstanceGuid")?.Invoke(root, null);
                object e = g?.GetType().GetProperty("ExportedGuid")?.GetValue(g) ?? g?.GetType().GetField("ExportedGuid")?.GetValue(g);
                return e is Guid x ? x : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }
    }
}
