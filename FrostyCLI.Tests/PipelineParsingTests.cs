using System;
using System.Collections.Generic;
using System.IO;
using FrostyCLI.Core;
using FrostyCLI.Pipeline;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests
{
    public class PipelineParsingTests
    {
        private sealed class CaptureWriteBus : IWriteOpBus
        {
            public string Op;
            public string[] Args;
            public int Execute(string op, string[] args, ExportLogger logger, OutputFormatter formatter)
            {
                Op = op;
                Args = args;
                return 0;
            }
        }

        private string WriteTempPipeline(object obj)
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path, JsonConvert.SerializeObject(obj));
            return path;
        }

        [Test]
        public void ValidPipeline_StepsParsedCorrectly()
        {
            string path = WriteTempPipeline(new
            {
                game = "swbf2",
                steps = new object[]
                {
                    new { op = "export", ebx = "path/to/asset", out_ = "out/asset.fbx", format = "fbx" }
                }
            });
            try
            {
                // Manually set the "out" key since "out" is a C# keyword
                var json = File.ReadAllText(path).Replace("out_", "out");
                var root = JObject.Parse(json);
                var steps = root["steps"].ToObject<List<PipelineStep>>();
                Assert.AreEqual(1, steps.Count);
                Assert.AreEqual("export", steps[0].Op);
                Assert.AreEqual("path/to/asset", steps[0].Ebx);
                Assert.AreEqual("out/asset.fbx", steps[0].Out);
                Assert.AreEqual("fbx", steps[0].Format);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void OnError_DefaultsToFail()
        {
            string path = WriteTempPipeline(new
            {
                steps = new object[] { new { op = "export", ebx = "foo", out_ = "bar.fbx" } }
            });
            try
            {
                var json = File.ReadAllText(path).Replace("out_", "out");
                var root = JObject.Parse(json);
                var steps = root["steps"].ToObject<List<PipelineStep>>();
                Assert.AreEqual("fail", steps[0].OnError);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void MissingGamePath_IsNull()
        {
            string path = WriteTempPipeline(new { steps = new object[0] });
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                string gamePath = root["game-path"] != null ? root["game-path"].Value<string>() : null;
                Assert.IsNull(gamePath);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void UnknownFields_DoNotCrash()
        {
            string path = WriteTempPipeline(new
            {
                steps = new object[]
                {
                    new { op = "export", ebx = "foo", out_ = "bar.fbx", unknownField = "ignored" }
                }
            });
            try
            {
                var json = File.ReadAllText(path).Replace("out_", "out");
                var root = JObject.Parse(json);
                var steps = root["steps"].ToObject<List<PipelineStep>>();
                Assert.AreEqual(1, steps.Count);
                Assert.AreEqual("export", steps[0].Op);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void MultipleSteps_AllParsed()
        {
            string path = WriteTempPipeline(new
            {
                steps = new object[]
                {
                    new { op = "export", ebx = "a", out_ = "a.fbx" },
                    new { op = "export", ebx = "b", out_ = "b.fbx" },
                    new { op = "export", ebx = "c", out_ = "c.fbx" }
                }
            });
            try
            {
                var json = File.ReadAllText(path).Replace("out_", "out");
                var root = JObject.Parse(json);
                var steps = root["steps"].ToObject<List<PipelineStep>>();
                Assert.AreEqual(3, steps.Count);
                Assert.AreEqual("a", steps[0].Ebx);
                Assert.AreEqual("c", steps[2].Ebx);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void InvalidJson_ThrowsJsonException()
        {
            string path = Path.Combine(Path.GetTempPath(), "bad_pipeline_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path, "{ this is not valid json }}}");
            try
            {
                Assert.Throws<JsonException>(() => JObject.Parse(File.ReadAllText(path)));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void ProjectLoad_Op_Parsed()
        {
            string path = WriteTempPipeline(new
            {
                steps = new object[]
                {
                    new { op = "project-load", file = "mymod.fbproject" }
                }
            });
            try
            {
                var root  = JObject.Parse(File.ReadAllText(path));
                var steps = root["steps"].ToObject<System.Collections.Generic.List<PipelineStep>>();
                Assert.AreEqual(1, steps.Count);
                Assert.AreEqual("project-load", steps[0].Op);
                Assert.AreEqual("mymod.fbproject", steps[0].File);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void ProjectSave_Op_Parsed()
        {
            string path = WriteTempPipeline(new
            {
                steps = new object[]
                {
                    new { op = "project-save", file = "mymod.fbproject" }
                }
            });
            try
            {
                var root  = JObject.Parse(File.ReadAllText(path));
                var steps = root["steps"].ToObject<System.Collections.Generic.List<PipelineStep>>();
                Assert.AreEqual("project-save", steps[0].Op);
                Assert.AreEqual("mymod.fbproject", steps[0].File);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void ModBuild_Op_ParsesAllFields()
        {
            string path = WriteTempPipeline(new
            {
                steps = new object[]
                {
                    new
                    {
                        op              = "mod-build",
                        out_            = "MyMod.fbmod",
                        mod_name        = "Cryo Chamber",
                        mod_author      = "hartz",
                        mod_version     = "1.0.0",
                        mod_description = "A test mod"
                    }
                }
            });
            try
            {
                var json = File.ReadAllText(path)
                    .Replace("out_",            "out")
                    .Replace("mod_name",        "mod-name")
                    .Replace("mod_author",      "mod-author")
                    .Replace("mod_version",     "mod-version")
                    .Replace("mod_description", "mod-description");
                var root  = JObject.Parse(json);
                var steps = root["steps"].ToObject<System.Collections.Generic.List<PipelineStep>>();
                Assert.AreEqual("mod-build",    steps[0].Op);
                Assert.AreEqual("MyMod.fbmod",  steps[0].Out);
                Assert.AreEqual("Cryo Chamber", steps[0].ModName);
                Assert.AreEqual("hartz",        steps[0].ModAuthor);
                Assert.AreEqual("1.0.0",        steps[0].ModVersion);
                Assert.AreEqual("A test mod",   steps[0].ModDescription);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void EbxSet_Op_ParsesAllFields()
        {
            string path = WriteTempPipeline(new
            {
                steps = new object[]
                {
                    new { op = "ebx-set", ebx = "xp2/foo", prop = "Name", value = "bar" }
                }
            });
            try
            {
                var root  = JObject.Parse(File.ReadAllText(path));
                var steps = root["steps"].ToObject<List<PipelineStep>>();
                Assert.AreEqual("ebx-set", steps[0].Op);
                Assert.AreEqual("xp2/foo", steps[0].Ebx);
                Assert.AreEqual("Name",    steps[0].Prop);
                Assert.AreEqual("bar",     steps[0].Value.ToString());
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void EbxSet_NumericValue_ParsedAsJToken()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path, "{\"steps\":[{\"op\":\"ebx-set\",\"ebx\":\"xp2/foo\",\"prop\":\"Count\",\"value\":1.0}]}");
            try
            {
                var root  = JObject.Parse(File.ReadAllText(path));
                var steps = root["steps"].ToObject<List<PipelineStep>>();
                Assert.IsTrue(steps[0].Value != null);
                Assert.AreEqual("1", steps[0].Value.ToString());
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void EbxClone_Op_ParsesFields()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path, "{\"steps\":[{\"op\":\"ebx-clone\",\"source\":\"xp2/foo\",\"as\":\"xp2/foo_cryo\"}]}");
            try
            {
                var root  = JObject.Parse(File.ReadAllText(path));
                var steps = root["steps"].ToObject<List<PipelineStep>>();
                Assert.AreEqual("ebx-clone",    steps[0].Op);
                Assert.AreEqual("xp2/foo",      steps[0].Source);
                Assert.AreEqual("xp2/foo_cryo", steps[0].AsPath);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void LevelAdd_Op_ParsesAllFields()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path,
                "{\"steps\":[{\"op\":\"level-add\",\"ebx\":\"xp2/levels/lvl\",\"blueprint\":\"xp2/bp\",\"pos\":\"0,0,0\",\"rot\":\"0,90,0\",\"scale\":\"1\"}]}");
            try
            {
                var root  = JObject.Parse(File.ReadAllText(path));
                var steps = root["steps"].ToObject<List<PipelineStep>>();
                Assert.AreEqual("level-add",      steps[0].Op);
                Assert.AreEqual("xp2/levels/lvl", steps[0].Ebx);
                Assert.AreEqual("xp2/bp",         steps[0].Blueprint);
                Assert.AreEqual("0,0,0",          steps[0].Pos);
                Assert.AreEqual("0,90,0",         steps[0].Rot);
                Assert.AreEqual("1",              steps[0].Scale);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void LevelAddEntity_Op_ParsesAndForwardsAllFields()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path,
                "{\"steps\":[{\"op\":\"level-add-entity\",\"ebx\":\"s2/levels/layer\"," +
                "\"type\":\"AlternateSpawnEntityData\",\"pos\":\"1,2,3\",\"rot\":\"0,90,0\"," +
                "\"scale\":\"1\",\"transform_field\":\"Transform\"," +
                "\"set\":{\"Team\":\"Team1\",\"Priority\":7.5,\"Enabled\":true}}]}");

            IWriteOpBus oldBus = WriteOpBus.Instance;
            var capture = new CaptureWriteBus();
            WriteOpBus.Instance = capture;
            try
            {
                int result = new JsonPipelineRunner().Run(path, new ExportLogger(quiet: true), new OutputFormatter(json: false));
                Assert.AreEqual(0, result);
                Assert.AreEqual("level-add-entity", capture.Op);
                string forwarded = string.Join("|", capture.Args);
                Assert.IsTrue(forwarded.IndexOf("s2/levels/layer|--type|AlternateSpawnEntityData", StringComparison.Ordinal) >= 0);
                Assert.IsTrue(forwarded.IndexOf("--pos|1,2,3", StringComparison.Ordinal) >= 0);
                Assert.IsTrue(forwarded.IndexOf("--transform-field|Transform", StringComparison.Ordinal) >= 0);
                Assert.IsTrue(forwarded.IndexOf("--set|Team=Team1", StringComparison.Ordinal) >= 0);
                Assert.IsTrue(forwarded.IndexOf("--set|Priority=7.5", StringComparison.Ordinal) >= 0);
                Assert.IsTrue(forwarded.IndexOf("--set|Enabled=True", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            finally
            {
                WriteOpBus.Instance = oldBus;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void LevelImportLights_Op_ParsesAndForwardsAllFields()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path,
                "{\"steps\":[{\"op\":\"level-import-lights\",\"ebx\":\"s2/levels/layer\"," +
                "\"from\":\"lights.json\",\"intensity_scale\":0.5,\"offset\":\"1,2,3\",\"skip_disabled\":true}]}");

            IWriteOpBus oldBus = WriteOpBus.Instance;
            var capture = new CaptureWriteBus();
            WriteOpBus.Instance = capture;
            try
            {
                int result = new JsonPipelineRunner().Run(path, new ExportLogger(quiet: true), new OutputFormatter(json: false));
                Assert.AreEqual(0, result);
                Assert.AreEqual("level-import-lights", capture.Op);
                string forwarded = string.Join("|", capture.Args);
                Assert.IsTrue(forwarded.IndexOf("s2/levels/layer|--from|lights.json", StringComparison.Ordinal) >= 0);
                Assert.IsTrue(forwarded.IndexOf("--intensity-scale|0.5", StringComparison.Ordinal) >= 0);
                Assert.IsTrue(forwarded.IndexOf("--offset|1,2,3", StringComparison.Ordinal) >= 0);
                Assert.IsTrue(forwarded.IndexOf("--skip-disabled", StringComparison.Ordinal) >= 0);
            }
            finally
            {
                WriteOpBus.Instance = oldBus;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void ImportTexture_Op_ParsesAllFields()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path,
                "{\"steps\":[{\"op\":\"import-texture\",\"file\":\"a.dds\",\"as\":\"s2/x\",\"format\":\"dds\",\"no_mipmaps\":true}]}");
            try
            {
                var root  = JObject.Parse(File.ReadAllText(path));
                var steps = root["steps"].ToObject<List<PipelineStep>>();
                Assert.AreEqual("import-texture", steps[0].Op);
                Assert.AreEqual("a.dds",          steps[0].File);
                Assert.AreEqual("s2/x",           steps[0].AsPath);
                Assert.AreEqual("dds",            steps[0].Format);
                Assert.IsTrue(steps[0].NoMipmaps);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void ImportMesh_Op_ParsesAllFields()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path,
                "{\"steps\":[{\"op\":\"import-mesh\",\"file\":\"a.fbx\",\"as\":\"s2/mesh\",\"scale\":\"2\"}]}");
            try
            {
                var root  = JObject.Parse(File.ReadAllText(path));
                var steps = root["steps"].ToObject<List<PipelineStep>>();
                Assert.AreEqual("import-mesh", steps[0].Op);
                Assert.AreEqual("a.fbx",       steps[0].File);
                Assert.AreEqual("s2/mesh",     steps[0].AsPath);
                Assert.AreEqual("2",           steps[0].Scale);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void PortOps_ParseBatchFields()
        {
            string json = "{\"steps\":[" +
                "{\"op\":\"port-import\",\"file\":\"pkg\",\"as\":\"s2/new\",\"template_prefix\":\"s2/\",\"keep_template_materials\":true,\"no_physics\":true,\"keep_composite\":true,\"mvdb\":\"s2/db\"}," +
                "{\"op\":\"port-import-layer\",\"file\":\"layer\",\"into\":\"s2/level\",\"registry\":\"s2/registry\",\"ported_prefix\":\"s2/ported\",\"prefer_ported\":true,\"exclude_guid\":true,\"offset\":\"1,2,3\"}]}";
            var steps = JObject.Parse(json)["steps"].ToObject<List<PipelineStep>>();
            Assert.AreEqual("s2/", steps[0].TemplatePrefix);
            Assert.IsTrue(steps[0].KeepTemplateMaterials);
            Assert.IsTrue(steps[0].NoPhysics);
            Assert.IsTrue(steps[0].KeepComposite);
            Assert.AreEqual("s2/db", steps[0].Mvdb);
            Assert.AreEqual("s2/level", steps[1].Into);
            Assert.AreEqual("s2/registry", steps[1].Registry);
            Assert.AreEqual("s2/ported", steps[1].PortedPrefix);
            Assert.IsTrue(steps[1].PreferPorted);
            Assert.IsTrue(steps[1].ExcludeGuid);
            Assert.AreEqual("1,2,3", steps[1].Offset);
        }

        [Test]
        public void MvdbAdd_MaterialIsNotRequired_AndHashIsForwarded()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path,
                "{\"steps\":[{\"op\":\"mvdb-add\",\"ebx\":\"s2/level/mvdb\",\"mesh\":\"s2/mesh\",\"variation_hash\":\"0x1234\"}]}");
            IWriteOpBus previous = WriteOpBus.Instance;
            var bus = new CapturingWriteOpBus();
            WriteOpBus.Instance = bus;
            try
            {
                int rc = new JsonPipelineRunner().Run(path, new ExportLogger(quiet: true), new OutputFormatter(false));
                Assert.AreEqual(0, rc);
                Assert.AreEqual("mvdb-add", bus.Op);
                Assert.AreEqual(5, bus.Args.Length);
                Assert.AreEqual("s2/level/mvdb", bus.Args[0]);
                Assert.AreEqual("--mesh", bus.Args[1]);
                Assert.AreEqual("s2/mesh", bus.Args[2]);
                Assert.AreEqual("--variation-hash", bus.Args[3]);
                Assert.AreEqual("0x1234", bus.Args[4]);
            }
            finally
            {
                WriteOpBus.Instance = previous;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void MvdbAdd_LikeIsForwarded()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path,
                "{\"steps\":[{\"op\":\"mvdb-add\",\"ebx\":\"s2/level/mvdb\",\"mesh\":\"s2/mesh\",\"like\":\"s2/donor\"}]}");
            IWriteOpBus previous = WriteOpBus.Instance;
            var bus = new CapturingWriteOpBus();
            WriteOpBus.Instance = bus;
            try
            {
                int rc = new JsonPipelineRunner().Run(path, new ExportLogger(quiet: true), new OutputFormatter(false));
                Assert.AreEqual(0, rc);
                Assert.AreEqual("mvdb-add", bus.Op);
                Assert.AreEqual(5, bus.Args.Length);
                Assert.AreEqual("--like", bus.Args[3]);
                Assert.AreEqual("s2/donor", bus.Args[4]);
            }
            finally
            {
                WriteOpBus.Instance = previous;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void LevelPlace_RequiresGroupAndForwardsOptions()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path,
                "{\"steps\":[{\"op\":\"level-place\",\"ebx\":\"s2/level\",\"blueprint\":\"s2/bp\"," +
                "\"pos\":\"1,2,3\",\"group\":\"0\",\"like\":\"s2/donor\"}]}");
            IWriteOpBus previous = WriteOpBus.Instance;
            var bus = new CapturingWriteOpBus();
            WriteOpBus.Instance = bus;
            try
            {
                int rc = new JsonPipelineRunner().Run(path, new ExportLogger(quiet: true), new OutputFormatter(false));
                Assert.AreEqual(0, rc);
                Assert.AreEqual("level-place", bus.Op);
                Assert.AreEqual("s2/level", bus.Args[0]);
                Assert.IsTrue(Array.IndexOf(bus.Args, "--group") > 0);
                Assert.IsTrue(Array.IndexOf(bus.Args, "--like") > 0);
            }
            finally
            {
                WriteOpBus.Instance = previous;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void LevelPlace_MissingGroup_Fails()
        {
            string path = Path.Combine(Path.GetTempPath(), "pipeline_test_" + Guid.NewGuid() + ".json");
            File.WriteAllText(path,
                "{\"steps\":[{\"op\":\"level-place\",\"ebx\":\"s2/level\",\"blueprint\":\"s2/bp\",\"pos\":\"1,2,3\"}]}");
            IWriteOpBus previous = WriteOpBus.Instance;
            WriteOpBus.Instance = new CapturingWriteOpBus();
            try
            {
                Assert.AreEqual(1, new JsonPipelineRunner().Run(path, new ExportLogger(quiet: true), new OutputFormatter(false)));
            }
            finally
            {
                WriteOpBus.Instance = previous;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void AudioPipeline_ForwardsPackageAndPlacementOptions()
        {
            var bus = new CapturingWriteOpBus();
            var previous = WriteOpBus.Instance;
            WriteOpBus.Instance = bus;
            string path = Path.Combine(Path.GetTempPath(), "audio_pipeline_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"steps\":[{\"op\":\"AUDIO-IMPORT\",\"file\":\"package\",\"as\":\"new-wave\",\"template\":\"donor\",\"bundle\":\"level-bundle\"}]}");
                Assert.AreEqual(0, new JsonPipelineRunner().Run(path, new ExportLogger(true), new OutputFormatter(false)));
                Assert.AreEqual("audio", bus.Op);
                Assert.AreEqual("import|package|--as|new-wave|--template|donor|--bundle|level-bundle", string.Join("|", bus.Args));
                File.WriteAllText(path, "{\"steps\":[{\"op\":\"audio-place\",\"ebx\":\"patch\",\"into\":\"layer\",\"pos\":\"1,2,3\",\"play_on_creation\":false}]}");
                Assert.AreEqual(0, new JsonPipelineRunner().Run(path, new ExportLogger(true), new OutputFormatter(false)));
                Assert.AreEqual("place|patch|--into|layer|--pos|1,2,3|--play-on-creation|false", string.Join("|", bus.Args));
            }
            finally { WriteOpBus.Instance = previous; File.Delete(path); }
        }

        [Test]
        public void AudioPipeline_RequiresExplicitFlattenAndKnownEncoding()
        {
            var bus = new CapturingWriteOpBus();
            var previous = WriteOpBus.Instance;
            WriteOpBus.Instance = bus;
            string path = Path.Combine(Path.GetTempPath(), "audio_pipeline_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"steps\":[{\"op\":\"import-sound\",\"file\":\"input.wav\",\"as\":\"wave\",\"encoding\":\"pcm\",\"flatten\":true}]}");
                Assert.AreEqual(0, new JsonPipelineRunner().Run(path, new ExportLogger(true), new OutputFormatter(false)));
                Assert.AreEqual("input.wav|--as|wave|--flatten|--pcm", string.Join("|", bus.Args));
                File.WriteAllText(path, "{\"steps\":[{\"op\":\"import-sound\",\"file\":\"input.wav\",\"as\":\"wave\",\"encoding\":\"bogus\"}]}");
                bus.Op = null;
                Assert.AreEqual(1, new JsonPipelineRunner().Run(path, new ExportLogger(true), new OutputFormatter(false)));
                Assert.IsNull(bus.Op);
            }
            finally { WriteOpBus.Instance = previous; File.Delete(path); }
        }

        [Test]
        public void GraphPipeline_ForwardsMigrationAndDestinationOptions()
        {
            var bus = new CapturingWriteOpBus();
            var previous = WriteOpBus.Instance;
            WriteOpBus.Instance = bus;
            string path = Path.Combine(Path.GetTempPath(), "graph_pipeline_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"steps\":[{\"op\":\"audio-graph-import\",\"file\":\"graph.json\",\"as\":\"new-patch\",\"bundle\":\"bundle\",\"graph_map\":\"migration.json\",\"out\":\"report.json\"}]}");
                Assert.AreEqual(0, new JsonPipelineRunner().Run(path, new ExportLogger(true), new OutputFormatter(false)));
                Assert.AreEqual("audio", bus.Op);
                Assert.AreEqual("graph-import|graph.json|--out|report.json|--as|new-patch|--bundle|bundle|--map|migration.json", string.Join("|", bus.Args));
                File.WriteAllText(path, "{\"steps\":[{\"op\":\"audio-graph-plan\",\"file\":\"graph.json\",\"as\":\"new-patch\"}]}");
                Assert.AreEqual(0, new JsonPipelineRunner().Run(path, new ExportLogger(true), new OutputFormatter(false)));
                Assert.AreEqual("graph-plan|graph.json|--as|new-patch", string.Join("|", bus.Args));
            }
            finally { WriteOpBus.Instance = previous; File.Delete(path); }
        }

        private sealed class CapturingWriteOpBus : IWriteOpBus
        {
            public string Op;
            public string[] Args;

            public int Execute(string op, string[] args, ExportLogger logger, OutputFormatter formatter)
            {
                Op = op;
                Args = args;
                return 0;
            }
        }
    }
}
