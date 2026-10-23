using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests.Integration
{
    public class AudioPortIntegrationTests
    {
        private const string Wave = "Sound/SP/Planets/Bespin/Shared/SFX/SW02_SP_Planets_Bespin_Shared_SFX_CarboniteChamber_LiftActivationDown_01";
        private const string Patch = "Sound/SP/Planets/Bespin/Shared/SFX/SW02_SP_Planets_Bespin_Shared_SFX_GasTanks_Loop_Patch";
        private static readonly ExportLogger Logger = new ExportLogger(quiet: true);

        private static void Ensure()
        {
            if (!Session.IsInitialized) Session.Initialize("swbf2", Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), null, null, Logger);
            if (!Session.Project.IsLoaded) Session.Project.New();
        }

        [Test, IntegrationTest("swbf2")]
        public void CompressedEncoder_PreservesStereoAndRate()
        {
            if (!EALayer3Encoder.IsAvailable()) { Console.WriteLine("skip: ffmpeg/encoder unavailable"); return; }
            string wav = Path.Combine(Path.GetTempPath(), "audio_stereo_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                File.WriteAllBytes(wav, AudioPackageTests.Wav(2, 48000));
                var encoded = EALayer3Encoder.Encode(wav, Console.WriteLine);
                Assert.IsNotNull(encoded);
                var header = AudioSegmentHeader.Read(encoded, 0);
                Assert.AreEqual(2, header.Channels);
                Assert.AreEqual(48000, header.SampleRate);
                header.ValidateBlocks(encoded, 0);
            }
            finally { File.Delete(wav); }
        }

        [Test, IntegrationTest("swbf2")]
        public void NativeImport_PreservesPayloadLoopsAndDestinationPolicy()
        {
            Ensure();
            string name = "Tests/Audio/" + Guid.NewGuid().ToString("N");
            var source = App.AssetManager.GetEbxEntry(Wave);
            List<byte[]> bytes; var package = SoundWavePort.Capture(source, out bytes);
            Assert.IsTrue((int)package.Wave["RuntimeVariations"][0]["SegmentCount"] > 1, "fixture must exercise a multi-segment loop");
            var bundle = App.AssetManager.EnumerateBundles().First(b => b.Name.Equals("win32/S2/Levels/CloudCity_01/CloudCity_01", StringComparison.OrdinalIgnoreCase));
            JObject original = EbxDumper.Dump(App.AssetManager.GetEbx(source), true, true);
            var freshIds = new List<Guid>();
            try
            {
                var entry = SoundWavePort.Import(package, bytes, Wave, name, App.AssetManager.GetBundleId(bundle), false);
                List<byte[]> actual; var imported = SoundWavePort.Capture(entry, out actual);
                foreach (JToken c in imported.Wave["Chunks"]) freshIds.Add(Guid.Parse((string)c["ChunkId"]));
                Assert.IsTrue(!entry.Guid.Equals(source.Guid));
                Assert.IsTrue(SoundWavePort.EqualFields(package.Wave["Segments"], imported.Wave["Segments"]));
                Assert.IsTrue(SoundWavePort.EqualFields(package.Wave["RuntimeVariations"], imported.Wave["RuntimeVariations"]));
                for (int i = 0; i < bytes.Count; i++)
                {
                    Assert.IsTrue(bytes[i].SequenceEqual(actual[i]));
                    Assert.IsTrue((string)package.Wave["Chunks"][i]["ChunkId"] != freshIds[i].ToString());
                    Assert.IsTrue(App.AssetManager.GetChunkEntry(freshIds[i]).AddedBundles.Contains(App.AssetManager.GetBundleId(bundle)));
                }
                var importedDump = EbxDumper.Dump(App.AssetManager.GetEbx(entry), true, true);
                Assert.IsTrue(JToken.DeepEquals(original["Policy"], importedDump["Policy"]));
                Assert.IsTrue(SoundWavePort.EqualFields(original, EbxDumper.Dump(App.AssetManager.GetEbx(source), true, true)), "donor changed");
            }
            finally
            {
                var entry = App.AssetManager.GetEbxEntry(name); if (entry != null) App.AssetManager.RevertAsset(entry);
                foreach (Guid id in freshIds) { var chunk = App.AssetManager.GetChunkEntry(id); if (chunk != null) App.AssetManager.RevertAsset(chunk); }
            }
        }

        [Test, IntegrationTest("swbf2")]
        public void DryRunAndInvalidPackage_DoNotCreateAssets()
        {
            Ensure();
            string name = "Tests/Audio/" + Guid.NewGuid().ToString("N");
            List<byte[]> bytes; var package = SoundWavePort.Capture(App.AssetManager.GetEbxEntry(Wave), out bytes);
            var bundle = App.AssetManager.EnumerateBundles().First();
            SoundWavePort.Import(package, bytes, Wave, name, App.AssetManager.GetBundleId(bundle), true);
            Assert.IsNull(App.AssetManager.GetEbxEntry(name));
            package.Wave["RuntimeVariations"][0]["LastLoopSegmentIndex"] = 255;
            Assert.Throws<InvalidDataException>(() => SoundWavePort.Import(package, bytes, Wave, name, App.AssetManager.GetBundleId(bundle), false));
            Assert.IsNull(App.AssetManager.GetEbxEntry(name));
        }

        [Test, IntegrationTest("swbf2")]
        public void WavImport_RejectsImplicitLoopFlatteningWithoutChangingChunk()
        {
            Ensure();
            var source = App.AssetManager.GetEbxEntry(Wave);
            List<byte[]> before; var package = SoundWavePort.Capture(source, out before);
            string wav = Path.Combine(Path.GetTempPath(), "audio_loop_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                File.WriteAllBytes(wav, AudioPackageTests.Wav(2, 100));
                string error = HeadlessSoundImporter.Import(source, wav, 0, Logger, "pcm");
                Assert.IsTrue(error != null && error.Contains("--flatten"));
                List<byte[]> after; var check = SoundWavePort.Capture(source, out after);
                Assert.IsTrue(SoundWavePort.EqualFields(package.Wave, check.Wave));
                Assert.IsTrue(before[0].SequenceEqual(after[0]));
            }
            finally { File.Delete(wav); }
        }

        [Test, IntegrationTest("swbf2")]
        public void Place_CreatesReferencedSoundAtRequestedTransform()
        {
            Ensure();
            string layer = "Tests/AudioLayer/" + Guid.NewGuid().ToString("N");
            var fmt = new OutputFormatter(false);
            try
            {
                Assert.AreEqual(0, EbxWriteCommand.ExecuteCreate(new[] { layer, "--type", "LayerData" }, Logger, fmt));
                Assert.AreEqual(0, new AudioCommand().Execute(new[] { "place", Patch, "--into", layer, "--pos", "1,2,3", "--play-on-creation", "false" }, Logger, fmt));
                var asset = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(layer));
                var sound = asset.Objects.Single(o => o.GetType().Name == "SoundEntityData");
                dynamic entity = sound;
                Assert.AreEqual(false, (bool)entity.PlayOnCreation);
                Assert.AreEqual(1f, (float)entity.Transform.trans.x);
                Assert.AreEqual(2f, (float)entity.Transform.trans.y);
                Assert.AreEqual(3f, (float)entity.Transform.trans.z);
                var dump = EbxDumper.Dump(asset, true, true);
                Assert.IsTrue(dump.ToString().Contains(Patch));
                Assert.AreEqual(1, ((IList)SoundWavePort.Get(asset.RootObject, "Objects")).Count);
                Assert.AreEqual(1, new AudioCommand().Execute(new[] { "place", Wave, "--into", layer, "--pos", "1,2,3" }, Logger, fmt));
                Assert.AreEqual(1, ((IList)SoundWavePort.Get(App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(layer)).RootObject, "Objects")).Count);
            }
            finally { var entry = App.AssetManager.GetEbxEntry(layer); if (entry != null) App.AssetManager.RevertAsset(entry); }
        }
    }
}
