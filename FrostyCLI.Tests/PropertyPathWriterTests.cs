using System;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class PropertyPathWriterTests
    {
        private class TestObj
        {
            public string   Name     { get; set; }
            public int      Count    { get; set; }
            public TestVec3 Position { get; set; }
        }

        private struct TestVec3
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
        }

        private class TestWithList
        {
            public System.Collections.Generic.List<TestVec3> Points { get; set; }
                = new System.Collections.Generic.List<TestVec3>();
        }

        private struct Inner  { public float V { get; set; } }
        private struct Outer  { public Inner Sub { get; set; } }
        private class TestNestedInList
        {
            public System.Collections.Generic.List<Outer> Items { get; set; }
                = new System.Collections.Generic.List<Outer>();
        }

        [Test]
        public void SetValue_SimpleStringProperty_Works()
        {
            var o = new TestObj { Name = "old" };
            PropertyPathWriter.SetValue(o, "Name", "hello");
            Assert.AreEqual("hello", o.Name);
        }

        [Test]
        public void SetValue_SimpleIntProperty_Works()
        {
            var o = new TestObj();
            PropertyPathWriter.SetValue(o, "Count", "42");
            Assert.AreEqual(42, o.Count);
        }

        [Test]
        public void SetValue_NestedStructProperty_WriteBack()
        {
            var o = new TestObj { Position = new TestVec3 { X = 0f, Y = 0f, Z = 0f } };
            PropertyPathWriter.SetValue(o, "Position.X", "1.5");
            Assert.IsTrue(Math.Abs(o.Position.X - 1.5f) < 1e-5f);
        }

        [Test]
        public void SetValue_ArrayElement_IntValue_Works()
        {
            var w = new TestWithList();
            w.Points.Add(new TestVec3 { X = 0f, Y = 0f, Z = 0f });
            PropertyPathWriter.SetValue(w, "Points[0].Y", "2.5");
            Assert.IsTrue(Math.Abs(w.Points[0].Y - 2.5f) < 1e-5f);
        }

        [Test]
        public void SetValue_MissingProperty_Throws()
        {
            var o = new TestObj();
            try
            {
                PropertyPathWriter.SetValue(o, "NonExistent", "x");
                Assert.IsTrue(false, "expected throw");
            }
            catch (InvalidOperationException ex)
            {
                Assert.IsTrue(ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        [Test]
        public void SetValue_ArrayIndexOutOfRange_Throws()
        {
            var w = new TestWithList();
            try
            {
                PropertyPathWriter.SetValue(w, "Points[0].X", "1.0");
                Assert.IsTrue(false, "expected throw");
            }
            catch (InvalidOperationException ex)
            {
                Assert.IsTrue(ex.Message.IndexOf("out of range", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        [Test]
        public void SetValue_TypeCoercionFailure_Throws()
        {
            var o = new TestObj();
            Assert.Throws<FormatException>(
                () => PropertyPathWriter.SetValue(o, "Count", "not_a_number"));
        }

        [Test]
        public void SetValue_ListStructNestedStruct_WriteBack()
        {
            var obj = new TestNestedInList();
            obj.Items.Add(new Outer { Sub = new Inner { V = 0f } });
            PropertyPathWriter.SetValue(obj, "Items[0].Sub.V", "9.9");
            Assert.IsTrue(Math.Abs(obj.Items[0].Sub.V - 9.9f) < 1e-4f);
        }

        // The shape of a blueprint's event wiring with value-type endpoints: a list of struct
        // connections, each holding a struct EventSpec. Both struct levels have to be written back.
        private struct SpecStruct { public int Id { get; set; } }
        private struct ConnStruct { public SpecStruct SourceEvent { get; set; } public SpecStruct TargetEvent { get; set; } }
        private class StructBlueprint
        {
            public System.Collections.Generic.List<ConnStruct> EventConnections { get; set; }
                = new System.Collections.Generic.List<ConnStruct>();
        }

        [Test]
        public void SetValue_StructInListElement_EventConnectionPath_WritesBothLevelsBack()
        {
            var bp = new StructBlueprint();
            for (int i = 0; i < 14; i++)
                bp.EventConnections.Add(new ConnStruct { SourceEvent = new SpecStruct { Id = i }, TargetEvent = new SpecStruct { Id = 171404716 } });
            PropertyPathWriter.SetValue(bp, "EventConnections[13].TargetEvent.Id", "-1541066415");
            Assert.AreEqual(-1541066415, bp.EventConnections[13].TargetEvent.Id);
            Assert.AreEqual(13, bp.EventConnections[13].SourceEvent.Id);
            Assert.AreEqual(171404716, bp.EventConnections[12].TargetEvent.Id);
        }

        // Stand-in for FrostySdk's IsTransientAttribute (matched by name).
        [AttributeUsage(AttributeTargets.Property)]
        private sealed class IsTransientAttribute : Attribute { }

        // Mirrors the SDK's generated EventSpec: Name is transient, its getter caches a string rendered
        // from Id on first read, and its setter re-derives Id. The project reader assigns Name after Id,
        // so a stale cached Name turns an edited Id back on reload.
        private class FakeEventSpec
        {
            internal static readonly System.Collections.Generic.Dictionary<int, string> Known =
                new System.Collections.Generic.Dictionary<int, string> { { 42, "SetTrue" } };

            protected int _Id;
            protected string _Name;
            public int Id { get { return _Id; } set { _Id = value; } }

            [IsTransient]
            public string Name
            {
                get
                {
                    if (_Name == null) _Name = Known.ContainsKey(_Id) ? Known[_Id] : "0x" + _Id.ToString("x8");
                    return _Name;
                }
                set
                {
                    _Name = value;
                    if (value.StartsWith("0x")) _Id = int.Parse(value.Substring(2), System.Globalization.NumberStyles.HexNumber);
                    else foreach (var kv in Known) if (kv.Value == value) _Id = kv.Key;
                }
            }

            // What a project save + load does to the pair: Id is read first, then the transient Name.
            public FakeEventSpec Reloaded()
            {
                var r = new FakeEventSpec { Id = Id };
                r.Name = Name;
                return r;
            }
        }
        private class FakeConnection { public FakeEventSpec TargetEvent { get; set; } = new FakeEventSpec(); }
        private class FakeBlueprint
        {
            public System.Collections.Generic.List<FakeConnection> EventConnections { get; set; }
                = new System.Collections.Generic.List<FakeConnection>();
        }

        [Test]
        public void SetValue_EventSpecId_AfterNameWasRead_SurvivesReload()
        {
            var bp = new FakeBlueprint();
            bp.EventConnections.Add(new FakeConnection());
            bp.EventConnections[0].TargetEvent.Id = 171404716;
            Assert.AreEqual("0x0a376dac", bp.EventConnections[0].TargetEvent.Name);   // a dump caches Name

            PropertyPathWriter.SetValue(bp, "EventConnections[0].TargetEvent.Id", "-1541066415");

            FakeEventSpec spec = bp.EventConnections[0].TargetEvent;
            Assert.AreEqual(-1541066415, spec.Id);
            Assert.AreEqual("0xa4253151", spec.Name);
            Assert.AreEqual(-1541066415, spec.Reloaded().Id);
        }

        [Test]
        public void SetValue_EventSpecId_KnownEvent_KeepsReadableName()
        {
            var bp = new FakeBlueprint();
            bp.EventConnections.Add(new FakeConnection());
            bp.EventConnections[0].TargetEvent.Id = 7;
            string cached = bp.EventConnections[0].TargetEvent.Name;
            Assert.AreEqual("0x00000007", cached);

            PropertyPathWriter.SetValue(bp, "EventConnections[0].TargetEvent.Id", "42");

            Assert.AreEqual("SetTrue", bp.EventConnections[0].TargetEvent.Name);
            Assert.AreEqual(42, bp.EventConnections[0].TargetEvent.Reloaded().Id);
        }

        // Every SDK DataContainer marks Frosty's bookkeeping members transient; that alone must not make a type
        // "transient" for the persist check, or every set round-trips the whole partition.
        private class OnlyBookkeepingTransients
        {
            [IsTransient] public string __Id { get; set; }
            [IsTransient] public Guid __InstanceGuid { get; set; }
            public float Velocity { get; set; }
        }

        [Test]
        public void HasTransientMembers_IgnoresBookkeepingMembers()
        {
            Assert.IsFalse(PropertyPathWriter.HasTransientMembers(typeof(OnlyBookkeepingTransients)));
        }

        [Test]
        public void HasTransientMembers_CountsRealTransientMember()
        {
            Assert.IsTrue(PropertyPathWriter.HasTransientMembers(typeof(FakeEventSpec)));
        }

        // An object with a transient member that does not feed the written one (AffectorApplyEntityData,
        // SuperSphereEvaluatorData and AnimatableAttachData all declare transient members like this).
        private class FakeWithTransientExtra
        {
            public float Duration { get; set; }
            [IsTransient] public string Cache { get; set; }
            public int Id { get; set; }
        }

        [Test]
        public void CanBeReappliedOnLoad_OnlyTheEventSpecShapeAndTransientMembers()
        {
            Assert.IsTrue(PropertyPathWriter.CanBeReappliedOnLoad(typeof(FakeEventSpec), "Id"));      // Name re-derives it
            Assert.IsTrue(PropertyPathWriter.CanBeReappliedOnLoad(typeof(FakeEventSpec), "Name"));    // transient itself
            Assert.IsFalse(PropertyPathWriter.CanBeReappliedOnLoad(typeof(FakeWithTransientExtra), "Duration"));
            Assert.IsFalse(PropertyPathWriter.CanBeReappliedOnLoad(typeof(FakeWithTransientExtra), "Id"));   // no transient Name
            Assert.IsTrue(PropertyPathWriter.CanBeReappliedOnLoad(typeof(FakeWithTransientExtra), "Cache"));
            Assert.IsFalse(PropertyPathWriter.CanBeReappliedOnLoad(typeof(FakeWithTransientExtra), "Missing"));
        }

        private class IgnoresWrites { public int Frozen { get { return 7; } set { } } }

        [Test]
        public void SetValue_WriteThatDoesNotTake_Throws()
        {
            try
            {
                PropertyPathWriter.SetValue(new IgnoresWrites(), "Frozen", "3");
                Assert.IsTrue(false, "expected throw");
            }
            catch (InvalidOperationException ex)
            {
                Assert.IsTrue(ex.Message.IndexOf("did not take", StringComparison.OrdinalIgnoreCase) >= 0, ex.Message);
            }
        }
    }
}
