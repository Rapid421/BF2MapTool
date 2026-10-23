using System;
using System.Collections.Generic;
using System.IO;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>
    /// BFII Enlighten system database: <c>EnlightenDataAsset.DatabaseResource</c>, resType 1892010814
    /// (FIFA-branch name <c>EnlightenDatabaseData</c>). Layout (BFII_EXACT, projects/enlighten-authoring/SPEC.md):
    /// <code>
    /// 0x00 header (0x70): u32 atlas W, H, format, f32 preExposure, u32 probeMode, u32 dirRgb, guid assetGuid,
    ///      u32 numMeshInstances @0x28, u32 numTerrainInstances @0x2C, u64 meshInstances @0x30,
    ///      u64 terrainInstances @0x38, u32 numProbeSets @0x4C, u64 probeSets @0x50
    /// mesh instances  N x 32  { u32 hash; u32 uvMappingsCount; u64 -> uv transform(s); u64 textures; u64 pad }
    /// terrain records T x 48  { Vec4 (su,0,0,sv); Vec2 (ou,ov); 8 pad; AABB2 (minX,minZ,maxX,maxZ) }
    /// uv transforms   32 bytes per mapping { Vec4 (su,0,0,sv); Vec2 (ou,ov); 8 pad }
    /// probe sets      P x 240 (EnlightenProbeSetData; +0xC4 count, +0xC8 globalOffset, +0xD0 u64 -> positions)
    /// probe positions float4 per probe
    /// fixup table     u32 offsets of every pointer field: 0x30, 0x38, each mesh instance +8, each probe set
    ///                 +0xD0 (non-null), and finally 0x50  (res meta = [9, tableOffset, tableBytes, 0])
    /// </code>
    /// Parse keeps every byte it does not model (header, record bodies) so <see cref="Write"/> reproduces a shipped
    /// database byte for byte; edits go through the typed lists.
    /// </summary>
    public sealed class EnlightenDatabase
    {
        public const uint ResType = 1892010814;
        public const int HeaderSize = 0x70;

        public byte[] Header = new byte[HeaderSize];
        public List<MeshInstance> MeshInstances = new List<MeshInstance>();
        public List<TerrainLightmap> Terrain = new List<TerrainLightmap>();
        public List<ProbeSet> ProbeSets = new List<ProbeSet>();

        /// <summary>Anything between the last probe position and the fixup table (empty on every file seen).</summary>
        public byte[] Trailer = new byte[0];

        /// <summary>Res meta word 0, the cookie flags: 1 Enable | 8 HasLightmaps (9 on every lit level, 1 without an atlas).</summary>
        public uint CookieFlags = 9;

        /// <summary>Set when the payload is not modelled (a 4-byte 0xDEADBEEF placeholder, or dynamic probe data):
        /// <see cref="Write"/> then returns it unchanged.</summary>
        public byte[] Unmodelled;
        public byte[] UnmodelledMeta;
        public string UnmodelledReason;

        public uint Width { get => U32(Header, 0); set => Put(Header, 0, value); }
        public uint Height { get => U32(Header, 4); set => Put(Header, 4, value); }
        public uint OutputFormat => U32(Header, 8);
        public float PreExposure => BitConverter.ToSingle(Header, 0xC);
        public uint ProbeMode => U32(Header, 0x10);
        public bool DirectionalRgb => U32(Header, 0x14) != 0;
        public Guid AssetGuid { get { var g = new byte[16]; Array.Copy(Header, 0x18, g, 0, 16); return new Guid(g); } }

        /// <summary>
        /// Grow the output atlas to newW x newH keeping every texel where it is. The uv transforms are normalised to the
        /// atlas size (uv_atlas = uv x scale + offset, in 0..1), so every mesh-instance transform and terrain record is
        /// rescaled by W/newW and H/newH; the textures must grow the same way (<see cref="AtlasIo.GrowDds"/>).
        /// </summary>
        public void ResizeAtlas(uint newW, uint newH)
        {
            if (newW < Width || newH < Height) throw new ArgumentException("the atlas can only grow");
            double kx = (double)Width / newW, ky = (double)Height / newH;
            foreach (var mi in MeshInstances)
                foreach (var uv in mi.UvTransforms)
                {
                    PutF(uv, 0, (float)(F(uv, 0) * kx)); PutF(uv, 16, (float)(F(uv, 16) * kx));
                    PutF(uv, 12, (float)(F(uv, 12) * ky)); PutF(uv, 20, (float)(F(uv, 20) * ky));
                }
            foreach (var t in Terrain)
            {
                t.ScaleU = (float)(t.ScaleU * kx); t.OffsetU = (float)(t.OffsetU * kx);
                t.ScaleV = (float)(t.ScaleV * ky); t.OffsetV = (float)(t.OffsetV * ky);
            }
            Width = newW; Height = newH;
        }

        public sealed class MeshInstance
        {
            public uint Hash;
            /// <summary>Raw 32-byte UV transform records, one per uv mapping (always one in shipped data).</summary>
            public List<byte[]> UvTransforms = new List<byte[]>();
            public ulong Textures;
            public ulong Pad;

            public float ScaleU(int i = 0) => BitConverter.ToSingle(UvTransforms[i], 0);
            public float ScaleV(int i = 0) => BitConverter.ToSingle(UvTransforms[i], 12);
            public float OffsetU(int i = 0) => BitConverter.ToSingle(UvTransforms[i], 16);
            public float OffsetV(int i = 0) => BitConverter.ToSingle(UvTransforms[i], 20);
        }

        public sealed class TerrainLightmap
        {
            public byte[] Raw = new byte[48];

            public float ScaleU { get => F(Raw, 0); set => PutF(Raw, 0, value); }
            public float ScaleV { get => F(Raw, 12); set => PutF(Raw, 12, value); }
            public float OffsetU { get => F(Raw, 16); set => PutF(Raw, 16, value); }
            public float OffsetV { get => F(Raw, 20); set => PutF(Raw, 20, value); }
            public float MinX { get => F(Raw, 32); set => PutF(Raw, 32, value); }
            public float MinZ { get => F(Raw, 36); set => PutF(Raw, 36, value); }
            public float MaxX { get => F(Raw, 40); set => PutF(Raw, 40, value); }
            public float MaxZ { get => F(Raw, 44); set => PutF(Raw, 44, value); }

            /// <summary>Texel rect [X0,X1) x [Y0,Y1) whose texel centres span the tile's world box.</summary>
            public void TexelRect(uint w, uint h, out int x0, out int x1, out int y0, out int y1)
            {
                x0 = (int)Math.Round((MinX * ScaleU + OffsetU) * w - 0.5);
                x1 = (int)Math.Round((MaxX * ScaleU + OffsetU) * w + 0.5);
                y0 = (int)Math.Round((MinZ * ScaleV + OffsetV) * h - 0.5);
                y1 = (int)Math.Round((MaxZ * ScaleV + OffsetV) * h + 0.5);
            }

            /// <summary>World (x, z) of a texel centre.</summary>
            public void TexelWorld(uint w, uint h, int px, int py, out double x, out double z)
            {
                x = ((px + 0.5) / w - OffsetU) / ScaleU;
                z = ((py + 0.5) / h - OffsetV) / ScaleV;
            }

            /// <summary>Map the world square [minX,maxX] x [minZ,maxZ] onto texel rect origin (x0, y0) with
            /// <paramref name="texels"/> texel centres per side (edge centres on the edges), as the shipped tiles do.</summary>
            public void Place(double minX, double minZ, double maxX, double maxZ, int x0, int y0, int texels, uint w, uint h)
            {
                double su = (texels - 1) / ((maxX - minX) * w), sv = (texels - 1) / ((maxZ - minZ) * h);
                ScaleU = (float)su; ScaleV = (float)sv;
                OffsetU = (float)((x0 + 0.5) / w - minX * su);
                OffsetV = (float)((y0 + 0.5) / h - minZ * sv);
                MinX = (float)minX; MinZ = (float)minZ; MaxX = (float)maxX; MaxZ = (float)maxZ;
            }
        }

        public sealed class ProbeSet
        {
            public byte[] Raw = new byte[240];
            public byte[] Positions = new byte[0];   // count x float4

            public uint Count => U32(Raw, 0xC4);
            public uint GlobalOffset { get => U32(Raw, 0xC8); set => Put(Raw, 0xC8, value); }
            public uint DimX => U32(Raw, 0xB4);
            public uint DimY => U32(Raw, 0xB8);
            public uint DimZ => U32(Raw, 0xBC);
            public float BlendDistance => F(Raw, 0xB0);
            public uint Priority => U32(Raw, 0xC0);
            public ulong PositionsPtr => BitConverter.ToUInt64(Raw, 0xD0);
            public float MinX => F(Raw, 0x90);
            public float MinY => F(Raw, 0x94);
            public float MinZ => F(Raw, 0x98);
            public float MaxX => F(Raw, 0xA0);
            public float MaxY => F(Raw, 0xA4);
            public float MaxZ => F(Raw, 0xA8);
            public Guid Guid { get { var g = new byte[16]; Array.Copy(Raw, 0, g, 0, 16); return new Guid(g); } }

            /// <summary>
            /// A terrain probe grid like the shipped ones (BFII_EXACT, Tatooine): one per terrain tile, dims x 1 x dims
            /// probes spanning the tile's box edge to edge (x fastest), 2 m above the ground, identity transforms, AABB y =
            /// lowest probe - 10 .. highest + 30, blend 3, priority 0; everything else copied from a shipped grid.
            /// </summary>
            public static ProbeSet TerrainGrid(ProbeSet template, double minX, double minZ, double maxX, double maxZ, int dims,
                                               Func<double, double, double> ground, uint globalOffset, Guid guid)
            {
                var ps = new ProbeSet { Raw = (byte[])template.Raw.Clone() };
                Array.Copy(guid.ToByteArray(), 0, ps.Raw, 0, 16);
                Put(ps.Raw, 0xB4, (uint)dims); Put(ps.Raw, 0xB8, 1u); Put(ps.Raw, 0xBC, (uint)dims);
                Put(ps.Raw, 0xC4, (uint)(dims * dims)); Put(ps.Raw, 0xC8, globalOffset);
                ps.Positions = new byte[dims * dims * 16];
                double lo = double.MaxValue, hi = double.MinValue;
                for (int j = 0; j < dims; j++)
                    for (int i = 0; i < dims; i++)
                    {
                        double x = minX + (maxX - minX) * i / (dims - 1), z = minZ + (maxZ - minZ) * j / (dims - 1);
                        double y = ground(x, z) + 2.0;
                        ps.SetPosition(j * dims + i, (float)x, (float)y, (float)z);
                        lo = Math.Min(lo, y); hi = Math.Max(hi, y);
                    }
                PutF(ps.Raw, 0x90, (float)minX); PutF(ps.Raw, 0x94, (float)(lo - 10)); PutF(ps.Raw, 0x98, (float)minZ); PutF(ps.Raw, 0x9C, 0);
                PutF(ps.Raw, 0xA0, (float)maxX); PutF(ps.Raw, 0xA4, (float)(hi + 30)); PutF(ps.Raw, 0xA8, (float)maxZ); PutF(ps.Raw, 0xAC, 0);
                return ps;
            }

            /// <summary>
            /// The set's world box: its AABB (+0x90 / +0xA0) under the local-to-world LinearTransform at +0x10 (rows right, up,
            /// forward, trans; row vectors). DICE's volumes keep a unit AABB (-0.5 .. 0.5) and put size and centre in that
            /// transform (Kamino darkroom volumes: scale 450 x 200 x 450 at y -3000); single probes and terrain grids carry an
            /// identity transform and a world AABB (BFII_EXACT, Kamino / Tatooine END).
            /// </summary>
            public void WorldBox(out float[] min, out float[] max)
            {
                min = new[] { float.MaxValue, float.MaxValue, float.MaxValue }; max = new[] { float.MinValue, float.MinValue, float.MinValue };
                for (int c = 0; c < 8; c++)
                {
                    float x = (c & 1) != 0 ? MaxX : MinX, y = (c & 2) != 0 ? MaxY : MinY, z = (c & 4) != 0 ? MaxZ : MinZ;
                    for (int k = 0; k < 3; k++)
                    {
                        float w = x * F(Raw, 0x10 + k * 4) + y * F(Raw, 0x20 + k * 4) + z * F(Raw, 0x30 + k * 4) + F(Raw, 0x40 + k * 4);
                        min[k] = Math.Min(min[k], w); max[k] = Math.Max(max[k], w);
                    }
                }
            }

            /// <summary>
            /// An axis-aligned volume in DICE's form: unit AABB, local-to-world = scale(size) then translate(centre) at +0x10,
            /// its inverse at +0x50 (w column 0, as shipped).
            /// </summary>
            public void SetVolumeBox(float[] min, float[] max)
            {
                for (int k = 0; k < 32; k++) PutF(Raw, 0x10 + k * 4, 0f);
                for (int k = 0; k < 3; k++)
                {
                    float s = max[k] - min[k], c = 0.5f * (min[k] + max[k]);
                    PutF(Raw, 0x10 + k * 16 + k * 4, s); PutF(Raw, 0x40 + k * 4, c);
                    PutF(Raw, 0x50 + k * 16 + k * 4, 1f / s); PutF(Raw, 0x80 + k * 4, -c / s);
                    PutF(Raw, 0x90 + k * 4, -0.5f); PutF(Raw, 0xA0 + k * 4, 0.5f);
                }
                PutF(Raw, 0x9C, 0); PutF(Raw, 0xAC, 0);
            }

            /// <summary>A probe volume (dims above 1 on every axis).</summary>
            public bool IsVolume => DimX > 1 && DimY > 1 && DimZ > 1;

            /// <summary>Terrain probe sets (EnlightenTerrainSceneDescriptionBuilder): an XZ grid one probe high, blend 3.</summary>
            public bool IsTerrainGrid => DimY == 1 && DimX > 1 && DimZ > 1 && Math.Abs(BlendDistance - 3.0f) < 1e-4f;

            public void Position(int i, out float x, out float y, out float z)
            {
                x = F(Positions, i * 16); y = F(Positions, i * 16 + 4); z = F(Positions, i * 16 + 8);
            }

            public void SetPosition(int i, float x, float y, float z)
            {
                PutF(Positions, i * 16, x); PutF(Positions, i * 16 + 4, y); PutF(Positions, i * 16 + 8, z);
            }
        }

        public static EnlightenDatabase Parse(byte[] d)
        {
            if (d == null) throw new InvalidDataException("no Enlighten database payload");
            if (d.Length < HeaderSize)
                return new EnlightenDatabase { Unmodelled = d, UnmodelledReason = "placeholder (" + d.Length + " bytes)" };
            for (int i = 0; i < (int)U32(d, 0x4C); i++)
            {
                long o = (long)BitConverter.ToUInt64(d, 0x50) + (long)i * 240;
                if (o + 240 <= d.Length && (BitConverter.ToUInt64(d, (int)o + 0xD8) != 0 || BitConverter.ToUInt64(d, (int)o + 0xE0) != 0))
                    return new EnlightenDatabase { Unmodelled = d, UnmodelledReason = "probe set " + i + " carries dynamic-data pointers" };
            }
            var db = new EnlightenDatabase();
            Array.Copy(d, db.Header, HeaderSize);
            int n = (int)U32(d, 0x28), t = (int)U32(d, 0x2C), p = (int)U32(d, 0x4C);
            long pMesh = (long)BitConverter.ToUInt64(d, 0x30), pTerr = (long)BitConverter.ToUInt64(d, 0x38);
            long pProbe = (long)BitConverter.ToUInt64(d, 0x50);
            if (n > 0 && pMesh != HeaderSize) throw new InvalidDataException("mesh instances do not follow the header");
            long expectTerr = HeaderSize + (long)n * 32;
            if (pTerr != expectTerr) throw new InvalidDataException("terrain records do not follow the mesh instances (0x" + pTerr.ToString("X") + ")");
            long uvStart = pTerr + (long)t * 48;
            long cursor = uvStart;
            for (int i = 0; i < n; i++)
            {
                long o = HeaderSize + (long)i * 32;
                var mi = new MeshInstance { Hash = U32(d, (int)o), Textures = BitConverter.ToUInt64(d, (int)o + 16), Pad = BitConverter.ToUInt64(d, (int)o + 24) };
                uint maps = U32(d, (int)o + 4);
                long ptr = (long)BitConverter.ToUInt64(d, (int)o + 8);
                if (maps > 0 && ptr != cursor) throw new InvalidDataException("uv transforms are not in instance order (instance " + i + ")");
                for (int k = 0; k < maps; k++)
                {
                    var raw = new byte[32];
                    Array.Copy(d, cursor, raw, 0, 32);
                    mi.UvTransforms.Add(raw);
                    cursor += 32;
                }
                db.MeshInstances.Add(mi);
            }
            for (int i = 0; i < t; i++)
            {
                var tl = new TerrainLightmap();
                Array.Copy(d, pTerr + (long)i * 48, tl.Raw, 0, 48);
                db.Terrain.Add(tl);
            }
            if (p > 0 && pProbe != cursor) throw new InvalidDataException("probe sets do not follow the uv transforms");
            long posCursor = pProbe + (long)p * 240;
            for (int i = 0; i < p; i++)
            {
                var ps = new ProbeSet();
                Array.Copy(d, pProbe + (long)i * 240, ps.Raw, 0, 240);
                ulong pp = ps.PositionsPtr;
                if (pp != 0)
                {
                    if ((long)pp != posCursor) throw new InvalidDataException("probe positions are not in set order (set " + i + ")");
                    ps.Positions = new byte[ps.Count * 16];
                    Array.Copy(d, (long)pp, ps.Positions, 0, ps.Positions.Length);
                    posCursor += ps.Positions.Length;
                }
                db.ProbeSets.Add(ps);
            }
            db.EndOfData = posCursor;
            return db;
        }

        /// <summary>Byte offset where the modelled sections end (the fixup table or <see cref="Trailer"/> starts).</summary>
        public long EndOfData;

        /// <summary>Parse a payload together with its 16-byte res meta; checks the meta's table against the payload.</summary>
        public static EnlightenDatabase Parse(byte[] data, byte[] meta)
        {
            var db = Parse(data);
            if (db.Unmodelled != null) { db.UnmodelledMeta = meta; return db; }
            if (meta != null && meta.Length >= 12)
            {
                db.CookieFlags = U32(meta, 0);
                uint tableOff = U32(meta, 4), tableBytes = U32(meta, 8);
                if (tableOff + tableBytes != data.Length) throw new InvalidDataException("res meta table does not end the payload");
                if (tableOff < db.EndOfData) throw new InvalidDataException("res meta table overlaps the data");
                db.Trailer = new byte[tableOff - db.EndOfData];
                Array.Copy(data, db.EndOfData, db.Trailer, 0, db.Trailer.Length);
            }
            return db;
        }

        /// <summary>Serialise; <paramref name="meta"/> receives the 16-byte res meta [9, tableOffset, tableBytes, 0].</summary>
        public byte[] Write(out byte[] meta)
        {
            if (Unmodelled != null)
            {
                meta = UnmodelledMeta != null ? (byte[])UnmodelledMeta.Clone() : new byte[16];
                return (byte[])Unmodelled.Clone();
            }
            int n = MeshInstances.Count, t = Terrain.Count, p = ProbeSets.Count;
            long pMesh = HeaderSize, pTerr = pMesh + (long)n * 32, pUv = pTerr + (long)t * 48;
            long uvBytes = 0;
            foreach (var mi in MeshInstances) uvBytes += mi.UvTransforms.Count * 32L;
            long pProbe = pUv + uvBytes;
            long pPos = pProbe + (long)p * 240;
            long posBytes = 0;
            foreach (var ps in ProbeSets) posBytes += ps.Positions.Length;
            long tableOff = pPos + posBytes + Trailer.Length;
            var fix = new List<uint> { 0x30, 0x38 };

            var ms = new MemoryStream();
            var h = (byte[])Header.Clone();
            Put(h, 0x28, (uint)n); Put(h, 0x2C, (uint)t); Put(h, 0x4C, (uint)p);
            PutU64(h, 0x30, (ulong)pMesh); PutU64(h, 0x38, (ulong)pTerr); PutU64(h, 0x50, (ulong)pProbe);
            ms.Write(h, 0, HeaderSize);

            long uvCursor = pUv;
            var rec = new byte[32];
            for (int i = 0; i < n; i++)
            {
                var mi = MeshInstances[i];
                Array.Clear(rec, 0, 32);
                Put(rec, 0, mi.Hash);
                Put(rec, 4, (uint)mi.UvTransforms.Count);
                PutU64(rec, 8, mi.UvTransforms.Count > 0 ? (ulong)uvCursor : 0);
                PutU64(rec, 16, mi.Textures);
                PutU64(rec, 24, mi.Pad);
                ms.Write(rec, 0, 32);
                if (mi.UvTransforms.Count > 0) fix.Add((uint)(pMesh + (long)i * 32 + 8));
                uvCursor += mi.UvTransforms.Count * 32L;
            }
            foreach (var tl in Terrain) ms.Write(tl.Raw, 0, 48);
            foreach (var mi in MeshInstances) foreach (var uv in mi.UvTransforms) ms.Write(uv, 0, 32);
            long posCursor = pPos;
            for (int i = 0; i < p; i++)
            {
                var raw = (byte[])ProbeSets[i].Raw.Clone();
                if (ProbeSets[i].Positions.Length > 0)
                {
                    PutU64(raw, 0xD0, (ulong)posCursor);
                    fix.Add((uint)(pProbe + (long)i * 240 + 0xD0));
                    posCursor += ProbeSets[i].Positions.Length;
                }
                else PutU64(raw, 0xD0, 0);
                ms.Write(raw, 0, 240);
            }
            foreach (var ps in ProbeSets) ms.Write(ps.Positions, 0, ps.Positions.Length);
            ms.Write(Trailer, 0, Trailer.Length);
            fix.Add(0x50);
            foreach (uint f in fix) ms.Write(BitConverter.GetBytes(f), 0, 4);
            meta = new byte[16];
            Put(meta, 0, CookieFlags); Put(meta, 4, (uint)tableOff); Put(meta, 8, (uint)(fix.Count * 4));
            return ms.ToArray();
        }

        internal static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        internal static float F(byte[] b, int o) => BitConverter.ToSingle(b, o);
        internal static void Put(byte[] b, int o, uint v) { Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4); }
        internal static void PutF(byte[] b, int o, float v) { Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4); }
        internal static void PutU64(byte[] b, int o, ulong v) { Array.Copy(BitConverter.GetBytes(v), 0, b, o, 8); }
    }

    /// <summary>
    /// BFII static Enlighten database (the probe SH store): <c>StaticEnlightenData.DatabaseResource</c>, resType
    /// 3335336582 (FIFA name <c>EnlightenStaticDatabaseData</c>, 4-byte packed). Header: u32 W @0, u32 H @4,
    /// u32 numProbes @8, u64 probes @0x0C (= 32), u32 numLightStates @0x14, u64 lightStates @0x18; probes are
    /// 144 bytes (9 float4: RGB SH L2 in .xyz, sky-visibility SH L1 in .w of the first four); light states are
    /// 8 bytes each (61 on Endor space, none on ground levels); padding to 16; fixup table [0x0C, 0x18]
    /// (res meta [1, tableOffset, 8, 0]). A 4-byte 0xDEADBEEF payload is a disabled placeholder.
    /// </summary>
    public sealed class StaticEnlightenDatabase
    {
        public const uint ResType = 3335336582;
        public byte[] Header = new byte[32];
        public byte[] Probes = new byte[0];
        public byte[] LightStates = new byte[0];
        public byte[] Padding = new byte[0];
        public byte[] Table = new byte[0];
        public uint CookieWord0 = 1;
        public byte[] Unmodelled;
        public byte[] UnmodelledMeta;

        public uint Width { get => EnlightenDatabase.U32(Header, 0); set => EnlightenDatabase.Put(Header, 0, value); }
        public uint Height { get => EnlightenDatabase.U32(Header, 4); set => EnlightenDatabase.Put(Header, 4, value); }
        public int ProbeCount => Probes.Length / 144;

        public static StaticEnlightenDatabase Parse(byte[] d, byte[] meta = null)
        {
            if (d.Length < 32) return new StaticEnlightenDatabase { Unmodelled = d, UnmodelledMeta = meta };
            var s = new StaticEnlightenDatabase();
            Array.Copy(d, s.Header, 32);
            uint count = EnlightenDatabase.U32(d, 8);
            long probes = (long)BitConverter.ToUInt64(d, 0x0C);
            uint states = EnlightenDatabase.U32(d, 0x14);
            long statesPtr = (long)BitConverter.ToUInt64(d, 0x18);
            if (probes != 32) throw new InvalidDataException("static database probes do not start at 32");
            if (32 + (long)count * 144 != statesPtr) throw new InvalidDataException("static database light states do not follow the probes");
            s.Probes = new byte[count * 144];
            Array.Copy(d, 32, s.Probes, 0, s.Probes.Length);
            s.LightStates = new byte[states * 8];
            Array.Copy(d, statesPtr, s.LightStates, 0, s.LightStates.Length);
            long tableOff = meta != null && meta.Length >= 12 ? EnlightenDatabase.U32(meta, 4) : d.Length - 8;
            long dataEnd = statesPtr + s.LightStates.Length;
            if (tableOff < dataEnd) throw new InvalidDataException("static database table overlaps the data");
            s.Padding = new byte[tableOff - dataEnd];
            Array.Copy(d, dataEnd, s.Padding, 0, s.Padding.Length);
            s.Table = new byte[d.Length - tableOff];
            Array.Copy(d, tableOff, s.Table, 0, s.Table.Length);
            if (meta != null && meta.Length >= 4) s.CookieWord0 = EnlightenDatabase.U32(meta, 0);
            return s;
        }

        public byte[] Write(out byte[] meta)
        {
            if (Unmodelled != null)
            {
                meta = UnmodelledMeta != null ? (byte[])UnmodelledMeta.Clone() : new byte[16];
                return (byte[])Unmodelled.Clone();
            }
            var h = (byte[])Header.Clone();
            long statesPtr = 32 + Probes.Length;
            EnlightenDatabase.Put(h, 8, (uint)(Probes.Length / 144));
            EnlightenDatabase.PutU64(h, 0x0C, 32);
            EnlightenDatabase.Put(h, 0x14, (uint)(LightStates.Length / 8));
            EnlightenDatabase.PutU64(h, 0x18, (ulong)statesPtr);
            long dataEnd = statesPtr + LightStates.Length;
            int pad = (int)((16 - dataEnd % 16) % 16);
            var ms = new MemoryStream();
            ms.Write(h, 0, 32);
            ms.Write(Probes, 0, Probes.Length);
            ms.Write(LightStates, 0, LightStates.Length);
            ms.Write(Padding.Length == pad ? Padding : new byte[pad], 0, pad);
            long tableOff = dataEnd + pad;
            var table = new byte[8];
            EnlightenDatabase.Put(table, 0, 0x0C);
            EnlightenDatabase.Put(table, 4, 0x18);
            ms.Write(table, 0, 8);
            meta = new byte[16];
            EnlightenDatabase.Put(meta, 0, CookieWord0);
            EnlightenDatabase.Put(meta, 4, (uint)tableOff);
            EnlightenDatabase.Put(meta, 8, 8);
            return ms.ToArray();
        }

        public float Coef(int probe, int k, int ch) => BitConverter.ToSingle(Probes, probe * 144 + k * 16 + ch * 4);
        public void SetCoef(int probe, int k, int ch, float v) => EnlightenDatabase.PutF(Probes, probe * 144 + k * 16 + ch * 4, v);
    }
}
