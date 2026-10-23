using hk;
using OpenTK.Mathematics;

namespace HavokCollisionTool;

/// <summary>
/// Builds an <see cref="hknpStaticCompoundShape"/> that owns several convex polytopes,
/// together with the quantised AABB tree the runtime walks to reach them.
///
/// This is the shape SWBF2 itself uses whenever a collision asset has more than one
/// convex piece. Writing several root shapes into
/// <see cref="HavokPhysicsContainer._shapes"/> instead does not work: the engine takes
/// slot 0 as the body's physics shape, so every piece after the first is present in the
/// file but collides with nothing — the "you can only stand on the first shape" symptom.
///
/// Every constant below was read out of shipped collision data
/// (levels/mp/starkiller_01/objects/skb_entrancehall_01, a five-piece compound),
/// not guessed.
/// </summary>
internal static class CompoundBuilder
{
    /// <summary>
    /// User data on a composite root. The high 16 bits are the material override and
    /// 0xFFFF is the "inherit, do not override" sentinel, so a compound contributes no
    /// material of its own and each child's material wins. Shipped compounds and
    /// compressed meshes both carry exactly this value.
    /// </summary>
    public const ulong InheritUserData = 0xFFFF0000UL;

    /// <summary>Shipped compounds mark every instance as non-destructible with this.</summary>
    private const ushort NoDestruction = 0xFFFF;

    /// <summary>
    /// Divisor of the parent extent used by the node codec. 15 is the largest value a
    /// nibble can hold and 15 * 15 = 225 &lt; 226, so a child box can never be quantised
    /// past its parent's far face.
    /// </summary>
    private const float CodecRange = 226f;

    public static hknpStaticCompoundShape Build(IReadOnlyList<hknpShape> children, ulong userData)
    {
        if (children.Count == 0)
        {
            throw new InvalidDataException("A compound shape needs at least one child shape.");
        }

        List<Box3> boxes = children.Select(GetAabb).ToList();
        Box3 domain = Union(boxes, 0, boxes.Count);

        hkFreeListArrayhknpShapeInstancehkHandleshort32767hknpShapeInstanceIdDiscriminant8hknpShapeInstance instances = new()
        {
            // -1 means "no free slot"; every element is live.
            _firstFree = -1
        };

        hknpStaticCompoundShapeTree tree = BuildTree(boxes, domain);

        // Each record points back into the tree: trans.w holds the index of the node whose
        // leaf names it. Read it off the finished tree rather than threading it through Emit.
        int[] leafNode = new int[children.Count];
        for (int k = 0; k < tree._nodes.Count; k++)
        {
            hkcdStaticTreeCodec3Axis6 node = tree._nodes[k]!;
            if ((node._hiData & 0x80) == 0)
            {
                leafNode[node._loData | (node._hiData << 16)] = k;
            }
        }

        for (int i = 0; i < children.Count; i++)
        {
            // Children are already in the asset's local space, so the placement is identity.
            // Serialised as 16 floats, row-major, translation in row 4. The w lanes are
            // storage, not geometry — see InstanceRecords. The shape-size lane (M34) depends
            // on the serialised layout and is filled in just before the res is written.
            Matrix4 transform = Matrix4.Identity;
            transform.M14 = InstanceRecords.PackInt24(InstanceRecords.PropChildFlags);
            transform.M44 = InstanceRecords.PackInt24(leafNode[i]);

            instances._elements.Add(new hknpShapeInstance
            {
                _transform = transform,
                _scale = new Vector4(1f, 1f, 1f, 1f),
                _shape = children[i],

                // The shape tag is added to the part index as the codec walks down the
                // tree. Zero keeps every child on the part the compound itself resolved
                // to, which is what a single-part collision asset wants.
                _shapeTag = 0,
                _destructionTag = NoDestruction,
                _padding = new byte[30]
            });
        }

        return new hknpStaticCompoundShape
        {
            _flags = hknpShape_FlagsEnum.IS_COMPOSITE_SHAPE,
            _numShapeKeyBits = ShapeKeyBits(children.Count),
            _dispatchType = hknpCollisionDispatchType_Enum.COMPOSITE,
            _convexRadius = 0f,
            _userData = userData,

            // Mass properties live on the children; shipped compounds carry none.
            _properties = null,

            _edgeWeldingMap = new hknpSparseCompactMapunsignedshort
            {
                _secondaryKeyMask = 0xFFFFFFFF,
                _sencondaryKeyBits = 0
            },
            _shapeTagCodecInfo = 0xFFFFFFFF,

            _instances = instances,
            _aabb = ToHkAabb(domain),
            _isMutable = false,
            // The same tree the records' leaf lanes were read from — building it again here could
            // reorder equal-centre boxes and leave every back-link pointing at the wrong node.
            _boundingVolumeData = new hknpStaticCompoundShapeData { _aabbTree = tree }
        };
    }

    /// <summary>
    /// Bits needed to address a child, which is how the runtime sizes a shape key.
    /// Shipped: five instances -&gt; 3.
    /// </summary>
    private static byte ShapeKeyBits(int count)
    {
        byte bits = 0;
        while ((1 << bits) < count)
        {
            bits++;
        }
        return bits;
    }

    #region AABB tree

    /// <summary>
    /// Builds the compound's bounding volume hierarchy.
    ///
    /// Layout, decoded from shipped data: a full binary tree of 2N-1 nodes in one flat
    /// array, depth first. A node's left child is always the next node; its right child
    /// sits <c>2 * (leaves in the left subtree)</c> nodes further on. Each node stores
    /// its box as three bytes, quantised against its parent's box rather than in
    /// absolute units.
    /// </summary>
    private static hknpStaticCompoundShapeTree BuildTree(List<Box3> boxes, Box3 domain)
    {
        List<hkcdStaticTreeCodec3Axis6> nodes = new(Math.Max(1, boxes.Count * 2 - 1));

        // Sorted in place as the tree splits; carries the original instance index so a
        // leaf can still name the child it bounds.
        List<int> order = Enumerable.Range(0, boxes.Count).ToList();

        Emit(nodes, boxes, order, 0, boxes.Count, domain);

        return new hknpStaticCompoundShapeTree
        {
            _domain = ToHkAabb(domain),
            _nodes = nodes.Cast<hkcdStaticTreeCodec3Axis6?>().ToList()
        };
    }

    /// <summary>
    /// Emits the subtree covering <c>order[lo..hi)</c> and returns its leaf count.
    /// </summary>
    private static int Emit(List<hkcdStaticTreeCodec3Axis6> nodes, List<Box3> boxes,
                            List<int> order, int lo, int hi, Box3 parent)
    {
        hkcdStaticTreeCodec3Axis6 node = new() { _xyz = new byte[3] };
        nodes.Add(node);

        // The node's own box has to be quantised against the parent before recursing:
        // children quantise against the *decoded* box, not the exact one, or the
        // rounding drifts and a child ends up outside its parent.
        Box3 decoded = Encode(node, parent, Union(boxes, order, lo, hi));

        if (hi - lo == 1)
        {
            int child = order[lo];
            node._loData = (ushort)child;
            node._hiData = (byte)(child >> 16);
            return 1;
        }

        // Median split along the widest axis of this node's box. Any partition produces
        // a legal tree; this one keeps the boxes tight, which is what the traversal
        // rejects work with.
        int axis = WidestAxis(decoded);
        order.Sort(lo, hi - lo, Comparer<int>.Create(
            (a, b) => Centre(boxes[a], axis).CompareTo(Centre(boxes[b], axis))));

        int mid = lo + (hi - lo) / 2;

        int leftLeaves = Emit(nodes, boxes, order, lo, mid, decoded);

        // Offset to the right child. Always even — a subtree of k leaves is 2k-1 nodes,
        // so the offset is 1 + (2k-1) = 2k — which is why the field stores it halved.
        int offset = 2 * leftLeaves;
        node._loData = (ushort)(offset >> 1);
        node._hiData = (byte)(((offset >> 1) >> 16) | 0x80);

        Emit(nodes, boxes, order, mid, hi, decoded);
        return hi - lo;
    }

    /// <summary>
    /// Packs <paramref name="child"/> into three bytes relative to <paramref name="parent"/>
    /// and returns the box the runtime will decode back out.
    ///
    /// Each axis gets two nibbles: the high one pushes the minimum up from the parent's
    /// minimum, the low one pulls the maximum down from the parent's maximum, both by
    /// <c>nibble² / 226</c> of the parent extent. Rounding is always outward, so the
    /// decoded box contains the real one — a box quantised too small would let the
    /// traversal miss collisions against the geometry it bounds.
    /// </summary>
    private static Box3 Encode(hkcdStaticTreeCodec3Axis6 node, Box3 parent, Box3 child)
    {
        for (int i = 0; i < 3; i++)
        {
            float extent = parent.Max[i] - parent.Min[i];
            byte high = 0;
            byte low = 0;

            if (extent > 0f)
            {
                float scale = extent / CodecRange;

                while (high < 15 && parent.Min[i] + scale * (high + 1) * (high + 1) <= child.Min[i])
                {
                    high++;
                }

                while (low < 15 && parent.Max[i] - scale * (low + 1) * (low + 1) >= child.Max[i])
                {
                    low++;
                }
            }

            node._xyz[i] = (byte)((high << 4) | low);
        }

        Box3 decoded = Decode(parent, node);

        // Cheap insurance against a float edge case silently producing a box that does
        // not bound its geometry. Failing here is far better than shipping a res whose
        // collision has holes in it.
        for (int i = 0; i < 3; i++)
        {
            if (decoded.Min[i] > child.Min[i] || decoded.Max[i] < child.Max[i])
            {
                throw new InvalidDataException(
                    $"AABB tree quantisation lost geometry on axis {i}: " +
                    $"[{decoded.Min[i]}, {decoded.Max[i]}] does not contain " +
                    $"[{child.Min[i]}, {child.Max[i]}].");
            }
        }

        return decoded;
    }

    /// <summary>The runtime's side of <see cref="Encode"/>, kept here for the self test.</summary>
    public static Box3 Decode(Box3 parent, hkcdStaticTreeCodec3Axis6 node)
    {
        Vector3 min = parent.Min;
        Vector3 max = parent.Max;

        for (int i = 0; i < 3; i++)
        {
            float scale = (parent.Max[i] - parent.Min[i]) / CodecRange;
            int high = node._xyz[i] >> 4;
            int low = node._xyz[i] & 0xF;

            min[i] += scale * high * high;
            max[i] -= scale * low * low;
        }

        return new Box3(min, max);
    }

    #endregion

    #region Geometry helpers

    public static Box3 GetAabb(hknpShape shape) => shape switch
    {
        hknpConvexShape convex => ShapeBuilder.ComputeAabb(convex._vertices),
        hknpCompressedMeshShape mesh => CompressedMeshBuilder.GetAabb(mesh),
        hknpCompoundShape compound when compound._aabb is not null =>
            new Box3(new Vector3(compound._aabb._min), new Vector3(compound._aabb._max)),
        _ => throw new NotSupportedException(
            $"Cannot compute an AABB for {shape.GetType().Name}.")
    };

    public static Box3 Union(IReadOnlyList<Box3> boxes, int lo, int hi)
    {
        Vector3 min = boxes[lo].Min;
        Vector3 max = boxes[lo].Max;

        for (int i = lo + 1; i < hi; i++)
        {
            min = Vector3.ComponentMin(min, boxes[i].Min);
            max = Vector3.ComponentMax(max, boxes[i].Max);
        }

        return new Box3(min, max);
    }

    private static Box3 Union(IReadOnlyList<Box3> boxes, IReadOnlyList<int> order, int lo, int hi)
    {
        Vector3 min = boxes[order[lo]].Min;
        Vector3 max = boxes[order[lo]].Max;

        for (int i = lo + 1; i < hi; i++)
        {
            min = Vector3.ComponentMin(min, boxes[order[i]].Min);
            max = Vector3.ComponentMax(max, boxes[order[i]].Max);
        }

        return new Box3(min, max);
    }

    private static float Centre(Box3 box, int axis) => (box.Min[axis] + box.Max[axis]) * 0.5f;

    private static int WidestAxis(Box3 box)
    {
        Vector3 size = box.Max - box.Min;
        if (size.X >= size.Y && size.X >= size.Z) return 0;
        return size.Y >= size.Z ? 1 : 2;
    }

    /// <summary>
    /// W is padding in a serialised hkAabb — shipped files leave stale values in it.
    /// Zero in both keeps min &lt;= max on every lane, so a SIMD containment test cannot
    /// trip over it.
    /// </summary>
    private static hkAabb ToHkAabb(Box3 box) => new()
    {
        _min = new Vector4(box.Min, 0f),
        _max = new Vector4(box.Max, 0f)
    };

    #endregion
}
