using System;
using System.Collections.Generic;
using System.Globalization;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `ebx diff-shipped` -- compare one object against the shipped population of its own type.
    ///
    /// Every real defect found in the battle-royale work was found by doing this by hand:
    /// BundleHeap.AllowGrow false where all nine shipped references are true; a Flags value carried
    /// over from a duplication source instead of derived from the object's own GUID; a BundleName
    /// inherited from whatever was cloned; layer refs still pointing at the originals. Not one of
    /// them errors -- the data loads and the feature silently does nothing -- and every one is
    /// decidable from shipped data without launching the game.
    ///
    /// The population is read with getUnmodifiedData, so it is always SHIPPED data even for assets
    /// the project has edited. Comparing against the project would let an object that was broken the
    /// same way as its neighbours come back clean, which is precisely how the original faults
    /// survived review.
    /// </summary>
    public static class EbxDiffShippedCommand
    {
        private const int DefaultMaxAssets = 400;
        private const int DefaultMaxInstances = 500;
        private const int DefaultMaxFields = 40;
        private const int DefaultMaxDepth = 4;

        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null, objectSel = null, typeName = null, scan = null, format = "table";
            int maxAssets = DefaultMaxAssets, maxInstances = DefaultMaxInstances;
            int maxFields = DefaultMaxFields, maxDepth = DefaultMaxDepth;
            bool all = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--object" && i + 1 < args.Length) objectSel = args[++i];
                else if (args[i] == "--type" && i + 1 < args.Length) typeName = args[++i];
                else if (args[i] == "--scan" && i + 1 < args.Length) scan = args[++i];
                else if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (args[i] == "--json") format = "json";
                else if (args[i] == "--all") all = true;
                else if (args[i] == "--max-assets" && i + 1 < args.Length && !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out maxAssets))
                { Console.Error.WriteLine("ebx diff-shipped: --max-assets must be a non-negative integer"); return 1; }
                else if (args[i] == "--max-assets") i++;
                else if (args[i] == "--max-instances" && i + 1 < args.Length && !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out maxInstances))
                { Console.Error.WriteLine("ebx diff-shipped: --max-instances must be a non-negative integer"); return 1; }
                else if (args[i] == "--max-instances") i++;
                else if (args[i] == "--max-fields" && i + 1 < args.Length && !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out maxFields))
                { Console.Error.WriteLine("ebx diff-shipped: --max-fields must be a non-negative integer"); return 1; }
                else if (args[i] == "--max-fields") i++;
                else if (args[i] == "--max-depth" && i + 1 < args.Length && !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out maxDepth))
                { Console.Error.WriteLine("ebx diff-shipped: --max-depth must be a non-negative integer"); return 1; }
                else if (args[i] == "--max-depth") i++;
                else if (args[i].StartsWith("--"))
                { Console.Error.WriteLine("ebx diff-shipped: unknown option '" + args[i] + "'"); return 1; }
                else ebxPath = args[i];
            }

            if (ebxPath == null)   { Console.Error.WriteLine("ebx diff-shipped: missing <ebx>"); return 1; }
            if (objectSel == null) { Console.Error.WriteLine("ebx diff-shipped: --object is required"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("ebx diff-shipped: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { Console.Error.WriteLine("ebx diff-shipped: asset not found: " + ebxPath); return 1; }

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("ebx diff-shipped: load failed: " + ex.Message); return 1; }

            object ours = EbxWriteCommand.FindPartitionObject(asset, objectSel);
            if (ours == null)
            { Console.Error.WriteLine("ebx diff-shipped: --object not found in " + ebxPath + ": " + objectSel); return 1; }

            string wanted = typeName ?? ours.GetType().Name;
            Guid ourGuid = PointerRefUtil.InstanceGuid(ours);

            // Narrow the search to assets of the same ROOT type by default. Instances of a given type
            // overwhelmingly live in partitions of one shape -- SubWorldReferenceObjectData in
            // SubWorldData/LevelData ones -- so scanning every EBX to find them costs minutes to
            // reach the same verdict. `--all` and `--scan <prefix>` widen it when that matters, and
            // the report always says how the population was gathered so a bounded sample is never
            // mistaken for the whole population.
            var shipped = new List<object>();
            int assetsScanned = 0, assetsWithInstances = 0;
            foreach (EbxAssetEntry cand in Candidates(entry, scan, all))
            {
                if (assetsScanned >= maxAssets || shipped.Count >= maxInstances) break;
                assetsScanned++;

                EbxAsset candAsset;
                // getUnmodifiedData: the population must be shipped data, never our own edits.
                try { candAsset = App.AssetManager.GetEbx(cand, true); }
                catch { continue; }
                if (candAsset == null) continue;

                bool found = false;
                foreach (object o in candAsset.Objects)
                {
                    if (o == null || o.GetType().Name != wanted) continue;
                    // Never compare an object against itself. Its shipped twin carries the same
                    // instance GUID, so a modified object would otherwise be told it matches.
                    if (ourGuid != Guid.Empty && PointerRefUtil.InstanceGuid(o) == ourGuid) continue;
                    shipped.Add(o);
                    found = true;
                    if (shipped.Count >= maxInstances) break;
                }
                if (found) assetsWithInstances++;
            }

            if (shipped.Count == 0)
            {
                Console.Error.WriteLine("ebx diff-shipped: no shipped instances of " + wanted +
                    " found in " + assetsScanned + " asset(s). Widen the search with --all or --scan <path-prefix>.");
                return 1;
            }

            List<FieldFinding> findings = ShippedDiff.Compare(ours, shipped, maxDepth);

            if (format == "json") PrintJson(ebxPath, wanted, ourGuid, findings, shipped.Count, assetsScanned, assetsWithInstances, formatter);
            else PrintTable(ebxPath, wanted, ourGuid, findings, shipped.Count, assetsScanned, maxFields, formatter);
            return 0;
        }

        // Which assets to look in, in order. Same-root-type first because that is where instances of
        // a type actually live; a prefix or the whole catalogue when asked.
        private static IEnumerable<EbxAssetEntry> Candidates(EbxAssetEntry self, string scan, bool all)
        {
            if (!all && scan == null)
            {
                yield return self;   // siblings in our own partition are the most relevant population
                // Defaults only: EnumerateEbx dereferences bundleSubPath, so passing null for it
                // throws rather than meaning "no filter".
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(self.Type))
                    if (e != self) yield return e;
                yield break;
            }

            yield return self;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e == self) continue;
                if (scan != null && (e.Name == null ||
                    !e.Name.StartsWith(scan, StringComparison.OrdinalIgnoreCase))) continue;
                yield return e;
            }
        }

        private static void PrintTable(string ebx, string type, Guid guid, List<FieldFinding> findings,
                                       int population, int assetsScanned, int maxFields,
                                       OutputFormatter formatter)
        {
            formatter.WriteLine("");
            formatter.WriteLine(type + "  " + (guid == Guid.Empty ? "(not exported)" : guid.ToString()) + "  in " + ebx);
            formatter.WriteLine("compared against " + population + " shipped instance(s) from " +
                                  assetsScanned + " asset(s) scanned");
            formatter.WriteLine("");

            int shown = 0, interesting = 0;
            foreach (FieldFinding f in findings)
            {
                if (f.Verdict == ShippedDiff.Matches || f.Verdict == ShippedDiff.PerInstance) continue;
                interesting++;
                if (shown >= maxFields) continue;
                shown++;
                formatter.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-24} {1}",
                    f.Verdict.ToUpperInvariant(), f.Field));
                formatter.WriteLine("      ours:    " + f.Ours);
                formatter.WriteLine("      shipped: " + Distribution(f));
                if (f.Note != null) formatter.WriteLine("      note:    " + f.Note);
            }

            if (interesting > shown)
                formatter.WriteLine("  ... " + (interesting - shown) + " more (raise --max-fields, or use --format json)");
            if (interesting == 0)
                formatter.WriteLine("  no disagreement with the shipped population");
            formatter.WriteLine("");
            formatter.WriteLine("Result: " + findings.Count + " field(s) compared, " + interesting + " worth attention");
        }

        private static string Distribution(FieldFinding f)
        {
            if (f.Shipped == null || f.Shipped.Count == 0) return "<none>";
            // Listing values one at a time says nothing when every instance has its own; the note
            // carries the actual finding, and four sample values would only bury it.
            if (f.Shipped.Count == f.ShippedCount && f.ShippedCount > 1)
                return f.ShippedCount + " distinct values, one per instance";
            var parts = new List<string>();
            int shown = 0;
            foreach (KeyValuePair<string, int> kv in f.Shipped)
            {
                if (shown++ >= 4) { parts.Add("+" + (f.Shipped.Count - 4) + " more"); break; }
                parts.Add(kv.Value + "/" + f.ShippedCount + " " + kv.Key);
            }
            return string.Join(", ", parts.ToArray());
        }

        private static void PrintJson(string ebx, string type, Guid guid, List<FieldFinding> findings,
                                      int population, int assetsScanned, int assetsWithInstances,
                                      OutputFormatter formatter)
        {
            var rows = new List<object>();
            foreach (FieldFinding f in findings)
            {
                var dist = new List<object>();
                if (f.Shipped != null)
                    foreach (KeyValuePair<string, int> kv in f.Shipped)
                        dist.Add(new Dictionary<string, object> { { "value", kv.Key }, { "count", kv.Value } });
                rows.Add(new Dictionary<string, object>
                {
                    { "field", f.Field },
                    { "verdict", f.Verdict },
                    { "ours", f.Ours },
                    { "shipped_count", f.ShippedCount },
                    { "shipped", dist },
                    { "note", f.Note },
                });
            }

            var payload = new Dictionary<string, object>
            {
                { "ebx", ebx },
                { "type", type },
                { "instance_guid", guid == Guid.Empty ? null : guid.ToString() },
                { "population", population },
                { "assets_scanned", assetsScanned },
                { "assets_with_instances", assetsWithInstances },
                { "findings", rows },
            };
            formatter.WriteLine(JsonConvert.SerializeObject(payload, Formatting.Indented));
        }
    }
}
