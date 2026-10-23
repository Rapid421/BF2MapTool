using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bfii.TerrainBuild;
using Bfii.TerrainFormat;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    // `terrain create` end to end without a game install: heightmap files on disk, a host terrain made by the same
    // generator, the payload written with --out and read back. The in-game proofs are in the terrain-authoring LOG.
    public class TerrainCreateCommandTests
    {
        private static ExportLogger Logger() { return new ExportLogger(quiet: true); }
        private static OutputFormatter Fmt() { return new OutputFormatter(json: false); }

        private static string Dir()
        {
            string d = Path.Combine(Path.GetTempPath(), "frostycli-terrain-create-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        private static void WriteR32(string path, int n, Func<int, int, float> h, double minX, double minZ, double size)
        {
            var bytes = new byte[n * n * 4];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    BitConverter.GetBytes(h(x, z)).CopyTo(bytes, (z * n + x) * 4);
            File.WriteAllBytes(path, bytes);
            File.WriteAllText(path + ".json", "{ \"width\": " + n + ", \"height\": " + n + ", \"format\": \"f32le\", " +
                "\"worldMinX\": " + minX + ", \"worldMaxX\": " + (minX + size) + ", \"worldMinZ\": " + minZ + ", \"worldMaxZ\": " + (minZ + size) +
                ", \"rowOrder\": \"min-z-first\" }");
        }

        // A host: a 1,024 m terrain from (-512, -512) painting layers 0-3 (layer 3 where x > 0), made by the generator.
        private static string Host(string dir)
        {
            var frame = new TerrainFrame { MinX = -512, MinZ = -512, Extent = 1024, WorldSizeY = 256, MaxLevel = 3 };
            int n = 129;
            var place = new HeightmapPlacement { MinX = -512, MinZ = -512, SizeX = 1024, SizeZ = 1024 };
            var r = new Raster { Width = n, Height = n, Channels = 1, IsFloat = true, Data = Enumerable.Repeat(40f, n * n).ToArray() };
            var w = new List<LayerInput> { new LayerInput { HostLayer = 0 } };
            for (int l = 1; l <= 3; l++)
            {
                var g = new float[n * n];
                for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) g[z * n + x] = l == 3 ? (x > n / 2 ? 1f : 0f) : (x == l ? 1f : 0f);
                w.Add(new LayerInput { HostLayer = l, Weights = new WorldGrid(g, n, n, place) });
            }
            var input = new TerrainGeneratorInput
            {
                Frame = frame,
                Heights = new RasterHeightSource(frame, r, place),
                Layers = new WeightMapLayerSource(w),
                MaskOptions = new MaskBuildOptions { MaxLevel = 2 },      // native 1,024 px: the round trip stays quick
                MaterialDecls = new List<uint> { 0xe460800, 0xe45e800 },
            };
            string path = Path.Combine(dir, "host.streamingtree_win32.res");
            File.WriteAllBytes(path, TerrainGenerator.Generate(input, out _));
            return path;
        }

        [Test]
        public void Create_WritesAPayloadThatParsesBack()
        {
            string d = Dir();
            string h = Path.Combine(d, "h.r32");
            WriteR32(h, 65, (x, z) => 30f + x * 0.2f + z * 0.1f, -300, -300, 600);
            string o = Path.Combine(d, "out.res");
            int rc = new TerrainCommand().Execute(new[] { "create", "--heights", h, "--out", o }, Logger(), Fmt());
            Assert.AreEqual(0, rc);
            var res = TerrainStreamingTreeParser.Parse(File.ReadAllBytes(o)).Resource;
            Assert.IsNotNull(res);
            Assert.AreEqual(-300f, res.Heightfield.Root.BoundingBox.MinX.Value);      // no host: the heightmap's own square
            Assert.AreEqual(5, res.RasterTrees.Count);
        }

        [Test]
        public void Create_OnAHost_SnapsToItsGridAndTakesItsMaterials()
        {
            string d = Dir();
            string host = Host(d);
            string h = Path.Combine(d, "h.r32");
            WriteR32(h, 129, (x, z) => 50f, -200, -200, 300);
            File.WriteAllText(Path.Combine(d, "layers.json"),
                "{ \"layers\": [ { \"hostLayer\": 0 }, { \"hostLayer\": 3, \"mask\": \"rock.png\", \"material\": 1 } ] }");
            var px = new byte[64 * 64];
            for (int i = 0; i < px.Length; i++) px[i] = (byte)(i % 64 < 32 ? 255 : 0);
            PngWriter.Write(Path.Combine(d, "rock.png"), 64, 64, 1, px);
            string o = Path.Combine(d, "out.res");
            int rc = new TerrainCommand().Execute(new[] { "create", "--heights", h, "--host", host, "--layers", Path.Combine(d, "layers.json"), "--out", o }, Logger(), Fmt());
            Assert.AreEqual(0, rc);
            var res = TerrainStreamingTreeParser.Parse(File.ReadAllBytes(o)).Resource;
            // [-200, 100] fits the host's own square on its grid
            Assert.AreEqual(-512f, res.Heightfield.Root.BoundingBox.MinX.Value);
            Assert.AreEqual(1024f, res.Heightfield.Root.BoundingBox.MaxX.Value - res.Heightfield.Root.BoundingBox.MinX.Value);
            Assert.IsTrue(res.Material.MaterialDecls.SequenceEqual(new uint[] { 0xe460800, 0xe45e800 }), "host material declarations");
            Assert.IsTrue(new MaskReader(res.Mask).Layers.Contains(3), "painted layer 3");
        }

        [Test]
        public void Create_RejectsALayerTheHostDoesNotPaint()
        {
            string d = Dir();
            string host = Host(d);
            string h = Path.Combine(d, "h.r32");
            WriteR32(h, 33, (x, z) => 50f, -100, -100, 200);
            File.WriteAllText(Path.Combine(d, "layers.json"), "{ \"layers\": [ { \"hostLayer\": 0 }, { \"hostLayer\": 7, \"mask\": \"m.png\" } ] }");
            PngWriter.Write(Path.Combine(d, "m.png"), 4, 4, 1, new byte[16]);
            int rc = new TerrainCommand().Execute(new[] { "create", "--heights", h, "--host", host, "--layers", Path.Combine(d, "layers.json"), "--out", Path.Combine(d, "o.res") }, Logger(), Fmt());
            Assert.AreEqual(1, rc);
        }

        [Test]
        public void Create_HolesPaintTheGivenHoleLayer()
        {
            string d = Dir();
            string host = Host(d);
            string h = Path.Combine(d, "h.r32");
            WriteR32(h, 129, (x, z) => Math.Abs(x - 64) < 4 && Math.Abs(z - 64) < 4 ? float.NaN : 45f, -128, -128, 256);
            string o = Path.Combine(d, "out.res");
            int rc = new TerrainCommand().Execute(new[] { "create", "--heights", h, "--host", host, "--hole-layer", "3", "--out", o }, Logger(), Fmt());
            Assert.AreEqual(0, rc);
            var res = TerrainStreamingTreeParser.Parse(File.ReadAllBytes(o)).Resource;
            Assert.IsTrue(res.Heightfield.EnumerateNodes().Any(n => n.ContainsHoles), "a node carries holes");
            var reader = new MaskReader(res.Mask);
            Assert.AreEqual(255, (int)reader.AlphaAt(3, 0, 0, reader.DeepestLevel));
        }

        [Test]
        public void ExportLayersThenCreate_GivesTheLayersAndMaterialsBack()
        {
            // the host terrain's own heights, masks and materials exported at native size and fed back to create
            string d = Dir();
            string host = Host(d);
            var hostRes = TerrainStreamingTreeParser.Parse(File.ReadAllBytes(host)).Resource;
            string h = Path.Combine(d, "h.r32");
            WriteR32(h, 129, (x, z) => 40f, -512, -512, 1024);
            string layers = Path.Combine(d, "layers");
            int native = new MaskReader(hostRes.Mask).DeepestLevel;
            int size = 64 << (native + 2);
            Assert.AreEqual(0, new TerrainCommand().Execute(new[] { "export-layers", host, "--out", layers, "--size", size.ToString() }, Logger(), Fmt()));
            Assert.IsTrue(File.Exists(Path.Combine(layers, "material.png")), "material map exported");
            string o = Path.Combine(d, "again.res");
            Assert.AreEqual(0, new TerrainCommand().Execute(new[] { "create", "--heights", h, "--host", host, "--layers", Path.Combine(layers, "layers.json"), "--out", o }, Logger(), Fmt()));
            var again = TerrainStreamingTreeParser.Parse(File.ReadAllBytes(o)).Resource;

            var a = new MaskReader(hostRes.Mask).Render(new[] { 1, 2, 3 }, size, native);
            var b = new MaskReader(again.Mask).Render(new[] { 1, 2, 3 }, size, native);
            foreach (int l in new[] { 1, 2, 3 })
                Assert.IsTrue(a[l].SequenceEqual(b[l]), "layer " + l + " comes back unchanged");
            int m = MaterialReader.NativeSize(hostRes.Material);
            Assert.IsTrue(MaterialReader.Render(hostRes.Material, m).SequenceEqual(MaterialReader.Render(again.Material, m)), "materials come back unchanged");
        }

        [Test]
        public void Retexture_RewritesOnlyTextureParameters()
        {
            // two texture parameters as Tatooine's combination depots lay them out (u64 key, u32 0xAD0ABFD3, u16 1, u16,
            // u32 1, GUID), plus the same GUID bytes loose elsewhere, which must stay
            var sand = new Guid("05044846-dca4-33ad-ed84-6035ea465940");
            var mine = new Guid("11111111-2222-3333-4444-555555555555");
            var data = new List<byte>();
            void Param(Guid g, ushort id)
            {
                data.AddRange(BitConverter.GetBytes(0x75ff514ba905f71fUL));
                data.AddRange(BitConverter.GetBytes(0xAD0ABFD3u));
                data.AddRange(BitConverter.GetBytes((ushort)1)); data.AddRange(BitConverter.GetBytes(id));
                data.AddRange(BitConverter.GetBytes(1u));
                data.AddRange(g.ToByteArray());
            }
            data.AddRange(new byte[52]);
            Param(new Guid("0cb758ae-d2e2-6552-d1ff-a620f17cd0a5"), 0xb1c1);
            Param(sand, 0xd2b7);
            data.AddRange(new byte[7]);
            data.AddRange(sand.ToByteArray());                  // not a parameter
            byte[] bytes = data.ToArray();
            Assert.AreEqual(1, TerrainCommand.RetargetTextureParams(bytes, sand.ToByteArray(), mine.ToByteArray()));
            Assert.AreEqual(mine, new Guid(bytes.Skip(52 + 36 + 20).Take(16).ToArray()));
            Assert.AreEqual(sand, new Guid(bytes.Skip(bytes.Length - 16).Take(16).ToArray()));
        }

        [Test]
        public void EmptyDecals_IsTheShippedEmptyForm()
        {
            // the test range's and Endor 02's decals, byte for byte
            var shipped = new byte[25];
            new byte[] { 0x00, 0x00, 0xF0, 0x42, 0, 0, 0, 0, 0x01 }.CopyTo(shipped, 0);
            Assert.IsTrue(TerrainCommand.EmptyDecals(120f).SequenceEqual(shipped));
        }

        [Test]
        public void Create_ClearDecalsNeedsAs()
        {
            string d = Dir();
            string h = Path.Combine(d, "h.r32");
            WriteR32(h, 33, (x, z) => 50f, -100, -100, 200);
            int rc = new TerrainCommand().Execute(new[] { "create", "--heights", h, "--out", Path.Combine(d, "o.res"), "--clear-decals" }, Logger(), Fmt());
            Assert.AreEqual(1, rc);
        }
    }
}
