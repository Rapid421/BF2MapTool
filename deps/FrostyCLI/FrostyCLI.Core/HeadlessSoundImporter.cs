using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Interfaces;
using FrostySdk.Managers;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Replaces one SoundWave variation with a channel-matched PCM WAV. Multi-segment
    /// variations require explicit flattening; use native audio packages to retain loops/seek data.
    /// </summary>
    public static class HeadlessSoundImporter
    {
        /// <summary>Returns null on success, or an error message. encoding = "auto" | "ealayer3" | "pcm".</summary>
        public static string Import(EbxAssetEntry entry, string wavPath, int variationIndex, ILogger logger, string encoding = "auto", bool flatten = false)
        {
            if (entry == null) return "sound import: EBX entry is null.";
            if (!File.Exists(wavPath)) return "sound import: file not found: " + wavPath;
            if (!Session.Project.IsLoaded || !Session.Project.IsComplete) return "sound import: a complete project is required.";

            string mode = (encoding ?? "auto").ToLowerInvariant();
            if (mode != "auto" && mode != "pcm" && mode != "ealayer3") return "sound import: unknown encoding " + mode;
            if (entry.Type != "SoundWaveAsset") return "sound import: target must be a SoundWaveAsset.";
            try
            {
                dynamic target = App.AssetManager.GetEbx(entry).RootObject;
                if (variationIndex < 0 || variationIndex >= (int)target.RuntimeVariations.Count) return "sound import: variation index is out of range.";
                if ((int)target.RuntimeVariations[variationIndex].SegmentCount > 1 && !flatten)
                    return "sound import: this variation has multiple segments. Use audio export/import to preserve its loops, or --flatten to explicitly replace it with one segment.";
                int first = (int)target.RuntimeVariations[variationIndex].FirstSegmentIndex;
                for (int i = 0; i < (int)target.RuntimeVariations.Count; i++)
                    if (i != variationIndex && first >= (int)target.RuntimeVariations[i].FirstSegmentIndex &&
                        first < (int)target.RuntimeVariations[i].FirstSegmentIndex + (int)target.RuntimeVariations[i].SegmentCount)
                        return "sound import: variations share a segment row; use an independent audio package/template.";
                var wav = ParseWav(wavPath); // reject malformed/oversized input before starting an encoder
                if (wav.Channels != (int)target.ChannelCount)
                    return "sound import: WAV channels differ from the destination wave. Use matching channels or a matching template; refusing to change other variations.";
            }
            catch (Exception ex) { return "sound import: " + ex.Message; }
            byte[] chunkData = null;

            // EALayer3 (smaller, game-native) when requested or available; needs ffmpeg + the encoder.
            if (mode == "ealayer3" || (mode == "auto" && EALayer3Encoder.IsAvailable()))
            {
                chunkData = EALayer3Encoder.Encode(wavPath, m => logger?.Log("[SoundImport] {0}", m));
                if (chunkData == null && mode == "ealayer3")
                    return "sound import: EALayer3 encode failed — needs ffmpeg.exe + ealayer3_encode.exe present (thirdparty/). Use --pcm for the uncompressed path.";
                // mode=auto: fall through to PCM on failure.
            }

            if (chunkData == null)
            {
                try { chunkData = BuildPcm16BigChunk(wavPath); }
                catch (Exception ex) { return "sound import: could not read/pack WAV: " + ex.Message; }
            }
            if (chunkData == null || chunkData.Length < 16) return "sound import: produced empty audio chunk.";

            EbxAsset asset;
            try { asset = SoundWavePort.CloneEbx(App.AssetManager.GetEbx(entry)); }
            catch (Exception ex) { return "sound import: load failed: " + ex.Message; }

            dynamic soundWave = asset.RootObject;
            ChunkAssetEntry changedChunk;
            byte[] changedData;
            try
            {
                ImportIntoVariation(soundWave, variationIndex, chunkData, out changedChunk, out changedData);
                asset = SoundWavePort.CloneEbx(asset); // target serialization must succeed before changing the chunk
            }
            catch (Exception ex) { return "sound import: " + ex.Message; }

            byte[] previousData = SoundWavePort.ReadChunk(changedChunk);
            bool wasModified = changedChunk.IsModified;
            try
            {
                if (!App.AssetManager.ModifyChunk(changedChunk.Id, changedData, null))
                    throw new IOException("ModifyChunk did not update the audio payload.");
                App.AssetManager.ModifyEbx(entry.Name, asset);
            }
            catch (Exception ex)
            {
                try
                {
                    if (wasModified) App.AssetManager.ModifyChunk(changedChunk.Id, previousData, null);
                    else App.AssetManager.RevertAsset(changedChunk);
                }
                catch (Exception rollback) { return "sound import: " + ex.Message + "; chunk rollback failed: " + rollback.Message; }
                return "sound import: " + ex.Message;
            }

            logger?.Log("[SoundImport] imported {0} -> {1} variation #{2} ({3} bytes)",
                Path.GetFileName(wavPath), entry.Name, variationIndex, chunkData.Length);
            return null;
        }

        // --- WAV parsing (minimal RIFF/PCM reader; no NAudio) ---

        internal struct WavData { public int Channels; public int SampleRate; public int BitsPerSample; public byte[] Pcm; }

        internal static WavData ParseWav(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F'
                || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
                throw new InvalidDataException("not a RIFF/WAVE file");

            var w = new WavData();
            int pos = 12;
            bool haveFmt = false, haveData = false;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int size = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (size < 0 || (long)body + size > b.Length) throw new InvalidDataException("truncated WAV chunk");
                if (id == "fmt ")
                {
                    if (size < 16) throw new InvalidDataException("truncated WAV fmt chunk");
                    int fmt = BitConverter.ToUInt16(b, body);
                    w.Channels      = BitConverter.ToUInt16(b, body + 2);
                    w.SampleRate    = BitConverter.ToInt32(b, body + 4);
                    w.BitsPerSample = BitConverter.ToUInt16(b, body + 14);
                    if (fmt != 1) throw new InvalidDataException("WAV must be uncompressed PCM (fmt=1), got " + fmt);
                    haveFmt = true;
                }
                else if (id == "data")
                {
                    int len = Math.Min(size, b.Length - body);
                    w.Pcm = new byte[len];
                    Array.Copy(b, body, w.Pcm, 0, len);
                    haveData = true;
                }
                pos = body + size + (size & 1); // chunks are word-aligned
                if (haveFmt && haveData) break;
            }
            if (!haveFmt || !haveData) throw new InvalidDataException("WAV missing fmt/data chunk");
            if (w.BitsPerSample != 16) throw new InvalidDataException("WAV must be 16-bit PCM, got " + w.BitsPerSample + "-bit");
            if (w.Channels < 1 || w.Channels > 8) throw new InvalidDataException("WAV channel count must be 1..8");
            if (w.SampleRate < 1 || w.SampleRate > ushort.MaxValue) throw new InvalidDataException("WAV sample rate exceeds the Frostbite header range");
            if (w.Pcm.Length == 0 || w.Pcm.Length % (w.Channels * 2) != 0) throw new InvalidDataException("WAV contains no complete sample frames");
            if (w.Pcm.Length / w.Channels / 2 > 0x00ffffff) throw new InvalidDataException("WAV exceeds the per-segment sample limit; split it into segments");
            return w;
        }

        // --- Frostbite PCM16Big chunk packing (ported from SoundEditorPlugin's PCM path) ---

        private static byte[] BuildPcm16BigChunk(string wavPath)
        {
            WavData w = ParseWav(wavPath);
            int channels = w.Channels;
            byte[] pcm = w.Pcm;

            using (var nw = new NativeWriter(new MemoryStream(), false, false))
            {
                int totalSamples = 0;
                nw.Write(1207959564, Endian.Big);                       // chunk header
                ((BinaryWriter)nw).Write((byte)18);                     // codec (PCM16Big)
                ((BinaryWriter)nw).Write((byte)((channels - 1) << 2));  // channel config
                nw.Write((ushort)w.SampleRate, Endian.Big);
                long countPos = nw.Position;
                nw.Write(1073741824, Endian.Big);                       // total-samples placeholder (0x40000000)

                int srcPos = 0;
                while (srcPos < pcm.Length)
                {
                    int blockBytes = 19456 * channels;
                    int n = Math.Min(blockBytes, pcm.Length - srcPos);
                    if (n <= 0) break;
                    nw.Write((n + 8) | 0x44000000, Endian.Big);         // block header
                    nw.Write(n / channels / 2, Endian.Big);            // samples in block
                    for (int i = 0; i < n / 2; i++)
                    {
                        short s = BitConverter.ToInt16(pcm, srcPos + i * 2);
                        nw.Write(s, Endian.Big);                        // little-endian source -> big-endian out
                    }
                    totalSamples += n / channels / 2;
                    srcPos += n;
                }
                nw.Write(1157627908, Endian.Big);                       // end marker
                nw.Position = countPos;
                nw.Write(totalSamples | 0x40000000, Endian.Big);
                return nw.ToByteArray();
            }
        }

        // --- Variation wiring (ported from SoundEditorPlugin.FrostySoundWaveEditor.ImportIntoVariation) ---

        private static void ImportIntoVariation(dynamic soundWave, int variationIndex, byte[] chunkData, out ChunkAssetEntry chunkEntry, out byte[] outData)
        {
            if (variationIndex < 0 || variationIndex >= (int)soundWave.RuntimeVariations.Count)
                throw new Exception("variation index " + variationIndex + " is out of range (0.." + ((int)soundWave.RuntimeVariations.Count - 1) + ").");

            dynamic variation = soundWave.RuntimeVariations[variationIndex];
            int chunkIndex = (int)variation.ChunkIndex;
            if (chunkIndex < 0 || chunkIndex >= (int)soundWave.Chunks.Count)
                throw new Exception("variation points to invalid ChunkIndex " + chunkIndex + ".");

            dynamic chunkRef = soundWave.Chunks[chunkIndex];
            Guid chunkId = chunkRef.ChunkId;
            chunkEntry = App.AssetManager.GetChunkEntry(chunkId);
            if (chunkEntry == null) throw new Exception("chunk " + chunkId + " not found in asset manager.");

            // Count how many variations share this chunk.
            int refCount = 0;
            foreach (dynamic v in soundWave.RuntimeVariations)
                if ((Guid)soundWave.Chunks[(int)v.ChunkIndex].ChunkId == chunkId) refCount++;

            outData = chunkData;
            uint newOffset = 0;
            if (refCount > 1)
            {
                // Append rather than shifting a shared chunk: other variations may use segment
                // indices in a different order, or carry seek tables between the audio ranges.
                byte[] original = SoundWavePort.ReadChunk(chunkEntry);
                newOffset = checked((uint)original.Length);
                outData = original.Concat(chunkData).ToArray();
            }

            foreach (dynamic c in soundWave.Chunks)
                if ((Guid)c.ChunkId == chunkId) c.ChunkSize = (uint)outData.Length;
            soundWave.Seekable = false;
            variation.PersistentDataSize = 0u;
            soundWave.Segments[(int)variation.FirstSegmentIndex].SamplesOffset = newOffset;
            soundWave.Segments[(int)variation.FirstSegmentIndex].SegmentLength = (float)AudioSegmentHeader.Read(chunkData, 0).Seconds;
            if ((int)variation.FirstSegmentIndex < (int)soundWave.Segments.Count)
                soundWave.Segments[(int)variation.FirstSegmentIndex].SeekTableOffset = uint.MaxValue;
            variation.SegmentCount = (byte)1;
            variation.FirstLoopSegmentIndex = (byte)0;
            variation.LastLoopSegmentIndex = (byte)0;
        }
    }
}
