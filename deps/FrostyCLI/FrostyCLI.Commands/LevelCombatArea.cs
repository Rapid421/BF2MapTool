using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyLevelTools;

namespace FrostyCLI.Commands
{
    // `level combat-area <level> [--area <rect:x0,z0,x1,z1 | x,z;x,z;...>] [--floor y] [--height h] [--field name]`
    //
    // Lists the shapes the level's game modes use as their play-area boundary (walking out warns, then kills after the
    // mode's delay), and with --area replaces each outline with a new one. A `level create --skirmish-from` level
    // inherits its host's arena polygon at the host's coordinates; --area fits it to the new layout.
    public static class LevelCombatArea
    {
        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = null, area = null, only = null;
            double? floor = null, height = null;
            var fields = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> next = () => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " needs a value");
                if (a == "--area") area = next();
                else if (a == "--floor") floor = double.Parse(next(), CultureInfo.InvariantCulture);
                else if (a == "--height") height = double.Parse(next(), CultureInfo.InvariantCulture);
                else if (a == "--field") fields.Add(next());
                else if (a == "--only") only = next();
                else if (a.StartsWith("--")) { Console.Error.WriteLine("level combat-area: unknown option " + a); return 1; }
                else level = a;
            }
            if (level == null)
            {
                Console.Error.WriteLine("usage: level combat-area <level> [--area rect:x0,z0,x1,z1 | --area x,z;x,z;...] [--floor y] [--height h (0 = no vertical limit)] [--field <link field>] [--only <partition name part>]");
                return 1;
            }
            List<double[]> poly = null;
            try { if (area != null) poly = CombatArea.ParsePolygon(area); }
            catch (Exception ex) { Console.Error.WriteLine("level combat-area: " + ex.Message); return 1; }

            int rc = Apply(App.AssetManager, level, poly, floor, height, fields.Count > 0 ? CombatArea.LinkFields.Concat(fields).ToArray() : null,
                           s => logger.Log("level combat-area: " + s), out int changed, out List<CombatArea.Link> links, only);
            if (rc != 0) return rc;
            var inv = CultureInfo.InvariantCulture;
            var result = new Dictionary<string, string> { { "level", level }, { "links", links.Count.ToString(inv) } };
            if (poly != null)
            {
                double[] b = CombatArea.Bounds(poly);
                result["area"] = string.Format(inv, "{0} points, x {1:0.#}..{2:0.#}, z {3:0.#}..{4:0.#}", poly.Count, b[0], b[1], b[2], b[3]);
                result["shapesRewritten"] = changed.ToString(inv);
            }
            formatter.WriteResult("OK", "level combat-area", result);
            return 0;
        }

        // Rewrites every combat-area shape of <level> (poly null: list only). Also reports the level's spawns that the
        // new outline leaves outside: a spawn outside the combat area puts the player straight into the countdown.
        // <only>: just the links whose partition (or shape partition) name contains it, e.g. one mode's sub-world
        public static int Apply(AssetManager am, string level, List<double[]> poly, double? floor, double? height, string[] fields,
                                Action<string> log, out int changed, out List<CombatArea.Link> links, string only = null)
        {
            changed = 0;
            links = CombatArea.Find(am, level, fields);
            if (only != null)
                links = links.Where(l => l.Partition.Name.IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0
                                      || l.ShapeAsset.Name.IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var inv = CultureInfo.InvariantCulture;
            if (links.Count == 0) { log("no combat-area link (" + string.Join(" / ", fields ?? CombatArea.LinkFields) + ") in the partitions of " + level); return poly == null ? 0 : 1; }
            var done = new HashSet<object>(RefEq.Instance);
            var modified = new Dictionary<string, EbxAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in links)
            {
                var pts = CombatArea.WorldPoints(l.Shape);
                double[] b = pts.Count > 0 ? CombatArea.Bounds(pts.Select(p => new[] { p[0], p[2] })) : new double[4];
                double y = pts.Count > 0 ? pts.Average(p => p[1]) : 0, h = CombatArea.Height(l.Shape);
                log(string.Format(inv, "{0}: {1} -> {2} {3} in {4}: {5} points, x {6:0.#}..{7:0.#}, z {8:0.#}..{9:0.#}, y {10:0.##}{11}",
                    l.Partition.Name.Substring(l.Partition.Name.LastIndexOf('/') + 1), l.Field + " of " + l.Source, l.Shape.GetType().Name, l.ShapeGuid,
                    l.ShapeAsset.Name, pts.Count, b[0], b[1], b[2], b[3], y, h > 0 ? string.Format(inv, ", height {0:0.#} (inside {1:0.#} < y <= {2:0.#})", h, y, y + h) : ", no vertical limit"));
                if (poly == null || !done.Add(l.Shape)) continue;
                double f = floor ?? y;
                CombatArea.Rewrite(l.Shape, poly, f, height);
                modified[l.ShapeAsset.Name] = l.ShapeAssetData;
                changed++;
                double nh = CombatArea.Height(l.Shape);
                log(string.Format(inv, "  rewritten: {0} points at y {1:0.##}{2}", poly.Count, f,
                    nh > 0 ? string.Format(inv, ", height {0:0.#}", nh) : ", no vertical limit"));
            }
            foreach (var kv in modified) am.ModifyEbx(kv.Key, kv.Value);
            if (poly != null)
            {
                string dir = level.Trim().Trim('/'); dir = dir.Substring(0, dir.LastIndexOf('/') + 1);
                int spawns = 0, outside = 0;
                foreach (EbxAssetEntry e in am.EnumerateEbx().Where(x => x.Name.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    EbxAsset a;
                    try { a = am.GetEbx(e); } catch { continue; }
                    foreach (object o in a.Objects.Where(o => o.GetType().Name == "AlternateSpawnEntityData"))
                    {
                        double[] t = Trans(o);
                        if (t == null) continue;
                        spawns++;
                        if (!CombatArea.Contains(poly, t[0], t[2])) outside++;
                    }
                }
                if (spawns > 0) log(string.Format(inv, "spawns: {0} of {1} stand outside the new area{2}", outside, spawns,
                    outside > 0 ? " (a spawn outside is not used by modes that check it, and a player spawned there gets the out-of-bounds countdown)" : ""));
            }
            return 0;
        }

        private static double[] Trans(object o)
        {
            try { dynamic t = o.GetType().GetProperty("Transform")?.GetValue(o); return new double[] { t.trans.x, t.trans.y, t.trans.z }; }
            catch { return null; }
        }

        private sealed class RefEq : IEqualityComparer<object>
        {
            public static readonly RefEq Instance = new RefEq();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
