using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostyCLI.Core;
using FrostyCLI.Core.Animation;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class ExportSkinMeshTests
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

        private static bool Swbf2Ready(ExportLogger logger)
        {
            EnsureSession(logger);
            return App.AssetManager != null && App.AssetManager.GetEbxEntry(AnimSkeleton.DefaultSkeletonPath) != null;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ExportSkinMesh_WritesValidFaeMesh()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            if (!Swbf2Ready(logger)) { Console.WriteLine("  (skipped: SWBF2 not active)"); return; }

            string mesh = "characters/hero/darthvader/darthvader_01/darthvader_01_mesh";
            if (App.AssetManager.GetEbxEntry(mesh) == null) { Console.WriteLine("  (skipped: Vader mesh not present)"); return; }

            List<string> skelBones = AnimSkeleton.ReadBoneNames(AnimSkeleton.DefaultSkeletonPath);
            string outFile = Path.Combine(Path.GetTempPath(), "faemesh_" + Guid.NewGuid() + ".faemesh");
            try
            {
                int r = new ExportSkinMeshCommand().Execute(new[] { mesh, "--out", outFile }, logger, fmt);
                Assert.AreEqual(0, r);
                Assert.IsTrue(File.Exists(outFile));

                using (var br = new BinaryReader(File.OpenRead(outFile)))
                {
                    string magic = Encoding.ASCII.GetString(br.ReadBytes(8));
                    Assert.AreEqual("FAEMESH1", magic);

                    int boneCount = (int)br.ReadUInt32();
                    Assert.AreEqual(skelBones.Count, boneCount);
                    var names = new List<string>();
                    for (int i = 0; i < boneCount; i++) names.Add(ReadCStr(br));
                    for (int i = 0; i < boneCount; i++) Assert.AreEqual(skelBones[i], names[i]);

                    int vtxCount = (int)br.ReadUInt32();
                    Assert.IsTrue(vtxCount > 100, "too few vertices: " + vtxCount);
                    int weighted = 0, nonzeroNormal = 0, maxBone = -1;
                    for (int i = 0; i < vtxCount; i++)
                    {
                        float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
                        float nx = br.ReadSingle(), ny = br.ReadSingle(), nz = br.ReadSingle();
                        br.ReadSingle(); br.ReadSingle(); // uv
                        int[] bi = { br.ReadInt32(), br.ReadInt32(), br.ReadInt32(), br.ReadInt32() };
                        float[] bw = { br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
                        foreach (int b in bi) { Assert.IsTrue(b >= -1 && b < boneCount, "bone index out of range: " + b); if (b > maxBone) maxBone = b; }
                        float sum = bw[0] + bw[1] + bw[2] + bw[3];
                        if (sum > 0f) { Assert.IsTrue(Math.Abs(sum - 1f) < 0.01f, "weights not normalized: " + sum); weighted++; }
                        if (Math.Abs(nx) + Math.Abs(ny) + Math.Abs(nz) > 1e-3f) nonzeroNormal++;
                    }

                    int idxCount = (int)br.ReadUInt32();
                    Assert.IsTrue(idxCount > 0 && idxCount % 3 == 0, "bad index count: " + idxCount);
                    for (int i = 0; i < idxCount; i++) { uint idx = br.ReadUInt32(); Assert.IsTrue(idx < vtxCount, "index out of range"); }

                    int albLen = (int)br.ReadUInt32();
                    string albedo = albLen > 0 ? Encoding.UTF8.GetString(br.ReadBytes(albLen)) : "";

                    Console.WriteLine(string.Format("RE: bones={0} verts={1} weighted={2} nonzeroNormal={3} maxBone={4} tris={5} albedo='{6}'",
                        boneCount, vtxCount, weighted, nonzeroNormal, maxBone, idxCount / 3, albedo));

                    Assert.IsTrue(weighted > vtxCount / 2, "most vertices should be skinned, got " + weighted + "/" + vtxCount);
                }
            }
            finally { try { File.Delete(outFile); } catch { } }
        }

        private static string ReadCStr(BinaryReader br)
        {
            var sb = new StringBuilder(); byte b;
            while ((b = br.ReadByte()) != 0) sb.Append((char)b);
            return sb.ToString();
        }
    }
}
