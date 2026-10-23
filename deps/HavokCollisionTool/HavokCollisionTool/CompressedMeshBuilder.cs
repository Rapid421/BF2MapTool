using hk;
using OpenTK.Mathematics;

namespace HavokCollisionTool;

/// <summary>
/// Builds a real <see cref="hknpCompressedMeshShape"/> (Havok 2013.3 hkcdStaticMeshTree) from a triangle soup: exact
/// triangle collision, the shape Frostbite's own pipeline makes for static meshes (HavokMeshShapeBuilder::endBuild ->
/// new hknpCompressedMeshShape) and the one shipped BFII architecture stands players on (Hoth snow cave walls and
/// ceilings, Kashyyyk terrace stairs, Vardos stairs: slot 0 is a compressed mesh).
///
/// Every rule below was read off shipped data and verified on it before this was written (work/cms_decode.py over the
/// Vardos stairs, the Kashyyyk stairs, two Hoth meshes and the Starkiller rock wall: 13,100 primitive boxes, 3,450
/// section-tree and 38 section-level internal nodes, 44 leaf indices, all consistent):
///
///   mesh tree domain            AABB of the mesh; shared vertices are quantised in it, 21 / 21 / 22 bits
///                               (x | y &lt;&lt; 21 | z &lt;&lt; 42), decoded min + q * extent / (2^bits - 1)
///   section codec parms         offset xyz, scale xyz; packed vertex = offset + q * scale, q = x 11 | y 11 &lt;&lt; 11 | z 10 &lt;&lt; 22
///   section shared vertices     (first index into _sharedVerticesIndex &lt;&lt; 8) | number of packed vertices: local vertex
///                               indices below that count are packed, the rest index the section's shared list
///   section primitives          (first primitive &lt;&lt; 8) | count (at most 127: a key holds prim &lt;&lt; 1 in 8 bits)
///   section data runs           (first run &lt;&lt; 8) | count; one run per section, value 0 (the shape's own material)
///   primitive                   4 local vertex indices; [2] == [3] is a triangle
///   section tree (Codec3Axis4)  pre-order; leaf data = primitive &lt;&lt; 1, internal = (leaves in left subtree &lt;&lt; 1) | 1
///   section-level tree (Axis5)  leaf hi/lo = section index; internal hi = 0x80 | left leaves &gt;&gt; 8, lo = left leaves
///   node box from its parent    min += extent/226 * hi^2, max -= extent/226 * lo^2 (hi/lo = the nibbles of each xyz byte)
///   section _leafIndex          index of the section's leaf in the section-level tree
///   shape key                   (section &lt;&lt; 8) | (primitive &lt;&lt; 1) | triangle-in-quad; bitsPerKey = bit width of the
///                               largest key = the shape's numShapeKeyBits; numPrimitiveKeys = triangle count
///   _quadIsFlat                 one bit per primitive (set for triangles), _triangleIsInternal two per primitive (0:
///                               no welding hints, as the Vardos stairs ship)
/// </summary>
internal static class CompressedMeshBuilder
{
    // A section holds at most 127 primitives (the key stores prim << 1 in a byte) and 256 local vertices (byte
    // indices). Shipped sections stop at 124; stay under both with room to spare.
    private const int MaxSectionPrims = 120;
    private const int MaxSectionVerts = 240;
    // A section's packed vertices are 11 / 11 / 10-bit fractions of its box, so the box size sets the precision: a 24 m
    // section rounds to under 6 mm (x, y) and 12 mm (z), a 200 m one to 5-10 cm. Shipped sections are a few metres.
    private const float MaxSectionExtent = 24f;

    public sealed class Stats
    {
        public int Triangles, Sections, PackedVertices, SharedVertices, Dropped;
        public float MaxError;
    }

    public static hknpCompressedMeshShape Build(IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles,
                                                ulong userData, out Stats stats)
    {
        stats = new Stats();

        // ---- 1. weld coincident vertices and drop degenerate / repeated triangles
        var weldKey = new Dictionary<(long, long, long), int>();
        var verts = new List<Vector3>();
        int[] remap = new int[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            Vector3 p = positions[i];
            var k = ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4));
            if (!weldKey.TryGetValue(k, out int id))
            {
                id = verts.Count;
                weldKey[k] = id;
                verts.Add(p);
            }
            remap[i] = id;
        }

        var tris = new List<(int A, int B, int C)>();
        var seenTri = new HashSet<(int, int, int)>();
        for (int t = 0; t + 2 < triangles.Count; t += 3)
        {
            int a = remap[triangles[t]], b = remap[triangles[t + 1]], c = remap[triangles[t + 2]];
            if (a == b || b == c || a == c) { stats.Dropped++; continue; }
            Vector3 n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
            if (n.Length < 1e-7f) { stats.Dropped++; continue; }
            // same triangle twice (same winding, any rotation) collides once
            var key = Canon(a, b, c);
            if (!seenTri.Add(key)) { stats.Dropped++; continue; }
            tris.Add((a, b, c));
        }
        if (tris.Count == 0)
        {
            throw new InvalidDataException("no triangles with area to build a compressed mesh from");
        }

        // ---- 2. sections: spatial median splits until a group fits a section
        var groups = new List<List<int>>();
        Split(Enumerable.Range(0, tris.Count).ToList(), tris, verts, groups);

        // ---- 3. section-level tree over the groups (it decides the section order: sections are listed in leaf order)
        Box3[] groupBox = groups.Select(g => BoxOfTris(g, tris, verts)).ToArray();
        var topOrder = new List<int>();
        var topShape = new List<TreeNode>();
        BuildTree(Enumerable.Range(0, groups.Count).ToList(), i => Centre(groupBox[i]), topShape, topOrder);
        // topOrder = group ids in leaf order -> section index = position in topOrder
        var sectionOfGroup = new int[groups.Count];
        for (int s = 0; s < topOrder.Count; s++) sectionOfGroup[topOrder[s]] = s;
        var sectionTris = topOrder.Select(g => groups[g]).ToList();

        // ---- 4. which vertices more than one section uses: those are shared (one global copy, no cracks)
        var vertSections = new Dictionary<int, HashSet<int>>();
        for (int s = 0; s < sectionTris.Count; s++)
            foreach (int t in sectionTris[s])
                foreach (int v in new[] { tris[t].A, tris[t].B, tris[t].C })
                {
                    if (!vertSections.TryGetValue(v, out var set)) vertSections[v] = set = new HashSet<int>();
                    set.Add(s);
                }

        // mesh domain (all vertices), padded so no axis is zero-width
        Box3 domain = BoxOf(verts.Where((_, i) => vertSections.ContainsKey(i)));
        domain = Pad(domain, 1e-3f);

        var mesh = new hknpCompressedMeshShapeTree();
        var sharedId = new Dictionary<int, int>();          // global vertex -> index into _sharedVertices
        var sharedDecoded = new Dictionary<int, Vector3>();
        int tri3 = 0;
        uint maxKey = 0;
        float maxErr = 0f;

        for (int s = 0; s < sectionTris.Count; s++)
        {
            List<int> st = sectionTris[s];
            var used = new List<int>();
            var usedSet = new HashSet<int>();
            foreach (int t in st)
                foreach (int v in new[] { tris[t].A, tris[t].B, tris[t].C })
                    if (usedSet.Add(v)) used.Add(v);
            List<int> own = used.Where(v => vertSections[v].Count == 1).ToList();
            List<int> shr = used.Where(v => vertSections[v].Count > 1).ToList();

            // packed vertices, quantised in the box of this section's own vertices
            Box3 pbox = own.Count > 0 ? BoxOf(own.Select(v => verts[v])) : new Box3(domain.Min, domain.Max);
            pbox = Pad(pbox, 1e-3f);
            Vector3 scale = new((pbox.Max.X - pbox.Min.X) / 2047f, (pbox.Max.Y - pbox.Min.Y) / 2047f, (pbox.Max.Z - pbox.Min.Z) / 1023f);
            var local = new Dictionary<int, int>();
            var decoded = new List<Vector3>();
            uint firstPacked = (uint)mesh._packedVertices.Count;
            foreach (int v in own)
            {
                Vector3 p = verts[v];
                uint qx = Quant(p.X, pbox.Min.X, scale.X, 2047), qy = Quant(p.Y, pbox.Min.Y, scale.Y, 2047), qz = Quant(p.Z, pbox.Min.Z, scale.Z, 1023);
                mesh._packedVertices.Add(qx | (qy << 11) | (qz << 22));
                Vector3 d = new(pbox.Min.X + qx * scale.X, pbox.Min.Y + qy * scale.Y, pbox.Min.Z + qz * scale.Z);
                maxErr = Math.Max(maxErr, (d - p).Length);
                local[v] = decoded.Count;
                decoded.Add(d);
            }
            uint firstShared = (uint)mesh._sharedVerticesIndex.Count;
            foreach (int v in shr)
            {
                if (!sharedId.TryGetValue(v, out int sid))
                {
                    sid = mesh._sharedVertices.Count;
                    sharedId[v] = sid;
                    Vector3 p = verts[v];
                    Vector3 ext = domain.Max - domain.Min;
                    ulong qx = Quant64(p.X, domain.Min.X, ext.X, 0x1FFFFF), qy = Quant64(p.Y, domain.Min.Y, ext.Y, 0x1FFFFF), qz = Quant64(p.Z, domain.Min.Z, ext.Z, 0x3FFFFF);
                    mesh._sharedVertices.Add(qx | (qy << 21) | (qz << 42));
                    Vector3 d = new(domain.Min.X + qx * ext.X / 0x1FFFFF, domain.Min.Y + qy * ext.Y / 0x1FFFFF, domain.Min.Z + qz * ext.Z / 0x3FFFFF);
                    sharedDecoded[v] = d;
                    maxErr = Math.Max(maxErr, (d - p).Length);
                }
                mesh._sharedVerticesIndex.Add(checked((ushort)sid));
                local[v] = decoded.Count;
                decoded.Add(sharedDecoded[v]);
            }
            if (own.Count > 255 || decoded.Count > 256)
                throw new InvalidOperationException($"section {s}: {own.Count} packed / {decoded.Count} vertices exceed a section");

            // the section's own tree over its primitives, which also fixes the primitive order (leaf order)
            Func<int, Vector3> primCentre = t => (decoded[local[tris[t].A]] + decoded[local[tris[t].B]] + decoded[local[tris[t].C]]) / 3f;
            var secShape = new List<TreeNode>();
            var primOrder = new List<int>();
            BuildTree(st, primCentre, secShape, primOrder);
            uint firstPrim = (uint)mesh._primitives.Count;
            var primIndexOfTri = new Dictionary<int, int>();
            for (int p = 0; p < primOrder.Count; p++)
            {
                var (a, b, c) = tris[primOrder[p]];
                mesh._primitives.Add(new hkcdStaticMeshTreeBasePrimitive
                {
                    _indices = new[] { (byte)local[a], (byte)local[b], (byte)local[c], (byte)local[c] }
                });
                primIndexOfTri[primOrder[p]] = p;
                tri3++;
                maxKey = Math.Max(maxKey, ((uint)s << 8) | ((uint)p << 1));
            }

            Box3 secDomain = BoxOf(decoded);
            var section = new hkcdStaticMeshTreeBaseSection
            {
                _codecParms = new[] { pbox.Min.X, pbox.Min.Y, pbox.Min.Z, scale.X, scale.Y, scale.Z },
                _firstPackedVertex = firstPacked,
                _sharedVertices = new hkcdStaticMeshTreeBaseSectionSharedVertices { _data = (firstShared << 8) | (uint)own.Count },
                _primitives = new hkcdStaticMeshTreeBaseSectionPrimitives { _data = (firstPrim << 8) | (uint)primOrder.Count },
                _dataRuns = new hkcdStaticMeshTreeBaseSectionDataRuns { _data = ((uint)mesh._primitiveDataRuns.Count << 8) | 1u },
                _numPackedVertices = (byte)own.Count,
                _numSharedIndices = (byte)shr.Count,
                _leafIndex = 0,           // filled once the section-level tree is encoded
                _page = 0, _flags = 0, _layerData = 0, _unusedData = 0,
                _domain = ToAabb(secDomain),
            };
            // encode the section tree: node boxes are the decoded vertices of the primitives below them
            Func<int, Box3> leafBox = t => BoxOf(new[] { decoded[local[tris[t].A]], decoded[local[tris[t].B]], decoded[local[tris[t].C]] });
            foreach (var n in EncodeTree(secShape, leafBox, secDomain))
            {
                byte data = n.IsLeaf ? (byte)(primIndexOfTri[n.Item] << 1) : (byte)((n.LeftLeaves << 1) | 1);
                if (!n.IsLeaf && n.LeftLeaves > 127) throw new InvalidOperationException("section tree too large");
                section._nodes.Add(new hkcdStaticTreeCodec3Axis4 { _xyz = n.Xyz, _data = data });
            }
            mesh._sections.Add(section);
            mesh._primitiveDataRuns.Add(new hknpCompressedMeshShapeTreeDataRun
            {
                _value = new hknpCompressedMeshShapeTreeDataRunData { _data = 0 },
                _index = 0,
                _count = (byte)primOrder.Count
            });
        }

        // ---- 5. section-level tree, now that every section's box is known
        Box3[] secBoxes = mesh._sections.Select(sx => new Box3(new Vector3(sx!._domain!._min.X, sx._domain._min.Y, sx._domain._min.Z),
                                                                 new Vector3(sx._domain._max.X, sx._domain._max.Y, sx._domain._max.Z))).ToArray();
        // the shape list was built over group ids; leaves map to sections through sectionOfGroup
        var topNodes = EncodeTree(topShape, g => secBoxes[sectionOfGroup[g]], domain).ToList();
        for (int k = 0; k < topNodes.Count; k++)
        {
            var n = topNodes[k];
            byte hi, lo;
            if (n.IsLeaf)
            {
                int s = sectionOfGroup[n.Item];
                hi = (byte)((s >> 8) & 0x7F); lo = (byte)(s & 0xFF);
                mesh._sections[s]!._leafIndex = (ushort)k;
            }
            else
            {
                hi = (byte)(0x80 | ((n.LeftLeaves >> 8) & 0x7F)); lo = (byte)(n.LeftLeaves & 0xFF);
            }
            mesh._nodes.Add(new hkcdStaticTreeCodec3Axis5 { _xyz = n.Xyz, _hiData = hi, _loData = lo });
        }
        mesh._domain = ToAabb(domain);
        mesh._numPrimitiveKeys = tri3;
        mesh._maxKeyValue = maxKey;
        mesh._bitsPerKey = BitWidth(maxKey);

        var shape = new hknpCompressedMeshShape
        {
            _flags = hknpShape_FlagsEnum.IS_COMPOSITE_SHAPE | hknpShape_FlagsEnum.NO_GET_ALL_SHAPE_KEYS_ON_SPU |
                     hknpShape_FlagsEnum.CONTAINS_ONLY_TRIANGLES,
            _numShapeKeyBits = (byte)mesh._bitsPerKey,
            _dispatchType = hknpCollisionDispatchType_Enum.COMPOSITE,
            _convexRadius = 0f,
            _userData = userData,
            _properties = null,
            _edgeWeldingMap = new hknpSparseCompactMapunsignedshort { _secondaryKeyMask = 0xFFFFFFFF, _sencondaryKeyBits = 0 },
            _shapeTagCodecInfo = 0xFFFFFFFF,
            _data = new hknpCompressedMeshShapeData
            {
                _meshTree = mesh,
                _simdTree = new hkcdSimdTree
                {
                    _domain = new hkAabb
                    {
                        _min = new Vector4(float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue),
                        _max = new Vector4(-float.MaxValue, -float.MaxValue, -float.MaxValue, -float.MaxValue)
                    },
                    _root = 0
                }
            },
            _quadIsFlat = BitField(mesh._primitives.Count, true),
            _triangleIsInternal = BitField(mesh._primitives.Count * 2, false),
        };

        stats.Triangles = tri3;
        stats.Sections = mesh._sections.Count;
        stats.PackedVertices = mesh._packedVertices.Count;
        stats.SharedVertices = mesh._sharedVertices.Count;
        stats.MaxError = maxErr;
        return shape;
    }

    /// <summary>The mesh's AABB (its tree domain), for the Frostbite part table.</summary>
    public static Box3 GetAabb(hknpCompressedMeshShape shape)
    {
        hkAabb d = shape._data!._meshTree!._domain!;
        return new Box3(new Vector3(d._min.X, d._min.Y, d._min.Z), new Vector3(d._max.X, d._max.Y, d._max.Z));
    }

    // ------------------------------------------------------------------ trees

    private sealed class TreeNode
    {
        public bool IsLeaf;
        public int Item;              // leaf: the item (triangle or group id)
        public int LeftLeaves;        // internal: leaves under the left child
        public TreeNode? Left, Right;
    }

    private readonly struct Encoded
    {
        public readonly bool IsLeaf; public readonly int Item; public readonly int LeftLeaves; public readonly byte[] Xyz;
        public Encoded(bool leaf, int item, int left, byte[] xyz) { IsLeaf = leaf; Item = item; LeftLeaves = left; Xyz = xyz; }
    }

    /// <summary>Median-split binary tree over items by centre; records the leaf order.</summary>
    private static void BuildTree(List<int> items, Func<int, Vector3> centre, List<TreeNode> roots, List<int> leafOrder)
    {
        roots.Clear();
        roots.Add(Build(items, centre, leafOrder));
    }

    private static TreeNode Build(List<int> items, Func<int, Vector3> centre, List<int> leafOrder)
    {
        if (items.Count == 1)
        {
            leafOrder.Add(items[0]);
            return new TreeNode { IsLeaf = true, Item = items[0] };
        }
        Box3 cb = BoxOf(items.Select(centre));
        Vector3 ext = cb.Max - cb.Min;
        int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : (ext.Y >= ext.Z ? 1 : 2);
        var sorted = items.OrderBy(i => centre(i)[axis]).ThenBy(i => i).ToList();
        int half = sorted.Count / 2;
        var left = Build(sorted.GetRange(0, half), centre, leafOrder);
        var right = Build(sorted.GetRange(half, sorted.Count - half), centre, leafOrder);
        return new TreeNode { Left = left, Right = right, LeftLeaves = CountLeaves(left) };
    }

    private static int CountLeaves(TreeNode n) => n.IsLeaf ? 1 : CountLeaves(n.Left!) + CountLeaves(n.Right!);

    /// <summary>
    /// Pre-order encoding. Each node's box is coded against its parent's DECODED box (exactly what the runtime walks),
    /// shrunk as far as the 4-bit codes allow while still holding everything below it.
    /// </summary>
    private static IEnumerable<Encoded> EncodeTree(List<TreeNode> roots, Func<int, Box3> leafBox, Box3 rootParent)
    {
        var outList = new List<Encoded>();
        Encode(roots[0], leafBox, rootParent, outList);
        return outList;
    }

    private static Box3 Encode(TreeNode n, Func<int, Box3> leafBox, Box3 parent, List<Encoded> outList)
    {
        Box3 content = Content(n, leafBox);
        byte[] xyz = new byte[3];
        Box3 decoded = parent;
        for (int a = 0; a < 3; a++)
        {
            float pmin = parent.Min[a], pmax = parent.Max[a], s = (pmax - pmin) / 226f;
            float eps = Math.Max(1e-6f, Math.Abs(pmax - pmin) * 1e-6f);
            int hi = 0, lo = 0;
            if (s > 0)
            {
                for (int k = 15; k >= 0; k--) if (pmin + s * k * k <= content.Min[a] - eps) { hi = k; break; }
                for (int k = 15; k >= 0; k--) if (pmax - s * k * k >= content.Max[a] + eps) { lo = k; break; }
            }
            xyz[a] = (byte)((hi << 4) | lo);
            Vector3 mn = decoded.Min, mx = decoded.Max;
            mn[a] = pmin + s * hi * hi;
            mx[a] = pmax - s * lo * lo;
            decoded = new Box3(mn, mx);
        }
        int idx = outList.Count;
        outList.Add(new Encoded(n.IsLeaf, n.Item, n.LeftLeaves, xyz));
        if (!n.IsLeaf)
        {
            Encode(n.Left!, leafBox, decoded, outList);
            Encode(n.Right!, leafBox, decoded, outList);
        }
        return decoded;
    }

    private static Box3 Content(TreeNode n, Func<int, Box3> leafBox)
    {
        if (n.IsLeaf) return leafBox(n.Item);
        Box3 l = Content(n.Left!, leafBox), r = Content(n.Right!, leafBox);
        return new Box3(Vector3.ComponentMin(l.Min, r.Min), Vector3.ComponentMax(l.Max, r.Max));
    }

    // ------------------------------------------------------------------ sections

    private static void Split(List<int> idx, List<(int A, int B, int C)> tris, List<Vector3> verts, List<List<int>> outGroups)
    {
        int uniq = idx.SelectMany(t => new[] { tris[t].A, tris[t].B, tris[t].C }).Distinct().Count();
        Box3 vb = BoxOfTris(idx, tris, verts);
        Vector3 vext = vb.Max - vb.Min;
        bool small = idx.Count == 1 || Math.Max(vext.X, Math.Max(vext.Y, vext.Z)) <= MaxSectionExtent;
        if (idx.Count <= MaxSectionPrims && uniq <= MaxSectionVerts && small)
        {
            outGroups.Add(idx);
            return;
        }
        Func<int, Vector3> c = t => (verts[tris[t].A] + verts[tris[t].B] + verts[tris[t].C]) / 3f;
        Box3 cb = BoxOf(idx.Select(c));
        Vector3 ext = cb.Max - cb.Min;
        int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : (ext.Y >= ext.Z ? 1 : 2);
        var sorted = idx.OrderBy(t => c(t)[axis]).ThenBy(t => t).ToList();
        int half = sorted.Count / 2;
        Split(sorted.GetRange(0, half), tris, verts, outGroups);
        Split(sorted.GetRange(half, sorted.Count - half), tris, verts, outGroups);
    }

    // ------------------------------------------------------------------ helpers

    private static (int, int, int) Canon(int a, int b, int c)
    {
        if (a <= b && a <= c) return (a, b, c);
        if (b <= a && b <= c) return (b, c, a);
        return (c, a, b);
    }

    private static uint Quant(float v, float min, float scale, uint max)
    {
        if (scale <= 0) return 0;
        double q = Math.Round((v - min) / scale);
        return (uint)Math.Clamp(q, 0, max);
    }

    private static ulong Quant64(float v, float min, float ext, ulong max)
    {
        if (ext <= 0) return 0;
        double q = Math.Round((v - min) / ext * max);
        return (ulong)Math.Clamp(q, 0, max);
    }

    private static int BitWidth(uint v)
    {
        int bits = 1;
        while (bits < 32 && (v >> bits) != 0) bits++;
        return bits;
    }

    private static hkBitField BitField(int numBits, bool set)
    {
        var storage = new hkBitFieldStoragehkArrayunsignedinthkContainerHeapAllocator { _numBits = numBits };
        int words = (numBits + 31) / 32;
        for (int w = 0; w < words; w++)
        {
            uint word = 0;
            if (set)
            {
                int bitsHere = Math.Min(32, numBits - w * 32);
                word = bitsHere == 32 ? 0xFFFFFFFFu : (1u << bitsHere) - 1u;
            }
            storage._words.Add(word);
        }
        return new hkBitField { _storage = storage };
    }

    private static Box3 BoxOf(IEnumerable<Vector3> pts)
    {
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (Vector3 p in pts) { mn = Vector3.ComponentMin(mn, p); mx = Vector3.ComponentMax(mx, p); }
        return new Box3(mn, mx);
    }

    private static Box3 BoxOfTris(List<int> idx, List<(int A, int B, int C)> tris, List<Vector3> verts) =>
        BoxOf(idx.SelectMany(t => new[] { verts[tris[t].A], verts[tris[t].B], verts[tris[t].C] }));

    private static Vector3 Centre(Box3 b) => (b.Min + b.Max) * 0.5f;

    private static Box3 Pad(Box3 b, float minExtent)
    {
        Vector3 mn = b.Min, mx = b.Max;
        for (int a = 0; a < 3; a++)
            if (mx[a] - mn[a] < minExtent) { float c = (mx[a] + mn[a]) * 0.5f; mn[a] = c - minExtent * 0.5f; mx[a] = c + minExtent * 0.5f; }
        return new Box3(mn, mx);
    }

    private static hkAabb ToAabb(Box3 b) => new()
    {
        // shipped mesh-tree and section domains carry w = 0.5 in both corners
        _min = new Vector4(b.Min.X, b.Min.Y, b.Min.Z, 0.5f),
        _max = new Vector4(b.Max.X, b.Max.Y, b.Max.Z, 0.5f)
    };
}
