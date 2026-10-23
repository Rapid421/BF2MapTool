using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class EbxDumperTests
    {
        private class FakeEntity
        {
            public string Name    { get; set; }
            public int    Count   { get; set; }
            public bool   Active  { get; set; }
            public float  Value   { get; set; }
            public FakeChild Child { get; set; }
            public List<string> Tags { get; set; }
        }
        private class FakeChild { public double Score { get; set; } }

        [Test]
        public void DumpRootObject_Primitives_SerializedCorrectly()
        {
            var obj = new FakeEntity { Name = "foo", Count = 42, Active = true, Value = 1.5f };
            JObject jo = EbxDumper.DumpRootObject(obj, resolve: false);

            Assert.AreEqual("FakeEntity", (string)jo["__type"]);
            Assert.AreEqual("foo",  (string)jo["Name"]);
            Assert.AreEqual(42,     (int)   jo["Count"]);
            Assert.AreEqual(true,   (bool)  jo["Active"]);
        }

        [Test]
        public void DumpRootObject_NestedObject_HasTypeField()
        {
            var obj = new FakeEntity { Child = new FakeChild { Score = 99.0 } };
            JObject jo = EbxDumper.DumpRootObject(obj, resolve: false);

            JObject child = (JObject)jo["Child"];
            Assert.IsNotNull(child);
            Assert.AreEqual("FakeChild", (string)child["__type"]);
            Assert.AreEqual(99.0, (double)child["Score"]);
        }

        [Test]
        public void DumpRootObject_NullProperty_SerializedAsNull()
        {
            var obj = new FakeEntity { Child = null };
            JObject jo = EbxDumper.DumpRootObject(obj, resolve: false);
            Assert.AreEqual(JTokenType.Null, jo["Child"].Type);
        }

        [Test]
        public void DumpRootObject_List_SerializedAsArray()
        {
            var obj = new FakeEntity { Tags = new List<string> { "hero", "rebel" } };
            JObject jo = EbxDumper.DumpRootObject(obj, resolve: false);

            JArray arr = (JArray)jo["Tags"];
            Assert.IsNotNull(arr);
            Assert.AreEqual(2, arr.Count);
            Assert.AreEqual("hero",  (string)arr[0]);
            Assert.AreEqual("rebel", (string)arr[1]);
        }

        [Test]
        public void DumpRootObject_GuidProperty_HyphenatedLowercase()
        {
            var obj = new GuidHolder { Id = new Guid("82f196f5-ba34-4732-8b72-fb547f581497") };
            JObject jo = EbxDumper.DumpRootObject(obj, resolve: false);
            Assert.AreEqual("82f196f5-ba34-4732-8b72-fb547f581497", (string)jo["Id"]);
        }

        [Test]
        public void DumpRootObject_Enum_SerializedAsName()
        {
            var obj = new EnumHolder { Mode = TestMode.Active };
            JObject jo = EbxDumper.DumpRootObject(obj, resolve: false);
            Assert.AreEqual("Active", (string)jo["Mode"]);
        }

        [Test]
        public void DumpRootObject_CircularRef_NoStackOverflow()
        {
            var obj = new CircularNode { Name = "root" };
            obj.Self = obj;
            JObject jo = EbxDumper.DumpRootObject(obj, resolve: false);
            Assert.IsNotNull(jo);
            // The Self slot should contain a circular marker, not infinite recursion
            JObject selfSlot = jo["Self"] as JObject;
            Assert.IsNotNull(selfSlot);
            Assert.AreEqual(true, (bool)selfSlot["__circular"]);
            Assert.AreEqual("CircularNode", (string)selfSlot["__type"]);
        }

        private class GuidHolder { public Guid Id { get; set; } }
        private enum TestMode { Inactive, Active }
        private class EnumHolder { public TestMode Mode { get; set; } }
        private class CircularNode
        {
            public string Name { get; set; }
            public CircularNode Self { get; set; }
        }
    }
}
