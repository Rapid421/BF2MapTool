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
    /// `level radiosity-override`: rewrite how a class of static-group placements is lit by Enlighten, without touching
    /// the bake. Every member whose MeshAsset path contains one of the --mesh substrings has each entry of its
    /// InstanceRadiosityTypeOverride that equals --from set to --to (the RadiosityTypeOverride_ prefix is optional;
    /// --from any pads the list to InstanceCount with None first and rewrites every entry).
    /// <code>
    /// level radiosity-override &lt;level-ebx&gt; --mesh &lt;substring&gt;... --from &lt;type|any&gt; --to &lt;type&gt;
    /// </code>
    /// Types (BFII RadiosityTypeOverride): None (the mesh's own EnlightenType; a Static mesh then needs a lightmap chart),
    /// Dynamic (lit by the light probes), LightProbe, Static, TerrainProjected (samples the terrain's lightmap), Proxy.
    /// Used to A/B the 2015 trench pieces, which 2015 draws TerrainProjected, against probe lighting.
    /// </summary>
    public static class LevelRadiosityOverride
    {
        const string Prefix = "RadiosityTypeOverride_";

        internal static string Norm(string t)
        {
            if (t == null || t.Equals("any", StringComparison.OrdinalIgnoreCase)) return t?.ToLowerInvariant();
            return t.StartsWith(Prefix, StringComparison.Ordinal) ? t : Prefix + t;
        }

        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = null, from = null, to = null;
            var meshes = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mesh" && i + 1 < args.Length) meshes.Add(args[++i]);
                else if (args[i] == "--from" && i + 1 < args.Length) from = Norm(args[++i]);
                else if (args[i] == "--to" && i + 1 < args.Length) to = Norm(args[++i]);
                else if (!args[i].StartsWith("--")) level = args[i];
            }
            if (level == null || meshes.Count == 0 || from == null || to == null || to == "any")
            {
                Console.Error.WriteLine("Usage: frostycli level radiosity-override <level-ebx> --mesh <substring>... --from <type|any> --to <type>");
                return 1;
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("level radiosity-override: AssetManager not initialized."); return 1; }
            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry(level);
            if (levelEntry == null) { Console.Error.WriteLine("level radiosity-override: level not found: " + level); return 1; }
            EbxAsset asset = App.AssetManager.GetEbx(levelEntry);

            int changed = 0, membersHit = 0;
            var perMesh = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int g = 0; ; g++)
            {
                string gerr;
                object group = LevelCommand.FindStaticGroup(asset.RootObject, g, out gerr);
                if (group == null) break;
                IList members = LevelCommand.ReadList(group, "MemberDatas");
                for (int m = 0; members != null && m < members.Count; m++)
                {
                    object member = LevelCommand.ResolveInternal(members[m]) ?? members[m];
                    if (member == null) continue;
                    string mesh = LevelCommand.ResolveBlueprintPath(LevelCommand.ReadMember(member, "MeshAsset"));
                    if (mesh == null || !meshes.Exists(s => mesh.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    IList rad = LevelCommand.ReadList(member, "InstanceRadiosityTypeOverride");
                    if (rad == null) { Console.Error.WriteLine("level radiosity-override: group " + g + " member " + m + " has no InstanceRadiosityTypeOverride"); return 1; }
                    Type et = PropertyPathWriter.ListElementType(rad);
                    object target;
                    try { target = Enum.Parse(et, to); }
                    catch (Exception) { Console.Error.WriteLine("level radiosity-override: unknown type " + to); return 1; }
                    int n = 0;
                    if (from == "any")
                    {
                        int count = LevelCommand.ConvertToInt(LevelCommand.ReadMember(member, "InstanceCount"));
                        while (rad.Count < count) rad.Add(Enum.Parse(et, Prefix + "None"));
                        for (int k = 0; k < rad.Count; k++) if (!Equals(rad[k], target)) { rad[k] = target; n++; }
                    }
                    else
                    {
                        for (int k = 0; k < rad.Count; k++)
                            if (string.Equals(rad[k]?.ToString(), from, StringComparison.Ordinal)) { rad[k] = target; n++; }
                    }
                    if (n == 0) continue;
                    if (member.GetType().IsValueType) members[m] = member;
                    changed += n; membersHit++;
                    int prev; perMesh.TryGetValue(mesh, out prev); perMesh[mesh] = prev + n;
                }
            }
            foreach (var kv in perMesh) logger.Log("level radiosity-override: {0} instance(s) of {1}", kv.Value, kv.Key);
            if (changed > 0 && !DryRun.Active && !EbxWriteCommand.CallModifyEbx(level, asset, logger)) return 1;
            formatter.WriteResult("OK", "level radiosity-override", new Dictionary<string, string>
            {
                { "level", level }, { "from", from }, { "to", to },
                { "members", membersHit.ToString(CultureInfo.InvariantCulture) },
                { "instances", changed.ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }
    }
}
