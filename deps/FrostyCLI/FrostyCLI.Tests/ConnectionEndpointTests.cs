using System;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    // Cross-partition connection endpoints.
    //
    // Essentially all real logic is cross-asset. Kashyyyk's mode graph lives on the SubWorldData of
    // Levels/MP/Kashyyyk_01/GameModes but wires entities that live in a DIFFERENT partition,
    // Levels/MP/Kashyyyk_01/GameModes_Flow -- shipped data holds those endpoints as EXTERNAL
    // PointerRefs (file guid + exported instance guid), and both sides of one connection can be
    // external at once.
    //
    // These drive the endpoint writer over a fake partition, so they run without the game. The fakes
    // mirror the real SDK shapes (FrostySdk.Ebx.PointerRef is an immutable struct with
    // PointerRef(object) / PointerRef(EbxImportReference) constructors; EbxImportReference exposes
    // FileGuid/ClassGuid as public FIELDS) because the production code matches by type NAME.
    public class ConnectionEndpointTests
    {
        public struct FakeImport { public Guid FileGuid; public Guid ClassGuid; }

        public struct PointerRef
        {
            public int Type;
            public object Internal;
            public FakeImport External;
            public PointerRef(object o) { Type = 1; Internal = o; External = default(FakeImport); }
            public PointerRef(FakeImport i) { Type = 2; Internal = null; External = i; }
        }

        // An EventConnection names its endpoints with an EventSpec (Id + Name), not a bare id.
        public struct EventSpec { public int Id { get; set; } public string Name { get; set; } }

        public class FakeEventConnection
        {
            public PointerRef Source { get; set; }
            public PointerRef Target { get; set; }
            public EventSpec SourceEvent { get; set; }
            public EventSpec TargetEvent { get; set; }
        }

        // Stands in for a partition object. A non-exported one returns Guid.Empty, which is exactly
        // the case an external ref cannot address.
        public class FakeEntity
        {
            private readonly Guid _guid;
            public FakeEntity(Guid g) { _guid = g; }
            public Guid GetInstanceGuid() { return _guid; }
        }

        private static readonly Guid FileGuid  = new Guid("ac12bb01-1111-2222-3333-444455556666");
        private static readonly Guid ClassGuid = new Guid("34be33c7-b369-4bd6-bb09-be587d537252");

        // The shape shipped data carries: Source is an external PointerRef naming the OTHER
        // partition's file guid and the endpoint object's exported instance guid. Writing an
        // internal ref here instead produces a connection that authors cleanly and is inert.
        [Test]
        public void SetEndpoint_External_WritesImportReference()
        {
            var conn = new FakeEventConnection();
            var ep = ConnectionEndpoint.External(FileGuid, ClassGuid);

            ConnectionCommand.SetEndpoint(conn, conn.GetType(), "Source", ep, "StreamIn");

            Assert.AreEqual(2, conn.Source.Type);
            Assert.AreEqual(FileGuid, conn.Source.External.FileGuid);
            Assert.AreEqual(ClassGuid, conn.Source.External.ClassGuid);
        }

        // The port id has to land the same way it does for an internal endpoint -- an external ref
        // with a zero event id is a connection to nothing.
        [Test]
        public void SetEndpoint_External_StillWritesTheEventId()
        {
            var conn = new FakeEventConnection();

            ConnectionCommand.SetEndpoint(conn, conn.GetType(), "Target",
                ConnectionEndpoint.External(FileGuid, ClassGuid), "StreamIn");

            Assert.AreEqual(unchecked((int)0xD27BC17E), conn.TargetEvent.Id);
            Assert.AreEqual("StreamIn", conn.TargetEvent.Name);
        }

        // Endpoints inside one partition must keep working exactly as before.
        [Test]
        public void SetEndpoint_Internal_StillWritesInternalRef()
        {
            var conn = new FakeEventConnection();
            var entity = new FakeEntity(ClassGuid);

            ConnectionCommand.SetEndpoint(conn, conn.GetType(), "Target",
                ConnectionEndpoint.Internal(entity), "StreamIn");

            Assert.AreEqual(1, conn.Target.Type);
            Assert.IsTrue(ReferenceEquals(entity, conn.Target.Internal),
                "the internal ref should point at the endpoint object itself");
        }

        // An external ref addresses its target by exported instance guid. A non-exported object has
        // none, so the ref resolves to nothing at runtime -- and nothing errors. Refuse it instead.
        [Test]
        public void TryExternalEndpoint_NonExportedObject_IsRefused()
        {
            ConnectionEndpoint ep; string error;
            bool ok = ConnectionCommand.TryExternalEndpoint(new FakeEntity(Guid.Empty), FileGuid,
                                                            out ep, out error);
            Assert.IsFalse(ok, "a non-exported endpoint must be refused");
            Assert.IsTrue(error != null && error.IndexOf("export", StringComparison.OrdinalIgnoreCase) >= 0,
                "the error should say the object is not exported; got: " + error);
        }

        [Test]
        public void TryExternalEndpoint_ExportedObject_CarriesBothGuids()
        {
            ConnectionEndpoint ep; string error;
            bool ok = ConnectionCommand.TryExternalEndpoint(new FakeEntity(ClassGuid), FileGuid,
                                                            out ep, out error);
            Assert.IsTrue(ok, "error: " + error);
            Assert.IsTrue(ep.IsExternal, "should be an external endpoint");
            Assert.AreEqual(FileGuid, ep.FileGuid);
            Assert.AreEqual(ClassGuid, ep.ClassGuid);
        }
    }
}
