using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    public static class EbxWriteCommand
    {

        // Finds a partition object by instance GUID (preferred, always unambiguous) or by type name
        // (only useful when the type occurs once). Needed because many authored objects are held as
        // PointerRefs and are not reachable from the root by a member path.
        // Selector forms: an instance GUID, a type name, or "TypeName[N]" for the Nth object of that
        // type in partition order. The index matters because a partition may hold several objects of
        // one type with no other way to tell them apart -- default/settings carries two
        // GameModeSettings, both GamePlatform_Any -- and a bare type name can only ever reach the
        // first, silently.
        internal static object FindPartitionObject(EbxAsset asset, string selector)
        {
            if (string.IsNullOrEmpty(selector)) return null;

            // "TypeName#N" / "#N": the object whose InternalId is N -- the only unambiguous handle on an object that is
            // not exported (a level's StaticModelGroupEntityData), since partition order is not the root Objects order
            int hash = selector.IndexOf('#');
            if (hash >= 0)
            {
                int wantId;
                string wantType = selector.Substring(0, hash);
                if (!int.TryParse(selector.Substring(hash + 1), out wantId)) return null;
                foreach (object o in asset.Objects)
                {
                    if (o == null) continue;
                    if (wantType.Length > 0 && !string.Equals(o.GetType().Name, wantType, StringComparison.OrdinalIgnoreCase)) continue;
                    try { dynamic d = o; if ((int)d.GetInstanceGuid().InternalId == wantId) return o; }
                    catch { }
                }
                return null;
            }

            int ordinal = -1;
            string typeSel = selector;
            int br = selector.IndexOf('[');
            if (br > 0 && selector.EndsWith("]"))
            {
                int parsed;
                if (int.TryParse(selector.Substring(br + 1, selector.Length - br - 2), out parsed) && parsed >= 0)
                {
                    ordinal = parsed;
                    typeSel = selector.Substring(0, br);
                }
            }

            Guid selGuid; bool isGuid = Guid.TryParse(typeSel, out selGuid);
            int seen = 0;
            foreach (object o in asset.Objects)
            {
                if (o == null) continue;
                if (isGuid)
                {
                    try { dynamic d = o; if (((Guid)d.GetInstanceGuid().ExportedGuid) == selGuid) return o; }
                    catch { }
                }
                else if (string.Equals(o.GetType().Name, typeSel, StringComparison.OrdinalIgnoreCase))
                {
                    if (ordinal < 0 || seen == ordinal) return o;
                    seen++;
                }
            }
            return null;
        }

        public static int ExecuteSet(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null;
            string prop    = null;
            string value   = null;
            string resRefTarget = null;
            string objectSel    = null;
            string eachType     = null;
            bool   create       = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--prop"  && i + 1 < args.Length) { prop  = args[++i]; }
                else if (args[i] == "--each-type" && i + 1 < args.Length) { eachType = args[++i]; }
                else if (args[i] == "--value" && i + 1 < args.Length) { value = args[++i]; }
                else if (args[i] == "--res" && i + 1 < args.Length) { resRefTarget = args[++i]; }
                else if (args[i] == "--object" && i + 1 < args.Length) { objectSel = args[++i]; }
                else if (args[i] == "--create") create = true;
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }

            if (ebxPath == null) { Console.Error.WriteLine("ebx set: missing <ebx-path>"); return 1; }
            if (prop    == null) { Console.Error.WriteLine("ebx set: missing --prop"); return 1; }
            // --res <res-path>: write that resource's RID into a ResourceRef field. A newly created Res
            // gets a fresh RID every build, so the value cannot be hardcoded -- naming the res is the
            // only stable way to point a cloned resource-backed asset at its own payload.
            if (resRefTarget != null)
            {
                if (!Session.Project.IsLoaded)
                { Console.Error.WriteLine("ebx set: no project loaded."); return 1; }
                if (App.AssetManager == null)
                { Console.Error.WriteLine("ebx set: AssetManager not initialized."); return 1; }
                ResAssetEntry re = App.AssetManager.GetResEntry(resRefTarget);
                if (re == null) { Console.Error.WriteLine("ebx set: --res not found: " + resRefTarget); return 1; }
                value = re.ResRid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (value   == null) { Console.Error.WriteLine("ebx set: missing --value (or --res)"); return 1; }

            if (!Session.Project.IsLoaded)
            {
                Console.Error.WriteLine("ebx set: no project loaded. Run 'project load' first.");
                return 1;
            }
            if (App.AssetManager == null)
            {
                Console.Error.WriteLine("ebx set: AssetManager not initialized.");
                return 1;
            }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { Console.Error.WriteLine("ebx set: entry not found: " + ebxPath); return 1; }

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("ebx set: load failed: " + ex.Message); return 1; }

            // Gate BEFORE the in-place write: for an already-modified asset GetEbx returns the cached
            // DataObject, so mutating it would leak even though CallModifyEbx is skipped under dry-run.
            // --object: same escape hatch as `ebx set-ref --object`. Sublevel wiring lives on
            // SubWorldReferenceObjectData instances held as PointerRefs in Objects[], which the
            // member-walking path writer cannot descend into; selecting the object by instance GUID
            // (or type name, when unique) is the only way to reach fields like BundleName.
            object setRoot = asset.RootObject;
            if (objectSel != null)
            {
                setRoot = FindPartitionObject(asset, objectSel);
                if (setRoot == null)
                { Console.Error.WriteLine("ebx set: --object '" + objectSel + "' not found in " + ebxPath); return 1; }
            }

            // --each-type <TypeName> and/or a "[*]" in --prop: apply the same write to every object of that type in
            // the partition and to every element of the wildcard list(s), e.g. every 1p fire effect of every
            // attachment modifier: --each-type WeaponFiringEffectsModifier --prop "FireEffects1p[*].UseZoomOffset".
            if (eachType != null || prop.Contains("[*]"))
            {
                var roots = new List<object>();
                if (eachType != null)
                {
                    foreach (object o in asset.Objects)
                        if (o != null && string.Equals(o.GetType().Name, eachType, StringComparison.Ordinal)) roots.Add(o);
                    if (roots.Count == 0) { Console.Error.WriteLine("ebx set: no " + eachType + " objects in " + ebxPath); return 1; }
                }
                else roots.Add(setRoot);
                int written = 0;
                foreach (object r in roots)
                    foreach (string concrete in ExpandWildcards(r, prop))
                    {
                        if (DryRun.Active) { written++; continue; }
                        try { PropertyPathWriter.SetValue(r, concrete, value, null); }
                        catch (Exception ex) { Console.Error.WriteLine("ebx set: " + concrete + ": " + ex.Message); return 1; }
                        string lostEach = CheckSetPersists(asset, r, concrete, logger);
                        if (lostEach != null) { Console.Error.WriteLine("ebx set: " + lostEach); return 1; }
                        written++;
                    }
                if (written == 0) { Console.Error.WriteLine("ebx set: '" + prop + "' matched nothing in " + ebxPath); return 1; }
                if (DryRun.Active) { DryRun.Report(logger, "set {0} = {1} on {2} ({3} field(s))", prop, value, ebxPath, written); return 0; }
                formatter.WriteResult("OK", "ebx set", new Dictionary<string, string> { { "ebx", ebxPath }, { "prop", prop }, { "fields written", written.ToString() } });
                if (!CallModifyEbx(ebxPath, asset, logger)) return 1;
                return 0;
            }

            if (DryRun.Active) { DryRun.Report(logger, "set {0} = {1} on {2}{3}", prop, value, ebxPath, objectSel != null ? " (object " + objectSel + ")" : ""); return 0; }

            try
            {
                // --create is opt-in: without it a typo in a path stays an error rather than
                // becoming a new object nobody asked for.
                PropertyPathWriter.SetValue(setRoot, prop, value,
                    create ? new PartitionObjectFactory(asset, "ebx set") : null);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine("ebx set: " + ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ebx set: write failed: " + ex.Message);
                return 1;
            }

            string lost = CheckSetPersists(asset, setRoot, prop, logger);
            if (lost != null) { Console.Error.WriteLine("ebx set: " + lost); return 1; }

            if (!CallModifyEbx(ebxPath, asset, logger)) return 1;
            return 0;
        }

        // Expands every "[*]" in a property path into one concrete path per list element (lists read through
        // PropertyPathReader, so the same paths the writer accepts). A missing or null list expands to nothing.
        internal static List<string> ExpandWildcards(object root, string path)
        {
            var result = new List<string>();
            int star = path.IndexOf("[*]", StringComparison.Ordinal);
            if (star < 0) { result.Add(path); return result; }
            string listPath = path.Substring(0, star), rest = path.Substring(star + 3);
            object list;
            try { list = PropertyPathReader.GetValue(root, listPath); } catch { return result; }
            var coll = list as System.Collections.ICollection;
            if (coll == null) return result;
            for (int i = 0; i < coll.Count; i++)
                result.AddRange(ExpandWildcards(root, listPath + "[" + i + "]" + rest));
            return result;
        }

        // A set can hold in memory and still be lost by a save: a transient member is written with the
        // project and re-applied on load (EventSpec.Name re-derives Id). When the written member is one a
        // load can put back (transient itself, or an Id under a transient Name), put the asset through the
        // project writer/reader pair the .fbproject save uses and read the path back, so such a write fails
        // here instead of vanishing silently. Only then: the in-memory trip reorders and renumbers internal
        // objects, drops ones created earlier in the process and can scramble internal pointers, so on any
        // other write it failed sets the real save keeps (AffectorApplyEntityData.Duration, an emitter's
        // OuterRadius, a WSVFX's Attach.Bone, all read back as written in a fresh process).
        // Returns null when the value survives (or the check does not apply), else the reason.
        internal static string CheckSetPersists(EbxAsset asset, object setRoot, string prop, ExportLogger logger)
        {
            object owner;
            try { owner = PropertyPathWriter.LeafOwner(setRoot, prop); }
            catch (Exception ex) { return "cannot re-read '" + prop + "' after the write: " + ex.Message; }
            string leaf = prop.Substring(prop.LastIndexOf('.') + 1);
            int bracket = leaf.IndexOf('[');
            if (bracket >= 0) leaf = leaf.Substring(0, bracket);
            if (owner == null || !PropertyPathWriter.CanBeReappliedOnLoad(owner.GetType(), leaf)) return null;
            // Opt-out for a round trip that misreads the object (Utapau KB16: a VE TonemapComponentData read back
            // BloomScale.x's bits as FieldFlagOverride0 even when the value was set to what it already was). Verify
            // such a write by saving, reloading in a new process and dumping instead.
            if (Environment.GetEnvironmentVariable("FROSTYCLI_SKIP_PERSIST_CHECK") == "1")
            {
                logger.LogWarning("ebx set: '{0}' persist check skipped (FROSTYCLI_SKIP_PERSIST_CHECK=1) - verify with a reload", prop);
                return null;
            }

            // Twice at most, for the same reason HeadlessProject.Save writes twice: the first project-format
            // serialisation in a process can read back out of range while a second pass over the same state is
            // fine. A round trip that cannot run at all says nothing about the value, so it only warns.
            EbxAsset saved = null;
            Exception rtErr = null;
            for (int attempt = 0; attempt < 2 && saved == null; attempt++)
            {
                try { saved = BinaryCloneEbx(asset); }
                catch (Exception ex) { rtErr = ex; }
            }
            if (saved == null)
            {
                logger.LogWarning("ebx set: '{0}' not verified against a save: the project round trip failed ({1})", prop, rtErr.Message);
                return null;
            }

            bool byIdentity;
            object savedRoot = CounterpartObject(asset, saved, setRoot, out byIdentity);
            if (savedRoot == null)
            {
                logger.LogWarning("ebx set: could not find the written object after a project round trip; '{0}' not verified", prop);
                return null;
            }

            object now, after;
            try { now = PropertyPathReader.GetValue(setRoot, prop); }
            catch (Exception ex) { return "cannot re-read '" + prop + "' after the write: " + ex.Message; }
            // The trip can drop what a path walks through (an internal object made by `set-ref --target-object new:`
            // earlier in the same process is not in the copy, while the real save keeps it), so a path that no
            // longer resolves in the copy leaves the write unverified rather than failed.
            try { after = PropertyPathReader.GetValue(savedRoot, prop); }
            catch (Exception ex)
            {
                logger.LogWarning("ebx set: '{0}' not verified against a save: the round-tripped copy cannot be read there ({1})", prop, ex.Message);
                return null;
            }
            if (PropertyPathWriter.SameValue(now, after)) return null;
            // The counterpart can be the wrong object, and the in-memory trip itself can be unreliable. An internal
            // object has no stable identity across it: the reader can hand the objects back in a different order and
            // renumbers internal ids to match, so the counterpart above can be a sibling of the same type. And the
            // trip can come back with values of objects nobody wrote (all three SuperSphereEvaluatorData.OuterRadius
            // of a cloned smoke emitter read 0, the saved project keeps them). So judge by the values of `prop` over
            // every object of that type: unchanged multiset = the write survived; the other objects' values intact
            // but the written one gone = a real loss; the other objects changed too = the trip proves nothing.
            switch (CompareValuesAcrossType(asset, saved, setRoot.GetType(), prop, now, after))
            {
                case TripVerdict.Survived: return null;
                case TripVerdict.Unreliable:
                    logger.LogWarning("ebx set: '{0}' not verified against a save: the project round trip also changed values that were not written", prop);
                    return null;
            }
            // A loss is only proven when the object read back is certainly the one written. Where it may not be, the
            // mismatch says as much about the trip as about the write (PF_Ability_OfficerTurret: three
            // SimpleIntStateData made by set-ref new: in the same process, the third State.Key read back as the
            // second's, and a fresh process read all three as written), so it warns.
            string uncertain = UncertainCounterpart(asset.Objects, CountObjects(saved.Objects), setRoot, owner, byIdentity);
            if (uncertain != null)
            {
                logger.LogWarning("ebx set: '{0}' = {1} read back as {2} after an in-memory project round trip, but {3}, so that proves nothing; " +
                                  "verify it with a save and a reload in a new process", prop, now, after, uncertain);
                return null;
            }
            return "'" + prop + "' = " + now + " would not survive a save: after a project write/read it reads back as " + after +
                   " (" + owner.GetType().Name + " has transient members that are re-applied on load)";
        }

        private enum TripVerdict { Survived, Lost, Unreliable }

        // Why the object read back after the round trip may not be the written one, or null when it certainly is: it was
        // found by identity (the root, or its exported guid), the trip kept every object, none was created in this
        // process, and the written member is on that object itself rather than on one it references.
        internal static string UncertainCounterpart(IEnumerable objects, int copyCount, object setRoot, object owner, bool byIdentity)
        {
            if (!byIdentity) return "the written object has no exported guid to find it by after the trip";
            if (CountObjects(objects) != copyCount) return "the trip did not keep every object of the partition";
            if (InProcessObjects.AnyCreated(objects)) return "the partition holds objects created in this process";
            if (!ReferenceEquals(owner, setRoot) && objects != null)
                foreach (object o in objects)
                    if (ReferenceEquals(o, owner)) return "the written member is on a referenced object";
            return null;
        }

        private static int CountObjects(IEnumerable objects)
        {
            int n = 0;
            if (objects != null) foreach (object o in objects) if (o != null) n++;
            return n;
        }

        // The values of `prop` over every object of `type`, before and after the round trip, as sorted multisets.
        // Equal: the write survived. Equal once the written value (`now`) is taken out of `before` and the value
        // read back in its place (`after`) out of the trip: only the written value was lost. Otherwise the trip
        // also changed objects that were not written, so it says nothing about this write.
        private static TripVerdict CompareValuesAcrossType(EbxAsset from, EbxAsset to, Type type, string prop, object now, object after)
        {
            Func<object, string> str = v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            Func<EbxAsset, List<string>> values = a =>
            {
                var l = new List<string>();
                foreach (object o in a.Objects)
                    if (o != null && o.GetType() == type) l.Add(str(PropertyPathReader.GetValue(o, prop)));
                l.Sort(StringComparer.Ordinal);
                return l;
            };
            try
            {
                List<string> before = values(from), back = values(to);
                if (System.Linq.Enumerable.SequenceEqual(before, back)) return TripVerdict.Survived;
                if (!before.Remove(str(now)) || !back.Remove(str(after))) return TripVerdict.Unreliable;
                return System.Linq.Enumerable.SequenceEqual(before, back) ? TripVerdict.Lost : TripVerdict.Unreliable;
            }
            catch { return TripVerdict.Lost; }
        }


        // The object in `to` (a binary round trip of `from`) that corresponds to `obj`: same exported instance
        // guid, else the same position in the partition's object order (BinaryCloneEbx writes DoNotSort, so the
        // order holds). Internal ids are renumbered by the writer, so an internal object's id can belong to a
        // different object after the trip: matching on it compared e.g. a passive AffectorApplyEntityData
        // (Duration 0) against the edited one and failed a set that does persist. `byIdentity`: found as the root or by
        // its exported guid, not by position.
        private static object CounterpartObject(EbxAsset from, EbxAsset to, object obj, out bool byIdentity)
        {
            byIdentity = true;
            if (ReferenceEquals(obj, from.RootObject)) return to.RootObject;
            object want = null;
            try { want = ((dynamic)obj).GetInstanceGuid(); } catch { }
            try { if (want != null && !(bool)((dynamic)want).IsExported) want = null; } catch { want = null; }
            int at = 0, pos = -1;
            foreach (object o in from.Objects) { if (ReferenceEquals(o, obj)) { pos = at; break; } at++; }
            at = 0;
            object byPos = null;
            foreach (object o in to.Objects)
            {
                if (o == null) { at++; continue; }
                if (want != null)
                {
                    object g = null;
                    try { g = ((dynamic)o).GetInstanceGuid(); } catch { }
                    if (g != null && want.Equals(g)) return o;
                }
                if (at == pos && o.GetType() == obj.GetType()) byPos = o;
                at++;
            }
            byIdentity = false;
            return byPos;
        }

        // Appends an element to a list property. Primitive/string/enum/CString elements take --value
        // (coerced); struct/object elements are constructed (default, or by --type for polymorphic lists).
        public static int ExecuteArrayAdd(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null, prop = null, value = null, typeName = null, target = null, classGuidStr = null, objectSel = null;
            string targetFileGuidStr = null;
            bool create = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--prop"  && i + 1 < args.Length) prop  = args[++i];
                else if (args[i] == "--value" && i + 1 < args.Length) value = args[++i];
                else if (args[i] == "--type"  && i + 1 < args.Length) typeName = args[++i];
                else if (args[i] == "--target" && i + 1 < args.Length) target = args[++i];
                else if (args[i] == "--target-file-guid" && i + 1 < args.Length) targetFileGuidStr = args[++i];
                else if (args[i] == "--class-guid" && i + 1 < args.Length) classGuidStr = args[++i];
                else if (args[i] == "--object" && i + 1 < args.Length) objectSel = args[++i];
                else if (args[i] == "--create") create = true;
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }
            if (ebxPath == null) { Console.Error.WriteLine("ebx array-add: missing <ebx-path>"); return 1; }
            if (prop    == null) { Console.Error.WriteLine("ebx array-add: missing --prop <list-path>"); return 1; }

            EbxAsset asset; EbxAssetEntry entry;
            if (!LoadForWrite("ebx array-add", ebxPath, out entry, out asset)) return 1;

            // --object <guid|TypeName|TypeName[N]> resolves the list against a partition sub-object
            // rather than the root, matching `ebx set`. Lists that only exist on sub-objects -- a
            // settings bundle's GameModeSettings.Config, say -- are unreachable from the root type
            // otherwise, and an unparsed --object silently became the asset path instead.
            object listRoot = asset.RootObject;
            if (objectSel != null)
            {
                listRoot = FindPartitionObject(asset, objectSel);
                if (listRoot == null)
                { Console.Error.WriteLine("ebx array-add: --object '" + objectSel + "' not found in " + ebxPath); return 1; }
            }

            System.Collections.IList list;
            try
            {
                list = PropertyPathWriter.ResolveList(listRoot, prop,
                    create ? new PartitionObjectFactory(asset, "ebx array-add") : null);
            }
            catch (Exception ex) { Console.Error.WriteLine("ebx array-add: " + ex.Message); return 1; }

            Type elemType = PropertyPathWriter.ListElementType(list);
            object item;
            bool addedPartitionObject = false;
            Guid externalTargetGuid = Guid.Empty;
            try
            {
                if (targetFileGuidStr != null)
                {
                    // Append an EXTERNAL PointerRef by raw file guid + class guid, WITHOUT requiring the target
                    // asset in this project. Used to merge several mods' additions into one shared asset (e.g. the
                    // frontend map lists every ported level registers in): the target lives in another mod and
                    // resolves at runtime from that mod's bundles.
                    Guid fg, cg2;
                    if (!Guid.TryParse(targetFileGuidStr, out fg))
                    { Console.Error.WriteLine("ebx array-add: invalid --target-file-guid"); return 1; }
                    if (classGuidStr == null || !Guid.TryParse(classGuidStr, out cg2))
                    { Console.Error.WriteLine("ebx array-add: --target-file-guid needs --class-guid <guid>"); return 1; }
                    item = new PointerRef(new EbxImportReference { FileGuid = fg, ClassGuid = cg2 });
                    externalTargetGuid = fg;
                }
                else if (target != null)
                {
                    // Append an EXTERNAL PointerRef (file guid + class guid). A sublevel's Objects list
                    // references entities that live in ANOTHER partition -- e.g. shipped
                    // A2_M3SUL_DS02.Objects holds a direct ref to GD_Default's TerrainEntityData,
                    // separately from the LayerReferenceObjectData that points at the LayerData.
                    // Without that entry the terrain entity is never spawned and no terrain appears.
                    EbxAssetEntry te = App.AssetManager.GetEbxEntry(target);
                    if (te == null) { Console.Error.WriteLine("ebx array-add: --target not found: " + target); return 1; }
                    Guid cg;
                    if (classGuidStr != null)
                    {
                        if (!Guid.TryParse(classGuidStr, out cg))
                        { Console.Error.WriteLine("ebx array-add: invalid --class-guid"); return 1; }
                    }
                    else
                    {
                        try { dynamic r = App.AssetManager.GetEbx(te).RootObject; cg = (Guid)r.GetInstanceGuid().ExportedGuid; }
                        catch (Exception ex)
                        { Console.Error.WriteLine("ebx array-add: could not resolve class guid: " + ex.Message); return 1; }
                    }
                    item = new PointerRef(new EbxImportReference { FileGuid = te.Guid, ClassGuid = cg });
                    externalTargetGuid = te.Guid;
                }
                else if (value != null)
                {
                    item = FrostyCLI.Core.TypeCoercion.Coerce(elemType, value);
                }
                else if (typeName != null)
                {
                    item = CreateObjectByType(typeName);
                    if (item == null) { Console.Error.WriteLine("ebx array-add: type not found: " + typeName); return 1; }
                    // A level's Objects list is List<PointerRef>, not a list of the objects themselves:
                    // the object lives in the partition and the list holds an INTERNAL pointer to it.
                    // Without this, authoring a sublevel (SubWorldData.Objects += LayerReferenceObjectData)
                    // fails with "is not of type PointerRef". Mirrors LevelCommand's place/add path.
                    if (elemType.Name == "PointerRef" && item.GetType().Name != "PointerRef")
                    {
                        // Give the new object a fresh EXPORTED instance GUID and the next free
                        // internal id, exactly as `level add-entity` does. Without it the object
                        // lands in the partition with a zero AssetClassGuid and IsExported=false,
                        // so nothing can ever reference it -- a connection endpoint or a
                        // cross-partition ref addresses an object by its exported GUID, and
                        // "no object with instance GUID 00000000-0000-0000-0000-000000000000" is
                        // the failure that surfaces, far from the array-add that caused it.
                        // Shipped shape and area objects are exported for exactly this reason.
                        try
                        {
                            MethodInfo setGuid = item.GetType().GetMethod("SetInstanceGuid",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (setGuid != null)
                                setGuid.Invoke(item, new object[] {
                                    new AssetClassGuid(Guid.NewGuid(), LevelCommand.NextInternalId(asset)) });
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("ebx array-add: could not assign an instance GUID: " + ex.Message);
                            return 1;
                        }
                        asset.AddObject(item, false);
                        InProcessObjects.Mark(item);
                        item = new PointerRef(item);
                        addedPartitionObject = true;
                    }
                }
                else if (elemType.IsPrimitive || elemType == typeof(string) || elemType.IsEnum)
                {
                    Console.Error.WriteLine("ebx array-add: element type " + elemType.Name + " requires --value"); return 1;
                }
                else if (elemType.IsAbstract || elemType.IsInterface)
                {
                    Console.Error.WriteLine("ebx array-add: element type " + elemType.Name + " is abstract; pass --type <concrete-type>"); return 1;
                }
                else
                {
                    item = Activator.CreateInstance(elemType);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("ebx array-add: build element failed: " + ex.Message); return 1; }

            // Element built (validated); gate before mutating the (possibly cached) list.
            if (DryRun.Active) { DryRun.Report(logger, "array-add to {0}.{1} (count {2} -> {3})", ebxPath, prop, list.Count, list.Count + 1); return 0; }

            if (externalTargetGuid != Guid.Empty) { try { asset.AddDependency(externalTargetGuid); } catch { } }

            int newIndex;
            try { newIndex = list.Add(item); }
            catch (Exception ex) { Console.Error.WriteLine("ebx array-add: Add failed (fixed-size array?): " + ex.Message); return 1; }

            if (!CallModifyEbx(ebxPath, asset, logger)) return 1;
            formatter.WriteResult("OK", "ebx array-add", new Dictionary<string, string>
            { { "ebx", ebxPath }, { "prop", prop }, { "index", newIndex.ToString() }, { "count", list.Count.ToString() } });
            return 0;
        }

        // Removes the element at --index from a list property.
        public static int ExecuteArrayRemove(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null, prop = null, objectSel = null; int index = -1; bool haveIndex = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--prop" && i + 1 < args.Length) prop = args[++i];
                else if (args[i] == "--index" && i + 1 < args.Length) { haveIndex = int.TryParse(args[++i], out index); }
                else if (args[i] == "--object" && i + 1 < args.Length) objectSel = args[++i];
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }
            if (ebxPath == null) { Console.Error.WriteLine("ebx array-remove: missing <ebx-path>"); return 1; }
            if (prop    == null) { Console.Error.WriteLine("ebx array-remove: missing --prop <list-path>"); return 1; }
            if (!haveIndex || index < 0) { Console.Error.WriteLine("ebx array-remove: missing/invalid --index <n>"); return 1; }

            EbxAsset asset; EbxAssetEntry entry;
            if (!LoadForWrite("ebx array-remove", ebxPath, out entry, out asset)) return 1;

            // --object mirrors array-add: without it a list on a sub-object could be appended to but
            // never trimmed, which is a one-way door on any partition whose content hangs off the root.
            object listRoot = asset.RootObject;
            if (objectSel != null)
            {
                listRoot = FindPartitionObject(asset, objectSel);
                if (listRoot == null)
                { Console.Error.WriteLine("ebx array-remove: --object '" + objectSel + "' not found in " + ebxPath); return 1; }
            }

            System.Collections.IList list;
            try { list = PropertyPathWriter.ResolveList(listRoot, prop); }
            catch (Exception ex) { Console.Error.WriteLine("ebx array-remove: " + ex.Message); return 1; }

            if (index >= list.Count) { Console.Error.WriteLine("ebx array-remove: index " + index + " out of range (count " + list.Count + ")"); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "array-remove index {0} from {1}.{2}", index, ebxPath, prop); return 0; }
            try { list.RemoveAt(index); }
            catch (Exception ex) { Console.Error.WriteLine("ebx array-remove: RemoveAt failed (fixed-size array?): " + ex.Message); return 1; }

            if (!CallModifyEbx(ebxPath, asset, logger)) return 1;
            formatter.WriteResult("OK", "ebx array-remove", new Dictionary<string, string>
            { { "ebx", ebxPath }, { "prop", prop }, { "removed", index.ToString() }, { "count", list.Count.ToString() } });
            return 0;
        }

        // Shared load-for-write: resolves the entry and loads a writable EbxAsset.
        internal static bool LoadForWrite(string cmd, string ebxPath, out EbxAssetEntry entry, out EbxAsset asset)
        {
            entry = null; asset = null;
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine(cmd + ": no project loaded. Run 'project load' first."); return false; }
            if (App.AssetManager == null) { Console.Error.WriteLine(cmd + ": AssetManager not initialized."); return false; }
            entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { Console.Error.WriteLine(cmd + ": entry not found: " + ebxPath); return false; }
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine(cmd + ": load failed: " + ex.Message); return false; }
            return true;
        }

        public static int ExecuteClone(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string source = null;
            string dest   = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--as" && i + 1 < args.Length) dest = args[++i];
                else if (!args[i].StartsWith("--")) source = args[i];
            }

            if (source == null) { Console.Error.WriteLine("ebx clone: missing <source-path>"); return 1; }
            if (dest   == null) { Console.Error.WriteLine("ebx clone: missing --as"); return 1; }

            if (!Session.Project.IsLoaded)
            { Console.Error.WriteLine("ebx clone: no project loaded. Run 'project load' first."); return 1; }
            if (App.AssetManager == null)
            { Console.Error.WriteLine("ebx clone: AssetManager not initialized."); return 1; }

            EbxAssetEntry srcEntry = App.AssetManager.GetEbxEntry(source);
            if (srcEntry == null) { Console.Error.WriteLine("ebx clone: source not found: " + source); return 1; }
            if (App.AssetManager.GetEbxEntry(dest) != null)
            { Console.Error.WriteLine("ebx clone: destination already exists: " + dest); return 1; }

            EbxAsset srcAsset;
            try { srcAsset = App.AssetManager.GetEbx(srcEntry); }
            catch (Exception ex) { Console.Error.WriteLine("ebx clone: load failed: " + ex.Message); return 1; }

            // Clone via the FrostySdk BINARY round-trip (EbxBaseWriter -> EbxReader), NOT a JSON
            // round-trip: JSON nulls internal PointerRefs + CStrings and collapses EventSpec Ids
            // (beta bug #2/#3). Then assign a fresh file GUID + a deterministic root instance GUID,
            // and AddEbx the new entry (ModifyEbx left a phantom that wasn't findable -- beta bug #3b).
            EbxAsset cloned;
            try { cloned = BinaryCloneEbx(srcAsset); }
            catch (Exception ex)
            {
                // A bare Message ("Index was out of range") says nothing about which side of the
                // binary round-trip failed; the stack does.
                Console.Error.WriteLine("ebx clone: clone failed for " + source + ": " + ex.ToString());
                return 1;
            }

            cloned.SetFileGuid(Guid.NewGuid());
            try
            {
                dynamic root = cloned.RootObject;
                try { root.Name = dest; } catch { /* not all roots expose Name */ }
                var guid = new AssetClassGuid(
                    Utils.GenerateDeterministicGuid(cloned.Objects, (Type)root.GetType(), cloned.FileGuid), -1);
                root.SetInstanceGuid(guid);
            }
            catch (Exception ex) { Console.Error.WriteLine("ebx clone: guid assign failed: " + ex.Message); return 1; }

            if (DryRun.Active) { DryRun.Report(logger, "clone {0} -> {1}", source, dest); return 0; }
            try { App.AssetManager.AddEbx(dest, cloned); }
            catch (Exception ex)
            { Console.Error.WriteLine("ebx clone: AddEbx failed: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); return 1; }
            if (App.AssetManager.GetEbxEntry(dest) == null)
            { Console.Error.WriteLine("ebx clone: AddEbx did not create " + dest); return 1; }
            return 0;
        }

        // Deep-clones an EbxAsset by serializing it to the SDK binary EBX format and reading it back.
        // Preserves internal references, CStrings and typed values that a JSON round-trip drops.
        // The PROJECT writer/reader pair, not the game-format one: a game-format (EbxWriterV2) round trip reads the
        // bytes back through the shared type descriptors, and after other assets were written in the same process it
        // came back shifted by one field (VE_Gamewide_01's DynamicAOComponentData: every float from HbaoRadius on read
        // its neighbour's value, FieldFlagOverride0 got a float) or threw (OutOfMemory reading a string at a bad
        // offset; the old "Index was out of range" clone flake). The project format carries its own type layout.
        private static EbxAsset BinaryCloneEbx(EbxAsset source)
        {
            using (var writer = EbxBaseWriter.CreateProjectWriter(new System.IO.MemoryStream(),
                       EbxWriteFlags.DoNotSort | EbxWriteFlags.IncludeTransient))
            {
                writer.WriteAsset(source);
                byte[] buf = writer.ToByteArray();
                using (var reader = EbxReader.CreateProjectReader(new System.IO.MemoryStream(buf)))
                    return reader.ReadAsset<EbxAsset>();
            }
        }


        public static int ExecuteRevert(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = FirstPositional(args);
            if (ebxPath == null) { Console.Error.WriteLine("ebx revert: missing <ebx-path>"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("ebx revert: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("ebx revert: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { Console.Error.WriteLine("ebx revert: entry not found: " + ebxPath); return 1; }
            if (!entry.IsModified) { Console.Error.WriteLine("ebx revert: asset is not modified in this project: " + ebxPath); return 1; }
            if (entry.IsAdded) { Console.Error.WriteLine("ebx revert: asset was added by this project; use 'ebx delete' instead: " + ebxPath); return 1; }

            if (!CallRevertAsset(entry, logger)) return 1;
            return 0;
        }

        public static int ExecuteDelete(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = FirstPositional(args);
            if (ebxPath == null) { Console.Error.WriteLine("ebx delete: missing <ebx-path>"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("ebx delete: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("ebx delete: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { Console.Error.WriteLine("ebx delete: entry not found: " + ebxPath); return 1; }
            if (!entry.IsModified && !entry.IsAdded)
            { Console.Error.WriteLine("ebx delete: can only delete assets the project has touched (added or modified): " + ebxPath); return 1; }

            if (!CallRevertAsset(entry, logger)) return 1;
            return 0;
        }

        private static string FirstPositional(string[] args)
        {
            foreach (string a in args) if (!a.StartsWith("--")) return a;
            return null;
        }

        public static int ExecuteSetRef(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null, prop = null, target = null, classGuidStr = null, objectSel = null; bool setNull = false;
            bool create = false;
            // --target-object points the ref at another object in the SAME partition (by instance
            // guid or type name), which is what entity-to-entity connections inside a level need.
            string targetObjectSel = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--prop" && i + 1 < args.Length) prop = args[++i];
                else if (args[i] == "--target" && i + 1 < args.Length) target = args[++i];
                else if (args[i] == "--class-guid" && i + 1 < args.Length) classGuidStr = args[++i];
                else if (args[i] == "--object" && i + 1 < args.Length) objectSel = args[++i];
                else if (args[i] == "--target-object" && i + 1 < args.Length) targetObjectSel = args[++i];
                else if (args[i] == "--null") setNull = true;
                else if (args[i] == "--create") create = true;
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }
            if (ebxPath == null) { Console.Error.WriteLine("ebx set-ref: missing <ebx-path>"); return 1; }
            if (prop == null)    { Console.Error.WriteLine("ebx set-ref: missing --prop"); return 1; }
            if (target == null && targetObjectSel == null && !setNull)
            { Console.Error.WriteLine("ebx set-ref: missing --target, --target-object or --null"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("ebx set-ref: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("ebx set-ref: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { Console.Error.WriteLine("ebx set-ref: entry not found: " + ebxPath); return 1; }
            // --target-object writes an INTERNAL ref, so there is no external asset to resolve.
            bool internalRef = targetObjectSel != null;
            EbxAssetEntry targetEntry = (setNull || internalRef) ? null : App.AssetManager.GetEbxEntry(target);
            if (!setNull && !internalRef && targetEntry == null) { Console.Error.WriteLine("ebx set-ref: target not found: " + target); return 1; }

            Guid fileGuid = targetEntry == null ? Guid.Empty : targetEntry.Guid;
            Guid classGuid = Guid.Empty;
            bool resolveClassGuid = false;
            if (!setNull && !internalRef)
            {
                if (classGuidStr != null)
                {
                    if (!Guid.TryParse(classGuidStr, out classGuid))
                    { Console.Error.WriteLine("ebx set-ref: invalid --class-guid"); return 1; }
                }
                else
                {
                    // Resolved below, once the leaf member is known: which object inside the target
                    // asset to address depends on what the member declares it points at, and the
                    // asset's root is not always that object.
                    resolveClassGuid = true;
                }
            }

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("ebx set-ref: load failed: " + ex.Message); return 1; }

            // --object selects a partition object that is NOT reachable from the root by member path.
            // Terrain is the motivating case: a terrain-bearing LayerData (e.g.
            // Levels/SP/A2/M3SUL/DS02/GD_Default) has an EMPTY Objects list, and its
            // TerrainEntityData lives beside the root in the partition. SetRefByPath walks real
            // members by reflection, so without this there is no way to repoint
            // TerrainEntityData.TerrainAsset in a clone. Accepts a type name or an instance GUID.
            object writeRoot = asset.RootObject;
            if (objectSel != null)
            {
                Guid selGuid; bool isGuid = Guid.TryParse(objectSel, out selGuid);
                object found = null; var seen = new List<string>();
                // "Type[N]" (Nth object of a type) / "Type#id" (internal id): the shared selector used by ebx set,
                // so a pipeline can repoint the Nth entity it just placed with level add-entity (CoruscantBF3)
                if (objectSel.IndexOf('[') > 0 || objectSel.IndexOf('#') >= 0)
                    found = FindPartitionObject(asset, objectSel);
                else
                foreach (object o in asset.Objects)
                {
                    if (o == null) continue;
                    string tn = o.GetType().Name;
                    if (seen.Count < 40 && !seen.Contains(tn)) seen.Add(tn);
                    bool match = false;
                    if (isGuid)
                    {
                        try { dynamic do_ = o; match = ((Guid)do_.GetInstanceGuid().ExportedGuid) == selGuid; }
                        catch { }
                    }
                    else match = string.Equals(tn, objectSel, StringComparison.OrdinalIgnoreCase);
                    if (match) { found = o; break; }
                }
                if (found == null)
                {
                    Console.Error.WriteLine("ebx set-ref: --object '" + objectSel + "' not found in " + ebxPath
                        + ". Types present: " + string.Join(", ", seen.ToArray()));
                    return 1;
                }
                writeRoot = found;
            }

            if (DryRun.Active) { DryRun.Report(logger, "set-ref {0} -> {1} on {2}{3}", prop, target, ebxPath, objectSel != null ? " (object " + objectSel + ")" : ""); return 0; }

            object internalTarget = null;
            if (targetObjectSel != null)
            {
                // "path:Objects[25]" addresses the object through the root by member path. Objects
                // created by `array-add --type` all carry a zero ExportedGuid, so when a level holds
                // several of one type (three VisualEnvironmentReferenceObjectData, say) neither a guid
                // nor a type name can pick the right one -- the path can.
                // "new:<TypeName>" creates the target object and points the reference at it in one
                // step. It has to be one step: an object that nothing references yet is unreachable
                // from the root, and EBX serialization keeps only the reachable graph -- so creating
                // it in one invocation and referencing it in the next silently loses it, and the
                // later set-ref reports the guid as "not found in <asset>".
                // This is how a null PointerRef field gets a value at all: SubWorldReferenceObjectData
                // .InclusionSettings, for instance, is its own partition object that shipped levels
                // never list under Objects.
                if (targetObjectSel.StartsWith("new:", StringComparison.OrdinalIgnoreCase))
                {
                    string newType = targetObjectSel.Substring(4);
                    object created = CreateObjectByType(newType);
                    if (created == null)
                    { Console.Error.WriteLine("ebx set-ref: --target-object new: type not found: " + newType); return 1; }
                    try
                    {
                        MethodInfo setGuid = created.GetType().GetMethod("SetInstanceGuid",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (setGuid != null)
                            setGuid.Invoke(created, new object[] {
                                new AssetClassGuid(Guid.NewGuid(), LevelCommand.NextInternalId(asset)) });
                        asset.AddObject(created, false);
                        InProcessObjects.Mark(created);
                    }
                    catch (Exception ex)
                    { Console.Error.WriteLine("ebx set-ref: could not create " + newType + ": " + ex.Message); return 1; }
                    internalTarget = created;
                }
                else if (targetObjectSel.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
                {
                    string ipath = targetObjectSel.Substring(5);
                    object v;
                    try { v = FrostyCLI.Core.PropertyPathReader.GetValue(asset.RootObject, ipath); }
                    catch (Exception ex)
                    { Console.Error.WriteLine("ebx set-ref: --target-object path failed: " + ex.Message); return 1; }
                    if (v != null && v.GetType().Name == "PointerRef")
                    {
                        try { dynamic pr = v; internalTarget = pr.Internal; } catch { }
                    }
                    else internalTarget = v;
                }
                else internalTarget = FindPartitionObject(asset, targetObjectSel);
                if (internalTarget == null)
                { Console.Error.WriteLine("ebx set-ref: --target-object not found in " + ebxPath + ": " + targetObjectSel); return 1; }
            }

            // The asset ROOT is the right target almost always, and was used unconditionally. It is
            // wrong when the member declares what it points at and the root is not that thing:
            // SubWorldInclusionSetting.Criterion declares a SubWorldInclusionCriterion, while
            // Gameplay/GameModes/GameModeInclusionSettings' root is a WorldPartInclusion CONTAINER
            // holding the WorldPartInclusionCriterion shipped data actually points at. Writing the
            // root there serializes cleanly, loads, and addresses a container where a criterion is
            // expected -- so the gate simply never applies and nothing says why.
            if (resolveClassGuid)
            {
                Type declared = FrostyCLI.Core.EbxRefWriter.DeclaredTargetType(writeRoot, prop);
                FrostySdk.IO.EbxAsset targetAsset;
                try { targetAsset = App.AssetManager.GetEbx(targetEntry); }
                catch (Exception ex)
                { Console.Error.WriteLine("ebx set-ref: could not load the target asset: " + ex.Message); return 1; }

                string pickError, pickNote;
                if (!FrostyCLI.Core.RefTargetPicker.TryPick(declared, targetAsset.RootObject,
                                                            targetAsset.Objects, out classGuid,
                                                            out pickError, out pickNote))
                { Console.Error.WriteLine("ebx set-ref: " + pickError); return 1; }
                if (pickNote != null) logger.LogWarning("ebx set-ref: " + pickNote);
            }

            try
            {
                IPartitionObjectFactory pathFactory =
                    create ? new PartitionObjectFactory(asset, "ebx set-ref") : null;
                if (internalTarget != null)
                    FrostyCLI.Core.EbxRefWriter.SetRefByPath(writeRoot, prop, internalTarget, pathFactory);
                else
                    FrostyCLI.Core.EbxRefWriter.SetRefByPath(writeRoot, prop, fileGuid, classGuid, pathFactory);
            }
            catch (InvalidOperationException ex) { Console.Error.WriteLine("ebx set-ref: " + ex.Message); return 1; }
            catch (Exception ex) { Console.Error.WriteLine("ebx set-ref: write failed: " + ex.Message); return 1; }
            // The pointer alone is not enough: bundling walks EbxAsset.Dependencies, so a repointed
            // asset whose dependency list still names the old target leaves the NEW one out of the
            // level bundle -- and an unbundled emitter draws nothing, silently.
            if (fileGuid != Guid.Empty) { try { asset.AddDependency(fileGuid); } catch { } }

            if (!CallModifyEbx(ebxPath, asset, logger)) return 1;
            return 0;
        }

        /// <summary>
        /// Creates an object inside an existing asset's partition WITHOUT listing it as a world
        /// object.
        ///
        /// `ebx array-add --prop Objects --type T` is the only other way to get an object into a
        /// partition, and it necessarily appends to the world-object list as well. Plenty of
        /// partition content is not a world object: a SubWorldReferenceObjectData's
        /// InclusionSettings, for instance, is a partition object referenced only by a PointerRef,
        /// and shipped levels never list it under Objects. Putting a non-entity in that list asks
        /// the engine to spawn something that is not spawnable.
        ///
        /// Prints the new exported GUID so a following `ebx set-ref --target-object &lt;guid&gt;`
        /// can point at it.
        /// </summary>
        public static int ExecuteNewObject(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null, typeName = null, id = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--type" && i + 1 < args.Length) typeName = args[++i];
                else if (args[i] == "--id" && i + 1 < args.Length) id = args[++i];
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }
            if (ebxPath == null)  { Console.Error.WriteLine("ebx new-object: missing <ebx-path>"); return 1; }
            if (typeName == null) { Console.Error.WriteLine("ebx new-object: missing --type <TypeName>"); return 1; }

            EbxAsset asset; EbxAssetEntry entry;
            if (!LoadForWrite("ebx new-object", ebxPath, out entry, out asset)) return 1;

            object item = CreateObjectByType(typeName);
            if (item == null) { Console.Error.WriteLine("ebx new-object: type not found: " + typeName); return 1; }

            Guid exported = Guid.NewGuid();
            try
            {
                MethodInfo setGuid = item.GetType().GetMethod("SetInstanceGuid",
                    BindingFlags.Public | BindingFlags.Instance);
                if (setGuid != null)
                    setGuid.Invoke(item, new object[] { new AssetClassGuid(exported, LevelCommand.NextInternalId(asset)) });
            }
            catch (Exception ex)
            { Console.Error.WriteLine("ebx new-object: could not assign an instance GUID: " + ex.Message); return 1; }

            if (id != null)
            {
                try { PropertyPathWriter.SetValue(item, "__Id", id); }
                catch { /* __Id is optional; not every type carries one */ }
            }

            if (DryRun.Active)
            { DryRun.Report(logger, "new-object {0} in {1}", typeName, ebxPath); return 0; }

            try { asset.AddObject(item, false); InProcessObjects.Mark(item); }
            catch (Exception ex) { Console.Error.WriteLine("ebx new-object: AddObject failed: " + ex.Message); return 1; }

            if (!CallModifyEbx(ebxPath, asset, logger)) return 1;
            formatter.WriteResult("OK", "ebx new-object", new Dictionary<string, string>
            { { "ebx", ebxPath }, { "type", typeName }, { "guid", exported.ToString() } });
            return 0;
        }

        public static int ExecuteCreate(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null, typeName = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--type" && i + 1 < args.Length) typeName = args[++i];
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }
            if (ebxPath == null)  { Console.Error.WriteLine("ebx create: missing <ebx-path>"); return 1; }
            if (typeName == null) { Console.Error.WriteLine("ebx create: missing --type"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("ebx create: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("ebx create: AssetManager not initialized."); return 1; }
            if (App.AssetManager.GetEbxEntry(ebxPath) != null)
            { Console.Error.WriteLine("ebx create: path already exists: " + ebxPath); return 1; }

            object root;
            try { root = CreateObjectByType(typeName); }
            catch (Exception ex) { Console.Error.WriteLine("ebx create: " + ex.Message); return 1; }
            if (root == null) { Console.Error.WriteLine("ebx create: type not found in type library: " + typeName); return 1; }

            EbxAsset asset;
            try { asset = ConstructEbxAsset(root); }
            catch (Exception ex) { Console.Error.WriteLine("ebx create: cannot construct EbxAsset: " + ex.Message); return 1; }

            // Assign fresh file + instance GUIDs.
            try { AssignFreshGuids(asset, root); }
            catch (Exception ex) { Console.Error.WriteLine("ebx create: guid assign failed: " + ex.Message); return 1; }

            if (DryRun.Active) { DryRun.Report(logger, "create {0} ({1})", ebxPath, typeName); return 0; }
            // Use AddEbx (project-add) rather than ModifyEbx.
            try
            {
                Type amt = App.AssetManager.GetType();
                MethodInfo add = null;
                foreach (MethodInfo c in amt.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (c.Name == "AddEbx" && c.GetParameters().Length >= 2 &&
                        c.GetParameters()[0].ParameterType == typeof(string)) { add = c; break; }
                if (add == null) { Console.Error.WriteLine("ebx create: AssetManager.AddEbx(string,EbxAsset,...) not found"); return 1; }
                ParameterInfo[] ps = add.GetParameters();
                object[] call = new object[ps.Length];
                call[0] = ebxPath; call[1] = asset;
                if (ps.Length > 2) call[2] = Array.CreateInstance(typeof(int), 0); // params int[]
                add.Invoke(App.AssetManager, call);
            }
            catch (Exception ex)
            {
                string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                Console.Error.WriteLine("ebx create: AddEbx failed: " + msg); return 1;
            }
            return 0;
        }

        internal static object CreateObjectByType(string typeName)
        {
            // Prefer FrostySdk.TypeLibrary.CreateObject(string).
            Type tl = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                tl = asm.GetType("FrostySdk.TypeLibrary");
                if (tl != null) break;
            }
            if (tl != null)
            {
                MethodInfo m = tl.GetMethod("CreateObject", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (m != null) return m.Invoke(null, new object[] { typeName });
            }
            // Fallback: find the type by name and Activator.CreateInstance.
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType("FrostySdk.Ebx." + typeName) ?? asm.GetType(typeName); }
                catch { continue; }
                if (t == null) foreach (Type cand in SafeGetTypes(asm)) if (cand.Name == typeName) { t = cand; break; }
                if (t != null) return Activator.CreateInstance(t);
            }
            return null;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); } catch { return Array.Empty<Type>(); }
        }

        private static EbxAsset ConstructEbxAsset(object root)
        {
            Type at = typeof(EbxAsset);
            ConstructorInfo ctor = at.GetConstructor(new[] { typeof(object[]) });
            if (ctor != null) return (EbxAsset)ctor.Invoke(new object[] { new object[] { root } });
            ConstructorInfo ctor2 = at.GetConstructor(new[] { typeof(IEnumerable<object>) });
            if (ctor2 != null) return (EbxAsset)ctor2.Invoke(new object[] { new List<object> { root } });
            throw new InvalidOperationException("no object[] or IEnumerable<object> constructor on EbxAsset");
        }

        private static void AssignFreshGuids(EbxAsset asset, object root)
        {
            // FileGuid
            Type at = asset.GetType();
            Guid newFile = Guid.NewGuid();
            PropertyInfo fgp = at.GetProperty("FileGuid", BindingFlags.Public | BindingFlags.Instance);
            if (fgp != null && fgp.CanWrite) fgp.SetValue(asset, newFile);
            else { MethodInfo sfg = at.GetMethod("SetFileGuid", BindingFlags.Public | BindingFlags.Instance); if (sfg != null) sfg.Invoke(asset, new object[] { newFile }); }
            // Instance GUID on root
            MethodInfo getIg = root.GetType().GetMethod("GetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo setIg = root.GetType().GetMethod("SetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
            if (getIg != null && setIg != null)
            {
                object oldIg = getIg.Invoke(root, null);
                ConstructorInfo cinfo = oldIg.GetType().GetConstructor(new[] { typeof(Guid) });
                if (cinfo != null) setIg.Invoke(root, new[] { cinfo.Invoke(new object[] { Guid.NewGuid() }) });
            }
        }

        internal static bool CallRevertAsset(AssetEntry entry, ExportLogger logger)
        {
            if (DryRun.Active) { DryRun.Report(logger, "revert {0}", entry != null ? entry.Name : "?"); return true; }
            Type amt = App.AssetManager.GetType();
            MethodInfo m = null;
            foreach (MethodInfo cand in amt.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (cand.Name == "RevertAsset") { m = cand; break; }
            if (m == null) { Console.Error.WriteLine("ebx write: AssetManager.RevertAsset not found"); return false; }
            // Signature: RevertAsset(AssetEntry, bool dataOnly = false, bool suppressOnModify = true)
            ParameterInfo[] ps = m.GetParameters();
            object[] call = new object[ps.Length];
            call[0] = entry;
            for (int i = 1; i < ps.Length; i++) call[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (object)false;
            try { m.Invoke(App.AssetManager, call); return true; }
            catch (Exception ex)
            {
                string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                Console.Error.WriteLine("ebx write: RevertAsset failed: " + msg);
                return false;
            }
        }

        // `ebx replace-string <asset-prefix> --from <text> --to <text> [--type <EbxType>]` rewrites every string and
        // CString field (including list elements and nested structs) in every object of every asset whose path starts
        // with the prefix. Case-insensitive match; the replacement is inserted as given. Built to retarget
        // SubWorldReferenceObjectData.BundleName (and friends) across a scaffolded level, where the fields sit on
        // internal objects that `ebx set` cannot address.
        public static int ExecuteReplaceString(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string prefix = null, from = null, to = null, typeFilter = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--from" && i + 1 < args.Length) from = args[++i];
                else if (args[i] == "--to" && i + 1 < args.Length) to = args[++i];
                else if (args[i] == "--type" && i + 1 < args.Length) typeFilter = args[++i];
                else if (!args[i].StartsWith("--")) prefix = args[i];
            }
            if (prefix == null || from == null || to == null)
            { Console.Error.WriteLine("ebx replace-string: usage <asset-prefix> --from <text> --to <text> [--type <EbxType>]"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("ebx replace-string: no project loaded."); return 1; }

            var entries = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                if (e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && (typeFilter == null || string.Equals(e.Type, typeFilter, StringComparison.OrdinalIgnoreCase)))
                    entries.Add(e);

            int assetsChanged = 0, fields = 0;
            foreach (EbxAssetEntry e in entries)
            {
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(e); } catch { continue; }
                int n = 0;
                var seen = new HashSet<object>(RefEq.Instance);
                Func<string, string> map = v => v.IndexOf(from, StringComparison.OrdinalIgnoreCase) < 0 ? null
                    : System.Text.RegularExpressions.Regex.Replace(v, System.Text.RegularExpressions.Regex.Escape(from),
                        to.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (object o in asset.Objects) n += MapStrings(o, map, seen);
                if (n == 0) continue;
                fields += n;
                if (DryRun.Active) { DryRun.Report(logger, "replace {0} field(s) in {1}", n, e.Name); continue; }
                if (!CallModifyEbx(e.Name, asset, logger)) return 1;
                assetsChanged++;
                logger.Log("replace-string: {0} field(s) in {1}", n, e.Name);
            }
            formatter.WriteResult("OK", "ebx replace-string", new Dictionary<string, string>
            { { "prefix", prefix }, { "scanned", entries.Count.ToString() }, { "assets", assetsChanged.ToString() }, { "fields", fields.ToString() } });
            return 0;
        }


        // Replaces inside one object graph node; returns the number of fields changed. Does not follow
        // PointerRefs (every partition object is visited through asset.Objects instead).
        internal static int MapStrings(object obj, Func<string, string> map, HashSet<object> seen)
        {
            if (obj == null) return 0;
            Type t = obj.GetType();
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t.Name == "PointerRef" || t.Name == "CString") return 0;
            if (!t.IsValueType && !seen.Add(obj)) return 0;
            int n = 0;
            if (obj is System.Collections.IList)
            {
                var list = (System.Collections.IList)obj;
                for (int i = 0; i < list.Count; i++)
                {
                    object el = list[i];
                    if (el == null) continue;
                    object repl;
                    if (TryMapScalar(el, map, out repl)) { list[i] = repl; n++; continue; }
                    int k = MapStrings(el, map, seen);
                    if (k > 0 && el.GetType().IsValueType) list[i] = el;
                    n += k;
                }
                return n;
            }
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                if (v == null) continue;
                object repl;
                if (TryMapScalar(v, map, out repl)) { if (p.CanWrite) { p.SetValue(obj, repl); n++; } continue; }
                int k = MapStrings(v, map, seen);
                if (k > 0 && v.GetType().IsValueType && p.CanWrite) p.SetValue(obj, v);
                n += k;
            }
            return n;
        }

        private static bool TryMapScalar(object v, Func<string, string> map, out object repl)
        {
            repl = null;
            Type t = v.GetType();
            if (t != typeof(string) && t.Name != "CString") return false;
            string s = v.ToString();
            if (s == null) return false;
            string r = map(s);
            if (r == null || r == s) return false;
            if (t == typeof(string)) { repl = r; return true; }
            MethodInfo imp = t.GetMethod("op_Implicit", new[] { typeof(string) });
            if (imp != null) { repl = imp.Invoke(null, new object[] { r }); return true; }
            ConstructorInfo ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) { repl = ctor.Invoke(new object[] { r }); return true; }
            return false;
        }

        internal sealed class RefEq : IEqualityComparer<object>
        {
            public static readonly RefEq Instance = new RefEq();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        internal static bool CallModifyEbx(string ebxName, EbxAsset asset, ExportLogger logger)
        {
            if (DryRun.Active) { DryRun.Report(logger, "modify {0}", ebxName); return true; }
            Type amt = App.AssetManager.GetType();
            MethodInfo m = amt.GetMethod("ModifyEbx",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(EbxAsset) }, null);
            if (m == null)
            {
                foreach (MethodInfo cand in amt.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (cand.Name == "ModifyEbx" && cand.GetParameters().Length == 2) { m = cand; break; }
            }
            if (m == null) { Console.Error.WriteLine("ebx write: AssetManager.ModifyEbx not found"); return false; }
            try { m.Invoke(App.AssetManager, new object[] { ebxName, asset }); return true; }
            catch (Exception ex)
            {
                string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                Console.Error.WriteLine("ebx write: ModifyEbx failed: " + msg);
                return false;
            }
        }
    }
}
