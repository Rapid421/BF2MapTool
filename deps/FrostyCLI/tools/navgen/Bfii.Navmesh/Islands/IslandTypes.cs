// VENDORED from BfiiMapTools src\Bfii.Navmesh\Islands\IslandTypes.cs (sha256:6E35810F1CB3F731).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Bfii.Navmesh.Format;

namespace Bfii.Navmesh.Islands
{
    /// <summary>Settings for connected-component labelling.</summary>
    public sealed class IslandLabelOptions
    {
        /// <summary>
        /// First id handed out. Defaults to 1 because whether 0 is a valid island id is UNKNOWN in
        /// this workspace; see the remarks on <see cref="IslandLabeler"/> for the check that
        /// settles it and for why 1 is safe under both readings.
        /// </summary>
        public int FirstIslandId { get; set; } = 1;

        public static IslandLabelOptions Default => new IslandLabelOptions();

        /// <summary>Numbering from 0, for use once shipped data has proven 0 is ordinary.</summary>
        public static IslandLabelOptions ZeroBased => new IslandLabelOptions { FirstIslandId = 0 };
    }

    /// <summary>Outcome of labelling one graph.</summary>
    public sealed class IslandLabelResult
    {
        public IslandLabelResult(int islandCount, int firstIslandId, IReadOnlyList<int> areaCounts, int asymmetricLinks)
        {
            IslandCount = islandCount;
            FirstIslandId = firstIslandId;
            AreaCountsByIsland = areaCounts;
            AsymmetricLinks = asymmetricLinks;
        }

        public int IslandCount { get; }

        public int FirstIslandId { get; }

        /// <summary>Area count per island, indexed by island ordinal, not by island id.</summary>
        public IReadOnlyList<int> AreaCountsByIsland { get; }

        /// <summary>Adjacency references that pointed outside the graph being labelled.</summary>
        public int AsymmetricLinks { get; }

        public int LargestIslandAreaCount
        {
            get
            {
                int max = 0;
                for (int i = 0; i < AreaCountsByIsland.Count; i++)
                {
                    if (AreaCountsByIsland[i] > max)
                    {
                        max = AreaCountsByIsland[i];
                    }
                }

                return max;
            }
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format(
                CultureInfo.InvariantCulture,
                "{0} island(s), ids {1}..{2}",
                IslandCount,
                FirstIslandId,
                FirstIslandId + Math.Max(0, IslandCount - 1)));

            for (int i = 0; i < AreaCountsByIsland.Count && i < 16; i++)
            {
                sb.Append(string.Format(
                    CultureInfo.InvariantCulture,
                    "; id {0}={1} areas",
                    FirstIslandId + i,
                    AreaCountsByIsland[i]));
            }

            if (AreaCountsByIsland.Count > 16)
            {
                sb.Append("; ...");
            }

            if (AsymmetricLinks > 0)
            {
                sb.Append(string.Format(
                    CultureInfo.InvariantCulture, "; {0} out-of-graph adjacency link(s)", AsymmetricLinks));
            }

            return sb.ToString();
        }
    }

    /// <summary>Distribution of the island ids currently stored in a graph.</summary>
    public sealed class IslandHistogram
    {
        public IslandHistogram(IReadOnlyDictionary<int, int> countsById, int areaCount)
        {
            CountsById = countsById;
            AreaCount = areaCount;
        }

        public IReadOnlyDictionary<int, int> CountsById { get; }

        public int AreaCount { get; }

        public int DistinctIds => CountsById.Count;

        /// <summary>Areas carrying the saturated sentinel instead of a real component id.</summary>
        public int SentinelAreas
        {
            get
            {
                CountsById.TryGetValue(NavArea.SentinelIslandNum, out int n);
                return n;
            }
        }

        /// <summary>
        /// True when this graph uses id 0. Observing this in shipped DICE data is what proves 0 is
        /// a valid island id rather than a reserved one.
        /// </summary>
        public bool UsesIdZero => CountsById.ContainsKey(0);

        public override string ToString()
        {
            List<int> ids = new List<int>(CountsById.Keys);
            ids.Sort();

            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format(
                CultureInfo.InvariantCulture,
                "{0} area(s), {1} distinct island id(s)",
                AreaCount,
                DistinctIds));

            int shown = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                if (shown++ >= 24)
                {
                    sb.Append("; ...");
                    break;
                }

                int id = ids[i];
                string label = id == NavArea.SentinelIslandNum ? " (SENTINEL)" : string.Empty;
                sb.Append(string.Format(
                    CultureInfo.InvariantCulture, "; {0}{1}={2}", id, label, CountsById[id]));
            }

            return sb.ToString();
        }
    }

    /// <summary>Agreement between stored island ids and actual graph connectivity.</summary>
    public sealed class IslandVerification
    {
        public IslandVerification(
            int actualComponents,
            int splitComponents,
            int mergedIds,
            int sentinelAreas,
            int areaCount,
            int asymmetricLinks)
        {
            ActualComponents = actualComponents;
            SplitComponents = splitComponents;
            MergedIds = mergedIds;
            SentinelAreas = sentinelAreas;
            AreaCount = areaCount;
            AsymmetricLinks = asymmetricLinks;
        }

        /// <summary>Connected components actually present in the adjacency graph.</summary>
        public int ActualComponents { get; }

        /// <summary>Components whose areas do not all share one stored id.</summary>
        public int SplitComponents { get; }

        /// <summary>
        /// Stored ids shared by areas in more than one component. This is the failure the sentinel
        /// produces, and the one that makes a planner accept an unreachable goal.
        /// </summary>
        public int MergedIds { get; }

        public int SentinelAreas { get; }

        public int AreaCount { get; }

        public int AsymmetricLinks { get; }

        public bool IsConsistent => SplitComponents == 0 && MergedIds == 0;

        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture,
            "{0} area(s), {1} actual component(s); splitComponents={2} mergedIds={3} sentinelAreas={4} -> {5}",
            AreaCount,
            ActualComponents,
            SplitComponents,
            MergedIds,
            SentinelAreas,
            IsConsistent ? "CONSISTENT" : "INCONSISTENT");
    }
}
