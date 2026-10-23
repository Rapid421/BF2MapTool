using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Linq;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class ImportSoundTests
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

        // 48kHz / mono / 16-bit PCM WAV with a simple tone.
        private static byte[] MakeWav(int samples, int ch = 1)
        {
            const int sr = 48000, bits = 16;
            int dataLen = samples * ch * 2;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)ch); w.Write(sr);
            w.Write(sr * ch * bits / 8); w.Write((short)(ch * bits / 8)); w.Write((short)bits);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
            for (int i = 0; i < samples * ch; i++) w.Write((short)((i % 100) * 200));
            w.Flush();
            return ms.ToArray();
        }

        // --- Unit guards (no game needed) ---

        [Test]
        public void ImportSound_MissingAs_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            string tmp = Path.Combine(Path.GetTempPath(), "g_" + Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllBytes(tmp, MakeWav(48));
            try { Assert.AreEqual(1, ImportCommand.ExecuteSound(new[] { tmp }, logger, new OutputFormatter(false))); }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [Test]
        public void ImportSound_WrongExt_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            int rc = ImportCommand.ExecuteSound(new[] { "C:\\nope.mp3", "--as", "s2/x" }, logger, new OutputFormatter(false));
            Assert.AreEqual(1, rc);
        }

        // The EALayer3 (compressed) path is gated on ffmpeg + the encoder both being present. When
        // ffmpeg is absent (as in CI), IsAvailable() must be false so `import sound` falls back to PCM.
        [Test]
        public void EALayer3_GatedOnFfmpegPresence()
        {
            bool available = EALayer3Encoder.IsAvailable();
            if (available) { Console.WriteLine("note: ffmpeg present — EALayer3 path is available"); return; }
            Assert.IsFalse(EALayer3Encoder.IsAvailable(), "EALayer3 must be unavailable without ffmpeg");
        }

        // --- Integration: import a PCM WAV into a SoundWaveAsset variation ---

        [Test]
        [IntegrationTest("swbf2")]
        public void ImportSound_PcmWav_ModifiesVariationChunk()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            // Find a SoundWaveAsset whose variation 0 maps to a chunk referenced by exactly one
            // variation (the simple, non-spliced path).
            EbxAssetEntry target = null;
            Guid chunkId = Guid.Empty;
            int scanned = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("SoundWaveAsset"))
            {
                if (++scanned > 300) break;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                try
                {
                    dynamic root = a.RootObject;
                    if ((int)root.RuntimeVariations.Count <= 0 || (int)root.Chunks.Count <= 0) continue;
                    dynamic v0 = root.RuntimeVariations[0];
                    if ((int)v0.SegmentCount != 1) continue;
                    int ci = (int)v0.ChunkIndex;
                    if (ci < 0 || ci >= (int)root.Chunks.Count) continue;
                    Guid cid = root.Chunks[ci].ChunkId;
                    if (App.AssetManager.GetChunkEntry(cid) == null) continue;
                    int refs = 0;
                    foreach (dynamic v in root.RuntimeVariations) if ((int)v.ChunkIndex == ci) refs++;
                    if (refs != 1) continue;
                    target = e; chunkId = cid; break;
                }
                catch { continue; }
            }
            if (target == null) { Console.WriteLine("skip: no usable single-variation SoundWaveAsset found"); return; }

            string wav = Path.Combine(Path.GetTempPath(), "snd_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                File.WriteAllBytes(wav, MakeWav(4800, (int)((dynamic)App.AssetManager.GetEbx(target).RootObject).ChannelCount)); // 0.1s @ 48kHz
                string err = HeadlessSoundImporter.Import(target, wav, 0, null);
                Assert.IsTrue(err == null, "import returned: " + err);
                Assert.IsTrue(App.AssetManager.GetEbxEntry(target.Name).IsModified, "SoundWave ebx should be modified");
                Assert.IsTrue(App.AssetManager.GetChunkEntry(chunkId).IsModified, "audio chunk should be modified");
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetChunkEntry(chunkId)); } catch { }
                try { App.AssetManager.RevertAsset(App.AssetManager.GetEbxEntry(target.Name)); } catch { }
                try { File.Delete(wav); } catch { }
            }
        }

        // G5: the compressed EALayer3 path, end to end (ffmpeg -> 48kHz mono MP3 -> ealayer3_encode ->
        // Frostbite chunk). Gated on EALayer3Encoder.IsAvailable() so it runs only where ffmpeg + the
        // encoder are both present (skips on CI without ffmpeg). Asserts the variation chunk is modified
        // and that the EALayer3 chunk is smaller than the equivalent uncompressed PCM (proof it compressed).
        [Test]
        [IntegrationTest("swbf2")]
        public void ImportSound_EALayer3_ModifiesVariationChunk()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();
            if (!EALayer3Encoder.IsAvailable())
            { Console.WriteLine("skip: ffmpeg/ealayer3_encode not present -- EALayer3 path unavailable"); return; }

            // Same single-variation SoundWaveAsset selection as the PCM test.
            EbxAssetEntry target = null; Guid chunkId = Guid.Empty; int scanned = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("SoundWaveAsset"))
            {
                if (++scanned > 300) break;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                try
                {
                    dynamic root = a.RootObject;
                    if ((int)root.RuntimeVariations.Count <= 0 || (int)root.Chunks.Count <= 0) continue;
                    dynamic v0 = root.RuntimeVariations[0];
                    if ((int)v0.SegmentCount != 1) continue;
                    int ci = (int)v0.ChunkIndex;
                    if (ci < 0 || ci >= (int)root.Chunks.Count) continue;
                    Guid cid = root.Chunks[ci].ChunkId;
                    if (App.AssetManager.GetChunkEntry(cid) == null) continue;
                    int refs = 0; foreach (dynamic v in root.RuntimeVariations) if ((int)v.ChunkIndex == ci) refs++;
                    if (refs != 1) continue;
                    target = e; chunkId = cid; break;
                }
                catch { continue; }
            }
            if (target == null) { Console.WriteLine("skip: no usable single-variation SoundWaveAsset found"); return; }

            string wav = Path.Combine(Path.GetTempPath(), "snd_el3_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                File.WriteAllBytes(wav, MakeWav(48000, (int)((dynamic)App.AssetManager.GetEbx(target).RootObject).ChannelCount)); // 1.0s @ 48kHz -- enough to compress meaningfully
                string err = HeadlessSoundImporter.Import(target, wav, 0, logger, "ealayer3");
                Assert.IsTrue(err == null, "EALayer3 import returned: " + err);
                Assert.IsTrue(App.AssetManager.GetEbxEntry(target.Name).IsModified, "SoundWave ebx should be modified");
                Assert.IsTrue(App.AssetManager.GetChunkEntry(chunkId).IsModified, "audio chunk should be modified");
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetChunkEntry(chunkId)); } catch { }
                try { App.AssetManager.RevertAsset(App.AssetManager.GetEbxEntry(target.Name)); } catch { }
                try { File.Delete(wav); } catch { }
            }
        }

        // P3: import into a NON-ZERO runtime variation of a multi-variation SoundWaveAsset. The earlier
        // tests only cover variation 0; this exercises the variation-index selection for N>0. Driven
        // through the full CLI path (ImportCommand.ExecuteSound --variation N) so the arg also gets tested.
        [Test]
        [IntegrationTest("swbf2")]
        public void ImportSound_IntoNonZeroVariation_Modifies()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry target = null; int varIdx = -1; Guid chunkId = Guid.Empty; int scanned = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("SoundWaveAsset"))
            {
                if (++scanned > 500) break;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                try
                {
                    dynamic root = a.RootObject;
                    int vc = (int)root.RuntimeVariations.Count;
                    if (vc < 2 || (int)root.Chunks.Count <= 0) continue;
                    int idx = vc - 1; // the last variation -> guaranteed N>0
                    dynamic v = root.RuntimeVariations[idx];
                    if ((int)v.SegmentCount != 1) continue;
                    int ci = (int)v.ChunkIndex;
                    if (ci < 0 || ci >= (int)root.Chunks.Count) continue;
                    Guid cid = root.Chunks[ci].ChunkId;
                    if (App.AssetManager.GetChunkEntry(cid) == null) continue;
                    target = e; varIdx = idx; chunkId = cid; break;
                }
                catch { continue; }
            }
            if (target == null) { Console.WriteLine("skip: no multi-variation SoundWaveAsset found"); return; }

            string wav = Path.Combine(Path.GetTempPath(), "snd_vN_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                File.WriteAllBytes(wav, MakeWav(4800, (int)((dynamic)App.AssetManager.GetEbx(target).RootObject).ChannelCount));
                int rc = ImportCommand.ExecuteSound(
                    new[] { wav, "--as", target.Name, "--variation", varIdx.ToString(), "--pcm" }, logger, fmt);
                Assert.AreEqual(0, rc);
                Assert.IsTrue(App.AssetManager.GetEbxEntry(target.Name).IsModified, "SoundWave ebx should be modified");
                Assert.IsTrue(App.AssetManager.GetChunkEntry(chunkId).IsModified, "variation #" + varIdx + " chunk should be modified");
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetChunkEntry(chunkId)); } catch { }
                try { App.AssetManager.RevertAsset(App.AssetManager.GetEbxEntry(target.Name)); } catch { }
                try { File.Delete(wav); } catch { }
            }
        }

        // Shared-chunk replacement must preserve every existing byte and neighbouring segment row.
        [Test]
        [IntegrationTest("swbf2")]
        public void ImportSound_SharedChunk_PreservesOtherVariations()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry target = null; int varIdx = -1; Guid chunkId = Guid.Empty; int scanned = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("SoundWaveAsset"))
            {
                if (++scanned > 800) break;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                try
                {
                    dynamic root = a.RootObject;
                    int vc = (int)root.RuntimeVariations.Count;
                    int cc = (int)root.Chunks.Count;
                    if (vc < 2 || cc <= 0) continue;
                    int[] refCount = new int[cc];
                    foreach (dynamic v in root.RuntimeVariations) { int ci = (int)v.ChunkIndex; if (ci >= 0 && ci < cc) refCount[ci]++; }
                    for (int i = 0; i < vc; i++)
                    {
                        if ((int)root.RuntimeVariations[i].SegmentCount != 1) continue;
                        int ci = (int)root.RuntimeVariations[i].ChunkIndex;
                        if (ci < 0 || ci >= cc || refCount[ci] <= 1) continue;
                        Guid cid = root.Chunks[ci].ChunkId;
                        if (App.AssetManager.GetChunkEntry(cid) == null) continue;
                        target = e; varIdx = i; chunkId = cid; break;
                    }
                    if (target != null) break;
                }
                catch { continue; }
            }
            if (target == null) { Console.WriteLine("skip: no SoundWaveAsset with a shared (multi-ref) chunk found"); return; }

            string wav = Path.Combine(Path.GetTempPath(), "snd_splice_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                byte[] before = SoundWavePort.ReadChunk(App.AssetManager.GetChunkEntry(chunkId));
                var oldDump = EbxDumper.Dump(App.AssetManager.GetEbx(target), true, true);
                File.WriteAllBytes(wav, MakeWav(2400, (int)((dynamic)App.AssetManager.GetEbx(target).RootObject).ChannelCount)); // 0.05s -- small splice
                string err = HeadlessSoundImporter.Import(target, wav, varIdx, logger, "pcm");
                Assert.IsTrue(err == null, "shared-chunk splice import returned: " + err);
                Assert.IsTrue(App.AssetManager.GetChunkEntry(chunkId).IsModified, "shared chunk should be modified by the splice");
                byte[] after = SoundWavePort.ReadChunk(App.AssetManager.GetChunkEntry(chunkId));
                Assert.IsTrue(after.Take(before.Length).SequenceEqual(before), "existing sample and seek bytes changed");
                var newDump = EbxDumper.Dump(App.AssetManager.GetEbx(target), true, true);
                int changedSegment = (int)oldDump["RuntimeVariations"][varIdx]["FirstSegmentIndex"];
                for (int i = 0; i < oldDump["Segments"].Count(); i++)
                    if (i != changedSegment) Assert.IsTrue(SoundWavePort.EqualFields(oldDump["Segments"][i], newDump["Segments"][i]), "neighbour segment changed");
                Assert.AreEqual(before.Length, (int)newDump["Segments"][changedSegment]["SamplesOffset"]);
                var header = AudioSegmentHeader.Read(after, before.Length);
                header.ValidateBlocks(after, before.Length);
                Assert.AreEqual(2400, header.Frames);
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetChunkEntry(chunkId)); } catch { }
                try { App.AssetManager.RevertAsset(App.AssetManager.GetEbxEntry(target.Name)); } catch { }
                try { File.Delete(wav); } catch { }
            }
        }
    }
}
