using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FrostyCLI.Core
{
    // Whether a level's spawn points stand on its navmesh, and on the piece of it the other bots walk. A bot that
    // spawns off the navmesh, or on a piece joined to nothing else (a ledge, a roof left without its stairs), stands
    // still for the whole round -- TNAV4's two stuck bots spawned on such a ledge. The polygons are split into
    // connected pieces (polygons sharing a vertex); a spawn belongs to the polygon under it that is nearest its own
    // height without being above it (a roof over a spawn is not where it stands; ground far below still is -- the bot
    // drops onto it). The main piece is the one most spawns stand on, not the largest: on an interior the largest
    // piece can be a roof (Kamino's is), and the play space is where the spawns are.
    public static class NavmeshSpawnReach
    {
        public enum Where { Main, Island, Off }

        public sealed class Mesh
        {
            public readonly List<double[]> V = new List<double[]>();
            public readonly List<int[]> F = new List<int[]>();
        }

        public sealed class Spot
        {
            public double X, Y, Z;
            public Where Class;
            public double IslandArea;   // m2, when Class is Island
            public double Distance;     // m to the nearest navmesh vertex, when Class is Off
        }

        public sealed class Report
        {
            public readonly List<Spot> Spots = new List<Spot>();
            public int Pieces;
            public double Area, MainArea;
            public int Count(Where w) { return Spots.Count(s => s.Class == w); }
        }

        // Spawns stand up to this far below the polygon they belong to (spawn transforms sit on or just over the ground).
        public const double Headroom = 1.0;

        public static Mesh ReadObj(string path)
        {
            var m = new Mesh();
            foreach (string raw in File.ReadLines(path))
            {
                string l = raw.Trim();
                if (l.StartsWith("v ", StringComparison.Ordinal))
                {
                    string[] t = l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    m.V.Add(new[] { Parse(t[1]), Parse(t[2]), Parse(t[3]) });
                }
                else if (l.StartsWith("f ", StringComparison.Ordinal))
                {
                    string[] t = l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    m.F.Add(t.Skip(1).Select(s => int.Parse(s.Split('/')[0], CultureInfo.InvariantCulture) - 1).ToArray());
                }
            }
            return m;
        }

        public static Report Classify(Mesh m, IEnumerable<double[]> spawns)
        {
            var rep = new Report();
            int n = m.F.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            Func<int, int> find = null;
            find = a => { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; };
            var byVertex = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
                foreach (int v in m.F[i])
                {
                    double[] p = m.V[v];
                    string key = string.Format(CultureInfo.InvariantCulture, "{0:0.000},{1:0.000},{2:0.000}", p[0], p[1], p[2]);
                    if (byVertex.TryGetValue(key, out int other)) parent[find(i)] = find(other);
                    else byVertex[key] = i;
                }
            var area = new Dictionary<int, double>();
            var meanY = new double[n];
            for (int i = 0; i < n; i++)
            {
                int r = find(i);
                area[r] = (area.TryGetValue(r, out double a) ? a : 0) + Area(m, m.F[i]);
                meanY[i] = m.F[i].Average(v => m.V[v][1]);
            }
            var under = new List<Tuple<double[], int>>();      // each spawn and the polygon it stands on (-1: none)
            var perPiece = new Dictionary<int, int>();
            foreach (double[] s in spawns)
            {
                int best = -1;
                double bestDy = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (meanY[i] > s[1] + Headroom || !Inside(m, m.F[i], s[0], s[2])) continue;
                    double dy = s[1] - meanY[i];
                    if (dy < bestDy) { bestDy = dy; best = i; }
                }
                under.Add(Tuple.Create(s, best));
                if (best >= 0) perPiece[find(best)] = (perPiece.TryGetValue(find(best), out int k) ? k : 0) + 1;
            }
            int main = perPiece.Count > 0
                ? perPiece.OrderByDescending(kv => kv.Value).ThenByDescending(kv => area[kv.Key]).First().Key
                : area.Count == 0 ? -1 : area.OrderByDescending(kv => kv.Value).First().Key;
            rep.Pieces = area.Count;
            rep.Area = area.Values.Sum();
            rep.MainArea = main < 0 ? 0 : area[main];

            foreach (var u in under)
            {
                double[] s = u.Item1;
                int best = u.Item2;
                var spot = new Spot { X = s[0], Y = s[1], Z = s[2] };
                if (best < 0)
                {
                    spot.Class = Where.Off;
                    spot.Distance = m.V.Count == 0 ? double.PositiveInfinity
                        : m.V.Min(p => Math.Sqrt((p[0] - s[0]) * (p[0] - s[0]) + (p[2] - s[2]) * (p[2] - s[2])));
                }
                else if (find(best) != main)
                {
                    spot.Class = Where.Island;
                    spot.IslandArea = area[find(best)];
                }
                else spot.Class = Where.Main;
                rep.Spots.Add(spot);
            }
            return rep;
        }

        public static string Describe(Report r, int maxListed = 5)
        {
            string head = string.Format(CultureInfo.InvariantCulture,
                "{0} spawn(s): {1} on the main navmesh ({2:0.0}% of {3:0} m2, {4} piece(s)), {5} on islands, {6} off it",
                r.Spots.Count, r.Count(Where.Main), r.Area > 0 ? 100 * r.MainArea / r.Area : 0, r.Area, r.Pieces,
                r.Count(Where.Island), r.Count(Where.Off));
            var bad = r.Spots.Where(s => s.Class != Where.Main).Take(maxListed).Select(s => s.Class == Where.Island
                ? string.Format(CultureInfo.InvariantCulture, "island ({0:0.#}, {1:0.#}, {2:0.#}) {3:0} m2", s.X, s.Y, s.Z, s.IslandArea)
                : string.Format(CultureInfo.InvariantCulture, "off ({0:0.#}, {1:0.#}, {2:0.#}) {3:0.#} m away", s.X, s.Y, s.Z, s.Distance)).ToList();
            return bad.Count == 0 ? head : head + ": " + string.Join("; ", bad);
        }

        private static double Area(Mesh m, int[] f)
        {
            double s = 0;
            for (int i = 0; i < f.Length; i++)
            {
                double[] a = m.V[f[i]], b = m.V[f[(i + 1) % f.Length]];
                s += a[0] * b[2] - b[0] * a[2];
            }
            return Math.Abs(s) / 2;
        }

        private static bool Inside(Mesh m, int[] f, double x, double z)
        {
            bool c = false;
            for (int i = 0, j = f.Length - 1; i < f.Length; j = i++)
            {
                double[] a = m.V[f[i]], b = m.V[f[j]];
                if ((a[2] > z) != (b[2] > z) && x < (b[0] - a[0]) * (z - a[2]) / (b[2] - a[2]) + a[0]) c = !c;
            }
            return c;
        }

        private static double Parse(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
    }
}
