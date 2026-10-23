using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Frosty.Core;
using FrostySdk.Interfaces;
using FrostySdk.Managers;

namespace FrostyCLI.Core
{
    public class HeadlessProject
    {
        private FrostyProject _inner;
        public bool   IsLoaded { get; private set; }
        public string FilePath { get; private set; }

        /// <summary>
        /// False when Load() only partially populated the project.  Saving such a project writes
        /// back what little was read and destroys everything that was not -- observed truncating a
        /// 73 MB mod to 41 KB -- so Save() refuses unless explicitly allowed.
        /// </summary>
        public bool IsComplete { get; private set; } = true;

        internal static void EnsureConfig()
        {
            // FrostyProject() ctor calls Config.Get(...) which requires Config.Current != null
            Type configType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                configType = asm.GetType("Frosty.Core.Config");
                if (configType != null) break;
            }
            if (configType == null) return;
            PropertyInfo currentProp = configType.GetProperty("Current",
                BindingFlags.Public | BindingFlags.Static);
            if (currentProp == null) return;
            if (currentProp.GetValue(null) != null) return;
            try
            {
                object cfg = Activator.CreateInstance(configType, true);
                currentProp.SetValue(null, cfg,
                    BindingFlags.NonPublic | BindingFlags.Static, null, null, null);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to initialize Frosty.Core.Config.Current: " + ex.Message, ex);
            }
        }

        public void New()
        {
            EnsureConfig();
            _inner = new FrostyProject();
            IsLoaded = true;
            IsComplete = true;   // nothing was read, so nothing can be lost
            FilePath = null;
        }

        public void Load(string path, ILogger logger)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path must not be null or empty.", "path");
            if (!File.Exists(path))
                throw new FileNotFoundException("Project file not found: " + path, path);

            EnsureConfig();

            // A project already open in this process left its edits in the shared asset manager, and FrostyProject.Load
            // reads the file over them: an asset the file adds is added a second time ("An item with the same key has
            // already been added") and the load stops partway. Opening a project replaces the one in memory, so put the
            // asset manager back to the game's data first.
            if (IsLoaded && App.AssetManager != null)
                App.AssetManager.Reset();

            FrostyProject fp = new FrostyProject();
            bool complete = true;

            // FrostyProject.Load resolves a custom action handler per modified entry through
            // App.PluginManager, which is null headlessly and NREs partway through -- leaving the
            // project half-read. The same stub the mod build uses works here, so install it for the
            // load and restore afterwards.
            PluginManager savedPm = App.PluginManager;
            bool installedStub = false;
            if (App.PluginManager == null)
            {
                PluginManager stub = CreateHeadlessPluginManager();
                if (stub != null) { App.PluginManager = stub; installedStub = true; }
            }
            try
            {
                fp.Load(path);
            }
            catch (Exception ex)
            {
                // App.PluginManager is null in headless mode — expected WPF stub limitation.
                // What was read is usable for inspection, but the project is NOT whole, so it must
                // never be written back over the file it came from.
                complete = false;
                logger.LogWarning("Project loaded with headless limitation: " + ex.Message);
                if (Environment.GetEnvironmentVariable("FROSTYCLI_DEBUG") != null) logger.LogWarning("  at " + ex.GetType().Name + ": " + ex.StackTrace);
                logger.LogWarning("This project is only partially loaded. It can be read, but saving it would discard everything that failed to load, so saves over it are refused.");
            }
            finally
            {
                if (installedStub) App.PluginManager = savedPm;
            }

            _inner = fp;
            IsLoaded = true;
            IsComplete = complete;
            FilePath = path;
        }

        public bool Save(string path, ILogger logger)
        {
            return Save(path, logger, false);
        }

        public bool Save(string path, ILogger logger, bool allowIncomplete)
        {
            if (!IsLoaded)
            { logger.LogError("No project loaded."); return false; }

            string savePath = path ?? FilePath;
            if (string.IsNullOrEmpty(savePath))
            { logger.LogError("No save path specified and no file was loaded."); return false; }

            if (!IsComplete && !allowIncomplete)
            {
                logger.LogError("Refusing to save: this project did not load completely, and saving it would write back only the part that loaded, discarding the rest.");
                logger.LogError("To keep headless edits, start a fresh project instead: pass --save-as <new.fbproject> without --project. Pass 'project save --allow-incomplete <file>' only if you accept the loss.");
                return false;
            }

            try
            {
                // Written twice on purpose. When assets were loaded and bundle memberships added in
                // the same process as an EBX edit, the FIRST serialisation of that EBX came back
                // unreadable on reload (EbxReader index out of range) while a second pass over the
                // same in-memory state was fine -- reproduced deterministically with
                // 'level add --group --blueprint' on Endor_01. The first pass warms whatever the
                // writer caches; the second is the one kept.
                _inner.Save(savePath, false);
                _inner.Save(savePath, false);
                FilePath = savePath;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError("Project save failed: " + ex.Message);
                return false;
            }
        }

        public bool Build(string outPath, ModSettings settings, ILogger logger)
        {
            if (!IsLoaded)
            { logger.LogError("No project loaded. Run 'project load' or 'project new' first."); return false; }
            if (string.IsNullOrEmpty(outPath))
            { logger.LogError("outPath must not be null or empty."); return false; }

            try
            {
                // Resolve Frosty.Core.Mod.ModSettings via reflection (avoid direct namespace reference)
                Type modSettingsType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    modSettingsType = asm.GetType("Frosty.Core.Mod.ModSettings");
                    if (modSettingsType != null) break;
                }
                if (modSettingsType == null)
                {
                    MethodInfo getMethod = typeof(FrostyProject).GetMethod("GetModSettings",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (getMethod != null)
                        modSettingsType = getMethod.ReturnType;
                }
                if (modSettingsType == null)
                    throw new InvalidOperationException("Cannot locate Frosty.Core.Mod.ModSettings type.");

                // The project's own mod settings (what Frosty Editor's Export to Mod uses): any field the caller left
                // empty falls back to it, and its icon + screenshots always carry over. Without this a CLI build of an
                // editor project silently dropped the mod's icon and screenshots.
                object projSettings = _inner.ModSettings;
                Func<string, string, string> orProject = (value, prop) =>
                    !string.IsNullOrEmpty(value) ? value
                    : (projSettings?.GetType().GetProperty(prop)?.GetValue(projSettings) as string ?? "");

                string title       = orProject(settings?.Name,        "Title");
                string author      = orProject(settings?.Author,      "Author");
                string version     = orProject(settings?.Version,     "Version");
                string description = orProject(settings?.Description, "Description");
                string category    = orProject(settings?.Category,    "Category");

                object sdkSettings = Activator.CreateInstance(modSettingsType);
                SetProp(sdkSettings, modSettingsType, "Title",       title);
                SetProp(sdkSettings, modSettingsType, "Author",      author);
                SetProp(sdkSettings, modSettingsType, "Version",     version);
                SetProp(sdkSettings, modSettingsType, "Description", description);
                SetProp(sdkSettings, modSettingsType, "Category",    category);
                if (projSettings != null && projSettings.GetType() == modSettingsType)
                {
                    PropertyInfo icon = modSettingsType.GetProperty("Icon");
                    if (icon != null && icon.CanWrite) icon.SetValue(sdkSettings, icon.GetValue(projSettings));
                    MethodInfo getShot = modSettingsType.GetMethod("GetScreenshot");
                    MethodInfo setShot = modSettingsType.GetMethod("SetScreenshot");
                    if (getShot != null && setShot != null)
                        for (int i = 0; i < 4; i++)
                            setShot.Invoke(sdkSettings, new object[] { i, getShot.Invoke(projSettings, new object[] { i }) });
                }

                // Call WriteToMod via reflection (avoids direct Frosty.Core.Mod namespace reference)
                MethodInfo writeToMod = typeof(FrostyProject).GetMethod("WriteToMod",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(string), modSettingsType, typeof(bool), typeof(CancellationToken) },
                    null);
                if (writeToMod == null)
                    throw new InvalidOperationException("FrostyProject.WriteToMod method not found.");

                // FrostyModWriter.WriteProject calls App.PluginManager.GetCustomHandler(...) for every
                // modified EBX/Res entry; App.PluginManager is null headlessly, which NREs (chunk-only
                // mods build fine because the chunk path never calls it). Install a minimal stub with
                // empty handler dictionaries (GetCustomHandler returns null, so plain ebx/res mods
                // serialize via the default resource path) just for the build, then restore — scoped so
                // it can't change project-load behaviour elsewhere.
                PluginManager savedPm = App.PluginManager;
                bool installedStub = false;
                if (App.PluginManager == null)
                {
                    PluginManager stub = CreateHeadlessPluginManager();
                    if (stub != null) { App.PluginManager = stub; installedStub = true; }
                }
                // merge handlers that would drop an edit are left out for this build (the asset ships as a plain EBX)
                List<KeyValuePair<object, object>> bypassed = BypassLossyMergeHandlers(App.PluginManager, logger);
                // Kyber's map declaration: kept in the project as chunk BEBEBEBE (Kyber Level Declarator's store), shipped
                // as the embedded resource KyberLevelDeclarationData with the chunk left out -- what the plugin does
                var embedded = new Dictionary<string, byte[]>(settings?.Embedded ?? new Dictionary<string, byte[]>());
                ChunkAssetEntry kyber = App.AssetManager.GetChunkEntry(KyberDeclaration.ChunkId);
                byte[] kyberData = null;
                if (kyber != null)
                {
                    using (System.IO.Stream s = App.AssetManager.GetChunk(kyber))
                    using (var ms = new System.IO.MemoryStream()) { s.CopyTo(ms); kyberData = ms.ToArray(); }
                    var decl = KyberDeclaration.Parse(kyberData);
                    if (!KyberDeclaration.IsEmpty(decl) && !embedded.ContainsKey(KyberDeclaration.EmbeddedName))
                    {
                        embedded[KyberDeclaration.EmbeddedName] = KyberDeclaration.ToBytes(decl);
                        // Kyber reads a mod's declaration only when its category is exactly Map, Maps or Gameplay
                        // (kyber_collection frosty_mod_reader.dart) -- and then takes it from resource 6, after the icon
                        // and four screenshots
                        string cat = settings?.Category ?? "";
                        if (cat != "Map" && cat != "Maps" && cat != "Gameplay")
                        {
                            SetProp(sdkSettings, modSettingsType, "Category", "Map");
                            logger.Log("mod build: category '" + cat + "' -> 'Map' (Kyber reads declarations from Map/Maps/Gameplay mods only)");
                        }
                    }
                    App.AssetManager.RevertAsset(kyber);
                    logger.Log("mod build: Kyber declaration (" + ((Newtonsoft.Json.Linq.JArray)decl["maps"]).Count + " map(s)) embedded as " +
                               KyberDeclaration.EmbeddedName + "; chunk " + KyberDeclaration.ChunkId + " left out");
                }
                if (Environment.GetEnvironmentVariable("FROSTYCLI_DEBUG") != null)
                {
                    int checkedRes = 0, bad = 0;
                    foreach (ResAssetEntry re in App.AssetManager.EnumerateRes(modifiedOnly: true))
                    {
                        if (re.ModifiedEntry == null) { if (re.HasModifiedData) { bad++; logger.LogError("preflight: " + re.Name + " HasModifiedData but no ModifiedEntry"); } continue; }
                        object dobj = re.ModifiedEntry.DataObject;
                        if (dobj == null) continue;
                        checkedRes++;
                        try
                        {
                            MethodInfo save = dobj.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                            if (save != null) save.Invoke(dobj, null);
                        }
                        catch (Exception pex)
                        {
                            bad++;
                            logger.LogError("preflight: " + re.Name + " (" + dobj.GetType().Name + ", added bundles " + re.AddedBundles.Count + ", bundles " + re.Bundles.Count + "): " + (pex.InnerException ?? pex).Message);
                        }
                    }
                    logger.Log("preflight: " + checkedRes + " handler res checked, " + bad + " failing");
                }
                try
                {
                    if (embedded.Count == 0)
                        writeToMod.Invoke(_inner, new object[]
                            { outPath, sdkSettings, false, CancellationToken.None });
                    else
                        WriteWithEmbedded(outPath, modSettingsType, sdkSettings, embedded, logger);
                }
                finally
                {
                    RestoreHandlers(App.PluginManager, bypassed);
                    if (installedStub) App.PluginManager = savedPm;
                    if (kyberData != null && App.AssetManager.GetChunkEntry(KyberDeclaration.ChunkId) == null)
                        App.AssetManager.AddChunk(kyberData, KyberDeclaration.ChunkId);
                }
                return true;
            }
            catch (Exception ex)
            {
                string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                logger.LogError("Mod build failed: " + msg);
                if (Environment.GetEnvironmentVariable("FROSTYCLI_DEBUG") != null)
                    logger.LogError("STACK: " + ex.ToString());
                return false;
            }
        }

        // Minimal headless PluginManager: created without running its ctor/field initializers, with
        // the two custom-handler dictionaries created and then POPULATED from the loaded plugin
        // assemblies' [RegisterCustomHandler] attributes (mirroring the real PluginManager.LoadPlugin
        // logic). This lets FrostyModWriter run per-type custom action handlers headlessly during mod
        // build (e.g. MeshSetPlugin's ShaderBlockDepot Res handler) instead of falling back to
        // the default path. Plain ebx/res/chunk mods are unaffected -- their types aren't in the dicts,
        // so GetCustomHandler still returns null for them.
        public static PluginManager CreateHeadlessPluginManager()
        {
            try
            {
                var pm = (PluginManager)System.Runtime.Serialization.FormatterServices
                    .GetUninitializedObject(typeof(PluginManager));
                object ebxDict = null, resDict = null;
                foreach (string fieldName in new[] { "m_customHandlers", "m_resCustomHandlers" })
                {
                    FieldInfo fi = typeof(PluginManager).GetField(fieldName,
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fi == null) continue;
                    object dict = Activator.CreateInstance(fi.FieldType);
                    fi.SetValue(pm, dict);
                    if (fieldName == "m_customHandlers") ebxDict = dict; else resDict = dict;
                }
                PopulateCustomHandlers(ebxDict as System.Collections.IDictionary,
                                       resDict as System.Collections.IDictionary);
                return pm;
            }
            catch { return null; }
        }

        // Scans loaded assemblies for [RegisterCustomHandler] (Frosty.Core.Attributes) and fills the
        // ebx (uint hash -> handler Type) and res (ResourceType -> handler Type) dictionaries, mirroring
        // PluginManager.LoadPlugin. Reflection-only so it can't hard-fail the build path.
        private static void PopulateCustomHandlers(System.Collections.IDictionary ebxDict,
                                                   System.Collections.IDictionary resDict)
        {
            if (ebxDict == null && resDict == null) return;
            Type typeLib = FindType("FrostySdk.TypeLibrary");
            Type fnv1    = FindType("Frosty.Hash.Fnv1");
            MethodInfo getType     = typeLib?.GetMethod("GetType", new[] { typeof(string) });
            MethodInfo getDerived  = typeLib?.GetMethod("GetDerivedTypes", new[] { typeof(Type) });
            MethodInfo hashString  = fnv1?.GetMethod("HashString", new[] { typeof(string) });

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                object[] attrs;
                try { attrs = asm.GetCustomAttributes(false); } catch { continue; }
                foreach (object attr in attrs)
                {
                    Type at = attr.GetType();
                    if (at.Name != "RegisterCustomHandlerAttribute") continue;
                    try
                    {
                        string kind  = at.GetProperty("HandlerType")?.GetValue(attr)?.ToString();
                        Type handler = at.GetProperty("HandlerClassType")?.GetValue(attr) as Type;
                        if (handler == null) continue;

                        // FROSTYCLI_NO_RES_HANDLERS=1: skip res custom-action handlers so the
                        // plain res path is used. MeshSetPlugin's ShaderBlockDepot handler NREs
                        // headlessly in ShaderBlockDepotResource..ctor, which makes mod build fail
                        // for ANY project containing a modified <mesh>_mesh/blocks resource --
                        // including one produced by the tool's own `material graft`. With the
                        // handler skipped the resource ships as raw bytes, which is correct when
                        // the caller has already written the final bytes.
                        if (kind == "Res" && resDict != null
                            && Environment.GetEnvironmentVariable("FROSTYCLI_NO_RES_HANDLERS") == null)
                        {
                            object resType = at.GetProperty("ResType")?.GetValue(attr);
                            if (resType != null && !resDict.Contains(resType)) resDict.Add(resType, handler);
                        }
                        // FROSTYCLI_NO_EBX_HANDLERS=1: same escape hatch for EBX custom-action
                        // handlers. Swbf2MergerPlugin's MeshVariationActionHandler NREs headlessly
                        // in MeshVariationResource..ctor, so ANY project containing a modified
                        // MeshVariationDatabase fails to build. Skipping it ships the asset as a
                        // plain EBX replacement instead of a merge-friendly delta - correct for a
                        // single mod, worse for stacking with other mods that touch the same db.
                        else if (kind == "Ebx" && ebxDict != null && getType != null && hashString != null
                                 && Environment.GetEnvironmentVariable("FROSTYCLI_NO_EBX_HANDLERS") == null)
                        {
                            string ebxType = at.GetProperty("EbxType")?.GetValue(attr) as string;
                            if (string.IsNullOrEmpty(ebxType)) continue;
                            Type assetType = getType.Invoke(null, new object[] { ebxType }) as Type;
                            if (assetType == null) continue;
                            AddEbxHandler(ebxDict, hashString, assetType, handler);
                            if (getDerived != null)
                            {
                                var derived = getDerived.Invoke(null, new object[] { assetType }) as System.Collections.IEnumerable;
                                if (derived != null)
                                    foreach (Type sub in derived) AddEbxHandler(ebxDict, hashString, sub, handler);
                            }
                        }
                    }
                    catch { /* best-effort per attribute */ }
                }
            }
        }

        // FrostyModWriter.WriteProject hands every modified shipped EBX whose type has a custom handler to that handler's
        // SaveToMod and writes nothing else for it. Swbf2MergerPlugin's MetaDataActionHandler carries only entries appended to
        // a UIMetaDataAsset's root Assets list (MergeHandlerFidelity), so an edit inside one of its objects -- `level create`'s
        // Custom Arcade picker entries in UI/Metadata/OptionsEnumUIMetaData -- shipped as an empty delta and the level was
        // missing from the menus. For each such asset the handler's replay (shipped asset + appended Assets) is serialised
        // and compared with the edited asset; when they differ the type's handler is removed for this build, so the asset
        // goes out as a plain EBX (correct on its own; it only loses merging with another mod's edit of the same asset).
        internal static List<KeyValuePair<object, object>> BypassLossyMergeHandlers(PluginManager pm, ILogger logger)
        {
            var removed = new List<KeyValuePair<object, object>>();
            if (pm == null || App.AssetManager == null) return removed;
            var dict = typeof(PluginManager).GetField("m_customHandlers", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(pm)
                       as System.Collections.IDictionary;
            MethodInfo hashString = FindType("Frosty.Hash.Fnv1")?.GetMethod("HashString", new[] { typeof(string) });
            if (dict == null || hashString == null) return removed;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(modifiedOnly: true).ToList())
            {
                if (e.IsAdded || !e.HasModifiedData || string.IsNullOrEmpty(e.Type)) continue;
                object key;
                try { key = (uint)(int)hashString.Invoke(null, new object[] { e.Type.ToLower() }); } catch { continue; }
                if (!dict.Contains(key)) continue;
                object registered = dict[key];
                Type handler = registered as Type ?? registered?.GetType();
                if (handler == null || handler.FullName != MergeHandlerFidelity.MetaDataHandler) continue;
                string why;
                try { why = MetaDataEditLost(e); }
                catch (Exception ex) { why = "its edit could not be checked (" + ex.Message + ")"; }
                if (why == null) continue;
                dict.Remove(key);
                removed.Add(new KeyValuePair<object, object>(key, registered));
                logger.Log("mod build: " + e.Name + " ships as a plain EBX: " + why + " (" + handler.Name +
                           " carries only entries appended to the root Assets list)");
            }
            return removed;
        }

        // null when MetaDataActionHandler reproduces the edit exactly, else why it does not
        private static string MetaDataEditLost(EbxAssetEntry e)
        {
            FrostySdk.IO.EbxAsset shipped = App.AssetManager.GetEbx(e, true);   // a fresh parse of the shipped asset
            FrostySdk.IO.EbxAsset edited = App.AssetManager.GetEbx(e);
            var shippedAssets = ((dynamic)shipped.RootObject).Assets as System.Collections.IList;
            var editedAssets = ((dynamic)edited.RootObject).Assets as System.Collections.IList;
            if (shippedAssets == null || editedAssets == null) return "it has no root Assets list";
            List<object> added = MergeHandlerFidelity.ShippedAdditions(shippedAssets, editedAssets);
            foreach (object a in added) shippedAssets.Add(a);
            if (MergeHandlerFidelity.Reproduces(Serialise(shipped), Serialise(edited))) return null;
            return "the handler would ship " + added.Count + " appended Assets entr" + (added.Count == 1 ? "y" : "ies") +
                   " and drop the rest of the edit";
        }

        private static byte[] Serialise(FrostySdk.IO.EbxAsset asset)
        {
            using (var w = FrostySdk.IO.EbxBaseWriter.CreateWriter(new MemoryStream()))
            {
                w.WriteAsset(asset);
                return w.ToByteArray();
            }
        }

        internal static void RestoreHandlers(PluginManager pm, List<KeyValuePair<object, object>> removed)
        {
            if (pm == null || removed == null || removed.Count == 0) return;
            var dict = typeof(PluginManager).GetField("m_customHandlers", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(pm)
                       as System.Collections.IDictionary;
            if (dict == null) return;
            foreach (var kv in removed) if (!dict.Contains(kv.Key)) dict.Add(kv.Key, kv.Value);
        }

        private static void AddEbxHandler(System.Collections.IDictionary ebxDict, MethodInfo hashString, Type assetType, Type handler)
        {
            try
            {
                uint key = (uint)(int)hashString.Invoke(null, new object[] { assetType.Name.ToLower() });
                if (!ebxDict.Contains(key)) ebxDict.Add(key, handler);
            }
            catch { }
        }

        // FrostyProject.WriteToMod with extra embedded resources: FrostyModWriter keeps every AddResource in one list that
        // WriteProject appends to and writes at the end, so resources added before WriteProject ship beside the icon and
        // screenshots (Kyber Level Declarator adds KyberLevelDeclarationData the same way, from a Harmony postfix).
        private void WriteWithEmbedded(string outPath, Type modSettingsType, object sdkSettings, Dictionary<string, byte[]> embedded, ILogger logger)
        {
            Type writerType = FindType("Frosty.Core.IO.FrostyModWriter")
                ?? throw new InvalidOperationException("Cannot locate Frosty.Core.IO.FrostyModWriter.");
            Type embeddedType = writerType.GetNestedType("EmbeddedResource", BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new InvalidOperationException("FrostyModWriter has no EmbeddedResource type.");
            using (var fs = new FileStream(outPath, FileMode.Create))
            {
                object writer = Activator.CreateInstance(writerType, new object[] { fs, sdkSettings });
                try
                {
                    object manifest = writerType.GetProperty("ResourceManifest").GetValue(writer);
                    MethodInfo add = writerType.GetMethod("AddResource");
                    Func<string, byte[], object> make = (name, data) => Activator.CreateInstance(embeddedType,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { name, data, manifest }, null);
                    // FrostyMod reads the icon from resource 0 and the screenshots from 1-4 by POSITION (FrostyMod.cs), so
                    // the extra resources go after an icon + four screenshots of their own, as the Kyber plugin's (it adds its
                    // resource right after Screenshot3). WriteProject then adds its own five (no data from the CLI), which the
                    // mod manager never reads.
                    add.Invoke(writer, new[] { make("Icon", (byte[])modSettingsType.GetProperty("Icon")?.GetValue(sdkSettings)) });
                    MethodInfo shot = modSettingsType.GetMethod("GetScreenshot");
                    for (int i = 0; i < 4; i++)
                        add.Invoke(writer, new[] { make("Screenshot" + i, (byte[])shot?.Invoke(sdkSettings, new object[] { i })) });
                    // Kyber takes its declaration from resource 6 by position: it goes first among the extras
                    foreach (var kv in embedded.OrderBy(e => e.Key == KyberDeclaration.EmbeddedName ? 0 : 1))
                    {
                        add.Invoke(writer, new[] { make(kv.Key, kv.Value) });
                        logger.Log("mod build: embedded resource " + kv.Key + " (" + kv.Value.Length + " bytes)");
                    }
                    writerType.GetMethod("WriteProject").Invoke(writer, new object[] { _inner, false, CancellationToken.None });
                }
                finally { (writer as IDisposable)?.Dispose(); }
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t; try { t = asm.GetType(fullName); } catch { continue; }
                if (t != null) return t;
            }
            return null;
        }

        private static void SetProp(object obj, Type type, string name, object value)
        {
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
                prop.SetValue(obj, value);
        }
    }
}
