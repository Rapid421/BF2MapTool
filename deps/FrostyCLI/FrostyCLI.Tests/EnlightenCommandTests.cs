using System;
using System.Collections.Generic;
using FrostyCLI.Commands;
using FrostyCLI.Core;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests
{
    public class EnlightenCommandTests
    {
        [Test]
        public void MissingSubcommandFails()
        {
            Assert.AreEqual(1, new EnlightenCommand().Execute(new string[0],
                new ExportLogger(quiet: true), new OutputFormatter(false)));
        }

        [Test]
        public void UnknownSubcommandFails()
        {
            Assert.AreEqual(1, new EnlightenCommand().Execute(new[] { "bake" },
                new ExportLogger(quiet: true), new OutputFormatter(false)));
        }

        [Test]
        public void CollectsStaticAndDynamicReferences()
        {
            JObject root = JObject.Parse(@"{
              '__type':'LevelData',
              'Objects':[
                {'__type':'DynamicEnlightenEntityData','Enable':true,
                 'EnlightenData':{'__type':'PointerRef','__path':'levels/test/end'}},
                {'__type':'StaticEnlightenEntityData','FluxAutoBake':true,
                 'EnlightenData':{'__type':'PointerRef','__path':'levels/test/fls'},
                 'DynamicEnlightenData':{'__type':'PointerRef','__path':'levels/test/end'}}
              ]}");
            var entities = new JArray();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            EnlightenCommand.CollectEnlightenReferences(root, entities, paths);
            Assert.AreEqual(2, entities.Count);
            Assert.AreEqual(2, paths.Count);
            Assert.IsTrue(paths.Contains("levels/test/end"));
            Assert.IsTrue(paths.Contains("levels/test/fls"));
        }
    }
}
