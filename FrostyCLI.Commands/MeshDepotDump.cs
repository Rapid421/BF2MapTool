using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `mesh depot-dump &lt;depot res&gt;... [--out f]`: the block tree of a ShaderBlockDepot -- per resource its index, type, hash,
    /// children (by index), and the payload that matters for variations: a MeshParamDbBlock's mesh guid, LOD and parameter
    /// count, a persistent block's parameters (name hash, type hash, value bytes), a ShaderBlockMeshVariationEntry's RVM shader
    /// references. Base depots ("&lt;mesh&gt;_mesh/blocks") and variation depots ("&lt;variation&gt;/&lt;mesh file&gt;_&lt;hash&gt;/shaderblocks_variation/blocks")
    /// alike, so the two can be compared block by block.
    /// </summary>
    public static class MeshDepotDump
    {
        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string outFile = null;
            var names = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (!args[i].StartsWith("--")) names.Add(args[i]);
            }
            if (names.Count == 0) { Console.Error.WriteLine("Usage: frostycli mesh depot-dump <depot res>... [--out <file>]"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("mesh depot-dump: AssetManager not initialized."); return 1; }
            var sb = new StringBuilder();
            foreach (string n in names)
            {
                ResAssetEntry re = App.AssetManager.GetResEntry(n.ToLowerInvariant());
                if (re == null) { Console.Error.WriteLine("mesh depot-dump: res not found: " + n); return 1; }
                byte[] meta = (byte[])re.ResMeta.Clone();
                try
                {
                    ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(re);
                    sb.AppendLine("== " + re.Name + "  resources=" + depot.ResourceCount + " lods=" + depot.Count);
                    var index = new Dictionary<ShaderBlockResource, int>();
                    for (int i = 0; i < depot.ResourceCount; i++) index[depot.GetResource(i)] = i;
                    for (int i = 0; i < depot.ResourceCount; i++) sb.AppendLine(Describe(depot.GetResource(i), i, index));
                }
                finally { Array.Copy(meta, re.ResMeta, Math.Min(meta.Length, re.ResMeta.Length)); }
            }
            if (outFile != null) { File.WriteAllText(outFile, sb.ToString()); formatter.WriteResult("OK", "mesh depot-dump", new Dictionary<string, string> { { "out", outFile } }); }
            else Console.Write(sb.ToString());
            return 0;
        }

        static string Describe(ShaderBlockResource r, int i, Dictionary<ShaderBlockResource, int> index)
        {
            var sb = new StringBuilder();
            sb.Append(string.Format("[{0}] {1} {2:X16}", i, r.GetType().Name, r.Hash));
            if (r is ShaderStaticParamDbBlock st)
                sb.Append(" children=[" + string.Join(",", st.Resources.Select(c => c == null ? "null" : (index.TryGetValue(c, out int k) ? k.ToString() : "?" + c.GetType().Name))) + "]");
            if (r is MeshParamDbBlock mp)
                sb.Append(" mesh=" + mp.MeshAssetGuid + " lod=" + mp.LodIndex + " params=" + mp.Parameters.Count + Params(mp.Parameters));
            else if (r is ShaderPersistentParamDbBlock pp)
                sb.Append(" params=" + pp.Parameters.Count + Params(pp.Parameters));
            if (r is ShaderBlockMeshVariationEntry mv)
                sb.Append(" rvmGuids=[" + string.Join(",", mv.RvmShaderRefGuids) + "] rvmInts=[" + string.Join(",", mv.RvmShaderRefInts) + "]");
            return sb.ToString();
        }

        static string Params(List<ParameterEntry> ps)
        {
            var sb = new StringBuilder();
            foreach (ParameterEntry p in ps)
                sb.Append(string.Format(" {{{0:X8}/{1:X8}:{2}}}", p.NameHash, p.TypeHash, p.Value == null ? "" : BitConverter.ToString(p.Value.Length > 24 ? p.Value.Take(24).ToArray() : p.Value).Replace("-", "")));
            return sb.ToString();
        }
    }
}
