using System;
using System.IO;
using Frosty.Core;
using FrostySdk.Interfaces;
using FrostySdk.Managers;
using MeshSetPlugin;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    /// <summary>
    /// Headless driver for MeshSetPlugin's FBXImporter — replaces an existing
    /// MeshAsset's geometry from an FBX file. Mirrors HeadlessMeshAssetDefinition's
    /// EBX→Res→MeshSet resolution, then calls FBXImporter.ImportFBX, which performs
    /// ModifyRes + LinkAsset internally.
    ///
    /// v1 supports rigid/composite meshes only (skinned meshes need a skeleton and
    /// are rejected). The FBX must structurally match the target (LOD count and
    /// section names) — produce it by exporting the target mesh and editing it.
    /// </summary>
    public static class HeadlessMeshImporter
    {
        /// <summary>
        /// Replace the geometry of <paramref name="entry"/> from the FBX at <paramref name="filePath"/>.
        /// Returns null on success, otherwise an error message.
        /// </summary>
        public static string Import(EbxAssetEntry entry, string filePath, float scale, ILogger logger, string skeleton = "")
        {
            try
            {
                if (App.AssetManager == null) return "AssetManager not initialized.";
                if (entry == null)            return "no target asset entry.";
                if (!File.Exists(filePath))   return "file not found: " + filePath;

                var asset = App.AssetManager.GetEbx(entry);
                if (asset == null) return "Failed to load EBX: " + entry.Name;

                ulong resRid;
                try { resRid = (ulong)((dynamic)asset.RootObject).MeshSetResource; }
                catch (Exception ex) { return "Could not read MeshSetResource RID from " + entry.Name + ": " + ex.Message; }
                if (resRid == 0) return "MeshSetResource is 0 for " + entry.Name + " -- not a mesh asset, or no geometry data.";

                ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
                if (resEntry == null) return "ResAssetEntry not found for RID 0x" + resRid.ToString("X16");

                MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
                if (meshSet == null) return "Failed to load MeshSet for " + entry.Name;

                if (meshSet.Type == MeshType.MeshType_Skinned && string.IsNullOrEmpty(skeleton))
                    return "'" + entry.Name + "' is a skinned mesh; pass --skeleton <skeleton-ebx> to import it.";

                var importer = new FBXImporter(logger ?? new SilentLogger());
                if (scale > 0f) importer.Scale = scale;

                // SkeletonAsset must name the skeleton EBX for skinned meshes; "" for rigid/composite.
                var settings = new FrostyMeshImportSettings { SkeletonAsset = skeleton ?? "" };

                // ImportFBX parses the FBX, rebuilds the MeshSet geometry, and calls
                // App.AssetManager.ModifyRes + entry.LinkAsset itself. It throws on a
                // structural mismatch (wrong LOD count / no meshes at a LOD).
                importer.ImportFBX(filePath, meshSet, asset, entry, settings);

                return null;
            }
            catch (Exception ex)
            {
                // The plugin's messages are terse ("Object reference not set..."); name the frame that
                // threw so template mismatches can be told apart from FBX problems.
                string where = null;
                try
                {
                    var trace = new System.Diagnostics.StackTrace(ex, false);
                    for (int i = 0; i < trace.FrameCount && where == null; i++)
                    {
                        var m = trace.GetFrame(i).GetMethod();
                        if (m != null && m.DeclaringType != null && m.DeclaringType.Namespace != null && m.DeclaringType.Namespace.StartsWith("MeshSetPlugin"))
                            where = m.DeclaringType.Name + "." + m.Name;
                    }
                }
                catch { }
                return ex.Message + (where != null ? " (in " + where + ")" : "");
            }
        }

        private sealed class SilentLogger : ILogger
        {
            public void Log(string text, params object[] vars) { }
            public void LogWarning(string text, params object[] vars) { }
            public void LogError(string text, params object[] vars) { }
        }
    }
}
