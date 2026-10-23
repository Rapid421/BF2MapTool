using System;
using System.Collections;
using System.Collections.Generic;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Chooses which exported object inside a target asset a reference should address.
    ///
    /// The asset ROOT is the right answer almost always, and it is what `set-ref --target` used
    /// unconditionally. It is wrong when the member declares what it points at and the root is not
    /// that thing. Shipped SubWorldInclusionSetting.Criterion points at the
    /// WorldPartInclusionCriterion INSIDE Gameplay/GameModes/GameModeInclusionSettings, whose root is
    /// a WorldPartInclusion container holding it -- a WorldPartInclusion is an Asset, not a
    /// SubWorldInclusionCriterion. Writing the root there gives a reference that serializes cleanly,
    /// loads, and addresses a container where the engine expects a criterion. Nothing errors; the
    /// gate simply never applies.
    ///
    /// Matching is by ASSIGNABILITY, not by name: shipped data carries WorldPartInclusionCriterion
    /// where the member declares SubWorldInclusionCriterion, and a name comparison would reject the
    /// very object the game uses.
    /// </summary>
    public static class RefTargetPicker
    {
        /// <summary>
        /// Picks the instance GUID to write. Returns false, with an error naming the alternatives,
        /// when the choice is ambiguous or impossible -- guessing there would write a reference to
        /// the wrong object and never report it.
        /// </summary>
        /// <param name="note">Set when a non-root object was chosen, so the caller can say so.</param>
        public static bool TryPick(Type declaredBaseType, object root, IEnumerable objects,
                                   out Guid classGuid, out string error, out string note)
        {
            classGuid = Guid.Empty;
            error = null;
            note = null;

            Guid rootGuid = PointerRefUtil.InstanceGuid(root);

            // Nothing declared, or the root is already the declared thing: the old behaviour, which
            // is correct for the overwhelming majority of assets.
            if (declaredBaseType == null || root == null || declaredBaseType.IsInstanceOfType(root))
            {
                if (root == null) { error = "the target asset has no root object"; return false; }
                if (rootGuid == Guid.Empty)
                { error = "the target asset's root has no exported instance GUID; pass --class-guid"; return false; }
                classGuid = rootGuid;
                return true;
            }

            var candidates = new List<object>();
            if (objects != null)
                foreach (object o in objects)
                {
                    if (o == null || ReferenceEquals(o, root)) continue;
                    if (declaredBaseType.IsInstanceOfType(o)) candidates.Add(o);
                }

            var addressable = new List<object>();
            foreach (object c in candidates)
                if (PointerRefUtil.InstanceGuid(c) != Guid.Empty) addressable.Add(c);

            // Nothing better to offer. The write is probably wrong -- a reference declared to point
            // at one thing, aimed at an asset that holds no such thing -- but there is no candidate
            // to prefer, and refusing would turn a command that has always accepted this into one
            // that fails. Warn and leave the root; refusing is reserved for a genuine coin flip.
            if (addressable.Count == 0)
            {
                if (rootGuid == Guid.Empty)
                { error = "the target asset's root has no exported instance GUID; pass --class-guid"; return false; }
                note = "the reference expects a " + declaredBaseType.Name + ", but the target asset's root " +
                       "is a " + root.GetType().Name + " and it holds no exported " + declaredBaseType.Name +
                       (candidates.Count > 0 ? " that is exported" : "") +
                       "; pointing at the root anyway -- name the object with --class-guid if that is wrong";
                classGuid = rootGuid;
                return true;
            }

            if (addressable.Count > 1)
            {
                var guids = new List<string>();
                foreach (object c in addressable)
                    guids.Add(c.GetType().Name + " " + PointerRefUtil.InstanceGuid(c));
                error = "the target asset holds " + addressable.Count + " objects the reference could " +
                        "address (" + string.Join(", ", guids.ToArray()) + "); choose one with --class-guid";
                return false;
            }

            classGuid = PointerRefUtil.InstanceGuid(addressable[0]);
            note = "the target asset's root is a " + root.GetType().Name + ", not a " +
                   declaredBaseType.Name + "; pointing at its " + addressable[0].GetType().Name +
                   " " + classGuid + " instead";
            return true;
        }
    }
}
