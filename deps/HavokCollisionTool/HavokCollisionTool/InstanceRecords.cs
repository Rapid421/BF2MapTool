using System.Reflection;
using Frosty.Sdk.IO;
using Frosty.Sdk.Utils;
using FrostyHavokPlugin;
using FrostyHavokPlugin.Interfaces;
using hk;
using OpenTK.Mathematics;

namespace HavokCollisionTool;

/// <summary>
/// The packed values an <see cref="hknpShapeInstance"/> record carries in its transform, and
/// the one kind of record this tool must never overwrite.
///
/// A record's transform serialises as four hkVector4 and only their xyz are geometry. The
/// four w floats are storage (hkVector4::setInt24W: the float's bits are
/// <c>0x3F000000 | value</c>). Decoded from shipped BFII data and the in-game-verified writer
/// in frostycli-pr:
///
/// <list type="table">
///   <item><term>col0.w</term><description>flags. 0x40 on every child of a prop's compound
///   (98 of 98 in a shipped staticmodelgroup, 5 of 5 in skb_entrancehall_01). A level's
///   placement array uses 0x62–0x6E instead.</description></item>
///   <item><term>col1.w</term><description>raw zero inside a prop compound.</description></item>
///   <item><term>col2.w</term><description>the shape's memory size in bytes: a convex shape's
///   serialised object size, since it keeps its arrays inline; 208 for a compound; 160 for a
///   compressed mesh. Shipped values run from 128 to 3424, so this is measured, never assumed.</description></item>
///   <item><term>trans.w</term><description>the index of the AABB-tree node whose leaf names
///   this instance — a back-link from the record into the tree.</description></item>
/// </list>
///
/// Building records from <see cref="Matrix4.Identity"/> left 0, 0, 0 and 1.0 in those lanes,
/// which matches no shipped record. The XML dump prints only xyz, which is how that went
/// unnoticed: the output looked structurally identical to the original.
/// </summary>
internal static class InstanceRecords
{
    /// <summary>col0.w on a child of a prop's compound: an identity placement.</summary>
    public const int PropChildFlags = 0x40;

    /// <summary>
    /// Shapes that keep their data out of line carry a fixed runtime size rather than their
    /// packfile object size (from the verified writer's table, 11,177 of 11,177 shipped records).
    /// </summary>
    private static readonly Dictionary<Type, int> OutOfLineSizes = new()
    {
        [typeof(hknpStaticCompoundShape)] = 208,
        [typeof(hknpCompressedMeshShape)] = 160
    };

    public static float PackInt24(int value) =>
        BitConverter.Int32BitsToSingle(0x3F000000 | (value & 0xFFFFFF));

    /// <summary>
    /// Whether a lane holds a packed value. Bit 23 must be clear as well as the top byte
    /// matching: 1.0f is 0x3F800000, which shares the top byte, and an identity matrix puts
    /// exactly that in trans.w. Every real packed value is far below 0x800000 — shipped sizes
    /// top out at 3424, leaf indices in the tens of thousands.
    /// </summary>
    public static bool IsInt24(float lane) =>
        (BitConverter.SingleToInt32Bits(lane) & unchecked((int)0xFF800000)) == 0x3F000000;

    public static int Int24(float lane) => BitConverter.SingleToInt32Bits(lane) & 0xFFFFFF;

    #region Records written by earlier builds

    /// <summary>
    /// Fills in the packed lanes of records an earlier build of this tool left empty.
    ///
    /// Every import rewrites the whole res, so a compound an earlier build put into a slot this
    /// import does not touch is carried through as it was. Reproduced: after a compressed import
    /// of shape 1, shape 0's old compound stayed 0/5 packed — and the size pass then filled
    /// col2.w alone, leaving records half-written. So each lane is repaired on its own.
    ///
    /// Only identity children of a prop compound are touched, because that is all an earlier
    /// build ever wrote and the only placement whose flags are known (0x40). A level placement
    /// (scale.w 0) carries flag bits a prop child does not, so it is never guessed at; shipped
    /// placement arrays always arrive packed anyway. The size lane is left to FillShapeSizes.
    /// </summary>
    /// <returns>How many records needed any repair.</returns>
    public static int RepairLegacyRecords(hknpStaticCompoundShape compound)
    {
        List<hknpShapeInstance?> records = compound._instances?._elements ?? new();
        List<hkcdStaticTreeCodec3Axis6?> nodes = compound._boundingVolumeData?._aabbTree?._nodes ?? new();

        Dictionary<int, int> leafNode = new();
        for (int k = 0; k < nodes.Count; k++)
        {
            hkcdStaticTreeCodec3Axis6? node = nodes[k];
            if (node is not null && (node._hiData & 0x80) == 0)
            {
                leafNode[node._loData | (node._hiData << 16)] = k;
            }
        }

        int repaired = 0;
        for (int i = 0; i < records.Count; i++)
        {
            hknpShapeInstance? record = records[i];
            if (record is null || record._scale.W == 0f)
            {
                continue;
            }

            Matrix4 transform = record._transform;
            bool changed = false;

            if (!IsInt24(transform.M14) && IsIdentityPlacement(transform, record._scale))
            {
                transform.M14 = PackInt24(PropChildFlags);
                changed = true;
            }

            if (!IsInt24(transform.M44) && leafNode.TryGetValue(i, out int leaf))
            {
                transform.M44 = PackInt24(leaf);
                changed = true;
            }

            if (changed)
            {
                record._transform = transform;
                repaired++;
            }
        }

        return repaired;
    }

    private static bool IsIdentityPlacement(Matrix4 t, Vector4 scale) =>
        Math.Abs(t.M11 - 1f) < 1e-3f && Math.Abs(t.M22 - 1f) < 1e-3f && Math.Abs(t.M33 - 1f) < 1e-3f &&
        Math.Abs(t.M12) < 1e-3f && Math.Abs(t.M13) < 1e-3f && Math.Abs(t.M21) < 1e-3f &&
        Math.Abs(t.M23) < 1e-3f && Math.Abs(t.M31) < 1e-3f && Math.Abs(t.M32) < 1e-3f &&
        scale.X == 1f && scale.Y == 1f && scale.Z == 1f;

    #endregion

    #region Shape size lane

    /// <summary>
    /// Writes the shape-size lane (col2.w) of every compound record that does not already have
    /// one, measuring each shape exactly as it is about to be serialised.
    ///
    /// Records that came from the template keep their shipped value. Where that value
    /// disagrees with this tool's layout of the same shape it is reported, because it would
    /// mean the serialiser lays shapes out differently from the game's pipeline.
    /// </summary>
    public static void FillShapeSizes(HavokPhysicsData data, HavokPhysicsContainer container)
    {
        List<hknpStaticCompoundShape> compounds = new();
        foreach (hknpShape? shape in container._shapes)
        {
            CollectCompounds(shape, compounds);
        }

        if (compounds.Count == 0)
        {
            return;
        }

        Dictionary<IHavokObject, uint> offsets = MeasureLayout(data, out uint dataEnd);
        uint[] starts = offsets.Values.Distinct().OrderBy(x => x).ToArray();

        int disagreements = 0;
        foreach (hknpStaticCompoundShape compound in compounds)
        {
            foreach (hknpShapeInstance? instance in compound._instances!._elements)
            {
                if (instance?._shape is null)
                {
                    continue;
                }

                int size = SizeOf(instance._shape, offsets, starts, dataEnd);
                Matrix4 transform = instance._transform;

                if (IsInt24(transform.M34))
                {
                    disagreements += Int24(transform.M34) != size ? 1 : 0;
                    continue;
                }

                transform.M34 = PackInt24(size);
                instance._transform = transform;
            }
        }

        if (disagreements > 0)
        {
            Console.WriteLine(
                $"  note: {disagreements} record(s) kept from the template carry a shape size that differs " +
                "from how this tool lays that shape out. The shipped values were kept.");
        }
    }

    private static void CollectCompounds(hknpShape? shape, List<hknpStaticCompoundShape> into)
    {
        if (shape is not hknpStaticCompoundShape compound || into.Contains(compound))
        {
            return;
        }

        into.Add(compound);
        foreach (hknpShapeInstance? instance in compound._instances!._elements)
        {
            CollectCompounds(instance?._shape, into);
        }
    }

    private static int SizeOf(hknpShape shape, Dictionary<IHavokObject, uint> offsets, uint[] starts, uint dataEnd)
    {
        if (OutOfLineSizes.TryGetValue(shape.GetType(), out int fixedSize))
        {
            return fixedSize;
        }

        if (!offsets.TryGetValue(shape, out uint start))
        {
            throw new InvalidDataException(
                $"{shape.GetType().Name} was not serialised, so its size cannot be measured.");
        }

        // An object's span runs to the next object: the serialiser writes each object, then
        // its inline arrays, then pads, before starting the next one.
        int index = Array.BinarySearch(starts, start);
        uint end = index + 1 < starts.Length ? starts[index + 1] : dataEnd;
        return (int)(end - start);
    }

    /// <summary>
    /// Serialises once without keeping the result, and returns where every object landed in
    /// the data section plus where the object data ends. Serialisation is deterministic and
    /// lane values never change sizes, so the real write lays everything out identically.
    ///
    /// The offsets come from the vendored serialiser's private bookkeeping, read by reflection
    /// so the vendored copy stays byte-identical to its pinned upstream commit.
    /// </summary>
    private static Dictionary<IHavokObject, uint> MeasureLayout(HavokPhysicsData data, out uint dataEnd)
    {
        PackFileSerializer serializer = new();
        using Block<byte> packfile = new(0);
        using Block<byte> fixups = new(0);

        using (BlockStream stream = new(packfile, true))
        using (BlockStream fixupStream = new(fixups, true))
        {
            serializer.Serialize(data.m_obj!, stream, fixupStream, data.m_header!);
        }

        FieldInfo? field = typeof(PackFileSerializer)
            .GetField("m_globalLookup", BindingFlags.NonPublic | BindingFlags.Instance);

        if (field?.GetValue(serializer) is not Dictionary<IHavokObject, uint> lookup)
        {
            throw new InvalidOperationException(
                "The vendored PackFileSerializer no longer has m_globalLookup, so shape sizes cannot " +
                "be measured. The vendored library has changed; update InstanceRecords to match.");
        }

        // The data section header records where the virtual fixup table starts, which is where
        // object data stops.
        ReadOnlySpan<byte> bytes = packfile.ToSpan();
        int header = bytes[..Math.Min(bytes.Length, 0x200)].IndexOf("__data__"u8);
        if (header < 0)
        {
            throw new InvalidDataException("Serialised packfile has no __data__ section header.");
        }

        dataEnd = BitConverter.ToUInt32(bytes.Slice(header + 0x20, 4));
        return new Dictionary<IHavokObject, uint>(lookup, ReferenceEqualityComparer.Instance);
    }

    #endregion

    #region Placement arrays

    /// <summary>
    /// Whether a compound is a level's placement array rather than one object's collision.
    ///
    /// A StaticModelGroup's physics res has the same type and container as a prop's, but its
    /// root shapes place every member of the group in the world. The records give it away:
    /// scale.w is 0 for a level placement and 1.0 inside a prop (shipped: 2 of 2 placement
    /// arrays vs 98 of 98 prop compounds), and the flags are a genuinely packed value.
    /// </summary>
    public static bool IsPlacementArray(hknpStaticCompoundShape compound)
    {
        hknpShapeInstance? first = compound._instances?._elements.FirstOrDefault();
        return first is not null && IsInt24(first._transform.M14) && first._scale.W == 0f;
    }

    /// <summary>
    /// Refuses an import that would replace a placement array. Doing it anyway writes a
    /// single-object compound over the positions of every object in the group, detaching all
    /// of them from their collision — and it loads without error, so nothing would say so.
    /// </summary>
    public static void RefuseToReplacePlacementArrays(HavokPhysicsContainer container, IEnumerable<int> slots)
    {
        foreach (int slot in slots)
        {
            if (slot >= container._shapes.Count ||
                container._shapes[slot] is not hknpStaticCompoundShape compound ||
                !IsPlacementArray(compound))
            {
                continue;
            }

            throw new InvalidDataException(
                $"Shape {slot} of this res is not an object's collision. It is a level placement array: " +
                $"{compound._instances!._elements.Count} placements, each positioning another object's " +
                "own collision in the world. This res belongs to a StaticModelGroup, and replacing that " +
                "shape would detach every object in the group from its collision.\n\n" +
                "Import into the individual object's own *_Physics_Win32 asset instead. " +
                "Inspect Havok Collision on this asset lists what the placements point at.");
        }
    }

    #endregion
}
