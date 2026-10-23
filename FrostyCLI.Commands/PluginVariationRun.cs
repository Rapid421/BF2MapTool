using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `mesh variation-plugin-run`: drive the Mesh Variation Studio (SWBF2) editor plugin's own builder headlessly, so
    /// the plugin's code path can be built into a mod and checked in game without clicking through the editor.
    /// The plugin assembly is loaded from --plugin and its internal VariationBuilder.Create is invoked by reflection
    /// with a VariationRequest filled from the options; the outcome (created assets, warnings, errors) is printed.
    /// </summary>
    public static class PluginVariationRun
    {
        const string Usage = "Usage: frostycli mesh variation-plugin-run --plugin <MeshVariationStudioSwbf2.dll> --mesh <mesh> " +
                             "[--template <ObjectVariation>] [--scope-template <ObjectVariation>] [--name <base name>] [--folder <folder>] " +
                             "[--count <n>] [--no-seed] [--no-scope] [--deep] [--set-texture <param>=<texture>]... [--repoint <partition>]...";

        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string plugin = null, mesh = null, template = null, scopeTemplate = null, name = null, folder = null;
            int count = 1;
            bool seed = true, scope = true, deep = false;
            var setTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var repoint = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--plugin" && i + 1 < args.Length) plugin = args[++i];
                else if (a == "--mesh" && i + 1 < args.Length) mesh = args[++i];
                else if (a == "--template" && i + 1 < args.Length) template = args[++i];
                else if (a == "--scope-template" && i + 1 < args.Length) scopeTemplate = args[++i];
                else if (a == "--name" && i + 1 < args.Length) name = args[++i];
                else if (a == "--folder" && i + 1 < args.Length) folder = args[++i];
                else if (a == "--count" && i + 1 < args.Length) count = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--no-seed") seed = false;
                else if (a == "--no-scope") scope = false;
                else if (a == "--deep") deep = true;
                else if (a == "--repoint" && i + 1 < args.Length) repoint.Add(args[++i]);
                else if (a == "--set-texture" && i + 1 < args.Length)
                {
                    string kv = args[++i]; int eq = kv.IndexOf('=');
                    if (eq <= 0) { Console.Error.WriteLine("--set-texture takes <param>=<texture>"); return 1; }
                    setTextures[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                }
                else { Console.Error.WriteLine("mesh variation-plugin-run: unknown option " + a + "\n" + Usage); return 1; }
            }
            if (plugin == null || mesh == null) { Console.Error.WriteLine(Usage); return 1; }
            if (App.Logger == null) App.Logger = logger;

            Assembly asm;
            try { asm = Assembly.LoadFrom(plugin); }
            catch (Exception ex) { Console.Error.WriteLine("mesh variation-plugin-run: cannot load " + plugin + ": " + ex.Message); return 1; }
            Type reqType = asm.GetType("MeshVariationStudioSwbf2.Core.VariationRequest");
            Type builder = asm.GetType("MeshVariationStudioSwbf2.Core.VariationBuilder");
            if (reqType == null || builder == null) { Console.Error.WriteLine("mesh variation-plugin-run: " + plugin + " is not Mesh Variation Studio"); return 1; }

            object req = Activator.CreateInstance(reqType, true);
            Func<string, EbxAssetEntry> entry = p =>
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(p);
                if (e == null) throw new ArgumentException("not found: " + p);
                return e;
            };
            try
            {
                Set(req, "Mesh", entry(mesh));
                if (template != null) Set(req, "Template", entry(template));
                if (scopeTemplate != null) Set(req, "ScopeTemplate", entry(scopeTemplate));
                if (name != null) Set(req, "BaseName", name);
                if (folder != null) Set(req, "Folder", folder);
                Set(req, "Count", count);
                Set(req, "SeedTextureParameters", seed);
                Set(req, "ScopeToSourceBundles", scope);
                Set(req, "DeepDatabaseScan", deep);
            }
            catch (ArgumentException ex) { Console.Error.WriteLine("mesh variation-plugin-run: " + ex.Message); return 1; }

            MethodInfo create = builder.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
            object outcome;
            try { outcome = create.Invoke(null, new object[] { req, null }); }
            catch (TargetInvocationException ex) { Console.Error.WriteLine("mesh variation-plugin-run: the plugin threw " + ex.InnerException); return 1; }

            var created = (IList)Get(outcome, "Created");
            // What a user does next in the editor: edit the new variation's texture in the property grid, then Sync
            if (setTextures.Count > 0)
                foreach (object c in created)
                {
                    EbxAssetEntry ve = (EbxAssetEntry)c;
                    FrostySdk.IO.EbxAsset va = App.AssetManager.GetEbx(ve);
                    int edits = 0;
                    foreach (object o in va.Objects)
                    {
                        if (o.GetType().Name != "MeshMaterialVariation") continue;
                        foreach (object tp in (IList)((dynamic)o).Shader.TextureParameters)
                        {
                            string pn = ((dynamic)tp).ParameterName?.ToString();
                            string tex;
                            if (pn == null || !setTextures.TryGetValue(pn, out tex)) continue;
                            EbxAssetEntry te = App.AssetManager.GetEbxEntry(tex);
                            if (te == null) { Console.Error.WriteLine("texture not found: " + tex); return 1; }
                            Guid inst = ((FrostySdk.Ebx.AssetClassGuid)((dynamic)App.AssetManager.GetEbx(te).RootObject).GetInstanceGuid()).ExportedGuid;
                            ((dynamic)tp).Value = new FrostySdk.Ebx.PointerRef(new FrostySdk.IO.EbxImportReference { FileGuid = te.Guid, ClassGuid = inst });
                            va.AddDependency(te.Guid);
                            edits++;
                        }
                    }
                    App.AssetManager.ModifyEbx(ve.Name, va);
                    logger.Log("plugin-run: {0}: {1} texture parameter(s) edited; running VariationSync", ve.Name, edits);
                    Type sync = asm.GetType("MeshVariationStudioSwbf2.Core.VariationSync");
                    object so;
                    try { so = sync.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { ve, null }); }
                    catch (TargetInvocationException ex) { Console.Error.WriteLine("mesh variation-plugin-run: Sync threw " + ex.InnerException); return 1; }
                    foreach (object w in (IList)Get(so, "Warnings")) logger.LogWarning("plugin sync: {0}", w);
                    foreach (object e in (IList)Get(so, "Errors")) Console.Error.WriteLine("plugin sync ERROR: " + e);
                    logger.Log("plugin-run: sync: {0}", so.GetType().GetMethod("Summary").Invoke(so, null));
                }
            if (repoint.Count > 0)
                foreach (object c in created)
                {
                    EbxAssetEntry ve = (EbxAssetEntry)c;
                    uint newHash = MeshVariationDupe.NameHash(ve.Name);
                    EbxAssetEntry src = template != null ? App.AssetManager.GetEbxEntry(template) : null;
                    uint srcHash = src != null ? MeshVariationDupe.NameHash(src.Name) : 0u;
                    Guid rootGuid = ((FrostySdk.Ebx.AssetClassGuid)((dynamic)App.AssetManager.GetEbx(ve).RootObject).GetInstanceGuid()).ExportedGuid;
                    foreach (string part in repoint)
                    {
                        string err = MeshVariationDupe.RepointPublic(part, new HashSet<Guid> { App.AssetManager.GetEbxEntry(mesh).Guid }, src ?? ve, srcHash, ve.Guid, rootGuid, newHash, src == null, out int n);
                        if (err != null) { Console.Error.WriteLine(err); return 1; }
                        logger.Log("plugin-run: {0}: {1} placement(s) now use {2}", part, n, ve.Name);
                    }
                }
            var warnings = (IList)Get(outcome, "Warnings");
            var errors = (IList)Get(outcome, "Errors");
            foreach (object w in warnings) logger.LogWarning("plugin: {0}", w);
            foreach (object e in errors) Console.Error.WriteLine("plugin ERROR: " + e);
            var info = new Dictionary<string, string>
            {
                { "summary", (string)outcome.GetType().GetMethod("Summary").Invoke(outcome, null) },
            };
            int k = 0;
            foreach (object c in created) info["created_" + (k++)] = ((EbxAssetEntry)c).Name;
            formatter.WriteResult(errors.Count == 0 ? "OK" : "ERR", "mesh variation-plugin-run", info);
            return errors.Count == 0 && created.Count > 0 ? 0 : 1;
        }

        static void Set(object o, string field, object value)
        {
            FieldInfo f = o.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) throw new ArgumentException("the plugin's VariationRequest has no field " + field);
            f.SetValue(o, value);
        }

        static object Get(object o, string field) =>
            o.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);
    }
}
