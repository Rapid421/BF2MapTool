using System;
using System.Collections.Generic;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class SubWorldScaffolderTests
    {
        // Minimal graph: an object with a list of child "refs" (each a PointerRef struct).
        // The remapper matches leaf struct by type.Name == "PointerRef", so name it exactly that.
        public struct FakeImport { public Guid FileGuid; public Guid ClassGuid; }
        public struct PointerRef { public int Type; public FakeImport External; }
        public class Node { public string Name { get; set; } public List<PointerRef> Refs { get; set; } = new List<PointerRef>(); }

        private static SubWorldScaffolder.Source Src(string path, Guid fg, Node n)
            => new SubWorldScaffolder.Source { Path = path, FileGuid = fg, Root = n };

        [Test]
        public void Scaffold_RenamesPaths()
        {
            Guid a = Guid.NewGuid();
            var sources = new List<SubWorldScaffolder.Source> { Src("lvl/supremacy/gameplay", a, new Node { Name = "g" }) };
            var result = SubWorldScaffolder.Plan(sources, "lvl/supremacy", "lvl/cryo");
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("lvl/cryo/gameplay", result[0].DestPath);
            Assert.AreEqual(true, result[0].NewFileGuid != a);
        }

        [Test]
        public void Scaffold_RemapsInternalRefs()
        {
            Guid ga = Guid.NewGuid(), gb = Guid.NewGuid();
            var nodeA = new Node { Name = "a" };
            nodeA.Refs.Add(new PointerRef { Type = 2, External = new FakeImport { FileGuid = gb } });
            var sources = new List<SubWorldScaffolder.Source>
            {
                Src("lvl/supremacy/a", ga, nodeA),
                Src("lvl/supremacy/b", gb, new Node { Name = "b" }),
            };
            var result = SubWorldScaffolder.Plan(sources, "lvl/supremacy", "lvl/cryo");
            // The ref in cloned A should now point at cloned B's new guid, not gb.
            Guid newB = result.Find(r => r.DestPath == "lvl/cryo/b").NewFileGuid;
            var clonedA = (Node)result.Find(r => r.DestPath == "lvl/cryo/a").NewRoot;
            Assert.AreEqual(newB, clonedA.Refs[0].External.FileGuid);
        }

        [Test]
        public void Scaffold_LeavesExternalRefsUnchanged()
        {
            Guid ga = Guid.NewGuid(), outside = Guid.NewGuid();
            var nodeA = new Node { Name = "a" };
            nodeA.Refs.Add(new PointerRef { Type = 2, External = new FakeImport { FileGuid = outside } });
            var sources = new List<SubWorldScaffolder.Source> { Src("lvl/supremacy/a", ga, nodeA) };
            var result = SubWorldScaffolder.Plan(sources, "lvl/supremacy", "lvl/cryo");
            var clonedA = (Node)result[0].NewRoot;
            Assert.AreEqual(outside, clonedA.Refs[0].External.FileGuid);
        }
    }
}
