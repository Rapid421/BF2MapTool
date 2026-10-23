using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Core
{
    // A lossless SoundWave payload. Game-specific policy/mixer/graph references belong to
    // the destination template, not to this package. Original seek data stays in the chunks.
    public sealed class AudioPackage
    {
        public int Version = 1;
        public string Game;
        public string Asset;
        public string FileGuid;
        public JObject Wave = new JObject();
        public List<AudioChunkFile> Chunks = new List<AudioChunkFile>();

        internal static readonly string[] Fields = {
            "RuntimeVariations", "Segments", "Chunks", "Selection", "Seekable",
            "VariationHistoryCount", "PersistentVariationCount", "PreferAvailableVariations",
            "StreamingMode", "VoicePriority", "ChannelCount", "PrimePriority", "RequestPriority"
        };

        public static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        public static AudioPackage Load(string directory, out List<byte[]> payloads)
        {
            var package = JsonConvert.DeserializeObject<AudioPackage>(
                File.ReadAllText(Path.Combine(directory, "audio.json")));
            if (package == null || package.Version != 1) throw new InvalidDataException("Unsupported audio package version.");
            if (package.Chunks == null) throw new InvalidDataException("Missing chunk manifest.");
            payloads = new List<byte[]>();
            for (int i = 0; i < package.Chunks.Count; i++)
            {
                var chunk = package.Chunks[i];
                // Fixed filenames avoid reading outside the package and make imports reproducible.
                if (chunk == null || chunk.File != "chunk-" + i + ".bin")
                    throw new InvalidDataException("Invalid chunk filename at index " + i);
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, chunk.File));
                if (bytes.LongLength != chunk.Size || !string.Equals(Hash(bytes), chunk.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Chunk size/checksum mismatch: " + chunk.File);
                payloads.Add(bytes);
            }
            package.Validate(payloads);
            return package;
        }

        public void Validate(IList<byte[]> payloads)
        {
            if (Version != 1 || Wave == null) throw new InvalidDataException("Unsupported or missing audio package.");
            if (Game != "swbf1" && Game != "swbf2") throw new InvalidDataException("Audio packages support swbf1 and swbf2 only.");
            foreach (string field in Fields)
                if (Wave[field] == null) throw new InvalidDataException("Missing wave field: " + field);
            foreach (var field in Wave.Properties())
                if (!Fields.Contains(field.Name)) throw new InvalidDataException("Unexpected wave field: " + field.Name);
            var chunks = Wave["Chunks"] as JArray;
            var variations = Wave["RuntimeVariations"] as JArray;
            var segments = Wave["Segments"] as JArray;
            if (chunks == null || variations == null || segments == null || variations.Count == 0)
                throw new InvalidDataException("Package needs chunks, segments and at least one variation.");
            if (payloads == null || chunks.Count != payloads.Count || chunks.Count == 0)
                throw new InvalidDataException("Chunk payload count does not match the wave.");
            if (Chunks == null || Chunks.Count != chunks.Count)
                throw new InvalidDataException("Chunk manifest count does not match the wave.");
            for (int i = 0; i < chunks.Count; i++)
            {
                if (payloads[i] == null || (long)chunks[i]["ChunkSize"] != payloads[i].LongLength)
                    throw new InvalidDataException("Wave chunk size mismatch at " + i);
                var manifest = Chunks[i];
                if (manifest == null || manifest.File != "chunk-" + i + ".bin" || manifest.Size != payloads[i].LongLength ||
                    !string.Equals(manifest.Sha256, Hash(payloads[i]), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Chunk manifest/checksum mismatch at " + i);
            }
            for (int i = 0; i < variations.Count; i++)
            {
                JToken v = variations[i];
                int ci = RequiredInt(v, "ChunkIndex"), first = RequiredInt(v, "FirstSegmentIndex"), count = RequiredInt(v, "SegmentCount");
                if (ci < 0 || ci >= chunks.Count || count < 1 || count > 255 || first < 0 || (long)first + count > segments.Count)
                    throw new InvalidDataException("Invalid chunk/segment range for variation " + i);
                int loopFirst = RequiredInt(v, "FirstLoopSegmentIndex"), loopLast = RequiredInt(v, "LastLoopSegmentIndex");
                if (loopFirst < 0 || loopLast < loopFirst || loopLast >= count)
                    throw new InvalidDataException("Invalid loop range for variation " + i);
                if (RequiredInt(v, "SubtitleCount") != 0)
                    throw new InvalidDataException("Localized/subtitled audio is not supported by the ambient/SFX package route.");
                long persistent = (long)v["PersistentDataSize"];
                if (persistent < 0 || persistent > payloads[ci].LongLength)
                    throw new InvalidDataException("Persistent data exceeds its chunk for variation " + i);
                for (int j = first; j < first + count; j++)
                {
                    long offset = (long)segments[j]["SamplesOffset"];
                    float length = (float)segments[j]["SegmentLength"];
                    if (float.IsNaN(length) || float.IsInfinity(length) || length < 0)
                        throw new InvalidDataException("Invalid segment duration.");
                    var h = AudioSegmentHeader.Read(payloads[ci], offset);
                    h.ValidateBlocks(payloads[ci], offset);
                    if (h.Channels != RequiredInt(Wave, "ChannelCount"))
                        throw new InvalidDataException("ChannelCount disagrees with segment " + j);
                    long seek = (long)segments[j]["SeekTableOffset"];
                    if (seek != uint.MaxValue && (seek < 0 || seek >= payloads[ci].LongLength))
                        throw new InvalidDataException("Seek table offset is outside its chunk.");
                }
            }
        }

        internal static int RequiredInt(JToken token, string field)
        {
            if (token?[field] == null || token[field].Type != JTokenType.Integer)
                throw new InvalidDataException("Missing integer field: " + field);
            return checked((int)token[field]);
        }
    }

    public sealed class AudioChunkFile
    {
        public string File;
        public long Size;
        public string Sha256;
    }

    public sealed class AudioSegmentHeader
    {
        public int Codec, Channels, SampleRate, Frames;
        public double Seconds => Frames / (double)SampleRate;

        public void ValidateBlocks(byte[] bytes, long offset)
        {
            long pos = offset + 12, frames = 0;
            int blocks = 0;
            while (pos <= bytes.LongLength - 4)
            {
                int p = checked((int)pos);
                int size = (bytes[p + 1] << 16) | (bytes[p + 2] << 8) | bytes[p + 3];
                if (bytes[p] == 0x45 && size == 4)
                {
                    if (blocks == 0 || frames != Frames) throw new InvalidDataException("Audio block sample count disagrees with its header.");
                    return;
                }
                if (bytes[p] != 0x44 || size < 8 || pos + size > bytes.LongLength)
                    throw new InvalidDataException("Invalid/truncated audio data block.");
                long blockFrames = ((long)bytes[p + 4] << 24) | ((long)bytes[p + 5] << 16) | ((long)bytes[p + 6] << 8) | bytes[p + 7];
                if (blockFrames == 0 || frames + blockFrames > Frames)
                    throw new InvalidDataException("Audio data block has an invalid frame count.");
                if (Codec == 0x12 && size - 8 != blockFrames * Channels * 2)
                    throw new InvalidDataException("PCM payload length disagrees with its frame/channel count.");
                frames += blockFrames; blocks++; pos += size;
            }
            throw new InvalidDataException("Audio segment has no end marker.");
        }

        public static AudioSegmentHeader Read(byte[] bytes, long offset)
        {
            if (bytes == null || offset < 0 || offset > bytes.LongLength - 12)
                throw new InvalidDataException("Audio segment header lies outside its chunk.");
            int p = checked((int)offset);
            if (bytes[p] != 0x48 || bytes[p + 1] != 0 || bytes[p + 2] != 0 || bytes[p + 3] != 12)
                throw new InvalidDataException("Unrecognized Frostbite audio segment header.");
            int codec = bytes[p + 4];
            if (codec != 0x12 && codec != 0x14 && codec != 0x15 && codec != 0x16)
                throw new InvalidDataException("Unsupported audio codec 0x" + codec.ToString("x2"));
            int rate = bytes[p + 6] * 256 + bytes[p + 7];
            if (rate == 0) throw new InvalidDataException("Audio sample rate is zero.");
            return new AudioSegmentHeader {
                Codec = codec, Channels = (bytes[p + 5] >> 2) + 1, SampleRate = rate,
                Frames = (bytes[p + 9] << 16) | (bytes[p + 10] << 8) | bytes[p + 11]
            };
        }
    }
}
