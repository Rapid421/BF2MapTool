using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostyCLI.Core;
using FrostyCLI.Core.Enlighten;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `enlighten interior-volumes &lt;level&gt;`: give Dynamic (probe-lit) objects inside a ported SWBF 2015 interior the
    /// interior's own probes. 2015 bakes carry interior probes only as scattered (staticProbeSet) sets, which BFII's dynamic
    /// lookup skips, so ported cables, crates and containers inside the Hoth base were lit by the outdoor terrain probes
    /// (~100x, "almost emissive"). Each scattered set whose probes are far darker than what the dynamic lookup returns at
    /// the same points gets a regular probe volume over its box (DICE's interior-volume form: grid, priority above the
    /// terrain's 0, blend), resampled from its own probes. Idempotent: volumes carry a GUID derived from their source set.
    /// See FrostyCLI.Core.Enlighten.ProbeVolumes; memory bfii-probe-lit-interiors.
    /// </summary>
    public static class EnlightenInteriorVolumes
    {
        public const string Usage = "enlighten interior-volumes <level> [--bakes <static-name>,...] [--max-ratio 0.25] [--spacing 3] [--spacing-y 2] [--priority 101] [--blend 1] [--dry-run] [--out report.json]";

        public static int Execute(string[] args, ExportLogger logger)
        {
            string level = null, onlyBakes = null, outFile = null;
            double maxRatio = 0.25, spacing = 3, spacingY = 2; uint priority = 101; float blend = 1; bool dry = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--bakes" && i + 1 < args.Length) onlyBakes = args[++i];
                else if (a == "--max-ratio" && i + 1 < args.Length) maxRatio = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--spacing" && i + 1 < args.Length) spacing = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--spacing-y" && i + 1 < args.Length) spacingY = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--priority" && i + 1 < args.Length) priority = uint.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--blend" && i + 1 < args.Length) blend = float.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (a == "--dry-run") dry = true;
                else if (!a.StartsWith("--") && level == null) level = a;
                else { Console.Error.WriteLine("enlighten interior-volumes: unknown argument " + a + "\n  " + Usage); return 1; }
            }
            dry |= DryRun.Active;     // the global --dry-run reaches here as the ambient flag, not as an argument
            if (level == null || spacing <= 0 || spacingY <= 0) { Console.Error.WriteLine("Usage: frostycli " + Usage); return 1; }

            List<EnlightenRelight.Bake> bakes = EnlightenRelight.FindBakes(level, logger);
            if (onlyBakes != null)
            {
                var keep = new HashSet<string>(onlyBakes.Split(','), StringComparer.OrdinalIgnoreCase);
                bakes = bakes.Where(b => keep.Contains(Path.GetFileName(b.Static))).ToList();
            }
            if (bakes.Count == 0) { Console.Error.WriteLine("enlighten interior-volumes: no Enlighten bakes found under " + level); return 1; }

            var report = new JArray();
            int totalAdded = 0;
            foreach (var b in bakes)
            {
                if (!EnlightenRelight.LoadBake(b)) return 1;
                if (b.Fls == null || b.Db == null) { Console.Error.WriteLine("enlighten interior-volumes: " + b.Static + " has no modelled databases"); return 1; }
                List<ProbeVolumes.Candidate> plan = ProbeVolumes.Plan(b.Db, b.Fls.Probes, maxRatio, spacing, spacingY, priority, blend);
                var br = new JObject { ["static"] = b.Static, ["dynamic"] = b.Dynamic };
                var sets = new JArray();
                foreach (var c in plan)
                {
                    var ps = b.Db.ProbeSets[c.Set];
                    sets.Add(new JObject
                    {
                        ["set"] = c.Set, ["probes"] = c.Probes,
                        ["box"] = new JArray(ps.MinX, ps.MinY, ps.MinZ, ps.MaxX, ps.MaxY, ps.MaxZ),
                        ["ownDc"] = Math.Round(c.OwnDc, 2), ["lookupDc"] = Math.Round(c.LookupDc, 2), ["ratio"] = Math.Round(c.Ratio, 4),
                        ["interior"] = c.Interior, ["alreadyPresent"] = c.AlreadyPresent,
                        ["volume"] = c.Volume == null ? null : new JObject
                        {
                            ["guid"] = c.VolumeGuid.ToString("D"),
                            ["dim"] = new JArray(c.Volume.DimX, c.Volume.DimY, c.Volume.DimZ),
                            ["probes"] = c.Volume.Count,
                        },
                    });
                }
                int interior = plan.Count(c => c.Interior), present = plan.Count(c => c.AlreadyPresent);
                int before = b.Db.ProbeSets.Count, probesBefore = b.Fls.ProbeCount;
                int added = ProbeVolumes.Apply(b.Db, b.Fls, plan);
                Console.Out.WriteLine(string.Format("bake {0}: {1} scattered set(s), {2} interior (ratio < {3}), {4} volume(s) added ({5} already present); probe sets {6} -> {7}, probes {8} -> {9}",
                    Path.GetFileName(b.Static), plan.Count, interior, maxRatio.ToString(CultureInfo.InvariantCulture), added, present,
                    before, b.Db.ProbeSets.Count, probesBefore, b.Fls.ProbeCount));
                foreach (var c in plan.Where(c => c.Interior))
                    Console.Out.WriteLine(string.Format("  set {0,3}: {1,3} probes, own {2,9:0.0} vs dynamic lookup {3,9:0.0} (x{4:0.000}){5}",
                        c.Set, c.Probes, c.OwnDc, c.LookupDc, c.Ratio,
                        c.Volume != null ? string.Format(" -> volume {0}x{1}x{2}", c.Volume.DimX, c.Volume.DimY, c.Volume.DimZ) : (c.AlreadyPresent ? " (volume present)" : "")));
                br["added"] = added; br["sets"] = sets;
                report.Add(br);
                totalAdded += added;
                if (dry) { DryRun.Report(logger, "write {0} probe volume(s) into {1}", added, b.Static); continue; }
                if (added == 0) continue;

                byte[] endMeta, endData = b.Db.Write(out endMeta);
                byte[] flsMeta, flsData = b.Fls.Write(out flsMeta);
                var back = EnlightenDatabase.Parse(endData, endMeta);
                var backFls = StaticEnlightenDatabase.Parse(flsData, flsMeta);
                if (back.ProbeSets.Count != b.Db.ProbeSets.Count || backFls.ProbeCount != b.Fls.ProbeCount
                    || back.ProbeSets.Sum(p => (long)p.Count) != backFls.ProbeCount)
                { Console.Error.WriteLine("enlighten interior-volumes: the written databases do not parse back consistently"); return 1; }
                try
                {
                    App.AssetManager.ModifyRes(b.DbRes.Name, endData, endMeta);
                    App.AssetManager.ModifyRes(b.FlsRes.Name, flsData, flsMeta);
                }
                catch (Exception ex) { Console.Error.WriteLine("enlighten interior-volumes: writing the databases of " + b.Static + ": " + ex.Message); return 1; }
            }
            if (outFile != null) File.WriteAllText(outFile, report.ToString());
            Console.Out.WriteLine(dry ? "dry run: nothing written" : (totalAdded + " volume(s) written; save the project (--save/--save-as) to keep them"));
            return 0;
        }
    }
}
