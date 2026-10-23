using System;
using System.Reflection;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// Creates partition objects for path auto-creation, inside one EBX asset.
    ///
    /// A new sub-object needs two things beyond being constructed: an instance GUID, and a place in
    /// the partition. Without them it is unreachable from the root, and EBX serialization keeps only
    /// the reachable graph -- so the object is silently dropped on save and the reference that was
    /// supposed to reach it addresses a GUID that exists nowhere. That is not hypothetical: shape
    /// data created and left unreferenced vanished on save, and the layer tested clean only because
    /// it was empty again.
    ///
    /// Objects are EXPORTED, matching `ebx new-object`, `array-add --type` and
    /// `set-ref --target-object new:`. An exported instance can be addressed by GUID, which is what
    /// a connection endpoint or a cross-partition reference needs; the shipped inclusion-settings
    /// objects are not exported, but nothing reaches them from outside their own partition either.
    /// </summary>
    internal sealed class PartitionObjectFactory : IPartitionObjectFactory
    {
        private readonly EbxAsset _asset;
        private readonly string _op;

        public PartitionObjectFactory(EbxAsset asset, string op)
        {
            _asset = asset;
            _op = op;
        }

        public object Create(Type type, out string error)
        {
            error = null;
            if (type == null) { error = "no type to create"; return null; }

            // Through TypeLibrary rather than Activator: the generated types are constructed with
            // their lists and defaults in place, and a bare Activator instance is missing them.
            object obj = EbxWriteCommand.CreateObjectByType(type.Name);
            if (obj == null)
            {
                error = _op + ": type not found in the type library: " + type.Name;
                return null;
            }

            try
            {
                MethodInfo setGuid = obj.GetType().GetMethod("SetInstanceGuid",
                    BindingFlags.Public | BindingFlags.Instance);
                if (setGuid != null)
                    setGuid.Invoke(obj, new object[] {
                        new AssetClassGuid(Guid.NewGuid(), LevelCommand.NextInternalId(_asset)) });
                _asset.AddObject(obj, false);
                InProcessObjects.Mark(obj);
            }
            catch (Exception ex)
            {
                error = _op + ": could not add a " + type.Name + " to the partition: " +
                        (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return null;
            }

            return obj;
        }
    }
}
