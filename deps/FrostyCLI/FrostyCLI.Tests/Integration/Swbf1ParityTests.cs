using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace FrostyCLI.Tests.Integration
{
    // SWBF1 write-command parity. Session.Initialize is once-per-process and the runner runs all
    // integration tests in one process, so an in-process [IntegrationTest("swbf1")] would run against
    // whichever game inited first (swbf2 in practice). Instead these tests shell out to the built
    // FrostyCLI.exe with --game swbf1 (a fresh process), which genuinely exercises SWBF1.
    public class Swbf1ParityTests
    {
        private static string FindCliExe()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string exe = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..",
                "FrostyCLI", "bin", "x64", "Release", "FrostyCLI.exe"));
            return File.Exists(exe) ? exe : null;
        }

        private static int RunCli(string exe, string args, out string stdout, out string stderr, int timeoutMs = 180000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var p = Process.Start(psi);
            string o = "", e = "";
            var to = new System.Threading.Thread(() => { try { o = p.StandardOutput.ReadToEnd(); } catch { } }) { IsBackground = true };
            var te = new System.Threading.Thread(() => { try { e = p.StandardError.ReadToEnd(); } catch { } }) { IsBackground = true };
            to.Start(); te.Start();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } stdout = o; stderr = e; return -1; }
            to.Join(3000); te.Join(3000);
            stdout = o; stderr = e;
            return p.ExitCode;
        }

        // Validates the SWBF1 write+build path end to end: a fresh swbf1 process runs a pipeline that
        // creates a new EBX asset and builds a .fbmod from it.
        [Test]
        [IntegrationTest("swbf1")]
        public void Swbf1_WritePipeline_CreatesAssetAndBuildsMod()
        {
            string gp1 = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF1");
            if (string.IsNullOrEmpty(gp1)) { Console.WriteLine("skip: FROSTYCLI_GAME_PATH_SWBF1 not set"); return; }
            string exe = FindCliExe();
            if (exe == null) { Console.WriteLine("skip: FrostyCLI.exe not found"); return; }

            string tag       = Guid.NewGuid().ToString("N").Substring(0, 8);
            string fbmod     = Path.Combine(Path.GetTempPath(), "swbf1_parity_" + tag + ".fbmod");
            string pipeline  = Path.Combine(Path.GetTempPath(), "swbf1_parity_" + tag + ".json");
            string gpJson    = gp1.Replace("\\", "/");
            string fbmodJson = fbmod.Replace("\\", "/");
            try
            {
                File.WriteAllText(pipeline,
                    "{\n" +
                    "  \"game\": \"swbf1\",\n" +
                    "  \"game-path\": \"" + gpJson + "\",\n" +
                    "  \"steps\": [\n" +
                    "    { \"op\": \"project-new\" },\n" +
                    "    { \"op\": \"ebx-create\", \"ebx\": \"swbf1_test/frostycli_" + tag + "\", \"type\": \"DataContainer\" },\n" +
                    "    { \"op\": \"mod-build\", \"out\": \"" + fbmodJson + "\", \"mod-name\": \"swbf1parity\" }\n" +
                    "  ]\n" +
                    "}\n");

                string so, se;
                int rc = RunCli(exe, "run \"" + pipeline + "\"", out so, out se);
                Assert.IsTrue(rc == 0, "swbf1 write pipeline rc=" + rc + "; stderr: " + se);
                Assert.IsTrue(File.Exists(fbmod), "swbf1 mod build produced no .fbmod");
                Assert.IsTrue(new FileInfo(fbmod).Length > 0, "swbf1 .fbmod is empty");
            }
            finally
            {
                try { File.Delete(pipeline); } catch { }
                try { File.Delete(fbmod); } catch { }
            }
        }

        // Validates the SWBF1 texture import round-trip end to end: export a resident SWBF1 texture to
        // DDS via the exe (export-batch), then a fresh swbf1 process runs a pipeline that imports that
        // DDS back over the same asset and builds a .fbmod. Exercises adapter discovery (the exe must
        // load FrostyCLI.Adapters.Texture from its Adapters\ dir) + import-texture + mod-build on swbf1.
        [Test]
        [IntegrationTest("swbf1")]
        public void Swbf1_ImportTexture_RoundTripBuildsMod()
        {
            string gp1 = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF1");
            if (string.IsNullOrEmpty(gp1)) { Console.WriteLine("skip: FROSTYCLI_GAME_PATH_SWBF1 not set"); return; }
            string exe = FindCliExe();
            if (exe == null) { Console.WriteLine("skip: FrostyCLI.exe not found"); return; }

            // A reliably-present SWBF1 cryo-chamber wall texture.
            const string ebx = "XP2/Objects/Architecture/_WallsCryoChamber_01/T_WallsCryoChamberUpper_TubeTile_01_NM";
            string tag      = Guid.NewGuid().ToString("N").Substring(0, 8);
            string expDir   = Path.Combine(Path.GetTempPath(), "swbf1_teximp_" + tag);
            string fbmod    = Path.Combine(Path.GetTempPath(), "swbf1_teximp_" + tag + ".fbmod");
            string pipeline = Path.Combine(Path.GetTempPath(), "swbf1_teximp_" + tag + ".json");
            string gpJson   = gp1.Replace("\\", "/");
            try
            {
                Directory.CreateDirectory(expDir);
                string so, se;
                int rcExp = RunCli(exe, "export-batch --game swbf1 --game-path \"" + gp1 + "\""
                    + " --type TextureAsset --filter \"" + ebx.ToLowerInvariant() + "\""
                    + " --out \"" + expDir + "\" --format dds", out so, out se);
                Assert.IsTrue(rcExp == 0, "swbf1 export-batch rc=" + rcExp + "; stderr: " + se);

                string dds = null;
                foreach (var f in Directory.EnumerateFiles(expDir, "*.dds", SearchOption.AllDirectories)) { dds = f; break; }
                Assert.IsTrue(dds != null, "swbf1 export-batch produced no DDS (adapters not loaded?)");

                string ddsJson   = dds.Replace("\\", "/");
                string fbmodJson = fbmod.Replace("\\", "/");
                File.WriteAllText(pipeline,
                    "{\n" +
                    "  \"game\": \"swbf1\",\n" +
                    "  \"game-path\": \"" + gpJson + "\",\n" +
                    "  \"steps\": [\n" +
                    "    { \"op\": \"project-new\" },\n" +
                    "    { \"op\": \"import-texture\", \"file\": \"" + ddsJson + "\", \"as\": \"" + ebx + "\" },\n" +
                    "    { \"op\": \"mod-build\", \"out\": \"" + fbmodJson + "\", \"mod-name\": \"swbf1teximport\" }\n" +
                    "  ]\n" +
                    "}\n");

                int rc = RunCli(exe, "run \"" + pipeline + "\"", out so, out se);
                Assert.IsTrue(rc == 0, "swbf1 import-texture pipeline rc=" + rc + "; stderr: " + se);
                Assert.IsTrue(File.Exists(fbmod), "swbf1 import-texture build produced no .fbmod");
                Assert.IsTrue(new FileInfo(fbmod).Length > 0, "swbf1 import-texture .fbmod is empty");
            }
            finally
            {
                try { File.Delete(pipeline); } catch { }
                try { File.Delete(fbmod); } catch { }
                try { Directory.Delete(expDir, true); } catch { }
            }
        }

        // 48kHz / mono / 16-bit PCM WAV with a simple tone (matches ImportSoundTests.MakeWav).
        private static byte[] MakeWav(int samples)
        {
            const int sr = 48000, ch = 1, bits = 16;
            int dataLen = samples * 2;
            var ms = new MemoryStream();
            var w = new System.IO.BinaryWriter(ms);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)ch); w.Write(sr);
            w.Write(sr * ch * bits / 8); w.Write((short)(ch * bits / 8)); w.Write((short)bits);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
            for (int i = 0; i < samples; i++) w.Write((short)((i % 100) * 200));
            w.Flush();
            return ms.ToArray();
        }

        // Validates the SWBF1 sound import round-trip end to end: a fresh swbf1 process runs a pipeline
        // that imports a synthetic PCM WAV into a resident SoundWaveAsset variation (the new import-sound
        // pipeline op) and builds a .fbmod. Exercises import-sound + the PCM16Big packing on swbf1.
        [Test]
        [IntegrationTest("swbf1")]
        public void Swbf1_ImportSound_RoundTripBuildsMod()
        {
            string gp1 = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF1");
            if (string.IsNullOrEmpty(gp1)) { Console.WriteLine("skip: FROSTYCLI_GAME_PATH_SWBF1 not set"); return; }
            string exe = FindCliExe();
            if (exe == null) { Console.WriteLine("skip: FrostyCLI.exe not found"); return; }

            // A reliably-present SWBF1 hero-power SoundWaveAsset (single variation 0).
            const string ebx = "Sound/Characters/Powers/Leia/SW01_Characters_Powers_Leia_TrooperBane_StartIdleStop";
            string tag      = Guid.NewGuid().ToString("N").Substring(0, 8);
            string wav      = Path.Combine(Path.GetTempPath(), "swbf1_snd_" + tag + ".wav");
            string fbmod    = Path.Combine(Path.GetTempPath(), "swbf1_snd_" + tag + ".fbmod");
            string pipeline = Path.Combine(Path.GetTempPath(), "swbf1_snd_" + tag + ".json");
            string gpJson    = gp1.Replace("\\", "/");
            string wavJson   = wav.Replace("\\", "/");
            string fbmodJson = fbmod.Replace("\\", "/");
            try
            {
                File.WriteAllBytes(wav, MakeWav(4800)); // 0.1s @ 48kHz
                File.WriteAllText(pipeline,
                    "{\n" +
                    "  \"game\": \"swbf1\",\n" +
                    "  \"game-path\": \"" + gpJson + "\",\n" +
                    "  \"steps\": [\n" +
                    "    { \"op\": \"project-new\" },\n" +
                    "    { \"op\": \"import-sound\", \"file\": \"" + wavJson + "\", \"as\": \"" + ebx + "\", \"variation\": 0, \"encoding\": \"pcm\", \"flatten\": true },\n" +
                    "    { \"op\": \"mod-build\", \"out\": \"" + fbmodJson + "\", \"mod-name\": \"swbf1snd\" }\n" +
                    "  ]\n" +
                    "}\n");

                string so, se;
                int rc = RunCli(exe, "run \"" + pipeline + "\"", out so, out se);
                Assert.IsTrue(rc == 0, "swbf1 import-sound pipeline rc=" + rc + "; stderr: " + se);
                Assert.IsTrue(File.Exists(fbmod), "swbf1 import-sound build produced no .fbmod");
                Assert.IsTrue(new FileInfo(fbmod).Length > 0, "swbf1 import-sound .fbmod is empty");
            }
            finally
            {
                try { File.Delete(wav); } catch { }
                try { File.Delete(pipeline); } catch { }
                try { File.Delete(fbmod); } catch { }
            }
        }

        // Validates SWBF1 res + chunk import in one fresh process: a pipeline exports a resident Res,
        // imports it back (ModifyRes), creates a new chunk from raw bytes (AddChunk via the new
        // chunk-import op with "new": true), and builds a .fbmod. Exercises the res-import / chunk-import
        // pipeline ops + the SWBF1 res/chunk compression path.
        [Test]
        [IntegrationTest("swbf1")]
        public void Swbf1_ResAndChunkImport_RoundTripBuildsMod()
        {
            string gp1 = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF1");
            if (string.IsNullOrEmpty(gp1)) { Console.WriteLine("skip: FROSTYCLI_GAME_PATH_SWBF1 not set"); return; }
            string exe = FindCliExe();
            if (exe == null) { Console.WriteLine("skip: FrostyCLI.exe not found"); return; }

            // A reliably-present SWBF1 shared-eye-texture Res.
            const string res = "characters/heads/texture_shared/eyes/t_eyes_ao_m";
            string tag      = Guid.NewGuid().ToString("N").Substring(0, 8);
            string resbin   = Path.Combine(Path.GetTempPath(), "swbf1_res_" + tag + ".bin");
            string chbin    = Path.Combine(Path.GetTempPath(), "swbf1_chunk_" + tag + ".bin");
            string fbmod    = Path.Combine(Path.GetTempPath(), "swbf1_reschunk_" + tag + ".fbmod");
            string pipeline = Path.Combine(Path.GetTempPath(), "swbf1_reschunk_" + tag + ".json");
            string gpJson     = gp1.Replace("\\", "/");
            string resbinJson = resbin.Replace("\\", "/");
            string chbinJson  = chbin.Replace("\\", "/");
            string fbmodJson  = fbmod.Replace("\\", "/");
            try
            {
                var chunkBytes = new byte[256];
                for (int i = 0; i < chunkBytes.Length; i++) chunkBytes[i] = (byte)(i & 0xFF);
                File.WriteAllBytes(chbin, chunkBytes);

                File.WriteAllText(pipeline,
                    "{\n" +
                    "  \"game\": \"swbf1\",\n" +
                    "  \"game-path\": \"" + gpJson + "\",\n" +
                    "  \"steps\": [\n" +
                    "    { \"op\": \"project-new\" },\n" +
                    "    { \"op\": \"res-export\", \"res\": \"" + res + "\", \"out\": \"" + resbinJson + "\" },\n" +
                    "    { \"op\": \"res-import\", \"file\": \"" + resbinJson + "\", \"res\": \"" + res + "\" },\n" +
                    "    { \"op\": \"chunk-import\", \"file\": \"" + chbinJson + "\", \"new\": true },\n" +
                    "    { \"op\": \"mod-build\", \"out\": \"" + fbmodJson + "\", \"mod-name\": \"swbf1reschunk\" }\n" +
                    "  ]\n" +
                    "}\n");

                string so, se;
                int rc = RunCli(exe, "run \"" + pipeline + "\"", out so, out se);
                Assert.IsTrue(rc == 0, "swbf1 res/chunk import pipeline rc=" + rc + "; stderr: " + se);
                Assert.IsTrue(File.Exists(fbmod), "swbf1 res/chunk import build produced no .fbmod");
                Assert.IsTrue(new FileInfo(fbmod).Length > 0, "swbf1 res/chunk import .fbmod is empty");
            }
            finally
            {
                try { File.Delete(resbin); } catch { }
                try { File.Delete(chbin); } catch { }
                try { File.Delete(pipeline); } catch { }
                try { File.Delete(fbmod); } catch { }
            }
        }
    }
}
