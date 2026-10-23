using System;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class LevelCommandTests
    {
        private static ExportLogger Logger() { return new ExportLogger(quiet: true); }
        private static OutputFormatter Fmt() { return new OutputFormatter(json: false); }

        [Test]
        public void Level_NoSubcommand_Returns1()
        {
            var cmd = new LevelCommand();
            int r = cmd.Execute(new string[0], Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void Level_UnknownSubcommand_Returns1()
        {
            var cmd = new LevelCommand();
            int r = cmd.Execute(new[] { "move" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelList_MissingPath_Returns1()
        {
            var cmd = new LevelCommand();
            int r = cmd.Execute(new[] { "list" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelTree_MissingPath_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "tree" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelStats_MissingPath_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "stats" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelAdd_MissingLevelPath_Returns1()
        {
            var cmd = new LevelCommand();
            int r = cmd.Execute(new[] { "add" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelAdd_MissingBlueprint_Returns1()
        {
            var cmd = new LevelCommand();
            int r = cmd.Execute(new[] { "add", "xp2/levels/foo" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelList_FormatFlagParsed()
        {
            // Should not return arg-validation error; at most fails with 1 due to no AssetManager.
            var cmd = new LevelCommand();
            int r = cmd.Execute(new[] { "list", "xp2/foo", "--format", "json" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelMove_MemberWithoutInstance_Returns1()
        {
            int r = new LevelCommand().Execute(
                new[] { "move", "xp2/foo", "--member", "3", "--pos", "1,2,3" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelMove_InstanceWithoutMember_Returns1()
        {
            int r = new LevelCommand().Execute(
                new[] { "move", "xp2/foo", "--instance", "0", "--pos", "1,2,3" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelMove_GuidAndMemberTogether_Returns1()
        {
            int r = new LevelCommand().Execute(new[]
            {
                "move", "xp2/foo", "--guid", Guid.NewGuid().ToString(),
                "--member", "0", "--instance", "0", "--pos", "1,2,3"
            }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelMove_GroupedWithoutPos_Returns1()
        {
            int r = new LevelCommand().Execute(
                new[] { "move", "xp2/foo", "--member", "0", "--instance", "0" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelAdd_BlueprintAndMemberTogether_Returns1()
        {
            // Distinct operations: --blueprint places a new entity, --member appends an instance of
            // a blueprint the member already holds.
            int r = new LevelCommand().Execute(
                new[] { "add", "xp2/foo", "--blueprint", "xp2/bp", "--member", "0", "--pos", "1,2,3" },
                Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelAdd_BlueprintAsGroupMemberWithoutPos_Returns1()
        {
            int r = new LevelCommand().Execute(
                new[] { "add", "xp2/foo", "--group", "0", "--blueprint", "xp2/bp" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelAdd_GroupedWithoutPos_Returns1()
        {
            int r = new LevelCommand().Execute(
                new[] { "add", "xp2/foo", "--member", "0" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelExportLights_MissingPath_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "export-lights" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelExportLights_UnknownOption_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "export-lights", "xp2/layer", "--bogus" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelExportLights_NegativeDepth_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "export-lights", "xp2/layer", "--max-depth", "-1" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelImportLights_MissingFrom_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "import-lights", "xp2/layer" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelImportLights_MissingLayer_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "import-lights", "--from", "lights.json" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelImportLights_BadScale_Returns1BeforeSessionWork()
        {
            int r = new LevelCommand().Execute(new[] { "import-lights", "xp2/layer", "--from", "lights.json", "--intensity-scale", "0" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelImportLights_BadOffset_Returns1BeforeSessionWork()
        {
            int r = new LevelCommand().Execute(new[] { "import-lights", "xp2/layer", "--from", "lights.json", "--offset", "1,2" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelImportLights_MissingFile_Returns1BeforeSessionWork()
        {
            int r = new LevelCommand().Execute(new[] { "import-lights", "xp2/layer", "--from", "definitely-not-here.json" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelAddEntity_MissingLayer_Returns1()
        {
            int r = new LevelCommand().Execute(
                new[] { "add-entity", "--type", "AlternateSpawnEntityData" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelAddEntity_MissingType_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "add-entity", "xp2/layer" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelAddEntity_MalformedSet_Returns1()
        {
            int r = new LevelCommand().Execute(new[]
            { "add-entity", "xp2/layer", "--type", "AlternateSpawnEntityData", "--set", "Enabled" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelAddEntity_UnknownOption_Returns1()
        {
            int r = new LevelCommand().Execute(new[]
            { "add-entity", "xp2/layer", "--type", "AlternateSpawnEntityData", "--unknown" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelAddEntity_InvalidTransform_Returns1BeforeSessionWork()
        {
            int r = new LevelCommand().Execute(new[]
            { "add-entity", "xp2/layer", "--type", "AlternateSpawnEntityData", "--pos", "1,2" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelRemove_MemberWithoutInstance_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "remove", "xp2/foo", "--member", "1" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelRemove_InstanceWithoutMember_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "remove", "xp2/foo", "--instance", "0" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelRemove_GuidAndMemberTogether_Returns1()
        {
            int r = new LevelCommand().Execute(new[]
            { "remove", "xp2/foo", "--guid", Guid.NewGuid().ToString(), "--member", "0", "--instance", "0" },
                Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelValidate_AllWithPath_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "validate", "--all", "xp2/foo" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelList_MalformedNear_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "list", "xp2/foo", "--near", "1,2" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelList_NonNumericNear_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "list", "xp2/foo", "--near", "a,b,c" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelValidate_MissingPath_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "validate" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelValidate_NegativeMaxDepth_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "validate", "xp2/foo", "--max-depth", "-1" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelValidate_NegativeMaxIssues_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "validate", "xp2/foo", "--max-issues", "-1" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void LevelValidate_FlagsParsed()
        {
            // Arg parsing must succeed; without an AssetManager the command still exits 1, and in
            // particular never 2 -- exit 2 is reserved for actual validation findings.
            int r = new LevelCommand().Execute(
                new[] { "validate", "xp2/foo", "--format", "json", "--strict", "--max-depth", "4" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void RadiosityOverride_TypeNames_TakeThePrefixOptionally()
        {
            Assert.AreEqual("RadiosityTypeOverride_Dynamic", LevelRadiosityOverride.Norm("Dynamic"));
            Assert.AreEqual("RadiosityTypeOverride_TerrainProjected", LevelRadiosityOverride.Norm("RadiosityTypeOverride_TerrainProjected"));
            Assert.AreEqual("any", LevelRadiosityOverride.Norm("ANY"));
        }

        [Test]
        public void RadiosityOverride_MissingArgs_Returns1()
        {
            Assert.AreEqual(1, LevelRadiosityOverride.Execute(new[] { "xp2/foo", "--mesh", "trench", "--from", "any" }, Logger(), Fmt()));
            Assert.AreEqual(1, LevelRadiosityOverride.Execute(new[] { "xp2/foo", "--mesh", "trench", "--from", "None", "--to", "any" }, Logger(), Fmt()));
        }

        [Test]
        public void TransformGroup_ParseTransform12_ReadsRowsInOrder()
        {
            LinearTransformData t = LevelCommand.ParseTransform12("-0.792,0,-0.6105, 0,1,0, 0.6105,0,-0.792, 270.8798,359.68,-1635.7933");
            Assert.AreEqual(-0.792f, t.Right[0]); Assert.AreEqual(-0.6105f, t.Right[2]);
            Assert.AreEqual(1f, t.Up[1]);
            Assert.AreEqual(0.6105f, t.Forward[0]); Assert.AreEqual(-0.792f, t.Forward[2]);
            Assert.AreEqual(270.8798f, t.Trans[0]); Assert.AreEqual(-1635.7933f, t.Trans[2]);
        }

        [Test]
        public void TransformGroup_ParseTransform12_RejectsWrongCount()
        {
            Assert.Throws<ArgumentException>(delegate { LevelCommand.ParseTransform12("1,0,0,0,1,0,0,0,1,0,0"); });
        }

        [Test]
        public void TransformGroup_MissingTransform_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "transform-group", "xp2/foo", "--group", "0" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void TransformGroup_BadCentroidGuard_Returns1()
        {
            int r = new LevelCommand().Execute(new[] { "transform-group", "xp2/foo", "--group", "0",
                "--transform", "1,0,0,0,1,0,0,0,1,0,0,0", "--if-centroid-near", "1,2,3" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }
    }
}
