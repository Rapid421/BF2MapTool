using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace FrostyCLI.Core
{
    // A level's own copy of a terrain: the TerrainData EBX (its layers travel inside it) and the three resources it
    // points at -- TerrainStreamingTree, VisualTerrain, TerrainLayerCombinations -- under a new name, optionally with a
    // new streaming-tree payload (what `terrain create` writes). The terrain entities of the new level are then pointed
    // at the copy, so the host level keeps its own terrain. The resources ship beside the EBX under its name
    // (<name>, <name>.streamingtree_win32, <name>.layercombinations_win32), which is how `level create`'s dependency
    // closure picks them up as the asset's companions.
    public static class TerrainCloner
    {
        public static readonly string[] ResourceFields = { "TerrainStreamingTreeResource", "VisualResource", "TerrainLayerCombinationsResource" };

        public sealed class Result
        {
            public EbxAssetEntry Entry;
            public Guid RootInstanceGuid;
            public readonly List<string> Resources = new List<string>();
        }

        public static Result Clone(string sourceName, string newName, byte[] streamingTreePayload = null)
        {
            AssetManager am = App.AssetManager ?? throw new InvalidOperationException("AssetManager not initialized");
            if (am.GetEbxEntry(newName) != null) throw new InvalidOperationException("destination already exists: " + newName);
            EbxAssetEntry src = am.GetEbxEntry(sourceName) ?? throw new InvalidOperationException("terrain not found: " + sourceName);
            if (src.Type != "TerrainData") throw new InvalidOperationException(sourceName + " is a " + src.Type + ", not a TerrainData");
            EbxAsset srcAsset = am.GetEbx(src);

            EbxAsset asset = BinaryCloneEbx(srcAsset);
            asset.SetFileGuid(Guid.NewGuid());
            dynamic root = asset.RootObject;
            try { root.Name = newName; } catch { }
            var rootGuid = new AssetClassGuid(Utils.GenerateDeterministicGuid(asset.Objects, (Type)root.GetType(), asset.FileGuid), -1);
            root.SetInstanceGuid(rootGuid);

            var result = new Result { RootInstanceGuid = rootGuid.ExportedGuid };
            var added = new List<ResAssetEntry>();
            string srcLower = src.Name.ToLowerInvariant(), newLower = newName.ToLowerInvariant();
            foreach (string field in ResourceFields)
            {
                PropertyInfo pi = root.GetType().GetProperty(field);
                if (pi == null) continue;
                ulong rid = (ulong)(dynamic)pi.GetValue((object)root);
                if (rid == 0) continue;
                ResAssetEntry re = am.GetResEntry(rid) ?? throw new InvalidOperationException(field + ": res " + rid.ToString("X16") + " not found");
                // <name><suffix>: the resource keeps its suffix after the terrain's name
                string suffix = re.Name.StartsWith(srcLower, StringComparison.OrdinalIgnoreCase) ? re.Name.Substring(srcLower.Length) : "." + field.ToLowerInvariant();
                string resName = newLower + suffix;
                if (am.GetResEntry(resName) != null) throw new InvalidOperationException("resource already exists: " + resName);
                byte[] bytes = field == "TerrainStreamingTreeResource" && streamingTreePayload != null ? streamingTreePayload : GetRes(re);
                byte[] meta = (byte[])(re.ResMeta ?? new byte[16]).Clone();
                am.AddRes(resName, (ResourceType)re.ResType, meta, bytes);
                ResAssetEntry nr = am.GetResEntry(resName) ?? throw new InvalidOperationException("AddRes did not create " + resName);
                // ResourceRef converts implicitly from its rid
                MethodInfo fromRid = pi.PropertyType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ulong) }, null);
                pi.SetValue((object)root, fromRid != null ? fromRid.Invoke(null, new object[] { nr.ResRid }) : Activator.CreateInstance(pi.PropertyType, nr.ResRid));
                added.Add(nr);
                result.Resources.Add(resName + (field == "TerrainStreamingTreeResource" && streamingTreePayload != null ? " (new payload)" : ""));
            }

            EbxAssetEntry entry = am.AddEbx(newName, asset) ?? throw new InvalidOperationException("AddEbx did not create " + newName);
            foreach (ResAssetEntry r in added) { try { entry.LinkAsset(r); } catch { } }
            result.Entry = entry;
            return result;
        }

        // Every TerrainEntityData.TerrainAsset among `partitions` that points at `oldTerrain` now points at the copy.
        public static int Repoint(IEnumerable<EbxAssetEntry> partitions, EbxAssetEntry oldTerrain, Result copy, Action<string> log = null)
        {
            AssetManager am = App.AssetManager;
            int total = 0;
            foreach (EbxAssetEntry e in partitions)
            {
                EbxAsset a = am.GetEbx(e);
                int n = 0;
                foreach (object obj in a.Objects)
                {
                    if (obj == null || obj.GetType().Name != "TerrainEntityData") continue;
                    PropertyInfo pi = obj.GetType().GetProperty("TerrainAsset");
                    if (pi == null) continue;
                    var pr = (PointerRef)pi.GetValue(obj);
                    if (pr.Type != PointerRefType.External || pr.External.FileGuid != oldTerrain.Guid) continue;
                    pi.SetValue(obj, new PointerRef(new EbxImportReference { FileGuid = copy.Entry.Guid, ClassGuid = copy.RootInstanceGuid }));
                    n++;
                }
                if (n == 0) continue;
                am.ModifyEbx(e.Name, a);
                log?.Invoke(e.Name + ": " + n + " terrain entit" + (n == 1 ? "y" : "ies") + " -> " + copy.Entry.Name);
                total += n;
            }
            return total;
        }

        // The TerrainData the first TerrainEntityData among `partitions` points at.
        public static EbxAssetEntry FindTerrain(IEnumerable<EbxAssetEntry> partitions)
        {
            AssetManager am = App.AssetManager;
            foreach (EbxAssetEntry e in partitions)
            {
                EbxAsset a = am.GetEbx(e);
                foreach (object obj in a.Objects)
                {
                    if (obj == null || obj.GetType().Name != "TerrainEntityData") continue;
                    PropertyInfo pi = obj.GetType().GetProperty("TerrainAsset");
                    var pr = pi != null ? (PointerRef)pi.GetValue(obj) : default(PointerRef);
                    if (pr.Type != PointerRefType.External) continue;
                    EbxAssetEntry t = am.GetEbxEntry(pr.External.FileGuid);
                    if (t != null) return t;
                }
            }
            return null;
        }

        private static EbxAsset BinaryCloneEbx(EbxAsset source)
        {
            using (var writer = EbxBaseWriter.CreateProjectWriter(new MemoryStream(), EbxWriteFlags.DoNotSort | EbxWriteFlags.IncludeTransient))
            {
                writer.WriteAsset(source);
                byte[] buf = writer.ToByteArray();
                using (var reader = EbxReader.CreateProjectReader(new MemoryStream(buf)))
                    return reader.ReadAsset<EbxAsset>();
            }
        }

        private static byte[] GetRes(ResAssetEntry entry)
        {
            MethodInfo m = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
            object raw = m?.Invoke(App.AssetManager, new object[] { entry });
            if (raw is byte[] b) return b;
            if (raw is Stream s) { using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }
            throw new InvalidOperationException("unexpected GetRes return type");
        }
    }
}
