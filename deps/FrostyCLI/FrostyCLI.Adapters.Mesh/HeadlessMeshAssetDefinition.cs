using System;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using MeshSetPlugin;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    public class HeadlessMeshAssetDefinition : AssetDefinition
    {
        /// <summary>When set, composite meshes export as rigid geometry (no PART skeleton, no skins).</summary>
        public static bool ExportCompositeAsRigid = false;

        public override bool Export(EbxAssetEntry entry, string path, string filterType)
        {
            if (App.AssetManager == null)
                throw new InvalidOperationException("AssetManager not initialized.");

            // Flat skin+texture export for FAAE (keeps the bone index/weight channels the FBX/OBJ
            // path drops). Routed via the filterType token so it reuses the existing handler lookup.
            // faemesh token may carry an explicit skeleton: "faemesh|<skeletonEbxPath>". When absent,
            // FaeMeshExporter falls back to Walrus_HumanMale.
            if ((filterType != null && filterType.StartsWith("faemesh", StringComparison.OrdinalIgnoreCase))
                || path.EndsWith(".faemesh", StringComparison.OrdinalIgnoreCase))
            {
                string skel = null;
                if (filterType != null)
                {
                    int bar = filterType.IndexOf('|');
                    if (bar >= 0) skel = filterType.Substring(bar + 1);
                }
                return FaeMeshExporter.Export(entry, path, string.IsNullOrEmpty(skel) ? null : skel);
            }

            var asset = App.AssetManager.GetEbx(entry);
            if (asset == null)
                throw new Exception("Failed to load EBX: " + entry.Name);

            dynamic mesh = asset.RootObject;

            ulong resRid;
            try { resRid = (ulong)mesh.MeshSetResource; }
            catch (Exception ex)
            {
                throw new Exception(
                    "Could not read MeshSetResource RID from " + entry.Name + ": " + ex.Message, ex);
            }

            if (resRid == 0)
                throw new Exception("MeshSetResource is 0 for " + entry.Name + " -- no geometry data.");

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            if (resEntry == null)
                throw new Exception("ResAssetEntry not found for RID 0x" + resRid.ToString("X16"));

            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
            if (meshSet == null)
                throw new Exception("Failed to load MeshSet for " + entry.Name);

            // A composite mesh exports with a PART_n skeleton and a skin per section. Frosty's FBX
            // importer only accepts such a skin into a composite target (a rigid target needs a
            // skeleton it does not have and throws inside ProcessSection). For a rigid port the parts
            // are wanted as plain geometry, so present the MeshSet as rigid to the exporter: no
            // skeleton, no skins, no part move. In-memory only; the shipped resource is untouched.
            if (ExportCompositeAsRigid && meshSet.Type == MeshType.MeshType_Composite)
            {
                meshSet.Type = MeshType.MeshType_Rigid;
                foreach (MeshSetLod lod in meshSet.Lods) lod.Type = MeshType.MeshType_Rigid;
            }

            string outDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            // Skinned meshes export EMPTY of readable geometry without a skeleton -- the FBX is written
            // but the mesh isn't bound, so Assimp reports HasMeshes=False and OBJ conversion yields 0
            // verts (beta bug #5). Auto-source a compatible SkeletonAsset (bone count covers the mesh) so
            // a plain `export <skinned-mesh>` produces a readable, riggable FBX.
            string skeleton = "";
            if (meshSet.Lods.Count > 0 && meshSet.Lods[0].Type == MeshType.MeshType_Skinned)
            {
                skeleton = FindCompatibleSkeleton(meshSet) ?? "";
                if (skeleton == "")
                    Console.Error.WriteLine("export: '" + entry.Name + "' is a skinned mesh and no compatible SkeletonAsset was found; geometry may be unreadable.");
            }

            // Honor the requested format: the exporter selects its writer by matching this token against
            // the FBX SDK writer descriptions ("FBX binary (*.fbx)" / "Alias OBJ (*.obj)"). Passing
            // "binary" for an .obj output made the SDK refuse to write (wrong extension) -> no file.
            bool wantObj = string.Equals(filterType, "obj", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase);
            string fileType = wantObj ? "OBJ" : "binary";

            var exporter = new FBXExporter(null);
            exporter.ExportFBX(
                mesh,
                path,
                "2017",
                "Centimeters",
                inFlattenHierarchy: true,
                inExportSingleLod:  false,
                skeleton:           skeleton,
                fileType:           fileType,
                meshSets:           new[] { meshSet });

            return true;
        }

        // Finds a SkeletonAsset whose bone count covers this skinned mesh's max bone count. Caches the
        // pick per required-count so batch exports don't re-enumerate. Any covering skeleton yields
        // correct vertex positions (the bind pose lives in the mesh); exact rigging needs the mesh's own
        // skeleton, which the mesh EBX does not reference here.
        private static readonly System.Collections.Generic.Dictionary<int, string> _skeletonCache
            = new System.Collections.Generic.Dictionary<int, string>();

        private static string FindCompatibleSkeleton(MeshSet meshSet)
        {
            int need = 0;
            foreach (MeshSetLod lod in meshSet.Lods) if (lod.BoneCount > need) need = lod.BoneCount;
            lock (_skeletonCache)
            {
                if (_skeletonCache.TryGetValue(need, out string cached)) return cached;
                string best = null; int bestCount = int.MaxValue;
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("SkeletonAsset"))
                {
                    FrostySdk.IO.EbxAsset a;
                    try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                    int cnt; try { cnt = (int)((dynamic)a.RootObject).BoneNames.Count; } catch { continue; }
                    if (cnt >= need && cnt < bestCount) { best = e.Name; bestCount = cnt; } // smallest covering = closest fit
                }
                _skeletonCache[need] = best;
                return best;
            }
        }
    }
}
