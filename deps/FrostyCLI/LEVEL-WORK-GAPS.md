# What FrostyCLI is missing for Battlefront II level work

Written 2026-08-31 against upstream `b62f86f`. Every "verified" line below was reproduced on
this machine against a real BFII install; everything else is marked.

**One-line summary:** the CLI can read and edit every part of a BFII level *except where the
props actually are*. Static placements are nested two hops down and their transforms live in a
Havok resource the CLI has no reader for. A complete read/write implementation of that resource
already exists in the FrostyLevelEditor plugin and could be ported.

---

## 1. `level list` returns 0 entities on every BFII level — VERIFIED

Reproduced on five assets: `StarKiller_01`, `StarKiller_01/Art_Exterior`, `StarKiller_01/GA_Prison`,
`StarKiller_01/Lobby`, `Naboo_01`. All print `0 entities total`.

**Cause.** `LevelCommand` looks for a list on the *level root* whose element type name contains
`Entity` or `Blueprint`. BFII does not lay levels out that way:

```
LevelData                          11 x SubWorldReferenceObjectData   (pointers only)
 └─ SubWorldData                    Objects[]  ->  internal PointerRefs
     └─ StaticModelGroupEntityData
          └─ MemberDatas[0..263]    StaticModelGroupMemberData        <- the placements
```

`Art_Exterior` holds **264 members**, one per distinct mesh, each with an `InstanceCount`
(observed range 1 to 436). The CLI never descends to them.

**Needed:** recurse `SubWorldReferenceObjectData` into sub-worlds, then walk
`StaticModelGroupEntityData.MemberDatas`.

## 2. No Havok physics resource reader — VERIFIED ABSENT

`grep -rl "Havok|hknp|PhysicsData" FrostyCLI.Core FrostyCLI.Commands` returns only the bundled
`FrostySdk.dll` / `FrostyCore.dll` binaries. **No CLI source touches physics.**

This matters more than it sounds, because of §3.

## 3. Instance transforms have two sources and the CLI can reach only one — VERIFIED

From `LevelEditorPlugin/Entities/Shared/GameShared/StaticModelGroupEntity.cs:166-181`:

```
if (member.InstanceTransforms.Count > 0)   ->  transform = member.InstanceTransforms[i]     (EBX)
else if (physicsData != null)              ->  transform = physicsData.GetTransform(n)      (Havok RES)
```

**In `Art_Exterior` every member observed has `InstanceTransforms: []`** — `container_s_01`
carries `InstanceCount = 204` and zero stored transforms. So that level takes the **second**
branch exclusively.

`ebx set` can write the first source. Nothing in the CLI can read or write the second.

**Consequence:** the CLI cannot move a static prop in a level that stores transforms in physics
data, which appears to be the normal case.

## 4. The reader-writer already exists and is not a research problem

`LevelEditorPlugin/Resources/Physics/HavokPhysicsData.cs` — **1,305 lines**, handling
`hknpCompressedMeshShape`, `hknpStaticCompoundShape`, `hknpConvexPolytopeShape`,
`hknpCapsuleShape`, `hknpSphereShape`, `hknpShapeMassProperties`, `hknpInstance`, `hkpDomain`,
`hkpInstance`.

**It writes as well as reads** — `SetTransform(int, Matrix)` at `:786`,
`SaveInternal(NativeWriter)` at `:802`, plus `SetTransformInMemory` and undo variants at
`:1010-1038`.

Porting it into `FrostyCLI.Core` is a port of working code, not new reverse engineering. The CLI
already has `res <ls|export|import>` and `chunk <export|import>`, so the payload plumbing exists.

## 5. `level add` cannot add a static instance — INFERRED from the data model

Adding one instance to a member is a coordinated multi-array edit, not an append:

| What | Why |
|---|---|
| `InstanceCount` | +1 |
| a transform | into `InstanceTransforms` **or** the Havok resource, per §3 |
| `PhysicsPartRange` | observed `{First, Last}`; adding parts shifts every *later* member's range |
| `NetworkIdRange` | same, and the group carries `NetworkIdCount = 261` |
| `PartBoundingBoxes` | group-level, **4,248** entries indexed by part |
| `InstanceObjectVariation` | parallel per-instance array |

`ebx array-add --prop <list>` appends to one list. It cannot maintain the index ranges that make
the others agree. **Untested** — I did not attempt a write.

## 6. Object variations are resolved through a database the CLI does not consult — VERIFIED in plugin

`StaticModelGroupEntity.cs:183-205` resolves `ObjectVariationHash` against the owning
sub-world's `MeshVariationDatabase`. The CLI has an `mvdb` command for editing that database, but
`level list` does not use it, so a listing cannot report which variation an instance uses.

## 7. No sub-world traversal — VERIFIED

`level list <asset>` operates on exactly the asset named. A BFII level is 11 sub-world
references; listing "the level" requires following them. `level scaffold` does clone sub-world
structures, so the concept exists in the codebase — it is just not used for reading.

## 8. No layer model — VERIFIED absent

The editor presents `static_instances`, `Default`, `Setup`, `Sound`, `GameModes/<mode>/...` as
layers. Nothing in the CLI has a layer concept, so there is no way to scope an operation to one.

## 9. Only one placement family is even attempted — INFERRED

The plugin carries **196 entity classes** (`Entities/`), including `StaticModelEntity`,
`StaticModelGroupEntity`, `TerrainEntity`, `GameSplineEntity`. The CLI models none of them; its
`level add` constructs whatever type the target list already holds. Prefabs
(`SpatialPrefabBlueprint`), spline entities and terrain entities are unhandled.

---

## Ranked, by what unblocks the most

1. **Port `HavokPhysicsData`** into `FrostyCLI.Core`. Unblocks §2, §3 and most of §5, and it is
   existing code.
2. **Teach `level list` to descend** sub-worlds and static model groups, resolving transforms
   from both sources (§1, §7). Read-only, so it is safe to land first and it makes every later
   change testable.
3. **`level move`** for a group instance — write one transform back through whichever source
   owns it. Smallest useful write.
4. **`level add`** for a group instance, with the index-range bookkeeping of §5.
5. Variations (§6), layers (§8), other entity families (§9).

**Suggested first PR: items 1 and 2 together.** Read-only, independently verifiable against a
shipped level, and it turns `level list` from "returns 0 on every BFII level" into something
that works — which is a defensible unit on its own.

---

## What is NOT missing

Worth stating so nobody rebuilds it: `ebx dump --deep` already resolves internal `PointerRef`s
correctly. Without `--deep` a BFII level reads as almost empty (316 lines for `Art_Exterior`);
with it, 276,178. This tripped me up before I read the upstream source, and it is the single
most useful flag for level inspection.
