using System.Collections;
using System.Runtime.CompilerServices;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Partition objects this process created (`ebx set-ref --target-object new:`, `level add-entity`,
    /// `ebx array-add --type`, ...). Until a save and a reload they exist only in memory, and the in-memory
    /// project round trip that `ebx set` checks a write with is not faithful for a partition holding them: it
    /// can drop them or hand one's values to a sibling, so a mismatch it reports there proves nothing. Weak
    /// keys, so an object a later project-load replaced goes with its asset.
    /// </summary>
    public static class InProcessObjects
    {
        private static readonly ConditionalWeakTable<object, object> created = new ConditionalWeakTable<object, object>();
        private static readonly object Marker = new object();

        public static void Mark(object obj)
        {
            if (obj != null && !created.TryGetValue(obj, out _)) created.Add(obj, Marker);
        }

        public static bool IsCreated(object obj) => obj != null && created.TryGetValue(obj, out _);

        public static bool AnyCreated(IEnumerable objects)
        {
            if (objects == null) return false;
            foreach (object o in objects) if (IsCreated(o)) return true;
            return false;
        }
    }
}
