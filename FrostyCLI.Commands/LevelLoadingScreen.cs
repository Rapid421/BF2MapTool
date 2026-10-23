using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyLevelTools;

namespace FrostyCLI.Commands
{
    // `level loading-screen <level> --like <shipped level> [--movie <file.webm> | --image <file> [--seconds s]]`
    //
    // The multiplayer loading screen of a level is chosen by level path: UI/Configuration/WSUILoadingScreenSettings
    // .MPLoadLevelScreenOverrides[] = { LevelName, LoadingScreenAssetName, IntroMovieName, MovieName, DisplayTimeout }
    // (assets named by string, loaded from the UI superbundle's loadingscreens_bundle). A level with no entry gets the
    // default screen and its Yavin placeholder videos. The screen itself (WSUILoadingScreenAsset) is a full-screen intro +
    // loop video: MovieTexture2Asset = one chunk holding a .webm (Planet_Tatooine_01.webm, 2.15 MB).
    //
    // Without --movie/--image the level gets the --like level's screen. With them it gets its own: the like-level's
    // movie asset and screen asset cloned (UI/Video/Loading/<Leaf>, UI/LoadingScreens/MPLoadLevel/MPLoadLevel<Leaf>Asset)
    // into the same bundles, the movie pointing at a new chunk with the given video; --image encodes a still (or a slow
    // push-in) into VP8 WebM with ffmpeg (FFMPEG, PATH or imageio_ffmpeg).
    public static class LevelLoadingScreen
    {
        public const string SettingsAsset = "UI/Configuration/WSUILoadingScreenSettings";

        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = null, like = null, movie = null, image = null, codec = "vp8";
            double seconds = 10;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> next = () => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " needs a value");
                if (a == "--like") like = next();
                else if (a == "--movie") movie = next();
                else if (a == "--image") image = next();
                else if (a == "--seconds") seconds = double.Parse(next(), CultureInfo.InvariantCulture);
                else if (a == "--codec") codec = next();
                else if (a.StartsWith("--")) { Console.Error.WriteLine("level loading-screen: unknown option " + a); return 1; }
                else level = a;
            }
            if (level == null || like == null)
            {
                Console.Error.WriteLine("usage: level loading-screen <level> --like <shipped level with a loading screen> [--movie <file.webm> | --image <file> [--seconds s] [--codec vp8|vp9]]");
                return 1;
            }
            byte[] video = null;
            try
            {
                if (movie != null) video = File.ReadAllBytes(movie);
                else if (image != null) video = EncodeStill(image, seconds, codec, logger);
            }
            catch (Exception ex) { Console.Error.WriteLine("level loading-screen: " + ex.Message); return 1; }
            Dictionary<string, string> r;
            try { r = Apply(App.AssetManager, level, like, video, s => logger.Log("level loading-screen: " + s)); }
            catch (Exception ex) { Console.Error.WriteLine("level loading-screen: " + ex.Message); return 1; }
            formatter.WriteResult("OK", "level loading-screen", r);
            return 0;
        }

        public static Dictionary<string, string> Apply(AssetManager am, string level, string like, byte[] video, Action<string> log)
        {
            level = level.Trim().Trim('/');
            EbxAssetEntry se = am.GetEbxEntry(SettingsAsset) ?? throw new InvalidOperationException(SettingsAsset + " not found");
            EbxAsset settings = am.GetEbx(se);
            IList list = (IList)Get(settings.RootObject, "MPLoadLevelScreenOverrides");
            object host = list.Cast<object>().FirstOrDefault(x => Same(Get(x, "LevelName"), like))
                ?? throw new InvalidOperationException("no MP loading screen is set for " + like + " (" + list.Count + " entries in " + SettingsAsset + ")");
            string leaf = level.Substring(level.LastIndexOf('/') + 1);
            string screenName = Str(Get(host, "LoadingScreenAssetName")), introName = Str(Get(host, "IntroMovieName")), movieName = Str(Get(host, "MovieName"));
            var r = new Dictionary<string, string> { { "level", level }, { "like", like } };

            if (video != null)
            {
                // the movie: the like-level's MovieTexture2Asset cloned, its chunk replaced by the new video
                EbxAssetEntry hostMovie = am.GetEbxEntry(movieName) ?? throw new InvalidOperationException("movie " + movieName + " not found");
                string newMovie = "UI/Video/Loading/" + leaf;
                EbxAssetEntry mv = am.GetEbxEntry(newMovie);
                EbxAsset mva;
                if (mv == null)
                {
                    mva = LevelDuplicator.CloneAsset(am.GetEbx(hostMovie));
                    mva.SetFileGuid(Guid.NewGuid());
                    Set(mva.RootObject, "Name", newMovie);
                    mv = am.AddEbx(newMovie, mva, new int[0]);
                    foreach (int b in hostMovie.Bundles) mv.AddToBundle(b);
                }
                else mva = am.GetEbx(mv);
                ChunkAssetEntry hostChunk = am.GetChunkEntry((Guid)Get(am.GetEbx(hostMovie).RootObject, "ChunkGuid"));
                int[] bundles = hostChunk != null && hostChunk.Bundles.Count > 0 ? hostChunk.Bundles.ToArray() : hostMovie.Bundles.ToArray();
                Guid chunk = am.AddChunk(video, null, null, bundles);
                ChunkAssetEntry ce = am.GetChunkEntry(chunk);
                if (ce != null) mv.LinkAsset(ce);   // what Frosty does for an asset's chunk: H32 = hash of the lower-case ebx name
                Set(mva.RootObject, "ChunkGuid", chunk);
                Set(mva.RootObject, "ChunkSize", (uint)video.Length);
                Set(mva.RootObject, "MovieFilename", leaf + ".webm");
                am.ModifyEbx(newMovie, mva);
                log("movie " + newMovie + ": chunk " + chunk + ", " + video.Length + " B, bundles " + string.Join(",", bundles.Select(b => am.GetBundleEntry(b)?.Name)));

                // the screen: the like-level's WSUILoadingScreenAsset cloned, both videos pointing at the new movie
                EbxAssetEntry hostScreen = am.GetEbxEntry(screenName) ?? throw new InvalidOperationException("loading screen " + screenName + " not found");
                string newScreen = "UI/LoadingScreens/MPLoadLevel/MPLoadLevel" + leaf + "Asset";
                EbxAssetEntry sc = am.GetEbxEntry(newScreen);
                EbxAsset sca;
                if (sc == null)
                {
                    sca = LevelDuplicator.CloneAsset(am.GetEbx(hostScreen));
                    sca.SetFileGuid(Guid.NewGuid());
                    Set(sca.RootObject, "Name", newScreen);
                    sc = am.AddEbx(newScreen, sca, new int[0]);
                    foreach (int b in hostScreen.Bundles) sc.AddToBundle(b);
                }
                else sca = am.GetEbx(sc);
                var movieRef = new PointerRef(new EbxImportReference { FileGuid = mv.Guid, ClassGuid = (Guid)((dynamic)mva.RootObject).GetInstanceGuid().ExportedGuid });
                Set(sca.RootObject, "IntroMovie", movieRef);
                Set(sca.RootObject, "Movie", movieRef);
                am.ModifyEbx(newScreen, sca);
                screenName = newScreen; introName = newMovie; movieName = newMovie;
                r["movie"] = newMovie; r["screen"] = newScreen; r["videoBytes"] = video.Length.ToString(CultureInfo.InvariantCulture);
            }

            // the override entry for the level (replaced when it is already there)
            bool fresh = SetOverride(list, host, level, screenName, introName, movieName);
            am.ModifyEbx(se.Name, settings);
            log((fresh ? "added" : "updated") + " the MP loading screen of " + level + ": " + screenName + " (intro " + introName + ", loop " + movieName + ")");
            r["screen"] = screenName;
            return r;
        }

        // Adds (or updates) the MPLoadLevelScreenOverrides entry for <level>: a copy of the host's entry (game mode, widget,
        // timeout, versions) with the level's name, screen and videos. True when a new entry was added.
        public static bool SetOverride(IList list, object host, string level, string screen, string intro, string movie)
        {
            level = level.Trim().Trim('/');
            int at = -1;
            for (int i = 0; i < list.Count; i++) if (Same(Get(list[i], "LevelName"), level)) { at = i; break; }
            object entry = at >= 0 ? list[at] : Activator.CreateInstance(host.GetType());
            if (at < 0)
                foreach (var p in host.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
                    p.SetValue(entry, CopyValue(p.GetValue(host)));
            Set(entry, "LevelName", level);
            Set(entry, "LoadingScreenAssetName", screen);
            Set(entry, "IntroMovieName", intro);
            Set(entry, "MovieName", movie);
            if (at < 0) list.Add(entry); else list[at] = entry;
            return at < 0;
        }

        // a still image as a VP8/VP9 WebM: a slow 6% push-in over <seconds> at 30 fps, 1920x1080
        public static byte[] EncodeStill(string image, double seconds, string codec, ExportLogger logger)
        {
            string ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg not found (set FFMPEG, put it on PATH, or pip install imageio-ffmpeg)");
            string outFile = Path.Combine(Path.GetTempPath(), "frostycli-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".webm");
            int frames = (int)Math.Round(seconds * 30);
            string vf = string.Format(CultureInfo.InvariantCulture,
                "scale=2112:1188:force_original_aspect_ratio=increase,crop=2112:1188,zoompan=z='1+0.06*on/{0}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d={0}:s=1920x1080:fps=30,format=yuv420p", frames);
            string lib = codec == "vp9" ? "libvpx-vp9" : "libvpx";
            string argsLine = string.Format(CultureInfo.InvariantCulture,
                "-y -loglevel error -i \"{0}\" -vf \"{1}\" -t {2} -an -c:v {3} -b:v 2M -auto-alt-ref 0 \"{4}\"", image, vf, seconds, lib, outFile);
            var psi = new ProcessStartInfo(ffmpeg, argsLine) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(outFile)) throw new InvalidOperationException("ffmpeg failed: " + err.Trim());
            }
            byte[] data = File.ReadAllBytes(outFile);
            try { File.Delete(outFile); } catch { }
            logger.Log("level loading-screen: " + image + " -> " + lib + " WebM, " + seconds + " s, " + data.Length + " B");
            return data;
        }

        public static string FindFfmpeg()
        {
            string env = Environment.GetEnvironmentVariable("FFMPEG");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string f = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(f)) return f; } catch { }
            }
            string py = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
            if (Directory.Exists(py))
                foreach (string f in Directory.GetFiles(py, "ffmpeg-win*.exe", SearchOption.AllDirectories)) return f;
            return null;
        }

        private static bool Same(object a, string b) => string.Equals(Str(a), b?.Trim().Trim('/'), StringComparison.OrdinalIgnoreCase);
        private static string Str(object v) => v?.ToString() ?? "";
        private static object Get(object o, string name) => o?.GetType().GetProperty(name)?.GetValue(o);

        private static void Set(object o, string name, object value)
        {
            var p = o.GetType().GetProperty(name) ?? throw new InvalidOperationException(o.GetType().Name + " has no " + name);
            Type t = p.PropertyType;
            if (t.Name == "CString" && value is string s) value = Activator.CreateInstance(t, s);
            p.SetValue(o, value);
        }

        // shallow copy of list values (IncludedInVersions) so the new entry does not share the host's list
        private static object CopyValue(object v)
        {
            if (v is IList l && !(v is Array))
            {
                var c = (IList)Activator.CreateInstance(v.GetType());
                foreach (object x in l) c.Add(x);
                return c;
            }
            return v;
        }
    }
}
