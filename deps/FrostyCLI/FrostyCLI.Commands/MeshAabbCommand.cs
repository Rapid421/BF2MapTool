using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `mesh aabb &lt;mesh&gt;... [--from-file f] [--out f.json]`: each mesh's MeshSet bounding box (object space), as JSON
    /// {mesh: [minx, miny, minz, maxx, maxy, maxz]}. Turns placement origins into extents -- a long cable's origin can sit
    /// 20 m from the part a play-tester points at (hoth-port scripts/where.py).
    /// </summary>
    public static class MeshAabbCommand
    {
        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string outFile = null;
            var meshes = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--from-file" && i + 1 < args.Length)
                {
                    foreach (string l in File.ReadAllLines(args[++i])) if (l.Trim().Length > 0) meshes.Add(l.Trim());
                }
                else if (!args[i].StartsWith("--")) meshes.Add(args[i]);
            }
            if (meshes.Count == 0) { Console.Error.WriteLine("Usage: frostycli mesh aabb <mesh>... [--from-file f] [--out f.json]"); return 1; }
            var sb = new StringBuilder("{");
            int ok = 0, bad = 0;
            foreach (string m in meshes)
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(m);
                if (e == null) { bad++; continue; }
                try
                {
                    ulong rid = (ulong)((dynamic)App.AssetManager.GetEbx(e).RootObject).MeshSetResource;
                    MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                    var b = ms.BoundingBox;
                    if (ok > 0) sb.Append(',');
                    sb.Append('\n').Append('"').Append(m.Replace("\"", "")).Append("\": [")
                      .Append(string.Join(",", new[] { b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z }.Select(f => f.ToString("R", CultureInfo.InvariantCulture))))
                      .Append(']');
                    ok++;
                }
                catch { bad++; }
            }
            sb.Append("\n}");
            if (outFile != null) File.WriteAllText(outFile, sb.ToString()); else Console.WriteLine(sb.ToString());
            formatter.WriteResult("OK", "mesh aabb", new Dictionary<string, string> { { "meshes", ok.ToString() }, { "unreadable", bad.ToString() } });
            return 0;
        }
    }
}
