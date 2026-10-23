// VENDORED from BfiiMapTools src\Bfii.Navmesh\Obj\ObjWriter.cs (sha256:E2C239AA3C9D409F).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Obj
{
    /// <summary>
    /// Exports a navigation graph back to Wavefront OBJ, one face per area.
    /// </summary>
    /// <remarks>
    /// This is the other half of the cheapest acceptance test in the navigation track: compile an
    /// OBJ into a graph, export the graph back to OBJ, and compare face for face. Worker report 08
    /// test T1. It costs nothing and it catches a whole class of writer bugs before anything
    /// reaches the game.
    ///
    /// Vertices are welded on export with the same tolerance the parser uses, so a compile-export
    /// cycle produces the same vertex set it started from rather than one duplicate per shared
    /// corner.
    /// </remarks>
    public static class ObjWriter
    {
        public static void WriteFile(string path, NavGraph graph, float weldTolerance = 1e-4f)
        {
            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                Write(writer, graph, weldTolerance);
            }
        }

        public static string WriteText(NavGraph graph, float weldTolerance = 1e-4f)
        {
            StringWriter writer = new StringWriter(CultureInfo.InvariantCulture);
            Write(writer, graph, weldTolerance);
            return writer.ToString();
        }

        public static void Write(TextWriter writer, NavGraph graph, float weldTolerance = 1e-4f)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            if (graph == null)
            {
                throw new ArgumentNullException(nameof(graph));
            }

            List<Vec3> vertices = new List<Vec3>();
            Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
            List<int[]> faces = new List<int[]>(graph.Areas.Count);

            for (int i = 0; i < graph.Areas.Count; i++)
            {
                NavArea area = graph.Areas[i];
                int[] face = new int[area.Edges.Count];
                for (int j = 0; j < area.Edges.Count; j++)
                {
                    face[j] = Weld(area.Edges[j].Position, vertices, cells, weldTolerance);
                }

                faces.Add(face);
            }

            writer.WriteLine("# exported by Bfii.Navmesh");
            writer.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "# graph: version={0} layer={1} areas={2} buildScale={3:R} voxSize={4:R} radius={5:R} step={6:R} height={7:R}",
                graph.Version,
                graph.Layer,
                graph.Areas.Count,
                graph.BuildScale,
                graph.VoxelSize,
                graph.AgentRadius,
                graph.StepHeight,
                graph.AgentHeight));

            for (int i = 0; i < vertices.Count; i++)
            {
                Vec3 v = vertices[i];
                writer.WriteLine(string.Format(
                    CultureInfo.InvariantCulture, "v {0:R} {1:R} {2:R}", v.X, v.Y, v.Z));
            }

            for (int i = 0; i < faces.Count; i++)
            {
                int[] face = faces[i];
                StringBuilder sb = new StringBuilder("f");
                for (int j = 0; j < face.Length; j++)
                {
                    sb.Append(' ');
                    sb.Append((face[j] + 1).ToString(CultureInfo.InvariantCulture));
                }

                writer.WriteLine(sb.ToString());
            }
        }

        private static int Weld(
            Vec3 position,
            List<Vec3> vertices,
            Dictionary<long, List<int>> cells,
            float tolerance)
        {
            long cx = (long)Math.Floor(position.X / tolerance);
            long cy = (long)Math.Floor(position.Y / tolerance);
            long cz = (long)Math.Floor(position.Z / tolerance);

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        List<int>? bucket;
                        if (!cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out bucket))
                        {
                            continue;
                        }

                        for (int i = 0; i < bucket.Count; i++)
                        {
                            Vec3 candidate = vertices[bucket[i]];
                            if (Math.Abs(candidate.X - position.X) < tolerance &&
                                Math.Abs(candidate.Y - position.Y) < tolerance &&
                                Math.Abs(candidate.Z - position.Z) < tolerance)
                            {
                                return bucket[i];
                            }
                        }
                    }
                }
            }

            int index = vertices.Count;
            vertices.Add(position);

            long key = Key(cx, cy, cz);
            List<int>? target;
            if (!cells.TryGetValue(key, out target))
            {
                target = new List<int>();
                cells.Add(key, target);
            }

            target.Add(index);
            return index;
        }

        private static long Key(long x, long y, long z)
        {
            unchecked
            {
                long h = x * 73856093L;
                h ^= y * 19349663L;
                h ^= z * 83492791L;
                return h;
            }
        }
    }
}
