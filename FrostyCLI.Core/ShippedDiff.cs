using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace FrostyCLI.Core
{
    /// <summary>One field of the object under test, against the shipped population of its type.</summary>
    public sealed class FieldFinding
    {
        /// <summary>Dotted path, e.g. "BundleHeap.AllowGrow".</summary>
        public string Field;

        /// <summary>Our rendered value, or null when we do not carry this field.</summary>
        public string Ours;

        /// <summary>How many shipped instances carry this field.</summary>
        public int ShippedCount;

        /// <summary>Shipped values and their counts, most common first.</summary>
        public List<KeyValuePair<string, int>> Shipped;

        /// <summary>One of the <see cref="ShippedDiff"/> verdicts.</summary>
        public string Verdict;

        /// <summary>Why, when the verdict alone does not say it.</summary>
        public string Note;
    }

    /// <summary>
    /// Compares one object against shipped instances of its own type, field by field.
    ///
    /// Every real defect in the battle-royale work was found this way by hand: BundleHeap.AllowGrow
    /// false where all nine shipped references are true; a Flags value carried over from a
    /// duplication source instead of derived from the object's own GUID; a BundleName inherited from
    /// whatever was cloned; layer refs still pointing at the originals. None of those error -- the
    /// data loads and the feature does nothing -- and every one of them is decidable from shipped
    /// data without launching the game.
    ///
    /// Two different kinds of fault, so two kinds of check:
    ///
    ///   1. Distribution. A field where the shipped population agrees and we do not.
    ///   2. Derivation. A field whose shipped values are all DIFFERENT is not therefore free. Flags
    ///      on a placed reference object is per-instance unique and still constrained: across the
    ///      shipped population its low 25 bits equal the object's own exported GUID. A duplicated
    ///      object that keeps its source's Flags shares a sub-level identity with it. A pure
    ///      distribution check cannot see that, because "unique" and "wrong" look identical.
    ///
    /// The derivation is established FROM the population, never assumed. It holds for some types and
    /// not others -- measured over the shipped corpus it is 100% on SubWorldReferenceObjectData
    /// (1318/1318 exported instances), SpatialPrefabReferenceObjectData and StaticModelEntityData,
    /// and 0% on ObjectBlueprint, LayerData and LogicPrefabBlueprint. Asserting it where it does not
    /// hold would flag every correct object.
    ///
    /// Reflection-only, so it is unit-testable over plain objects and keeps Core free of any
    /// compile-time dependency on the generated EBX types.
    /// </summary>
    public static class ShippedDiff
    {
        public const string UnanimousDisagreement = "unanimous-disagreement";
        public const string DerivedViolation      = "derived-violation";
        public const string Disagreement          = "disagreement";
        public const string Rare                  = "rare";
        public const string PerInstance           = "per-instance";
        public const string Matches               = "matches";

        // Ranking, most serious first. A unanimous population disagreeing with us is the strongest
        // signal available without running the game, so it leads.
        private static readonly string[] Order =
        {
            UnanimousDisagreement, DerivedViolation, Disagreement, Rare, PerInstance, Matches
        };

        // Identity members. __InstanceGuid and __Id differ per instance by construction, so they
        // carry no information here and would only push real findings down the list.
        private static readonly HashSet<string> Skipped =
            new HashSet<string>(StringComparer.Ordinal) { "__InstanceGuid", "__Id" };

        /// <summary>
        /// A field is treated as per-instance only with enough instances to tell "everything differs"
        /// apart from "we happened to sample two".
        /// </summary>
        private const int MinInstancesForPerInstance = 3;

        /// <summary>And a derivation needs at least this many agreeing instances to be believed.</summary>
        private const int MinInstancesForDerivation = 3;

        /// <summary>Below this share of the population, matching is worth reporting as unusual.</summary>
        private const double RareShare = 0.10;

        private sealed class Cell
        {
            public string Text;
            public object Raw;
        }

        public static List<FieldFinding> Compare(object ours, IList<object> shipped, int maxDepth)
        {
            var findings = new List<FieldFinding>();
            if (ours == null || shipped == null || shipped.Count == 0) return findings;

            Dictionary<string, Cell> mine = Flatten(ours, maxDepth);

            var population = new List<Dictionary<string, Cell>>(shipped.Count);
            var guids = new List<Guid>(shipped.Count);
            foreach (object s in shipped)
            {
                if (s == null) continue;
                population.Add(Flatten(s, maxDepth));
                guids.Add(PointerRefUtil.InstanceGuid(s));
            }
            if (population.Count == 0) return findings;

            Guid ourGuid = PointerRefUtil.InstanceGuid(ours);

            foreach (KeyValuePair<string, Cell> kv in mine)
            {
                string field = kv.Key;

                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                var raws = new List<object>();
                var rawGuids = new List<Guid>();
                for (int i = 0; i < population.Count; i++)
                {
                    Cell c;
                    if (!population[i].TryGetValue(field, out c)) continue;
                    int n;
                    counts[c.Text] = counts.TryGetValue(c.Text, out n) ? n + 1 : 1;
                    raws.Add(c.Raw);
                    rawGuids.Add(guids[i]);
                }
                // A field no shipped instance carries says nothing -- the population is a different
                // subtype, not evidence that we are wrong.
                if (raws.Count == 0) continue;

                var finding = new FieldFinding
                {
                    Field = field,
                    Ours = kv.Value.Text,
                    ShippedCount = raws.Count,
                    Shipped = SortByCount(counts),
                };

                int matching;
                counts.TryGetValue(kv.Value.Text ?? "", out matching);
                bool everyShippedDiffers = counts.Count == raws.Count && raws.Count >= MinInstancesForPerInstance;

                if (everyShippedDiffers)
                {
                    finding.Verdict = PerInstance;
                    string note;
                    if (DerivationHolds(raws, rawGuids, out note))
                    {
                        finding.Note = note;
                        if (ourGuid != Guid.Empty && !SatisfiesDerivation(kv.Value.Raw, ourGuid))
                        {
                            finding.Verdict = DerivedViolation;
                            finding.Note = note + "; ours does not, so it names another object's sub-level identity";
                        }
                    }
                    else
                    {
                        finding.Note = "every shipped instance differs, so there is no majority to compare against";
                    }
                }
                else if (matching == 0)
                {
                    finding.Verdict = counts.Count == 1 ? UnanimousDisagreement : Disagreement;
                }
                else if ((double)matching / raws.Count < RareShare)
                {
                    finding.Verdict = Rare;
                }
                else
                {
                    finding.Verdict = Matches;
                }

                findings.Add(finding);
            }

            findings.Sort(delegate (FieldFinding a, FieldFinding b)
            {
                int ra = Rank(a.Verdict), rb = Rank(b.Verdict);
                if (ra != rb) return ra - rb;
                return string.CompareOrdinal(a.Field, b.Field);
            });
            return findings;
        }

        private static int Rank(string verdict)
        {
            for (int i = 0; i < Order.Length; i++) if (Order[i] == verdict) return i;
            return Order.Length;
        }

        private static List<KeyValuePair<string, int>> SortByCount(Dictionary<string, int> counts)
        {
            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                if (a.Value != b.Value) return b.Value - a.Value;
                return string.CompareOrdinal(a.Key, b.Key);
            });
            return list;
        }

        // ---------------------------------------------------------------- derivation

        /// <summary>
        /// The only derivation checked: the field's low 25 bits equal the low 25 bits of the object's
        /// own exported GUID (its first four bytes). Measured over the shipped corpus this holds for
        /// 1318/1318 exported SubWorldReferenceObjectData and is cleanly all-or-nothing per type,
        /// which is what makes "does it hold across this population?" a usable test.
        ///
        /// The top bits are NOT part of it. They are real flags and they vary (0x02 on 742 shipped
        /// instances, 0x00 on 277, then 0x0a, 0x32, 0x06, 0x0e, 0x03, 0x07 and a tail), so treating
        /// the whole word as derived would clobber them.
        /// </summary>
        private const uint DerivedMask = 0x01FFFFFFu;

        private static bool DerivationHolds(IList<object> values, IList<Guid> instanceGuids, out string note)
        {
            note = null;
            int checkedCount = 0;
            for (int i = 0; i < values.Count; i++)
            {
                if (instanceGuids[i] == Guid.Empty) continue;   // not exported: the rule cannot apply
                uint v;
                if (!TryUInt(values[i], out v)) return false;
                if ((v & DerivedMask) != (GuidData1(instanceGuids[i]) & DerivedMask)) return false;
                checkedCount++;
            }
            if (checkedCount < MinInstancesForDerivation) return false;
            note = "all " + checkedCount.ToString(CultureInfo.InvariantCulture) +
                   " shipped instances satisfy (value & 0x01FFFFFF) == (instance GUID's first four bytes & 0x01FFFFFF)";
            return true;
        }

        private static bool SatisfiesDerivation(object ourValue, Guid ourGuid)
        {
            uint v;
            if (!TryUInt(ourValue, out v)) return false;
            return (v & DerivedMask) == (GuidData1(ourGuid) & DerivedMask);
        }

        private static uint GuidData1(Guid g)
        {
            byte[] b = g.ToByteArray();      // little-endian Data1
            return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        private static bool TryUInt(object o, out uint value)
        {
            value = 0;
            if (o == null) return false;
            Type t = o.GetType();
            if (t == typeof(uint))   { value = (uint)o; return true; }
            if (t == typeof(int))    { value = unchecked((uint)(int)o); return true; }
            if (t == typeof(ushort)) { value = (ushort)o; return true; }
            if (t == typeof(short))  { value = unchecked((uint)(short)o); return true; }
            if (t == typeof(ulong))  { value = unchecked((uint)(ulong)o); return true; }
            if (t == typeof(long))   { value = unchecked((uint)(long)o); return true; }
            if (t == typeof(byte))   { value = (byte)o; return true; }
            return false;
        }

        // ---------------------------------------------------------------- flattening

        private static Dictionary<string, Cell> Flatten(object obj, int maxDepth)
        {
            var cells = new Dictionary<string, Cell>(StringComparer.Ordinal);
            Walk(obj, "", maxDepth, cells);
            return cells;
        }

        private static void Walk(object obj, string prefix, int depthLeft, Dictionary<string, Cell> cells)
        {
            if (obj == null || depthLeft < 0) return;
            foreach (PropertyInfo pi in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (Skipped.Contains(pi.Name)) continue;
                if (pi.GetIndexParameters().Length != 0) continue;
                object value;
                try { value = pi.GetValue(obj); } catch { continue; }

                string path = prefix.Length == 0 ? pi.Name : prefix + "." + pi.Name;
                Type t = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;

                if (t.Name == "PointerRef")
                {
                    cells[path] = new Cell { Text = DescribeRef(value), Raw = null };
                    continue;
                }
                if (value is IList)
                {
                    // Element-wise comparison across a population is a different and much noisier
                    // question; the length is the part that catches a half-built list.
                    cells[path + ".Count"] = new Cell
                    {
                        Text = ((IList)value).Count.ToString(CultureInfo.InvariantCulture),
                        Raw = ((IList)value).Count,
                    };
                    continue;
                }
                if (IsLeaf(t))
                {
                    cells[path] = new Cell { Text = Render(value), Raw = value };
                    continue;
                }
                if (value != null && depthLeft > 0) Walk(value, path, depthLeft - 1, cells);
            }
        }

        private static bool IsLeaf(Type t)
        {
            if (t.IsPrimitive || t.IsEnum) return true;
            if (t == typeof(string) || t == typeof(decimal) || t == typeof(Guid) || t == typeof(DateTime)) return true;
            // CString and friends: a value type with no readable sub-structure of its own.
            return t.Name == "CString" || t.Name == "FileRef" || t.Name == "ResourceRef" || t.Name == "SHA1";
        }

        private static string Render(object value)
        {
            if (value == null) return "<null>";
            IFormattable f = value as IFormattable;
            return f != null ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
        }

        // A PointerRef's own GUIDs are unique per instance and say nothing on their own, but WHICH
        // asset it points at is exactly how "layer refs still pointing at the originals" shows up.
        private static string DescribeRef(object pr)
        {
            if (pr == null) return "<null>";
            object kind = Member(pr, "Type");
            string k = kind == null ? "" : kind.ToString();
            if (k == "Null" || k == "") return "<null>";
            if (k == "Internal")
            {
                object inner = Member(pr, "Internal");
                return inner == null ? "internal:<null>" : "internal:" + inner.GetType().Name;
            }
            object ext = Member(pr, "External");
            object file = ext == null ? null : Member(ext, "FileGuid");
            return "external:" + (file == null ? "?" : file.ToString());
        }

        private static object Member(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) { try { return p.GetValue(obj); } catch { return null; } }
            FieldInfo fi = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) return null;
            try { return fi.GetValue(obj); } catch { return null; }
        }
    }
}
