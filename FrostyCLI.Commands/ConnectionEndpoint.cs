using System;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// One side of a connection: either an object in the SAME partition (an internal PointerRef) or
    /// an object in another asset (an external PointerRef -- file guid plus exported instance guid).
    ///
    /// Both forms occur in shipped data, often on the same connection. Kashyyyk's mode graph lives
    /// on the SubWorldData of Levels/MP/Kashyyyk_01/GameModes and wires entities in
    /// Levels/MP/Kashyyyk_01/GameModes_Flow, so a single record can carry an internal Source and an
    /// external Target -- or two external endpoints.
    /// </summary>
    internal struct ConnectionEndpoint
    {
        /// <summary>The endpoint object when this is an internal ref; null for an external one.</summary>
        public object InternalObject;

        /// <summary>The containing asset's file guid (external only).</summary>
        public Guid FileGuid;

        /// <summary>The endpoint object's EXPORTED instance guid (external only).</summary>
        public Guid ClassGuid;

        public bool IsExternal { get { return InternalObject == null; } }

        public static ConnectionEndpoint Internal(object obj)
        {
            return new ConnectionEndpoint { InternalObject = obj };
        }

        public static ConnectionEndpoint External(Guid fileGuid, Guid classGuid)
        {
            return new ConnectionEndpoint { FileGuid = fileGuid, ClassGuid = classGuid };
        }
    }
}
