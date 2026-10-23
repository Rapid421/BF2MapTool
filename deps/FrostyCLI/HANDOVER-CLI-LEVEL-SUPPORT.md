# Handover — add Battlefront II level support to FrostyCLI

Paste this whole file as your first message to a fresh session. It is self-contained: nothing
below depends on prior conversation.

---

## What you are doing

`FrostyCLI` is a headless command-line Frosty Editor. Its `level` commands **do not work on
Star Wars Battlefront II (2017)** — `level list` returns `0 entities` on every BFII level. The
goal is to fix that and open a pull request.

Repo: **https://github.com/alexiahartzell/frostycli**

---

## READ THIS FIRST — a licensing constraint that changes the approach

The working reference implementation for the hard part lives in the **FrostyLevelEditor plugin**,
which is licensed **CC BY-NC-ND 4.0** — Attribution, **NonCommercial, NoDerivatives**.

**NoDerivatives means you may not copy that code into FrostyCLI and distribute the result.**
Its `README.md` states the licence; `LICENSE.txt` sits at that repo's root.

So:

- **Read it to understand the file format.** Facts about a binary layout are not copyrightable.
- **Write your own implementation** from that understanding. Do not copy, transliterate, or
  translate its source.
- Do not paste its code into a PR.
- If you want to reuse it directly, someone must ask the plugin's author for permission first.
- `frostycli` itself has **no LICENSE file at its repo root** — worth checking with the
  maintainer before contributing anything derived from third-party code.

Everything below is written on the assumption of a clean-room reimplementation.

---

## Environment — all verified working on this machine

| Thing | Where / state |
|---|---|
| Upstream clone | `F:\Frosty\frostycli-pr` — HEAD was `b62f86f` (2026-06-27) |
| Build | `dotnet build FrostyCLI.sln -c Release -p:Platform=x64` — succeeds, 0 errors |
| Built exe | `FrostyCLI\bin\x64\Release\FrostyCLI.exe` |
| Deployed copy | `F:\Frosty\frostycli` (exe + DLLs + `lib/` contents alongside) |
| Game install | `F:\STAR WARS Battlefront II` |
| `gh` CLI | 2.98.0, authenticated as **Rapid421** |
| git identity | `rapid421` / `rubesk2008@gmail.com` |
| .NET | SDKs 8/9/10 present; projects target **.NET Framework 4.8**, targeting packs installed |

Standard invocation:

```
cd F:\Frosty\frostycli
.\FrostyCLI.exe --game swbf2 --game-path "F:\STAR WARS Battlefront II" <command>
```

**There is also a `frostycli-0.1.0 Source.zip` in Downloads. Ignore it — it is months behind
upstream and lacks fixes that already shipped.** Work from the clone.

---

## The problem, established by measurement

`level list` returns `0 entities` on all of: `StarKiller_01`, `StarKiller_01/Art_Exterior`,
`StarKiller_01/GA_Prison`, `StarKiller_01/Lobby`, `Naboo_01`.

`LevelCommand` looks for a list on the **level root** whose element type name contains `Entity`
or `Blueprint`. BFII does not lay levels out that way:

```
LevelData                          11 x SubWorldReferenceObjectData    (pointers only)
 └─ SubWorldData                    Objects[] -> internal PointerRefs
     └─ StaticModelGroupEntityData
          MemberDatas[0..263]       StaticModelGroupMemberData         <- the placements
          PartBoundingBoxes[0..4247] AxisAlignedBox
          Components[0..1]          StaticModelGroupPhysicsComponentData
          NetworkIdCount            261
```

`Art_Exterior` holds **264 members**, one per distinct mesh, `InstanceCount` observed from 1 to
436 (`container_s_01` = 204).

### The part that actually blocks you

Instance transforms come from **two** sources. From the plugin's
`Entities/Shared/GameShared/StaticModelGroupEntity.cs:166-181`:

```
if (member.InstanceTransforms.Count > 0)  ->  member.InstanceTransforms[i]     (in EBX)
else if (physicsData != null)             ->  physicsData.GetTransform(n)      (Havok resource)
```

**Every member observed in `Art_Exterior` has `InstanceTransforms: []`.** That level takes the
second branch exclusively. `ebx set` reaches the first source; nothing in the CLI reaches the
second.

`grep -rl "Havok|hknp|PhysicsData" FrostyCLI.Core FrostyCLI.Commands` returns **only bundled
SDK binaries** — no CLI source touches physics.

---

## The format you need to reimplement

Study — do not copy — this file:

```
F:\Frosty\plugin-corpus\repos-pinned\memory1ac__FrostyLevelEditor_ME\
    Tools\LevelEditorPlugin\Resources\Physics\HavokPhysicsData.cs      (1,305 lines)
```

It reads **and writes** BFII's Havok resource. Relevant landmarks:

| Line | What |
|---|---|
| `:776` | `GetTransform(int index, out Matrix)` |
| `:786` | `SetTransform(int index, Matrix)` |
| `:802` | `SaveInternal(NativeWriter)` — the write path |
| `:1010-1038` | in-memory set + undo variants |
| `:1177` | `GetTransform(int)` |

Shape types it handles: `hknpCompressedMeshShape`, `hknpStaticCompoundShape`,
`hknpStaticCompoundShapeData`, `hknpConvexPolytopeShape`, `hknpCapsuleShape`, `hknpSphereShape`,
`hknpShapeMassProperties`, `hknpInstance`, `hkpDomain`, `hkpInstance`.

Also read `StaticModelGroupEntity.cs:145-205` for the unpack loop — how members become
per-instance placements, and how `ObjectVariationHash` resolves through the sub-world's
`MeshVariationDatabase`.

The CLI already has `res <ls|export|import>` and `chunk <export|import>`, so payload plumbing
exists.

---

## Suggested first PR — keep it small and verifiable

**Read-only. Two parts.**

1. A Havok resource reader in `FrostyCLI.Core`, sufficient to return a transform by index.
2. `level list` descends `SubWorldReferenceObjectData` into sub-worlds, walks
   `StaticModelGroupEntityData.MemberDatas`, and resolves each instance's transform from
   whichever of the two sources owns it.

**Acceptance:** `level list Levels/MP/StarKiller_01/Art_Exterior` prints **264 members** with
real positions instead of `0 entities total`.

That is defensible on its own, independently checkable against a shipped level, and it makes
every later change testable.

**Then, in order:** `level move` (write one transform back through the owning source), then
`level add` — which is harder than it looks, see below.

### Why `level add` is not an append

Adding one instance is a coordinated multi-array edit:

| Array | Why |
|---|---|
| `InstanceCount` | +1 |
| a transform | into `InstanceTransforms` **or** the Havok resource |
| `PhysicsPartRange` | `{First,Last}` — inserting shifts every *later* member's range |
| `NetworkIdRange` | same, and the group carries `NetworkIdCount` |
| `PartBoundingBoxes` | group-level, 4,248 entries, indexed by part |
| `InstanceObjectVariation` | parallel per-instance array |

`ebx array-add --prop <list>` appends to one list and cannot keep the others consistent.

---

## Traps that cost real time

**`ebx dump` without `--deep` reads a BFII level as almost empty.** `Art_Exterior` gives 316
lines without it and **276,178** with it, because every placement sits behind an internal
`PointerRef`. Always pass `--deep`.

**Cold cache is 30-60s; warm is ~3s.** The first run after a rebuild reindexes 155,117 EBX.

**CLI output has log lines before the JSON.** Parse from the first `{`, not the first byte.

**`oo2core_6_win64.dll not found — skipping Oodle bind`** is benign for EBX work.

**`StaticModelGroupElementEntityData` is not a game type.** It is invented by the level editor
plugin (`Data/Entities/StaticModelGroupElementEntityData.cs`) as an editable view. It appears
**zero times** in shipped level data. Do not search for it in the game.

---

## Opening the PR

`gh` is installed and authenticated as `Rapid421`.

```
cd F:\Frosty\frostycli-pr
git checkout -b bfii-level-support
# ... make changes, build, verify ...
git add -A && git commit
gh repo fork --remote=true
git push -u origin bfii-level-support
gh pr create --title "..." --body "..."
```

In the PR body, state plainly: which BFII levels you tested, the before/after of
`level list`, that the implementation is a clean-room reimplementation from format study rather
than ported code, and that the write path is untested if you have not tested it.

---

## Verify before you claim anything works

```
cd F:\Frosty\frostycli
.\FrostyCLI.exe --game swbf2 --game-path "F:\STAR WARS Battlefront II" ^
    level list "Levels/MP/StarKiller_01/Art_Exterior"
```

Expect 264 members. Cross-check any transform you produce against
`ebx dump "<asset>" --deep` and read the numbers yourself — do not trust your own output
because it parsed without an exception.

Full gap analysis, with what is verified versus inferred, is in `LEVEL-WORK-GAPS.md` beside
this file.
