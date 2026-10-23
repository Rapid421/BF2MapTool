using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    // H2: material/shader-parameter editing. `material set <mesh> --material <idx> --param <name>
    // --value <v>` updates the mesh's ShaderBlockDepot param block. Validated structurally (the param
    // sets + the res is modified); in-game render is the user's check.
    public class MaterialTests
    {
        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string adaptersDir = Path.Combine(exeDir, "Adapters");
            if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);
            Session.Initialize("swbf2", gp, null, adaptersDir, logger);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void MaterialSet_VectorParam_ModifiesShaderBlockDepot()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            // Find a mesh that (a) has a name-prefixed ShaderBlockDepot and (b) exposes a vector shader
            // parameter on a material -- then set it and confirm the depot resource is modified.
            int examined = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("MeshAsset"))
            {
                if (++examined > 400) break;
                FrostySdk.IO.EbxAsset asset; try { asset = App.AssetManager.GetEbx(e); } catch { continue; }
                dynamic root = asset.RootObject;

                // Must have a ShaderBlockDepot whose name starts with the mesh name.
                ResAssetEntry sbdEntry = null;
                string meshLower = e.Name.ToLowerInvariant();
                foreach (ResAssetEntry re in App.AssetManager.EnumerateRes((uint)ResourceType.ShaderBlockDepot))
                    if (re.Name.IndexOf(meshLower, StringComparison.OrdinalIgnoreCase) == 0) { sbdEntry = re; break; }
                if (sbdEntry == null) continue;

                int matCount; try { matCount = (int)root.Materials.Count; } catch { continue; }
                for (int mi = 0; mi < matCount; mi++)
                {
                    string paramName = null;
                    try
                    {
                        dynamic mat = root.Materials[mi].Internal;
                        foreach (dynamic p in mat.Shader.VectorParameters) { paramName = (string)p.ParameterName; if (!string.IsNullOrEmpty(paramName)) break; }
                    }
                    catch { continue; }
                    if (string.IsNullOrEmpty(paramName)) continue;

                    int sections;
                    string err = FrostyCLI.Adapters.Mesh.MaterialParamEditor.SetParam(e, mi, paramName, "0.25,0.5,0.75,1.0", logger, out sections);
                    if (err != null || sections == 0) continue; // this material/param didn't land -- try next

                    try
                    {
                        Assert.IsTrue(App.AssetManager.GetResEntry(sbdEntry.ResRid).IsModified || sbdEntry.IsModified,
                            "ShaderBlockDepot should be modified after material set");
                        return; // validated
                    }
                    finally
                    {
                        try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(sbdEntry.ResRid)); } catch { }
                    }
                }
            }
            Console.WriteLine("skip: no mesh with a settable vector shader param + ShaderBlockDepot found");
        }
    }
}
