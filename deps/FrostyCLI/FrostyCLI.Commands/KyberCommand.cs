using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    // `kyber declare|show|remove`: the map list Kyber (the community online server) offers when hosting.
    //   kyber declare --level <level path> --name <display name> --modes SkirmishBlast,Blast,... [--image <png|jpg>]
    //   kyber show
    //   kyber remove --level <level path>
    // Stored in the project as Kyber Level Declarator's chunk BEBEBEBE-...; `mod build` ships it as the embedded resource
    // KyberLevelDeclarationData (and leaves the chunk out), as the plugin does.
    public class KyberCommand : ICommand
    {
        public string Name => "kyber";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1) { Console.Error.WriteLine("usage: kyber <declare|show|remove> ..."); return 1; }
            string verb = args[0].ToLowerInvariant();
            string level = null, name = null, modes = null, image = null;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> next = () => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " needs a value");
                if (a == "--level") level = next();
                else if (a == "--name") name = next();
                else if (a == "--modes") modes = next();
                else if (a == "--image") image = next();
                else { Console.Error.WriteLine("kyber: unknown option " + a); return 1; }
            }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("kyber: no project loaded (use --project or --save-as)."); return 1; }
            JObject d = Read();
            switch (verb)
            {
                case "show":
                    Console.Out.WriteLine(Redact(d).ToString());
                    return 0;
                case "declare":
                    if (level == null || modes == null) { Console.Error.WriteLine("usage: kyber declare --level <level path> --name <display name> --modes <mode ids> [--image <file>]"); return 1; }
                    if (App.AssetManager.GetEbxEntry(level) == null && App.AssetManager.GetEbxEntry(level + "/description") == null)
                        logger.LogWarning("kyber: no LevelData named " + level + " in the project -- Kyber launches the id as given");
                    string b64 = null;
                    if (image != null)
                    {
                        try { b64 = Convert.ToBase64String(Jpeg(image)); }
                        catch (Exception ex) { Console.Error.WriteLine("kyber: --image: " + ex.Message); return 1; }
                    }
                    KyberDeclaration.UpsertMap(d, name, level, modes.Split(','), b64);
                    break;
                case "remove":
                    if (level == null || !KyberDeclaration.RemoveMap(d, level)) { Console.Error.WriteLine("kyber: no declared map " + level); return 1; }
                    break;
                default:
                    Console.Error.WriteLine("kyber: unknown subcommand " + verb); return 1;
            }
            Write(d);
            formatter.WriteResult("OK", "kyber " + verb, new Dictionary<string, string>
            {
                { "maps", string.Join(", ", ((JArray)d["maps"]).Select(m => (string)m["name"] + " = " + (string)m["id"] + " [" + string.Join(",", ((JArray)m["supportedModes"]).Select(x => (string)x)) + "]" + (string.IsNullOrEmpty((string)m["image"]) ? "" : " +image"))) },
                { "chunk", KyberDeclaration.ChunkId.ToString() },
            });
            return 0;
        }

        public static JObject Read()
        {
            ChunkAssetEntry e = App.AssetManager.GetChunkEntry(KyberDeclaration.ChunkId);
            if (e == null) return KyberDeclaration.Empty();
            using (Stream s = App.AssetManager.GetChunk(e))
            using (var ms = new MemoryStream()) { s.CopyTo(ms); return KyberDeclaration.Parse(ms.ToArray()); }
        }

        public static void Write(JObject d)
        {
            byte[] data = KyberDeclaration.ToBytes(d);
            if (App.AssetManager.GetChunkEntry(KyberDeclaration.ChunkId) == null)
                App.AssetManager.AddChunk(data, KyberDeclaration.ChunkId);
            else
                App.AssetManager.ModifyChunk(KyberDeclaration.ChunkId, data);
        }

        // what the plugin does to an image: at most 1024 px on the long side, JPEG quality 80
        public static byte[] Jpeg(string file)
        {
            using (var src = Image.FromFile(file))
            {
                Image img = src;
                if (src.Width > 1024 || src.Height > 1024)
                {
                    float f = Math.Max(src.Width / 1024f, src.Height / 1024f);
                    var b = new Bitmap((int)(src.Width / f), (int)(src.Height / f));
                    using (var g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, b.Width, b.Height);
                    }
                    img = b;
                }
                var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                var p = new EncoderParameters(1);
                p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                using (var ms = new MemoryStream())
                {
                    img.Save(ms, enc, p);
                    if (!ReferenceEquals(img, src)) img.Dispose();
                    return ms.ToArray();
                }
            }
        }

        private static JObject Redact(JObject d)
        {
            var c = (JObject)d.DeepClone();
            foreach (JObject m in ((JArray)c["maps"]).OfType<JObject>().Concat(((JArray)c["modes"]).OfType<JObject>()))
                if (m["image"] != null && ((string)m["image"] ?? "").Length > 0) m["image"] = "<" + ((string)m["image"]).Length + " base64 chars>";
            return c;
        }
    }
}
