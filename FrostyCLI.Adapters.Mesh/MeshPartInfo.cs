using System;
using System.Collections;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    /// <summary>
    /// Part layout of a mesh asset's geometry resource. A composite mesh (MeshType_Composite) is drawn as N parts, each
    /// placed by its own part transform, and a static-group member using one must supply exactly that many physics parts
    /// per instance: DICE's own members do (Hoth_01 Content: Box_M_01_A 3, LightGround_03 3, DF9_01_Static 3,
    /// TransporterPersonel_01 9), and a 2015 member with fewer (LightGround_03 2) makes the renderer read past the member's
    /// parts right after load (game+0x6E6ED6A).
    /// </summary>
    public static class MeshPartInfo
    {
        /// <summary>Number of parts of a composite mesh; 0 for rigid/skinned meshes; -1 if it cannot be read.</summary>
        public static int CompositePartCount(EbxAssetEntry meshEntry, out string meshType)
        {
            meshType = null;
            try
            {
                if (meshEntry == null || App.AssetManager == null) return -1;
                object root = App.AssetManager.GetEbx(meshEntry).RootObject;
                ulong rid = (ulong)((dynamic)root).MeshSetResource;
                ResAssetEntry res = rid != 0 ? App.AssetManager.GetResEntry(rid) : null;
                if (res == null) return -1;
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(res);
                meshType = ms.Type.ToString();
                if (!meshType.EndsWith("Composite", StringComparison.OrdinalIgnoreCase)) return 0;
                FieldInfo f = typeof(MeshSet).GetField("m_partTransforms", BindingFlags.Instance | BindingFlags.NonPublic);
                ICollection parts = f != null ? f.GetValue(ms) as ICollection : null;
                return parts != null ? parts.Count : -1;
            }
            catch { return -1; }
        }
    }
}
