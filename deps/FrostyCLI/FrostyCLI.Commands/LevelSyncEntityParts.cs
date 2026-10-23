using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `level sync-entity-parts &lt;blueprint&gt;...`: make a static-model entity blueprint's physics component describe
    /// as many parts as its physics resource holds.
    ///
    /// The body walks the parts its resource declares (the HavokPhysicsData reloc header's leading u32 partCount) and
    /// indexes the physics component's Parts and each body's PartIndices with them. Ported 2015 props (port import on a
    /// one-part template) kept 2015's multi-part resource and PhysicsPartInfos (UplinkBase_01 5, SnowSpeeder_Crashed_01
    /// 10, LightGround_02 / BoxMedium_02C / BoxSmall_03C 2) but the template's ONE Parts entry and PartIndices [0]: the
    /// first damage crashed the server at game+0x601CD1 (loop index 1), HOTH_J4 47 s into a fight. Trimming
    /// PhysicsPartInfos to one instead (tried in HOTH_J6) crashed within 5 s: the 5-part body then overran a 1-entry table.
    /// Shipped props agree on every count (Box_M_01_A: resource 3, Parts 3, PartIndices [0,1,2], PhysicsPartInfos 3).
    ///
    /// Each added Parts entry copies entry 0 (zero AABB, same TransformNode = the part component) -- the right wiring
    /// when every PhysicsPartInfo names part component 0, as all five ports do; the command refuses otherwise.
    /// </summary>
    public static class LevelSyncEntityParts
    {
        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            var blueprints = new List<string>();
            foreach (string a in args) if (!a.StartsWith("--")) blueprints.Add(a);
            if (blueprints.Count == 0) { Console.Error.WriteLine("Usage: frostycli level sync-entity-parts <blueprint>..."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level sync-entity-parts: AssetManager not initialized."); return 1; }
            int changed = 0;
            foreach (string bp in blueprints)
            {
                string err = Sync(bp, logger, out bool did, out string detail);
                if (err != null) { Console.Error.WriteLine("level sync-entity-parts: " + bp + ": " + err); return 1; }
                logger.Log("level sync-entity-parts: {0}: {1}", bp, detail);
                if (did) changed++;
            }
            formatter.WriteResult("OK", "level sync-entity-parts", new Dictionary<string, string>
            { { "blueprints", blueprints.Count.ToString(CultureInfo.InvariantCulture) }, { "changed", changed.ToString(CultureInfo.InvariantCulture) } });
            return 0;
        }

        /// <summary>The part count a HavokPhysicsData resource declares (reloc header, first u32).</summary>
        internal static int ResourcePartCount(byte[] res) => res != null && res.Length >= 4 ? (int)BitConverter.ToUInt32(res, 0) : -1;

        static string Sync(string path, ExportLogger logger, out bool did, out string detail)
        {
            did = false; detail = null;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
            if (entry == null) return "not found";
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            object ent = LevelCommand.ResolveInternal(LevelCommand.ReadMember(asset.RootObject, "Object"));
            if (ent == null || ent.GetType().Name != "StaticModelEntityData") return "not a StaticModelEntityData blueprint";
            object comp = null;
            foreach (object c in LevelCommand.ReadList(ent, "Components") ?? new ArrayList())
            {
                object o = LevelCommand.ResolveInternal(c) ?? c;
                if (o != null && o.GetType().Name.EndsWith("PhysicsComponentData", StringComparison.Ordinal)) { comp = o; break; }
            }
            if (comp == null) { detail = "no physics component; left alone"; return null; }
            IList parts = LevelCommand.ReadList(comp, "Parts");
            IList bodies = LevelCommand.ReadList(comp, "PhysicsBodies");
            if (parts == null || parts.Count == 0 || bodies == null || bodies.Count == 0) return "physics component without Parts / PhysicsBodies";

            // the resource behind the first body's HavokAsset
            object body0 = LevelCommand.ResolveInternal(bodies[0]) ?? bodies[0];
            object assetRef = LevelCommand.ReadMember(body0, "Asset");
            object havok = LevelCommand.ResolveInternal(assetRef);
            if (havok == null)
            {
                object external = assetRef != null ? LevelCommand.ReadMember(assetRef, "External") : null;
                object fg = external != null ? LevelCommand.ReadMember(external, "FileGuid") : null;
                EbxAssetEntry he = fg is Guid ? App.AssetManager.GetEbxEntry((Guid)fg) : null;
                if (he != null) havok = App.AssetManager.GetEbx(he).RootObject;
            }
            if (havok == null) return "cannot resolve the physics asset";
            ulong rid = 0;
            try { rid = (ulong)((dynamic)havok).Resource; } catch { }
            ResAssetEntry re = rid != 0 ? App.AssetManager.GetResEntry(rid) : null;
            if (re == null) return "the physics asset names no resource";
            byte[] res = ReadRes(re);
            int n = ResourcePartCount(res);
            if (n <= 0 || n > 1024) return "implausible resource part count " + n;

            IList infos = LevelCommand.ReadList(ent, "PhysicsPartInfos");
            IList boxes = LevelCommand.ReadList(ent, "PartBoundingBoxes");
            int infoCount = infos != null ? infos.Count : 0, boxCount = boxes != null ? boxes.Count : 0;
            if (parts.Count == n && AllBodiesIndexed(bodies, n))
            {
                detail = string.Format("consistent ({0} part(s); PhysicsPartInfos {1}, PartBoundingBoxes {2})", n, infoCount, boxCount);
                return null;
            }
            if (parts.Count > n) return "Parts holds " + parts.Count + " for a " + n + "-part resource (not grown, not trimmed)";
            if (infos != null)
                foreach (object i in infos)
                    if (Convert.ToInt32(LevelCommand.ReadMember(i, "PartComponentIndex"), CultureInfo.InvariantCulture) != 0)
                        return "PhysicsPartInfos name part components other than 0; copying Parts[0] would mis-wire them";

            int before = parts.Count;
            object template = parts[0];
            while (parts.Count < n) parts.Add(CopyStruct(template));
            foreach (object b in bodies)
            {
                object body = LevelCommand.ResolveInternal(b) ?? b;
                IList pi = LevelCommand.ReadList(body, "PartIndices");
                if (pi == null) return "a body has no PartIndices";
                Type et = PropertyPathWriter.ListElementType(pi);
                pi.Clear();
                for (int k = 0; k < n; k++) pi.Add(Convert.ChangeType(k, et, CultureInfo.InvariantCulture));
            }
            if (!DryRun.Active && !EbxWriteCommand.CallModifyEbx(path, asset, logger)) return "could not write the blueprint";
            did = true;
            detail = string.Format("Parts {0} -> {1}, PartIndices 0..{2} on {3} bod(ies) (resource {1} parts; PhysicsPartInfos {4}, PartBoundingBoxes {5})",
                before, n, n - 1, bodies.Count, infoCount, boxCount);
            if (infoCount != n || boxCount != n)
                logger.LogWarning("level sync-entity-parts: {0}: PhysicsPartInfos {1} / PartBoundingBoxes {2} differ from the resource's {3} parts", path, infoCount, boxCount, n);
            return null;
        }

        /// <summary>For a StaticModelEntityData blueprint with a physics component: { resource part count (-1 unreadable),
        /// component Parts, PhysicsPartInfos, PartBoundingBoxes, fewest PartIndices over the bodies }; null otherwise.</summary>
        internal static int[] Counts(EbxAsset asset)
        {
            object ent = LevelCommand.ResolveInternal(LevelCommand.ReadMember(asset.RootObject, "Object"));
            if (ent == null || ent.GetType().Name != "StaticModelEntityData") return null;
            object comp = null;
            foreach (object c in LevelCommand.ReadList(ent, "Components") ?? new ArrayList())
            {
                object o = LevelCommand.ResolveInternal(c) ?? c;
                if (o != null && o.GetType().Name.EndsWith("PhysicsComponentData", StringComparison.Ordinal)) { comp = o; break; }
            }
            if (comp == null) return null;
            IList parts = LevelCommand.ReadList(comp, "Parts"), bodies = LevelCommand.ReadList(comp, "PhysicsBodies");
            if (parts == null || bodies == null || bodies.Count == 0) return null;
            int minIdx = int.MaxValue, resParts = -1;
            foreach (object b in bodies)
            {
                object body = LevelCommand.ResolveInternal(b) ?? b;
                IList pi = LevelCommand.ReadList(body, "PartIndices");
                minIdx = Math.Min(minIdx, pi != null ? pi.Count : 0);
                if (resParts >= 0) continue;
                try
                {
                    object assetRef = LevelCommand.ReadMember(body, "Asset");
                    object havok = LevelCommand.ResolveInternal(assetRef);
                    if (havok == null)
                    {
                        object external = assetRef != null ? LevelCommand.ReadMember(assetRef, "External") : null;
                        object fg = external != null ? LevelCommand.ReadMember(external, "FileGuid") : null;
                        EbxAssetEntry he = fg is Guid ? App.AssetManager.GetEbxEntry((Guid)fg) : null;
                        if (he != null) havok = App.AssetManager.GetEbx(he).RootObject;
                    }
                    ulong rid = havok != null ? (ulong)((dynamic)havok).Resource : 0;
                    ResAssetEntry re = rid != 0 ? App.AssetManager.GetResEntry(rid) : null;
                    if (re != null) resParts = ResourcePartCount(ReadRes(re));
                }
                catch { resParts = -1; }
            }
            IList infos = LevelCommand.ReadList(ent, "PhysicsPartInfos"), boxes = LevelCommand.ReadList(ent, "PartBoundingBoxes");
            return new[] { resParts, parts.Count, infos != null ? infos.Count : 0, boxes != null ? boxes.Count : 0, minIdx };
        }

        static bool AllBodiesIndexed(IList bodies, int n)
        {
            foreach (object b in bodies)
            {
                IList pi = LevelCommand.ReadList(LevelCommand.ResolveInternal(b) ?? b, "PartIndices");
                if (pi == null || pi.Count != n) return false;
            }
            return true;
        }

        static object CopyStruct(object source)
        {
            object copy = Activator.CreateInstance(source.GetType());
            foreach (PropertyInfo p in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0) p.SetValue(copy, p.GetValue(source));
            return copy;
        }

        static byte[] ReadRes(ResAssetEntry entry)
        {
            object raw = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) })
                ?.Invoke(App.AssetManager, new object[] { entry });
            if (raw is byte[] b) return b;
            if (raw is System.IO.Stream s)
                using (var ms = new System.IO.MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
            return null;
        }
    }
}
