using System;
using System.Collections.Generic;
using System.IO;
using FrostySdk.Ebx;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShaderDataPlugin;

namespace FrostyCLI.Commands
{
    // Reads the game's compiled shader databases (IShaderDatabase res) the way the
    // FrostyShaderDataPlugin does, headless: `shaderdb dump <ShaderGraph> [--out file.json]`
    // lists, per render path and permutation, the graph's external value parameters with their
    // defaults, its constant (baked-in) textures and its external texture parameters.
    public class ShaderDbCommand : ICommand
    {
        public string Name => "shaderdb";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 2 || args[0].ToLowerInvariant() != "dump")
            { Console.Error.WriteLine("Usage: frostycli shaderdb dump <shader-graph>... [--list <file>] [--out <file.json>] [--all]"); return 1; }
            var graphs = new List<string>(); string outPath = null; bool all = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (args[i] == "--list" && i + 1 < args.Length) { foreach (string l in File.ReadAllLines(args[++i])) if (l.Trim().Length > 0) graphs.Add(l.Trim()); }
                else if (args[i] == "--all") all = true;
                else if (!args[i].StartsWith("--")) graphs.Add(args[i]);
            }
            if (graphs.Count == 0) { Console.Error.WriteLine("shaderdb dump: missing <shader-graph>"); return 1; }
            if (!ShaderDb.LoadAll(logger)) { Console.Error.WriteLine("shaderdb dump: a shader database failed to load (see log)"); return 1; }
            var results = new JObject(); int found = 0, missing = 0;
            foreach (string graph in graphs)
            {
                List<RenderPath> paths;
                if (!ShaderDb.TryGetRenderPaths(graph, out paths)) { results[graph] = null; missing++; logger.Log("shaderdb dump: not in any database: " + graph); continue; }
                var st = new JArray();
                foreach (object[] t in ShaderDb.GetStreamableTextures(graph))
                    st.Add(new JObject { ["texture"] = (string)t[0], ["handle"] = (uint)t[1], ["coordType"] = (uint)t[2], ["vertexUsage"] = (uint)t[3], ["tilesPerCoord"] = (float)t[4] });
                results[graph] = new JObject { ["graph"] = graph, ["streamableTextures"] = st, ["renderPaths"] = Project(paths, all) };
                found++;
            }
            JToken root = graphs.Count == 1 ? (results[graphs[0]] ?? new JObject()) : new JObject { ["graphs"] = results };
            string json = root.ToString(Formatting.Indented);
            if (outPath != null) { File.WriteAllText(outPath, json); formatter.WriteResult("OK", "shaderdb dump", new Dictionary<string, string> { { "graphs", found.ToString() }, { "missing", missing.ToString() }, { "out", outPath } }); }
            else Console.WriteLine(json);
            return missing == graphs.Count ? 1 : 0;
        }

        private static JArray Project(List<RenderPath> paths, bool all)
        {
            var arr = new JArray();
            foreach (RenderPath rp in paths)
            {
                var pairs = new JArray();
                foreach (PermutationPair pair in rp.PermutationPairs)
                {
                    pairs.Add(new JObject { ["pixel"] = Perm(pair.PixelShader, all), ["vertex"] = all ? Perm(pair.VertexShader, all) : null });
                }
                arr.Add(new JObject { ["renderPath"] = rp.RenderPathName.ToString(), ["permutations"] = pairs });
            }
            return arr;
        }

        private static JObject Perm(ShaderGraphPermutation p, bool all)
        {
            var o = new JObject();
            var ev = new JArray();
            foreach (ExternalValue v in p.ExternalValueConstants)
                ev.Add(new JObject { ["name"] = v.Name.ToString(), ["type"] = v.Type.ToString(), ["required"] = v.Required, ["default"] = v.DefaultValue == null ? null : JToken.FromObject(v.DefaultValue) });
            o["externalValues"] = ev;
            var tc = new JArray(); foreach (CString t in p.TextureConstants) tc.Add(t.ToString()); o["constantTextures"] = tc;
            var et = new JArray(); foreach (CString t in p.ExternalTextureConstants) et.Add(t.ToString()); o["externalTextures"] = et;
            var tb = new JArray();
            foreach (TextureBinding b in p.TextureBindings)
                tb.Add(new JObject { ["slot"] = b.Slot, ["texture"] = b.Texture.ToString(), ["reference"] = b.ReferenceType.ToString(), ["function"] = b.ShaderFunction.ToString(), ["type"] = b.TextureType.ToString(), ["hash"] = b.Hash.ToString() });
            o["textureBindings"] = tb;
            // where the compiled bytecode sits, so it can be pulled out and disassembled
            o["dbPath"] = p.shaderDataLookup.DbPath;
            o["dbOffset"] = p.shaderDataLookup.DbOffset;
            o["shaderSize"] = p.shaderDataLookup.ShaderSize;
            if (all)
            {
                var vc = new JArray(); foreach (object v in p.ValueConstants) vc.Add(v == null ? null : JToken.FromObject(v)); o["valueConstants"] = vc;
                var ss = new JArray(); foreach (var s in p.SamplerStates) ss.Add(JToken.FromObject(s)); o["samplerStates"] = ss;
            }
            return o;
        }
    }
}
