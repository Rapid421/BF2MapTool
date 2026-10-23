using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace FrostyCLI.Core.Animation
{
    // Reads the ordered bone-name list from a Frostbite SkeletonAsset EBX (default the SWBF2
    // humanoid rig FAAE binds against). The names and their order define the SEAnim bone tracks;
    // FAAE matches them case-insensitively against the same skeleton's BoneNames array.
    public static class AnimSkeleton
    {
        public const string DefaultSkeletonPath = "Characters/Rigs/Humanoids/Walrus_HumanMale";

        // Returns the skeleton's bone names in EBX order. Throws with a clear message if the
        // asset can't be found or read.
        public static List<string> ReadBoneNames(string skeletonPath)
        {
            if (App.AssetManager == null)
                throw new InvalidOperationException("AssetManager not initialized.");
            if (string.IsNullOrEmpty(skeletonPath))
                skeletonPath = DefaultSkeletonPath;

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(skeletonPath);
            if (entry == null)
            {
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                {
                    if (e.Name.IndexOf(skeletonPath, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        entry = e;
                        break;
                    }
                }
            }
            if (entry == null)
                throw new Exception("Skeleton EBX not found: " + skeletonPath);

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            if (asset == null)
                throw new Exception("Failed to load skeleton EBX: " + entry.Name);

            IList boneNames = ReadListProp(asset.RootObject, "BoneNames");
            if (boneNames == null || boneNames.Count == 0)
                throw new Exception("Skeleton '" + entry.Name + "' has no BoneNames.");

            var names = new List<string>(boneNames.Count);
            for (int i = 0; i < boneNames.Count; i++)
                names.Add(ExtractBoneName(boneNames[i]));
            return names;
        }

        // A bone's rest local transform from the skeleton's EBX LocalPose (Y-up, meters).
        public struct BindLocal { public SeVec3 Trans; public SeQuat Rot; }

        // Reads the EBX LocalPose per bone (parallel to BoneNames) as rest local transforms.
        // Used to express exported animation in the EBX LocalPose frame.
        public static Dictionary<string, BindLocal> ReadBindPose(string skeletonPath)
        {
            if (App.AssetManager == null) throw new InvalidOperationException("AssetManager not initialized.");
            if (string.IsNullOrEmpty(skeletonPath)) skeletonPath = DefaultSkeletonPath;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(skeletonPath);
            if (entry == null) throw new Exception("Skeleton EBX not found: " + skeletonPath);
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            IList names = ReadListProp(asset.RootObject, "BoneNames");
            IList lp = ReadListProp(asset.RootObject, "LocalPose");
            var map = new Dictionary<string, BindLocal>(StringComparer.OrdinalIgnoreCase);
            if (names == null || lp == null) return map;
            for (int i = 0; i < names.Count && i < lp.Count; i++)
            {
                string name = ExtractBoneName(names[i]);
                float[] r = Axis(lp[i], "right"), u = Axis(lp[i], "up"), f = Axis(lp[i], "forward"), t = Axis(lp[i], "trans");
                map[name] = new BindLocal
                {
                    Trans = new SeVec3(t[0], t[1], t[2]),
                    Rot = MatrixRowsToQuat(r, u, f),
                };
            }
            return map;
        }

        private static float[] Axis(object lt, string field)
        {
            try
            {
                dynamic d = lt;
                object v = field == "right" ? d.right : field == "up" ? d.up : field == "forward" ? d.forward : d.trans;
                if (v == null) return new float[3];
                dynamic dv = v;
                return new[] { (float)dv.x, (float)dv.y, (float)dv.z };
            }
            catch { return new float[3]; }
        }

        // Row-basis (right/up/forward as matrix rows) -> unit quaternion.
        private static SeQuat MatrixRowsToQuat(float[] r, float[] u, float[] f)
        {
            float m00 = r[0], m01 = r[1], m02 = r[2];
            float m10 = u[0], m11 = u[1], m12 = u[2];
            float m20 = f[0], m21 = f[1], m22 = f[2];
            float tr = m00 + m11 + m22; float x, y, z, w;
            if (tr > 0) { float s = (float)Math.Sqrt(tr + 1) * 2; w = s / 4; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s; }
            else if (m00 > m11 && m00 > m22) { float s = (float)Math.Sqrt(1 + m00 - m11 - m22) * 2; w = (m21 - m12) / s; x = s / 4; y = (m01 + m10) / s; z = (m02 + m20) / s; }
            else if (m11 > m22) { float s = (float)Math.Sqrt(1 + m11 - m00 - m22) * 2; w = (m02 - m20) / s; x = (m01 + m10) / s; y = s / 4; z = (m12 + m21) / s; }
            else { float s = (float)Math.Sqrt(1 + m22 - m00 - m11) * 2; w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = s / 4; }
            float len = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (len < 1e-8f) return SeQuat.Identity;
            return new SeQuat(x / len, y / len, z / len, w / len);
        }

        private static IList ReadListProp(object obj, string propName)
        {
            try
            {
                dynamic d = obj;
                if (propName == "BoneNames") return d.BoneNames as IList;
            }
            catch { }
            try
            {
                PropertyInfo pi = obj.GetType().GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (pi != null) return pi.GetValue(obj) as IList;
            }
            catch { }
            return null;
        }

        // Frosty CString.ToString() returns the string value directly; fall back to a .Value property.
        private static string ExtractBoneName(object item)
        {
            if (item == null) return string.Empty;
            string s = item.ToString();
            if (!string.IsNullOrEmpty(s) && s != item.GetType().FullName)
                return s;
            try
            {
                PropertyInfo pi = item.GetType().GetProperty("Value",
                    BindingFlags.Public | BindingFlags.Instance);
                if (pi != null) return pi.GetValue(item)?.ToString() ?? string.Empty;
            }
            catch { }
            return s ?? string.Empty;
        }
    }
}
