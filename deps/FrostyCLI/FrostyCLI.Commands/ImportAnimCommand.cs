using System;
using System.Collections.Generic;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Core.Animation;

namespace FrostyCLI.Commands
{
    // Imports a SEAnim (.seanim) into the game by re-encoding it over an existing hero-strike clip
    // in the antstate bank, then modifying that resource in the loaded project. Build a .fbmod with
    // `mod build` afterwards.
    //
    //   import-anim <file.seanim> --clip <name> [<state-machine-ebx>] [--skeleton <path>]
    //
    // The new animation is appended into the bank and the target clip's instance fields are
    // repointed to it (other clips untouched). Bones with no rig DOF are skipped.
    public class ImportAnimCommand : ICommand
    {
        public string Name => "import-anim";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string seanimFile = null, clipName = null, skeleton = AnimSkeleton.DefaultSkeletonPath;
            string ebxPath = ExportAnimCommand.AntStateClipSource_DefaultStateMachine;
            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--clip"          && i + 1 < args.Length) clipName  = args[++i];
                else if (args[i] == "--skeleton"      && i + 1 < args.Length) skeleton  = args[++i];
                else if (args[i] == "--state-machine" && i + 1 < args.Length) ebxPath   = args[++i];
                else if (!args[i].StartsWith("--")) seanimFile = args[i];
            }

            if (seanimFile == null) { Console.Error.WriteLine("import-anim: missing <file.seanim>"); return 1; }
            if (clipName == null)   { Console.Error.WriteLine("import-anim: missing --clip <name>"); return 1; }
            if (!File.Exists(seanimFile)) { Console.Error.WriteLine("import-anim: file not found: " + seanimFile); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("import-anim: AssetManager not initialized. Pass --game-path."); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("import-anim: no project loaded. Run 'project new' or 'project load' first."); return 1; }

            try
            {
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(ExportAnimCommand.SharedAnimBankRes);
                if (resEntry == null) { Console.Error.WriteLine("import-anim: shared anim bank not found."); return 1; }

                Guid guid = Guid.Empty;
                foreach (StrikeClip s in ExportAnimCommand.LoadStrikes(ebxPath))
                    if (string.Equals(s.FullName, clipName, StringComparison.OrdinalIgnoreCase)) { guid = s.ControllerAssetGuid; break; }
                if (guid == Guid.Empty) { Console.Error.WriteLine("import-anim: clip not found: " + clipName); return 1; }

                byte[] bank = LoadRes(resEntry);
                SeAnim anim = SeAnimReader.Read(seanimFile);

                AntAnimationDecoder.EncodeResult res;
                using (var dec = new AntAnimationDecoder(new MemoryStream(bank)))
                {
                    if (dec.RigDofCount == 0) { Console.Error.WriteLine("import-anim: rig dof map empty."); return 1; }
                    res = dec.EncodeClip(guid, anim, bank);
                }

                App.AssetManager.ModifyRes(resEntry.Name, res.Bank);
                if (!App.AssetManager.GetResEntry(resEntry.Name).IsModified)
                { Console.Error.WriteLine("import-anim: ModifyRes did not take effect."); return 1; }

                var f = new Dictionary<string, string>();
                f["clip"]       = clipName;
                f["channels"]   = res.Channels.ToString();
                f["frames"]     = res.Frames.ToString();
                f["unmapped"]   = res.Unmapped.ToString();
                f["bankGrewBy"] = (res.Bank.Length - bank.Length).ToString();
                formatter.WriteResult("OK", "import-anim", f);
                Console.Error.WriteLine("import-anim: clip modified in project. Run 'mod build <out.fbmod>' to package.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("import-anim: " + ex.Message);
                logger.LogError("import-anim failed: {0}", ex);
                return 1;
            }
        }

        private static byte[] LoadRes(ResAssetEntry entry)
        {
            System.Reflection.MethodInfo m = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
            object raw = m.Invoke(App.AssetManager, new object[] { entry });
            if (raw is byte[] b) return b;
            using (var ms = new MemoryStream()) { ((Stream)raw).CopyTo(ms); return ms.ToArray(); }
        }
    }
}
