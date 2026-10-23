using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Whether a merge-style custom action handler can carry an EBX edit, decided before the mod is written.
    ///
    /// Swbf2MergerPlugin's MetaDataActionHandler ships a UIMetaDataAsset as the entries of its root <c>Assets</c> list that
    /// the shipped asset lacks, and nothing else (MetaDataResource diffs rootObject.Assets only; Modify appends them to the
    /// shipped asset at apply time). `level create --menu-name` edits objects INSIDE UI/Metadata/OptionsEnumUIMetaData (the
    /// Custom Arcade map pickers and their labels), so through the handler every Hoth 2015 build since T11 shipped that
    /// asset as a 132-byte empty delta and the level never appeared in the menus. A build that did not load the plugin
    /// (HOTH_T1, map-playable MP2) wrote the full EBX and the entry showed.
    /// </summary>
    public static class MergeHandlerFidelity
    {
        public const string MetaDataHandler = "Swbf2MergerPlugin.Handlers.MetaDataActionHandler";

        /// <summary>The entries MetaDataActionHandler ships: those of the edited root Assets list the shipped list does not
        /// contain (value equality, as the handler's own List.Contains), nulls skipped, in the edited order.</summary>
        public static List<object> ShippedAdditions(IList shipped, IList edited)
        {
            var added = new List<object>();
            if (edited == null) return added;
            foreach (object item in edited)
                if (item != null && (shipped == null || !shipped.Contains(item))) added.Add(item);
            return added;
        }

        /// <summary>The handler reproduces the edit exactly when the shipped asset with <see cref="ShippedAdditions"/>
        /// appended serialises to the same bytes as the edited asset. Anything else (a field changed inside an object, an
        /// entry removed or reordered) is lost through the handler, so the asset must go out as a plain EBX. A false
        /// negative only costs mergeability with other mods; a false positive loses the edit, so equality is strict.</summary>
        public static bool Reproduces(byte[] replay, byte[] edited)
        {
            return replay != null && edited != null && replay.Length == edited.Length && replay.SequenceEqual(edited);
        }
    }
}
