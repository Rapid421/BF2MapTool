using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests
{
    public class AudioPackageTests
    {
        internal static byte[] Segment(int channels = 2, int frames = 1)
        {
            int size = frames * channels * 2 + 8;
            return new byte[] { 0x48, 0, 0, 12, 0x12, (byte)((channels - 1) << 2), 0xbb, 0x80,
                0x40, (byte)(frames >> 16), (byte)(frames >> 8), (byte)frames,
                0x44, (byte)(size >> 16), (byte)(size >> 8), (byte)size,
                0, (byte)(frames >> 16), (byte)(frames >> 8), (byte)frames
            }.Concat(new byte[size - 8]).Concat(new byte[] { 0x45, 0, 0, 4 }).ToArray();
        }

        internal static AudioPackage Fixture(out List<byte[]> bytes)
        {
            bytes = new List<byte[]> { Segment().Concat(Segment()).Concat(Segment()).ToArray() };
            var package = new AudioPackage { Game = "swbf1", Asset = "test/loop", FileGuid = Guid.NewGuid().ToString() };
            foreach (string field in AudioPackage.Fields) package.Wave[field] = 0;
            package.Wave["ChannelCount"] = 2;
            package.Wave["Seekable"] = false;
            package.Wave["PreferAvailableVariations"] = true;
            package.Wave["Selection"] = "SoundWaveVariationSelection_Sequential";
            package.Wave["Chunks"] = new JArray(new JObject { ["__type"] = "SoundDataChunk", ["ChunkId"] = Guid.NewGuid().ToString(), ["ChunkSize"] = bytes[0].Length });
            package.Wave["RuntimeVariations"] = new JArray(new JObject {
                ["__type"] = "SoundWaveRuntimeVariation", ["PersistentDataSize"] = 0, ["FirstSegmentIndex"] = 0,
                ["FirstSubtitleIndex"] = 0, ["SubtitleCount"] = 0, ["SegmentCount"] = 3, ["ChunkIndex"] = 0,
                ["FirstLoopSegmentIndex"] = 1, ["LastLoopSegmentIndex"] = 1, ["Weight"] = 100
            });
            package.Wave["Segments"] = new JArray(Enumerable.Range(0, 3).Select(i => new JObject {
                ["__type"] = "SoundWaveVariationSegment", ["SamplesOffset"] = i * 28, ["SeekTableOffset"] = uint.MaxValue, ["SegmentLength"] = 1.0f / 48000
            }));
            package.Chunks.Add(new AudioChunkFile { File = "chunk-0.bin", Size = bytes[0].Length, Sha256 = AudioPackage.Hash(bytes[0]) });
            return package;
        }

        [Test] public void Header_DurationCountsFramesNotInterleavedSamples()
        {
            var header = AudioSegmentHeader.Read(Segment(2, 48000), 0);
            Assert.AreEqual(2, header.Channels);
            Assert.AreEqual(1.0, header.Seconds);
            Assert.Throws<InvalidDataException>(() => AudioSegmentHeader.Read(Segment(), -1));
            Assert.Throws<InvalidDataException>(() => AudioSegmentHeader.Read(Segment(), 20));
        }

        [Test] public void NativePackage_PreservesStartLoopStopAndSharedChunk()
        {
            List<byte[]> bytes; var package = Fixture(out bytes);
            ((JArray)package.Wave["RuntimeVariations"]).Add(package.Wave["RuntimeVariations"][0].DeepClone());
            package.Validate(bytes);
            Assert.AreEqual(1, (int)package.Wave["RuntimeVariations"][1]["FirstLoopSegmentIndex"]);
        }

        [Test] public void Package_RejectsBadRangesChannelsAndUnknownCodec()
        {
            foreach (Action<AudioPackage, List<byte[]>> corrupt in new Action<AudioPackage, List<byte[]>>[] {
                (p,b) => p.Wave["RuntimeVariations"][0]["ChunkIndex"] = 1,
                (p,b) => p.Wave["RuntimeVariations"][0]["SegmentCount"] = 4,
                (p,b) => p.Wave["RuntimeVariations"][0]["LastLoopSegmentIndex"] = 3,
                (p,b) => p.Wave["Segments"][0]["SamplesOffset"] = -1,
                (p,b) => p.Wave["Segments"][0]["SeekTableOffset"] = 99,
                (p,b) => p.Wave["ChannelCount"] = 1,
                (p,b) => b[0][4] = 0xff,
                (p,b) => p.Wave["Chunks"][0]["ChunkSize"] = 0,
                (p,b) => p.Wave["RuntimeVariations"][0]["PersistentDataSize"] = 999,
                (p,b) => p.Chunks[0].Sha256 = new string('0', 64),
                (p,b) => p.Wave["Policy"] = "foreign-policy"
            }) {
                List<byte[]> bytes; var package = Fixture(out bytes); corrupt(package, bytes);
                if (bytes[0][4] == 0xff) package.Chunks[0].Sha256 = AudioPackage.Hash(bytes[0]); // exercise codec validation past the checksum
                Assert.Throws<InvalidDataException>(() => package.Validate(bytes));
            }
        }

        [Test] public void Package_RejectsSubtitlesRatherThanImportingForeignIndices()
        {
            List<byte[]> bytes; var package = Fixture(out bytes);
            package.Wave["RuntimeVariations"][0]["SubtitleCount"] = 1;
            Assert.Throws<InvalidDataException>(() => package.Validate(bytes));
        }

        [Test] public void Package_ReadChecksHashesVersionAndFilenames()
        {
            string dir = Path.Combine(Path.GetTempPath(), "audio_package_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                List<byte[]> bytes; var package = Fixture(out bytes);
                File.WriteAllBytes(Path.Combine(dir, "chunk-0.bin"), bytes[0]);
                string manifest = Path.Combine(dir, "audio.json");
                File.WriteAllText(manifest, JsonConvert.SerializeObject(package));
                List<byte[]> loaded;
                AudioPackage.Load(dir, out loaded);
                Assert.IsTrue(bytes[0].SequenceEqual(loaded[0]));
                bytes[0][12] ^= 1;
                File.WriteAllBytes(Path.Combine(dir, "chunk-0.bin"), bytes[0]);
                Assert.Throws<InvalidDataException>(() => AudioPackage.Load(dir, out loaded));
                package.Chunks[0].File = "../outside.bin";
                File.WriteAllText(manifest, JsonConvert.SerializeObject(package));
                Assert.Throws<InvalidDataException>(() => AudioPackage.Load(dir, out loaded));
                package.Version = 2;
                File.WriteAllText(manifest, JsonConvert.SerializeObject(package));
                Assert.Throws<InvalidDataException>(() => AudioPackage.Load(dir, out loaded));
            }
            finally { Directory.Delete(dir, true); }
        }

        internal static byte[] Wav(int channels, int frames)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                int size = channels * frames * 2;
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + size);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((ushort)1); writer.Write((ushort)channels); writer.Write(48000);
                writer.Write(48000 * channels * 2); writer.Write((ushort)(channels * 2)); writer.Write((ushort)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(size);
                for (int frame = 0; frame < frames; frame++)
                    for (int ch = 0; ch < channels; ch++) writer.Write((short)(ch == 0 ? frame % 100 * 200 : -(frame % 100 * 200)));
                return stream.ToArray();
            }
        }

        [Test] public void WavParser_PreservesStereoAndRejectsTruncation()
        {
            string path = Path.Combine(Path.GetTempPath(), "audio_wav_" + Guid.NewGuid().ToString("N") + ".wav");
            try {
                byte[] wav = Wav(2, 100);
                File.WriteAllBytes(path, wav);
                var parsed = HeadlessSoundImporter.ParseWav(path);
                Assert.AreEqual(2, parsed.Channels); Assert.AreEqual(400, parsed.Pcm.Length);
                File.WriteAllBytes(path, wav.Take(wav.Length - 1).ToArray());
                Assert.Throws<InvalidDataException>(() => HeadlessSoundImporter.ParseWav(path));
                wav[22] = 0;
                File.WriteAllBytes(path, wav);
                Assert.Throws<InvalidDataException>(() => HeadlessSoundImporter.ParseWav(path));
            } finally { File.Delete(path); }
        }

        [Test] public void ReadBack_OnlyAllowsFloat32Rounding()
        {
            Assert.IsTrue(SoundWavePort.EqualFields(new JValue(3.632229), new JValue(3.63222909)));
            Assert.IsFalse(SoundWavePort.EqualFields(new JValue(3.632229), new JValue(3.63)));
            Assert.IsFalse(SoundWavePort.EqualFields(new JValue(1), new JValue(2)));
        }
    }
}
