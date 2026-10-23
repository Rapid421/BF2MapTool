using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Core.Animation;

namespace FrostyCLI.Commands
{
    // anim-frame-offset --edit <clip>:<bone>:<dx>,<dy>,<dz> [--edit ...] [--res <bank res>]
    //                   --move-children <clip>:<bone>:<dx>,<dy>,<dz> [--skeleton <SkeletonAsset>]
    //                   --copy-pose <from clip>:<to clip>   (applied first)
    // Moves one bone's translation in FrameAnimationAsset poses (single-frame held poses such as the first-person
    // P_1p_<gun>_ZoomPose clips) by patching the floats in place in the bank res, then ModifyRes in the project.
    // Nothing structural changes, so no codec encoder is involved.
    // --move-children has the effect of translating <bone> by (dx,dy,dz) in its parent's frame, but writes the
    // offset into each child's translation (rotated into <bone>'s frame by the clip's own rotation of <bone>).
    // Use it when a later graph layer overrides the bone's own translation: in the 1p soldier graph,
    // 1P.CameraPosition.SF overrides SpineX.t (mask OnlySpineXTranslation.BML), so a SpineX edit never shows,
    // while its children (SpineXLeft/Right = arms, Wep_Root = gun) come from the action pose.
    public class AnimFrameOffsetCommand : ICommand
    {
        public string Name => "anim-frame-offset";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string res = ExportAnimCommand.SharedAnimBankRes, skeleton = "Walrus1p_HM_Ske";
            var edits = new List<string>(); var moves = new List<string>(); var copies = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--edit" && i + 1 < args.Length) edits.Add(args[++i]);
                else if (args[i] == "--move-children" && i + 1 < args.Length) moves.Add(args[++i]);
                else if (args[i] == "--copy-pose" && i + 1 < args.Length) copies.Add(args[++i]);
                else if (args[i] == "--skeleton" && i + 1 < args.Length) skeleton = args[++i];
                else if (args[i] == "--res" && i + 1 < args.Length) res = args[++i];
            }
            if (edits.Count == 0 && moves.Count == 0 && copies.Count == 0) { Console.Error.WriteLine("anim-frame-offset: usage --edit <clip>:<bone>:<dx>,<dy>,<dz> | --move-children <clip>:<bone>:<dx>,<dy>,<dz> [--skeleton S] [--res <bank>]"); return 1; }
            if (App.AssetManager == null || !Session.Project.IsLoaded) { Console.Error.WriteLine("anim-frame-offset: needs a loaded project (--project)."); return 1; }
            ResAssetEntry entry = App.AssetManager.GetResEntry(res);
            if (entry == null) { Console.Error.WriteLine("anim-frame-offset: res not found: " + res); return 1; }
            object raw = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) }).Invoke(App.AssetManager, new object[] { entry });
            byte[] bank;
            if (raw is byte[] b) bank = b; else using (var ms = new MemoryStream()) { ((Stream)raw).CopyTo(ms); bank = ms.ToArray(); }

            // --copy-pose <from>:<to> runs first: <to> becomes an exact copy of <from> (same channel table required),
            // so later --edit / --move-children on <to> start from the donor pose.
            foreach (string c in copies)
            {
                string[] p = c.Split(':');
                if (p.Length != 2) { Console.Error.WriteLine("anim-frame-offset: bad --copy-pose " + c); return 1; }
                string err; byte[] next; int copied, unmatched;
                using (var dec = new AntAnimationDecoder(new MemoryStream(bank))) next = dec.CopyFramePose(bank, p[0], p[1], out err, out copied, out unmatched);
                if (next == null) { Console.Error.WriteLine("anim-frame-offset: " + err); return 1; }
                bank = next;
                formatter.WriteResult("OK", "anim-frame-offset", new Dictionary<string, string> {
                    { "copy-pose", p[0] + " -> " + p[1] }, { "channels copied", copied.ToString() }, { "target channels kept (no match)", unmatched.ToString() } });
            }

            // Expand each --move-children into per-child --edit entries (offset in the bone's own frame).
            foreach (string m in moves)
            {
                string[] p = m.Split(':');
                if (p.Length != 3) { Console.Error.WriteLine("anim-frame-offset: bad --move-children '" + m + "'"); return 1; }
                string[] d = p[2].Split(',');
                if (d.Length != 3) { Console.Error.WriteLine("anim-frame-offset: bad offset in '" + m + "'"); return 1; }
                float[] v = { float.Parse(d[0], CultureInfo.InvariantCulture), float.Parse(d[1], CultureInfo.InvariantCulture), float.Parse(d[2], CultureInfo.InvariantCulture) };
                List<KeyValuePair<string, int>> joints;
                using (var br = new AntBankBrowser(new MemoryStream(bank))) joints = br.SkeletonJoints(skeleton);
                int bi = joints.FindIndex(j => string.Equals(j.Key, p[1], StringComparison.OrdinalIgnoreCase));
                if (bi < 0) { Console.Error.WriteLine("anim-frame-offset: no joint " + p[1] + " in skeleton " + skeleton); return 1; }
                float[] q; var have = new List<string>();
                using (var dec = new AntAnimationDecoder(new MemoryStream(bank)))
                {
                    q = dec.ReadFrameChannel(p[0], p[1], true);
                    for (int j = 0; j < joints.Count; j++)
                        if (joints[j].Value == bi)
                        {
                            if (dec.ReadFrameChannel(p[0], joints[j].Key, false) != null) have.Add(joints[j].Key);
                            else Console.Error.WriteLine("anim-frame-offset: note: child " + joints[j].Key + " has no translation channel in " + p[0] + " (stays put)");
                        }
                }
                if (q == null) { Console.Error.WriteLine("anim-frame-offset: " + p[1] + ".q is not a channel of " + p[0]); return 1; }
                float[] local = InverseRotate(q, v);
                for (int k = 0; k < 3; k++) if (Math.Abs(local[k]) < 1e-5f) local[k] = 0f; // float noise from the rotation
                string ls = string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", local[0], local[1], local[2]);
                foreach (string c in have) edits.Add(p[0] + ":" + c + ":" + ls);
            }

            foreach (string e in edits)
            {
                string[] p = e.Split(':');
                if (p.Length != 3) { Console.Error.WriteLine("anim-frame-offset: bad --edit '" + e + "'"); return 1; }
                string[] d = p[2].Split(',');
                if (d.Length != 3) { Console.Error.WriteLine("anim-frame-offset: bad offset in '" + e + "'"); return 1; }
                float dx = float.Parse(d[0], CultureInfo.InvariantCulture), dy = float.Parse(d[1], CultureInfo.InvariantCulture), dz = float.Parse(d[2], CultureInfo.InvariantCulture);
                string err; float[] before, after; byte[] next;
                using (var dec = new AntAnimationDecoder(new MemoryStream(bank)))
                    next = dec.PatchFrameTranslation(bank, p[0], p[1], dx, dy, dz, out err, out before, out after);
                if (next == null) { Console.Error.WriteLine("anim-frame-offset: " + err); return 1; }
                bank = next;
                formatter.WriteResult("OK", "anim-frame-offset", new Dictionary<string, string> {
                    { "clip", p[0] }, { "bone", p[1] },
                    { "before", string.Format(CultureInfo.InvariantCulture, "{0:0.#####},{1:0.#####},{2:0.#####}", before[0], before[1], before[2]) },
                    { "after", string.Format(CultureInfo.InvariantCulture, "{0:0.#####},{1:0.#####},{2:0.#####}", after[0], after[1], after[2]) } });
            }
            App.AssetManager.ModifyRes(entry.Name, bank);
            if (!App.AssetManager.GetResEntry(entry.Name).IsModified) { Console.Error.WriteLine("anim-frame-offset: ModifyRes did not take effect."); return 1; }
            return 0;
        }

        // conj(q) * v * q for a unit quaternion (x,y,z,w): expresses a parent-frame vector in the bone's frame.
        private static float[] InverseRotate(float[] q, float[] v)
        {
            double x = -q[0], y = -q[1], z = -q[2], w = q[3];
            // t = 2 * cross(qv, v); v' = v + w*t + cross(qv, t)
            double tx = 2 * (y * v[2] - z * v[1]), ty = 2 * (z * v[0] - x * v[2]), tz = 2 * (x * v[1] - y * v[0]);
            return new[] { (float)(v[0] + w * tx + (y * tz - z * ty)), (float)(v[1] + w * ty + (z * tx - x * tz)), (float)(v[2] + w * tz + (x * ty - y * tx)) };
        }
    }
}
