using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // Gives one material of a (cloned) mesh the shader of a material on another mesh in the same
    // level: the donor's compiled permutation (MESHVAR RvmShaderRef), a salted copy of its parameter
    // and texture blocks (the section holder), the EBX shader reference + texture parameter list, and
    // the MeshVariationDatabase record's shader guid/id + texture parameters. Afterwards the textures
    // can be swapped with `material set` / ebx-set-ref by the donor's parameter names.
    internal static class MaterialGraft
    {
        public static string Execute(string meshPath, int matIdx, string donorPath, int donorMat, string mvdbPath,
            ExportLogger logger, Dictionary<string, string> info)
        {
            EbxAssetEntry me = App.AssetManager.GetEbxEntry(meshPath);
            if (me == null) return "mesh not found: " + meshPath;
            EbxAssetEntry de = App.AssetManager.GetEbxEntry(donorPath);
            if (de == null) return "donor mesh not found: " + donorPath;
            EbxAsset meshAsset = App.AssetManager.GetEbx(me);
            EbxAsset donorAsset = App.AssetManager.GetEbx(de);
            MeshSet ms = LoadMeshSet(meshAsset), ds = LoadMeshSet(donorAsset);
            if (ms == null) return "could not load the mesh's MeshSet";
            if (ds == null) return "could not load the donor's MeshSet";
            ResAssetEntry nb = App.AssetManager.GetResEntry(me.Name.ToLowerInvariant() + "_mesh/blocks");
            ResAssetEntry dnb = App.AssetManager.GetResEntry(de.Name.ToLowerInvariant() + "_mesh/blocks");
            if (nb == null) return "mesh has no shader block depot";
            if (dnb == null) return "donor has no shader block depot";
            ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(nb);
            ShaderBlockDepot ddepot = App.AssetManager.GetResAs<ShaderBlockDepot>(dnb);
            FieldInfo resField = typeof(ShaderBlockDepot).GetField("sbResources", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo entField = typeof(ShaderBlockDepot).GetField("sbEntries", BindingFlags.Instance | BindingFlags.NonPublic);
            var all = (List<ShaderBlockResource>)resField.GetValue(depot);
            var entries = (List<ShaderBlockEntry>)entField.GetValue(depot);
            var dall = (List<ShaderBlockResource>)resField.GetValue(ddepot);
            var dentries = (List<ShaderBlockEntry>)entField.GetValue(ddepot);
            if (entries.Count == 0 || dentries.Count == 0) return "empty depot";

            // Donor: LOD0 section using the donor material.
            int k = -1;
            for (int i = 0; i < ds.Lods[0].Sections.Count; i++) if (ds.Lods[0].Sections[i].MaterialId == donorMat) { k = i; break; }
            if (k < 0 || k >= dentries[0].Resources.Count) return "donor has no LOD0 section on material " + donorMat;
            var dsec = dentries[0].Resources[k] as ShaderStaticParamDbBlock;
            var dholder = dsec != null && dsec.Resources.Count > 1 ? dsec.Resources[1] as ShaderStaticParamDbBlock : null;
            if (dholder == null) return "donor section " + k + " has no parameter holder";
            ShaderBlockMeshVariationEntry dmv = null;
            foreach (ShaderBlockResource r in dall) if (r is ShaderBlockMeshVariationEntry && r.Hash == dentries[0].Hash) { dmv = (ShaderBlockMeshVariationEntry)r; break; }
            if (dmv == null || k >= dmv.RvmShaderRefGuids.Count) return "donor has no variation entry for LOD0";
            Guid refGuid = dmv.RvmShaderRefGuids[k]; int refInt = dmv.RvmShaderRefInts[k];

            // Copy the holder and its parameter/texture blocks, hashes salted with the mesh guid.
            // Two grafts of the same donor into one mesh must not produce identical block hashes
            // (the runtime de-duplicates blocks by hash): salt with the mesh guid AND the material index.
            byte[] salt = new byte[20];
            Array.Copy(me.Guid.ToByteArray(), salt, 16);
            Array.Copy(BitConverter.GetBytes(matIdx), 0, salt, 16, 4);
            var holder = new ShaderStaticParamDbBlock { Hash = Salt(dholder.Hash, salt), IsModified = true };
            foreach (ShaderBlockResource child in dholder.Resources)
            {
                var p = child as ShaderPersistentParamDbBlock;
                if (p == null) return "donor holder child is not a parameter block";
                var np = new ShaderPersistentParamDbBlock { Hash = Salt(p.Hash, salt), IsModified = true };
                foreach (ParameterEntry pe in p.Parameters)
                    using (var r = new NativeReader(new MemoryStream(pe.ToBytes()))) np.Parameters.Add(new ParameterEntry(r));
                holder.Resources.Add(np);
            }
            // Shipped depots never reference forward: put the new blocks in front of everything.
            int insertAt = 0;
            foreach (ShaderBlockResource c in holder.Resources) all.Insert(insertAt++, c);
            all.Insert(insertAt++, holder);

            // Rewire every section of the mesh that uses the material.
            int rewired = 0;
            for (int lod = 0; lod < ms.Lods.Count && lod < entries.Count; lod++)
            {
                ShaderBlockEntry sbe = entries[lod];
                ShaderBlockMeshVariationEntry mv = null;
                foreach (ShaderBlockResource r in all) if (r is ShaderBlockMeshVariationEntry && r.Hash == sbe.Hash) { mv = (ShaderBlockMeshVariationEntry)r; break; }
                for (int s = 0; s < ms.Lods[lod].Sections.Count && s < sbe.Resources.Count; s++)
                {
                    if (ms.Lods[lod].Sections[s].MaterialId != matIdx) continue;
                    var sec = sbe.Resources[s] as ShaderStaticParamDbBlock;
                    if (sec == null || sec.Resources.Count < 2) continue;
                    sec.Resources[1] = holder; sec.Hash = Salt(sec.Hash, salt); sec.IsModified = true;
                    if (mv != null && s < mv.RvmShaderRefGuids.Count) { mv.RvmShaderRefGuids[s] = refGuid; mv.RvmShaderRefInts[s] = refInt; mv.IsModified = true; }
                    rewired++;
                }
            }
            if (rewired == 0) return "no section of the mesh uses material " + matIdx;
            for (int i = 0; i < all.Count; i++) all[i].Index = i;
            App.AssetManager.ModifyRes(nb.ResRid, depot);

            // EBX: shader reference and texture parameter list from the donor material.
            IList mats = ReadList(meshAsset.RootObject, "Materials"), dmats = ReadList(donorAsset.RootObject, "Materials");
            if (mats == null || matIdx >= mats.Count) return "mesh has no material " + matIdx;
            if (dmats == null || donorMat >= dmats.Count) return "donor has no material " + donorMat;
            object mat = ResolveInternal(mats[matIdx]) ?? mats[matIdx];
            object dmat = ResolveInternal(dmats[donorMat]) ?? dmats[donorMat];
            object shader = GetMember(mat, "Shader"), dshader = GetMember(dmat, "Shader");
            Guid sf, sc;
            if (!TryGetExternalReference(GetMember(dshader, "Shader"), out sf, out sc)) return "donor material has no external shader reference";
            EbxRefWriter.SetRefByPath(mat, "Shader.Shader", sf, sc);
            meshAsset.AddDependency(sf);
            EbxAssetEntry shaderEntry = App.AssetManager.GetEbxEntry(sf);
            info["shader"] = shaderEntry != null ? shaderEntry.Name : sf.ToString();

            // Texture parameters: the donor's MVDB record lists them (its EBX often does not).
            EbxAssetEntry dbEntry = mvdbPath != null ? App.AssetManager.GetEbxEntry(mvdbPath) : null;
            if (mvdbPath != null && dbEntry == null) return "mvdb not found: " + mvdbPath;
            object drec = null, rec = null; EbxAsset db = null;
            if (dbEntry != null)
            {
                db = App.AssetManager.GetEbx(dbEntry);
                IList dbEntries = MvdbCommand.FindEntries(db.RootObject);
                foreach (object e in dbEntries)
                {
                    Guid fg, cg;
                    if (!TryGetExternalReference(GetMember(e, "Mesh"), out fg, out cg)) continue;
                    IList recs = ReadList(e, "Materials");
                    if (fg == de.Guid && drec == null && recs != null && donorMat < recs.Count) drec = recs[donorMat];
                    if (fg == me.Guid && rec == null && recs != null && matIdx < recs.Count) rec = recs[matIdx];
                }
                if (drec == null) return "donor mesh has no record in " + mvdbPath;
                if (rec == null) return "mesh has no record in " + mvdbPath;
            }
            IList tp = ReadList(shader, "TextureParameters");
            IList dtp = drec != null ? ReadList(drec, "TextureParameters") : ReadList(dshader, "TextureParameters");
            var names = new List<string>();
            if (tp != null && dtp != null)
            {
                tp.Clear();
                foreach (object t in dtp)
                {
                    Guid tf, tc; string name = Convert.ToString(GetMember(t, "ParameterName"));
                    object param = EbxWriteCommand.CreateObjectByType("TextureShaderParameter");
                    if (param == null) return "TextureShaderParameter type not in the type library";
                    PropertyPathWriter.SetValue(param, "ParameterName", name);
                    if (TryGetExternalReference(GetMember(t, "Value"), out tf, out tc)) { EbxRefWriter.SetRefByPath(param, "Value", tf, tc); meshAsset.AddDependency(tf); }
                    tp.Add(param); names.Add(name);
                }
            }
            if (!EbxWriteCommand.CallModifyEbx(meshPath, meshAsset, logger)) return "could not save the mesh EBX";
            if (rec != null)
            {
                SetMember(rec, "SurfaceShaderGuid", GetMember(drec, "SurfaceShaderGuid"));
                SetMember(rec, "SurfaceShaderId", GetMember(drec, "SurfaceShaderId"));
                SetMember(rec, "TextureParameters", DeepCopy(GetMember(drec, "TextureParameters")));
                if (!EbxWriteCommand.CallModifyEbx(mvdbPath, db, logger)) return "could not save the MVDB";
            }
            info["sections"] = rewired.ToString(); info["rvmRef"] = refGuid + "/" + refInt; info["textures"] = string.Join(",", names);
            return null;
        }

        private static ulong Salt(ulong hash, byte[] salt)
        {
            using (var w = new NativeWriter(new MemoryStream())) { w.Write(hash); w.Write(salt); return Frosty.Hash.CityHash.Hash64(w.ToByteArray()); }
        }
        private static MeshSet LoadMeshSet(EbxAsset asset)
        {
            try { ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource; if (rid == 0) return null; return App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid)); }
            catch { return null; }
        }
        private static object ResolveInternal(object v) { if (v == null) return null; return v.GetType().Name != "PointerRef" ? v : GetMember(v, "Internal"); }
        private static IList ReadList(object v, string n) { return GetMember(v, n) as IList; }
        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj);
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f == null ? null : f.GetValue(obj);
        }
        private static void SetMember(object obj, string name, object value)
        {
            PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) { p.SetValue(obj, value); return; }
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { f.SetValue(obj, value); return; }
            throw new InvalidOperationException("member '" + name + "' not found on " + obj.GetType().Name);
        }
        private static bool TryGetExternalReference(object pointerRef, out Guid fileGuid, out Guid classGuid)
        {
            fileGuid = Guid.Empty; classGuid = Guid.Empty;
            object external = GetMember(pointerRef, "External");
            if (external == null) return false;
            object fg = GetMember(external, "FileGuid"), cg = GetMember(external, "ClassGuid");
            if (!(fg is Guid) || (Guid)fg == Guid.Empty) return false;
            fileGuid = (Guid)fg; if (cg is Guid) classGuid = (Guid)cg; return true;
        }
        private static object DeepCopy(object value)
        {
            if (value == null) return null;
            Type t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is string || value is Guid || value is decimal) return value;
            if (t.Name == "PointerRef" || t.Name == "CString" || t.Name == "Sha1" || t.Name == "ResourceRef") return value;
            if (value is IList) { IList copy = (IList)Activator.CreateInstance(t); foreach (object item in (IList)value) copy.Add(DeepCopy(item)); return copy; }
            object obj = Activator.CreateInstance(t);
            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) { if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue; pi.SetValue(obj, DeepCopy(pi.GetValue(value))); }
            foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) fi.SetValue(obj, DeepCopy(fi.GetValue(value)));
            return obj;
        }
    }
}
