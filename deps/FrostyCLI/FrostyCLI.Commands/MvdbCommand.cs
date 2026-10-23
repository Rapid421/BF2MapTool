using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;

namespace FrostyCLI.Commands
{
    public class MvdbCommand : ICommand
    {
        public string Name => "mvdb";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1) { Console.Error.WriteLine("Usage: frostycli mvdb <list|add|remove> ..."); return 1; }
            switch (args[0].ToLowerInvariant())
            {
                case "list":   return ExecuteList(Sub(args), logger, formatter);
                case "add":    return ExecuteAdd(Sub(args), logger, formatter);
                case "copy":   return ExecuteCopy(Sub(args), logger, formatter);
                case "remove": return ExecuteRemove(Sub(args), logger, formatter);
                default: Console.Error.WriteLine("mvdb: unknown subcommand '" + args[0] + "'"); return 1;
            }
        }
        private static string[] Sub(string[] a) { var r = new string[a.Length - 1]; Array.Copy(a, 1, r, 0, r.Length); return r; }

        internal static IList FindEntries(object root)
        {
            foreach (PropertyInfo p in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object v; try { v = p.GetValue(root); } catch { continue; }
                if (!(v is IList)) continue;
                IList list = (IList)v;
                Type et = null;
                if (list.GetType().IsGenericType) et = list.GetType().GetGenericArguments()[0];
                else if (list.Count > 0 && list[0] != null) et = list[0].GetType();
                if (et != null && et.Name.IndexOf("MeshVariation", StringComparison.OrdinalIgnoreCase) >= 0)
                    return list;
            }
            return null;
        }

        public static int ExecuteList(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string path = null, filter = null, outFile = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (!args[i].StartsWith("--")) path = args[i];
            }
            if (path == null) { Console.Error.WriteLine("mvdb list: missing <path>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("mvdb list: AssetManager not initialized."); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
            if (entry == null) { Console.Error.WriteLine("mvdb list: not found: " + path); return 1; }
            EbxAsset asset; try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("mvdb list: load failed: " + ex.Message); return 1; }
            IList entries = FindEntries(asset.RootObject);
            if (entries == null) { Console.Error.WriteLine("mvdb list: no MeshVariation entry list found on root"); return 1; }

            var outArr = new List<object>();
            foreach (object e in entries)
            {
                if (e == null) continue;
                var jo = EbxDumper.DumpRootObject(e, resolve: true);
                if (filter != null && jo.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                outArr.Add(jo);
            }
            string json = JsonConvert.SerializeObject(outArr, Formatting.Indented);
            if (outFile != null) { System.IO.File.WriteAllText(outFile, json); Console.Out.WriteLine("wrote " + outArr.Count + " entries to " + outFile); }
            else Console.Out.WriteLine(json);
            return 0;
        }

        private static bool s_zeroMaterialId = false;
        // --like-keep-textures: the donor's MaterialId / SurfaceShaderGuid / SurfaceShaderId, but the texture parameters of
        // the mesh's OWN material (a ported mesh on a shipped particle template: the id is what the mesh-particle renderer
        // keys on - id 0 drew nothing - while the textures must stay the port's; CoruscantBF3 air traffic)
        private static bool s_likeKeepTextures = false;

        public static int ExecuteAdd(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            s_zeroMaterialId = false;
            s_likeKeepTextures = false;
            string path = null, mesh = null, material = null, varHash = null, like = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mesh" && i + 1 < args.Length) mesh = args[++i];
                else if (args[i] == "--like" && i + 1 < args.Length) like = args[++i];
                else if (args[i] == "--zero-material-id") s_zeroMaterialId = true;
                else if (args[i] == "--like-keep-textures") s_likeKeepTextures = true;
                else if (args[i] == "--material" && i + 1 < args.Length) material = args[++i];
                else if (args[i] == "--variation-hash" && i + 1 < args.Length) varHash = args[++i];
                else if (!args[i].StartsWith("--")) path = args[i];
            }
            if (path == null)     { Console.Error.WriteLine("mvdb add: missing <path>"); return 1; }
            if (mesh == null)     { Console.Error.WriteLine("mvdb add: missing --mesh"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("mvdb add: no project loaded."); return 1; }

            uint variationHash;
            if (!TryParseVariationHash(varHash, out variationHash))
            {
                Console.Error.WriteLine("mvdb add: --variation-hash must be an unsigned 32-bit decimal or 0x-prefixed hexadecimal value");
                return 1;
            }
            if (material != null)
                logger.LogWarning("mvdb add: --material is deprecated and ignored; material records are derived from the mesh's internal materials.");

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
            if (entry == null) { Console.Error.WriteLine("mvdb add: not found: " + path); return 1; }
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(mesh);
            if (meshEntry == null) { Console.Error.WriteLine("mvdb add: mesh not found: " + mesh); return 1; }

            EbxAsset asset; try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("mvdb add: load failed: " + ex.Message); return 1; }
            IList entries = FindEntries(asset.RootObject);
            if (entries == null) { Console.Error.WriteLine("mvdb add: no entry list found"); return 1; }

            EbxAsset meshAsset;
            try { meshAsset = App.AssetManager.GetEbx(meshEntry); }
            catch (Exception ex) { Console.Error.WriteLine("mvdb add: mesh load failed: " + ex.Message); return 1; }

            IList sourceMaterials = GetMember(meshAsset.RootObject, "Materials") as IList;
            if (sourceMaterials == null)
            {
                Console.Error.WriteLine("mvdb add: mesh has no Materials list: " + mesh);
                return 1;
            }
            if (sourceMaterials.Count == 0)
            {
                Console.Error.WriteLine("mvdb add: mesh has no internal materials: " + mesh);
                return 1;
            }
            if (EntryExists(entries, meshEntry.Guid, variationHash))
            {
                Console.Error.WriteLine("mvdb add: an entry already exists for mesh " + mesh + " with variation hash " + variationHash);
                return 1;
            }

            // MaterialId is per material *instance* and its derivation is unknown, so a fresh entry
            // can only default it to 0. --like copies the complete material records from an already-
            // registered mesh's entry, replacing only their Material pointer with the clone's. This
            // keeps the shipped id, shader identity and texture parameters coherent.
            List<object> donorMaterials = null;
            if (like != null)
            {
                EbxAssetEntry donorEntry = App.AssetManager.GetEbxEntry(like);
                if (donorEntry == null) { Console.Error.WriteLine("mvdb add: --like mesh not found: " + like); return 1; }
                donorMaterials = CollectMaterialRecords(entries, donorEntry.Guid);
                if (donorMaterials == null)
                {
                    // Cloud City style levels register the "s2/<mesh>" twin of a shared mesh rather than
                    // the mesh itself. Its records name the shader this level actually compiles
                    // permutations for; a foreign level's records can name one it does not (the mesh
                    // then binds to a shader the level cannot draw and stays invisible).
                    string twinName = like.StartsWith("s2/", StringComparison.OrdinalIgnoreCase) ? like.Substring(3) : "s2/" + like;
                    EbxAssetEntry twin = App.AssetManager.GetEbxEntry(twinName);
                    if (twin != null)
                    {
                        donorMaterials = CollectMaterialRecords(entries, twin.Guid);
                        if (donorMaterials != null) logger.Log("mvdb add: using material records for {0} from this database's entry for {1}", like, twinName);
                    }
                }
                if (donorMaterials == null)
                {
                    // A level-local target database need not contain the structural mesh selected as
                    // the clone template. Its complete shipped material record can live in another
                    // MVDB; find that database through the reverse-reference index and use it as the
                    // donor while still writing the new mesh's entry into `path`.
                    List<string> users;
                    if (IndexService.GetRefIndex().Refs.TryGetValue(donorEntry.Guid.ToString("D"), out users))
                        foreach (string user in users)
                        {
                            EbxAssetEntry donorDb = App.AssetManager.GetEbxEntry(user);
                            if (donorDb == null || donorDb.Type != "MeshVariationDatabase") continue;
                            try
                            {
                                IList donorEntries = FindEntries(App.AssetManager.GetEbx(donorDb).RootObject);
                                donorMaterials = donorEntries != null ? CollectMaterialRecords(donorEntries, donorEntry.Guid) : null;
                            }
                            catch { donorMaterials = null; }
                            if (donorMaterials != null)
                            {
                                logger.Log("mvdb add: using material records for {0} from {1}", like, donorDb.Name);
                                break;
                            }
                        }
                    if (donorMaterials == null)
                    {
                        Console.Error.WriteLine("mvdb add: --like mesh '" + like + "' has no entry in " + path + " or another indexed database");
                        return 1;
                    }
                }
                if (donorMaterials.Count == 0)
                {
                    Console.Error.WriteLine("mvdb add: --like mesh '" + like + "' has no material records");
                    return 1;
                }
                if (donorMaterials.Count != sourceMaterials.Count)
                    logger.LogWarning("mvdb add: --like mesh '{0}' has {1} material(s) while '{2}' has {3}; extra target slots reuse the final donor record",
                        like, donorMaterials.Count, mesh, sourceMaterials.Count);
            }

            Type elemType = entries.GetType().IsGenericType
                ? entries.GetType().GetGenericArguments()[0]
                : (entries.Count > 0 && entries[0] != null ? entries[0].GetType() : null);
            if (elemType == null) { Console.Error.WriteLine("mvdb add: cannot determine entry type"); return 1; }
            object newEntry;
            try { newEntry = Activator.CreateInstance(elemType); }
            catch (Exception ex) { Console.Error.WriteLine("mvdb add: cannot construct entry: " + ex.Message); return 1; }

            try
            {
                Guid meshClassGuid = ResolveClassGuid(meshAsset.RootObject);
                if (meshClassGuid == Guid.Empty)
                    throw new InvalidOperationException("mesh root has no exported class GUID");

                EbxRefWriter.SetRefByPath(newEntry, "Mesh", meshEntry.Guid, meshClassGuid);
                SetMember(newEntry, "VariationAssetNameHash", variationHash);

                IList targetMaterials = GetMember(newEntry, "Materials") as IList;
                if (targetMaterials == null)
                    throw new InvalidOperationException("entry has no Materials list");
                Type targetMaterialType = PropertyPathWriter.ListElementType(targetMaterials);
                if (targetMaterialType == null || targetMaterialType == typeof(object))
                    throw new InvalidOperationException("cannot determine MVDB material record type");

                for (int i = 0; i < sourceMaterials.Count; i++)
                {
                    try
                    {
                        targetMaterials.Add(BuildMaterialRecord(targetMaterialType, sourceMaterials[i], meshEntry,
                            donorMaterials == null ? null : donorMaterials[Math.Min(i, donorMaterials.Count - 1)]));
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new InvalidOperationException(ex.Message + " (material " + i + " of " + sourceMaterials.Count + " on " + meshEntry.Name + ")", ex);
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("mvdb add: build entry failed: " + ex.Message); return 1; }

            int materialCount = ((IList)GetMember(newEntry, "Materials")).Count;
            if (FrostyCLI.Core.DryRun.Active)
            {
                FrostyCLI.Core.DryRun.Report(logger, "mvdb add mesh {0} to {1} ({2} materials, variation hash {3})", mesh, path, materialCount, variationHash);
                return 0;
            }

            AddDependencies(asset, meshEntry.Guid, newEntry);
            int newIndex = entries.Add(newEntry);
            if (!EbxWriteCommand.CallModifyEbx(path, asset, logger))
            {
                entries.RemoveAt(newIndex);
                return 1;
            }
            formatter.WriteResult("OK", "mvdb add", new Dictionary<string, string>
            {
                { "ebx", path }, { "mesh", mesh }, { "index", newIndex.ToString(CultureInfo.InvariantCulture) },
                { "materials", materialCount.ToString(CultureInfo.InvariantCulture) },
                { "variation_hash", variationHash.ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }

        // `mvdb copy <db> --mesh <mesh> --from <db>` clones a mesh's entry from another database
        // record by record, keeping the shipped MaterialIds, shader ids and texture parameters. It is
        // the route for meshes `mvdb add` cannot build from the mesh alone (materials bound through an
        // internal ShaderInstance rather than an external shader), which is how a shipped prop from
        // another level gets registered in this one.
        public static int ExecuteCopy(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string path = null, mesh = null, from = null, varHash = null, fromVarHash = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mesh" && i + 1 < args.Length) mesh = args[++i];
                else if (args[i] == "--from" && i + 1 < args.Length) from = args[++i];
                else if (args[i] == "--variation-hash" && i + 1 < args.Length) varHash = args[++i];
                else if (args[i] == "--from-variation" && i + 1 < args.Length) fromVarHash = args[++i];
                else if (!args[i].StartsWith("--")) path = args[i];
            }
            if (path == null) { Console.Error.WriteLine("mvdb copy: missing <path>"); return 1; }
            if (mesh == null) { Console.Error.WriteLine("mvdb copy: missing --mesh"); return 1; }
            if (from == null) { Console.Error.WriteLine("mvdb copy: missing --from <db>"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("mvdb copy: no project loaded."); return 1; }
            uint variationHash;
            if (!TryParseVariationHash(varHash, out variationHash)) { Console.Error.WriteLine("mvdb copy: bad --variation-hash"); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
            if (entry == null) { Console.Error.WriteLine("mvdb copy: not found: " + path); return 1; }
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(mesh);
            if (meshEntry == null) { Console.Error.WriteLine("mvdb copy: mesh not found: " + mesh); return 1; }
            EbxAssetEntry fromEntry = App.AssetManager.GetEbxEntry(from);
            if (fromEntry == null) { Console.Error.WriteLine("mvdb copy: source database not found: " + from); return 1; }
            // --from-variation: take the donor from a DIFFERENT variation (typically 0, the base
            // entry) and re-stamp the copy with --variation-hash. Without it a new variation can
            // only be built by mvdb add, which cannot derive MaterialId, and an entry missing that
            // renders the mesh invisible.
            uint sourceHash = variationHash;
            if (fromVarHash != null && !TryParseVariationHash(fromVarHash, out sourceHash))
            { Console.Error.WriteLine("mvdb copy: bad --from-variation"); return 1; }
            string err = CopyEntry(entry, fromEntry, meshEntry, variationHash, sourceHash, logger, out int materials);
            if (err != null) { Console.Error.WriteLine("mvdb copy: " + err); return 1; }
            formatter.WriteResult("OK", "mvdb copy", new Dictionary<string, string>
            {
                { "ebx", path }, { "mesh", mesh }, { "from", from }, { "materials", materials.ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }

        internal static string CopyEntry(EbxAssetEntry targetDb, EbxAssetEntry sourceDb, EbxAssetEntry meshEntry, uint variationHash, ExportLogger logger, out int materials)
        {
            return CopyEntry(targetDb, sourceDb, meshEntry, variationHash, variationHash, logger, out materials);
        }

        internal static string CopyEntry(EbxAssetEntry targetDb, EbxAssetEntry sourceDb, EbxAssetEntry meshEntry, uint variationHash, uint sourceHash, ExportLogger logger, out int materials)
        {
            materials = 0;
            EbxAsset target, source;
            try { target = App.AssetManager.GetEbx(targetDb); source = App.AssetManager.GetEbx(sourceDb); }
            catch (Exception ex) { return "load failed: " + ex.Message; }
            IList entries = FindEntries(target.RootObject);
            IList sourceEntries = FindEntries(source.RootObject);
            if (entries == null || sourceEntries == null) return "no entry list found";
            if (EntryExists(entries, meshEntry.Guid, variationHash)) return "an entry already exists for mesh " + meshEntry.Name + " in " + targetDb.Name;
            object donor = null;
            foreach (object e in sourceEntries)
            {
                Guid fg, cg;
                if (!TryGetExternalReference(GetMember(e, "Mesh"), out fg, out cg) || fg != meshEntry.Guid) continue;
                object vh = GetMember(e, "VariationAssetNameHash");
                if (vh != null && Convert.ToUInt32(vh) != sourceHash) continue;
                donor = e; break;
            }
            if (donor == null) return sourceDb.Name + " has no entry for " + meshEntry.Name + " (variation " + variationHash + ")";
            object copy;
            try { copy = DeepCopy(donor); }
            catch (Exception ex) { return "could not copy the entry: " + ex.Message; }
            // re-stamp the copy so a base entry becomes the requested variation
            try { SetMember(copy, "VariationAssetNameHash", variationHash); } catch { }
            materials = (GetMember(copy, "Materials") as IList ?? new List<object>()).Count;
            if (DryRun.Active) { DryRun.Report(logger, "mvdb copy entry for {0} from {1} to {2} ({3} materials)", meshEntry.Name, sourceDb.Name, targetDb.Name, materials); return null; }
            AddDependencies(target, meshEntry.Guid, copy);
            int index = entries.Add(copy);
            if (!EbxWriteCommand.CallModifyEbx(targetDb.Name, target, logger)) { entries.RemoveAt(index); return "write failed"; }
            return null;
        }

        // Reflection deep copy for EBX value graphs: structs and classes are re-created member by
        // member, lists element by element; PointerRefs, GUIDs, strings and primitives are copied as
        // values (external references carry no object identity of their own).
        internal static object DeepCopyValue(object value) => DeepCopy(value);

        private static object DeepCopy(object value)
        {
            if (value == null) return null;
            Type t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is string || value is Guid || value is decimal) return value;
            if (t.Name == "PointerRef" || t.Name == "CString" || t.Name == "Sha1" || t.Name == "ResourceRef") return value;
            if (value is IList)
            {
                IList list = (IList)value;
                IList copy = (IList)Activator.CreateInstance(t);
                foreach (object item in list) copy.Add(DeepCopy(item));
                return copy;
            }
            object obj = Activator.CreateInstance(t);
            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                pi.SetValue(obj, DeepCopy(pi.GetValue(value)));
            }
            foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                fi.SetValue(obj, DeepCopy(fi.GetValue(value)));
            return obj;
        }

        public static int ExecuteRemove(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string path = null, mesh = null;
            bool orphans = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mesh" && i + 1 < args.Length) mesh = args[++i];
                else if (args[i] == "--orphans") orphans = true;
                else if (!args[i].StartsWith("--")) path = args[i];
            }
            if (mesh == "*orphans*") { orphans = true; mesh = null; }
            if (path == null) { Console.Error.WriteLine("mvdb remove: missing <path>"); return 1; }
            if (mesh == null && !orphans) { Console.Error.WriteLine("mvdb remove: missing --mesh (or --orphans)"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("mvdb remove: no project loaded."); return 1; }
            if (orphans) return RemoveOrphans(path, logger, formatter);

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
            if (entry == null) { Console.Error.WriteLine("mvdb remove: not found: " + path); return 1; }
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(mesh);
            if (meshEntry == null) { Console.Error.WriteLine("mvdb remove: mesh not found: " + mesh); return 1; }
            Guid meshGuid = meshEntry.Guid;

            EbxAsset asset; try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("mvdb remove: load failed: " + ex.Message); return 1; }
            IList entries = FindEntries(asset.RootObject);
            if (entries == null) { Console.Error.WriteLine("mvdb remove: no entry list found"); return 1; }

            int found = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] == null) continue;
                if (EntryReferencesMesh(entries[i], meshGuid)) { found = i; break; }
            }
            if (found < 0) { Console.Error.WriteLine("mvdb remove: no entry references mesh " + mesh); return 1; }
            if (FrostyCLI.Core.DryRun.Active) { FrostyCLI.Core.DryRun.Report(logger, "mvdb remove mesh {0} from {1}", mesh, path); return 0; }
            entries.RemoveAt(found);
            return EbxWriteCommand.CallModifyEbx(path, asset, logger) ? 0 : 1;
        }

        internal static bool TryParseVariationHash(string value, out uint hash)
        {
            hash = 0;
            if (string.IsNullOrWhiteSpace(value)) return true;
            string text = value.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string hex = text.Substring(2);
                return hex.Length > 0 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash);
            }
            return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out hash);
        }

        // Returns every material record on the first entry registering meshGuid, in order; null when
        // no such entry exists. A clone needs the donor's complete MVDB material record: MaterialId,
        // shader identity and texture parameters are a coherent runtime unit.
        private static List<object> CollectMaterialRecords(IList entries, Guid meshGuid)
        {
            foreach (object entry in entries)
            {
                if (entry == null || !EntryReferencesMesh(entry, meshGuid)) continue;
                IList materials = GetMember(entry, "Materials") as IList;
                if (materials == null) continue;
                var records = new List<object>();
                foreach (object material in materials) records.Add(material);
                return records;
            }
            return null;
        }

        private static object BuildMaterialRecord(Type recordType, object materialRef, EbxAssetEntry meshEntry,
            object donorMaterial)
        {
            object material = GetMember(materialRef, "Internal");
            if (material == null && materialRef != null && materialRef.GetType().Name.IndexOf("MeshMaterial", StringComparison.OrdinalIgnoreCase) >= 0)
                material = materialRef;
            if (material == null)
                throw new InvalidOperationException("mesh Materials contains a non-internal material reference");

            Guid materialClassGuid = ResolveClassGuid(material);
            if (materialClassGuid == Guid.Empty)
                throw new InvalidOperationException("mesh material has no exported class GUID");

            object shaderData = GetMember(material, "Shader");
            Guid shaderFileGuid = Guid.Empty;
            EbxAssetEntry shaderEntry = null;
            if (donorMaterial == null)
            {
                if (shaderData == null)
                    throw new InvalidOperationException("mesh material has no Shader data");
                object shaderRef = GetMember(shaderData, "Shader");
                Guid shaderClassGuid;
                if (!TryGetExternalReference(shaderRef, out shaderFileGuid, out shaderClassGuid))
                    throw new InvalidOperationException("mesh material shader is not an external reference");
                shaderEntry = App.AssetManager.GetEbxEntry(shaderFileGuid);
                if (shaderEntry == null)
                    throw new InvalidOperationException("surface shader asset not found: " + shaderFileGuid);
            }

            object record = Activator.CreateInstance(recordType);
            EbxRefWriter.SetRefByPath(record, "Material", meshEntry.Guid, materialClassGuid);
            // MaterialId 0 is what shipped map mods (e.g. Bioniip Laboratories, 108 ported meshes) carry on
            // every added entry; copying one donor id onto many clones puts the same id on many meshes.
            SetMember(record, "MaterialId", donorMaterial != null && !s_zeroMaterialId ? GetMember(donorMaterial, "MaterialId") : (object)0L);
            SetMember(record, "SurfaceShaderGuid", donorMaterial != null ? GetMember(donorMaterial, "SurfaceShaderGuid") : (object)shaderFileGuid);
            SetMember(record, "SurfaceShaderId", donorMaterial != null ? GetMember(donorMaterial, "SurfaceShaderId") : (object)unchecked((uint)Utils.HashString(shaderEntry.Name.ToLowerInvariant())));

            IList sourceTextures = GetMember(donorMaterial != null && !s_likeKeepTextures ? donorMaterial : shaderData, "TextureParameters") as IList;
            IList targetTextures = GetMember(record, "TextureParameters") as IList;
            if (sourceTextures == null || targetTextures == null)
                throw new InvalidOperationException("material texture parameter list is unavailable");
            foreach (object texture in sourceTextures) targetTextures.Add(texture);
            return record;
        }

        private static void AddDependencies(EbxAsset asset, Guid meshGuid, object newEntry)
        {
            asset.AddDependency(meshGuid);
            IList materials = GetMember(newEntry, "Materials") as IList;
            if (materials == null) return;
            foreach (object material in materials)
            {
                object shaderGuid = GetMember(material, "SurfaceShaderGuid");
                if (shaderGuid is Guid && (Guid)shaderGuid != Guid.Empty) asset.AddDependency((Guid)shaderGuid);
                IList textures = GetMember(material, "TextureParameters") as IList;
                if (textures == null) continue;
                foreach (object texture in textures)
                {
                    Guid fileGuid, classGuid;
                    if (TryGetExternalReference(GetMember(texture, "Value"), out fileGuid, out classGuid))
                        asset.AddDependency(fileGuid);
                }
            }
        }

        // True when any entry in the database at mvdbPath already registers meshGuid, whatever its
        // variation hash. Used by 'level place' to make its mvdb step idempotent.
        internal static bool DatabaseHasMesh(string mvdbPath, Guid meshGuid)
        {
            try
            {
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(mvdbPath);
                if (entry == null) return false;
                IList entries = FindEntries(App.AssetManager.GetEbx(entry).RootObject);
                if (entries == null) return false;
                foreach (object e in entries)
                    if (e != null && EntryReferencesMesh(e, meshGuid)) return true;
            }
            catch { }
            return false;
        }

        // The texture assets a database's entry (any variation) binds to a mesh's materials. A shipped
        // prop's textures are referenced from here, not from the mesh, so bundling the mesh's own
        // dependency closure never reaches them.
        internal static List<EbxAssetEntry> EntryTextures(EbxAssetEntry db, Guid meshGuid)
        {
            var result = new List<EbxAssetEntry>();
            var seen = new HashSet<Guid>();
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(db); } catch { return result; }
            IList entries = FindEntries(asset.RootObject);
            if (entries == null) return result;
            foreach (object e in entries)
            {
                Guid fg, cg;
                if (!TryGetExternalReference(GetMember(e, "Mesh"), out fg, out cg) || fg != meshGuid) continue;
                IList materials = GetMember(e, "Materials") as IList;
                if (materials == null) continue;
                foreach (object m in materials)
                {
                    IList textures = GetMember(m, "TextureParameters") as IList;
                    if (textures == null) continue;
                    foreach (object t in textures)
                    {
                        Guid tf, tc;
                        if (!TryGetExternalReference(GetMember(t, "Value"), out tf, out tc) || !seen.Add(tf)) continue;
                        EbxAssetEntry tex = App.AssetManager.GetEbxEntry(tf);
                        if (tex != null) result.Add(tex);
                    }
                }
            }
            return result;
        }

        internal static bool EntryExists(IList entries, Guid meshGuid, uint variationHash)
        {
            foreach (object entry in entries)
            {
                if (entry == null || !EntryReferencesMesh(entry, meshGuid)) continue;
                object hash = GetMember(entry, "VariationAssetNameHash");
                if (hash != null && Convert.ToUInt32(hash, CultureInfo.InvariantCulture) == variationHash) return true;
            }
            return false;
        }

        private static Guid ResolveClassGuid(object ebxObject)
        {
            try
            {
                MethodInfo method = ebxObject.GetType().GetMethod("GetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                object instanceGuid = method == null ? GetMember(ebxObject, "__InstanceGuid") : method.Invoke(ebxObject, null);
                object exportedGuid = GetMember(instanceGuid, "ExportedGuid");
                return exportedGuid is Guid ? (Guid)exportedGuid : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        private static bool TryGetExternalReference(object pointerRef, out Guid fileGuid, out Guid classGuid)
        {
            fileGuid = Guid.Empty;
            classGuid = Guid.Empty;
            object external = GetMember(pointerRef, "External");
            if (external == null) return false;
            object fg = GetMember(external, "FileGuid");
            object cg = GetMember(external, "ClassGuid");
            if (!(fg is Guid) || (Guid)fg == Guid.Empty) return false;
            fileGuid = (Guid)fg;
            if (cg is Guid) classGuid = (Guid)cg;
            return true;
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            PropertyInfo property = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(obj);
            FieldInfo field = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(obj);
        }

        private static void SetMember(object obj, string name, object value)
        {
            PropertyInfo property = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) { property.SetValue(obj, value); return; }
            FieldInfo field = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) { field.SetValue(obj, value); return; }
            throw new InvalidOperationException("member '" + name + "' not found on " + obj.GetType().Name);
        }

        // mvdb remove --orphans: entries whose Mesh reference points at a file guid with no EBX asset behind it. They are
        // left behind when a ported mesh is deleted (ebx-delete) so it can be re-imported cleanly.
        private static int RemoveOrphans(string path, ExportLogger logger, OutputFormatter formatter)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
            if (entry == null) { Console.Error.WriteLine("mvdb remove: not found: " + path); return 1; }
            EbxAsset asset; try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("mvdb remove: load failed: " + ex.Message); return 1; }
            IList entries = FindEntries(asset.RootObject);
            if (entries == null) { Console.Error.WriteLine("mvdb remove: no entry list found"); return 1; }

            var removed = new List<string>();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                object e = entries[i];
                if (e == null) continue;
                Guid meshGuid = EntryMeshFileGuid(e);
                if (meshGuid == Guid.Empty || App.AssetManager.GetEbxEntry(meshGuid) != null) continue;
                removed.Add("#" + i + " (mesh file guid " + meshGuid + ")");
                if (!FrostyCLI.Core.DryRun.Active) entries.RemoveAt(i);
            }
            if (removed.Count == 0)
            {
                formatter.WriteResult("OK", "mvdb remove", new Dictionary<string, string> { { "ebx", path }, { "orphans", "0" } });
                return 0;
            }
            removed.Reverse();
            if (FrostyCLI.Core.DryRun.Active) { FrostyCLI.Core.DryRun.Report(logger, "mvdb remove {0} orphaned entr(ies) from {1}", removed.Count, path); return 0; }
            if (!EbxWriteCommand.CallModifyEbx(path, asset, logger)) return 1;
            formatter.WriteResult("OK", "mvdb remove", new Dictionary<string, string>
            {
                { "ebx", path }, { "orphans", removed.Count.ToString(CultureInfo.InvariantCulture) }, { "removed", string.Join(", ", removed) }
            });
            return 0;
        }

        // The file guid an entry's Mesh reference points at (Guid.Empty when it has none).
        private static Guid EntryMeshFileGuid(object entry)
        {
            PropertyInfo p = entry.GetType().GetProperty("Mesh", BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return Guid.Empty;
            object pr; try { pr = p.GetValue(entry); } catch { return Guid.Empty; }
            if (pr == null) return Guid.Empty;
            object ext = pr.GetType().GetProperty("External")?.GetValue(pr)
                      ?? pr.GetType().GetField("External")?.GetValue(pr);
            if (ext == null) return Guid.Empty;
            object fg = ext.GetType().GetProperty("FileGuid")?.GetValue(ext)
                     ?? ext.GetType().GetField("FileGuid")?.GetValue(ext);
            return fg is Guid ? (Guid)fg : Guid.Empty;
        }

        private static bool EntryReferencesMesh(object entry, Guid meshGuid)
        {
            foreach (PropertyInfo p in entry.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType.Name != "PointerRef") continue;
                object pr; try { pr = p.GetValue(entry); } catch { continue; }
                if (pr == null) continue;
                object ext = pr.GetType().GetProperty("External")?.GetValue(pr)
                          ?? pr.GetType().GetField("External")?.GetValue(pr);
                if (ext == null) continue;
                object fg = ext.GetType().GetProperty("FileGuid")?.GetValue(ext)
                         ?? ext.GetType().GetField("FileGuid")?.GetValue(ext);
                if (fg is Guid && (Guid)fg == meshGuid) return true;
            }
            return false;
        }
    }
}
