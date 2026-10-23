using System;
using System.Collections.Generic;
using System.IO;
using FrostyCLI.Commands;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class PortCommandTests
    {
        // port import grows a mesh's Materials list by copying the template's last material. The copy must not share
        // the Shader struct or its parameter lists with the original, or assigning one material's shader assigns all
        // of them (Hoth HangarSystem_01_CombinedforE3: three materials came out as Snowtrim).
        [Test]
        public void DeepCopyEbxValue_SharesNoStructsOrLists()
        {
            var shader = new FrostySdk.Ebx.PortTestFakes.FakeShaderInstance { ShaderName = "SS_Floor", Scale = 2 };
            shader.TextureParameters.Add(new FrostySdk.Ebx.PortTestFakes.FakeParameter { ParameterName = "BaseColor" });
            var owner = new FrostySdk.Ebx.PortTestFakes.FakeObject();

            var copy = (FrostySdk.Ebx.PortTestFakes.FakeShaderInstance)PortCommand.DeepCopyEbxValue(shader);
            Assert.IsFalse(ReferenceEquals(shader, copy), "struct must be a new instance");
            Assert.IsFalse(ReferenceEquals(shader.TextureParameters, copy.TextureParameters), "list must be a new instance");
            Assert.IsFalse(ReferenceEquals(shader.TextureParameters[0], copy.TextureParameters[0]), "list elements must be copied");
            Assert.AreEqual("SS_Floor", copy.ShaderName);
            Assert.AreEqual(2, copy.Scale);
            copy.ShaderName = "SS_Snowtrim"; copy.TextureParameters[0].ParameterName = "Normal"; copy.TextureParameters.Add(new FrostySdk.Ebx.PortTestFakes.FakeParameter());
            Assert.AreEqual("SS_Floor", shader.ShaderName);
            Assert.AreEqual("BaseColor", shader.TextureParameters[0].ParameterName);
            Assert.AreEqual(1, shader.TextureParameters.Count);
            Assert.IsTrue(ReferenceEquals(owner, PortCommand.DeepCopyEbxValue(owner)), "EBX objects stay references");
            Assert.AreEqual("x", (string)PortCommand.DeepCopyEbxValue("x"));
        }

        [Test]
        public void Manifest_RoundTripsThroughJson()
        {
            string dir = Path.Combine(Path.GetTempPath(), "frostycli_port_" + Guid.NewGuid().ToString("N"));
            try
            {
                var m = new PortManifest
                {
                    SourceGame = "swbf1",
                    Blueprint = "XP2/Objects/Props/PipeReflective_01/PipeReflective_01",
                    BlueprintGuid = "0583cd8b-24d4-11e6-b70c-b92a1b5259db",
                    EntityType = "StaticModelEntityData",
                    Mesh = new PortMesh
                    {
                        Path = "xp2/objects/props/pipereflective_01/pipereflective_01_Mesh", Guid = "2b5707b4-8f14-8299-3928-300c31153f65",
                        Type = "RigidMeshAsset", File = "mesh.fbx", LodCount = 2,
                        SectionNames = new List<List<string>> { new List<string> { "ChromePipe", "" }, new List<string> { "ChromePipe" } },
                        SectionMaterials = new List<List<int>> { new List<int> { 0, 1 }, new List<int> { 0 } },
                        Materials = new List<PortMaterial>
                        {
                            new PortMaterial { Index = 0, ClassGuid = "b731f303-d2dc-c06e-3e1f-2049ec603a1a", ShaderPath = "S/SS_A", ShaderGuid = "f49e4eae-ff0f-11e4-a8ef-8d233c4ad1ca",
                                Textures = new List<PortTexture> { new PortTexture { Param = "CS_LargeScale", Path = "T/T_A_CS", Guid = "93a10d9e-ff0f-11e4-a8ef-8d233c4ad1ca", File = "textures/T_A_CS.dds" } } },
                            new PortMaterial { Index = 1 }
                        }
                    },
                    Physics = new PortPhysics { AssetPath = "X_Physics_Win32", Guid = "c23a4faa-7166-7358-ae82-3d73285d215c", ResName = "x_physics_win32", File = "physics.bin", ResType = 2432974693u, MetaHex = "90000001C0050000D0050000DC000000" },
                    PartBoundingBoxes = new List<PortBox> { new PortBox { Min = new[] { -1f, 0f, -1f }, Max = new[] { 1f, 2f, 1f } } }
                };
                m.Notes.Add("note");
                m.Save(dir);
                Assert.IsTrue(File.Exists(Path.Combine(dir, "manifest.json")));
                PortManifest back = PortManifest.Load(dir);
                Assert.AreEqual(m.Blueprint, back.Blueprint);
                Assert.AreEqual(m.Mesh.LodCount, back.Mesh.LodCount);
                Assert.AreEqual(2, back.Mesh.SectionNames.Count);
                Assert.AreEqual("", back.Mesh.SectionNames[0][1]);
                Assert.AreEqual(1, back.Mesh.SectionMaterials[0][1]);
                Assert.AreEqual(2, back.Mesh.Materials.Count);
                Assert.AreEqual("CS_LargeScale", back.Mesh.Materials[0].Textures[0].Param);
                Assert.AreEqual(m.Physics.MetaHex, back.Physics.MetaHex);
                Assert.AreEqual(2432974693u, back.Physics.ResType);
                Assert.AreEqual(2f, back.PartBoundingBoxes[0].Max[1]);
                Assert.AreEqual("note", back.Notes[0]);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Test]
        public void Manifest_LoadFailsWithoutFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "frostycli_port_missing_" + Guid.NewGuid().ToString("N"));
            bool threw = false;
            try { PortManifest.Load(dir); } catch (FileNotFoundException) { threw = true; }
            Assert.IsTrue(threw);
        }

        [Test]
        public void Export_RejectsMissingArguments()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(false);
            Assert.AreEqual(1, PortCommand.ExecuteExport(new string[0], logger, fmt));
            Assert.AreEqual(1, PortCommand.ExecuteExport(new[] { "A/B" }, logger, fmt));
            Assert.AreEqual(1, PortCommand.ExecuteExport(new[] { "--out", "x" }, logger, fmt));
        }

        [Test]
        public void Import_RejectsMissingArguments()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(false);
            Assert.AreEqual(1, PortCommand.ExecuteImport(new string[0], logger, fmt));
            Assert.AreEqual(1, PortCommand.ExecuteImport(new[] { "somedir" }, logger, fmt));
            Assert.AreEqual(1, PortCommand.ExecuteImport(new[] { "--as", "A/B" }, logger, fmt));
            // a failed import reverts what it created; with nothing loaded the rollback is a no-op, never a throw
            PortCommand.RollBackImport(new List<string> { "Objects/Ported/X/X_Mesh" }, logger, fmt);
        }

        [Test]
        public void Layer_RoundTripsThroughJson()
        {
            string dir = Path.Combine(Path.GetTempPath(), "frostycli_layer_" + Guid.NewGuid().ToString("N"));
            try
            {
                var layer = new PortLayer { SourceGame = "swbf1", Level = "XP2/Levels/Clouds/Clouds_04/StreamedLevel" };
                layer.Placements.Add(new PortPlacement
                {
                    Blueprint = "XP2/Objects/Props/A/A", Guid = "0583cd8b-24d4-11e6-b70c-b92a1b5259db", Group = 0, Member = 3, Instance = 7, Source = "havok",
                    Right = new[] { 0f, 0f, 1f }, Up = new[] { 0f, 1f, 0f }, Forward = new[] { -1f, 0f, 0f }, Trans = new[] { 1.5f, 2f, -3f }
                });
                layer.Save(dir);
                PortLayer back = PortLayer.Load(dir);
                Assert.AreEqual(1, back.Placements.Count);
                Assert.AreEqual("havok", back.Placements[0].Source);
                Assert.AreEqual(-1f, back.Placements[0].Forward[0]);
                Assert.AreEqual(7, back.Placements[0].Instance);
                Assert.AreEqual(layer.Level, back.Level);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Test]
        public void ExportLayer_AndImportLayer_RejectMissingArguments()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(false);
            Assert.AreEqual(1, PortCommand.ExecuteExportLayer(new string[0], logger, fmt));
            Assert.AreEqual(1, PortCommand.ExecuteExportLayer(new[] { "A/B" }, logger, fmt));
            Assert.AreEqual(1, PortCommand.ExecuteImportLayer(new string[0], logger, fmt));
            Assert.AreEqual(1, PortCommand.ExecuteImportLayer(new[] { "dir", "--into", "L" }, logger, fmt));
            Assert.AreEqual(1, PortCommand.ExecuteImportLayer(new[] { "dir", "--group", "0" }, logger, fmt));
        }

        [Test]
        public void TransformBuilder_AcceptsANineNumberBasis()
        {
            LinearTransformData t = TransformBuilder.Build("1,2,3", "0,0,1,0,1,0,-1,0,0", "2");
            Assert.AreEqual(2f, t.Right[2]);
            Assert.AreEqual(2f, t.Up[1]);
            Assert.AreEqual(-2f, t.Forward[0]);
            Assert.AreEqual(3f, t.Trans[2]);
        }

        // A composite prop ported onto a template of the same object: the source's (component, health state) wiring and
        // health values replace the generic "every part on component 0" rule; objects that differ are only reported.
        [Test]
        public void ApplySourcePartWiring_CopiesSourceWiringWhenShapesMatch()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frostycli_partwiring_" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "blueprint.json"),
                    "{\"Object\":{\"PhysicsPartInfos\":[{\"PartComponentIndex\":0,\"HealthStateIndex\":0},{\"PartComponentIndex\":1,\"HealthStateIndex\":0},{\"PartComponentIndex\":1,\"HealthStateIndex\":1}]," +
                    "\"Components\":[{\"__type\":\"PartComponentData\",\"IsFragile\":false,\"HealthStates\":[{\"PartIndex\":0,\"Health\":100.0,\"PhysicsEnabled\":true,\"Objects\":[]}]}," +
                    "{\"__type\":\"PartComponentData\",\"IsFragile\":true,\"HealthStates\":[{\"PartIndex\":1,\"Health\":100.0,\"PhysicsEnabled\":true,\"Objects\":[]},{\"PartIndex\":4294967295,\"Health\":99.8,\"PhysicsEnabled\":false,\"Objects\":[{},{}]}]}," +
                    "{\"__type\":\"StaticModelHealthComponentData\"}]}}");
                var entity = new FrostySdk.Ebx.PortTestFakes.FakeEntity();
                var c0 = new FrostySdk.Ebx.PortTestFakes.PartComponentData();
                c0.HealthStates.Add(new FrostySdk.Ebx.PortTestFakes.HealthStateData { PartIndex = 0, Health = 100f, PhysicsEnabled = true });
                var c1 = new FrostySdk.Ebx.PortTestFakes.PartComponentData();
                c1.HealthStates.Add(new FrostySdk.Ebx.PortTestFakes.HealthStateData { PartIndex = 1, Health = 100f, PhysicsEnabled = true });
                var ruin = new FrostySdk.Ebx.PortTestFakes.HealthStateData { PartIndex = 4294967295, Health = 30f, PhysicsEnabled = false };
                ruin.Objects.Add(new object());
                c1.HealthStates.Add(ruin);
                entity.Components.Add(c0); entity.Components.Add(c1); entity.Components.Add(new object());
                entity.PhysicsPartInfos.Add(new FrostySdk.Ebx.PortTestFakes.PhysicsPartInfo());
                var m = new FrostyCLI.Core.PortManifest { BlueprintFile = "blueprint.json" };
                var report = new System.Collections.Generic.List<string>();

                PortCommand.ApplySourcePartWiring(entity, dir, m, 3, report);

                Assert.AreEqual(3, entity.PhysicsPartInfos.Count);
                Assert.AreEqual(1, entity.PhysicsPartInfos[2].PartComponentIndex);
                Assert.AreEqual(1, entity.PhysicsPartInfos[2].HealthStateIndex);
                Assert.AreEqual(99.8f, ruin.Health);
                Assert.IsTrue(c1.IsFragile, "component flag copied");
                Assert.IsTrue(report.Exists(r => r.Contains("spawn other objects")), string.Join(" | ", report));

                // a one-state template component grows to the source's state count (value types are copied, lists deep)
                var grown = new FrostySdk.Ebx.PortTestFakes.FakeEntity();
                var g0 = new FrostySdk.Ebx.PortTestFakes.PartComponentData();
                g0.HealthStates.Add(new FrostySdk.Ebx.PortTestFakes.HealthStateData { PartIndex = 0, Health = 100f, PhysicsEnabled = true });
                var g1 = new FrostySdk.Ebx.PortTestFakes.PartComponentData();
                g1.HealthStates.Add(new FrostySdk.Ebx.PortTestFakes.HealthStateData { PartIndex = 1, Health = 100f, PhysicsEnabled = true });
                grown.Components.Add(g0); grown.Components.Add(g1);
                grown.PhysicsPartInfos.Add(new FrostySdk.Ebx.PortTestFakes.PhysicsPartInfo());
                var report3 = new System.Collections.Generic.List<string>();
                PortCommand.ApplySourcePartWiring(grown, dir, m, 3, report3);
                Assert.AreEqual(2, g1.HealthStates.Count);
                Assert.IsTrue(!object.ReferenceEquals(g1.HealthStates[0], g1.HealthStates[1]), "the added state is a copy");
                Assert.AreEqual(99.8f, g1.HealthStates[1].Health);
                Assert.AreEqual(3, grown.PhysicsPartInfos.Count);

                // a template shaped differently keeps the generic wiring
                var other = new FrostySdk.Ebx.PortTestFakes.FakeEntity();
                other.Components.Add(new FrostySdk.Ebx.PortTestFakes.PartComponentData());
                other.PhysicsPartInfos.Add(new FrostySdk.Ebx.PortTestFakes.PhysicsPartInfo());
                var report2 = new System.Collections.Generic.List<string>();
                PortCommand.ApplySourcePartWiring(other, dir, m, 3, report2);
                Assert.AreEqual(1, other.PhysicsPartInfos.Count);
                Assert.IsTrue(report2.Exists(r => r.Contains("not shaped like")), string.Join(" | ", report2));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Test]
        public void Command_RejectsUnknownSubcommand()
        {
            var cmd = new PortCommand();
            Assert.AreEqual("port", cmd.Name);
            Assert.AreEqual(1, cmd.Execute(new[] { "frobnicate" }, new ExportLogger(quiet: true), new OutputFormatter(false)));
            Assert.AreEqual(1, cmd.Execute(new string[0], new ExportLogger(quiet: true), new OutputFormatter(false)));
        }
    }
}

namespace FrostySdk.Ebx.PortTestFakes
{
    // Stand-ins for SDK-generated EBX types (the deep copy keys on the FrostySdk.Ebx namespace).
    public class FakeParameter { public string ParameterName { get; set; } }
    public class FakeShaderInstance
    {
        public string ShaderName { get; set; }
        public int Scale { get; set; }
        public System.Collections.Generic.List<FakeParameter> TextureParameters { get; set; } = new System.Collections.Generic.List<FakeParameter>();
    }
    public class FakeObject { public object GetInstanceGuid() { return null; } }
    public class HealthStateData
    {
        public uint PartIndex { get; set; }
        public float Health { get; set; }
        public bool PhysicsEnabled { get; set; }
        public bool CanSupportOtherParts { get; set; }
        public bool CopyDamageToBanger { get; set; }
        public System.Collections.Generic.List<object> Objects { get; set; } = new System.Collections.Generic.List<object>();
    }
    public class PartComponentData
    {
        public bool IsSupported { get; set; }
        public bool IsFragile { get; set; }
        public bool IsNetworkable { get; set; }
        public bool IsWindow { get; set; }
        public System.Collections.Generic.List<HealthStateData> HealthStates { get; set; } = new System.Collections.Generic.List<HealthStateData>();
    }
    public class PhysicsPartInfo
    {
        public int PartComponentIndex { get; set; }
        public int HealthStateIndex { get; set; }
    }
    public class FakeEntity
    {
        public System.Collections.Generic.List<object> Components { get; set; } = new System.Collections.Generic.List<object>();
        public System.Collections.Generic.List<PhysicsPartInfo> PhysicsPartInfos { get; set; } = new System.Collections.Generic.List<PhysicsPartInfo>();
    }
}
