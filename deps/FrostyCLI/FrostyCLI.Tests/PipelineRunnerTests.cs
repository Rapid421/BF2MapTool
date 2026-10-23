using System;
using System.Collections.Generic;
using System.IO;
using FrostyCLI.Core;
using FrostyCLI.Pipeline;

namespace FrostyCLI.Tests
{
    // How the runner treats a step it cannot run, and which ops reach the write bus. The bus is
    // replaced by a recorder, so none of these touch a game -- they pin the runner's own behaviour.
    public class PipelineRunnerTests
    {
        private sealed class RecordingBus : IWriteOpBus
        {
            public readonly List<string> Calls = new List<string>();
            public readonly HashSet<string> Failing = new HashSet<string>();

            public int Execute(string op, string[] args, ExportLogger logger, OutputFormatter formatter)
            {
                Calls.Add(op + "|" + string.Join("|", args));
                return Failing.Contains(op) ? 1 : 0;
            }
        }

        private static int Run(string stepsJson, RecordingBus bus)
        {
            string stderr;
            return Run(stepsJson, bus, out stderr);
        }

        private static int Run(string stepsJson, RecordingBus bus, out string stderr)
        {
            stderr = null;
            string path = Path.Combine(Path.GetTempPath(), "pipeline_runner_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "{\"steps\":[" + stepsJson + "]}");
            IWriteOpBus previousBus = WriteOpBus.Instance;
            TextWriter previousErr = Console.Error;
            var captured = new StringWriter();
            WriteOpBus.Instance = bus;
            Console.SetError(captured);
            try
            {
                return new JsonPipelineRunner().Run(path, new ExportLogger(quiet: true), new OutputFormatter(false));
            }
            finally
            {
                Console.SetError(previousErr);
                WriteOpBus.Instance = previousBus;
                stderr = captured.ToString();
                File.Delete(path);
            }
        }

        private static string OnlyCall(RecordingBus bus)
        {
            Assert.AreEqual(1, bus.Calls.Count);
            return bus.Calls[0];
        }

        // ---- a step the runner cannot run is a failure, not a skip ---------------------------------

        [Test]
        public void UnknownOp_FailsTheRun()
        {
            // A typo used to log a warning and report success, so the pipeline "finished" without
            // doing the step at all.
            Assert.AreEqual(1, Run("{\"op\":\"mvdb-cpy\",\"ebx\":\"s2/db\"}", new RecordingBus()));
        }

        [Test]
        public void UnknownOp_WithContinue_RunsTheRestAndStillFails()
        {
            var bus = new RecordingBus();
            int rc = Run("{\"op\":\"no-such-op\",\"on-error\":\"continue\"},{\"op\":\"ebx-delete\",\"ebx\":\"s2/a\"}", bus);
            Assert.AreEqual(1, rc);
            Assert.AreEqual("ebx-delete|s2/a", OnlyCall(bus));
        }

        [Test]
        public void ContinuedFailures_AreListedWhenTheRunEnds()
        {
            var bus = new RecordingBus();
            bus.Failing.Add("ebx-delete");
            string stderr;
            int rc = Run(
                "{\"op\":\"ebx-delete\",\"ebx\":\"s2/a\",\"on-error\":\"continue\"}," +
                "{\"op\":\"ebx-revert\",\"ebx\":\"s2/b\"}," +
                "{\"op\":\"ebx-delete\",\"ebx\":\"s2/c\",\"on-error\":\"continue\"}", bus, out stderr);
            Assert.AreEqual(1, rc);
            Assert.IsTrue(stderr.Contains("2 of 3 steps failed"), stderr);
            Assert.IsTrue(stderr.Contains("step 0 (ebx-delete)"), stderr);
            Assert.IsTrue(stderr.Contains("step 2 (ebx-delete)"), stderr);
            Assert.IsFalse(stderr.Contains("step 1 ("), stderr);
        }

        // ---- ops the CLI has that pipelines used to skip -------------------------------------------

        [Test]
        public void ModApply_ReachesTheBus()
        {
            var bus = new RecordingBus();
            Assert.AreEqual(0, Run("{\"op\":\"mod-apply\",\"file\":\"base.fbmod\"}", bus));
            Assert.AreEqual("mod-apply|apply|base.fbmod", OnlyCall(bus));
        }

        [Test]
        public void ModApply_MissingFile_FailsWithoutCallingTheBus()
        {
            var bus = new RecordingBus();
            Assert.AreEqual(1, Run("{\"op\":\"mod-apply\"}", bus));
            Assert.AreEqual(0, bus.Calls.Count);
        }

        [Test]
        public void ModTest_ForwardsLevelModeAndTimeout()
        {
            var bus = new RecordingBus();
            Assert.AreEqual(0, Run("{\"op\":\"mod-test\",\"file\":\"x.fbmod\",\"level\":\"S2/Levels/Kashyyyk_01/Kashyyyk_01\",\"mode\":\"Mode1\",\"timeout\":300}", bus));
            Assert.AreEqual("mod-test|test|x.fbmod|--level|S2/Levels/Kashyyyk_01/Kashyyyk_01|--mode|Mode1|--timeout|300", OnlyCall(bus));
        }

        [Test]
        public void ModDiff_ForwardsBaseFormatAndOut()
        {
            var bus = new RecordingBus();
            Assert.AreEqual(0, Run("{\"op\":\"mod-diff\",\"base\":\"old.fbmod\",\"format\":\"json\",\"out\":\"diff.json\"}", bus));
            Assert.AreEqual("mod-diff|diff|--base|old.fbmod|--format|json|--out|diff.json", OnlyCall(bus));
        }

        [Test]
        public void ModLint_ForwardsFormatAndStrict()
        {
            var bus = new RecordingBus();
            Assert.AreEqual(0, Run("{\"op\":\"mod-lint\",\"format\":\"json\",\"strict\":true}", bus));
            Assert.AreEqual("mod-lint|--format|json|--strict", OnlyCall(bus));
        }

        [Test]
        public void ModLint_Findings_FailTheStep()
        {
            // mod lint exits 2 when it reports errors; a pipeline gate has to treat that as failure.
            var bus = new RecordingBus();
            bus.Failing.Add("mod-lint");
            Assert.AreEqual(1, Run("{\"op\":\"mod-lint\"}", bus));
        }

        [Test]
        public void LevelValidate_ForwardsLevelAndStrict()
        {
            var bus = new RecordingBus();
            Assert.AreEqual(0, Run("{\"op\":\"level-validate\",\"ebx\":\"S2/Levels/CloudCity_01/CloudCity_01\",\"strict\":true}", bus));
            Assert.AreEqual("level-validate|S2/Levels/CloudCity_01/CloudCity_01|--strict", OnlyCall(bus));
        }

        [Test]
        public void MvdbCopy_ForwardsMeshSourceAndHash()
        {
            var bus = new RecordingBus();
            Assert.AreEqual(0, Run("{\"op\":\"mvdb-copy\",\"ebx\":\"s2/db\",\"mesh\":\"s2/mesh\",\"from\":\"s2/home_db\",\"variation_hash\":\"0x10\"}", bus));
            Assert.AreEqual("mvdb-copy|s2/db|--mesh|s2/mesh|--from|s2/home_db|--variation-hash|0x10", OnlyCall(bus));
        }

        [Test]
        public void MvdbCopy_MissingFrom_FailsWithoutCallingTheBus()
        {
            var bus = new RecordingBus();
            Assert.AreEqual(1, Run("{\"op\":\"mvdb-copy\",\"ebx\":\"s2/db\",\"mesh\":\"s2/mesh\"}", bus));
            Assert.AreEqual(0, bus.Calls.Count);
        }

        [Test]
        public void BundleComplete_NeverSavesMidPipeline()
        {
            // The CLI command saves the loaded project in place by default. Inside a pipeline that
            // would overwrite the input before the pipeline's own project-save runs.
            var bus = new RecordingBus();
            Assert.AreEqual(0, Run("{\"op\":\"bundle-complete\",\"level\":\"win32/a,win32/b\",\"cosmetic\":true}", bus));
            Assert.AreEqual("bundle-complete|--level|win32/a,win32/b|--cosmetic|--no-save", OnlyCall(bus));
        }

        // ---- bundle-add / bundle-remove name the table the asset lives in ---------------------------

        [Test]
        public void BundleAdd_Res_NamesTheResTable()
        {
            // A texture's res shares its EBX's name and the EBX lookup ignores case, so without --kind the
            // step bundled the EBX again and the res never reached the bundle (the level then hung at load).
            var bus = new RecordingBus();
            Assert.AreEqual(0, Run("{\"op\":\"bundle-add\",\"res\":\"fx/textures/misc/t_a\",\"bundle\":\"win32/b\"}", bus));
            Assert.AreEqual("bundle-add|fx/textures/misc/t_a|--to|win32/b|--kind|res", OnlyCall(bus));
        }

        [Test]
        public void BundleAdd_EbxAndChunk_NameTheirTables()
        {
            var bus = new RecordingBus();
            Assert.AreEqual(0, Run("{\"op\":\"bundle-add\",\"ebx\":\"FX/Textures/Misc/T_A\",\"bundle\":\"win32/b\",\"with_resources\":true},"
                                 + "{\"op\":\"bundle-add\",\"chunk_id\":\"9e10fc96-3c30-6e6b-85f8-1d4dfa91eae3\",\"bundle\":\"win32/b\"}", bus));
            Assert.AreEqual(2, bus.Calls.Count);
            Assert.AreEqual("bundle-add|FX/Textures/Misc/T_A|--to|win32/b|--kind|ebx|--with-resources", bus.Calls[0]);
            Assert.AreEqual("bundle-add|9e10fc96-3c30-6e6b-85f8-1d4dfa91eae3|--to|win32/b|--kind|chunk", bus.Calls[1]);
        }

        [Test]
        public void BundleRemove_Res_NamesTheResTable()
        {
            var bus = new RecordingBus();
            Assert.AreEqual(0, Run("{\"op\":\"bundle-remove\",\"res\":\"fx/textures/misc/t_a\",\"bundle\":\"win32/b\"}", bus));
            Assert.AreEqual("bundle-remove|fx/textures/misc/t_a|--from|win32/b|--kind|res", OnlyCall(bus));
        }

        [Test]
        public void AtlasChunkId_ReadsBytes16To32()
        {
            // t_tauntaun_14x1_01_c's header: flags 0, 1792x128, unknown 0, borders 0, then its chunk guid
            byte[] res = new byte[92];
            BitConverter.GetBytes((ushort)1792).CopyTo(res, 2);
            BitConverter.GetBytes((ushort)128).CopyTo(res, 4);
            Guid chunk = new Guid("9e10fc96-3c30-6e6b-85f8-1d4dfa91eae3");
            chunk.ToByteArray().CopyTo(res, 16);
            Assert.AreEqual(chunk, FrostyCLI.Commands.BundleCommand.AtlasChunkId(res));
            Assert.AreEqual(Guid.Empty, FrostyCLI.Commands.BundleCommand.AtlasChunkId(new byte[31]));
        }

        [Test]
        public void MeshDepotName_IsLowerMeshPathPlusMeshBlocks()
        {
            // `bundle add --with-resources` must carry this res with the mesh: HOTH_RG2's 120 unbundled ported depots
            // (Objects/Ported/Hoth2015/IonCannon_01/IonCannon_01_Mesh -> ..._mesh_mesh/blocks) were the invisible meshes
            Assert.AreEqual("objects/ported/hoth2015/ioncannon_01/ioncannon_01_mesh_mesh/blocks",
                FrostyCLI.Commands.BundleCommand.MeshDepotName("Objects/Ported/Hoth2015/IonCannon_01/IonCannon_01_Mesh"));
            Assert.AreEqual("_mesh/blocks", FrostyCLI.Commands.BundleCommand.MeshDepotName(null));
        }
    }
}
