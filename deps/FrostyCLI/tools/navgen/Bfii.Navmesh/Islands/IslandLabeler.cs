// VENDORED from BfiiMapTools src\Bfii.Navmesh\Islands\IslandLabeler.cs (sha256:2AE6D1B76463D0D8).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.Navmesh.Format;

namespace Bfii.Navmesh.Islands
{
    /// <summary>
    /// Assigns each area a connected-component id over the area adjacency graph.
    /// </summary>
    /// <remarks>
    /// THIS IS THE FIX FOR MASTER PLAN RISK R-06 AND WORKER REPORT 08 SECTION 8.2.
    ///
    /// The only existing importer stamps every area with 262143, the saturated 18-bit value, in
    /// place of computed connectivity. Two outcomes are possible at runtime and they cannot be
    /// distinguished offline: either the engine recomputes island membership when the graph is
    /// registered, in which case the sentinel is harmless, or it trusts the stored value, in which
    /// case every area claims membership of one island and reachability queries between genuinely
    /// disconnected regions answer yes. The experiment that separates them is specified in
    /// evidence\navmesh\TWO-ISLAND-TEST.md and needs one in-game run.
    ///
    /// This labeller is correct under either outcome: if the runtime recomputes, correct stored
    /// ids are ignored and cost nothing; if it trusts, correct stored ids are the fix.
    ///
    /// Field facts, both COMMUNITY_TESTED (FORMAT-PROVENANCE.md rows A2 and A3):
    ///   width  18 bits, at bit 7 of the area flags1 word, so the storable range is 0..262143;
    ///   the community importer writes 262143, the all-ones value in that width.
    ///
    /// UNKNOWN, and deliberately not guessed: whether 0 is a valid island id or is reserved. No
    /// evidence in this workspace settles it, and the middleware documentation that would is
    /// licensed and was not consulted. The default therefore starts numbering at 1, which is
    /// correct under both readings, at a cost of one wasted id out of 262143. Callers who later
    /// establish that 0 is ordinary can set <see cref="IslandLabelOptions.FirstIslandId"/> to 0.
    /// The decisive offline check is to run the CLI inspect command over a shipped DICE blob and
    /// look at the reported island histogram: an id of 0 in shipped data proves 0 is valid.
    /// </remarks>
    public static class IslandLabeler
    {
        /// <summary>
        /// Labels every area in the graph. Adjacency comes from resolved edge neighbours, and is
        /// treated as undirected: two areas are in the same island when either names the other.
        /// </summary>
        public static IslandLabelResult Label(NavGraph graph, IslandLabelOptions? options = null)
        {
            if (graph == null)
            {
                throw new ArgumentNullException(nameof(graph));
            }

            options = options ?? IslandLabelOptions.Default;

            List<NavArea> areas = graph.Areas;
            int count = areas.Count;

            Dictionary<NavArea, int> indexOf = new Dictionary<NavArea, int>(count);
            for (int i = 0; i < count; i++)
            {
                indexOf[areas[i]] = i;
            }

            List<int>[] adjacency = BuildUndirectedAdjacency(areas, indexOf, out int asymmetricLinks);

            int[] label = new int[count];
            for (int i = 0; i < count; i++)
            {
                label[i] = -1;
            }

            int nextId = options.FirstIslandId;
            int islandCount = 0;
            List<int> sizes = new List<int>();
            Stack<int> stack = new Stack<int>();

            for (int seed = 0; seed < count; seed++)
            {
                if (label[seed] != -1)
                {
                    continue;
                }

                if (nextId > NavArea.MaxIslandNum)
                {
                    throw new IslandOverflowException(
                        "the mesh has more connected components than the 18-bit island field can " +
                        "hold: reached id " + nextId + " with a maximum of " + NavArea.MaxIslandNum +
                        ". Merge or remove disconnected fragments before compiling.");
                }

                int id = nextId++;
                int size = 0;

                // Iterative flood fill. Recursion would stack-overflow on a large mesh, and the
                // shipped blobs run to several megabytes.
                stack.Push(seed);
                label[seed] = id;

                while (stack.Count > 0)
                {
                    int current = stack.Pop();
                    size++;

                    List<int> neighbours = adjacency[current];
                    for (int k = 0; k < neighbours.Count; k++)
                    {
                        int next = neighbours[k];
                        if (label[next] == -1)
                        {
                            label[next] = id;
                            stack.Push(next);
                        }
                    }
                }

                sizes.Add(size);
                islandCount++;
            }

            for (int i = 0; i < count; i++)
            {
                areas[i].IslandNum = label[i];
            }

            return new IslandLabelResult(islandCount, options.FirstIslandId, sizes, asymmetricLinks);
        }

        /// <summary>Labels every graph in the set, numbering each graph independently.</summary>
        /// <remarks>
        /// Independent numbering is the conservative choice: a graph is a separate agent type with
        /// its own planner state, so sharing an id space across graphs would assert a relationship
        /// this workspace has no evidence for.
        /// </remarks>
        public static IslandLabelResult[] LabelAll(NavSet set, IslandLabelOptions? options = null)
        {
            if (set == null)
            {
                throw new ArgumentNullException(nameof(set));
            }

            IslandLabelResult[] results = new IslandLabelResult[set.Graphs.Count];
            for (int i = 0; i < set.Graphs.Count; i++)
            {
                results[i] = Label(set.Graphs[i], options);
            }

            return results;
        }

        /// <summary>
        /// Reports the island ids currently stored in a graph without changing them.
        /// </summary>
        /// <remarks>
        /// Run this over a shipped DICE blob and the histogram answers two open questions at once:
        /// whether shipped data ever uses id 0, and whether shipped data ever uses the sentinel.
        /// </remarks>
        public static IslandHistogram Describe(NavGraph graph)
        {
            if (graph == null)
            {
                throw new ArgumentNullException(nameof(graph));
            }

            Dictionary<int, int> counts = new Dictionary<int, int>();
            for (int i = 0; i < graph.Areas.Count; i++)
            {
                int id = graph.Areas[i].IslandNum;
                counts.TryGetValue(id, out int n);
                counts[id] = n + 1;
            }

            return new IslandHistogram(counts, graph.Areas.Count);
        }

        /// <summary>
        /// Checks stored island ids against the connectivity actually present in the graph.
        /// </summary>
        /// <remarks>
        /// Two independent failures are separated because they have different causes. Areas that
        /// are connected but carry different ids means the labelling is stale. Areas that carry the
        /// same id but are not connected means the labelling is over-merged, which is exactly what
        /// the sentinel produces and exactly what makes a bot walk into a gap.
        /// </remarks>
        public static IslandVerification Verify(NavGraph graph)
        {
            if (graph == null)
            {
                throw new ArgumentNullException(nameof(graph));
            }

            int count = graph.Areas.Count;
            int[] stored = new int[count];
            for (int i = 0; i < count; i++)
            {
                stored[i] = graph.Areas[i].IslandNum;
            }

            Dictionary<NavArea, int> indexOf = new Dictionary<NavArea, int>(count);
            for (int i = 0; i < count; i++)
            {
                indexOf[graph.Areas[i]] = i;
            }

            List<int>[] adjacency = BuildUndirectedAdjacency(graph.Areas, indexOf, out int asymmetricLinks);

            int[] component = new int[count];
            for (int i = 0; i < count; i++)
            {
                component[i] = -1;
            }

            int components = 0;
            Stack<int> stack = new Stack<int>();
            for (int seed = 0; seed < count; seed++)
            {
                if (component[seed] != -1)
                {
                    continue;
                }

                int id = components++;
                stack.Push(seed);
                component[seed] = id;
                while (stack.Count > 0)
                {
                    int current = stack.Pop();
                    List<int> neighbours = adjacency[current];
                    for (int k = 0; k < neighbours.Count; k++)
                    {
                        int next = neighbours[k];
                        if (component[next] == -1)
                        {
                            component[next] = id;
                            stack.Push(next);
                        }
                    }
                }
            }

            int splitComponents = 0;
            int mergedIds = 0;
            int sentinelAreas = 0;

            Dictionary<int, int> idToComponent = new Dictionary<int, int>();
            Dictionary<int, int> componentToId = new Dictionary<int, int>();

            for (int i = 0; i < count; i++)
            {
                if (stored[i] == NavArea.SentinelIslandNum)
                {
                    sentinelAreas++;
                }

                if (componentToId.TryGetValue(component[i], out int seenId))
                {
                    if (seenId != stored[i])
                    {
                        splitComponents++;
                        componentToId[component[i]] = int.MinValue;
                    }
                }
                else
                {
                    componentToId[component[i]] = stored[i];
                }

                if (idToComponent.TryGetValue(stored[i], out int seenComponent))
                {
                    if (seenComponent != component[i])
                    {
                        mergedIds++;
                        idToComponent[stored[i]] = int.MinValue;
                    }
                }
                else
                {
                    idToComponent[stored[i]] = component[i];
                }
            }

            return new IslandVerification(
                components, splitComponents, mergedIds, sentinelAreas, count, asymmetricLinks);
        }

        private static List<int>[] BuildUndirectedAdjacency(
            List<NavArea> areas,
            Dictionary<NavArea, int> indexOf,
            out int asymmetricLinks)
        {
            int count = areas.Count;
            List<int>[] adjacency = new List<int>[count];
            for (int i = 0; i < count; i++)
            {
                adjacency[i] = new List<int>();
            }

            HashSet<long> seen = new HashSet<long>();
            asymmetricLinks = 0;

            for (int i = 0; i < count; i++)
            {
                List<NavEdge> edges = areas[i].Edges;
                for (int j = 0; j < edges.Count; j++)
                {
                    NavArea? neighbour = edges[j].AdjacentArea;
                    if (neighbour == null)
                    {
                        continue;
                    }

                    if (!indexOf.TryGetValue(neighbour, out int other))
                    {
                        // A neighbour outside this graph cannot participate in this graph's
                        // components; counted so it is visible rather than silently dropped.
                        asymmetricLinks++;
                        continue;
                    }

                    if (other == i)
                    {
                        continue;
                    }

                    long key = ((long)Math.Min(i, other) << 32) | (uint)Math.Max(i, other);
                    if (seen.Add(key))
                    {
                        adjacency[i].Add(other);
                        adjacency[other].Add(i);
                    }
                }
            }

            return adjacency;
        }
    }

    /// <summary>Raised when a mesh has more components than the 18-bit island field can hold.</summary>
    public sealed class IslandOverflowException : Exception
    {
        public IslandOverflowException(string message)
            : base(message)
        {
        }
    }
}
