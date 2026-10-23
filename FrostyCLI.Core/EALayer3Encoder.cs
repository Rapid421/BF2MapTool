using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace FrostyCLI.Core
{
    /// <summary>
    /// WAV to EALayer3 using ffmpeg plus the bundled encoder. Preserves mono/stereo and
    /// supported sample rates; other inputs return null so auto mode can preserve them as PCM.
    /// The WAV conversion adds encoder padding; native audio packages preserve original bytes.
    /// </summary>
    public static class EALayer3Encoder
    {
        private static string _encoderPath;
        private static string _ffmpegPath;

        private static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        public static string FindEncoder()
        {
            if (!string.IsNullOrEmpty(_encoderPath) && File.Exists(_encoderPath)) return _encoderPath;
            foreach (string p in new[]
            {
                Path.Combine(BaseDir, "ealayer3_encode.exe"),
                Path.Combine(BaseDir, "thirdparty", "ealayer3_encode.exe"),
                Path.Combine(BaseDir, "Plugins", "ealayer3_encode.exe"),
                Path.Combine(BaseDir, "dandev-el3.exe"),
            })
                if (File.Exists(p)) { _encoderPath = p; return p; }
            return null;
        }

        public static string FindFFmpeg()
        {
            if (!string.IsNullOrEmpty(_ffmpegPath) && File.Exists(_ffmpegPath)) return _ffmpegPath;
            foreach (string p in new[]
            {
                Path.Combine(BaseDir, "thirdparty", "ffmpeg.exe"),
                Path.Combine(BaseDir, "ffmpeg.exe"),
                Path.Combine(BaseDir, "Plugins", "ffmpeg.exe"),
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
            })
                if (File.Exists(p)) { _ffmpegPath = p; return p; }
            string env = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(env))
                foreach (string dir in env.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(p)) { _ffmpegPath = p; return p; }
                }
            return null;
        }

        /// <summary>True only if both the encoder and ffmpeg are present.</summary>
        public static bool IsAvailable() => FindEncoder() != null && FindFFmpeg() != null;

        /// <summary>Encodes a WAV/audio file to an EALayer3 Frostbite chunk, or null on any failure.</summary>
        public static byte[] Encode(string inputPath, Action<string> log = null)
        {
            string encoder = FindEncoder();
            string ffmpeg = FindFFmpeg();
            if (encoder == null || ffmpeg == null || !File.Exists(inputPath)) return null;

            HeadlessSoundImporter.WavData input;
            try { input = HeadlessSoundImporter.ParseWav(inputPath); }
            catch (Exception ex) { log?.Invoke("Invalid WAV: " + ex.Message); return null; }
            // MP3 represents mono/stereo. Wider beds keep their channels through the PCM or
            // lossless package route; never silently downmix them.
            if (input.Channels > 2 || (input.SampleRate != 32000 && input.SampleRate != 44100 && input.SampleRate != 48000))
            { log?.Invoke("EALayer3 supports mono/stereo at 32/44.1/48 kHz; use PCM to preserve this input."); return null; }

            string mp3 = Path.Combine(Path.GetTempPath(), "frosty_el3_" + Guid.NewGuid().ToString("N") + ".mp3");
            string outDir = Path.Combine(Path.GetTempPath(), "frosty_el3out_" + Guid.NewGuid().ToString("N"));
            string outBin = Path.Combine(outDir, "out.bin");
            try
            {
                string ffArgs = $"-y -i \"{inputPath}\" -af \"adelay=15|15,apad=pad_dur=0.1\" -ac {input.Channels} -ar {input.SampleRate} -codec:a libmp3lame -b:a 192k -reservoir 0 -write_xing 0 -map_metadata -1 \"{mp3}\"";
                if (!RunProcessSimple(ffmpeg, ffArgs, 60000) || !File.Exists(mp3) || new FileInfo(mp3).Length < 128)
                { log?.Invoke("ffmpeg MP3 conversion failed"); return null; }

                Directory.CreateDirectory(outDir);
                string encArgs = $"-E -b- --parser5 -o \"{outBin}\" \"{mp3}\"";
                byte[] raw = RunEncoderWithPolling(encoder, encArgs, outBin, log);
                if (raw == null || raw.Length < 16) { log?.Invoke("encoder produced no usable output"); return null; }
                byte[] result = FinalizeChunk(raw, log, input.Channels, input.SampleRate);
                if (result == null) return null;
                var header = AudioSegmentHeader.Read(result, 0);
                header.ValidateBlocks(result, 0);
                if (header.Channels != input.Channels || header.SampleRate != input.SampleRate)
                { log?.Invoke("Encoder changed the channel count/sample rate; refusing output."); return null; }
                return result;
            }
            catch (Exception ex) { log?.Invoke("EALayer3 encode error: " + ex.Message); return null; }
            finally
            {
                try { if (File.Exists(mp3)) File.Delete(mp3); } catch { }
                try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
            }
        }

        // --- process helpers ---

        private static bool RunProcessSimple(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            Process p = Process.Start(psi);
            if (p == null) return false;
            var t1 = new Thread(() => { try { p.StandardOutput.ReadToEnd(); } catch { } }) { IsBackground = true };
            var t2 = new Thread(() => { try { p.StandardError.ReadToEnd(); } catch { } }) { IsBackground = true };
            t1.Start(); t2.Start();
            try { p.StandardInput.Close(); } catch { }
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return false; }
            t1.Join(2000); t2.Join(2000);
            return p.ExitCode == 0;
        }

        // The encoder may not self-terminate cleanly; poll the output file until it stabilises, then stop.
        private static byte[] RunEncoderWithPolling(string exe, string args, string outputPath, Action<string> log)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, Arguments = args, WorkingDirectory = Path.GetDirectoryName(exe) ?? BaseDir,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            Process p;
            try { p = Process.Start(psi); } catch (Exception ex) { log?.Invoke("failed to start encoder: " + ex.Message); return null; }
            if (p == null) return null;
            var t1 = new Thread(() => { try { p.StandardOutput.ReadToEnd(); } catch { } }) { IsBackground = true };
            var t2 = new Thread(() => { try { p.StandardError.ReadToEnd(); } catch { } }) { IsBackground = true };
            t1.Start(); t2.Start();
            try { p.StandardInput.Close(); } catch { }

            long last = 0; int stable = 0; var sw = Stopwatch.StartNew(); bool ok = false;
            while (sw.ElapsedMilliseconds < 45000 && !p.HasExited)
            {
                Thread.Sleep(500);
                if (p.HasExited) break;
                long len = 0;
                try { if (File.Exists(outputPath)) len = new FileInfo(outputPath).Length; } catch { }
                if (len > 0 && len == last) { if (++stable >= 6) { ok = true; break; } } else stable = 0;
                last = len;
            }
            if (!p.HasExited) { try { p.Kill(); } catch { } try { p.WaitForExit(3000); } catch { } Thread.Sleep(300); }
            t1.Join(3000); t2.Join(3000);
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 8) return null;
            return File.ReadAllBytes(outputPath);
        }

        // --- chunk finalisation (ported) ---

        private static byte[] FinalizeChunk(byte[] raw, Action<string> log, int channels, int sampleRate)
        {
            if (raw == null || raw.Length < 12) return null;
            return raw[0] == 72 ? FinalizeFrostbiteChunk(raw, log) : ConvertHeaderlessToFrostbite(raw, log, channels, sampleRate);
        }

        private static byte[] FinalizeFrostbiteChunk(byte[] raw, Action<string> log)
        {
            if (raw.Length < 12 || raw[0] != 72) return null;
            int codec = raw[4];
            int channelCfg = raw[5];
            int sampleRate = (raw[6] << 8) | raw[7];
            int i = 12, blocks = 0, totalSamples = 0;
            int blockLen, blockSamples;
            for (; i + 8 <= raw.Length; totalSamples += blockSamples, blocks++, i += blockLen)
            {
                int tag = raw[i];
                if (tag == 68)
                {
                    blockLen = ((raw[i] & 0xF) << 24) | (raw[i + 1] << 16) | (raw[i + 2] << 8) | raw[i + 3];
                    blockLen &= 0xFFFFFF;
                    blockSamples = (raw[i + 4] << 24) | (raw[i + 5] << 16) | (raw[i + 6] << 8) | raw[i + 7];
                    if (blockLen >= 8 && i + blockLen <= raw.Length) continue;
                    break;
                }
                break; // 0x45 end marker or unexpected
            }
            if (blocks == 0) return null;
            using (var ms = new MemoryStream())
            {
                WriteBE32(ms, 1207959564);
                ms.WriteByte((byte)codec); ms.WriteByte((byte)channelCfg);
                WriteBE16(ms, (ushort)sampleRate);
                WriteBE32(ms, totalSamples | 0x40000000);
                ms.Write(raw, 12, i - 12);
                WriteBE32(ms, 1157627908);
                log?.Invoke($"EaLayer32Pcm: {blocks} blocks, {totalSamples} samples @ {sampleRate} Hz");
                return ms.ToArray();
            }
        }

        private static byte[] ConvertHeaderlessToFrostbite(byte[] raw, Action<string> log, int channels, int sampleRate)
        {
            int pos = 0, blocks = 0, totalSamples = 0;
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[12], 0, 12); // header placeholder
                while (pos + 8 <= raw.Length)
                {
                    int flags = (raw[pos] << 8) | raw[pos + 1];
                    int blockLen = (raw[pos + 2] << 8) | raw[pos + 3];
                    if (blockLen < 8 || pos + blockLen > raw.Length) break;
                    int sampleCount = (raw[pos + 4] << 24) | (raw[pos + 5] << 16) | (raw[pos + 6] << 8) | raw[pos + 7];
                    int payload = blockLen - 8;
                    WriteBE32(ms, 0x44000000 | (payload + 8));
                    WriteBE32(ms, sampleCount);
                    if (payload > 0) ms.Write(raw, pos + 8, payload);
                    totalSamples += sampleCount; blocks++; pos += blockLen;
                    if ((flags & 0x8000) != 0) break;
                }
                WriteBE32(ms, 1157627908);
                ms.Position = 0;
                WriteBE32(ms, 1207959564);
                ms.WriteByte(22); ms.WriteByte((byte)((channels - 1) << 2));
                WriteBE16(ms, (ushort)sampleRate);
                WriteBE32(ms, totalSamples | 0x40000000);
                log?.Invoke($"EaLayer32Pcm (headerless): {blocks} blocks, {totalSamples} samples");
                return ms.ToArray();
            }
        }

        private static void WriteBE32(Stream s, int v)
        { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

        private static void WriteBE16(Stream s, ushort v)
        { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    }
}
