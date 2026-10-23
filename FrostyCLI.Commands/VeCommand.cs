using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `ve`: author visual environments the way BFII blends them. A VE component only contributes the fields it names in
    /// two places that must agree: `PropertyOverrides` (a list of field names on VisualEnvironmentComponentData) and the
    /// `FieldFlagOverrideN` bitmasks, bit = the field's FieldIndex minus the component type's first own FieldIndex
    /// (checked on Hoth's VE_Sky_Arctic_Sunny_01: Tonemap's 21 overrides are exactly bits 1..40 of 0xffa93c32 / 0x101).
    /// Setting a value without them does nothing in game; `ebx set` alone cannot port a VE.
    /// <code>
    /// ve check &lt;ve&gt;...            every component: names vs bits (a mismatch means the mapping is wrong for that type)
    /// ve apply &lt;recipe.json&gt;      [{"ve": path, "object": "Type" | "Type[n]" | guid, "prop": "Field[.sub]",
    ///                                "value": "..." | "ref": "asset path" | "null": true, "override": true | false}]
    /// </code>
    /// "override": true names the field (top-level name of prop) in PropertyOverrides and sets its bit; false removes both
    /// (the field then falls back to lower-priority VEs, as it does for a VE that never set it); omitted leaves them.
    /// </summary>
    public class VeCommand : ICommand
    {
        public string Name => "ve";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 2) { Console.Error.WriteLine("Usage: frostycli ve <check <ve>...|apply <recipe.json>|compose <spec.json>>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("ve: AssetManager not initialized."); return 1; }
            string[] rest = args.Skip(1).ToArray();
            switch (args[0].ToLowerInvariant())
            {
                case "check": return Check(rest, formatter);
                case "apply": return Apply(rest[0], logger, formatter);
                case "compose": return Compose(rest[0], logger, formatter);
                default: Console.Error.WriteLine("ve: unknown subcommand " + args[0]); return 1;
            }
        }

        // ---------------------------------------------------------------- bit mapping

        /// <summary>FieldIndex of every property of a component type, and the type's first own index (bit 0).</summary>
        internal static Dictionary<string, int> FieldBits(Type t)
        {
            var own = new Dictionary<string, int>(StringComparer.Ordinal);
            int first = int.MaxValue;
            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                int idx = FieldIndex(pi);
                if (idx < 0 || pi.Name.StartsWith("FieldFlagOverride", StringComparison.Ordinal)) continue;
                own[pi.Name] = idx;
                first = Math.Min(first, idx);
            }
            var bits = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in own) bits[kv.Key] = kv.Value - first;
            return bits;
        }

        static int FieldIndex(PropertyInfo pi)
        {
            foreach (object a in pi.GetCustomAttributes(false))
                if (a.GetType().Name == "FieldIndexAttribute")
                {
                    object v = a.GetType().GetProperty("Index")?.GetValue(a) ?? a.GetType().GetField("Index")?.GetValue(a);
                    if (v != null) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
                }
            return -1;
        }

        static List<PropertyInfo> FlagWords(Type t) =>
            Enumerable.Range(0, 8).Select(i => t.GetProperty("FieldFlagOverride" + i)).TakeWhile(p => p != null).ToList();

        static bool GetBit(object o, int bit)
        {
            var words = FlagWords(o.GetType());
            if (bit / 32 >= words.Count) return false;
            ulong w = Convert.ToUInt64(words[bit / 32].GetValue(o), CultureInfo.InvariantCulture);
            return (w >> (bit % 32) & 1UL) != 0;
        }

        static bool SetBit(object o, int bit, bool on)
        {
            var words = FlagWords(o.GetType());
            if (bit < 0 || bit / 32 >= words.Count) return false;
            PropertyInfo pi = words[bit / 32];
            ulong w = Convert.ToUInt64(pi.GetValue(o), CultureInfo.InvariantCulture);
            w = on ? w | (1UL << (bit % 32)) : w & ~(1UL << (bit % 32));
            ulong max = pi.PropertyType == typeof(ushort) ? 0xFFFFUL : pi.PropertyType == typeof(byte) ? 0xFFUL : 0xFFFFFFFFUL;
            if (w > max) return false;
            pi.SetValue(o, Convert.ChangeType(w, pi.PropertyType, CultureInfo.InvariantCulture));
            return true;
        }

        static IList Overrides(object o) => o.GetType().GetProperty("PropertyOverrides")?.GetValue(o) as IList;

        static bool IsComponent(object o) => o != null && o.GetType().GetProperty("PropertyOverrides") != null && FlagWords(o.GetType()).Count > 0;

        // ---------------------------------------------------------------- check

        static int Check(string[] ves, OutputFormatter formatter)
        {
            int bad = 0, comps = 0;
            foreach (string ve in ves)
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(ve);
                if (e == null) { Console.Error.WriteLine("ve check: not found " + ve); return 1; }
                foreach (object o in App.AssetManager.GetEbx(e).Objects)
                {
                    if (!IsComponent(o)) continue;
                    comps++;
                    Dictionary<string, int> map = FieldBits(o.GetType());
                    var names = new HashSet<string>(Overrides(o).Cast<object>().Select(x => x.ToString()));
                    var fromNames = new HashSet<int>(names.Where(map.ContainsKey).Select(n => map[n]));
                    var fromBits = new HashSet<int>(map.Values.Where(b => GetBit(o, b)));
                    var onlyNames = names.Where(n => map.ContainsKey(n) && !fromBits.Contains(map[n])).ToList();
                    var onlyBits = map.Where(kv => fromBits.Contains(kv.Value) && !names.Contains(kv.Key)).Select(kv => kv.Key).ToList();
                    var unmapped = names.Where(n => !map.ContainsKey(n)).ToList();
                    string verdict = onlyNames.Count == 0 && onlyBits.Count == 0 ? "OK" : "MISMATCH";
                    if (verdict != "OK") bad++;
                    formatter.WriteResult(verdict, "ve check " + o.GetType().Name, new Dictionary<string, string>
                    {
                        { "ve", ve }, { "names", names.Count.ToString(CultureInfo.InvariantCulture) },
                        { "named_without_bit", string.Join(",", onlyNames) }, { "bit_without_name", string.Join(",", onlyBits) },
                        { "names_not_fields_of_the_type", string.Join(",", unmapped) },
                    });
                }
            }
            Console.Out.WriteLine(string.Format("ve check: {0} component(s), {1} mismatch(es)", comps, bad));
            return bad == 0 ? 0 : 2;
        }

        // ---------------------------------------------------------------- compose

        /// <summary>
        /// `ve compose &lt;spec.json&gt;`: build a VE blueprint with exactly the components a port needs, every override
        /// cleared, ready for `ve apply`. [{"ve": new path, "template": VE cloned when "ve" does not exist yet (default
        /// FX/VE/VE_PeekHole_Spotmeter_02: blueprint, entity, one Tonemap, no schematics), "components": [{"from": VE,
        /// "object": selector}], "interface": "drop" | "keep", "entity": {"Field": "value"}}]. Each component is a copy
        /// of the named shipped one (values only: its references are left unset) with PropertyOverrides emptied and every
        /// FieldFlagOverride word zeroed, so it applies nothing until `ve apply` names a field. ClientIndex = its index in
        /// Components, ServerIndex 255 and the entity's ClientRuntimeComponentCount = the count, as every shipped VE has
        /// them (VE_PV_Hoth_Interior_01: 5 components, ClientIndex 0..4, count 5).
        /// </summary>
        static int Compose(string specPath, ExportLogger logger, OutputFormatter formatter)
        {
            JArray specs;
            try { specs = JArray.Parse(File.ReadAllText(specPath)); }
            catch (Exception ex) { Console.Error.WriteLine("ve compose: " + specPath + ": " + ex.Message); return 1; }
            int built = 0;
            foreach (JObject s in specs)
            {
                string ve = (string)s["ve"], template = (string)s["template"] ?? "FX/VE/VE_PeekHole_Spotmeter_02";
                if (ve == null) { Console.Error.WriteLine("ve compose: spec needs \"ve\""); return 1; }
                if (App.AssetManager.GetEbxEntry(ve) == null && EbxWriteCommand.ExecuteClone(new[] { template, "--as", ve }, logger, formatter) != 0) return 1;
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(ve);
                EbxAsset asset = App.AssetManager.GetEbx(e);
                object bp = asset.RootObject;
                object entity = LevelCommand.ResolveInternal(bp.GetType().GetProperty("Object")?.GetValue(bp));
                IList comps = entity?.GetType().GetProperty("Components")?.GetValue(entity) as IList;
                if (comps == null) { Console.Error.WriteLine("ve compose: " + ve + " has no VisualEnvironmentEntityData.Components"); return 1; }
                foreach (object old in comps.Cast<object>().Select(LevelCommand.ResolveInternal).Where(x => x != null).ToList()) RemoveAll(asset, old);
                while (comps.Count > 0) comps.RemoveAt(comps.Count - 1);
                int i = 0;
                foreach (JObject c in (s["components"] as JArray) ?? new JArray())
                {
                    object src = LevelAuthor.FindIn((string)c["from"], (string)c["object"]);
                    if (src == null) { Console.Error.WriteLine("ve compose: no " + c["object"] + " in " + c["from"]); return 1; }
                    object copy = EbxWriteCommand.CreateObjectByType(src.GetType().Name);
                    LevelAuthor.CopyValues(src, copy);
                    ClearOverrides(copy);
                    copy.GetType().GetProperty("ClientIndex")?.SetValue(copy, Convert.ChangeType(i, copy.GetType().GetProperty("ClientIndex").PropertyType, CultureInfo.InvariantCulture));
                    copy.GetType().GetProperty("ServerIndex")?.SetValue(copy, Convert.ChangeType(255, copy.GetType().GetProperty("ServerIndex").PropertyType, CultureInfo.InvariantCulture));
                    copy.GetType().GetMethod("SetInstanceGuid").Invoke(copy, new object[] { new FrostySdk.Ebx.AssetClassGuid(LevelCommand.NextInternalId(asset)) });
                    asset.AddObject(copy, false);
                    comps.Add(new FrostySdk.Ebx.PointerRef(copy));
                    i++;
                }
                SetNumber(entity, "ClientRuntimeComponentCount", i);
                SetNumber(entity, "ServerRuntimeComponentCount", 0);
                if (string.Equals((string)s["interface"], "drop", StringComparison.OrdinalIgnoreCase))
                {
                    PropertyInfo ip = bp.GetType().GetProperty("Interface");
                    object itf = LevelCommand.ResolveInternal(ip?.GetValue(bp));
                    if (ip != null) ip.SetValue(bp, new FrostySdk.Ebx.PointerRef());
                    if (itf != null) RemoveAll(asset, itf);
                    foreach (string l in new[] { "PropertyConnections", "EventConnections", "LinkConnections" })
                        if (bp.GetType().GetProperty(l)?.GetValue(bp) is IList cl) while (cl.Count > 0) cl.RemoveAt(cl.Count - 1);
                }
                try
                {
                    foreach (var f in (s["entity"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                        PropertyPathWriter.SetValue(entity, f.Name, f.Value.Type == JTokenType.Boolean ? ((bool)f.Value ? "true" : "false") : f.Value.ToString());
                }
                catch (Exception ex) { Console.Error.WriteLine("ve compose: " + ve + " entity: " + ex.Message); return 1; }
                if (!EbxWriteCommand.CallModifyEbx(ve, asset, logger)) return 1;
                logger.Log("ve compose: {0}: {1} component(s)", ve, i);
                built++;
            }
            formatter.WriteResult("OK", "ve compose", new Dictionary<string, string> { { "ves", built.ToString(CultureInfo.InvariantCulture) } });
            return 0;
        }

        /// <summary>Clear every override of a component: no PropertyOverrides names, every FieldFlagOverride word 0.</summary>
        internal static void ClearOverrides(object component)
        {
            IList names = Overrides(component);
            if (names != null) while (names.Count > 0) names.RemoveAt(names.Count - 1);
            foreach (PropertyInfo w in FlagWords(component.GetType()))
                w.SetValue(component, Convert.ChangeType(0, w.PropertyType, CultureInfo.InvariantCulture));
        }

        static void RemoveAll(EbxAsset asset, object o)
        {
            // RemoveObject decrements a reference count; drop the object whatever it was
            for (int k = 0; k < 64 && asset.Objects.Contains(o); k++) asset.RemoveObject(o);
        }

        static void SetNumber(object o, string name, int v)
        {
            PropertyInfo pi = o.GetType().GetProperty(name);
            if (pi != null) pi.SetValue(o, Convert.ChangeType(v, pi.PropertyType, CultureInfo.InvariantCulture));
        }

        // ---------------------------------------------------------------- apply

        static int Apply(string recipePath, ExportLogger logger, OutputFormatter formatter)
        {
            JArray steps;
            try { steps = JArray.Parse(File.ReadAllText(recipePath)); }
            catch (Exception ex) { Console.Error.WriteLine("ve apply: " + recipePath + ": " + ex.Message); return 1; }
            var assets = new Dictionary<string, EbxAsset>(StringComparer.OrdinalIgnoreCase);
            var skipped = new List<string>();
            int applied = 0;
            foreach (JObject s in steps)
            {
                string ve = (string)s["ve"], sel = (string)s["object"], prop = (string)s["prop"];
                if (ve == null || sel == null || prop == null) { Console.Error.WriteLine("ve apply: step needs ve, object, prop: " + s.ToString(Newtonsoft.Json.Formatting.None)); return 1; }
                EbxAsset asset;
                if (!assets.TryGetValue(ve, out asset))
                {
                    EbxAssetEntry e = App.AssetManager.GetEbxEntry(ve);
                    if (e == null) { Console.Error.WriteLine("ve apply: not found " + ve); return 1; }
                    assets[ve] = asset = App.AssetManager.GetEbx(e);
                }
                object o = FindObject(asset, sel);
                if (o == null) { Console.Error.WriteLine("ve apply: no object " + sel + " in " + ve); return 1; }
                try
                {
                    if (s["ref"] != null)
                    {
                        EbxAssetEntry te = App.AssetManager.GetEbxEntry((string)s["ref"]);
                        if (te == null) { Console.Error.WriteLine("ve apply: ref target not found " + (string)s["ref"]); return 1; }
                        Guid cls = RootInstanceGuid(App.AssetManager.GetEbx(te).RootObject);
                        EbxRefWriter.SetRefByPath(o, prop, te.Guid, cls);
                        asset.AddDependency(te.Guid);
                    }
                    else if (s["null"] != null && (bool)s["null"])
                        EbxRefWriter.SetRefByPath(o, prop, Guid.Empty, Guid.Empty);
                    else if (s["value"] != null)
                        PropertyPathWriter.SetValue(o, prop, (string)s["value"]);
                }
                catch (Exception ex)
                {
                    // a 2015 field BFII does not have: report it, keep going (the recipe decides what is optional)
                    if (s["optional"] != null && (bool)s["optional"]) { skipped.Add(sel + "." + prop + " (" + ex.Message + ")"); continue; }
                    string root = prop.Split('.', '[')[0];
                    if (o.GetType().GetProperty(root) == null) { skipped.Add(sel + "." + prop + " (no such field on " + o.GetType().Name + ")"); continue; }
                    Console.Error.WriteLine("ve apply: " + ve + " " + sel + "." + prop + ": " + ex.Message); return 1;
                }
                if (s["override"] != null && IsComponent(o))
                {
                    bool on = (bool)s["override"];
                    string field = prop.Split('.', '[')[0];
                    Dictionary<string, int> map = FieldBits(o.GetType());
                    int bit;
                    if (!map.TryGetValue(field, out bit)) { Console.Error.WriteLine("ve apply: " + o.GetType().Name + " has no field " + field); return 1; }
                    IList names = Overrides(o);
                    Type et = PropertyPathWriter.ListElementType(names);
                    int at = -1;
                    for (int i = 0; i < names.Count; i++) if (names[i].ToString() == field) { at = i; break; }
                    if (on && at < 0) names.Add(MakeName(et, field));
                    if (!on && at >= 0) names.RemoveAt(at);
                    if (!SetBit(o, bit, on)) logger.Log("ve apply: {0}.{1}: bit {2} is outside the FieldFlagOverride words; name list only", o.GetType().Name, field, bit);
                }
                applied++;
            }
            foreach (var kv in assets)
                if (!EbxWriteCommand.CallModifyEbx(kv.Key, kv.Value, logger)) return 1;
            foreach (string k in skipped) logger.Log("ve apply: skipped {0}", k);
            formatter.WriteResult("OK", "ve apply", new Dictionary<string, string>
            {
                { "steps", applied.ToString(CultureInfo.InvariantCulture) }, { "assets", assets.Count.ToString(CultureInfo.InvariantCulture) },
                { "skipped", skipped.Count.ToString(CultureInfo.InvariantCulture) },
            });
            return 0;
        }

        static object MakeName(Type et, string name)
        {
            if (et == typeof(string)) return name;
            try { return Activator.CreateInstance(et, name); }
            catch
            {
                MethodInfo op = et.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                  .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == et && m.GetParameters()[0].ParameterType == typeof(string));
                return op.Invoke(null, new object[] { name });
            }
        }

        /// <summary>"Type", "Type[n]" (n-th object of that type in the asset) or an exported instance guid.</summary>
        static object FindObject(EbxAsset asset, string sel)
        {
            Guid g;
            if (Guid.TryParse(sel, out g))
                return asset.Objects.FirstOrDefault(o => RootInstanceGuid(o) == g);
            if (sel.IndexOf(':') > 0) return asset.Objects.FirstOrDefault(o => LevelPrune.Matches(o, sel));
            string type = sel; int n = 0;
            int b = sel.IndexOf('[');
            if (b > 0) { type = sel.Substring(0, b); n = int.Parse(sel.Substring(b + 1).TrimEnd(']'), CultureInfo.InvariantCulture); }
            return asset.Objects.Where(o => o != null && o.GetType().Name == type).Skip(n).FirstOrDefault();
        }

        static Guid RootInstanceGuid(object obj)
        {
            try
            {
                object g = obj.GetType().GetMethod("GetInstanceGuid")?.Invoke(obj, null);
                object e = g?.GetType().GetProperty("ExportedGuid")?.GetValue(g) ?? g?.GetType().GetField("ExportedGuid")?.GetValue(g);
                return e is Guid x ? x : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }
    }
}
