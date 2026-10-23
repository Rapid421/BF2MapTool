using System.Collections.Generic;
using ShaderDataPlugin;
using B = ShaderDataPlugin.GraphTextureDefaults.Binding;

namespace FrostyCLI.Tests
{
    // port export: a 2015 graph written for one prop bakes its art in (SS_MetalPlate_Floor_01 binds T_MetalPlate_Floor_02_C
    // for BaseColor; the material and its MVDB record set nothing). The port must carry that default or the prop draws
    // black on BFII's fallback preset (Hoth 2015 hangar floor plates).
    public class GraphTextureDefaultsTests
    {
        private const string Ext = GraphTextureDefaults.ExternalParameter, Emb = GraphTextureDefaults.EmbeddedTexture;

        [Test]
        public void AnExternalParameterTakesTheEmbeddedTextureInItsSlot()
        {
            // the MetalPlate permutation: slot 4 = BaseColor + its baked colour map; slot 5 a constant (no parameter)
            var perm = new List<B>
            {
                new B(4, "BaseColor", Ext),
                new B(4, "Objects/Props/MetalPlate_Floor_01/T_MetalPlate_Floor_02_C", Emb),
                new B(5, "Objects/Props/MetalPlate_Floor_01/T_MetalPlate_Floor_02_RGBM", Emb),
            };
            Dictionary<string, string> d = GraphTextureDefaults.Resolve(new[] { perm });
            Assert.AreEqual(1, d.Count);
            Assert.AreEqual("Objects/Props/MetalPlate_Floor_01/T_MetalPlate_Floor_02_C", d["BaseColor"]);
        }

        [Test]
        public void PermutationsVoteAndAnUnnamedTextureIsSkipped()
        {
            var a = new List<B> { new B(1, "Normal", Ext), new B(1, "T_A_N", Emb), new B(2, "Mask", Ext), new B(2, "0x34A0DB8D", Emb) };
            var b = new List<B> { new B(3, "Normal", Ext), new B(3, "T_A_N", Emb) };
            var c = new List<B> { new B(1, "Normal", Ext), new B(1, "T_B_N", Emb) };
            Dictionary<string, string> d = GraphTextureDefaults.Resolve(new[] { a, b, c });
            Assert.AreEqual("T_A_N", d["Normal"]);
            Assert.IsFalse(d.ContainsKey("Mask"));
        }

        // port import / regeo: the default is bound only where the source graph is replaced by a fallback preset; a kept
        // graph (same GUID, or same name after DICE moved it) binds it itself, and the 2015 copy is not imported.
        [Test]
        public void GraphDefaultsAreDroppedWhereTheSourceGraphIsKept()
        {
            var m = new FrostyCLI.Core.PortManifest { Mesh = new FrostyCLI.Core.PortMesh() };
            var kept = new FrostyCLI.Core.PortMaterial { Index = 0, ShaderPath = "Objects/Architecture/Hoth/HangarSystem_01/New/SS_HangarSystem_01_Wall_01" };
            kept.Textures.Add(new FrostyCLI.Core.PortTexture { Param = "BaseColor", Source = "shader-default" });
            kept.Textures.Add(new FrostyCLI.Core.PortTexture { Param = "Normal" });
            var fallen = new FrostyCLI.Core.PortMaterial { Index = 1, ShaderPath = "Objects/Props/MetalPlate_Floor_01/SS_MetalPlate_Floor_01" };
            fallen.Textures.Add(new FrostyCLI.Core.PortTexture { Param = "BaseColor", Source = "shader-default" });
            m.Mesh.Materials.Add(kept);
            m.Mesh.Materials.Add(fallen);
            var targets = new List<FrostySdk.Managers.EbxAssetEntry>
            {
                new FrostySdk.Managers.EbxAssetEntry { Name = "objects/architecture/hoth/hangarsystem_01/new/ss_hangarsystem_01_wall_01" },
                new FrostySdk.Managers.EbxAssetEntry { Name = "Shaders/Presets/SS_PropsMetallicPreset" },
            };
            var report = new List<string>();
            FrostyCLI.Commands.PortCommand.DropKeptGraphDefaults(m, targets, report);
            Assert.AreEqual(1, kept.Textures.Count);
            Assert.AreEqual("Normal", kept.Textures[0].Param);
            Assert.AreEqual(1, fallen.Textures.Count);
            Assert.AreEqual(1, report.Count);
        }

        // SS_LightGround_01_EmissiveGlow bakes its own T_LightGround_01_CS / _NW (no parameter) next to shared weathering maps.
        [Test]
        public void OwnConstantsAreTheGraphFolderArtOnly()
        {
            const string g = "Objects/Props/_Lighting/LightGround_01/SS_LightGround_01_EmissiveGlow";
            var perm = new List<B>
            {
                new B(2, "Objects/Props/_Lighting/LightGround_01/T_LightGround_01_CS", Emb),
                new B(3, "Objects/Props/_Lighting/LightGround_01/T_LightGround_01_NW", Emb),
                new B(4, "Shaders/Weathering/T_Arctic_WeatheringProps_01_RGBM", Emb),
                new B(5, "BaseColor", Ext), new B(5, "Objects/Props/_Lighting/LightGround_01/T_LightGround_01_Default_C", Emb),
                new B(6, "Objects/Props/_Lighting/LightGround_01/Sub/T_Other_C", Emb),
            };
            List<string> c = GraphTextureDefaults.OwnConstants(g, new[] { perm, perm });
            Assert.AreEqual(2, c.Count);
            Assert.AreEqual("CS", GraphTextureDefaults.Family(c[0]));
            Assert.AreEqual("NW", GraphTextureDefaults.Family(c[1]));
        }

        [Test]
        public void ConstantsBindByFamilyOnlyOnAFallbackPreset()
        {
            var m = new FrostyCLI.Core.PortManifest { Mesh = new FrostyCLI.Core.PortMesh() };
            var ice = new FrostyCLI.Core.PortMaterial { Index = 0, ShaderPath = "Objects/Nature/Arctic/_ArcticBase/ArcticBase_IceWall_Large_01/SS_ArcticBase_IceWall_Large_01" };
            ice.Textures.Add(new FrostyCLI.Core.PortTexture { Param = "C", Source = "shader-constant" });
            ice.Textures.Add(new FrostyCLI.Core.PortTexture { Param = "N", Source = "shader-constant" });
            ice.Textures.Add(new FrostyCLI.Core.PortTexture { Param = "RGBM", Source = "shader-constant" });
            m.Mesh.Materials.Add(ice);
            var targets = new List<FrostySdk.Managers.EbxAssetEntry> { new FrostySdk.Managers.EbxAssetEntry { Name = "Shaders/Presets/SS_PropsMetallicPreset" } };
            FrostyCLI.Commands.PortCommand.DropKeptGraphDefaults(m, targets, new List<string>());
            Assert.AreEqual(2, ice.Textures.Count);
            Assert.AreEqual("BaseColor", ice.Textures[0].Param);
            Assert.AreEqual("Normal", ice.Textures[1].Param);
            Assert.AreEqual(null, FrostyCLI.Commands.PortCommand.FamilyMap("Shaders/Presets/SS_PropsGenericPreset_EmissiveVertexColorRandom"));
            Assert.AreEqual("CS", FrostyCLI.Commands.PortCommand.FamilyMap("Shaders/Presets/SS_PropsNonMetallicPreset")["CS"]);
        }

        [Test]
        public void AParameterWithoutAnEmbeddedDefaultHasNone()
        {
            var perm = new List<B> { new B(0, "BaseColor", Ext), new B(1, "T_Other_C", Emb) };
            Assert.AreEqual(0, GraphTextureDefaults.Resolve(new[] { perm }).Count);
        }
    }
}
