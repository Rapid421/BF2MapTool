using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Minimal, read-only HKX 2013 reader for the 64-bit static-compound instance
    /// transforms stored in Battlefront II HavokPhysicsData resources.
    ///
    /// This intentionally does not model Havok shapes or collision data.  It reads
    /// the public packfile structure (sections, virtual fixups and pointer fixups)
    /// and exposes only the 3x4 transform and scale carried by each compound instance.
    ///
    /// A resource holds several hknpStaticCompoundShape objects and only one of them is
    /// the level's StaticModelGroup placement array.  The others are the duplicate second
    /// collision slot and the per-prop compounds used by props whose own collision is built
    /// from several pieces; those sit at identity translations and are not placements.
    /// Concatenating them all yields far more transforms than a group has instances, so the
    /// arrays are returned separately and the caller picks by expected length.
    /// </summary>
    public static class HavokTransformReader
    {
        public sealed class Transform
        {
            public float[] Right;
            public float[] Up;
            public float[] Forward;
            public float[] Translation;
        }

        /// <summary>
        /// One compound's AABB tree: a full binary tree of 2N-1 six-byte nodes, flat and depth
        /// first.  Byte layout per node, decoded from shipped data and verified against every
        /// compound in four level resources: three quantised box bytes, a flags byte where 0x80
        /// marks an internal node, then a little-endian ushort.  The 24-bit value those two carry
        /// is the instance index on a leaf, and on an internal node it is half the offset to the
        /// right child -- the left child is always the next node.
        /// </summary>
        public sealed class CompoundTree
        {
            public int NodeCount;
            public int LeafCount;
            public bool StructureValid;
            public string StructureError;
        }

        private const uint Magic1 = 0x57e0e057;
        private const uint Magic2 = 0x10c0c010;
        private const int TreeNodeSize = 6;

        /// <summary>
        /// Reads the placement array for a StaticModelGroup that needs <paramref name="expectedCount"/>
        /// instance transforms, choosing among the resource's compound shapes by length.
        /// </summary>
        public static bool TryReadPlacements(byte[] data, int expectedCount, out List<Transform> transforms, out string error)
        {
            transforms = null;
            List<List<Transform>> arrays;
            if (!TryReadInstanceArrays(data, out arrays, out error)) return false;

            transforms = SelectPlacements(arrays, expectedCount);
            if (transforms != null) return true;
            error = "no compound instance array holds " + expectedCount + " transforms (found " +
                DescribeSizes(arrays) + ")";
            return false;
        }

        /// <summary>
        /// Picks the compound instance array that holds exactly <paramref name="expectedCount"/>
        /// transforms, or null when none does.  The duplicate collision slot yields an identical
        /// array of the same length, so the first match is as good as any.
        /// </summary>
        public static List<Transform> SelectPlacements(List<List<Transform>> arrays, int expectedCount)
        {
            if (arrays == null) return null;
            foreach (List<Transform> candidate in arrays)
                if (candidate.Count == expectedCount) return candidate;
            return null;
        }

        public static string DescribeSizes(List<List<Transform>> arrays)
        {
            if (arrays == null || arrays.Count == 0) return "none";
            var sizes = new List<string>();
            foreach (List<Transform> candidate in arrays)
                sizes.Add(candidate.Count.ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", sizes.ToArray());
        }

        public static bool TryReadInstanceArrays(byte[] data, out List<List<Transform>> arrays, out string error)
        {
            List<CompoundTree> ignored;
            return TryReadCompounds(data, out arrays, out ignored, out error);
        }

        /// <summary>
        /// Reads every compound's instance array, plus the node count of every compound AABB tree
        /// in the resource.  Nothing in the file links a shape to its tree, so they are returned as
        /// separate lists and matched by size: a compound of N instances owns a tree of 2N-1 nodes.
        /// </summary>
        public static bool TryReadCompounds(byte[] data, out List<List<Transform>> arrays,
            out List<CompoundTree> trees, out string error)
        {
            arrays = new List<List<Transform>>();
            trees = new List<CompoundTree>();
            error = null;
            try
            {
                if (data == null || data.Length < 256) throw new InvalidDataException("resource is too small");

                List<int> headers = FindPackfileHeaders(data);
                if (headers.Count < 2) throw new InvalidDataException("64-bit HKX packfile was not found");
                int start = headers[headers.Count - 1];
                Packfile pack = ReadPackfile(data, start);
                if (pack.PointerBytes != 8) throw new InvalidDataException("expected a 64-bit HKX packfile");

                int packEnd = CheckedOffset(start, pack.DataOffset, pack.EndOffset, data.Length);
                if (packEnd + 8 > data.Length) throw new InvalidDataException("missing Frostbite fixup table sizes");
                int fixup32Size = ReadInt32(data, packEnd);
                int fixup64Size = ReadInt32(data, packEnd + 4);
                if (fixup32Size < 0 || fixup64Size < 0) throw new InvalidDataException("invalid fixup table sizes");
                int fixup64Start = checked(packEnd + 8 + fixup32Size);
                if (fixup64Start < 0 || fixup64Start + fixup64Size > data.Length)
                    throw new InvalidDataException("64-bit fixup table extends past the resource");

                // Bound the scan by the table's own size. Using the section's global-fixup
                // *offset* here (as this once did) reads roughly half the entries -- 160 of 322 on
                // a shipped level resource -- which silently drops pointers past the halfway mark.
                Dictionary<long, long> pointers = new Dictionary<long, long>();
                int pos = fixup64Start;
                int globalEnd = checked(fixup64Start + fixup64Size);
                while (pos + 8 <= globalEnd)
                {
                    int field = ReadInt32(data, pos);
                    int target = ReadInt32(data, pos + 4);
                    pos += 8;
                    if (field >= 0 && target >= 0)
                        pointers[pack.DataBase + field] = pack.DataBase + target;
                }

                List<int> shapeOffsets = FindObjects(pack, "hknpStaticCompoundShape");
                if (shapeOffsets.Count == 0)
                {
                    // Naming what IS in the packfile turns a dead end into a diagnosis. A resource
                    // built by an older Havok carries hkp* classes rather than the hknp* ones this
                    // reader knows, and that is visible immediately from this list.
                    throw new InvalidDataException("no hknpStaticCompoundShape in this resource; classes present: " +
                        DescribeClasses(pack));
                }

                // One entry per compound shape, in packfile order. Kept separate on purpose:
                // see the class remarks for why concatenating them is wrong.
                foreach (int shapeOffset in shapeOffsets)
                {
                    // hknpShape base (0x28), followed by the compound fields. The
                    // instance array pointer is 0x60 bytes from the object start.
                    long arrayField = shapeOffset + 0x60L;
                    long instanceOffset;
                    if (!pointers.TryGetValue(arrayField, out instanceOffset)) continue;
                    int count = ReadInt32(data, checked((int)arrayField + 8));
                    if (count < 0 || count > 1000000) throw new InvalidDataException("invalid instance count " + count);

                    const int instanceSize = 0x80;
                    var instances = new List<Transform>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int o = checked((int)instanceOffset + i * instanceSize);
                        if (o < 0 || o + instanceSize > data.Length) throw new InvalidDataException("instance array is truncated");

                        float sx = ReadSingle(data, o + 0x40);
                        float sy = ReadSingle(data, o + 0x44);
                        float sz = ReadSingle(data, o + 0x48);
                        instances.Add(new Transform
                        {
                            Right = Scale(ReadVec3(data, o + 0x00), sx),
                            Up = Scale(ReadVec3(data, o + 0x10), sy),
                            Forward = Scale(ReadVec3(data, o + 0x20), sz),
                            Translation = ReadVec3(data, o + 0x30)
                        });
                    }
                    arrays.Add(instances);
                }
                if (arrays.Count == 0) throw new InvalidDataException("static-compound instance arrays were not found");

                // hknpStaticCompoundShapeData carries the quantised AABB tree the runtime walks
                // before it touches geometry: a full binary tree of 2N-1 six-byte nodes, held in an
                // array 0x10 bytes into the object.
                foreach (int dataOffset in FindObjects(pack, "hknpStaticCompoundShapeData"))
                {
                    long arrayField = dataOffset + 0x10L;
                    long nodeOffset;
                    if (!pointers.TryGetValue(arrayField, out nodeOffset)) continue;
                    int nodes = ReadInt32(data, checked((int)arrayField + 8));
                    if (nodes < 0 || nodes > 4000000) continue;
                    trees.Add(WalkTree(data, checked((int)nodeOffset), nodes));
                }
                return true;
            }
            catch (Exception ex)
            {
                arrays.Clear();
                trees.Clear();
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Walks a tree the way the runtime does and reports whether it is structurally sound: every
        /// node reached at most once, and the leaves carrying each instance index exactly once.  A
        /// tree that fails this cannot route the traversal to all of its geometry.
        /// </summary>
        private static CompoundTree WalkTree(byte[] data, int nodesAt, int nodeCount)
        {
            var tree = new CompoundTree { NodeCount = nodeCount };
            if (nodeCount <= 0 || (nodeCount & 1) == 0)
            {
                tree.StructureError = "node count " + nodeCount + " is not a positive odd 2N-1";
                return tree;
            }
            int leafTarget = (nodeCount + 1) / 2;
            if (nodesAt < 0 || (long)nodesAt + (long)nodeCount * TreeNodeSize > data.Length)
            {
                tree.StructureError = "node array is truncated";
                return tree;
            }

            var seen = new bool[nodeCount];
            var leaves = new bool[leafTarget];
            var stack = new Stack<int>();
            stack.Push(0);
            int leafCount = 0;

            while (stack.Count > 0)
            {
                int index = stack.Pop();
                if (index < 0 || index >= nodeCount)
                {
                    tree.StructureError = "node index " + index + " is out of range";
                    return tree;
                }
                if (seen[index])
                {
                    tree.StructureError = "node " + index + " is reachable more than once";
                    return tree;
                }
                seen[index] = true;

                int o = nodesAt + index * TreeNodeSize;
                int flags = data[o + 3];
                int value = ((flags & 0x7F) << 16) | (data[o + 4] | (data[o + 5] << 8));

                if ((flags & 0x80) != 0)
                {
                    stack.Push(index + 1);
                    stack.Push(index + 2 * value);
                    continue;
                }
                if (value < 0 || value >= leafTarget)
                {
                    tree.StructureError = "leaf references instance " + value + " of " + leafTarget;
                    return tree;
                }
                if (leaves[value])
                {
                    tree.StructureError = "instance " + value + " is referenced by more than one leaf";
                    return tree;
                }
                leaves[value] = true;
                leafCount++;
            }

            tree.LeafCount = leafCount;
            if (leafCount != leafTarget)
            {
                tree.StructureError = "walk reached " + leafCount + " leaf/leaves, expected " + leafTarget;
                return tree;
            }
            tree.StructureValid = true;
            return tree;
        }

        /// <summary>
        /// True when every compound of N instances can be matched to a distinct tree of 2N-1 nodes.
        /// Duplicate slots produce equal-sized trees, so ties are interchangeable.
        /// </summary>
        public static bool TreesMatchInstanceArrays(List<List<Transform>> arrays, List<CompoundTree> trees,
            out string detail)
        {
            detail = null;
            if (arrays == null || trees == null) return true;

            foreach (CompoundTree tree in trees)
            {
                if (tree.StructureValid) continue;
                detail = "an AABB tree of " + tree.NodeCount + " nodes is malformed: " + tree.StructureError;
                return false;
            }

            var pool = new List<CompoundTree>(trees);
            foreach (List<Transform> candidate in arrays)
            {
                int expected = candidate.Count * 2 - 1;
                int found = -1;
                for (int i = 0; i < pool.Count; i++)
                    if (pool[i].NodeCount == expected) { found = i; break; }
                if (found >= 0) { pool.RemoveAt(found); continue; }
                detail = "a compound of " + candidate.Count + " instance(s) has no AABB tree of " +
                    expected + " nodes (trees present: " + DescribeTrees(trees) + ")";
                return false;
            }
            return true;
        }

        public static string DescribeTrees(List<CompoundTree> trees)
        {
            if (trees == null || trees.Count == 0) return "none";
            var parts = new List<string>();
            foreach (CompoundTree t in trees) parts.Add(t.NodeCount.ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", parts.ToArray());
        }

        private sealed class Packfile
        {
            public int PointerBytes;
            public int DataOffset;
            public int LocalFixupSize;
            public int GlobalFixupSize;
            public int VirtualFixupOffset;
            public int EndOffset;
            public long DataBase;
            public Dictionary<int, string> Classes = new Dictionary<int, string>();
            public Dictionary<int, string> Objects = new Dictionary<int, string>();
        }

        private static Packfile ReadPackfile(byte[] data, int start)
        {
            if (ReadUInt32(data, start) != Magic1 || ReadUInt32(data, start + 4) != Magic2)
                throw new InvalidDataException("invalid HKX magic");

            uint layout = ReadUInt32(data, start + 0x10);
            int pointerBytes = (int)(layout & 0xff);
            if (pointerBytes != 4 && pointerBytes != 8)
                throw new InvalidDataException("unsupported HKX pointer size " + pointerBytes);
            int sections = ReadInt32(data, start + 0x14);
            if (sections <= 0 || sections > 16) throw new InvalidDataException("invalid HKX section count");

            int classOffset = -1, classSize = 0, dataOffset = -1;
            int localFixups = 0, globalFixups = 0, virtualFixups = 0, endOffset = 0;
            int section = start + 0x50;
            for (int i = 0; i < sections; i++, section += 0x40)
            {
                string name = ReadAsciiZ(data, section, 16);
                if (name == "__classnames__")
                {
                    classOffset = ReadInt32(data, section + 0x14);
                    classSize = ReadInt32(data, section + 0x20);
                }
                else if (name == "__data__")
                {
                    dataOffset = ReadInt32(data, section + 0x14);
                    localFixups = ReadInt32(data, section + 0x18);
                    globalFixups = ReadInt32(data, section + 0x1c);
                    virtualFixups = ReadInt32(data, section + 0x20);
                    endOffset = ReadInt32(data, section + 0x2c);
                }
            }
            if (classOffset < 0 || dataOffset < 0) throw new InvalidDataException("required HKX sections were not found");

            Packfile result = new Packfile
            {
                PointerBytes = pointerBytes,
                DataOffset = dataOffset,
                LocalFixupSize = localFixups,
                GlobalFixupSize = globalFixups,
                VirtualFixupOffset = virtualFixups,
                EndOffset = endOffset,
                DataBase = start + (long)dataOffset
            };

            int classPos = checked(start + classOffset);
            int classEnd = checked(classPos + classSize);
            while (classPos + 6 <= classEnd && classEnd <= data.Length)
            {
                int descriptorStart = classPos;
                classPos += 5;
                int nameStart = classPos;
                while (classPos < classEnd && data[classPos] != 0) classPos++;
                if (classPos >= classEnd) break;
                string className = Encoding.ASCII.GetString(data, nameStart, classPos - nameStart);
                classPos++;
                if (className.Length > 0 && className.IndexOf('\0') < 0)
                    result.Classes[(descriptorStart - (start + classOffset)) + 5] = className;
                while (classPos < classEnd && data[classPos] == 0xff) classPos++;
            }

            int virtualPos = checked((int)result.DataBase + virtualFixups);
            int virtualEnd = checked((int)result.DataBase + endOffset);
            while (virtualPos + 12 <= virtualEnd && virtualEnd <= data.Length)
            {
                long objectRelative = ReadInt64(data, virtualPos);
                int classKey = ReadInt32(data, virtualPos + 8);
                virtualPos += 12;
                if (objectRelative < 0 || classKey < 0) continue;
                string className;
                if (result.Classes.TryGetValue(classKey, out className))
                    result.Objects[checked((int)(result.DataBase + objectRelative))] = className;
            }
            return result;
        }

        /// <summary>Distinct object class names in the packfile, with counts, most common first.</summary>
        private static string DescribeClasses(Packfile pack)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<int, string> item in pack.Objects)
            {
                int n;
                counts.TryGetValue(item.Value, out n);
                counts[item.Value] = n + 1;
            }
            if (counts.Count == 0) return "none";
            var names = new List<string>(counts.Keys);
            names.Sort(delegate(string a, string b)
            {
                int byCount = counts[b].CompareTo(counts[a]);
                return byCount != 0 ? byCount : string.CompareOrdinal(a, b);
            });
            var parts = new List<string>();
            for (int i = 0; i < names.Count && i < 12; i++)
                parts.Add(names[i] + " x" + counts[names[i]].ToString(CultureInfo.InvariantCulture));
            if (names.Count > 12) parts.Add("... +" + (names.Count - 12) + " more");
            return string.Join(", ", parts.ToArray());
        }

        private static List<int> FindObjects(Packfile pack, string className)
        {
            List<int> result = new List<int>();
            foreach (KeyValuePair<int, string> item in pack.Objects)
                if (string.Equals(item.Value, className, StringComparison.Ordinal)) result.Add(item.Key);
            result.Sort();
            return result;
        }

        private static List<int> FindPackfileHeaders(byte[] data)
        {
            List<int> result = new List<int>();
            for (int i = 0; i + 8 <= data.Length; i++)
            {
                if (ReadUInt32(data, i) == Magic1 && ReadUInt32(data, i + 4) == Magic2)
                {
                    result.Add(i);
                    i += 7;
                }
            }
            return result;
        }

        private static int CheckedOffset(int start, int a, int b, int length)
        {
            long value = start + (long)a + b;
            if (value < 0 || value > length) throw new InvalidDataException("HKX offset is outside the resource");
            return (int)value;
        }

        private static float[] ReadVec3(byte[] data, int offset)
        {
            return new[] { ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8) };
        }

        private static float[] Scale(float[] value, float scale)
        {
            return new[] { value[0] * scale, value[1] * scale, value[2] * scale };
        }

        private static string ReadAsciiZ(byte[] data, int offset, int max)
        {
            int count = 0;
            while (count < max && offset + count < data.Length && data[offset + count] != 0) count++;
            return Encoding.ASCII.GetString(data, offset, count);
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return unchecked((uint)ReadInt32(data, offset));
        }

        private static int ReadInt32(byte[] data, int offset)
        {
            if (offset < 0 || offset + 4 > data.Length) throw new EndOfStreamException();
            return data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24;
        }

        private static long ReadInt64(byte[] data, int offset)
        {
            uint lo = ReadUInt32(data, offset);
            uint hi = ReadUInt32(data, offset + 4);
            return unchecked((long)((ulong)lo | ((ulong)hi << 32)));
        }

        private static float ReadSingle(byte[] data, int offset)
        {
            return BitConverter.ToSingle(data, offset);
        }
    }
}
