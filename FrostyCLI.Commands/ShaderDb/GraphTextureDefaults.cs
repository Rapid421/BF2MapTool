using System;
using System.Collections.Generic;
using System.Linq;

namespace ShaderDataPlugin
{
    // The textures a compiled shader graph supplies for its own external texture parameters. A 2015 graph written for one
    // prop (SS_MetalPlate_Floor_01) bakes its art in: the material and its variation database record leave BaseColor
    // unset and the graph binds T_MetalPlate_Floor_02_C in that parameter's slot. A port that keeps only the explicit
    // parameters gives the prop no texture at all, so on BFII's fallback preset it drew black (Hoth 2015 hangar floor
    // plates, lights, ice walls).
    //
    // Rule (as the Hoth shader inventory derives it): in a pixel permutation, a slot bound both to an external texture
    // parameter and to an embedded texture holds that parameter's default. Permutations can disagree; the texture most
    // of them bind wins, ties broken by name so the result is stable.
    public static class GraphTextureDefaults
    {
        public const string ExternalParameter = "External texture parameter";
        public const string EmbeddedTexture = "Embedded texture";

        /// <summary>One pixel-permutation texture binding: slot, texture name and reference type as the database reads them.</summary>
        public struct Binding
        {
            public uint Slot;
            public string Texture;
            public string Reference;
            public Binding(uint slot, string texture, string reference) { Slot = slot; Texture = texture; Reference = reference; }
        }

        /// <summary>Parameter name -> default texture path for a graph in the loaded shader databases; empty when the
        /// graph is in none of them (ShaderDb.LoadAll first).</summary>
        public static Dictionary<string, string> Of(string graph)
        {
            List<RenderPath> paths;
            var perms = new List<IEnumerable<Binding>>();
            if (graph != null && ShaderDb.TryGetRenderPaths(graph, out paths))
                foreach (RenderPath rp in paths)
                    foreach (PermutationPair pair in rp.PermutationPairs)
                        if (pair.PixelShader != null)
                            perms.Add(pair.PixelShader.TextureBindings.Select(b => new Binding(b.Slot, b.Texture.ToString(), b.ReferenceType.ToString())));
            return Resolve(perms);
        }

        /// <summary>The graph's constant (embedded, parameterless) textures that live in the graph's own folder -- the prop's
        /// art, as opposed to shared weathering / terrain / common maps -- in first-seen order.</summary>
        public static List<string> ConstantsOf(string graph)
        {
            List<RenderPath> paths;
            var perms = new List<IEnumerable<Binding>>();
            if (graph != null && ShaderDb.TryGetRenderPaths(graph, out paths))
                foreach (RenderPath rp in paths)
                    foreach (PermutationPair pair in rp.PermutationPairs)
                        if (pair.PixelShader != null)
                            perms.Add(pair.PixelShader.TextureBindings.Select(b => new Binding(b.Slot, b.Texture.ToString(), b.ReferenceType.ToString())));
            return OwnConstants(graph, perms);
        }

        public static List<string> OwnConstants(string graph, IEnumerable<IEnumerable<Binding>> permutations)
        {
            var result = new List<string>();
            if (graph == null) return result;
            string folder = graph.Contains("/") ? graph.Substring(0, graph.LastIndexOf('/') + 1) : "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IEnumerable<Binding> perm in permutations)
            {
                var list = perm.ToList();
                var parameterSlots = new HashSet<uint>(list.Where(b => b.Reference == ExternalParameter).Select(b => b.Slot));
                foreach (Binding b in list)
                {
                    if (b.Reference != EmbeddedTexture || parameterSlots.Contains(b.Slot) || string.IsNullOrEmpty(b.Texture)) continue;
                    if (b.Texture.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!b.Texture.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || b.Texture.IndexOf('/', folder.Length) >= 0) continue;
                    if (seen.Add(b.Texture)) result.Add(b.Texture);
                }
            }
            return result;
        }

        /// <summary>A texture's packing family from its name suffix ("T_LightGround_01_CS" -> "CS"), upper case.</summary>
        public static string Family(string texture)
        {
            if (string.IsNullOrEmpty(texture)) return "";
            string name = texture.Substring(texture.LastIndexOf('/') + 1);
            int u = name.LastIndexOf('_');
            return u < 0 ? "" : name.Substring(u + 1).ToUpperInvariant();
        }

        /// <summary>The rule itself, over each permutation's bindings (separate from the database so it can be tested).</summary>
        public static Dictionary<string, string> Resolve(IEnumerable<IEnumerable<Binding>> permutations)
        {
            var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (IEnumerable<Binding> perm in permutations)
            {
                var external = new Dictionary<uint, string>();
                var embedded = new Dictionary<uint, List<string>>();
                foreach (Binding b in perm)
                {
                    if (string.IsNullOrEmpty(b.Texture)) continue;
                    if (b.Reference == ExternalParameter) external[b.Slot] = b.Texture;
                    else if (b.Reference == EmbeddedTexture)
                    {
                        List<string> list;
                        if (!embedded.TryGetValue(b.Slot, out list)) embedded[b.Slot] = list = new List<string>();
                        list.Add(b.Texture);
                    }
                }
                foreach (KeyValuePair<uint, string> ext in external)
                {
                    List<string> defaults;
                    if (!embedded.TryGetValue(ext.Key, out defaults)) continue;
                    Dictionary<string, int> v;
                    if (!votes.TryGetValue(ext.Value, out v)) votes[ext.Value] = v = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (string t in defaults) { int n; v.TryGetValue(t, out n); v[t] = n + 1; }
                }
            }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, int>> p in votes)
            {
                string best = p.Value.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Key;
                // a hashed name ("0x34A0DB8D") is a texture the database could not name: nothing to bind
                if (!best.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) result[p.Key] = best;
            }
            return result;
        }
    }
}
