using System;
using System.IO;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>
    /// SWBF 2015 (Frostbite 2014) Enlighten bake containers, read into the BFII model so a 2015 bake can be written back
    /// as a BFII one (`enlighten port-bake`). Same two resource types as BFII (memory enlighten-cross-game-port,
    /// cross-validated on Carbonite Chamber, Arctic_01):
    /// <code>
    /// END 1892010814, header 0x50: u32 W @0, H @4, format @8; u32 numMeshInstances @0x10, numTerrainInstances @0x14,
    ///     u64 -> mesh instances @0x18, u64 -> terrain instances @0x20, u32 numProbeSets @0x34, u64 -> probe sets @0x38.
    ///     Mesh instances, terrain records (48 B) and uv transforms (32 B) are BFII's byte for byte (10-int keys, not 11).
    ///     Probe sets (240 B): as BFII up to +0xCF; +0xD0 -> a shared index list, +0xD8 -> positions (BFII: +0xD0 ->
    ///     positions, +0xD8 validIndices and +0xE0 probeSetCore null for static bakes).
    /// FLS 3335336582: u32 W, H, numProbes, u64 -> probes (32) @0x0C; probes from 0x20, 144 B each (9 float4: RGB SH L2
    ///     in .xyz, sky-visibility SH L1 in .w), in probe-set globalOffset order - BFII's layout.
    /// </code>
    /// </summary>
    public static class Swbf1Enlighten
    {
        /// <summary>A 2015 END as the BFII model: <see cref="EnlightenDatabase.Header"/> carries only W and H (the caller
        /// supplies a BFII header); probe sets are converted to the BFII static layout (positions at +0xD0, +0xD8..+0xEF
        /// zero, +0xCE/+0xCF 0xFF as every shipped BFII set).</summary>
        public static EnlightenDatabase ParseEnd(byte[] d)
        {
            if (d == null || d.Length < 0x50) throw new InvalidDataException("not a 2015 Enlighten system database");
            uint n = U32(d, 0x10), t = U32(d, 0x14), p = U32(d, 0x34);
            long pMesh = (long)BitConverter.ToUInt64(d, 0x18), pTerr = (long)BitConverter.ToUInt64(d, 0x20), pProbe = (long)BitConverter.ToUInt64(d, 0x38);
            if (n > 0 && pMesh != 0x50) throw new InvalidDataException("2015 mesh instances do not follow the 0x50 header");
            if (pTerr != 0x50 + n * 32L) throw new InvalidDataException("2015 terrain records do not follow the mesh instances");
            var db = new EnlightenDatabase();
            db.Width = U32(d, 0); db.Height = U32(d, 4);
            for (int i = 0; i < n; i++)
            {
                long o = pMesh + i * 32L;
                var mi = new EnlightenDatabase.MeshInstance { Hash = U32(d, (int)o) };
                uint maps = U32(d, (int)o + 4);
                long ptr = (long)BitConverter.ToUInt64(d, (int)o + 8);
                for (int k = 0; k < maps; k++)
                {
                    var raw = new byte[32];
                    Array.Copy(d, ptr + k * 32L, raw, 0, 32);
                    mi.UvTransforms.Add(raw);
                }
                db.MeshInstances.Add(mi);
            }
            for (int i = 0; i < t; i++)
            {
                var tl = new EnlightenDatabase.TerrainLightmap();
                Array.Copy(d, pTerr + i * 48L, tl.Raw, 0, 48);
                db.Terrain.Add(tl);
            }
            for (int i = 0; i < p; i++)
            {
                long o = pProbe + i * 240L;
                var ps = new EnlightenDatabase.ProbeSet();
                Array.Copy(d, o, ps.Raw, 0, 0xD0);
                ps.Raw[0xCE] = 0xFF; ps.Raw[0xCF] = 0xFF;
                long pos = (long)BitConverter.ToUInt64(d, (int)o + 0xD8);
                uint count = ps.Count;
                if (count > 0 && pos == 0) throw new InvalidDataException("2015 probe set " + i + " has no positions");
                ps.Positions = new byte[count * 16];
                if (count > 0) Array.Copy(d, pos, ps.Positions, 0, ps.Positions.Length);
                db.ProbeSets.Add(ps);
            }
            return db;
        }

        /// <summary>A 2015 FLS: the probe count and the 144-byte probe records (raw, BFII layout).</summary>
        public static byte[] ReadProbes(byte[] d, out uint width, out uint height)
        {
            if (d == null || d.Length < 0x20) throw new InvalidDataException("not a 2015 static Enlighten database");
            width = U32(d, 0); height = U32(d, 4);
            uint n = U32(d, 8);
            if (BitConverter.ToUInt64(d, 0x0C) != 32) throw new InvalidDataException("2015 static database probes do not start at 32");
            if (0x20 + n * 144L > d.Length) throw new InvalidDataException("2015 static database is shorter than its probes");
            var probes = new byte[n * 144];
            Array.Copy(d, 0x20, probes, 0, probes.Length);
            return probes;
        }

        /// <summary>Strip the 16-byte res meta a framed dump (`res export --framed`) starts with.</summary>
        public static byte[] Unframe(byte[] framed, out byte[] meta)
        {
            meta = new byte[16];
            Array.Copy(framed, meta, 16);
            var d = new byte[framed.Length - 16];
            Array.Copy(framed, 16, d, 0, d.Length);
            return d;
        }

        static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    }
}
