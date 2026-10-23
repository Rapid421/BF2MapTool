using System;
using System.Collections.Generic;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// The `level closure --audit` rule for one external reference, usable by the pruning commands: a PointerRef whose
    /// partition exists but exports no instance with the referenced class guid resolves to nothing in game (the import
    /// misses: game+0x1522B97). `level clear --remove` / `level remove` leave such references behind in the connections
    /// of the parent sub-worlds and in the network registries; `level prune --dangling` and `port prune-refs --dangling`
    /// take exactly those out.
    /// </summary>
    internal sealed class DanglingRefs
    {
        readonly Dictionary<Guid, HashSet<Guid>> exported = new Dictionary<Guid, HashSet<Guid>>();

        HashSet<Guid> ExportsOf(EbxAssetEntry e)
        {
            HashSet<Guid> set;
            if (exported.TryGetValue(e.Guid, out set)) return set;
            set = new HashSet<Guid>();
            try
            {
                foreach (object obj in App.AssetManager.GetEbx(e).Objects)
                {
                    dynamic ig = ((dynamic)obj).GetInstanceGuid();
                    if ((bool)ig.IsExported) set.Add((Guid)ig.ExportedGuid);
                }
            }
            catch { set = null; }
            exported[e.Guid] = set;
            return set;
        }

        static object Member(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            return t.GetField(name)?.GetValue(o) ?? t.GetProperty(name)?.GetValue(o);
        }

        /// <summary>True when <paramref name="pointerRef"/> is External, its partition exists and was read, and the
        /// partition exports no instance with the referenced class guid. Unreadable partitions are never "dangling".</summary>
        public bool IsDangling(object pointerRef, out string why)
        {
            why = null;
            if (pointerRef == null || pointerRef.GetType().Name != "PointerRef") return false;
            if (Member(pointerRef, "Type")?.ToString() != "External") return false;
            object ext = Member(pointerRef, "External");
            if (!(Member(ext, "FileGuid") is Guid fg) || fg == Guid.Empty) return false;
            if (!(Member(ext, "ClassGuid") is Guid cg) || cg == Guid.Empty) return false;
            EbxAssetEntry t = App.AssetManager.GetEbxEntry(fg);
            if (t == null) return false;
            HashSet<Guid> set = ExportsOf(t);
            if (set == null || set.Contains(cg)) return false;
            why = t.Name + " has no exported instance " + cg;
            return true;
        }
    }
}
