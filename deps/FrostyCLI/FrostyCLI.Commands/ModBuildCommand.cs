using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;

namespace FrostyCLI.Commands
{
    public class ModBuildCommand : ICommand
    {
        public string Name { get { return "mod"; } }

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length == 0)
            {
                logger.LogError("Usage: mod <build|diff|apply|lint|test> ...");
                return 2;
            }
            switch (args[0].ToLowerInvariant())
            {
                case "build": return ExecuteBuild(args, logger, formatter);
                case "diff":  return ExecuteDiff(args, logger, formatter);
                case "apply": return ExecuteApply(args, logger, formatter);
                case "lint":  return ModLintCommand.Execute(Tail(args), logger, formatter);
                case "test":  return ExecuteTest(args, logger, formatter);
                case "review": return ModReview.Execute(args, logger, formatter);
                case "merge": return ModMerge.Execute(args, logger, formatter);
                default:
                    logger.LogError("mod: unknown subcommand '" + args[0] + "'. Use: build, diff, apply, merge, lint, test, review");
                    return 2;
            }
        }

        private static string[] Tail(string[] args)
        {
            var rest = new string[args.Length - 1];
            Array.Copy(args, 1, rest, 0, rest.Length);
            return rest;
        }

        public static int ExecuteBuild(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string outPath  = null;
            var    settings = new ModSettings();

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--out":
                        if (i + 1 < args.Length) outPath = args[++i];
                        else { logger.LogError("--out requires a value."); return 2; }
                        break;
                    case "--mod-name":
                        if (i + 1 < args.Length) settings.Name = args[++i];
                        else { logger.LogError("--mod-name requires a value."); return 2; }
                        break;
                    case "--mod-author":
                        if (i + 1 < args.Length) settings.Author = args[++i];
                        else { logger.LogError("--mod-author requires a value."); return 2; }
                        break;
                    case "--mod-version":
                        if (i + 1 < args.Length) settings.Version = args[++i];
                        else { logger.LogError("--mod-version requires a value."); return 2; }
                        break;
                    case "--mod-description":
                        if (i + 1 < args.Length) settings.Description = args[++i];
                        else { logger.LogError("--mod-description requires a value."); return 2; }
                        break;
                    case "--mod-category":
                        if (i + 1 < args.Length) settings.Category = args[++i];
                        else { logger.LogError("--mod-category requires a value."); return 2; }
                        break;
                    case "--embed":
                        // --embed <name>=<file>: an extra embedded resource in the .fbmod (beside the icon and screenshots)
                        {
                            string spec = i + 1 < args.Length ? args[++i] : null;
                            int eq = spec == null ? -1 : spec.IndexOf('=');
                            if (eq <= 0 || !File.Exists(spec.Substring(eq + 1))) { logger.LogError("--embed takes <name>=<existing file>."); return 2; }
                            settings.Embedded[spec.Substring(0, eq)] = File.ReadAllBytes(spec.Substring(eq + 1));
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(outPath))
            {
                logger.LogError("--out <file.fbmod> is required.");
                return 2;
            }

            if (string.IsNullOrEmpty(settings.Name))
            {
                logger.LogError("--mod-name is required (project has no name metadata).");
                return 2;
            }

            if (!Session.Project.IsLoaded)
            {
                logger.LogError("No project loaded. Run 'project load' or 'project new' first.");
                return 2;
            }

            if (DryRun.Active) { DryRun.Report(logger, "build mod to {0}", outPath); return 0; }
            bool ok = Session.Project.Build(outPath, settings, logger);
            var fields = new Dictionary<string, string>();
            fields["out"] = outPath;
            if (!ok) fields["error"] = "Build failed. See log above.";
            formatter.WriteResult(ok ? "OK" : "FAIL", "mod build", fields);
            return ok ? 0 : 1;
        }

        // Lists every asset the currently loaded project changes vs the base game.
        public static int ExecuteDiff(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string format   = "table";
            string outFile  = null;
            string basePath = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--base" && i + 1 < args.Length) basePath = args[++i];
            }

            if (!Session.Project.IsLoaded)
            {
                logger.LogError("mod diff: no project loaded. Run 'project load' or 'project new' first.");
                return 2;
            }
            if (App.AssetManager == null)
            {
                logger.LogError("mod diff: AssetManager not initialized.");
                return 2;
            }

            if (basePath != null)
            {
                // A .fbmod can be read read-only (FrostyMod, no AssetManager touch), so we diff its
                // modified resource names against the current project. A second .fbproject can't be
                // diffed without a 2nd AssetManager (single global), so that stays deferred.
                if (!File.Exists(basePath)) { logger.LogError("mod diff: --base file not found: " + basePath); return 2; }
                if (basePath.EndsWith(".fbmod", StringComparison.OrdinalIgnoreCase))
                    return ExecuteDiffAgainstMod(basePath, format, outFile, logger);
                if (basePath.EndsWith(".fbproject", StringComparison.OrdinalIgnoreCase))
                    return ExecuteDiffAgainstProject(basePath, format, outFile, logger);
                logger.LogError("mod diff: --base must be a .fbmod or .fbproject");
                return 2;
            }

            var items = new List<DiffItem>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(modifiedOnly: true))
                items.Add(new DiffItem { Name = e.Name, Kind = "ebx", Status = e.IsAdded ? "added" : "modified", Type = e.Type });
            foreach (ResAssetEntry r in App.AssetManager.EnumerateRes(modifiedOnly: true))
                items.Add(new DiffItem { Name = r.Name, Kind = "res", Status = r.IsAdded ? "added" : "modified", Type = r.Type });
            foreach (ChunkAssetEntry c in App.AssetManager.EnumerateChunks(modifiedOnly: true))
                items.Add(new DiffItem { Name = c.Name, Kind = "chunk", Status = c.IsAdded ? "added" : "modified", Type = c.Type });

            if (format == "json")
            {
                var arr = new List<object>();
                foreach (var it in items)
                    arr.Add(new { name = it.Name, kind = it.Kind, status = it.Status, type = it.Type });
                string json = JsonConvert.SerializeObject(arr, Formatting.Indented);
                if (outFile != null)
                {
                    File.WriteAllText(outFile, json);
                    Console.Out.WriteLine("wrote " + items.Count + " modified assets to " + outFile);
                }
                else Console.Out.WriteLine(json);
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine(string.Format("{0,-6} {1,-9} {2,-55} {3}", "kind", "status", "name", "type"));
                foreach (var it in items)
                    sb.AppendLine(string.Format("{0,-6} {1,-9} {2,-55} {3}", it.Kind, it.Status, it.Name, it.Type ?? ""));
                sb.AppendLine(items.Count + " modified assets total");
                string outp = sb.ToString();
                if (outFile != null)
                {
                    File.WriteAllText(outFile, outp);
                    Console.Out.WriteLine("wrote " + items.Count + " modified assets to " + outFile);
                }
                else Console.Out.Write(outp);
            }
            return 0;
        }

        // Applies an existing .fbmod into the currently loaded project. The .fbmod stores each
        // resource's data as a compressed cas blob (CompressFile output), so we decompress it and
        // reuse the AssetManager paths (none of which touch App.PluginManager): EBX modifications via
        // Reads a (possibly inherited) non-public instance field by name. Used to pull resMeta/resRid
        // off a mod's ResResource (only ResType is public).
        // Fnv1 as Frosty hashes bundle names for .fbmod bundle lists (and chunk H32).
        private static int Fnv1(string s)
        {
            uint h = 5381;
            foreach (byte c in System.Text.Encoding.UTF8.GetBytes(s)) h = (h * 33) ^ c;
            return (int)h;
        }

        // A chunk record carries the bundle-entry fields the game needs (H32 = hash of the owning res
        // name, FirstMip, logical offset/size, range); AddChunk/ModifyChunk don't know them.
        private static void CopyChunkFields(Frosty.Core.Mod.BaseModResource res, ChunkAssetEntry ce)
        {
            if (ce == null) return;
            try
            {
                if (GetPrivateField(res, "h32") is int h32) ce.H32 = h32;
                if (GetPrivateField(res, "firstMip") is int fm) ce.FirstMip = fm;
                if (GetPrivateField(res, "logicalOffset") is uint lo) ce.LogicalOffset = lo;
                if (GetPrivateField(res, "logicalSize") is uint ls && ls != 0) ce.LogicalSize = ls;
                if (GetPrivateField(res, "rangeStart") is uint rs) ce.RangeStart = rs;
                if (GetPrivateField(res, "rangeEnd") is uint re) ce.RangeEnd = re;
                if (ce.ModifiedEntry != null)
                {
                    ce.ModifiedEntry.H32 = ce.H32; ce.ModifiedEntry.FirstMip = ce.FirstMip;
                    ce.ModifiedEntry.LogicalOffset = ce.LogicalOffset; ce.ModifiedEntry.LogicalSize = ce.LogicalSize;
                    ce.ModifiedEntry.RangeStart = ce.RangeStart; ce.ModifiedEntry.RangeEnd = ce.RangeEnd;
                }
            }
            catch { }
        }

        private static object GetPrivateField(object o, string name)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        // Re-keys a just-added Res to its original RID so EBX referencing it resolve. AddRes assigns a
        // random RID; we move the entry to origRid in the AssetManager's private resRidList. No-op on a
        // RID collision (keeps the generated one).
        private static void PreserveResRid(ResAssetEntry entry, ulong origRid)
        {
            if (entry == null || entry.ResRid == origRid) return;
            var am = App.AssetManager;
            var f = am.GetType().GetField("resRidList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (!(f?.GetValue(am) is System.Collections.IDictionary dict)) return;
            if (dict.Contains(origRid)) return;
            dict.Remove(entry.ResRid);
            entry.ResRid = origRid;
            dict[origRid] = entry;
        }

        // Applies a handler-based mod resource (e.g. ShaderBlockDepot): resolves its custom action
        // handler via the headless PluginManager (Res -> by ResourceType, Ebx -> by type), runs
        // handler.Load(existing, data) to reconstruct the ModifiedResource, and attaches it to the
        // asset's ModifiedEntry.DataObject. Returns false (skip) if the entry/handler isn't available
        // (e.g. the handler's plugin isn't loaded, or an added handler resource).
        private static bool ApplyHandlerResource(Frosty.Core.Mod.BaseModResource res,
            Frosty.Core.Mod.ModResourceType type, Frosty.Core.Mod.FrostyMod mod,
            Frosty.Core.PluginManager pm, ExportLogger logger)
        {
            if (pm == null) return false;
            Frosty.Core.Mod.ICustomActionHandler handler = null;
            AssetEntry entry = null;
            if (type == Frosty.Core.Mod.ModResourceType.Res)
            {
                ResAssetEntry re = App.AssetManager.GetResEntry(res.Name);
                if (re == null) return false; // added handler res -> not yet supported
                entry = re;
                handler = pm.GetCustomHandler((ResourceType)re.ResType);
            }
            else if (type == Frosty.Core.Mod.ModResourceType.Ebx)
            {
                EbxAssetEntry ee = App.AssetManager.GetEbxEntry(res.Name);
                if (ee == null || string.IsNullOrEmpty(ee.Type)) return false;
                entry = ee;
                handler = pm.GetCustomHandler(ee.Type);
            }
            if (handler == null) return false; // handler's plugin not loaded (e.g. localization)

            try
            {
                // Handler resource data is NOT cas-compressed: FrostyModExecutor passes
                // fmod.GetResourceData(resource) straight into handler.Load (no CasReader). The handler
                // itself parses the ModifiedResource framing. (The non-handler EBX/Res/Chunk path below
                // IS cas-compressed and does use CasReader.) `existing` starts null, exactly as the
                // executor's HandlerExtraData.Data does on first load.
                byte[] blob = mod.GetResourceData(res);
                if (blob == null || blob.Length == 0) return false;
                object existing = entry.ModifiedEntry != null ? entry.ModifiedEntry.DataObject : null;
                object modified = handler.Load(existing, blob);
                if (modified == null) return false;
                if (entry.ModifiedEntry == null) entry.ModifiedEntry = new ModifiedAssetEntry();
                entry.ModifiedEntry.DataObject = modified;
                entry.ModifiedEntry.IsDirty = true;
                entry.IsDirty = true;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning("mod apply: handler for '{0}' failed: {1}", res.Name, ex.Message);
                return false;
            }
        }

        // Applies a .fbmod's EBX/Res/Chunk: modifications via ModifyEbx/ModifyRes/ModifyChunk, and
        // ADDED/new EBX (AddEbx), Res (AddRes, original ResRid preserved) and Chunk (AddChunk, id
        // preserved). Embedded/Bundle entries are ignored. Handler-based resources (e.g. ShaderBlockDepot)
        // run their custom action handler via the headless PluginManager (P1); only resources whose
        // handler plugin isn't loaded (e.g. localization) are skipped with a warning.
        // --only filter: null = everything; otherwise an exact resource name, or a prefix ending in '*'
        // (case-insensitive). Handler resources (a network registry's merge list) match by name like any other,
        // so a filtered apply leaves another mod's registry additions out unless they are named.
        internal static bool OnlyMatches(List<string> only, string name)
        {
            if (only == null) return true;
            string n = (name ?? "").ToLowerInvariant();
            foreach (string p in only)
                if (p.EndsWith("*") ? n.StartsWith(p.Substring(0, p.Length - 1)) : n == p) return true;
            return false;
        }

        public static int ExecuteApply(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string modPath = null;
            List<string> only = null;   // --only a,b,prefix*: apply just these resources (names, case-insensitive)
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--only" && i + 1 < args.Length)
                {
                    only = new List<string>();
                    foreach (string p in args[++i].Split(',')) if (p.Trim().Length > 0) only.Add(p.Trim().ToLowerInvariant());
                    continue;
                }
                if (!args[i].StartsWith("--") && modPath == null) modPath = args[i];
            }

            if (string.IsNullOrEmpty(modPath)) { logger.LogError("mod apply: missing <file.fbmod>"); return 2; }
            if (!File.Exists(modPath)) { logger.LogError("mod apply: file not found: " + modPath); return 2; }
            if (!Session.Project.IsLoaded)
            {
                logger.LogError("mod apply: no project loaded. Run 'project load' or 'project new' first.");
                return 2;
            }
            if (App.AssetManager == null) { logger.LogError("mod apply: AssetManager not initialized."); return 2; }

            Frosty.Core.Mod.FrostyMod mod;
            try { mod = new Frosty.Core.Mod.FrostyMod(modPath); }
            catch (Exception ex) { logger.LogError("mod apply: failed to read mod: " + ex.Message); return 1; }

            if (DryRun.Active)
            {
                int n = 0;
                foreach (Frosty.Core.Mod.BaseModResource r in mod.Resources)
                {
                    var t = r.Type;
                    if (t != Frosty.Core.Mod.ModResourceType.Ebx && t != Frosty.Core.Mod.ModResourceType.Res && t != Frosty.Core.Mod.ModResourceType.Chunk) continue;
                    if (!OnlyMatches(only, r.Name)) continue;
                    logger.Log("DRY-RUN: would apply " + t + " " + r.Name);
                    n++;
                }
                DryRun.Report(logger, "apply {0} resource(s) from {1}", n, Path.GetFileName(modPath));
                return 0;
            }

            // Bundles the mod ADDS (a new level's own bundles) must exist before anything can be put in them: without
            // this every membership into them was "unresolved" and a rebuilt mod shipped the level's assets in no
            // bundle at all (the level then loads forever). The record names the superbundle by Fnv1a(lower name).
            int bundlesAdded = 0;
            {
                Dictionary<int, int> sbByHash = null;
                foreach (Frosty.Core.Mod.BaseModResource res in mod.Resources)
                {
                    if (res.Type != Frosty.Core.Mod.ModResourceType.Bundle || string.IsNullOrEmpty(res.Name)) continue;
                    if (App.AssetManager.GetBundleId(res.Name) >= 0) continue;
                    if (sbByHash == null)
                    {
                        sbByHash = new Dictionary<int, int>();
                        foreach (SuperBundleEntry sb in App.AssetManager.EnumerateSuperBundles())
                            sbByHash[Frosty.Hash.Fnv1a.HashString(sb.Name.ToLowerInvariant())] = App.AssetManager.GetSuperBundleId(sb);
                    }
                    int sbHash = GetPrivateField(res, "superBundleName") is int h ? h : 0;
                    if (!sbByHash.TryGetValue(sbHash, out int sbIndex))
                    { logger.LogWarning("mod apply: bundle '{0}': superbundle hash {1:X8} not found, using superbundle 0", res.Name, sbHash); sbIndex = 0; }
                    BundleType bt = res.Name.IndexOf("/levels/", StringComparison.OrdinalIgnoreCase) >= 0 ? BundleType.SubLevel : BundleType.BlueprintBundle;
                    App.AssetManager.AddBundle(res.Name, bt, sbIndex);
                    bundlesAdded++;
                }
                if (bundlesAdded > 0) logger.Log("mod apply: {0} bundle(s) the mod adds created", bundlesAdded);
            }

            int applied = 0, skippedHandler = 0, skippedAdded = 0, skippedOther = 0, failed = 0, bundleOnly = 0;
            Frosty.Core.PluginManager headlessPm = null; // lazily built; resolves custom handlers headlessly
            foreach (Frosty.Core.Mod.BaseModResource res in mod.Resources)
            {
                var type = res.Type;
                if (type != Frosty.Core.Mod.ModResourceType.Ebx &&
                    type != Frosty.Core.Mod.ModResourceType.Res &&
                    type != Frosty.Core.Mod.ModResourceType.Chunk)
                { skippedOther++; continue; } // Embedded (icon/screenshots), Bundle, Invalid
                if (!OnlyMatches(only, res.Name)) { skippedOther++; continue; }
                if (res.ResourceIndex == -1) { bundleOnly++; continue; }   // a bundle-membership record: handled below
                if (res.HasHandler)
                {
                    // Handler-based resource (e.g. ShaderBlockDepot): run its custom action handler so the
                    // modification applies correctly, instead of skipping (P1).
                    if (headlessPm == null) headlessPm = HeadlessProject.CreateHeadlessPluginManager();
                    if (ApplyHandlerResource(res, type, mod, headlessPm, logger)) applied++;
                    else skippedHandler++;
                    continue;
                }
                try
                {
                    byte[] blob = mod.GetResourceData(res);
                    if (blob == null || blob.Length == 0)
                    { failed++; logger.LogWarning("mod apply: empty data for '{0}', skipping.", res.Name); continue; }

                    // .fbmod resource data is a compressed cas blob; null key => stream passes through.
                    byte[] raw = new CasReader(new MemoryStream(blob), null, 0u).Read();

                    switch (type)
                    {
                        case Frosty.Core.Mod.ModResourceType.Ebx:
                        {
                            EbxAsset asset = EbxReader.CreateReader(new MemoryStream(raw)).ReadAsset<EbxAsset>();
                            if (App.AssetManager.GetEbxEntry(res.Name) == null)
                                App.AssetManager.AddEbx(res.Name, asset);   // added/new ebx
                            else
                                App.AssetManager.ModifyEbx(res.Name, asset); // modify existing
                            applied++;
                            break;
                        }
                        case Frosty.Core.Mod.ModResourceType.Res:
                        {
                            ResAssetEntry re = App.AssetManager.GetResEntry(res.Name);
                            if (re == null)
                            {
                                // Added/new res: create it, preserving its ORIGINAL ResRid so EBX that
                                // reference it (added in the same mod) still resolve (AddRes assigns a
                                // random RID otherwise).
                                uint rtype = (res is Frosty.Core.Mod.ResResource rr) ? rr.ResType : 0u;
                                byte[] meta = GetPrivateField(res, "resMeta") as byte[] ?? new byte[0];
                                ResAssetEntry added = App.AssetManager.AddRes(res.Name, (ResourceType)rtype, meta, raw);
                                if (GetPrivateField(res, "resRid") is ulong origRid) PreserveResRid(added, origRid);
                                applied++;
                                break;
                            }
                            App.AssetManager.ModifyRes(re.Name, raw);
                            applied++;
                            break;
                        }
                        case Frosty.Core.Mod.ModResourceType.Chunk:
                        {
                            Guid id;
                            if (!Guid.TryParse(res.Name, out id)) { failed++; logger.LogWarning("mod apply: bad chunk id '{0}'", res.Name); break; }
                            if (App.AssetManager.GetChunkEntry(id) == null)
                            {
                                App.AssetManager.AddChunk(raw, id); // added/new chunk (id preserved)
                                CopyChunkFields(res, App.AssetManager.GetChunkEntry(id));
                                applied++;
                                break;
                            }
                            if (!App.AssetManager.ModifyChunk(id, raw)) { failed++; break; }
                            CopyChunkFields(res, App.AssetManager.GetChunkEntry(id));
                            applied++;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                { failed++; logger.LogWarning("mod apply: failed to apply '{0}': {1}", res.Name, ex.Message); }
            }

            // Bundle memberships: every record (including bundle-only ones with no payload) lists the
            // bundles the mod adds the asset to, as Fnv1 hashes of the lower-cased bundle name. Re-apply
            // them so a rebuilt mod carries the same memberships (and so a bundled chunk gets its H32).
            int bundled = 0, bundleMiss = 0;
            Dictionary<uint, int> byHash = null;
            foreach (Frosty.Core.Mod.BaseModResource res in mod.Resources)
            {
                var type = res.Type;
                if (type != Frosty.Core.Mod.ModResourceType.Ebx && type != Frosty.Core.Mod.ModResourceType.Res && type != Frosty.Core.Mod.ModResourceType.Chunk) continue;
                if (!OnlyMatches(only, res.Name)) continue;
                bool any = false; foreach (int h in res.AddedBundles) { any = true; break; }
                if (!any) continue;
                if (byHash == null)
                {
                    byHash = new Dictionary<uint, int>();
                    foreach (BundleEntry be in App.AssetManager.EnumerateBundles())
                        byHash[(uint)Fnv1(be.Name.ToLowerInvariant())] = App.AssetManager.GetBundleId(be);
                }
                AssetEntry entry = type == Frosty.Core.Mod.ModResourceType.Ebx ? (AssetEntry)App.AssetManager.GetEbxEntry(res.Name)
                                 : type == Frosty.Core.Mod.ModResourceType.Res ? (AssetEntry)App.AssetManager.GetResEntry(res.Name)
                                 : Guid.TryParse(res.Name, out Guid cid) ? App.AssetManager.GetChunkEntry(cid) : null;
                if (entry == null) { bundleMiss++; continue; }
                foreach (int h in res.AddedBundles)
                {
                    int bid;
                    if (!byHash.TryGetValue((uint)h, out bid)) { bundleMiss++; continue; }
                    if (entry.AddToBundle(bid)) bundled++;
                }
            }
            if (bundled > 0 || bundleMiss > 0) logger.Log("mod apply: bundle memberships applied {0}, unresolved {1}", bundled, bundleMiss);

            if (skippedHandler > 0)
                logger.LogWarning("mod apply: skipped {0} handler-based resource(s) whose handler plugin isn't loaded.",
                    skippedHandler);

            int skipped = skippedHandler + skippedAdded + skippedOther;
            var fields = new Dictionary<string, string>();
            fields["mod"]     = modPath;
            fields["applied"] = applied.ToString();
            fields["skipped"] = skipped.ToString();
            if (failed > 0) fields["failed"] = failed.ToString();
            bool hardFail = applied == 0 && failed > 0;
            formatter.WriteResult(hardFail ? "FAIL" : "OK", "mod apply", fields);
            return hardFail ? 1 : 0;
        }

        // ---- mod test: does this .fbmod load in the game? --------------------------------------------
        // Drives tools/gametest/autotest.ps1 through the elevated scheduled task it was registered
        // with (register-autotest.ps1): the script puts the mod alone into a Frosty Mod Manager pack,
        // has the mod manager apply + launch the game, injects FrostyGameLink, hooks the engine's own
        // first spawn to load <level>/<mode> instead of the frontend, and reports LOADED / CRASH / HANG.
        // This command only writes the argument file, fires the task and waits for the verdict.
        public static int ExecuteTest(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string modPath = null, level = "S2/Levels/CloudCity_01/CloudCity_01", mode = "SkirmishBlast", task = "FrostyAutoTest", script = null, module = null;
            int timeout = 240, wait = 0; bool keep = false, feeds = false, feedsEarly = false, vanilla = false, recordProps = false, deploy = false;
            double playSeconds = 0;
            string record = null, recordFilter = "";
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--keep") keep = true;
                else if (args[i] == "--feeds") feeds = true;
                else if (args[i] == "--deploy") deploy = true;
                else if (args[i] == "--play") playSeconds = 60;
                else if (args[i] == "--play-seconds" && i + 1 < args.Length) playSeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (args[i] == "--feeds-early") feedsEarly = true;
                else if (args[i] == "--record" && i + 1 < args.Length) record = args[++i];
                else if (args[i] == "--record-filter" && i + 1 < args.Length) recordFilter = args[++i];
                else if (args[i] == "--record-props") recordProps = true;
                else if (args[i] == "--vanilla" || args[i] == "vanilla") vanilla = true;
                else if (args[i] == "--level" && i + 1 < args.Length) level = args[++i];
                else if (args[i] == "--mode" && i + 1 < args.Length) mode = args[++i];
                else if (args[i] == "--timeout" && i + 1 < args.Length) timeout = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (args[i] == "--task" && i + 1 < args.Length) task = args[++i];
                else if (args[i] == "--script" && i + 1 < args.Length) script = args[++i];
                else if (args[i] == "--module" && i + 1 < args.Length) module = args[++i];
                else if (args[i] == "--wait" && i + 1 < args.Length) wait = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (!args[i].StartsWith("--")) modPath = args[i];
            }
            // vanilla: no mod at all - the harness empties its pack and Mod Manager launches the plain
            // game, still with gamelink injected and the level hook armed. Its verdict names "vanilla".
            if (vanilla) modPath = "vanilla";
            else
            {
                if (string.IsNullOrEmpty(modPath)) { logger.LogError("mod test: missing <file.fbmod> (or --vanilla)"); return 2; }
                modPath = Path.GetFullPath(modPath);
                if (!File.Exists(modPath)) { logger.LogError("mod test: file not found: " + modPath); return 2; }
            }
            // The argument/result files live next to the script THE TASK RUNS, so ask the task which
            // one that is before falling back to the repo copy.
            if (script == null) script = TaskScriptPath(task) ?? FindGameTestScript();
            if (script == null || !File.Exists(script))
            {
                logger.LogError("mod test: autotest.ps1 not found (pass --script <path>); it ships in tools/gametest of the repo.");
                return 2;
            }
            string dir = Path.GetDirectoryName(script);
            string argsFile = Path.Combine(dir, "autotest-args.json");
            string resultFile = Path.Combine(dir, "autotest-result.json");
            string logFile = Path.Combine(dir, "autotest.log");
            var a = new Dictionary<string, object> { { "mod", modPath }, { "level", level }, { "mode", mode }, { "timeout", timeout } };
            if (module != null) a["module"] = module;
            // --play: record from load, deploy, play, report (PlayTest). Implies --deploy and a recording.
            if (playSeconds > 0 && record == null)
                record = Path.Combine(Path.GetTempPath(), "frostycli-play-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            if (playSeconds > 0) deploy = true;
            if (deploy) keep = true;      // deploying happens after LOADED, so the game must stay up
            if (keep) a["keep"] = true;   // leave the game running after LOADED so a human can look around
            // feeds: gamelink's logic-event and property feeds on right after LOADED (never earlier - patching
            // the game's code before a level is up stalls or kills it), so a crash after load leaves the last
            // events and property writes in the gamelink log as 'feed:' lines.
            // feeds-early (implied by --record): the harness asks for both feeds right after injection and
            // gamelink arms them once the level starts loading (client level set, never at the frontend),
            // so the level's own start-up logic - game-mode streaming - is captured.
            if (record != null) feedsEarly = true;
            if (feedsEarly) a["feeds"] = "early";
            else if (feeds) a["feeds"] = true;
            File.WriteAllText(argsFile, Newtonsoft.Json.JsonConvert.SerializeObject(a, Newtonsoft.Json.Formatting.Indented));
            try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }
            if (wait <= 0) wait = timeout + 420;   // pack apply + game boot can add several minutes

            // Fire the elevated task. schtasks needs no elevation to *run* a task the user owns.
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Run /TN \"" + task + "\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            string outText;
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                outText = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    logger.LogError("mod test: could not start task '" + task + "': " + outText.Trim() +
                        " (register it once from an elevated shell: tools/gametest/register-autotest.ps1)");
                    return 1;
                }
            }
            logger.Log("mod test: task {0} started for {1} ({2} / {3}); waiting up to {4} s", task, Path.GetFileName(modPath), level, mode, wait);

            // --record: drain the feeds into a `game record`-format file while waiting for the verdict -
            // from the level's first logic until LOADED. Draining every 3 s keeps the property feed, which
            // runs hottest during load, from overflowing its buffer.
            System.IO.StreamWriter recording = null;
            var recordClock = System.Diagnostics.Stopwatch.StartNew();
            int recorded = 0;
            Action drainRecording = () =>
            {
                if (recording == null) return;
                try
                {
                    GameCommand.Send(31500, ("trace filter " + recordFilter).Trim(), 10000);
                    recorded += GameCommand.DrainFeed(31500, "trace", recording, recordClock);
                    if (recordProps)
                    {
                        GameCommand.Send(31500, ("prop filter " + recordFilter).Trim(), 10000);
                        recorded += GameCommand.DrainFeed(31500, "prop", recording, recordClock);
                    }
                    recording.Flush();
                }
                catch (System.Net.Sockets.SocketException) { }      // the game isn't up (or injected) yet
                catch (IOException) { }
            };
            if (record != null)
            {
                recording = new System.IO.StreamWriter(record, false, new System.Text.UTF8Encoding(false));
                logger.Log("mod test: recording {0} into {1}{2}", recordProps ? "events and properties" : "events", record,
                           recordFilter.Length > 0 ? " (filter: " + recordFilter + ")" : "");
            }

            DateTime deadline = DateTime.Now.AddSeconds(wait);
            while (DateTime.Now < deadline)
            {
                System.Threading.Thread.Sleep(3000);
                drainRecording();
                if (!File.Exists(resultFile)) continue;
                try
                {
                    var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(resultFile));
                    string resMod = (string)j["mod"];
                    if (!string.Equals(resMod, modPath, StringComparison.OrdinalIgnoreCase)) continue;   // stale verdict from a previous run
                    string status = (string)j["status"], detail = (string)j["detail"];
                    var fields = new Dictionary<string, string>
                    { { "mod", modPath }, { "level", level }, { "mode", mode }, { "status", status }, { "detail", detail ?? "" }, { "log", logFile } };
                    if (recording != null)
                    {
                        drainRecording();
                        recording.Dispose();
                        recording = null;
                        fields["record"] = record + " (" + recorded + " lines)";
                    }
                    formatter.WriteResult(status == "LOADED" ? "OK" : "FAIL", "mod test", fields);
                    // Loaded: how close the level is to the engine's fixed limits (only answers while the
                    // game is still up - --keep, or the harness's settle/hold window).
                    if (status == "LOADED")
                    {
                        try
                        {
                            string budget = GameCommand.Send(31500, "budget", 20000);
                            if (budget.StartsWith("unique meshes", StringComparison.Ordinal))
                                Console.Out.WriteLine(budget);
                        }
                        catch (Exception) { }
                        // --deploy: into the match by firing the spawn screen's START event inside the game
                        // (GameCommand.Deploy) - never keyboard or mouse input to the PC.
                        if (playSeconds > 0)
                        {
                            int played;
                            try { played = PlayTest.Run(31500, record, playSeconds, logger); }
                            catch (Exception ex) { logger.LogError("mod test: play test failed: " + ex.Message); played = 1; }
                            return played;
                        }
                        if (deploy)
                        {
                            int deployed;
                            try { deployed = GameCommand.Deploy(31500, new List<string> { "--timeout", "60" }, logger); }
                            catch (Exception ex) { logger.LogError("mod test: deploy failed: " + ex.Message); deployed = 1; }
                            if (deployed != 0) return 1;
                        }
                    }
                    // A crash or exit: explain it from the module's log - fault, known cause, last logic.
                    if (status != null && (status.StartsWith("CRASH", StringComparison.Ordinal) || status.StartsWith("EXIT", StringComparison.Ordinal)))
                    {
                        string known = CrashReport.FindKnownCrashes() ?? Path.Combine(dir, "known-crashes.json");
                        string report;
                        try { report = CrashReport.Build(CrashReport.DefaultLog, known); }
                        catch (Exception ex) { report = "crash report failed: " + ex.Message; }
                        Console.Out.WriteLine();
                        Console.Out.WriteLine(report);
                    }
                    return status == "LOADED" ? 0 : (status == "ERROR" ? 2 : 1);
                }
                catch { /* half-written; try again */ }
            }
            if (recording != null) { drainRecording(); recording.Dispose(); }
            logger.LogError("mod test: no verdict within " + wait + " s (see " + logFile + "; a stuck task can be ended with: schtasks /End /TN " + task + ")");
            return 1;
        }

        // The .ps1 the scheduled task actually runs: parsed out of `schtasks /Query /XML` (<Arguments>
        // holds "-File <path>"). Null when the task is missing or does not run a script file.
        private static string TaskScriptPath(string task)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Query /TN \"" + task + "\" /XML")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                string xml;
                using (var p = System.Diagnostics.Process.Start(psi)) { xml = p.StandardOutput.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0) return null; }
                var m = System.Text.RegularExpressions.Regex.Match(xml, @"-File\s+""?([^""<]+?\.ps1)""?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) return null;
                string path = m.Groups[1].Value.Trim();
                return File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        // tools/gametest/autotest.ps1, looked up from the executable's folder upwards (a source
        // checkout keeps bin/<platform>/<config> four levels below the repo root).
        private static string FindGameTestScript()
        {
            try
            {
                string d = Path.GetDirectoryName(typeof(ModBuildCommand).Assembly.Location);
                for (int i = 0; i < 6 && d != null; i++)
                {
                    string cand = Path.Combine(d, "tools", "gametest", "autotest.ps1");
                    if (File.Exists(cand)) return cand;
                    d = Path.GetDirectoryName(d);
                }
            }
            catch { }
            return null;
        }

        // Diffs a .fbmod's modified resource names against the current project's modified set.
        // Read-only: FrostyMod parses the .fbmod without touching App.AssetManager. Each asset is
        // tagged both / mod-only / project-only.
        private static int ExecuteDiffAgainstMod(string modPath, string format, string outFile, ExportLogger logger)
        {
            Frosty.Core.Mod.FrostyMod mod;
            try { mod = new Frosty.Core.Mod.FrostyMod(modPath); }
            catch (Exception ex) { logger.LogError("mod diff: failed to read mod: " + ex.Message); return 1; }

            var modSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // name -> kind
            foreach (Frosty.Core.Mod.BaseModResource res in mod.Resources)
            {
                string kind = res.Type == Frosty.Core.Mod.ModResourceType.Ebx ? "ebx"
                            : res.Type == Frosty.Core.Mod.ModResourceType.Res ? "res"
                            : res.Type == Frosty.Core.Mod.ModResourceType.Chunk ? "chunk" : null;
                if (kind == null) continue; // skip Embedded (icon/screenshots), Bundle, Invalid
                if (!string.IsNullOrEmpty(res.Name) && !modSet.ContainsKey(res.Name)) modSet[res.Name] = kind;
            }
            return RenderBaseDiff(modSet, "mod vs project: " + Path.GetFileName(modPath), format, outFile);
        }

        // Diffs a second .fbproject's modified-asset names (parsed read-only, no AssetManager touch)
        // against the current project's modified set.
        private static int ExecuteDiffAgainstProject(string projectPath, string format, string outFile, ExportLogger logger)
        {
            List<FbProjectManifestReader.Item> parsed = FbProjectManifestReader.Read(projectPath);
            if (parsed == null)
            { logger.LogError("mod diff: could not read " + Path.GetFileName(projectPath) + " (not a supported Frosty .fbproject v9-14)."); return 1; }

            var baseSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in parsed) if (!baseSet.ContainsKey(it.Name)) baseSet[it.Name] = it.Kind;
            return RenderBaseDiff(baseSet, "project vs project: " + Path.GetFileName(projectPath), format, outFile);
        }

        // Renders the diff of a base asset-name set (from a .fbmod or .fbproject) against the current
        // project's modified set: each asset tagged both / mod-only / project-only.
        private static int RenderBaseDiff(Dictionary<string, string> baseSet, string label, string format, string outFile)
        {
            var projSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(modifiedOnly: true)) projSet[e.Name] = "ebx";
            foreach (ResAssetEntry r in App.AssetManager.EnumerateRes(modifiedOnly: true)) projSet[r.Name] = "res";
            foreach (ChunkAssetEntry c in App.AssetManager.EnumerateChunks(modifiedOnly: true)) projSet[c.Name] = "chunk";

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in baseSet.Keys) names.Add(k);
            foreach (var k in projSet.Keys) names.Add(k);

            var items = new List<DiffItem>();
            foreach (var name in names)
            {
                bool inBase = baseSet.ContainsKey(name), inProj = projSet.ContainsKey(name);
                items.Add(new DiffItem
                {
                    Name   = name,
                    Kind   = inBase ? baseSet[name] : projSet[name],
                    Status = inBase && inProj ? "both" : (inBase ? "mod-only" : "project-only"),
                    Type   = null
                });
            }

            if (format == "json")
            {
                var arr = new List<object>();
                foreach (var it in items) arr.Add(new { name = it.Name, kind = it.Kind, source = it.Status });
                string json = JsonConvert.SerializeObject(arr, Formatting.Indented);
                if (outFile != null) { File.WriteAllText(outFile, json); Console.Out.WriteLine("wrote " + items.Count + " entries to " + outFile); }
                else Console.Out.WriteLine(json);
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine(string.Format("{0,-6} {1,-13} {2}", "kind", "source", "name"));
                foreach (var it in items)
                    sb.AppendLine(string.Format("{0,-6} {1,-13} {2}", it.Kind, it.Status, it.Name));
                sb.AppendLine(items.Count + " assets (" + label + ")");
                string outp = sb.ToString();
                if (outFile != null) { File.WriteAllText(outFile, outp); Console.Out.WriteLine("wrote " + items.Count + " entries to " + outFile); }
                else Console.Out.Write(outp);
            }
            return 0;
        }

        private sealed class DiffItem
        {
            public string Name;
            public string Kind;
            public string Status;
            public string Type;
        }
    }
}
