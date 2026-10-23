using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Adapters.Texture;
using FrostyCLI.Core;
using FrostyCLI.Core.Enlighten;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `enlighten bake-apply <level> --plan plan.json --probes probes.json --flux <fluxbake out> --prefix <asset prefix>`:
    /// a level's OWN bake from a Flux re-bake (tools/fluxbake on an `enlighten flux-scene --plan --probes` scene).
    /// 1. The level's bake (its lighting sub-world's Static/DynamicEnlightenEntityData) gets owned copies under --prefix
    ///    (EnlightenRelight.CloneBakeInto: five atlases, StaticEnlightenData, EnlightenDataAsset, wired, bundled in the
    ///    sub-world's bundle) -- the shipped data is never edited.
    /// 2. A fresh END: the host header with the plan's atlas size; mesh-instance records = the plan's (key, uv transform),
    ///    sorted by key; no terrain records; probe sets = the host's spawn-darkroom sets kept as shipped (SPEC 5: y below
    ///    -2000) + the plan's volumes (records cloned from a host volume: guid, AABB, dims, priority, blend, positions at
    ///    cell centres x fastest, static / dynamic flags 0 as DICE's volumes).
    /// 3. A fresh FLS: the darkroom probes' SH as shipped, the volumes' SH from Flux (probes.bin: irradiance + direct SH L2
    ///    summed, sky-visibility SHL1 in .w: ShProbe::setFromShL2WithSkyVisibility, L2 mode), header = the atlas size.
    /// 4. The atlases from fluxbake's encodings (irradiance RGBA16F x --tint, direction / sky BGRA8), compressed as shipped
    ///    (BC6H_UF16 irradiance, BC7 direction and sky visibility, 1 mip) and imported into the owned textures.
    /// 5. RadiosityTypeOverride None on every placement whose key has a record (static group member instances and object
    ///    references), in the level's partitions -- addLightMapHandle registers only override None.
    /// Every database is written framed to --out for inspection and round-trip checked (parse(write) == write).
    /// </summary>
    public static class EnlightenBakeApply
    {
        public static int Execute(string[] args, ExportLogger logger)
        {
            string level = null, planFile = null, probesFile = null, fluxDir = null, prefix = null, outDir = null, bakeFilter = null;
            double[] tint = { 1, 1, 1 };
            bool noOverrides = false;
            int depth = 3;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--plan" && i + 1 < args.Length) planFile = args[++i];
                else if (a == "--probes" && i + 1 < args.Length) probesFile = args[++i];
                else if (a == "--flux" && i + 1 < args.Length) fluxDir = args[++i];
                else if (a == "--prefix" && i + 1 < args.Length) prefix = args[++i].TrimEnd('/');
                else if (a == "--out" && i + 1 < args.Length) outDir = args[++i];
                else if (a == "--bake" && i + 1 < args.Length) bakeFilter = args[++i];
                else if (a == "--tint" && i + 1 < args.Length) tint = args[++i].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                else if (a == "--no-overrides") noOverrides = true;
                else if (a == "--depth" && i + 1 < args.Length) depth = int.Parse(args[++i]);
                else if (!a.StartsWith("--")) level = a;
                else { Console.Error.WriteLine("enlighten bake-apply: unknown option " + a); return 1; }
            }
            if (level == null || planFile == null || probesFile == null || fluxDir == null || prefix == null)
            {
                Console.Error.WriteLine("Usage: frostycli enlighten bake-apply <level> --plan plan.json --probes probes.json --flux <fluxbake out dir> --prefix <asset prefix> " +
                                        "[--out dir] [--bake filter] [--tint r,g,b] [--no-overrides] [--depth n]");
                return 1;
            }
            outDir = outDir ?? fluxDir;
            Directory.CreateDirectory(outDir);
            JObject plan = JObject.Parse(File.ReadAllText(planFile));
            int W = (int)plan["width"], H = (int)plan["height"];
            JArray vols = JArray.Parse(File.ReadAllText(probesFile));

            // ---- the bake and its owned copies
            List<EnlightenRelight.Bake> bakes = EnlightenRelight.FindBakes(level, logger);
            EnlightenRelight.Bake b = bakes.FirstOrDefault(x => bakeFilter == null || (x.Static ?? "").IndexOf(bakeFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            if (b == null || !EnlightenRelight.LoadBake(b)) { Console.Error.WriteLine("enlighten bake-apply: no loadable bake under " + level); return 1; }
            EnlightenDatabase host = b.Db;
            StaticEnlightenDatabase hostFls = b.Fls;
            if (hostFls == null) { Console.Error.WriteLine("enlighten bake-apply: " + b.Static + " has no static database"); return 1; }
            if (!b.Static.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) && !EnlightenRelight.CloneBakeInto(b, prefix, logger)) return 1;

            // ---- END
            var db = EnlightenDatabase.Parse(host.Write(out byte[] hostMeta), hostMeta);       // a deep copy through the round trip
            db.Width = (uint)W; db.Height = (uint)H;
            db.Terrain.Clear();
            db.MeshInstances.Clear();
            foreach (JObject r in (JArray)plan["records"])
            {
                var mi = new EnlightenDatabase.MeshInstance { Hash = Convert.ToUInt32((string)r["key"], 16) };
                var uv = new byte[32];
                JArray u = (JArray)r["uv"];
                for (int k = 0; k < 6; k++) Array.Copy(BitConverter.GetBytes((float)(double)u[k]), 0, uv, k * 4, 4);
                mi.UvTransforms.Add(uv);
                db.MeshInstances.Add(mi);
            }
            db.MeshInstances.Sort((x, y) => x.Hash.CompareTo(y.Hash));
            if (db.MeshInstances.Select(m => m.Hash).Distinct().Count() != db.MeshInstances.Count) { Console.Error.WriteLine("enlighten bake-apply: duplicate keys in the plan"); return 1; }
            // probe sets: darkroom sets as shipped, then the plan's volumes
            var flsProbes = new List<byte>();
            // the spawn screen / end-of-round volumes 3 km under the map (SPEC 5), judged by their WORLD box (volumes keep a unit
            // AABB under a scaling transform)
            var keep = host.ProbeSets.Where(ps => { float[] mn, mx; ps.WorldBox(out mn, out mx); return mx[1] < -2000 && ps.Count > 0; }).ToList();
            EnlightenDatabase.ProbeSet template = keep.FirstOrDefault(ps => ps.IsVolume) ?? host.ProbeSets.FirstOrDefault(ps => ps.IsVolume && ps.Count > 0);
            if (template == null) { Console.Error.WriteLine("enlighten bake-apply: no probe-set record to clone"); return 1; }
            db.ProbeSets.Clear();
            foreach (var ps in keep)
            {
                var c = new EnlightenDatabase.ProbeSet { Raw = (byte[])ps.Raw.Clone(), Positions = (byte[])ps.Positions.Clone() };
                byte[] sh = new byte[ps.Count * ProbeVolumes.ProbeBytes];
                Array.Copy(hostFls.Probes, (int)ps.GlobalOffset * ProbeVolumes.ProbeBytes, sh, 0, sh.Length);
                c.GlobalOffset = (uint)(flsProbes.Count / ProbeVolumes.ProbeBytes);
                flsProbes.AddRange(sh);
                db.ProbeSets.Add(c);
            }
            Dictionary<Guid, float[][]> fluxProbes = ReadFluxProbes(Path.Combine(fluxDir, "probes.bin"));
            int volProbes = 0, culledFilled = 0, culledVolumes = 0;
            foreach (JObject v in vols)
            {
                Guid g = new Guid((string)v["guid"]);
                JArray box = (JArray)v["aabb"], dims = (JArray)v["dims"], pos = (JArray)v["positions"];
                int n = pos.Count / 3;
                float[][] sh;
                if (!fluxProbes.TryGetValue(g, out sh) || sh.Length != n) { Console.Error.WriteLine("enlighten bake-apply: Flux has no probes for volume " + g); return 1; }
                // Culled probes (Flux ProbeBaking: more than half the non-sky rays hit back faces -> irradiance r[0] = -1,
                // LightProbe::markCulled) take the closest valid probe of the same set: findClosestValidFluxLightProbe
                // (FluxUtil.cpp, used by EnlightenFreezePipeline). The runtime blends the voxel grid trilinearly with no
                // validity test (LightProbeSystem::sampleVoxelGrid), so a -1 sentinel would darken anything near a wall.
                // A volume with no valid probe at all is left out (it lies inside geometry and holds no light).
                if (sh.All(p => p[0] < 0)) { culledVolumes++; continue; }
                var src = (float[][])sh.Clone();
                for (int k = 0; k < n; k++)
                {
                    if (src[k][0] >= 0) continue;
                    int best = k; double bd = double.MaxValue;
                    for (int q = 0; q < n; q++)
                    {
                        if (src[q][0] < 0) continue;
                        double dx = (double)pos[q * 3] - (double)pos[k * 3], dy = (double)pos[q * 3 + 1] - (double)pos[k * 3 + 1], dz = (double)pos[q * 3 + 2] - (double)pos[k * 3 + 2];
                        double d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 < bd) { bd = d2; best = q; }
                    }
                    sh[k] = src[best];
                    culledFilled++;
                }
                var ps = new EnlightenDatabase.ProbeSet { Raw = (byte[])template.Raw.Clone(), Positions = new byte[n * 16] };
                Array.Copy(g.ToByteArray(), 0, ps.Raw, 0, 16);
                // DICE's volume form: unit AABB, size and centre in the transforms (BFII_EXACT, Kamino volumes)
                ps.SetVolumeBox(new[] { (float)box[0], (float)box[1], (float)box[2] }, new[] { (float)box[3], (float)box[4], (float)box[5] });
                EnlightenDatabase.PutF(ps.Raw, 0xB0, (float)v["blend"]);
                EnlightenDatabase.Put(ps.Raw, 0xB4, (uint)dims[0]); EnlightenDatabase.Put(ps.Raw, 0xB8, (uint)dims[1]); EnlightenDatabase.Put(ps.Raw, 0xBC, (uint)dims[2]);
                EnlightenDatabase.Put(ps.Raw, 0xC0, (uint)v["priority"]);
                EnlightenDatabase.Put(ps.Raw, 0xC4, (uint)n);
                ps.Raw[0xCC] = 0; ps.Raw[0xCD] = 0;
                for (int k = 0; k < n; k++) ps.SetPosition(k, (float)pos[k * 3], (float)pos[k * 3 + 1], (float)pos[k * 3 + 2]);
                ps.GlobalOffset = (uint)(flsProbes.Count / ProbeVolumes.ProbeBytes);
                foreach (float[] p in sh)
                {
                    var rec = new byte[ProbeVolumes.ProbeBytes];
                    for (int c9 = 0; c9 < 9; c9++)
                        for (int ch = 0; ch < 3; ch++)
                            Array.Copy(BitConverter.GetBytes(p[ch * 9 + c9] + p[27 + ch * 9 + c9]), 0, rec, c9 * 16 + ch * 4, 4);
                    for (int c4 = 0; c4 < 4; c4++) Array.Copy(BitConverter.GetBytes(p[54 + c4]), 0, rec, c4 * 16 + 12, 4);
                    flsProbes.AddRange(rec);
                }
                db.ProbeSets.Add(ps);
                volProbes += n;
            }
            var fls = StaticEnlightenDatabase.Parse(hostFls.Write(out byte[] flsMeta0), flsMeta0);
            fls.Width = (uint)W; fls.Height = (uint)H;
            fls.Probes = flsProbes.ToArray();
            fls.LightStates = new byte[0];
            Put(fls.Header, 8, (uint)(fls.Probes.Length / ProbeVolumes.ProbeBytes));
            // round trip
            byte[] endMeta, endData = db.Write(out endMeta), flsMeta, flsData = fls.Write(out flsMeta);
            var db2 = EnlightenDatabase.Parse(endData, endMeta);
            byte[] endMeta2, endData2 = db2.Write(out endMeta2);
            if (!endData2.SequenceEqual(endData) || db2.MeshInstances.Count != db.MeshInstances.Count || db2.ProbeSets.Count != db.ProbeSets.Count)
            { Console.Error.WriteLine("enlighten bake-apply: END round trip failed"); return 1; }
            var fls2 = StaticEnlightenDatabase.Parse(flsData, flsMeta);
            byte[] flsMeta2, flsData2 = fls2.Write(out flsMeta2);
            if (!flsData2.SequenceEqual(flsData) || fls2.ProbeCount != fls.ProbeCount) { Console.Error.WriteLine("enlighten bake-apply: FLS round trip failed"); return 1; }
            File.WriteAllBytes(Path.Combine(outDir, "end.framed.bin"), EnlightenRelight.Frame(endMeta, endData));
            File.WriteAllBytes(Path.Combine(outDir, "fls.framed.bin"), EnlightenRelight.Frame(flsMeta, flsData));
            try { App.AssetManager.ModifyRes(b.DbRes.Name, endData, endMeta); App.AssetManager.ModifyRes(b.FlsRes.Name, flsData, flsMeta); }
            catch (Exception ex) { Console.Error.WriteLine("enlighten bake-apply: writing the databases: " + ex.Message); return 1; }
            Console.Out.WriteLine(string.Format("bake-apply: END {0}x{1}, {2} mesh records, {3} probe sets ({4} darkroom kept, {5} volumes / {6} probes; {7} culled probes given their closest valid probe, {8} all-culled volumes left out); FLS {9} probes -> {10}, {11}",
                W, H, db.MeshInstances.Count, db.ProbeSets.Count, keep.Count, vols.Count - culledVolumes, volProbes, culledFilled, culledVolumes, fls.ProbeCount, b.DbRes.Name, b.FlsRes.Name));

            // ---- atlases
            int failures = WriteAtlases(b, fluxDir, outDir, W, H, tint);
            if (failures > 0) return 1;

            // ---- overrides
            if (!noOverrides)
            {
                var keys = new HashSet<uint>(db.MeshInstances.Select(m => m.Hash));
                int set = 0, already = 0, objs = 0;
                EnlightenKeys.Walk(level, depth, (name, dump) =>
                {
                    var hits = EnlightenKeys.Collect(name, dump).Where(p => keys.Contains(p.Key)).ToList();
                    if (hits.Count == 0) return;
                    int s, a, o;
                    if (SetOverrides(name, hits, out s, out a, out o, logger)) { set += s; already += a; objs += o; }
                });
                Console.Out.WriteLine(string.Format("bake-apply: RadiosityTypeOverride None on {0} group instance(s) and {1} object reference(s) ({2} already None)", set, objs, already));
            }
            return 0;
        }

        static void Put(byte[] b, int o, uint v) { Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4); }

        /// <summary>probes.bin of tools/fluxbake: per set guid, count, then 58 floats per probe.</summary>
        static Dictionary<Guid, float[][]> ReadFluxProbes(string path)
        {
            var r = new Dictionary<Guid, float[][]>();
            byte[] d = File.ReadAllBytes(path);
            int o = 0, sets = BitConverter.ToInt32(d, o); o += 4;
            for (int s = 0; s < sets; s++)
            {
                var g = new byte[16]; Array.Copy(d, o, g, 0, 16); o += 16;
                int n = BitConverter.ToInt32(d, o); o += 4;
                var a = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    a[i] = new float[58];
                    for (int k = 0; k < 58; k++) { a[i][k] = BitConverter.ToSingle(d, o); o += 4; }
                }
                r[new Guid(g)] = a;
            }
            return r;
        }

        static int WriteAtlases(EnlightenRelight.Bake b, string fluxDir, string outDir, int W, int H, double[] tint)
        {
            long px = (long)W * H;
            byte[] irr16 = File.ReadAllBytes(Path.Combine(fluxDir, "irradiance.rgba16f"));
            if (irr16.Length != px * 8) { Console.Error.WriteLine("enlighten bake-apply: irradiance size " + irr16.Length + " is not " + W + "x" + H); return 1; }
            var rgb = new float[px * 3];
            long nonFinite = 0, overflow = 0;
            // A half +Inf is not always a broken bake: Flux's float irradiance above the half maximum (65504; a texel on an
            // emissive strip -- Mygeeto: 82,000-340,000 against a sky of 3,800) overflows in fluxbake's RGBA16F copy. With the
            // float atlas beside it (irradiance.f32, RGBA float) such a channel is told apart from a real NaN / Inf and takes
            // the half maximum, which is also the BC6H_UF16 maximum, so nothing representable is lost.
            const float HalfMax = 65504f;
            string f32Path = Path.Combine(fluxDir, "irradiance.f32");
            using (FileStream f32 = File.Exists(f32Path) && new FileInfo(f32Path).Length == px * 16 ? File.OpenRead(f32Path) : null)
            {
                var four = new byte[4];
                for (long i = 0; i < px; i++)
                    for (int c = 0; c < 3; c++)
                    {
                        ushort h = BitConverter.ToUInt16(irr16, (int)(i * 8 + c * 2));
                        double v = Half(h);
                        if (((h >> 10) & 31) == 31)
                        {
                            bool isOverflow = false;
                            if (h == 0x7C00 && f32 != null)
                            {
                                f32.Position = i * 16 + c * 4;
                                f32.Read(four, 0, 4);
                                float src = BitConverter.ToSingle(four, 0);
                                isOverflow = !float.IsNaN(src) && !float.IsInfinity(src) && src > HalfMax;
                            }
                            if (isOverflow) { overflow++; v = HalfMax; } else nonFinite++;
                        }
                        rgb[i * 3 + c] = (float)Math.Min(v * tint[c], HalfMax);
                    }
            }
            if (overflow > 0) Console.Out.WriteLine("bake-apply: " + overflow + " irradiance channel(s) above the half maximum clamped to " + HalfMax + " (finite in irradiance.f32)");
            // a NaN / Inf texel would encode as the BC6H maximum (a white-hot spot): the bake must be fixed, not the atlas
            if (nonFinite > 0) { Console.Error.WriteLine("enlighten bake-apply: irradiance has " + nonFinite + " non-finite channel(s); refusing to write the atlas"); return 1; }
            Func<string, byte[]> rgba = f =>
            {
                byte[] bgra = File.ReadAllBytes(Path.Combine(fluxDir, f));
                var o = new byte[bgra.Length];
                for (long i = 0; i < px; i++) { o[i * 4] = bgra[i * 4 + 2]; o[i * 4 + 1] = bgra[i * 4 + 1]; o[i * 4 + 2] = bgra[i * 4]; o[i * 4 + 3] = bgra[i * 4 + 3]; }
                return o;
            };
            var jobs = new List<Tuple<string, byte[], ImageFormat, int>>
            {
                Tuple.Create("StaticIrradianceTexture", AtlasIo.WriteHdr(rgb, W, H), ImageFormat.HDR, 95),
                Tuple.Create("StaticDirectionTexture", AtlasIo.WriteRgbaPng(rgba("dirR.bgra"), W, H), ImageFormat.PNG, 98),
                Tuple.Create("StaticDirectionTextureG", AtlasIo.WriteRgbaPng(rgba("dirG.bgra"), W, H), ImageFormat.PNG, 98),
                Tuple.Create("StaticDirectionTextureB", AtlasIo.WriteRgbaPng(rgba("dirB.bgra"), W, H), ImageFormat.PNG, 98),
                Tuple.Create("StaticSkyVisibilityTexture", AtlasIo.WriteRgbaPng(rgba("sky.bgra"), W, H), ImageFormat.PNG, 98),
            };
            int failures = 0;
            foreach (var job in jobs)
            {
                string ebx = b.Textures[job.Item1];
                try
                {
                    byte[] dds = EnlightenRelight.Compress(job.Item2, job.Item3, job.Item4);
                    string file = Path.Combine(outDir, Path.GetFileName(ebx) + ".dds");
                    File.WriteAllBytes(file, dds);
                    string err = HeadlessTextureImporter.Import(App.AssetManager.GetEbxEntry(ebx), file, "DDS", false);
                    if (err != null) { Console.Error.WriteLine("enlighten bake-apply: import " + ebx + ": " + err); failures++; continue; }
                    Console.Out.WriteLine("  imported " + ebx + " (" + W + "x" + H + ", dxgi " + job.Item4 + ")");
                }
                catch (Exception ex) { Console.Error.WriteLine("enlighten bake-apply: " + ebx + ": " + ex.Message); failures++; }
            }
            return failures;
        }

        static double Half(ushort h)
        {
            int e = (h >> 10) & 31, m = h & 1023;
            double v = e == 0 ? m / 1024.0 * Math.Pow(2, -14) : e == 31 ? 65504 : (1 + m / 1024.0) * Math.Pow(2, e - 15);
            return (h & 0x8000) != 0 ? -v : v;
        }

        /// <summary>RadiosityTypeOverride None on the given placements of one partition (group member instances, object references).</summary>
        static bool SetOverrides(string partition, List<EnlightenKeys.Placement> hits, out int set, out int already, out int objs, ExportLogger logger)
        {
            set = 0; already = 0; objs = 0;
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(partition);
            if (e == null) return false;
            EbxAsset asset = App.AssetManager.GetEbx(e);
            bool changed = false;
            foreach (object o in asset.Objects)
            {
                if (o == null) continue;
                string t = o.GetType().Name;
                dynamic ig = ((dynamic)o).GetInstanceGuid();
                Guid g = ig.ExportedGuid; int id = (int)ig.InternalId;
                if (t == "StaticModelGroupEntityData")
                {
                    var mine = hits.Where(p => p.Source == "group" && ((p.GroupGuid != Guid.Empty && p.GroupGuid == g) || (p.GroupGuid == Guid.Empty && p.GroupId == id))).ToList();
                    if (mine.Count == 0) continue;
                    IList members = (IList)o.GetType().GetProperty("MemberDatas").GetValue(o);
                    foreach (var p in mine)
                    {
                        object md = members[p.Member];
                        PropertyInfo pi = md.GetType().GetProperty("InstanceRadiosityTypeOverride");
                        IList ov = (IList)pi.GetValue(md);
                        Type et = ov.GetType().GetGenericArguments()[0];
                        object none = Enum.Parse(et, "RadiosityTypeOverride_None");
                        int count = Convert.ToInt32(md.GetType().GetProperty("InstanceCount").GetValue(md));
                        while (ov.Count < count) ov.Add(Enum.Parse(et, "RadiosityTypeOverride_Dynamic"));
                        if (Equals(ov[p.Instance], none)) { already++; continue; }
                        ov[p.Instance] = none;
                        set++; changed = true;
                    }
                }
                else if (t == "ObjectReferenceObjectData" || t == "SpatialPrefabReferenceObjectData")
                {
                    if (!hits.Any(p => (p.Source == "object" || p.Source == "prefab") && ((p.ObjectGuid != Guid.Empty && p.ObjectGuid == g) || (p.ObjectGuid == Guid.Empty && p.ObjectId == id)))) continue;
                    PropertyInfo pi = o.GetType().GetProperty("RadiosityTypeOverride");
                    object none = Enum.Parse(pi.PropertyType, "RadiosityTypeOverride_None");
                    if (Equals(pi.GetValue(o), none)) { already++; continue; }
                    pi.SetValue(o, none);
                    objs++; changed = true;
                }
            }
            if (changed && !EbxWriteCommand.CallModifyEbx(partition, asset, logger)) { Console.Error.WriteLine("enlighten bake-apply: could not write " + partition); return false; }
            return true;
        }
    }
}
