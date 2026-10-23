using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `level repoint-members`: give existing static-group members another blueprint (and its mesh) without rebuilding
    /// the group. A member's placements, physics parts, detail shapes and raycast material lookups belong to the member
    /// slot in the group's physics resource, not to the blueprint, so only MemberType and MeshAsset change -- which is
    /// why the new blueprint must describe the parts the slot already has: PhysicsPartInfos == PhysicsPartCountPerInstance
    /// (else damaging an instance crashes, game+0x601CD1), PartComponentData >= PartComponentCount (game+0xBC0848), and a
    /// composite mesh has exactly that many parts (game+0x6E6ED6A). Used to replace stand-ins and DICE revisions whose
    /// part structure differs from the source group (2015 BoxMedium_02 on BFII's 1-part Box_M_02) by ported blueprints.
    /// The new mesh must already be in the level's variation database and bundle (port import does both); `level
    /// validate` checks it.
    /// <code>
    /// level repoint-members &lt;level-ebx&gt; --set &lt;group&gt;:&lt;member&gt;=&lt;blueprint&gt;[@&lt;mesh&gt;]... [--force]
    /// level repoint-members &lt;level-ebx&gt; --from &lt;blueprint&gt; --to &lt;blueprint&gt;[@&lt;mesh&gt;] [--parts n] [--force]
    /// </code>
    /// --if-current &lt;blueprint&gt; (repeatable) leaves a --set member alone unless it still uses one of those blueprints,
    /// so a script can be re-run, or run after another stream already gave the member its real blueprint.
    /// --from/--to repoints every member of every group in the asset that uses the --from blueprint (only those with
    /// n physics parts per instance when --parts is given).
    /// </summary>
    public static class LevelRepointMembers
    {
        private sealed class Target
        {
            public int Group;
            public int Member;
            public string Blueprint;
            public string Mesh;
        }

        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = null, from = null, to = null;
            int onlyParts = -1;
            bool force = false;
            var sets = new List<Target>();
            var ifCurrent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--set" && i + 1 < args.Length)
                {
                    string v = args[++i];
                    int eq = v.IndexOf('='), colon = v.IndexOf(':');
                    if (eq < 0 || colon < 0 || colon > eq) { Console.Error.WriteLine("level repoint-members: --set wants <group>:<member>=<blueprint>[@<mesh>], got " + v); return 1; }
                    var t = new Target
                    {
                        Group = int.Parse(v.Substring(0, colon), CultureInfo.InvariantCulture),
                        Member = int.Parse(v.Substring(colon + 1, eq - colon - 1), CultureInfo.InvariantCulture)
                    };
                    SplitBlueprint(v.Substring(eq + 1), out t.Blueprint, out t.Mesh);
                    sets.Add(t);
                }
                else if (args[i] == "--from" && i + 1 < args.Length) from = args[++i];
                else if (args[i] == "--to" && i + 1 < args.Length) to = args[++i];
                else if (args[i] == "--parts" && i + 1 < args.Length) onlyParts = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--force") force = true;
                else if (args[i] == "--if-current" && i + 1 < args.Length) ifCurrent.Add(args[++i]);
                else if (!args[i].StartsWith("--")) level = args[i];
            }
            if (level == null || (sets.Count == 0 && (from == null || to == null)))
            {
                Console.Error.WriteLine("Usage: frostycli level repoint-members <level-ebx> --set <group>:<member>=<blueprint>[@<mesh>]... | --from <blueprint> --to <blueprint>[@<mesh>] [--parts n] [--force]");
                return 1;
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("level repoint-members: AssetManager not initialized."); return 1; }
            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry(level);
            if (levelEntry == null) { Console.Error.WriteLine("level repoint-members: level not found: " + level); return 1; }
            EbxAsset asset = App.AssetManager.GetEbx(levelEntry);

            if (from != null)
            {
                string toBp, toMesh;
                SplitBlueprint(to, out toBp, out toMesh);
                for (int g = 0; ; g++)
                {
                    string gerr;
                    object group = LevelCommand.FindStaticGroup(asset.RootObject, g, out gerr);
                    if (group == null) break;
                    IList members = LevelCommand.ReadList(group, "MemberDatas");
                    for (int m = 0; members != null && m < members.Count; m++)
                    {
                        object member = LevelCommand.ResolveInternal(members[m]) ?? members[m];
                        string bp = member != null ? LevelCommand.ResolveBlueprintPath(LevelCommand.ReadMember(member, "MemberType")) : null;
                        if (bp == null || !bp.Equals(from, StringComparison.OrdinalIgnoreCase)) continue;
                        if (onlyParts >= 0 && LevelCommand.ConvertToInt(LevelCommand.ReadMember(member, "PhysicsPartCountPerInstance")) != onlyParts) continue;
                        sets.Add(new Target { Group = g, Member = m, Blueprint = toBp, Mesh = toMesh });
                    }
                }
                if (sets.Count == 0) { Console.Error.WriteLine("level repoint-members: no member of " + level + " uses " + from + (onlyParts >= 0 ? " with " + onlyParts + " part(s)" : "")); return 1; }
            }

            int changed = 0, refused = 0, kept = 0;
            foreach (Target t in sets)
            {
                string where = "group " + t.Group + " member " + t.Member;
                string err;
                object group = LevelCommand.FindStaticGroup(asset.RootObject, t.Group, out err);
                if (group == null) { Console.Error.WriteLine("level repoint-members: " + err); return 1; }
                IList members;
                object member = LevelCommand.FindMember(group, t.Member, out members, out err);
                if (member == null) { Console.Error.WriteLine("level repoint-members: " + where + ": " + err); return 1; }
                string current = LevelCommand.ResolveBlueprintPath(LevelCommand.ReadMember(member, "MemberType"));
                if (current != null && current.Equals(t.Blueprint, StringComparison.OrdinalIgnoreCase)) { kept++; continue; }
                if (ifCurrent.Count > 0 && (current == null || !ifCurrent.Contains(current)))
                {
                    logger.Log("level repoint-members: {0} uses {1}, not a --if-current blueprint; left alone", where, current);
                    kept++;
                    continue;
                }

                EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(t.Blueprint);
                if (bpEntry == null) { Console.Error.WriteLine("level repoint-members: blueprint not found: " + t.Blueprint); return 1; }
                EbxAsset bpAsset = App.AssetManager.GetEbx(bpEntry);
                object entity = LevelCommand.ResolveInternal(LevelCommand.ReadMember(bpAsset.RootObject, "Object"));
                if (entity == null) { Console.Error.WriteLine("level repoint-members: " + t.Blueprint + " has no Object"); return 1; }
                Guid entityGuid;
                try
                {
                    dynamic dyn = entity;
                    entityGuid = (Guid)dyn.GetInstanceGuid().ExportedGuid;
                    if (!(bool)dyn.GetInstanceGuid().IsExported || entityGuid == Guid.Empty)
                    { Console.Error.WriteLine("level repoint-members: " + t.Blueprint + "'s entity is not exported; a member cannot reference it"); return 1; }
                }
                catch (Exception ex) { Console.Error.WriteLine("level repoint-members: " + t.Blueprint + ": " + ex.Message); return 1; }
                string meshPath = t.Mesh ?? LevelCommand.FindBlueprintMesh(bpAsset.RootObject);
                EbxAssetEntry meshEntry = meshPath != null ? App.AssetManager.GetEbxEntry(meshPath) : null;
                if (meshEntry == null) { Console.Error.WriteLine("level repoint-members: " + t.Blueprint + ": no mesh (" + meshPath + ")"); return 1; }

                // the slot's part structure against the new blueprint's
                IList inline = LevelCommand.ReadList(member, "InstanceTransforms");
                bool havok = inline == null || inline.Count == 0;
                int ppc = LevelCommand.ConvertToInt(LevelCommand.ReadMember(member, "PhysicsPartCountPerInstance"));
                int pcc = LevelCommand.ConvertToInt(LevelCommand.ReadMember(member, "PartComponentCount"));
                IList infos = LevelCommand.ReadList(entity, "PhysicsPartInfos");
                int partInfos = infos != null ? infos.Count : 0, partComponents = 0;
                foreach (object o in bpAsset.Objects) if (o != null && o.GetType().Name == "PartComponentData") partComponents++;
                var problems = new List<string>();
                if (havok && ppc > 0 && ppc != partInfos)
                    problems.Add(ppc + " physics part(s) per instance but " + partInfos + " PhysicsPartInfo(s) (game+0x601CD1 on damage)");
                if (pcc > partComponents)
                    problems.Add(pcc + " part component(s) but " + partComponents + " PartComponentData (game+0xBC0848)");
                if (havok && meshEntry.Type == "CompositeMeshAsset")
                {
                    string mtype;
                    int meshParts = FrostyCLI.Adapters.Mesh.MeshPartInfo.CompositePartCount(meshEntry, out mtype);
                    if (meshParts > 0 && meshParts != ppc)
                        problems.Add("composite mesh of " + meshParts + " part(s) for " + ppc + " physics part(s) per instance (game+0x6E6ED6A)");
                }
                if (problems.Count > 0)
                {
                    string msg = "level repoint-members: " + where + " -> " + t.Blueprint + ": " + string.Join("; ", problems);
                    if (!force) { Console.Error.WriteLine(msg); refused++; continue; }
                    logger.LogWarning(msg + " (--force)");
                }
                IList variations = LevelCommand.ReadList(member, "InstanceObjectVariation");
                int boundVariations = 0;
                if (variations != null)
                    foreach (object v in variations)
                        if (v != null && LevelCommand.ResolveBlueprintPath(v) != null) boundVariations++;
                if (boundVariations > 0)
                    logger.LogWarning("level repoint-members: " + where + " keeps " + boundVariations + " object variation(s) made for its old mesh; check they bind to " + meshEntry.Name);

                string oldBp = LevelCommand.ResolveBlueprintPath(LevelCommand.ReadMember(member, "MemberType"));
                EbxRefWriter.SetRefByPath(member, "MemberType", bpEntry.Guid, entityGuid);
                EbxRefWriter.SetRefByPath(member, "MeshAsset", meshEntry.Guid, LevelCommand.ResolveExportedGuid(meshEntry));
                if (member.GetType().IsValueType) members[t.Member] = member;
                asset.AddDependency(bpEntry.Guid);
                asset.AddDependency(meshEntry.Guid);
                changed++;
                logger.Log("level repoint-members: {0}: {1} -> {2} ({3})", where, oldBp, t.Blueprint, meshEntry.Name);
            }
            if (changed > 0 && !EbxWriteCommand.CallModifyEbx(level, asset, logger)) return 1;
            formatter.WriteResult(refused == 0 ? "OK" : "PARTIAL", "level repoint-members", new Dictionary<string, string>
            {
                { "level", level },
                { "repointed", changed.ToString(CultureInfo.InvariantCulture) },
                { "refused", refused.ToString(CultureInfo.InvariantCulture) },
                { "left_alone", kept.ToString(CultureInfo.InvariantCulture) },
            });
            return refused == 0 ? 0 : 1;
        }

        private static void SplitBlueprint(string v, out string blueprint, out string mesh)
        {
            int at = v.IndexOf('@');
            blueprint = at < 0 ? v : v.Substring(0, at);
            mesh = at < 0 ? null : v.Substring(at + 1);
        }
    }
}
