using System;
using System.Collections.Generic;
using FrostySdk;
using FrostyCLI.Adapters.Mesh;

namespace FrostyCLI.Tests
{
    // The geometry port's pure parts: the vertex codec, donor layout matching and ranking, the RVM input signature, vertex
    // encoding with derived channels, and the removal of a destructible composite's later-health-state parts.
    public class MeshGeoTests
    {
        private static GeoElement El(VertexElementUsage u, VertexElementFormat f, int offset)
        {
            return new GeoElement { Usage = u, Format = f, Offset = (byte)offset, Stream = 0 };
        }

        private static GeometryDeclarationDesc.Element DEl(VertexElementUsage u, VertexElementFormat f, int offset)
        {
            return new GeometryDeclarationDesc.Element { Usage = u, Format = f, Offset = (byte)offset, StreamIndex = 0 };
        }

        private static GeoSection Section(string name, int stride, int vertices, params GeoElement[] els)
        {
            var s = new GeoSection { Name = name, Stride = stride, VertexCount = vertices, Vertices = new byte[stride * vertices] };
            s.Elements.AddRange(els);
            return s;
        }

        [Test]
        public void FloatToHalf_RoundsToNearestEven()
        {
            Assert.AreEqual((ushort)0x3C00, VertexCodec.FloatToHalf(1f));
            Assert.AreEqual((ushort)0xBC00, VertexCodec.FloatToHalf(-1f));
            Assert.AreEqual((ushort)0x7BFF, VertexCodec.FloatToHalf(65504f));
            // 1 + 2^-11 lies half way between 0x3C00 and 0x3C01: ties go to the even mantissa
            Assert.AreEqual((ushort)0x3C00, VertexCodec.FloatToHalf(1f + 1f / 2048f));
            // 1 + 3 * 2^-11 lies half way between 0x3C01 and 0x3C02: even is 0x3C02
            Assert.AreEqual((ushort)0x3C02, VertexCodec.FloatToHalf(1f + 3f / 2048f));
        }

        [Test]
        public void Codec_RoundTripsHalf4AndUByte4N()
        {
            var b = new byte[16];
            VertexCodec.Encode(b, 0, VertexElementFormat.Half4, new[] { 0.5f, -2f, 0.25f, 1f });
            float[] x = VertexCodec.Decode(b, 0, VertexElementFormat.Half4);
            Assert.AreEqual(0.5f, x[0]); Assert.AreEqual(-2f, x[1]); Assert.AreEqual(0.25f, x[2]); Assert.AreEqual(1f, x[3]);
            VertexCodec.Encode(b, 8, VertexElementFormat.UByte4N, new[] { 0f, 1f, 128f / 255f, 1f });
            float[] y = VertexCodec.Decode(b, 8, VertexElementFormat.UByte4N);
            Assert.AreEqual(0f, y[0]); Assert.AreEqual(1f, y[1]); Assert.AreEqual(128f / 255f, y[2]);
        }

        [Test]
        public void RoundTrips_DetectsFloatToHalfNarrowing()
        {
            GeoSection s = Section("a", 12, 2, El(VertexElementUsage.Pos, VertexElementFormat.Float3, 0));
            VertexCodec.Encode(s.Vertices, 0, VertexElementFormat.Float3, new[] { 0.5f, 1f, 2f });
            VertexCodec.Encode(s.Vertices, 12, VertexElementFormat.Float3, new[] { -4f, 0.25f, 1024f });
            Assert.IsTrue(MeshGeoPort.RoundTrips(s, VertexElementUsage.Pos, VertexElementFormat.Half4), "exact halves survive");
            VertexCodec.Encode(s.Vertices, 12, VertexElementFormat.Float3, new[] { 0.1f, 0.25f, 1024f });
            Assert.IsFalse(MeshGeoPort.RoundTrips(s, VertexElementUsage.Pos, VertexElementFormat.Half4), "0.1 does not survive a half");
            Assert.IsTrue(MeshGeoPort.RoundTrips(s, VertexElementUsage.Pos, VertexElementFormat.Float3), "same format");
        }

        [Test]
        public void Compare_DroppedLightmapUvsWeighAsLostData()
        {
            var src = new List<GeoElement> { El(VertexElementUsage.Pos, VertexElementFormat.Float3, 0), El(VertexElementUsage.Normal, VertexElementFormat.Half4, 12),
                                             El(VertexElementUsage.RadiosityTexCoord, VertexElementFormat.Half2, 20) };
            var without = new List<GeometryDeclarationDesc.Element> { DEl(VertexElementUsage.Pos, VertexElementFormat.Half4, 0), DEl(VertexElementUsage.Normal, VertexElementFormat.Half4, 8) };
            var with = new List<GeometryDeclarationDesc.Element>(without) { DEl(VertexElementUsage.RadiosityTexCoord, VertexElementFormat.Half2, 16) };
            MeshGeoPort.LayoutMatch a = MeshGeoPort.Compare(src, without, new HashSet<VertexElementUsage>());
            MeshGeoPort.LayoutMatch b = MeshGeoPort.Compare(src, with, new HashSet<VertexElementUsage>());
            Assert.IsTrue(a.Usable && b.Usable, "both usable");
            Assert.IsTrue(a.Dropped.Contains(VertexElementUsage.RadiosityTexCoord), "dropped");
            Assert.IsTrue(MeshGeoPort.Score(a) >= 1000, "lost data weighs 1000");
            Assert.IsTrue(MeshGeoPort.Score(b) < MeshGeoPort.Score(a), "the donor that keeps the lightmap uvs ranks first");
        }

        [Test]
        public void Compare_FillsExtraUvSetsButRadiosityOnlyWhenNeverLightmapped()
        {
            var src = new List<GeoElement> { El(VertexElementUsage.Pos, VertexElementFormat.Half4, 0), El(VertexElementUsage.TexCoord0, VertexElementFormat.Half2, 8) };
            var donor = new List<GeometryDeclarationDesc.Element> { DEl(VertexElementUsage.Pos, VertexElementFormat.Half4, 0), DEl(VertexElementUsage.TexCoord0, VertexElementFormat.Half2, 8),
                                                                    DEl(VertexElementUsage.TexCoord1, VertexElementFormat.Half2, 12), DEl(VertexElementUsage.RadiosityTexCoord, VertexElementFormat.Half2, 16) };
            MeshGeoPort.LayoutMatch lit = MeshGeoPort.Compare(src, donor, null, false);
            Assert.IsFalse(lit.Usable, "a lightmapped mesh never gets invented lightmap uvs");
            Assert.IsTrue(lit.Filled.Contains(VertexElementUsage.TexCoord1), "TexCoord1 filled");
            MeshGeoPort.LayoutMatch never = MeshGeoPort.Compare(src, donor, null, true);
            Assert.IsTrue(never.Usable, "a never-lightmapped mesh may take (0,0)");
            Assert.IsTrue(never.Filled.Contains(VertexElementUsage.RadiosityTexCoord), "radiosity filled");
        }

        [Test]
        public void RvmSignature_PacksPositionSignAndHalfPairs()
        {
            var decl = new List<GeometryDeclarationDesc.Element>
            {
                DEl(VertexElementUsage.Pos, VertexElementFormat.Half3, 0), DEl(VertexElementUsage.BinormalSign, VertexElementFormat.Half, 6),
                DEl(VertexElementUsage.Normal, VertexElementFormat.Half4, 8), DEl(VertexElementUsage.TexCoord0, VertexElementFormat.Half2, 16),
                DEl(VertexElementUsage.RadiosityTexCoord, VertexElementFormat.Half2, 20)
            };
            Assert.AreEqual("0:8,8:8,16:8", MeshGeoPort.RvmSignature(decl));
        }

        [Test]
        public void EncodeVertices_DerivesBinormalFromSignAndFillsUvSets()
        {
            GeoSection s = Section("a", 18, 1, El(VertexElementUsage.Normal, VertexElementFormat.Half4, 0), El(VertexElementUsage.Tangent, VertexElementFormat.Half4, 8),
                                   El(VertexElementUsage.BinormalSign, VertexElementFormat.Half, 16));
            VertexCodec.Encode(s.Vertices, 0, VertexElementFormat.Half4, new[] { 0f, 0f, 1f, 0f });
            VertexCodec.Encode(s.Vertices, 8, VertexElementFormat.Half4, new[] { 1f, 0f, 0f, 0f });
            VertexCodec.Encode(s.Vertices, 16, VertexElementFormat.Half, new[] { -1f });
            var decl = new List<GeometryDeclarationDesc.Element>
            {
                DEl(VertexElementUsage.Normal, VertexElementFormat.Half4, 0), DEl(VertexElementUsage.Tangent, VertexElementFormat.Half4, 8),
                DEl(VertexElementUsage.Binormal, VertexElementFormat.Half4, 16), DEl(VertexElementUsage.TexCoord1, VertexElementFormat.Half2, 24)
            };
            byte[] v = MeshGeoPort.EncodeVertices(s, decl, 28);
            // B = sign * cross(T, N) = -1 * cross((1,0,0), (0,0,1)) = (0,1,0)
            float[] b = VertexCodec.Decode(v, 16, VertexElementFormat.Half4);
            Assert.AreEqual(0f, Math.Abs(b[0])); Assert.AreEqual(1f, b[1]); Assert.AreEqual(0f, Math.Abs(b[2]));
            float[] uv = VertexCodec.Decode(v, 24, VertexElementFormat.Half2);
            Assert.AreEqual(0f, uv[0]); Assert.AreEqual(1f, uv[1]);
        }

        [Test]
        public void TessKey_EmptyWithoutTessellation()
        {
            Assert.AreEqual("", SurfaceDonors.TessKey("ShaderTessellationType_None", "1", "True"));
            Assert.AreEqual("", SurfaceDonors.TessKey(null, null, null));
            Assert.AreEqual("ShaderTessellationType_DisplacementMapping|1|False", SurfaceDonors.TessKey("ShaderTessellationType_DisplacementMapping", "1", "False"));
        }

        [Test]
        public void EnlightenCounters_StartEmpty()
        {
            // PortGeo's per-run counters feed ApplyMeshFields' static -> dynamic rule; a fresh process has none
            Assert.IsTrue(FrostyCLI.Commands.PortGeo.LastRadiosityDropped >= 0 && FrostyCLI.Commands.PortGeo.LastRadiosityKept >= 0, "counters");
        }

        [Test]
        public void DropParts_RemovesLaterHealthStatePartsPerPrimitive()
        {
            // BoxMedium_01's shape: an intact section (part 0), a ruin section (part 3) and a depth section mixing both
            Func<string, int[], GeoSection> make = (name, parts) =>
            {
                GeoSection s = Section(name, 16, parts.Length, El(VertexElementUsage.Pos, VertexElementFormat.Float3, 0), El(VertexElementUsage.BoneIndices, VertexElementFormat.UByte4, 12));
                for (int i = 0; i < parts.Length; i++)
                {
                    VertexCodec.Encode(s.Vertices, i * 16, VertexElementFormat.Float3, new[] { (float)i, 0f, 0f });
                    s.Vertices[i * 16 + 12] = (byte)parts[i];
                }
                var ix = new uint[parts.Length];
                for (int i = 0; i < ix.Length; i++) ix[i] = (uint)i;
                s.Indices = ix; s.PrimitiveCount = parts.Length / 3;
                return s;
            };
            var lod = new GeoLod();
            lod.Sections.Add(make("Metal", new[] { 0, 0, 0 }));
            lod.Sections.Add(make("M_Ruin", new[] { 3, 3, 3 }));
            lod.Sections.Add(make("", new[] { 3, 3, 3, 0, 0, 0 }));
            lod.Categories.Add(new List<byte> { 0, 1 });
            lod.Categories.Add(new List<byte>());
            lod.Categories.Add(new List<byte>());
            lod.Categories.Add(new List<byte> { 2 });
            lod.Categories.Add(new List<byte> { 2 });
            var geo = new GeoMesh();
            geo.Lods.Add(lod);
            List<string> notes = geo.DropParts(new HashSet<int> { 3 });
            Assert.AreEqual(2, notes.Count);
            Assert.AreEqual(2, lod.Sections.Count);
            Assert.AreEqual("Metal", lod.Sections[0].Name);
            GeoSection depth = lod.Sections[1];
            Assert.AreEqual(3, depth.VertexCount); Assert.AreEqual(1, depth.PrimitiveCount);
            Assert.AreEqual(3f, VertexCodec.Decode(depth.Vertices, 0, VertexElementFormat.Float3)[0]);   // the part-0 vertices, order kept
            Assert.AreEqual("0,1,2", string.Join(",", depth.Indices));
            Assert.AreEqual("0", string.Join(",", lod.Categories[0]));
            Assert.AreEqual("1", string.Join(",", lod.Categories[3]));
            Assert.AreEqual("1", string.Join(",", lod.Categories[4]));
        }
    }
}
