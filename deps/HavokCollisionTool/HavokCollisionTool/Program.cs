using Frosty.Sdk.IO;
using FrostyHavokPlugin;
using hk;
using HavokFb;
using Box3 = OpenTK.Mathematics.Box3;

namespace HavokCollisionTool;

/// <summary>
/// Converts between SWBF2 HavokPhysicsData res assets (res type 0x91043F65) and
/// Wavefront OBJ.
///
/// Runs out-of-process from the Frosty 1.0.6.3 plugin: the res payload is net8.0
/// code (vendored from MagixGames/FrostyHavokPlugin@7f5dba4) and the editor is
/// net481, so they cannot share an AppDomain.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitFailed = 3;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "export":
                    if (args.Length != 4) return Usage();
                    return Export(args[1], args[2], args[3]);

                case "import":
                    return ParseImport(args);

                case "inspect":
                    if (args.Length != 3) return Usage();
                    return Inspect(args[1], args[2]);

                case "dumpxml":
                    if (args.Length != 4) return Usage();
                    return DumpXml(args[1], args[2], args[3]);

                case "selftest":
                    return SelfTest();

                default:
                    return Usage();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");

            // Stack traces embed the build machine's source paths, which are noise to
            // anyone running this and leak the author's directory layout. Opt in with
            // HAVOKTOOL_VERBOSE=1 when actually debugging.
            if (Environment.GetEnvironmentVariable("HAVOKTOOL_VERBOSE") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }

            return ExitFailed;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            HavokCollisionTool — SWBF2 HavokPhysicsData <-> OBJ

              export  <res.bin> <resmeta.bin> <out.obj>
                  Writes every shape in the res, plus its part AABBs, as OBJ objects.

              import [<mode>] [--hull=<auto|always|never>] <in.obj> <template-res.bin> <template-resmeta.bin> <out-res.bin> <out-resmeta.bin>
                  Rebuilds collision from the OBJ, keeping every other field of the
                  template res (materials, part table, packfile header) intact.

                  mode is one of:
                    convex      one loose convex polytope per OBJ object, replacing the
                                whole shape list. Shapes are read by position (0 stand on,
                                1 weapon fire, 2 character collision), so extra pieces are
                                inert or wrong. Right for a single piece; prefer compound.
                    compound    all OBJ objects become children of one static compound
                                in slot 0. This is what the game itself uses for
                                multi-piece collision, and the only mode where you can
                                stand on every piece. (default)
                    compressed  writes the geometry into slot 1, the shape weapon fire
                                collides with. Never changes what you stand on.
                    both        slot 0 and slot 1, from the same OBJ.
                    mesh        exact triangle collision: every face becomes part of an
                        hknpCompressedMeshShape written to slot 0 (stand on) and
                        slot 1 (weapon fire), the way shipped stairs and cave walls
                        are built. Flat sheets are kept; no hulls are made.

                  --hull=auto|always|never   (default auto)
                    A convex polytope stores face planes and cannot hold a dent, so a
                    concave piece collides as its hull whether you asked for one or not.
                    auto   build the hull for concave pieces only, and say so.
                    always build it for every piece.
                    never  keep the geometry as authored; concavity is still reported.

              inspect <res.bin> <resmeta.bin>
                  Prints the shape tree and the Frostbite part tables. Start here when
                  an import behaves oddly in game.

              dumpxml <res.bin> <resmeta.bin> <out.xml>
                  Dumps the deserialised Havok object graph for inspection.

              selftest
                  Builds a unit cube shape and a compound AABB tree and checks both
                  against the invariants the runtime relies on. Needs no game data.
            """);
        return ExitUsage;
    }

    #region Shape slots

    /// <summary>
    /// A SWBF2 collision res holds its shapes in a fixed order, confirmed against
    /// shipped assets (objects/props/objectsets/starkiller/crate_01 and
    /// levels/mp/starkiller_01/objects/skb_entrancehall_01, both
    /// [convex-or-compound, compressed mesh]):
    ///
    ///   slot 0 — the body's physics shape. What characters and vehicles stand on and
    ///            bump into. One shape, so several pieces have to be one compound.
    ///   slot 1 — the detailed shape weapon fire is traced against.
    ///   slot 2 — character-only collision, on assets that have one.
    ///
    /// The engine reads the slots by position; it does not iterate the list looking for
    /// more collision. That is why a flat list of N convex shapes leaves only the first
    /// standable, turns the second into the weapon-collision shape, and makes the rest
    /// something nobody asked for.
    ///
    /// A level's StaticModelGroup res is laid out differently again: its root slots are
    /// placement arrays, which import refuses to overwrite (see InstanceRecords).
    /// </summary>
    private const int PhysicsSlot = 0;
    private const int BulletSlot = 1;

    private enum ImportMode
    {
        Convex,
        Compound,
        Compressed,
        Both,
        Mesh
    }

    private static int ParseImport(string[] args)
    {
        ConvexHull.Mode hull = ConvexHull.Mode.Auto;
        List<string> positional = new();

        foreach (string arg in args.Skip(1))
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            const string HullOption = "--hull=";
            if (!arg.StartsWith(HullOption, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"error: unknown option '{arg}'.");
                return Usage();
            }

            hull = ConvexHull.Parse(arg.Substring(HullOption.Length));
        }

        // `import <5 paths>` keeps working and means compound, so existing scripts do
        // not silently keep producing the flat list that caused the inert-shape bug.
        ImportMode mode = ImportMode.Compound;
        int first = 0;

        if (positional.Count == 6)
        {
            if (!TryParseMode(positional[0], out mode))
            {
                Console.Error.WriteLine($"error: unknown import mode '{positional[0]}'.");
                return Usage();
            }
            first = 1;
        }
        else if (positional.Count != 5)
        {
            return Usage();
        }

        return Import(mode, hull, positional[first], positional[first + 1], positional[first + 2],
                      positional[first + 3], positional[first + 4]);
    }

    private static bool TryParseMode(string value, out ImportMode mode)
    {
        switch (value.ToLowerInvariant())
        {
            case "convex": mode = ImportMode.Convex; return true;
            case "compound": mode = ImportMode.Compound; return true;
            case "compressed": mode = ImportMode.Compressed; return true;
            case "both": mode = ImportMode.Both; return true;
            case "mesh": mode = ImportMode.Mesh; return true;
            default: mode = ImportMode.Compound; return false;
        }
    }

    #endregion

    private static HavokPhysicsData Load(string resPath, string metaPath)
    {
        byte[] meta = File.ReadAllBytes(metaPath);
        if (meta.Length < 16)
        {
            throw new InvalidDataException($"res meta is {meta.Length} bytes; expected at least 16.");
        }

        HavokPhysicsData data = new();
        using DataStream stream = new(File.OpenRead(resPath));
        data.Deserialize(stream, meta);
        return data;
    }

    private static HavokPhysicsContainer GetContainer(HavokPhysicsData data)
    {
        if (data.m_obj is not hkRootLevelContainer root || root._namedVariants.Count == 0)
        {
            throw new InvalidDataException("Res has no hkRootLevelContainer root.");
        }

        if (root._namedVariants[0]?._variant is not HavokPhysicsContainer container)
        {
            throw new InvalidDataException("Res root variant is not a HavokPhysicsContainer.");
        }

        return container;
    }

    private static int SelfTest() => CubeSelfTest.Run();

    private static int Export(string resPath, string metaPath, string objPath)
    {
        HavokPhysicsData data = Load(resPath, metaPath);
        HavokPhysicsContainer container = GetContainer(data);

        data.WriteToOBJ(objPath);
        Console.WriteLine($"exported {data.PartCount} part(s) to {objPath}");

        // The OBJ writer only knows how to walk convex polytopes and compounds of them.
        // A compressed mesh — normally the weapon-fire shape — is skipped silently, and
        // someone who then re-imports the OBJ would think they had round-tripped the
        // whole asset. Say so instead.
        for (int i = 0; i < container._shapes.Count; i++)
        {
            if (container._shapes[i] is hknpCompressedMeshShape)
            {
                Console.WriteLine(
                    $"  note: shape {i} is a compressed mesh and is NOT in the OBJ — this exporter " +
                    "cannot read that format. It stays untouched unless you import over it.");
            }
        }

        return ExitOk;
    }

    private static int DumpXml(string resPath, string metaPath, string xmlPath)
    {
        HavokPhysicsData data = Load(resPath, metaPath);
        data.SerializeXML(xmlPath);

        Console.WriteLine($"wrote {xmlPath}");
        return ExitOk;
    }

    #region Inspect

    private static int Inspect(string resPath, string metaPath)
    {
        HavokPhysicsData data = Load(resPath, metaPath);
        HavokPhysicsContainer container = GetContainer(data);

        Console.WriteLine("Frostbite part tables");
        Console.WriteLine($"  PartCount               {data.PartCount}");
        Console.WriteLine($"  PartTranslations        {data.PartTranslations.Count}");
        Console.WriteLine($"  LocalAabbs              {data.LocalAabbs.Count}");
        Console.WriteLine($"  MaterialIndices         {data.MaterialIndices.Count}" +
                          (data.MaterialIndices.Count > 0
                              ? $"  [{string.Join(" ", data.MaterialIndices.Take(16))}" +
                                (data.MaterialIndices.Count > 16 ? " ...]" : "]")
                              : string.Empty));
        Console.WriteLine($"  MaterialFlagsAndIndices {data.MaterialFlagsAndIndices.Count}");
        Console.WriteLine($"  DetailResourceIndices   {data.DetailResourceIndices.Count}");
        Console.WriteLine($"  MaterialCountUsed       {data.MaterialCountUsed}");
        Console.WriteLine($"  HighestMaterialIndex    {data.HighestMaterialIndex}");
        Console.WriteLine();

        Console.WriteLine($"Shapes ({container._shapes.Count})");
        for (int i = 0; i < container._shapes.Count; i++)
        {
            string role = container._shapes[i] is hknpStaticCompoundShape placement &&
                          InstanceRecords.IsPlacementArray(placement)
                ? "level placement array - positions other objects' collision; never import over it"
                : i switch
                {
                    PhysicsSlot => "physics / standable",
                    BulletSlot => "weapon fire",
                    2 => "character collision, where an asset has it",
                    _ => "not read by the engine"
                };

            Console.WriteLine($"  [{i}] {role}");
            Describe(container._shapes[i], "      ");
        }

        return ExitOk;
    }

    private static void Describe(hknpShape? shape, string indent)
    {
        if (shape is null)
        {
            Console.WriteLine($"{indent}(null)");
            return;
        }

        Console.WriteLine($"{indent}{shape.GetType().Name}");
        Console.WriteLine($"{indent}  flags            {shape._flags}");
        Console.WriteLine($"{indent}  dispatchType     {shape._dispatchType}");
        Console.WriteLine($"{indent}  numShapeKeyBits  {shape._numShapeKeyBits}");
        Console.WriteLine($"{indent}  userData         0x{shape._userData:X}" +
                          (IsInheritUserData(shape._userData)
                              ? "  (material: inherit)"
                              : $"  (material {shape._userData >> 16}, part {shape._userData & 0xFFFF})"));

        switch (shape)
        {
            case hknpConvexPolytopeShape convex:
                Console.WriteLine($"{indent}  vertices         {convex._vertices.Count} (padded)");
                Console.WriteLine($"{indent}  faces            {convex._faces.Count}");
                if (convex._properties?._entries?.FirstOrDefault(e => e?._object is hknpShapeMassProperties) is { } mass)
                {
                    Console.WriteLine($"{indent}  mass props key   0x{mass._key:X4}" +
                                      (mass._key != MassProperties.MassPropertiesKey
                                          ? "  (WRONG - written by an earlier version; import it again)"
                                          : string.Empty));
                }
                break;

            case hknpCompoundShape compound:
                int count = compound._instances?._elements.Count ?? 0;
                int nodes = (compound as hknpStaticCompoundShape)
                            ?._boundingVolumeData?._aabbTree?._nodes.Count ?? 0;
                Console.WriteLine($"{indent}  instances        {count}");
                Console.WriteLine($"{indent}  aabbTree nodes   {nodes}" +
                                  (count > 0 && nodes != count * 2 - 1
                                      ? $"  (expected {count * 2 - 1})"
                                      : string.Empty));
                // Shipped records always pack flags, the shape's size and a tree back-link into
                // the transform's w lanes. A compound written by an earlier version of this tool
                // has none, which is worth seeing at a glance when diagnosing an old import.
                int packed = 0;
                for (int i = 0; i < count; i++)
                {
                    if (compound._instances!._elements[i] is { } record &&
                        InstanceRecords.IsInt24(record._transform.M14))
                    {
                        packed++;
                    }
                }

                Console.WriteLine($"{indent}  packed records   {packed}/{count}" +
                                  (packed < count
                                      ? "  (INCOMPLETE - written by an earlier version; import it again)"
                                      : string.Empty));

                // A level's placement array runs to thousands of records, and listing all of
                // them buries everything else in the report.
                const int Shown = 8;
                for (int i = 0; i < Math.Min(count, Shown); i++)
                {
                    hknpShapeInstance? instance = compound._instances!._elements[i];
                    Console.WriteLine($"{indent}  instance {i} shapeTag={instance?._shapeTag}");
                    Describe(instance?._shape, indent + "    ");
                }

                if (count > Shown)
                {
                    Console.WriteLine($"{indent}  ... and {count - Shown} more instance(s)");
                }
                break;
        }
    }

    #endregion

    #region Import

    private static int Import(ImportMode mode, ConvexHull.Mode hull, string objPath,
                              string templateResPath, string templateMetaPath,
                              string outResPath, string outMetaPath)
    {
        // The template supplies the packfile header, the Frostbite-side part/material
        // tables and the container object. Only the shapes are rebuilt: inventing a
        // whole HavokPhysicsData from nothing produces a res the game rejects.
        HavokPhysicsData data = Load(templateResPath, templateMetaPath);
        HavokPhysicsContainer container = GetContainer(data);

        // Checked before any geometry work: a level's group res looks like a prop res from the
        // outside, and overwriting one of its placement arrays loads without error while
        // detaching every object in the group from its collision.
        int[] written = mode switch
        {
            ImportMode.Convex => Enumerable.Range(0, container._shapes.Count).ToArray(),
            ImportMode.Compound => new[] { PhysicsSlot },
            ImportMode.Compressed => new[] { BulletSlot },
            ImportMode.Mesh => new[] { PhysicsSlot, BulletSlot },
            _ => new[] { PhysicsSlot, BulletSlot }
        };
        InstanceRecords.RefuseToReplacePlacementArrays(container, written);

        List<WavefrontObj> objects = ReadObjects(objPath);

        Console.WriteLine($"mode:     {mode.ToString().ToLowerInvariant()}");
        Console.WriteLine($"hull:     {hull.ToString().ToLowerInvariant()}");
        Console.WriteLine($"template: {container._shapes.Count} shape(s), {data.PartCount} part(s)");
        Console.WriteLine($"obj:      {objects.Count} object(s)");

        // Every convex leaf the import writes inherits the template's leaf user data,
        // whose high 16 bits are the physics material. Building with 0 instead — which
        // is what this did before — quietly moved every imported surface onto material 0,
        // changing footstep sounds, impact effects and penetration.
        ulong leafUserData = FindLeafUserData(container);
        Console.WriteLine($"material: inheriting user data 0x{leafUserData:X} from the template");

        if (mode == ImportMode.Mesh)
        {
            // Exact triangle collision: every face of every OBJ object, as an hknpCompressedMeshShape in the slot you
            // stand on and the one weapon fire hits (shipped standable meshes: Hoth snow cave, Kashyyyk and Vardos
            // stairs). Flat sheets stay: a triangle mesh is surfaces, it needs no thickness.
            var positions = new List<OpenTK.Mathematics.Vector3>();
            var tris = new List<int>();
            foreach (WavefrontObj o in objects)
            {
                int baseIndex = positions.Count;
                foreach (System.Numerics.Vector4 v in o._vertices) positions.Add(new OpenTK.Mathematics.Vector3(v.X, v.Y, v.Z));
                foreach (WavefrontObj.Face f in o._faces)
                    foreach (WavefrontObj.Face t in f.AsTris())
                        foreach (int vi in t.Get()) tris.Add(baseIndex + vi);
            }
            hknpCompressedMeshShape standable = CompressedMeshBuilder.Build(positions, tris, leafUserData, out var st);
            hknpCompressedMeshShape bullet = CompressedMeshBuilder.Build(positions, tris, 0xFFFF0000UL, out _);
            SetSlot(container, PhysicsSlot, standable);
            SetSlot(container, BulletSlot, bullet);
            Console.WriteLine($"  mesh: {st.Triangles} triangle(s) in {st.Sections} section(s), {st.PackedVertices} packed + " +
                              $"{st.SharedVertices} shared vertices, max quantisation error {st.MaxError * 1000:0.##} mm, " +
                              $"{st.Dropped} degenerate/duplicate triangle(s) dropped");
            Console.WriteLine("  wrote shape 0 (what you stand on) and shape 1 (what weapon fire hits) as triangle meshes.");
            RefreshLocalAabbs(data, container);
            InstanceRecords.FillShapeSizes(data, container);
            byte[] meshMeta = File.ReadAllBytes(templateMetaPath);
            using (DataStream meshOut = new(File.Create(outResPath)))
            {
                data.Serialize(meshOut, meshMeta);
            }
            File.WriteAllBytes(outMetaPath, meshMeta);
            Console.WriteLine($"wrote {outResPath} ({new FileInfo(outResPath).Length} bytes) and {outMetaPath}");
            return ExitOk;
        }

        // Collected before anything is replaced — once a slot is overwritten its old
        // shapes are gone, and `both` writes two slots from the same donors.
        List<hknpShape> donors = CollectConvexLeaves(container);

        // Hulling is decided once, before any shape is built, so `both` cannot report the
        // same piece as concave twice. Pieces with no thickness come back null.
        objects = objects
            .Select((o, i) => ConvexHull.Prepare(o, i, hull))
            .Where(o => o is not null)
            .ToList()!;

        if (objects.Count == 0)
        {
            throw new InvalidDataException(
                "Every piece was flat, so there is nothing to collide with. Collision needs " +
                "closed geometry with thickness, not single-sided planes.");
        }

        switch (mode)
        {
            case ImportMode.Convex:
                BuildFlat(container, objects, donors, leafUserData);
                break;

            case ImportMode.Compound:
                SetSlot(container, PhysicsSlot, BuildShape(objects, donors, leafUserData));
                break;

            case ImportMode.Compressed:
                SetSlot(container, BulletSlot, BuildShape(objects, donors, leafUserData));
                break;

            case ImportMode.Both:
                // Two independent object graphs rather than the same shape referenced
                // twice: the two slots are meant to stay separately editable, and a
                // shared subgraph would make an edit to one silently change the other.
                SetSlot(container, PhysicsSlot, BuildShape(objects, donors, leafUserData));
                SetSlot(container, BulletSlot, BuildShape(objects, donors, leafUserData));
                break;
        }

        // Say which shape actually changed. Every mode wraps several pieces in a compound, so
        // the log reads the same for all of them — and a compressed import, which only touches
        // what weapon fire hits, was easy to mistake for one that makes things walkable.
        Console.WriteLine(mode switch
        {
            ImportMode.Convex => "  wrote the whole shape list.",
            ImportMode.Compound => "  wrote shape 0 — what you stand on. Shape 1, what weapon fire hits, is unchanged.",
            ImportMode.Compressed => "  wrote shape 1 — what weapon fire hits. Shape 0, what you stand on, is UNCHANGED: " +
                                     "this import cannot make anything walkable. Use the Convex Compound import for that.",
            _ => "  wrote shape 0 (what you stand on) and shape 1 (what weapon fire hits)."
        });

        RefreshLocalAabbs(data, container);

        // An import rewrites every shape, including slots it did not touch, so whatever an
        // earlier build of this tool wrote survives unless it is repaired here — reproduced with
        // a compressed import leaving an old shape-0 compound broken.
        int legacyRecords = 0;
        int legacyKeys = 0;
        foreach (hknpShape? shape in container._shapes)
        {
            legacyKeys += MassProperties.RepairKeys(shape);
            if (shape is hknpStaticCompoundShape compound)
            {
                legacyRecords += InstanceRecords.RepairLegacyRecords(compound);
            }
        }

        if (legacyRecords > 0 || legacyKeys > 0)
        {
            Console.WriteLine(
                $"  repaired data an earlier build wrote: {legacyRecords} compound record(s), " +
                $"{legacyKeys} mass-properties key(s).");
        }

        // Last, because it measures the shapes exactly as they are about to be serialised.
        InstanceRecords.FillShapeSizes(data, container);

        byte[] outMeta = File.ReadAllBytes(templateMetaPath);
        using (DataStream outStream = new(File.Create(outResPath)))
        {
            data.Serialize(outStream, outMeta);
        }
        File.WriteAllBytes(outMetaPath, outMeta);

        Console.WriteLine($"wrote {outResPath} ({new FileInfo(outResPath).Length} bytes) and {outMetaPath}");
        return ExitOk;
    }

    private static List<WavefrontObj> ReadObjects(string objPath)
    {
        List<WavefrontObj> all = WavefrontObj.ReadFromFile(objPath);

        // Export writes the bounding boxes alongside the collision so they can be seen in
        // a 3D app: one per part ("localaabb-N"), and for a compound the tree's domain
        // and per-child boxes ("0-domain", "0-aabb-N"). They are wireframe helpers, not
        // shapes — importing them would add a box-shaped collider around everything —
        // and every AABB is recomputed from the geometry anyway, so drop them.
        //
        // The name check is first because it can say *why* something was dropped; the
        // face check is the backstop, since these helpers are drawn with `l` elements and
        // so carry no faces at all.
        List<WavefrontObj> objects = new(all.Count);
        int skipped = 0;

        foreach (WavefrontObj obj in all)
        {
            if (IsBoundsHelper(obj.Name) || obj._faces.Count == 0)
            {
                skipped++;
                continue;
            }

            objects.Add(obj);
        }

        if (skipped > 0)
        {
            Console.WriteLine($"  skipping {skipped} bounding-box helper object(s); they are not collision shapes.");
        }

        if (objects.Count == 0)
        {
            throw new InvalidDataException(
                $"{objPath} contains no collision shapes. " +
                "Name each convex piece anything other than 'localaabb...'.");
        }

        objects = KeepFinestLod(objects);

        // Unreal's convention, and worth honouring because people already know it: if the
        // file carries objects named UCX_*, those ARE the collision and the rest is the
        // render mesh. It means one exported file can hold both, which is what makes
        // generating collision straight from a mesh asset practical.
        List<WavefrontObj> ucx = objects
            .Where(o => o.Name.StartsWith("UCX_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ucx.Count > 0 && ucx.Count < objects.Count)
        {
            Console.WriteLine(
                $"  using {ucx.Count} UCX_ object(s) as the collision and ignoring the other " +
                $"{objects.Count - ucx.Count} as render geometry.");
            return ucx;
        }

        return objects;
    }

    /// <summary>
    /// Drops every LOD but the finest.
    ///
    /// Frosty's mesh exporter writes every level of detail into one file, tagging each
    /// group <c>&lt;section&gt;:lod&lt;N&gt;</c> — a crate comes out as seven groups covering four
    /// LODs. Importing that as collision stacks four copies of the same object on top of
    /// each other. Which LOD is kept barely matters once each piece is hulled, since they
    /// share a silhouette; the finest is used because it is the one guaranteed not to have
    /// been shrunk by simplification.
    ///
    /// Groups with no LOD tag are always kept, so a hand-authored file is untouched.
    /// </summary>
    private static List<WavefrontObj> KeepFinestLod(List<WavefrontObj> objects)
    {
        int finest = int.MaxValue;
        foreach (WavefrontObj obj in objects)
        {
            if (TryGetLod(obj.Name, out int lod))
            {
                finest = Math.Min(finest, lod);
            }
        }

        if (finest == int.MaxValue)
        {
            return objects;
        }

        List<WavefrontObj> kept = objects
            .Where(o => !TryGetLod(o.Name, out int lod) || lod == finest)
            .ToList();

        if (kept.Count < objects.Count)
        {
            Console.WriteLine(
                $"  using lod{finest} only; ignoring {objects.Count - kept.Count} group(s) " +
                "from coarser LODs.");
        }

        return kept;
    }

    private static bool TryGetLod(string name, out int lod)
    {
        lod = 0;

        int marker = name.LastIndexOf(":lod", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && int.TryParse(name.Substring(marker + 4), out lod);
    }

    /// <summary>
    /// Names <c>WriteToOBJ</c> and <c>ShapeExtensions.ExportAabbs</c> give the wireframe
    /// boxes they draw next to the real geometry.
    /// </summary>
    private static bool IsBoundsHelper(string name) =>
        name.StartsWith("localaabb", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("-domain", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("-aabb-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the shape for one slot: a bare convex polytope for a single OBJ object,
    /// matching how the game ships single-piece collision, or a static compound over all
    /// of them.
    /// </summary>
    private static hknpShape BuildShape(List<WavefrontObj> objects, IReadOnlyList<hknpShape> donors,
                                        ulong leafUserData)
    {
        List<hknpShape> children = new(objects.Count);

        for (int i = 0; i < objects.Count; i++)
        {
            children.Add(BuildConvex(objects[i], i, Donor(donors, i), leafUserData));
        }

        if (children.Count == 1)
        {
            return children[0];
        }

        // Deliberately says nothing about collision: which shape this becomes is the caller's
        // call, and "so all of them collide" read as a promise even when the compound was
        // going into the weapon-fire slot.
        Console.WriteLine($"  wrapping {children.Count} pieces in one static compound shape.");
        return CompoundBuilder.Build(children, CompoundBuilder.InheritUserData);
    }

    /// <summary>
    /// Replaces the whole shape list with one loose convex polytope per OBJ object.
    /// Only slot 0 is the body's physics shape, so this is the mode that produced the
    /// "you can only stand on the first piece" behaviour; it stays available because it
    /// is still the right shape for a single-piece mesh.
    /// </summary>
    private static void BuildFlat(HavokPhysicsContainer container, List<WavefrontObj> objects,
                                  IReadOnlyList<hknpShape> donors, ulong leafUserData)
    {
        List<hknpShape?> shapes = new(objects.Count);

        for (int i = 0; i < objects.Count; i++)
        {
            shapes.Add(BuildConvex(objects[i], i, Donor(donors, i), leafUserData));
        }

        if (objects.Count > 1)
        {
            Console.WriteLine(
                $"  warning: {objects.Count} loose shapes. The engine reads shapes by position: " +
                "0 is what you stand on, 1 is what weapon fire hits, 2 is character-only collision " +
                "where an asset has it, and anything after that collides with nothing. " +
                "Use compound mode instead.");
        }

        container._shapes = shapes;
    }

    private static hknpConvexPolytopeShape BuildConvex(WavefrontObj obj, int index, hknpShape? template,
                                                       ulong userData)
    {
        if (template is not null and not hknpConvexPolytopeShape)
        {
            Console.WriteLine(
                $"  note: piece {index} takes its mass properties from a " +
                $"{template.GetType().Name} in the template.");
        }

        hknpConvexPolytopeShape shape = ShapeBuilder.BuildConvexShape(obj, template, userData);

        if (!MassProperties.HasTemplateProperties(template))
        {
            Console.WriteLine(
                $"  warning: piece {index} has no template mass properties; " +
                "inertia is left at an identity frame (fine for static collision).");
        }

        Console.WriteLine($"  piece {index}: {obj._vertices.Count} verts, {obj._faces.Count} faces");
        return shape;
    }

    /// <summary>
    /// Every convex leaf in the template, depth first, whatever slot or compound it sits
    /// in. These are the only shapes carrying mass properties, so they are the donors a
    /// rebuilt piece inherits density from.
    ///
    /// Searching the whole template rather than just the slot being written matters for
    /// the weapon-fire slot: it normally holds a compressed mesh, which has no mass
    /// properties at all, so a slot-local search would find nothing and every piece would
    /// fall back to a made-up density.
    /// </summary>
    private static List<hknpShape> CollectConvexLeaves(HavokPhysicsContainer container)
    {
        List<hknpShape> leaves = new();

        void Visit(hknpShape? shape)
        {
            switch (shape)
            {
                case hknpConvexShape convex:
                    leaves.Add(convex);
                    break;

                case hknpCompoundShape compound when compound._instances is not null:
                    foreach (hknpShapeInstance? instance in compound._instances._elements)
                    {
                        Visit(instance?._shape);
                    }
                    break;
            }
        }

        foreach (hknpShape? shape in container._shapes)
        {
            Visit(shape);
        }

        return leaves;
    }

    /// <summary>
    /// Pairs piece <paramref name="index"/> with a donor by position, falling back to the
    /// first donor once the new mesh has more pieces than the old one.
    /// </summary>
    private static hknpShape? Donor(IReadOnlyList<hknpShape> donors, int index) =>
        donors.Count == 0 ? null : donors[index < donors.Count ? index : 0];

    /// <summary>
    /// Writes a shape into a slot, appending when the template does not have that slot
    /// yet. Never leaves a hole: a null entry in the list is a null pointer in the res.
    /// </summary>
    private static void SetSlot(HavokPhysicsContainer container, int slot, hknpShape shape)
    {
        if (slot < container._shapes.Count)
        {
            container._shapes[slot] = shape;
            return;
        }

        container._shapes.Add(shape);
        Console.WriteLine($"  template had no slot {slot}; appended it as shape " +
                          $"{container._shapes.Count - 1}.");
    }

    /// <summary>
    /// First convex leaf user data in the template, depth first.
    ///
    /// Composite roots carry the 0xFFFF material sentinel meaning "inherit", so they are
    /// skipped — copying that onto a leaf would leave the surface with no material at all.
    /// </summary>
    private static ulong FindLeafUserData(HavokPhysicsContainer container)
    {
        foreach (hknpShape? shape in container._shapes)
        {
            if (TryFindLeafUserData(shape, out ulong userData))
            {
                return userData;
            }
        }

        Console.WriteLine("  warning: no convex leaf in the template to inherit a material from; " +
                          "using material 0.");
        return 0;
    }

    private static bool TryFindLeafUserData(hknpShape? shape, out ulong userData)
    {
        userData = 0;

        switch (shape)
        {
            case hknpConvexShape convex when !IsInheritUserData(convex._userData):
                userData = convex._userData;
                return true;

            case hknpCompoundShape compound when compound._instances is not null:
                foreach (hknpShapeInstance? instance in compound._instances._elements)
                {
                    if (TryFindLeafUserData(instance?._shape, out userData))
                    {
                        return true;
                    }
                }
                return false;

            default:
                return false;
        }
    }

    private static bool IsInheritUserData(ulong userData) => (userData >> 16 & 0xFFFF) == 0xFFFF;

    /// <summary>
    /// Refreshes the Frostbite-side per-part AABB for the physics slot.
    ///
    /// The part tables are deliberately not resized. They are indexed by *part*, and the
    /// part a shape resolves to comes from shape tags walked down the tree, not from its
    /// position in the shape list — so a compound of ten pieces is still one part.
    /// Resizing them to the shape count, which this did before, desynchronised the part
    /// table from the material table.
    /// </summary>
    private static void RefreshLocalAabbs(HavokPhysicsData data, HavokPhysicsContainer container)
    {
        if (data.LocalAabbs.Count == 0)
        {
            return;
        }

        hknpShape? physics = container._shapes.Count > PhysicsSlot ? container._shapes[PhysicsSlot] : null;
        if (physics is null)
        {
            return;
        }

        Box3 aabb = CompoundBuilder.GetAabb(physics);
        WarnOnScaleJump(data.LocalAabbs[0], aabb);
        data.LocalAabbs[0] = aabb;

        if (data.LocalAabbs.Count > 1)
        {
            Console.WriteLine(
                $"  warning: this asset has {data.LocalAabbs.Count} parts. Only part 0's AABB was " +
                "updated; the imported geometry is all on part 0, so the other parts keep their " +
                "original bounds.");
        }
    }

    /// <summary>
    /// Flags a new shape that is wildly bigger or smaller than the one it replaced.
    ///
    /// Collision is authored in the same units as the res it goes into, and the usual way
    /// to get that wrong by a factor of a hundred is exporting a mesh in centimetres when
    /// the game works in metres. That mistake produces a res that loads fine and is
    /// completely wrong in game, so it is worth a line in the log.
    /// </summary>
    private static void WarnOnScaleJump(Box3 before, Box3 after)
    {
        float oldSize = (before.Max - before.Min).Length;
        float newSize = (after.Max - after.Min).Length;

        if (oldSize <= 1e-4f || newSize <= 1e-4f)
        {
            return;
        }

        float ratio = newSize / oldSize;
        if (ratio is > 0.1f and < 10f)
        {
            return;
        }

        Console.WriteLine(
            $"  WARNING: the new collision is {ratio:0.##}x the size of what it replaced " +
            $"({oldSize:0.###} -> {newSize:0.###} units across). If that was not deliberate, " +
            "the source geometry is probably in the wrong units — Frostbite works in metres.");
    }

    #endregion
}
