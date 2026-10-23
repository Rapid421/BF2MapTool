TOOL progress: lighting match ('Blender World' mode) proven on three scenes (test level within 5% per light kind; Veranda; Mustafar). Purple fixed (64 px flat textures), own reflections, sun seal, one-sun / fog rules, three new warnings (suite 46/46); add-on reinstalled for the user, zip + bundle rebuilt. Cannot match: soft sun shadows, Filmic shadows. Nothing running.

# BF2 Map Tool - run log

Brief: `I:\MapPrompts\BF2MapTool_BLENDER_ADDON_PROMPT.md`. Everything under `I:\BF2MapTool` (TMP / TEMP / TMPDIR = its
`tmp`). Solo. Port folders are read-only; scripts are COPIED into `runtime\` and generalised there. Claims graded
BFII_EXACT / INFERRED / UNKNOWN.

## FOR THE USER (rewritten every turn)

**State (2026-10-12 06:12).** The add-on exists (sidebar "BF2 Map", 7 tabs). One button builds a level + `.fbmod`;
a second button builds it with baked lighting. Four levels built by the tool load in the game with bots fighting.
Nothing here was checked by a second pair of eyes: every "seen in game" is my reading of my own screenshots.

- **Seen in game by me (pictures under `work/<Name>/review/<tag>/`):**
  - `ToolArena` (the tool's own scene, not a port): sky / sun / exposure from the scene, normal maps raised and
    tiling, cut-out grate, emissives, doors that open for soldiers (`work/DoorTest/review/dt4`), kill pit, baked
    lighting at draft quality (`review/bk1`), modes Blast, SkirmishBlast (Arcade), SkirmishOnslaught.
  - `MustafarT`: Mustafar through the tool only, with bake (`review/mt2`) = TEST POINT 2, passed-for-now.
  - `RhenVar` (never ported before): unbaked `rv4`, baked `rvb1`; Onslaught with 10 bots (`rvm_SkirmishOnslaught`).
  - `HuntersArena` (a map from another game): unbaked `ha2`. Its first bake (`hab1`) stayed dark - only 16 of 540
    pieces took a lightmap. Fixed since (354 pieces now); the rebuilt map is being game-tested (`ha3`).
- **Please confirm by eye in the morning (test points I ran and read myself):** TP1 Mustafar regression
  (`work/MustafarT/review/t2`), TP2 Mustafar with bake (`mt2`), TP4 Rhen Var (`rvb1`), TP5 Hunters (`ha2`, then `ha3`).
- **Heroes vs Villains: the level LOADS (no fault, alive 5 minutes) but my harness cannot start the round** - no
  spawn screen comes up with one player, so there are no pictures and no bots. Loads = yes; plays = unknown.
- **Supremacy: WORKS IN GAME on the test arena (06:48, `work/ToolArena/review/tas1/`).** Tick Supremacy + five
  command posts (A..E): the tool makes a second level `<Name>_02` on the Felucia donor, moves posts / 160 spawns /
  capture zones / combat area onto your map and adds it to the Instant Action list. Launched by my harness in
  the offline Supremacy mode (Mode8): loads in 81 s, the player deploys, five post beams stand where the posts were
  placed, three are held (blue), one shows a capture bubble, dozens of bots and heroes fight, no fault.
  **For your eyes:** the Instant Action menu entry (I launch levels directly, I never see the menu), whether a
  full round ends properly, and the donor's parked vehicle visible off the map edge. Mustafar Supremacy
  **TEST POINT 3 passed-for-now (07:25, `work/MustafarT/review/mts1/`):** Mustafar built by the tool only, with
  draft bake, in offline Supremacy: loads in 101 s, the player deploys, 14 bots, red / blue post icons and beams on
  the refinery walkways and the landing pad, no fault.
- **Unproven / provisional - do not rely on:**
  1. Collision: all four modes are now PROVEN in game (06:39): custom - the player's feet rest at 2.00 m on a box
     whose visible top is 1 m; hull - a droid stands over the gap between two pillars; exact and none as before.
  2. Light modes Baked / Dynamic / Both are SEEN in game (07:12, `work/LightTest/review/lt1/overview.png`), but the
     same 1000 W lamp comes out far dimmer baked than real-time; a calibration row is building (`lc1`).
     Light strength (watts x 10), emissive strength (x 280), the six sky presets: still first guesses.
  3. Lava is a still, glowing, not-solid surface (the Check tab says so): the flowing lava preset is NOT built - on
     the Mustafar port it is a hand-written shader program put into the level's shader database, which I did not
     generalise tonight. Glass: written at 07:53 on the host's own window-glass surface (no tint from the
     material); in game test `gl1`. So "six classes proven" will be five at best.
  4. Walls with a repeating normal map are cut along the texture rows so they take a lightmap: seen in game on
     the arena (`ta10`: relief in every tile, shaded walls lit, no line) and Hunters (`ha3`: 354 pieces lit;
     ONE thin seam line on the sand in `hvv1_look_090.png` - cause not looked into).
  5. The importer refuses a few piece files depending on their exact bytes; the tool retries them in another
     encoding (always worked so far). The cause is unknown.
  6. Bake presets: only "draft" has been timed (arena 127 s, Mustafar 175 s, Rhen Var 224 s, Hunters 342 s).
- **Found by tonight's check tests (real bugs, fixed):** no game mode ticked was silently built as three modes;
  the add-on's bake quality names did not match the runtime's, so "Preview" and "Final" both baked "Normal".
- **Needs you:** nothing. C: is nearly full (about 19 GB); everything I write is on I:.
- **To try it:** the zip in `dist/` is STALE (it predates the bake stage, the wall cutting, the new checks and the
  overlay). It is rebuilt before I call anything finished. Mods: `work/<Name>/mods/<Name>.fbmod`, mode SkirmishBlast.
- **Your messages and what I did:** EA "sync was interrupted" box - every launch watches for it and clicks through
  (mouse, Enter as fallback); launches have a watchdog and a retry; materials of any node setup are baked and
  packed into the game's channel layout automatically, normal maps green-up, emissive detected (Materials tab).
- **Defaults taken (not asked):** Blender only dumps the scene, everything else runs outside it; host shell
  Kamino_01; this PC's Python 3.13; own CLI branch `feat/bf2maptool` (never pushed); test levels named
  `ToolArena`, `MustafarT`, `RhenVar`, `HuntersArena`, `DoorTest`, `ColTest*` so they sit next to your mods.

## Machine facts (measured 2026-10-12 00:40)

| item | value |
|---|---|
| disk free (00:30) | I: 98 GB, F: 73 GB, C: 18 GB |
| Blender | `C:/Program Files/Blender Foundation/Blender 5.2/blender.exe` (also 4.2 / 4.3 / 4.4 / 4.5 / 5.0) |
| Python | 3.13.14 `C:/Users/rubes/AppData/Local/Programs/Python/Python313/python.exe`; numpy, PIL, scipy, xatlas present |
| dotnet | 10.0.400 |
| FrostyCLI source | `I:/2005_Map_Ports/MustafarPort/cli-wt` @ 16daffc (`feat/mustafar-remake`), clean tree |
| HavokCollisionTool | `I:/UtapauPort/havoktool/HavokCollisionTool/bin/Release/net8.0/HavokCollisionTool.dll`, template `I:/UtapauPort/col/template.res(+meta)` |
| fluxbake | `<cli-wt>/tools/fluxbake/bin/fluxbake_src.exe` (2.3 MB) |
| leak filter | `I:/2005_Map_Ports/MustafarPort/tools/leakmask/` (cpp 404 lines + build.cmd, MSVC 2022 `/openmp`) |
| ShaderKit / rvm tools | `I:/NewShader/tools` (rvmdb, cascade, refwalk, insert_group), `I:/HothPort/rvm/tools/rvm_merge.py` |
| game / plugins | `F:/STAR WARS Battlefront II`, `F:/Frosty/Frosty Editor/Plugins` |
| test harness | `python I:/UtapauPort/work/write_play_args.py <mod> <level> <mode>`, `schtasks //Run //TN FrostyAutoTest` (task Ready); result `F:\Frosty\gamelink\autotest-result.json` (last: another session's CloudCity_01 run, 2026-10-11 23:09, status COORDS); log `C:\ProgramData\FrostyGameLink\gamelink.log` |
| game running now | no `starwarsbattlefrontii.exe` in the process list (EADesktop.exe is up - not mine, not touched) |
| reference build | `I:/2005_Map_Ports/MustafarPort/mods/MUS28.fbmod` (194,283,775 bytes), project `work/mbl/MBL_29_C.fbproject`, fixed blend `blend/mus_fixed.blend` (90 render meshes, 48,099 tris), 514 static pieces + 80 lava pieces |

## STEP 1 - INVENTORY (per stage: script, hard-coded assumptions found, CLI commands)

Common to every script: `P = 'I:/2005_Map_Ports/MustafarPort'`, `HDR` (game path `F:/STAR WARS Battlefront II`,
plugins `F:/Frosty/Frosty Editor/Plugins`), level `AddedContent/Mustafar/Levels/Mustafar_01/Mustafar_01`, prefix
`Objects/Ported/Mustafar`, short name `Mustafar`, GUID scheme `mustafar2026://`, host `Levels/MP/Kamino_01`,
`sys.path.insert(0, 'I:/2005_Map_Ports/MustafarPort/work')`, tag-named project files (`MUS9J_A`, `MUS4`), environment
variables as switches (`MUS_OFF`, `MUS_CUTOUT`, `MUS_EM`, ...), mod author `rubes`. All of these become `map.json`
(map-specific) or `settings.json` (machine-specific) fields.

### 1 split / export - `work/split_mus.py` (424 lines, runs IN Blender), `work/lava_split.py` (535)
- Does: world-space faces per material; winding flipped on negative determinant; non-finite / |uv| > 1000 UVs reset;
  corner normals kept as custom split normals; faces with an edge > 48 m cut by exact midpoint subdivision
  (distance-aware: max(48, d/4)); pieces <= CAP 3,000 tris and extent <= max(48 m, d/2), units = (object, material),
  median split fallback; UV islands cut into 2 x 2-tile groups and shifted by whole numbers; 2005 Doublesided faces get
  a reversed copy; data = Rot(-90 X) @ (world - centre), object Rot(+90 X); FBX export `global_scale=0.01,
  apply_unit_scale, use_tspace, bake_space_transform=False`; second UV map `UVSet1` (the importer fills TexCoord1 only
  from a second map); node name `mesh:lod0`; `pieces.json` (name, file, material, image, objects, tris, centre, size,
  uv stats, area).
- Hard-coded: play centre `PLAY_C = (10, 30, 68)`, `PLAY_R = 160`; `SUBDIV_LEN = 48`; `EXT 48`; piece prefix `MU`;
  `DROP_MATS` (three Mustafar material names); `DOUBLESIDED = 65536` read from the material-name suffix (2005 flag
  word - not a thing in a user's scene: becomes the material class "cut-out double-sided" / a per-material flag);
  `SCALE = 1`; env switches `MUS_FLIP / MUS_ONLY / MUS_SKYMIST / MUS_OBJECTS / MUS_EXCLUDE / MUS_PREFIX / MUS_DOUBLE`;
  render set = view-layer membership + `hide_render`.
- lava_split: lava = materials named `mus1_main_2_*` / `mus1_main_3_*`; EDGE 12 m; flow solver (potential, lips / feet
  of drops >= 4 m, 8 smoothing rounds); TexCoord1 = flow, Color0 = bank / heat / steep; piece origin = bottom centre.
  Importer stores TexCoord1 v as 1 - v (undone in the shader).
- CLI: none (Blender only). Collision OBJ: `work/dump_raw_obj.py` re-imports every FBX in Blender (drop scale, apply
  rotation, OBJ `forward -Z, up Y`) -> the physics frame = BFII axes of (world - centre): (x, z, -y). To verify on an
  existing `col/raw/*.obj` before the runtime writes these itself.

### 2 textures - `work/pack_mus.py` (242)
- Does: per material: class + packed PNGs. `CS` = rgb colour + smoothness alpha; `NAM` = rg normal (DX), b = template
  value, a = metal; `E` = emission rgb, alpha 255; cut-out variants `CA/NS` (A), `CS/NW` (B), `CK/NK` (C, red-key),
  `NJ` (J: NW with b 127, a = clip mask). Flat NAM = 4 x 4 texture. Normal convention by integrability
  (`nrm_conv`: corr > 0.05 OpenGL -> flip green; < -0.05 DirectX; else UNCLEAR kept). Dedup by md5 of the pixels.
- Hard-coded: source dirs (`tex/src`, `C:/Users/rubes/Downloads/BF2-05-Mustafar/...`); `BF205` table (12 Mustafar
  textures -> upscale / nrm / rgh / em files, `rpow`, `metal`, `estr`, `sconst`); class rules from the 2005 flag bits
  and three texture names; `NAM_B = {opaque 25, glow 19, lava 19, cutout 0}`; `EM_HOT 0.10`; `T_MU_` name prefix;
  `survey/emcover.json`.
- Tool form: inputs come from the Principled BSDF (export stage resolves each socket to an image file or a constant);
  class comes from the material's `bf2_class` property; NAM blue and every channel layout come from the TEMPLATE
  table, not the class name; sRGB / block format / mips come from `import-texture --template <shipped texture>`.
- CLI: `import-texture file as template`, `bundle-add ebx bundle with_resources`.

### 3 generator - `work/gen_mus.py` (586)
- Does: `dumps` (ebx-dump --deep of every partition), `s0` (host clean-up + prune), `prep` (package dirs: manifest.json
  + mesh.fbx + textures + physics.res/.resmeta; build JSON: import-texture, bundle-add deps, one `port-import` per
  piece), `phaseB` (retry failed imports as `_R1`, LodGroup -> NoCullingLodGroup, depot `<mesh>_mesh/blocks` bundle-add
  for non-physics pieces, `_EmissiveMultiplier`, lava params, RaycastRootShapeIndex 1 on both bodies, Schematics null,
  `level-add-group`, `port-import-layer` entities (no_bundle) + members (as_members, no_collision), WorldSizeXZ / Y
  8192), `retry` rounds, `owndb_steps` (own DX11 DB res, host DB out of the bundle), smoke helpers.
- Hard-coded: `OFF = (-96, 340, 77)`; Kamino clean-up lists (`DOOR_BPS` 2 GUIDs, `VE_VOLUME_BPS` 2 GUIDs,
  `DROPPED_HOST` 7 layers, 3 water assets, 6 level-clear layers, `Stormy` group clear); load gate GUIDs (0b83a2b4 ->
  Setup 2c37014e, fields 0x263cb49e / 0xa4253151); `BP_NOPHYS`, `BP_PHYS`, `PHYS_TPL`, `GROUP_TPL`
  (`Levels/MP/Kamino_01/Content`); `TEMPLATES` (opaque = Sullust turbolaser platform 1UV array; opaque2uv = box_m_02;
  glow = o_kam_emissivestrip_s512_01; cut-out A / B / C / J; lavaflow = volcanicbase_lavafalls_01) each with shader
  path + GUID + (param, plan key, template texture); `COMMON_DEPS`; `LAVA_PARAMS`; `OWNDB` / `HOSTDB` res names; res
  type 0x8DA16895; physics res type 0x91043F65; `EM_PER_STRENGTH`; `KILL_OBBS` (3 Kamino GUIDs + y); `SPAWN_LAYERS`;
  registry `<L>/<Level>_networkregistry_Win32`; `level_assets.json` taken from the level-create log by hand;
  map box (-436..253, -351..418) in `smokespawns`.
- CLI: `run <pipeline.json>` ops `project-load / project-save / ebx-dump / bundle-add / bundle-remove / level-clear /
  level-remove / cli / import-texture / port-import / ebx-set / ebx-set-ref / material-set / level-add-group /
  port-import-layer / res-import / res-export`; direct `level create ... --skirmish-from`, `connection add`,
  `port prune-refs`, `level prune --dangling --refs-into`, `level closure --audit`, `level validate`, `mod-build`,
  `mod lint`, `kyber show`, `port mesh-info`.
- **Which templates are PROVEN in game on a Kamino shell (Mustafar log):** opaque 1UV array (MUS1 / MUS3, user-seen),
  emissive strip (MUS1), cut-out J = Tatooine carpet AlphaTest (MUS23 screenshot, after the group-member fix and the
  Tatooine-DB surface), lava (MUS27 accepted). Cut-out A / B / C: never really drawn (the group entries never changed),
  so their status is UNKNOWN, not "failed". Glass: Kamino window (Mygeeto / Polis Massa, keep materials) - not in
  Mustafar's chain; to qualify in step 6.

### 4 collision - `work/mesh_collision.py` (42), `work/dump_raw_obj.py`, `work/run_collision.sh`
- Does: `dotnet HavokCollisionTool.dll import mesh <obj> <tpl.res> <tpl.resmeta> <out.res> <out.resmeta>` per piece (8
  threads), parses `mesh: N triangle ... error X mm`; writes `.framed` = resmeta + res; `report.json`.
- Hard-coded: tool path, template path, output dir, `work/colset.json` (`physres` = pieces that get a res,
  `collision` = pieces that get an entity).
- Hull (moving parts): `import both --hull=always` on the donor panel's res (doors). None: no res, no entity,
  `no_physics` blueprint + depot bundle-add. Custom mesh: the same `import mesh` on the user's collision object's
  triangles in the piece frame (new in the tool). Bot slabs: Mygeeto's tri-prism slabs for slot 0 (to read at step 6).

### 5 look - `work/gen_look_mus.py` (126), `work/gen_mus9.py` (79), `work/gen_mus9t.py`, `work/render_sky_mus.py`
- Does: `ebx-clone` donor VE -> own VE; bundle the VE + donor + donor `_class` / `_class_schematics` pair; import
  panorama + gradient `.hdr` with shipped templates; sky component fields (LuminanceScale, AlphaOutput Clear,
  PanoramicAlphaTexture override + DefaultPanoramaAlpha, SunScale); outdoor light (sun colour / intensity / rotation);
  fog (colour, start / end, curve, height fog); Enlighten sky fill; tonemap EV / MinEV / MaxEV; shadow distances; every
  `VisualEnvironmentReferenceObjectData` of the level -> the clone.
- Hard-coded: donor `Levels/SP/A2/M3SUL/Lighting/VE_A2M3_Cloudy_02`; templates `T_Sullust_Cloudy_01_C` /
  `_Fog_02_C`; component indices `Components[6]` / `[8]` and override slots `[23]` / `[3]` (valid for that donor only -
  the tool must find them by type in a dump); `VE_REFS` = root, HighEnd, Stormy (Kamino layers); all numbers in `DEF`.
- CLI ops: `ebx-clone`, `import-texture` (.hdr), `ebx-set`, `ebx-set-ref`, `ebx-array-add`, `ebx-dump`.

### 6 lights - `work/gen_lights_mus.py` (127)
- Does: `level-import-lights` records (PbrSphereLightEntityData; Fields Enabled / AffectDiffuse / AffectSpecular /
  AffectRadiosity / Color (max channel = 32) / Intensity (lumens) / AttenuationRadius / SphereRadius), each light
  pushed >= 0.7 m off the nearest triangle (closest-point test on the navgen geometry).
- Hard-coded: record template taken from `I:/KorribanPort/lights/korriban_lights.json`; lumens rule
  `PT_LM * sqrt(energy / 4)` shared inside a 6 m cluster; white mix 0.7; blue x 0.6; lift 0.8 m; radius 8 m; target
  layer `Shared_Lighting`.
- Tool form: one documented conversion (Blender W -> lumens, radius from the light's own cut-off or a default),
  per-light mode: Baked (AffectRadiosity + direct lightmap), Dynamic (runtime only), Both. Spot / area record types:
  to read from Korriban's file + `level-import-lights` source (step 7).
- CLI: `level-import-lights ebx from`.

### 7 navmesh / spawns - `navmesh build`, `work/gen_spawns_mus.py` (312)
- `navmesh build <level> --region x0,z0,x1,z1 --y-range a,b --skip <prefix> --cs 0.2 --ch 0.1 --climb 0.5 --stream
  <guid> [--navgen] [--blob file]`; exports `<Level>_navmesh.polys.obj` + `<Level>_geometry.obj`.
- Spawns: main navmesh component; candidates >= 2.5 m from an edge, >= 3 m headroom; Blast 12 + 12 at the 20 / 80 %
  points of the long axis + neutral by farthest-point; finder shapes (team discs 14 m, neutral k-means hulls); combat
  area = walkable hull + 40 m; Arcade defend areas; HvV starts at 30 / 70 %; HvV shapes keep Height 0; spawn map PNG.
- Hard-coded: stream GUID `c916f1b9-...` (from the level-create log); region / y-range numbers; file names
  `Mustafar_01_*`; layer names `TeamDeathmatch_Global`, `HeroArena_Logic`, `HeroArena_Shapes`,
  `TeamDeathmatch_Skirmish_Logic`, `TeamDeathmatch_Skirmish_DefendAreas`; dump dir `dumps/S0f`; rng seed 11; counts
  come from the host's own spawn objects (the script re-positions existing entities, it creates none).
- Tool form: hand-placed spawns from the scene override the generated ones per team / mode; counts stay the host's.

### 8 doors - `work/door_export_mus.py` (138, Blender), `work/door_gen_mus.py` (167)
- Does: panel frame by PCA (normal = smallest axis), prefab frame turned so the door logic's local-x slide = the
  wanted direction; whole panel as one leaf in both leaf slots; hull collision on Kamino's keyframed panel blueprint;
  clone `PF_AutomaticDoubleDoor_Large_Kamino_01`; open offset; OBB trigger resize; sound entity + 3 connections
  removed; Schematics null; light colours black; 9 registry objects; class + class_schematics PAIRS of Core /
  Controller / Lights; `level-add` per door (pos + 3x3 basis).
- Hard-coded: collection `MUS_DOORS`, mesh-name -> type table, `KAM` panel blueprint, `SRC_PREFAB`, `CORE / CTRL /
  LIGHTS`, the 9 `REG` (asset, class GUID) pairs, prefab indices (`Objects[2..5]`, `Interface.Fields[4,5,7,8]`,
  `LinkConnections[2]`, `EventConnections[2,3]`, `PropertyConnections[7]`), `RISE 0.94`, trigger depth 4.84, texture
  table for two Mustafar materials.
- CLI ops: `port-import` (blueprint_template + physics_template), `ebx-clone`, `ebx-array-remove`, `ebx-array-add`
  (registry, class_guid), `level-add blueprint pos rot`.

### 9 kill volumes - `work/kill_plan.py` (48)
- Does: three OBBs (centre + half extents, BFII) written onto Kamino's three kill OBBData (`GameModes_Shared`; one
  kill logic in `GameModes_Flow`); box top 0.35 m UNDER the lava surface; lava collision removed; navmesh vertices
  inside each box counted (must be 0).
- Hard-coded: lava body split by height (65.5 / 40 / 44), pads, the 3 OBB GUIDs (`gen_mus.KILL_OBBS`), exactly three
  volumes (host limit: more boxes need new OBBData + link connections - to build in the tool, measured on a dump).

### 10 bake - `work/mbl/*`, `tools/leakmask/`
- Chain: `enlighten keys --list` -> `mesh geo-dump --list` (`.meshgeo`) -> `classify.py` (lightmapped = opaque piece
  with a RadiosityTexCoord stream) -> `charts.py <t/m>` (xatlas, density by distance to walkable samples, side <= 1024,
  writes RadiosityTexCoord) -> `gen_regeo.py` (`port-regeo` per mesh) -> `enlighten chart-plan --match <prefix>
  --sides --also-host-records` -> `gen_mbl_materials.py` (albedo from the CS footprint, emissive = radiance / albedo
  luminance) -> `gen_mbl_probes.py` (24 m columns over walkable floors) -> sky cube (`checks/sky_cube.py`,
  `ve_sky_args.py`) -> `enlighten flux-scene --emitters <name parts>` -> `fluxbake_src.exe scene out --samples n
  --depth n --aa n --filter n --stitch n` (below-normal priority) -> `leakmask scene out out_f` -> `enlighten
  bake-apply <level> --bake Kamino --plan --probes --flux --out --prefix <L>/Lighting_MBL [--tint]` -> `end_check`,
  `fls_check` -> closure --audit -> validate -> lint -> mod-build.
- Hard-coded: `B = 'I:/EnlightenBake/Mustafar/'`; atlas `8192 x 3152` in `bake_stats` / `leak_look`; play extent and
  floor heights in `charts.py` / `gen_mbl_probes.py` (x -231..9, z -98..152, y 395..420); `--bake Kamino`; host FLS
  file `I:/EnlightenBake/Coruscant/host_01/FL_Kamino_Stormy_01_Static/static.framed.bin` + darkroom offset 22369 /
  count 1215; piece list files `pieces_MUS4.json` / `pieces_MUS4L.json`; lava emission simulation; emitter name parts.
- Measured times (Mustafar, 301 charts, 16.3 M texels, RX 6800 machine, game open): 256 spp 244 s; 512 spp + filter 5
  766 - 1,010 s; 1024 spp + filter 5 3,025 s. One 512 run died silently with ~3 GB free RAM -> the free-memory gate.
- Rule found in the log: `port regeo` rebuilds the shader-block depot and drops material overrides (depot 1,464 ->
  1,288 bytes) -> re-apply every `material-set` after regeo and compare.

### 11 checks written the hard way
`schem_pairs.py` (bundle listing: every `_class_schematics` has its `_class`), `cut_decl.py` (surface -> vertex
declarations with programs; needs ShaderKit `rvmdb / cascade / refwalk`), `grp_members.py` (StaticModelGroupEntityData
`MemberDatas[i].MeshAsset` census), `flat_check.py` (zero-thickness pieces per class), `work/depot_list.py` (depot
texture bindings, 36-byte stride), `mbl/checks/end_check.py`, `fls_check.py`, `bake_stats.py` (NaN count),
`plan_keys_cover.py`.

### 12 Supremacy - Polis Massa `pmf_one.py` (93), `pms_strip.py` (83), `pms_bots.py` (77), `pmf_cp.py` (373), `pmf_rootlate.py` (65), Mygeeto `sup2/my_cp.py`
- Recipe (BFII_EXACT for Polis Massa, user-tested PMF22 / PMF23): second level `<Name>_02` created IN THE SAME SESSION
  from the finished `<Name>_01` as terrain host: `level create <L2> --terrain-from <L1> --terrain-keep <31 partitions
  incl. Setup> --mode-from S8/Felucia/Levels/MP/Felucia_01/Felucia_01 --mode Mode1 --mode-subworld Mode1 --mode-keep
  <13 Mode1 / Mode8 partitions> --also-modes Mode8 --ve keep --menu-name --planet-name --kyber`; Kyber declared for
  Mode1 only; offline Instant Action Supremacy = **Mode8**; Instant Action list entry
  `UI/Metadata/OptionsEnumUIMetaData Items[95].ItemLevelReferences += UI/Data/GameModes/Levels/<Name>_02`.
- `pms_strip`: the intro cinematic STAYS (its finish event starts the round); 501 connections into Felucia's
  Partition_Intro_* / Mode1_VFX / _VFX_Schematic / Mode1_PA removed, those 3 layer refs removed, Felucia VE refs in the
  timeline Excluded / nulled, 83 Felucia objects in Mode1_Cinematics Excluded (else GPU reset at countdown 0).
- `pms_bots`: clone `Addons/Mode1/Gameplay/PF_Bot_Logic`, IntEntityData (Realm_Server, n) feeds both ForceFill setters
  (CLI internal-id numbering, `FROSTYCLI_SKIP_PERSIST_CHECK=1`), Mode1_Gameplay Objects[3] -> clone, registry entry.
- `pmf_cp`: CP letters by Felucia GUID prefix (A 887e04e0, B 0fe01103, C 04cf27e5, D eba596ca, E ec9ff755); 160
  spawns = 5 x 13 per-CP groups + 2 x 12 HQ sets + 71 fallback; capture zones; combat areas (`9da9a90e`, `6566716f`);
  **finder shapes: translate only, full size, y = floor + 3** (rewriting them broke the Mode8 round start; shrinking
  them to 22 m left nobody able to spawn); vehicle set pieces NOT Excluded (crash at countdown 0 when Excluded).
- Felucia superbundle mounted on the level (`description.SuperBundles += S8/Felucia/...`); root-late loop
  (`pmf_rootlate`: bundle-add every ROOT-LATE target into the root, re-audit, until 0).
- Hard-coded: every path / GUID above, partition lists `TK` / `MK`, dump-derived indices (`Objects[11,9,8]`,
  `PropertyConnections[39,40]`, `Objects[3]`), `OFF`, scratchpad dump path for Felucia's spawns.
- Mygeeto note: infinite load when `Setup` is not in `--terrain-keep` (the root's `Setup.OnTrue -> GameModes.StreamIn`
  then points at the shipped Kamino Setup).

## PAST SILENT FAILURES -> NAMED CHECK OR RULE (every row filled 2026-10-12 07:55; "name" = what exists in the tool today)

| # | failure (source) | kind | name in the tool | done |
|---|---|---|---|---|
| 1 | missing / unpacked image | pre-flight | `tex.missing` | done - check test 40 / 40 (`tests/checks/result.json`) |
| 2 | non-power-of-two / oversized texture | pre-flight | `tex.resized` (info: any size is accepted and resampled to a power of two, 4096 at most) | done - check test 40 / 40 (`tests/checks/result.json`) |
| 3 | cut-out material whose image has no alpha | pre-flight | `tex.cutout_no_alpha` | done - check test 40 / 40 (`tests/checks/result.json`) |
| 4 | face with > 1 material after split | pre-flight | by construction: the split writes one material per piece | done (rule, `stages/split.py`); group census 23 / 23 .. 645 / 645 |
| 5 | piece over 3,000 triangles | pre-flight | by construction: the split cuts pieces at 3,000 triangles | done (rule, `stages/split.py`) |
| 6 | zero-area / zero-uv triangles | pre-flight | `geo.degenerate` (zero-area faces; they are dropped) | done - check test 40 / 40 (`tests/checks/result.json`); zero-uv triangles are NOT checked |
| 7 | flipped normals | pre-flight | `geo.flipped` | done for closed objects - check test 40 / 40 (`tests/checks/result.json`); open meshes NOT detected |
| 8 | negative-scale object / unapplied scale | pre-flight | `geo.scale` (warning mirrored / info unapplied: the export uses the size seen) | done - check test 40 / 40 (`tests/checks/result.json`) |
| 9 | scene scale (doorway / riser) | pre-flight | `scene.scale` (whole map under 20 m or over 2 km tall; 1.8 m reference figure in the Map tab) | done - check test 40 / 40 (`tests/checks/result.json`); doorway / riser are NOT measured |
| 10 | map extent vs host world size | pre-flight | `scene.extent` | done - check test 40 / 40 (`tests/checks/result.json`) |
| 11 | no spawns and no walkable floor | pre-flight | `spawn.none`; too little walkable floor stops the `spawns` stage with a message | done - check test 40 / 40 (`tests/checks/result.json`) |
| 12 | spawn inside geometry | pre-flight | `spawn.in_geometry`, `spawn.no_floor` | done - check test 40 / 40 (`tests/checks/result.json`) |
| 13 | spawn in a kill volume | pre-flight | `spawn.in_kill` | done - check test 40 / 40 (`tests/checks/result.json`) |
| 14 | teams closer than a limit | pre-flight | `spawn.teams_close` (40 m) | done - check test 40 / 40 (`tests/checks/result.json`) |
| 15 | kill volume contains walkable floor (Mustafar upper lake) | pre-flight | `kill.walkable` | done - check test 40 / 40 (`tests/checks/result.json`) (see row 59) |
| 16 | light inside / within 0.5 m of a surface | pre-flight | `light.in_surface` | done - check test 40 / 40 (`tests/checks/result.json`) |
| 17 | more dynamic lights than the budget | pre-flight | `light.dynamic_budget` | done - check test 40 / 40 (`tests/checks/result.json`) |
| 18 | saturated light colour (MUS13 / MUS14 "super bright blue") | pre-flight warn | `light.saturated` | done - check test 40 / 40 (`tests/checks/result.json`) |
| 19 | door mesh not single-piece / no room / no direction | pre-flight | `door.materials`, `door.shape`, `door.direction`, `door.travel` | done - check test 40 / 40 (`tests/checks/result.json`) |
| 20 | name clash with a shipped level / installed port mod | pre-flight | `map.name_clash` + `shell` stage refusal | done for shipped levels - check test 40 / 40 (`tests/checks/result.json`); another installed mod only at `level create` |
| 21 | group draws `MemberDatas[i].MeshAsset` (MUS16 / MUS17) | rule + post-build | rule in `stages/importer.py` (place) + post-build group census in `finish.run_audit` | done: census on every build (arena 23 / 23, Hunters 542 / 542, Rhen Var 645 / 645) |
| 22 | `port regeo` drops material overrides (MUS22 / MUS23) | rule + post-build | rule: `stages/bake.py` re-applies every material after regeo; post-build `census.depots` | done: on every build; `tests/test_census.py` 5 / 5; baked arena / Rhen Var / Hunters clean |
| 23 | class schematics without its class (MUS12 crash 0x167FE84) | rule + post-build | rule `finish.with_partner` + post-build `schematics_pairs` | done: on every build (62 .. 65 class schematics, all paired) |
| 24 | surface without a program for the mesh's vertex declaration (MUS3 .. MUS16) | rule + post-build | rule: `stages/shaderdb.py` merges a surface from its home database and verifies; post-build `census.depots` surface census | done in the readable form: a piece draws a permutation its template uses; program-per-declaration is verified at merge time only |
| 25 | lava / no-collision surfaces in the navmesh; kill box over walkways (MUS28) | rule | rule: `collision.NO_COLLISION_CLASSES`, navmesh from collision; `kill.walkable`; kill stage tests navmesh corners | done (Mustafar: 2,276 navmesh corners tested, none inside) |
| 26 | depot texture of a template not bundled (MUS6 crash 0x1522B97; validate blind spot) | rule + post-build | rule: template `deps` bundled by the import; closure audit + lint `added-res-unbundled` | done (lint 0 errors on every build) |
| 27 | sound on cloned logic prefabs (PM19 crash) | rule | rule in `stages/doors.py`: `sound_refs` nulled, read back "no sound left" | done (doors proven in game `dt4`) |
| 28 | transient `EBX clone failed` | rule | rule: `cli.Cli.run` fresh-process retry (4), importer rounds (6) | done (seen working: arena 2 transient, Hunters 15) |
| 29 | bake emitter brightness = on-screen brightness (MUS9) | rule | two fields per material: `emission_screen`, `emission_bake` (Materials tab) | done as fields; numbers provisional (x 280) |
| 30 | leak filter not run (MUS8 "random orange") | rule | bake sub-step `leak` (bin/leakmask.exe) always runs | done (Rhen Var 156,522 texels rewritten) |
| 31 | bake started with too little free memory (MUS14 silent death) | rule | `bake.MIN_FREE_GB = 6`: the bake refuses to start below it | done as a gate; the limit is a first value, not measured to the edge |
| 32 | `python -` | rule | every subprocess is started from a `.py` / `.exe` path with stdin closed | done in the tool; my own slip at 03:22 is logged |
| 33 | multi-material template mesh (Cato crash 0x159E617) | rule | `hosts/kamino_01.TEMPLATES` lists single-material meshes only | done by table; not re-checked per build |
| 34 | renamed pipeline file whose step did not run (MUS16 fog slip) | rule | every stage ends with a read-back of what it wrote | done (stage results quote the read-back) |
| 35 | two CLI runs racing on one log (MUS12 note) | rule | `runner.Lock`: one `build.lock` per work folder | done |
| 36 | Supremacy infinite load (Setup not kept), GPU reset at round start (intro visuals), rewritten finder shapes | rule | `stages/supremacy.py`: Setup kept, intro stripped (501 / 3 / 83 / 11), finder shapes moved only | done: arena, Mustafar, Rhen Var in game |
| 37 | truncated validate list read as complete (MUS6) | rule | checks read `Result:` / count lines and full dumps, never a truncated list | done (`audit.py`) |
| 38 | play-area volumes keep the host's 0 / 20 / 30 m height when only their outline is moved: out of bounds everywhere (first tool test; the port had set 400 m by hand once) | rule + read-back | `stages/spawns.py` play-area volumes + read-back | done (in bounds on every map since `t2`) |
| 39 | closed doors cut the navmesh: 11 of 36 leaf slots were not left out by `navmesh build` | rule | `stages/nav.py` `--skip <prefix>_Door_` (+ row 53 for collision mode) | done (arena `ta10`, door test `dt4`) |
| 40 | Frosty's FBX importer rejects 2 of 514 files whose arrays are zlib level-1 streams | rule | `fbxbin` writes arrays uncompressed | done, 514 / 514 |
| 41 | only the clone failure is transient; any other importer message retried four times before | rule | SUPERSEDED by row 46 (those messages turned out to depend on the file's bytes, not on the piece) | retracted |
| 42 | resume after a Cancel in the collision stage: pieces converted before the Cancel were skipped and left out of the report (floors without collision, navmesh of 2 polygons) | rule | `collision`: converted = listed by the last complete report; planned vs reported must match | done, re-run 17 / 17 |
| 43 | normal maps flipped to DirectX read as dents in game; the ports' rule was never looked at with relief | rule | `texpack.TARGET = 'opengl'` (green up), plan note per material | done, seen in game (t6, ta8) |
| 44 | the opaque 1UV preset shows normal-map relief only where 0 <= v <= 1 (the 2UV preset only inside the unit square): tiled walls are flat | rule | `hosts.OPAQUE_RELIEF_TEMPLATE`: opaque + real relief -> the template that tiles; plan note | done, seen in game (ta8) |
| 45 | a Mapping node that tiles / turns an image was exported as the plain image, no warning | bake + check | `matbake` (`uv_transform`), `tex.uv_transform` when not baked | done (numeric test) |
| 46 | the importer refuses some piece files depending on their exact bytes ("mismatch in the amount of LODs", "at least one UV channel"): Mustafar 2 / 514 with zlib-1, Rhen Var 5 / 724 uncompressed, 1 with shared uvs | rule | `fbx.VARIANTS` + `importer`: a refused piece is rewritten in the next lossless encoding and imported again (6 rounds) | written; running on Rhen Var |
| 47 | a launch dies behind the EA app's "sync was interrupted" box; no harness line shows it | test harness | `tools/launcher_dialogs.py` in `tools/game_test.py` | click path proven live; in-test path not yet triggered |
| 48 | my own general retry re-ran the whole 724-piece import three times per round | rule | `cli.Cli.run` retries a transient stop only where the caller treats the stop as fatal | done |
| 49 | the navmesh was built from the VISIBLE meshes: it sat 1 m under a custom collision top | rule | `stages/nav.py` passes `--collision` (server physics bodies) | done (264 collision triangles in = the collision stage's count) |
| 50 | Collision "Hull" on a static object did nothing: the object was not solid | rule + test | `collision._hull`; `tests/test_overlay.py` | done: built = overlay (0 triangles off); in game `cta` (droid over the gap), `ctb` (player's feet at 2.00 m on the custom mesh) |
| 51 | with Auto spawns off, neutral and hero slots were still generated elsewhere | rule | `stages/spawns.py` v4: every slot on a placed spawn | done (door test `dt4`) |
| 52 | probe volumes stood only over LIGHTMAPPED floors: characters dark elsewhere | rule | `stages/bake.py` v3: probes over every solid floor | done (Hunters 10,224 probes; `ha3` in game) |
| 53 | a closed door's collision cut the collision navmesh in two; `--skip` was ignored in collision mode | rule | CLI `NavmeshCollision.Skip` (commit 7898e0c) | done (arena `ta10` builds: spawns pass) |
| 54 | a stage said "up to date" although the CLI it runs had changed | rule (dev) | stage version bumped by hand (`nav` v4) | OPEN: the CLI's version is not part of a stage's inputs |
| 55 | no game mode ticked was silently built as Blast + HvV + Arcade | pre-flight | `map.no_mode` (the fallback only applies to a map file without the key) | done (check test) |
| 56 | the add-on's bake quality names (preview / final) were not the runtime's (draft / high): both baked "normal" | rule | one list in `props.py` = `bake.QUALITY`; an unknown name stops the bake | done |
| 57 | a spawn stood in the end of a wall slab (my own arena scene) | pre-flight | `spawn.in_geometry` (inside a closed solid, or within 0.25 m of one) | done (check test; scene fixed) |
| 58 | spawn with nothing solid under it | pre-flight | `spawn.no_floor` | done (check test) |
| 59 | kill volume across a walkway (the Mustafar upper-lake case) | pre-flight | `kill.walkable`: floor at play height inside the box, measured by sample points | done (check test); the level is "lowest placed spawn - 1 m", a guess when nothing is placed (then a warning) |
| 60 | light inside or within 0.5 m of a surface | pre-flight | `light.in_surface` | done (check test; fires on 17 Mustafar and 2 Hunters lights) |
| 61 | door travel that leaves the panel in the doorway | pre-flight | `door.travel` | done (check test) |
| 62 | closed object drawn inside-out | pre-flight | `geo.flipped` (negative volume of a closed, consistently wound mesh) | done (check test); open meshes with flipped faces are NOT detected |
| 63 | level name taken by a shipped level | pre-flight + rule | `map.name_clash` (list read from the game, `work/shipped_levels.json`, 44 levels); `shell` stage refuses too | done (check test); a clash with another installed MOD is only seen when `level create` says "already exists" |

## Step 2 - runtime (STARTED)

Layout decided (`I:\BF2MapTool\runtime\bf2maptool\`):
`__main__.py` (commands `port`, `bake`, `rebuild-mod`, `check`, `check-setup`), `config.py` (map.json + settings.json
-> derived names), `state.py` (build.json: stage -> inputs hash, result, outputs), `runner.py` (stage graph, resume,
lock, live log), `cli.py` (FrostyCLI: pipeline JSON runner, direct calls, log parsing, clone-failure retry),
`stages/` (one module per pipeline stage), `hosts/kamino_01.py` (everything host-specific: clean-up lists, load gate,
kill logic, layer names, Supremacy keep lists), `templates.py` (material class -> template mesh / shader / texture
slots / channel layout / home shader database), `checks/` (pre-flight + post-build), `fbx.py` (piece writer).

### Own FrostyCLI - DONE (00:35)
`bash tools/setup_cli.sh` -> `git worktree add -b feat/bf2maptool I:/BF2MapTool/cli-wt 16daffc` (from Mustafar's
cli-wt), `dotnet build -c Release -p:Platform=x64`: **0 errors, 5 warnings, 13 s**; asset cache (103.6 MB) + fluxbake
copied; `FrostyCLI.exe man` runs. Log `logs/setup_cli.log`. Worktree 371 MB. No CLI source change yet.

### Default 1 measured - the runtime writes the piece FBX itself (00:36 - 00:42) - ADOPTED
- What FrostyCLI needs (read in `cli-wt`): `port import` hands `mesh.fbx` to Frosty's own `FBXImporter` (MeshSetPlugin);
  a package may instead carry `mesh.meshgeo` (the "geometry port": donor layout picked per surface) - NOT used here,
  it is a different, unproven route for these templates. `port regeo` (in-place meshgeo) stays the route for lightmap
  charts and lava flow data.
- Layout of a piece FBX, measured on Blender's output (`tmp/probe/fbx_look.py`): binary FBX 7400, UpAxis Y, FrontAxis
  Z, UnitScaleFactor 1, ONE node `mesh:lod0` with no transform, vertices in metres already in game axes
  (x, Blender z, -Blender y) relative to the piece centre, normals per corner (IndexToDirect), 2 UV sets (`UVSet0`,
  `UVSet1`), tangent + binormal per corner for both sets, one material. The Blender files also carry the texture's
  absolute path (`I:\2005_Map_Ports\...`) - the runtime's files carry no path at all.
- `runtime/bf2maptool/fbxbin.py` (generic binary FBX tree reader / writer) + `fbx.py` (`build / write / read /
  tangents`).
- Round trip on 6 pieces (`tmp/probe/fbx_roundtrip.py`): positions / UVs identical, normals within 9e-7.
- **Import test (BFII_EXACT for the importer, `tmp/probe/fbx_import_test.py`, log `logs/fbxtest.log`, 14 s):** pieces
  MU_0204 (44 tris) and MU_0000 (388 tris) imported twice into a COPY of the Mustafar shell project, from Blender's FBX
  (A) and the runtime's (B), both geo-dumped (`tmp/probe/fbx_geo_cmp.py`): same vertex declaration (Pos Half3 /
  BinormalSign / Normal / Tangent / TexCoord0 / TexCoord1 / RadiosityTexCoord, stride 36 + depth section), same
  bounding boxes, same triangle order; positions, TexCoord0, TexCoord1 identical; normals within 0.0005 (Half);
  vertices 104 / 104 and 660 / 647.
- Tangents vs Blender's mikktspace over ALL 514 Mustafar pieces (`tmp/probe/fbx_tan_all.py`): 142,068 corners of
  triangles with a uv area; 555 (0.39 %) differ by more than 26 deg; 2,676 triangles have no uv area (direction is
  arbitrary there). First version merged mirrored halves at uv seams (found on MU_0000: 3 triangles) -> corners are
  now grouped by uv handedness as mikktspace does. Whether the remaining 0.39 % shows on a normal-mapped surface is
  INFERRED (no), not seen in game.
- Not yet measured: vertex colours through the FBX (`LayerElementColor`, needed for the lava preset only).

### Runtime core + first three stages, proven against the Mustafar port (00:44 - 01:00)
Files: `runtime/bf2maptool/` `config.py` (map.json v1 + settings.json -> `Job`: every asset name, GUID and folder is
derived from `map.name`), `runner.py` (`build.json`: per stage inputs hash + result; `plan` says what is redone and
why; lock file per work folder; `@@STAGE` / `@@DONE` progress feed for the add-on), `cli.py` (FrostyCLI pipelines and
direct calls, one log per call, error / transient-failure parsing), `audit.py` (closure / validate / lint parsers,
partition dumps), `hosts/kamino_01.py` (ALL host facts: placement rule, templates, clean-up lists, load gate, donors),
`pipeline.py`, `__main__.py` (`python -m bf2maptool port|plan <map.json> --settings <settings.json>`),
`stages/{split,textures,collision,shell,importer}.py`, `texpack.py`, `fbx.py`, `fbxbin.py`, `meshgeo.py`.
Blender side: `addon/bf2_map_tool/props.py` (scene / object / material / light properties), `export_scene.py` (scene ->
`map.json` + `geo/*.npz` + `tex/*.png`; runs in the UI or headless on a saved copy).
Machine settings of THIS PC: `I:\BF2MapTool\settings.json` (not part of the shipped tool). Work folder
`I:\BF2MapTool\work\<Name>\`.

Regression data (throw-away, in `tests/mustafar/`, never shipped): `tag_and_export.py` (opens the port's
`mus_fixed.blend` read-only, tags classes from the port's plan, runs the tool's export), `patch_map.py` (the port's
hand choices: play centre, the lit blend's maps per material), `compare_split.py`. Test level name `MustafarT`
(`AddedContent/MustafarT/...`) so it can sit next to the port's own mod.

| stage | time | result | against the port (measured) |
|---|---|---|---|
| scene export (Blender 5.2 headless) | 27 s | 90 objects, 48,099 triangles, 52 materials, 22 images, 30 lights | the fixed blend's 90 meshes / 48,099 triangles |
| `split` (numpy, no Blender) | 9.6 s | **514 pieces, 50,032 triangles, 50 materials, largest 2,399** | identical counts; 514 / 514 pieces have the same material, triangle count and source objects at the same index; total area 478,651.5 m2 both; 450 pieces identical triangle for triangle (normals within 0.0095, uvs equal modulo whole numbers); 23 pieces hold a different but equal-area tiling (quads cut the other way / median ties); uv groups moved 3,252 (port 3,258); pieces with abs(uv) > 4: 21 / 21 |
| `textures` | 27.7 s | 50 materials (27 opaque, 14 emissive, 5 cut-out, 4 lava) -> 45 textures; normal maps: 14 materials DirectX, 4 OpenGL (flipped) | **all 118 material-slot textures pixel-identical to the port's `tex/final`** (`tmp/probe/cmp_tex.py`) |
| `collision` | 21.8 s | 366 pieces solid, 148 (lava) without, 47,884 triangles, **max error 11.55 mm** | port: 366 non-lava entities, max error 11.55 mm; the piece frame equals the port's raw OBJ (same 42 vertices on MU_0204) |

Re-run of the three stages with nothing changed: all `skip up to date` (state file works).

Generic rules now in code (were hand choices): placement offset = host rule from the play centre (reproduces the
port's (-96, 340, 77) from (10, 30, 68)); lava class is never solid; double-sided = reversed copies in the same piece
(left out of collision); a custom collision mesh rides on the first piece of its object; flat NAM = 4 x 4 texture;
cut-out without see-through texels is an error; AO is read but not packed (no channel on these templates; the bake
supplies occlusion) and says so in the plan.

SLIP (mine, 00:50): a stray `python -` at the start of one shell command hung it (the rule exists for exactly this);
stopped, nothing was written by it, redone with the Edit tool.

Written, NOT RUN YET (01:00 - 01:08): `stages/importer.py` (`import`: packages, texture import with the slot's shipped
template, one `port import` per piece, bounded retry rounds `_R<n>` for the transient clone failure; `place`: LOD
group, depot bundling, emissive strength, RaycastRootShapeIndex 1, one static group + collision entities, then a
read-back census of `MemberDatas[i].MeshAsset` against the plan), `stages/shaderdb.py` (own DX11 database: host image
+ the surfaces of templates whose `home_db` is another level, merged with the vendored tool, VERIFY PASS required,
imported as the level's own res, read back byte for byte), `runtime/bf2maptool/vendor/rvm/` = copy of HothPort's
`rvm_merge.py` + `rvmlib.py` + `xlate.py` + `rvm_sizes.py` + `cityhash64.py`, machine paths removed, hash through
`cityshim.py` (pip `cityhash` when installed, else the pure-Python file).
Emissive strength is PROVISIONAL: `hosts/kamino_01.EMISSIVE_PER_UNIT = 280` (the port's last by-eye value; the regression
map patches the same number in). The documented conversion rule is owed at the lighting step - graded UNKNOWN until then.
Another session is using the PC (a Coruscant-port FrostyCLI process, not mine, not touched).

### Shell + shader database stages RUN (01:01 - 01:08)
| stage | time | result | against the port (measured) |
|---|---|---|---|
| `shell` | 309 s | level create (Kyber: SkirmishBlast, SkirmishOnslaught, SkirmishDuel, Blast, HeroesVersusVillains; navmesh stream c916f1b9-...), 56 partitions, 10 registries, 36 host doors + 4 VE volumes removed, load gate added, world box 8192; closure 0 unresolved / 0 missing / 0 root-late / 0 stage-late; validate "no issues found"; read-back: 0 references into dropped host layers, 0 host doors | **all 56 layers have the same Objects / Event / Property / Link connection counts as the port's cleaned shell `dumps/S0f`** (`tmp/probe/cmp_shell.py`); the port removed the same 36 + 4 |
| `shaderdb` | 14 s | host image exported from the game: 42,616,317 bytes; cut-out surface `SS_PropsNonMetallicPreset_AlphaTest_TextureArray_01` merged from Tatooine_01's own database: closure 4,057 blocks (1,315 already in the host, 2,742 added), merged 43,031,043 bytes, **VERIFY PASS**, imported as the level's own res, read-back byte-identical | host image size = Mygeeto's export (42,616,317); the port's closure of this surface was also 4,057 blocks |

Found on the way: (1) the merge tool's self-check also needs `rvmdb.py` + `ptrclass.py` (first run failed on the
import; copied, cleaned, the FAILED stage was simply re-run = resume works); (2) a pipeline whose pieces fail one by
one exits non-zero although it saved its project - the first import run (493 of 514 imported, 19 transient clone
failures, as on the port) was wrongly treated as fatal; the import now judges by the saved project + the log;
(3) my build log echoed every CLI WARN line (232 KB for one import): now only errors and result lines are echoed,
everything stays in the per-call log.

Written since (not run yet): `stages/nav.py` (region and height band from the play area and the solid pieces, bundled
navgen, empty-navmesh and too-tall checks), `stages/spawns.py` (generalised `gen_spawns_mus.py`: hand-placed spawns
first, then generated on the main navmesh part, kill volumes / not-walkable boxes excluded, scene combat area or hull
+ 40 m, team-distance check, `spawns.json` + `spawn_map.png`), `stages/finish.py` (`audit`: closure with the
root-late repair loop that only ever bundles class + class_schematics PAIRS, validate, lint, schematics-pair check on
the real bundle listing, group census; `mod`), `chain.py` (project chain table).

### Import: 2 of 514 runtime-written FBX files REJECTED by the importer - cause located, fixed (01:14 - 01:19, machine clock)
- Symptom: pieces MU_0053 and MU_0099 failed in the first run AND in all four retry rounds with "Mesh must be
  exported with at least one UV channel (in FBXImporter.ProcessSection)" - so not the transient clone failure (my
  retry loop treated every failure as transient; the error text is now what decides).
- RETRACTION: the "Default 1 measured" entry above called the runtime's FBX files proven on TWO pieces. Two pieces
  were not enough: 512 of 514 imported, 2 did not.
- Bisect (`tmp/probe/fbx_bisect.py`, variants of MU_0053 imported one by one; port's Blender FBX of the same piece
  imports): either half of its triangles alone imports; the full piece fails as written and after a plain rewrite;
  it imports when EITHER uv set is written un-indexed, when the arrays are compressed at zlib level 6, or when the
  arrays are not compressed at all; shifting every file offset by 1 or 7 bytes does not help; no degenerate or
  repeated-index polygon, uv counts odd in 232 other pieces that import. So: the data is fine, the zlib LEVEL-1
  stream of this file is what the importer's FBX reader does not take (the same stream reads back with zlib and with
  my own reader). WHY the reader rejects it: UNKNOWN.
- Fix: `fbxbin.py` writes every array uncompressed (FBX encoding 0; no zlib on the importer's side at all). MU_0099
  confirmed importing after the fix. Split now takes 1.4 s; the 514 files are 15 MB -> larger, still small.
- Re-run: `import` 150 s: round 0 491 imported + 23 transient clone failures, round 1 23 / 23 -> **514 / 514**
  (templates: opaque 299, emissive 198 = 50 strips + 148 lava placeholder, cut-out 17 - the port's 299 / 50 / 17 / 148).
- SLIP (mine, second time, ~01:19): an empty `python -` command sent by habit; it hung, I stopped it, nothing ran.
  Rule for the rest of this job: scripts are written with the Write tool and run by path, nothing else.

### FIRST MOD from the runtime (01:19 - 01:27): `work/MustafarT/mods/MustafarT.fbmod` (111,587,166 bytes)
`python -m bf2maptool port tests/mustafar/export/map.json --settings settings.json`, one command, headless, exit 0.
| stage | time | result | against the port (measured) |
|---|---|---|---|
| `import` | 150 s | 514 / 514 pieces, 45 textures | see above |
| `place` | 55 s | ONE static group with 514 members (read back from the Default layer: every `MemberDatas[i].MeshAsset` = the planned mesh), 366 collision entities, offset (-96, 340, 77), emissive scale 280 | the port's offset; 366 entities as MUS4 |
| `navmesh` | 27 s | 1,392 polygons from 89,906 collision triangles; region x -246..74, z -113..207, height band 385..430; 515 no-collision meshes left out (lava among them) | port: 1,370 polygons from 89,690 triangles on a smaller hand-picked region (x to 24, z to 167) |
| `spawns` | 17 s | main navmesh part 1,103 of 1,392 polygons, 12,619 m2 (69 parts); 697 safe spots of 2,087 sampled; **Blast bases 168 m apart, 12 + 12 + 27 neutral; Heroes vs Villains starts 107 m apart, 13 + 16** | port: main part 1,101 polygons / 12,623 m2; bases 168 m apart, 12 + 12 + 27; HvV 13 + 16, 107 m apart - the same numbers |
| `audit` | 202 s | closure 0 unresolved / 0 missing / 0 root-late / 0 stage-late (4 members added by the closure); validate "no issues found"; lint 0 errors / 317 warnings; 61 class schematics, all with their class; group census 514 / 514 | port MUS3: same closure result, closureAdded 4 |
| `mod` | 11 s | 111.6 MB | port MUS3 (same content: full geometry, placeholder lava, no bake): 112.0 MB |
Whole run from an empty work folder: about 15 min (shell 5, import 2.5, audit 3.5, the rest under 1 each).
NOT in this mod yet: lights, own visual environment (it shows the host's stormy sky and lighting), doors, kill
volumes (the host's fall-kill slabs are still at the host's height), bake, lava preset (lava = emissive placeholder).
Graded: built and audited = BFII_EXACT for the project content; "loads / looks right" = UNKNOWN until the game run
below is read.

### Kill-volume stage WRITTEN, not run (01:29)
`stages/kill.py`, in the chain after `spawns`: the scene's kill boxes (oriented) go onto the host's three kill boxes
(`hosts/kamino_01.KILL_OBBS`, read from the port's build: the kill logic lives in GameModes_Flow and links to three
OBBData of GameModes_Shared that a layer dump does not show - they are edited and read back by GUID with
`ebx dump <layer> --object <guid>`); more boxes than the host has = an error that says so; one left over = the
automatic "below the map" slab (10 m under the lowest geometry, 300 m deep); unused boxes are parked on it; every
navmesh corner is tested against every scene box (walkable floor inside a kill box = error naming the box); every box
is read back and compared. To run: add the port's three lava boxes to the regression map.json first.

### Doors: design fixed, stage not written yet
Per door object: frame by PCA (normal = thinnest axis, sign made deterministic), slide vector from the chosen side
(up / down / left / right in the door's own plane), prefab frame x' = slide, z' = normal, y' = normal x slide (the
port's turned frame, generalised); doors with the same local geometry + material + slide share one panel asset and
one prefab clone; hull collision = HavokCollisionTool `import both --hull=always` on the host panel's physics res
(exported from the game once, cached); one `level-add` per door. Needs: door materials packed by the textures stage,
the throw-away export to bring the port's 18 door panels into the view layer tagged role = door, slide = up.

### First game launch STALLED in the harness - ended by me; watchdogs added (01:27 - 01:35)
- 01:27:48 `FrostyCLI mod review ...` started the FrostyAutoTest task. Until 01:32 the harness wrote NO log line: its
  elevated PowerShell sat in `csc.exe` (the script's `Add-Type` compiles come before its first log line; the second
  compile had started at 01:30:13 and used 0 s of CPU in two minutes). No Mod Manager and no game were ever started,
  so nothing about the mod was tested. A plain `Add-Type` compile in a normal PowerShell took 8.1 s at 01:33: why the
  elevated one stalled is UNKNOWN (not my mod: it never got that far).
- USER (01:31): "It's stuck on the power shell command - make sure in the night that never happens for a long time."
- Done: `schtasks //End //TN FrostyAutoTest` (the run I started; task back to Ready), my waiting `mod review` process
  stopped; nothing of mine left running (3 older PowerShell processes belong to other sessions, not touched).
- RULE FROM NOW ON (also saved to memory `no-long-hangs-unattended`): nothing waits without a limit.
  * `tools/game_test.py` (my test driver, machine-specific, not shipped): refuses to start when a game or the harness
    is busy (BUSY); 360 s for the harness's first log line (HARNESS_STALL -> ends the task run); 600 s for a verdict;
    1,200 s over everything; every `game deploy / where / shot` call has its own limit; the harness closes the game
    itself (hold + `hold.release`); the new part of `gamelink.log` / `autotest.log` and a `result.json` are always
    written. `tools/make_shots.py`: six viewpoints from the build's own spawn plan.
  * runtime `cli.spawn`: every tool call is stopped with everything it started when it runs longer than 45 min or
    prints nothing for 15 min (tested with a sleeping process: stopped after 8 s with a 6 s limit, message names the
    limit); the collision tool has 5 min per piece; stdin is closed for every tool.
  * me: long jobs run in the background with a done-marker; I do other work meanwhile; a stalled job is ended, logged,
    retried once at most.

### FIRST IN-GAME RESULT (01:35 - 01:48) - TEST POINT 1, partial, seen by me and by the user live
Run: `tools/game_test.py` (second try). Harness first line after 30 s; Frosty Mod Manager took 6 min 15 s on the
saturated C: drive; game injected 01:44:28; **`LOADED LEVEL IS 'AddedContent/MustafarT/Levels/MustafarT_01/MustafarT_01'`
at 01:44:47, no `fault:` line** (`work/MustafarT/review/t1/gamelink_new.log`). My wrapper's fixed 600 s verdict limit then
ended the harness 19 s after the load (my limit was wrong: it punished slow progress) - the game stayed up, so I drove
it directly: `tools/game_shots.py` (deploy + 6 shots in 68 s), `tools/game_stand.py`, then closed the game I had started
(`taskkill //PID 37956`; EADesktop left alone).
- `game deploy`: "the local player controls 0x10FB20320 (was 0x0)" -> spawning works (BFII_EXACT for this build).
- Standing test: camera at (-111.71, 407.84, 63.75) four samples 3 s apart, height unchanged -> the floor holds a
  soldier (stand-on collision, BFII_EXACT at that spot). Weapon-fire collision: NOT tested.
- `game send players`: 14 bots control a soldier -> bot spawns work. That they walk and fight: NOT observed.
- Shots `work/MustafarT/review/t1/*.png`: `base1_to_mid` shows the landing platform, a door frame with glowing strips,
  textured walls and a patch of orange lava (placeholder) under the host's dark stormy lighting; `over_mid` shows the
  refinery floors, rock and lava sheets textured from above - washed-out grey.
- **DEFECT, reported by the user watching ("out of the boundaries everywhere you looked") and confirmed in the data:**
  the Skirmish out-of-bounds polygon (`TeamDeathmatch_Skirmish_Logic` VolumeVectorShapeData) ships with Height 20 m, the
  defend areas 30 m, the Blast combat area 0. The port's early smoke build had set Height 400 on all of them by hand and
  every later port build inherited it; `gen_spawns_mus.py` (and my generalisation of it) only moved the outline to
  floor - 60 m, leaving a 20 m slab 48 m under the floors. RETRACTED: my first reading of the soldier's death after
  12 s ("shot by bots") and of the grey overview ("camera effect") - both were the out-of-bounds state.
  FIX (in `stages/spawns.py`, v3): Height is always written (base 60 m under the lowest spawn floor, top 340 m over
  the highest), and after saving the three layers are dumped again and checked: every volume's Height as planned, every
  Blast / Arcade spawn inside the outline and between base and top of both play volumes. New row 38 in the table.
- Not in that build: doors, kill volumes, lights, own sky, bake.

### Doors + kill volumes RUN (01:40 - 01:56)
- `doors` (20 s): **18 doors of 3 types: 5 x (4.32 x 3.66 m, opens 4.06 m), 2 x (4.06 x 3.00, 3.82), 11 x (4.17 x 5.20,
  3.92)** = the port's panel types A / B / C and its 0.94 rise. Prefab clone read back: same 5 objects and 2 / 13 / 5
  connections as the port's working prefab dump. Two mistakes of mine on the way: types were keyed on a hash of
  coordinates (float noise made 12+ types; now material + triangle count + sizes to 2 cm + travel), and my read-back
  called the orphaned sound instance a failure (the port's working prefab has the same orphan; the check now looks at
  the prefab's Objects and connections).
- `kill` (22 s): the port's 3 lava boxes on the host's 3 kill boxes; 2,989 navmesh corners tested, none inside;
  read-back difference 0.00000.
- **Doors cut the navmesh (measured):** with doors placed before the navmesh, `navmesh build` left out 25 of the 36
  leaf slots ("door leaves left out") and kept 11: collision triangles 89,906 -> 90,786, main walkable part 1,103 ->
  501 polygons (12,619 -> 6,028 m2, 78 parts). FIX: door panels are skipped by name (`--skip <PFX>_Door_`). Row 39.
- Runner flaw found: stage input hashes were all computed before the run, so a stage whose inputs an earlier stage
  rewrote in the same run stored a stale hash and was redone next time (a needless 514-piece re-import, noticed by the
  user). FIX: each stage is hashed right before it would run (`runner.decide`).

### Add-on STARTED (step 4, 01:50 - 01:57) - written, not yet loaded in Blender
`addon/bf2_map_tool/`: `__init__.py` (register), `props.py`, `prefs.py` (paths + Check setup lines), `build.py`
(the build as ONE subprocess outside Blender: saves a copy of the scene, starts `python -m bf2maptool build ...`,
a timer reads the `@@STAGE / @@RESULT / @@MOD / @@DONE` feed, Cancel kills the process tree), `ops.py` (Check setup,
Port map, Check scene, Bake lighting, Rebuild mod, Cancel, Open log, select-from-result, Guess classes with the reason
per material, Add-menu entries for spawns / hero spawns / command post / kill volume / not walkable / combat area /
reflection probe / 1.8 m figure, role and collision setters), `ui.py` (sidebar "BF2 Map", tabs Map, Materials,
Collision, Gameplay, Lighting, Check, Build), `export_scene.py`.
Runtime side: `__main__.py` commands `build` / `check` (headless Blender export first, with limits) and `check-setup`;
`setupcheck.py` (**run on this PC: 13 lines, all ok** - game folder, FrostyCLI answers, plugins, .NET, collision tool
answers, template, baker, Blender, work folder 104 GB free, numpy / PIL / scipy / xatlas); `checks/__init__.py`
(first pre-flight set: no geometry, no mode, Supremacy posts / not built yet, faces without material, no UV map,
degenerate faces, mirrored / unapplied scale, extent against the host, tiny / huge scene, per-material pack dry run
with the texture error codes, unsupported class, unsupported nodes, no Principled, guessed classes, kill volume count,
spawn in kill volume, teams too close, no spawns, door materials / direction / shape, dynamic light budget, saturated
light colour). The full list with one test scene per check is step 5.
`bake` answers "not built into the tool yet" (honest refusal until step 7).

### USER DIRECTION (01:52 - 01:55) - changes steps 9 / 10
1. "Are you even making the blender tool, what's this reimport of the map?" -> the panel had not been started; started
   now, in parallel with the regression.
2. "Maybe later on test on a map that's never been ported like Rhen Var."
3. "Make sure the thing works and test on multiple maps of different things; my system has a maps folder in F drive,
   find suitable candidates; make sure all the features are complete and then test on multiple maps, importing maps, so
   it doesn't just work for one map in one game."
-> Maps 2 and 3 (and more) come from different sources. Survey (`tests/survey/`, 16 blends, 5 s each,
   `tests/survey/out/*.json`), measured:

| blend | source | objects / triangles | extent m | materials | what it will test |
|---|---|---|---|---|---|
| SWBF2_Rhen_Var_Map (OG 2005, never ported) | SWBF2 2005 | 843 / 435,025 | 3,967 x 3,989 x 3,216 (sky dome) | 72, all Principled + base colour | the Mustafar kind on a 9 x larger map; snow terrain materials; sky dome must be set aside |
| Cantina.blend | unknown (PBR) | 220 / 213,341 | 31 x 15 x 8 | 39 Principled: base 39, normal 36, emission 9, alpha 1 | small interior; normal maps, emissives, 15 point lights (baked / dynamic) |
| TDM_Center_01 | Star Wars: Hunters | 940 / 696,503 | 220 x 222 x 44 | 141, 134 on custom node GROUPS | a built PvP arena; materials inside node groups |
| TDM_Bastion_A, GEN_Contrast, GEN_Laren, CP_Chasm_02 | Star Wars: Hunters | 630 - 1,173 / 0.5 - 0.7 M | up to 50,000 (sky) | 65 - 122, mostly groups | same family, larger |
| SwtorImperialBridge | SWTOR | 200 / 187,628 | 600 | 135, 127 on groups; 196 of 235 images external | another game; external images; armature modifiers |
| Tantive IV interior | a model shop file | 190 / 162,098 | 4.5 x 15 x 4.3 | 12, procedural nodes; 3 of 4 images missing | checks: missing images, scale, Mirror / Bevel / Geometry-nodes modifiers |
| ISDBrfRm, Reb_Brf_Rm, HngrImp, Landing Platform, CR90 | fan scenes | 0.5 - 3.1 M triangles | 20,000 - 50,000 (sky) | PBR + groups, 12 missing images in one | size limits, sky objects, >4096 images |
| Hunters Huttball | Star Wars: Hunters | 364 / 228,595 | 26,599 | 67, none Principled | 866 lights: the dynamic-light budget |
| LEGO Invasion of Naboo | OBJ file | not surveyed yet | | | a map that is IMPORTED into Blender first |

   What the survey already says the tool lacks (to build before map 2): **materials built from node groups** (most
   ripped game scenes: the exporter must look inside groups / follow group inputs / fall back to image-name suffixes),
   a way to mark **sky / backdrop objects** (extents of 20 - 50 km), image files that are not packed.
   Plan: map 2 = Rhen Var (same family, never ported), map 3 = Cantina (small interior, PBR + lights), map 4 =
   TDM_Center_01 (other game, group materials), then the OBJ import and one "bad" scene (Tantive IV) for the checks.

### Rebuild with the fixes (01:57 -)
`navmesh` with door panels skipped: 1,392 polygons from 89,906 triangles again (the numbers from before the doors);
`spawns`: main part 1,103 polygons / 12,619 m2, 697 safe spots; **play area base y 338.5, height 417.5 m, 5 volumes
written and read back, 0 spawn / volume pairs outside**; `kill`: 3 boxes, 3,018 navmesh corners tested, none inside,
read-back 0.0. Audit + mod: running.

### Add-on: packaged, installed, button pressed (01:57 - 02:01)
- `tools/make_zip.py` -> `dist/bf2_map_tool-0.1.0.zip` (43 files, 130 KB): the add-on with the runtime inside it
  (`bf2_map_tool/runtime/bf2maptool`), so ONE zip is the whole install.
- `tests/addon/test_install.py` (Blender 5.2, `--factory-startup`, throw-away profile `tmp/blender_scripts` so your own
  Blender profile is untouched): `preferences.addon_install` (the operator behind "Install from Disk") + enable ->
  enabled, panel class registered, properties on Scene / Object / Material / Light, all 12 operators present, the nine
  Add-menu objects created with their roles, Guess classes gives ("emissive", "emission is wired or coloured, strength
  5") and ("opaque", ...), **Check setup through the operator: 13 lines, all ok**. Measured headless: the dialog itself
  was not clicked and the panel was not drawn in that test.
- Own test map, built from nothing (no port data): `tests/scenes/make_arena.py` -> `tests/scenes/arena.blend`, level
  `ToolArena`: 80 x 60 m arena, pit with a cut-out grate bridge and a kill volume under it, platform + ramp, crates (one
  mirrored, one with unapplied scale), inner wall with a doorway and a door that slides up, emissive strips without
  collision, 5 materials with generated packed textures (one OpenGL normal map), point lights baked / dynamic / both,
  6 hand-placed team spawns.
- `tests/addon/test_port_button.py` in a WINDOWED Blender on that scene (running): the Port map operator is called by a
  timer inside Blender; a 0.25 s tick measures how long the interface is ever blocked; Cancel when `collision` starts;
  then Port map again. Seen so far in the feed: export 1.3 s (24 objects, 288 triangles, 5 materials, 6 images), the
  two expected check lines (mirrored crate = warning, unapplied scale = info), run 1 cancelled in `collision`, run 2:
  `split` and `textures` "skip up to date", `collision` redone ("last run: running") 8 pieces / 7.73 mm, `shell` started.

### Exporter: materials built from node groups (02:00)
The survey shows most ripped game scenes keep their shader in node groups. `export_scene.py` now: finds the Principled
BSDF inside (nested) groups; follows a link INTO a group (through its Group Output) and back OUT through the group's
inputs; takes the first image-bearing input of a colour Mix and says so (`approximate`); passes Hue / Saturation,
Bright / Contrast, RGB Curves through with a note; and, where it still cannot follow a chain, sorts the material's own
images by what their names say (`_n`, `_nrm`, `_e`, `_rgh`, `_albedo`, ... -> `by_name`), base colour = the largest sRGB
image as a last resort, always marked. First run on a Star Wars: Hunters arena is in the background.

### Results of the three jobs (02:02 - 02:09)
- **Mustafar rebuild DONE:** audit 270 s: closure 0 / 0 / 0 / 0, validate no issues, lint 0 errors / 320 warnings, 64
  class schematics all paired (61 + the door logic), group census 514 / 514; mod 111,688,982 bytes.
- **Hunters arena export (TDM_Center_01, read-only) DONE:** 940 objects, 696,503 triangles, 141 materials, 336 images.
  Base colour: 137 traced to an image THROUGH node groups, 2 found by image name, 2 constants; roughness 137, normal
  128, metallic 107, alpha 100, emission 137 (all "approximate": the group shader mixes it). Still not followed:
  emission strength behind a Value node (76; Value / RGB / Combine nodes handled since), alpha behind a Mix (37),
  normals rebuilt by Combine Color (11). Not built or seen in game.
- **Port map button test DONE** (`tests/addon/button1.json`, windowed Blender 5.2, scene `tests/scenes/arena.blend`):
  the operator returned after 0.53 s; 1,874 timer ticks over 477 s, **longest pause of Blender's main thread 1.4 s (one
  pause over 1 s)**; Cancel when `collision` started: build stopped, process dead 4 s later; second press: `split` and
  `textures` skipped, the rest ran: shell (new level `ToolArena` created), shaderdb, import, place, doors, navmesh -
  then **`spawns` stopped the build: "Only 0 safe spawn spots ... (4 m2)".**
  CAUSE (measured, `tmp/probe/arena_dbg.py`): my collision stage skipped pieces whose OBJ and .res already existed,
  but took "has collision" from the report of the last COMPLETE run; the cancelled run had written 9 .res files and no
  report, so on resume those 9 pieces (all the floors) were skipped AND left out of the report -> 8 of 17 solid ->
  navmesh of 2 polygons. FIX: a piece counts as converted only when the last complete report lists it; the stage fails
  if planned and reported pieces differ. Re-run: 17 solid, 1 without, 408 triangles, 7.73 mm. Row 42.
  This is what "a killed build resumes" has to mean; without the Cancel test it would have shipped.

### Game test wrapper: two more runs ended too early by ME (02:03 - 02:08)
- t2 first try: I saw no harness log line for 3.5 min and its PowerShell at 0.44 s CPU, called it stuck, ended the task
  run at 02:07:08. The harness log shows it had written its first line at 02:06:53 (3 min 20 s after the start) and
  started Mod Manager at 02:06:56. It was SLOW TO START, not stuck. RETRACTED: "stuck again" as told to the user.
  The ended run's Mod Manager (elevated; `taskkill` = access denied) kept going and launched the game on its own.
- USER (02:05): "make sure to check the PowerShell thing every time it's stuck."
- `tools/game_test.py` now: watches BOTH harness logs by size (the harness writes to `autotest-2.log` whenever a
  reader has `autotest.log` open - my own `tail` calls did that); when the logs are silent for 60 s it LOOKS at the
  harness: its PowerShell's CPU seconds and what it started (csc, Mod Manager, game), every 60 s, recorded in
  `result.json` (`harness_checks`); "stuck" = logs silent 480 s AND those processes idle for 180 s; `--leftover-ok`
  lets the next run start while a manager / game of my own ended run is up (the harness's first step closes them).
  Memory `no-long-hangs-unattended` updated with this.
- t2 second try (02:08): harness closed the leftover game, injected at 02:09:02 - running.

### GAME TEST t2 of the rebuilt Mustafar mod (02:08 - 02:13) - TEST POINT 1, run and read by me
`tools/game_test.py ... --out work/MustafarT/review/t2` (299 s in all; harness first line after 5.3 s, world up after
100 s; the harness closed the leftover game of my ended run first, as intended).
- `LOADED LEVEL IS 'AddedContent/MustafarT/Levels/MustafarT_01/MustafarT_01'` at 02:09:17, **no `fault:` line**.
- `game deploy`: the local player controls a soldier. Camera height 407.84 at four samples 3 s apart (drop 0.0) ->
  the floor holds. 14 bots control a soldier.
- **In bounds now:** `over_mid.png` is in full colour (t1's was the grey out-of-bounds picture): orange lava
  (placeholder), the refinery's floors, rock, green screens. Sent to the user.
- **Textures / emissives / door seen:** `hero1_to_mid.png` (interior): the 2K floor pattern, wall panels, green radar
  screens and ceiling light strips glowing, and **a door panel standing closed in its doorway**.
- NOT shown by this test (still INFERRED): that a door opens when someone walks up (same prefab recipe as the port,
  whose doors the user saw working); that the lava kill boxes kill; weapon-fire collision; cut-out grates (no shot
  aimed at one); bots moving and fighting; Heroes vs Villains and Arcade loads.
- Lighting is still the host's (stormy sky, no own lights, no bake): expected at this step.
- The wrapper mis-read the harness's final verdict (status stays `LOADED`, the end of the hold is in `hold`), waited
  its 120 s and reported `CLOSED_BY_TEST` although the harness had closed the game itself; fixed.

### Arena build (the tool's own map, not a port) (02:09 - )
After the collision-resume fix: `import` 18 / 18 in one round (16 opaque, 1 cut-out, 1 emissive; 9 textures), `place`
18 members / 17 entities at offset (-86, 408, 46), `doors` 1 door, `navmesh` 93 polygons from 816 triangles, height
band 397..415, `spawns` main part 81 polygons / 4,123 m2 (5 parts), 405 safe spots, hand-placed 3 + 3 used.
`kill` first stopped the build: "Kill pit holds walkable floor (18 navmesh corners ... z -7.9)" - the pit's own
bottom. The rule was too blunt: walkable floor = the MAIN connected part of the navmesh; an island inside a kill box
is the pit the box is for, reported, not an error. Re-run: 1 scene volume + the automatic "below the map" slab, 149
corners of the main floor tested, none inside, "Kill pit: 18 corners of isolated floor (a pit bottom)". Audit running.

### Lights: shipped records read from the game (02:12)
`level export-lights Levels/MP/Kamino_01/Kamino_01 --types lights` -> `tmp/probe/kamino_lights.json`: 555 lights
(424 PbrSpotLightEntityData, 100 PbrSphereLightEntityData, 26 PbrRectangularLightEntityData, 5 PbrTubeLightEntityData),
every one in `LightUnitType_LuminousPower` (lumens). Shipped values (BFII_EXACT): sphere 1,500 - 15,000 lm, radius
3 - 25 m, 72 of 100 with AffectRadiosity; spot 1,000 - 15,000,000 lm, OuterAngle 75 - 177, InnerAngle 0 - 99, 6 of 424
cast shadows (`QualityScalableEnabled_Low`); rectangular 5,000 - 15,000 lm with Width / Height / OuterAngle; fields
`DirectLightmapEnable`, `AffectRadiosity`, `CastShadows`, `Dimmer`, `RadiosityColorScale` exist on all. These are the
templates and the scale for the lights stage (not written yet).

SLIP: I used `python -c "..."` a few times tonight to print JSON. It does not read stdin and never hung, but the rule
says no `python -` in any form: `tools/show_json.py` replaces it; no more one-liners.

### ARENA: built and SEEN IN GAME (02:14 - 02:18) - the first level that is not a port
- Build: audit 195 s: closure 0 / 0 / 0 / 0, validate no issues, lint 0 errors / 19 warnings, 64 class schematics
  paired, group census 18 / 18; `work/ToolArena/mods/ToolArena.fbmod` 9,202,695 bytes; level
  `AddedContent/ToolArena/Levels/ToolArena_01/ToolArena_01`.
- Game test `tools/game_test.py ... --out work/ToolArena/review/t1` (whole run about 3.5 min; status RELEASED - the
  harness closed the game itself; no fault line): world up after 95 s, player deployed, 13 bots with a soldier.
- Seen in the eight shots (`work/ToolArena/review/t1/*.png`; three sent to the user):
  * `overview`: the arena as modelled - checker floors, perimeter and inner walls, platform and ramp, crates, the pit
    strip, the blue door, three glowing strips; bots spread over both halves, in full colour (in bounds);
  * `grate_bridge`: **the cut-out grate shows its holes (the pit is visible through them) and a bot stands ON it** -
    cut-out class and its collision on a map that is not Mustafar;
  * `door`: the door panel closed in its doorway; two bots walking past - bots MOVE (seen), fighting not seen;
  * the emissive strips glow.
- Player standing test: height drop 0.85 m over 9 s (first sample taken while the soldier dropped from the spawn
  height onto the floor; not a fall-through, which would be metres) - weaker evidence than Mustafar's 0.0.
- Open questions from these shots (not judged yet):
  1. the wall's generated normal map (detected OpenGL, integrability 0.983, green flipped) reads as diagonal stripes
     whose direction seems to change across some triangle edges. A cos x cos field does make diagonal ridges, so the
     stripes may be right, but a direction change across the diagonal of a flat quad would mean the two triangles get
     different tangent frames. Needs a calibration texture with an unambiguous picture (one dome per tile, an arrow)
     before the acceptance item "normal convention detected" can be called proven in game. UNKNOWN.
  2. the door did not open in the shot although a bot stood a few metres away; whether it opens at all is still not
     shown on any tool-built map.
  3. my test scene has a 4 m gap beside the doorway (wall and lintel do not meet) - scene mistake, to fix in
     `make_arena.py`.
  4. lighting is the host's stormy sky on both maps; own lights (4 in the scene) are not in this build: the lights
     stage was written after it.

### Lights stage WRITTEN, not run (02:14 - 02:16)
`stages/lights.py` in the chain after `doors`; `hosts/kamino_01_lights.json` = the most common shipped value of every
field per light type, from the game's own 555 records (`tools/make_light_templates.py`). point -> sphere light, spot
-> spot light, area -> rectangular light; mode switches Enabled / AffectRadiosity / DirectLightmapEnable; shadows =
`QualityScalableEnabled_Low` for dynamic / both; read-back: the layer is dumped and the light entities counted per
type with their lumens. PROVISIONAL and graded UNKNOWN: lumens = watts x 10; colour scaled to a largest channel of 32;
spots shine along -Forward (INFERRED from the host's lights: 92 % of its spots have Forward pointing up).

### ONE BUTTON PRESS -> MOD (02:19 - 02:24), `tests/addon/button2.json`
Windowed Blender 5.2, `tests/scenes/arena.blend` (regenerated: gap beside the door closed, a red spot light pointing
straight down, the wall's normal map replaced by one dome per cell), add-on from the rebuilt zip (45 files).
`bf2.port` returned after 0.37 s; the build ran outside Blender for 276.5 s: export -> checks -> split -> textures ->
collision -> shell (up to date) -> shaderdb -> import -> place -> doors -> **lights (first run: 5 lights = 2 dynamic,
1 both, 2 baked; read back from the layer by type and lumens)** -> navmesh 92 polygons -> spawns (main part 80
polygons / 4,115 m2) -> kill -> audit (closure 0 / 0 / 0 / 0, validate no issues, lint 0 errors) -> mod
`work/ToolArena/mods/ToolArena.fbmod` (9,217,736 bytes); feed ended `@@DONE ok` + `@@MOD <path>`.
**Blender's main thread: 1,086 ticks, longest pause 0.47 s, none over 1 s.**
Together with the first button run (Cancel in `collision`, process dead, resume skipping finished stages, new level
created from nothing) this is the measured basis for the acceptance items "one button ports a scene ... Blender
stays responsive; Cancel works" and "a killed build resumes" - on the arena; "redoes only the changed stages" is
shown by the skip lines (shell up to date; split / textures skipped on resume). Not yet clicked by a human in the
panel; the panel's drawing code has not been exercised by a test (it needs a visible redraw).
Lights as converted (provisional rule, UNKNOWN): 800 W -> 8,000 lm r 14.1 m (baked x2), 600 W -> 6,000 lm r 12.2 m
(both), 400 W -> 4,000 lm r 10 m (dynamic), spot 2,000 W -> 20,000 lm r 22.4 m (dynamic, red).

### 02:26 - 02:32 launch died behind the EA app's "sync was interrupted" box; handled
- `gt4` ended `CRASH - game exited before a world came up (no replacement process within 90 s)`. Screen: EA app dialog
  "Your latest sync was interrupted ... Error code: EC:10608", buttons LAUNCH GAME / CANCEL.
- Cause (EA app log, `EADesktop.log` 01:25:13Z): `cloudsync.juno.ea.com/lock/read` request failed (`HttpRequest send
  failed`, status 0) -> `Cloud sync failed ... UnavailableLock` -> `ErrorCloudLockNoLocal`. The post-game sync at
  01:17:53Z had completed ("Pushing post-game local state"), and the local files in
  `Documents\STAR WARS Battlefront II\settings` were intact (4 files, 02:15). So LAUNCH GAME could lose nothing.
- New: `tools/screen.py` (window list, screenshot, click, key), `tools/launcher_dialogs.py` (watches the EA log line
  `Forwarding EA Desktop error dialog to splash: ... errorCode=[N], title=[...]`; for 10608: save backup -> find the
  blue button in the window picture -> click; Enter as fallback; unknown codes are photographed and left alone),
  `tools/crop.py`. Live test on the real box: button found 136 x 32 px at (1055, 633), clicked, box closed after 1 s,
  the game started.
- `tools/game_test.py`: polls the watcher every 5 s during the launch wait; closes a stale box (Esc, then Cancel)
  before starting; a launch that dies before any level loaded is retried once after 60 s with a screenshot of the
  failure. NOT yet exercised: the in-test path (the next launches had no box), Esc / Cancel on a stale box, the retry.
- Memory note written (`ea-sync-interrupted-dialog`).

### 02:32 - 02:37 arena game test `work/ToolArena/review/t3/` (mod from button run 2, before the sky stage)
RELEASED / ok, world up after 95.6 s, 14 bots with a soldier, no faults, stand drop 0.84 m, 19 pictures.
- `spot_down.png`: a red pool on the floor under the spot -> a spot placed pointing down shines down (the
  "-Forward" rule holds for this orientation; BFII as seen, one orientation only).
- Walls: every dome of the calibration normal map is DARK at its upper-left and BRIGHT at its lower-right
  (`tmp/crop_domes.png`). The sun is above, so at least the vertical axis is inverted: the map reads as dents.
  Cause not known yet (texture green vs my FBX binormal). -> calibration slabs, below.
- Door: shut in all six `door_watch_*` frames (30 s). Not proven to open. Its metal face mirrors a Kamino interior
  = the host's reflection data is still what the level reflects.
- A red hatched border grows over the `door_watch` frames: the player's soldier is being damaged or is flagged out of
  bounds there - NOT determined which. To check with the player position / health next run.
- Light modes: not separable from these pictures (the host's sun lights the walls). Needs an A/B (same spot, lights
  on / off) - open.

### 02:27 - 02:41 stage `look` (sky, sun, fog, exposure) written and run on the arena
- `runtime/bf2maptool/hdr.py` (Radiance .hdr reader / writer), `stages/look.py`, chain order
  `... lights -> look -> navmesh ...`. Clone of `Levels/SP/A2/M3SUL/Lighting/VE_A2M3_Cloudy_02` as
  `Objects/Ported/<Name>/Lighting/VE_<Name>_01`; sky panorama 4096 x 1024 + gradient 512 x 256 written as .hdr and
  imported on the Sullust sky templates; `PanoramicAlphaTexture` override added at an index read from a dump of the
  clone; the three VisualEnvironment references of the shell re-pointed; donor class + class-schematics pair bundled.
- Presets clear / overcast / sunset / night / space / volcanic (numbers PROVISIONAL, UNKNOWN) or an .hdr file
  (upper half = sky; EV from the median sky texel). Sun direction from the scene's sun lamp: azimuth atan2(x, z),
  elevation, of the direction TO the sun in game axes (the Utapau port's convention - INFERRED, checked next run by
  the pole's shadow). Sun strength = the donor's own 60,000 x preset factor.
- Arena run: 60 steps, 0 reported errors, read-back equal to plan (panorama path, LuminanceScale 100, EV 11.5,
  fog end 800, sun 7,200, alpha override listed); audits after it clean (closure 0 / 0 / 0 / 0 / 0, validate no
  issues, lint 0 errors, class schematics 65 all paired, group census 18 / 18). Not seen in game yet.
- Fix: `@@MOD` is no longer printed by a part build (`--until` / `--only`).

### 02:40 - 02:55 material bake (your request) - `addon/bf2_map_tool/matbake.py`
- When: per Principled input whose chain is more than image / constant (the tracer's `unsupported` / `approximate`,
  plus NEW `uv_transform`: a Mapping node with a non-identity scale / rotation / location, a non-UV texture
  coordinate, box projection - until now that case was exported untiled WITHOUT a warning); the emission when its
  strength is node-driven; every wired input with "Always"; a material without a Principled BSDF as a whole
  (DIFFUSE colour / ROUGHNESS / EMIT / NORMAL passes).
- How: a private copy of the material (node groups on the path copied too), on a flat square with UVs 0..1 in its
  own scene; the socket's source -> Emission -> routed straight out of every group to the material output -> Cycles
  bake EMIT into a float image (no colour management); normal = bake NORMAL tangent +X +Y +Z (OpenGL), recorded with
  `normal_format: opengl` so the packer does not guess; emission = EMIT of the material, stored normalised with its
  peak as the strength. A constant result becomes a value, not a texture. Size = the material's largest image
  (same shape) x tiling up to 4, limit 1024 / 2048 / 4096.
- Test `tests/bake/make_bake_scene.py` + `check_bake.py` -> `tests/bake/out/result.json`: 10 / 10. Mean error in
  8-bit steps: tiled 0.25, mix 0.29, ramp 0.39, diffuse-only 0.25, grouped 0.26, alpha step 0.00, emission 0.35
  (strength 2.49996, expected 2.5029); bump -> green 0.594 over the upper half of a dome, 0.406 over the lower
  (raised, OpenGL); noise baked 1024 px, not flat; the plain control is NOT baked. 12 renders, 0.2 - 1.3 s.
  (First run showed 9 / 10: my test's expected value for the grouped case was wrong - a socket colour is linear.)
- Runtime: packs the 10 materials to 12 textures (`work/BakeTest/tex`). Checks: `tex.unsupported_node`,
  `tex.uv_transform`, `tex.approximate` only fire when the input was NOT baked; new `tex.bake_note`,
  `tex.bake_incomplete`; `tex.no_principled` only when not baked.
- Also: per-material "Normal map format" (Automatic / OpenGL / DirectX), exported and honoured by the packer;
  "Guess classes" now finds a Principled BSDF inside node groups.
- NOT done yet: the add-on zip rebuilt with this; a run on a real ripped scene (Hunters TDM_Center: 141 materials
  in node groups) to see how many bake and how long it takes; baked textures seen in game.

### 02:54 - 03:01 button run 3 + game test `review/t4` (first run with the `look` stage and calibration slabs)
- `tests/addon/button3.json`: one press -> mod in 477.4 s (audit 2 x closure = most of it), 1,890 ticks, longest
  main-thread pause 0.26 s. Mod 9,552,838 bytes.
- Game: RELEASED / ok, world up after 151 s, 14 bots, no faults, 25 pictures, no launcher box this time.
- **Sky / sun / exposure (BFII as seen):** `sky_sun.png`, `calib_all.png`: smooth blue gradient = the "clear" preset
  (no Kamino clouds); the pole at (-28.5, -20) throws its shadow to the north-west = the sun stands south-east, as
  the scene's sun lamp says -> `SunRotationX = atan2(x, z)`, `SunRotationY = elevation` of the direction TO the sun
  holds. Sunlit floor bright, sky about mid grey at EV 13. Faces turned from the sun: black (no ambient without a
  bake; `EnlightenComponentData.SkyBoxSkyColor` does nothing without radiosity data, as on the ports).
- **Normal map green (BFII as seen):** `tmp/crop_calib.png`, `tmp/crop_calib_r.png`: `calib_keep_a / _b` (green as
  authored = up) read as raised domes, bright to the upper right where the sun is, on the plain AND the mirrored-UV
  face; `calib_auto_a / _b` (green flipped down) read bright at the LOWER right = vertical axis inverted.
  -> `texpack.TARGET = 'opengl'`: OpenGL maps are kept, DirectX maps flipped; plan notes say which. Mirrored UVs
  need no special case. The red (x) axis is right in both.
- **Normal map does not tile on the opaque template (NEW silent failure, BFII as seen + measured):** in every slab
  face a diagonal band of cells has no relief. `tools/geo_dump.py` (new; `mesh geo-dump` of the imported piece):
  the stored frames are uniform per triangle, so it is not the tangents. The split shifts each UV island by whole
  numbers; face triangle A has u 0.5 .. 1.5, v 0.625 .. 1.375 (game v), triangle B the same shifted by (-1, +1).
  `tmp/probe/slab_uv_region.py`: the cells that show domes are exactly the part of each triangle with 0 <= u, v <= 1
  (right 2 columns x top 1.5 rows of A, left 2 columns x bottom 1.5 rows of B); the colour texture tiles everywhere.
  So for `SS_PropsMetallicPreset_1UV_TextureArray_01` the `_NAM_texcoord0` texture is not sampled with wrap outside
  the unit square. Texture res headers of the colour and normal textures are the same kind (flags 0x0001, group
  `TextureGroup_Pr...`), the TextureAsset has no addressing field -> the addressing is the shader's.
  The Mustafar port's table called this preset "unclamped normal UV" (INFERRED there, never seen with relief).
  Mustafar itself is not visibly affected (its normal maps are flat 4 x 4), every map with real normal maps is.
- Other observations: door shut in all 6 frames again; red hatched border grows over the door watch again.

### 03:00 automatic class + Hunters arena through the exporter and packer
- `texpack.effective_class`: a material left on Opaque with "Detect from the material" on (new material property,
  default on) is built as a cut-out when its WIRED alpha has holes (>= 0.5 % of the texture under one half), as
  emissive when emission x strength > 0.02 - judged on the traced or baked data, reported as `class.auto` (info)
  in the Check tab and in the texture plan. A class the user picked is never changed.
- `F:\Maps\TDM_Center_01_Combined_04` (Star Wars: Hunters, read-only): export with bake 141 materials, 138 baked,
  417 renders, 95.8 s, 0 failures, 0 leftover "unsupported / approximate" notes (was: 137 normal maps read through
  a "Combine Color" node as the raw image, 37 alphas through a Mix). Sizes 102 x 1024, 23 x 512, 7 x 2048, 5 x 256,
  1 x 1024 x 128. `tmp/exp_hunters` (551 files, 367 MB - my temp, to delete). Packer: 141 materials -> 293 textures
  in 57 s, classes emissive 43 / opaque 80 / cut-out 18 (all 61 non-opaque by detection), no errors.
  NOT seen in game; whether the 43 "emissive" are right to the eye is not judged.
- New tools: `tools/geo_dump.py`, `tools/rgb_view.py`, `tools/crop.py`, `tools/door_probe.py`,
  `tests/survey/bake_report.py`, `tests/addon/run_button_test.sh`, `tests/addon/run_arena_cycle.sh`;
  `game_test.py --then "<command>"` runs a probe after the pictures while the game is up.

### 03:06 - 03:26 button run 4, the clone failure in `doors`, door probe, review `t5` / `t6`
- Button run 4 (zip with bake / class / green-up code, install test passed): the build STOPPED in `doors` with
  `port import: mesh clone failed: EBX clone failed: Index was out of range` - the random failure the import stage
  already retries. Fix: `cli.Cli.run` reruns ANY pipeline that stopped on that message in a fresh process (up to 4
  runs, each from the saved project; old logs kept as `<name>.tryN.log`). Proven at once: the resumed build hit it
  again in `doors` run 1, run 2 passed. Mod 10,155,613 bytes, audits clean (group census 22 / 22).
  My cycle script then game-tested the OLD mod (it only looked at Blender's exit code): fixed with
  `tools/button_ok.py` (last run ok + mod exists). That run (`t5`) still gave the first door probe.
- **Door (BFII as seen + read live), `review/t6/door_probe.json`, `tools/door_show.py`:**
  the tool-built door prefab `Objects/Ported/ToolArena/Doors/PF_TO_Door_01` and the host's controller are loaded
  (262 wires). Live values before: `Bool[1a]` true, `And[14]` true -> `GeometryTrigger[4].Enabled`, the three
  `NumPlayersInside` casts false -> door commanded shut. After `deliver Bool[1b].SetTrue` (the input the controller's
  ForceOpen drives): `Or[3d]`, `And[f]` true, `FloatInterpolator[2f].In` = 1, and `door_fire_04.png` shows the panel
  slid up clear of the doorway (`door_fire_00.png`: shut). So panel, transform wiring, travel and axis work; the door
  was shut in every earlier frame because no counted soldier stood in its trigger. Whether bots count
  (`AreaTriggerInclude` 1 / 6) is NOT known -> check scene `tests/scenes/make_doortest.py` (spawns 3 m from two
  doors, a third door as control).
- **Weapon-fire collision (BFII as seen):** scorch decals on the door panel and on the walls in `door_fire_00.png`.
- **Normal maps (BFII as seen):** `tmp/crop_calib6.png`: `calib_auto_a / _b` (tool's automatic handling, green up)
  raised; `calib_keep_*` (declared DirectX, flipped) dents - as intended for the control.
- **Templates and normal-map tiling (BFII as seen), `t6/calib_tpl.png`:** emissive template
  (`SS_InteriorDetails_01`) and cut-out template (`SS_PropsNonMetallicPreset_AlphaTest_TextureArray_01`): a raised
  dome in EVERY cell. Only the opaque 1UV preset has the gaps.
- **Why (measured in the level's shader database, `tmp/probe/rvm_surface_samplers.py`):** 24 sampler blocks; bytes
  16 .. 19 of each = filter, address U, V, W. The permutation set of `SS_PropsMetallicPreset_1UV_TextureArray_01`
  reaches anisotropic samplers `wrap/wrap` AND `wrap/clamp`; the 2UV preset and the cut-out preset reach only
  `wrap/wrap`. With "wrap U, clamp V" the slab pattern is explained exactly (relief only where 0 <= v <= 1, any u).
  The earlier ports' table had it the other way round ("1UV unclamped, 2UV clamps") - INFERRED there.
  Over all 220 surfaces of the database: 157 reach only wrap/wrap, 4 the wrap/clamp pair.
- Candidate fix in test: host template `opaque2` = `box_m_02_mesh` + `SS_PropsMetallicPreset_2UV_TextureArray_01`
  (`_CS`, `_NAM_texcoord0` b 51 as Mygeeto used on this host; same vertex layout as the 1UV template mesh: TexCoord0,
  TexCoord1, RadiosityTexCoord - `port mesh-info`). Reached through a new per-material "Surface template" override
  (advanced / testing); the arena has a slab `calib_2uv` on it.
- Packer: images that are not powers of two, too large, or of another shape than the base colour are RESAMPLED
  (note `resized` / check `tex.resized`, info) instead of refused - Rhen Var has a 1023 px image.
- Rhen Var scene looked at (read-only, `tests/rhenvar/out/objects.json`): 2,891 objects = 843 meshes (all parented,
  all with unapplied scale, 9 mirrored) + 2,047 empties + 1 sun; 2 sky spheres 3,967 m, 4 cloud domes, THREE
  2,048 m terrain objects of 131,072 triangles each, `com_inv_col` invisible blockers, 72 Principled materials with
  packed images; the buildings sit in x 40 .. 318, y -340 .. -65. Needed before it can go through: a "Guess roles"
  helper (sky domes, duplicates, invisible helpers), a play area measured from the built part and not from a 2 km
  terrain, and cropping of geometry far outside the play area. Invisible blockers are not supported yet.

### 03:28 - 03:44 Rhen Var prepared and started; helpers for scenes that were not made for the tool
- `addon/bf2_map_tool/guess.py` + operator "Guess roles" (Map tab): sky shells (>= 1,500 m and <= 5,000 triangles, or
  sky-named and >= 400 m), exact duplicates (same vertex / face count, size, place), invisible helpers by name ->
  role "Leave out", every guess listed under Check, nothing deleted.
- Exporter: when no spawns / command posts exist, the play area is measured from the objects under 500 m across if
  they are the majority (noted in `map.json` `notes`). New map setting "Keep geometry within" (default 250 m): the
  split leaves out triangles farther than that outside the play area (`left_out_far` in the split result).
- `tests/rhenvar/prep.py` (source only READ, copy saved as `tests/rhenvar/rhenvar.blend`): map name / sky / fog +
  Guess roles, nothing else. 10 guesses (2 sky spheres, 4 cloud domes, 2 duplicate terrain layers, 2 `com_inv_col`),
  833 meshes left. `python -m bf2maptool check`: 0 errors, 1 warning (mirrored terrain), play centre
  (208, -180, 15) radius 167 m "geometry box", no material needed baking, one 1023 x 682 image resampled.
- Build started (`rv1`): split 724 pieces / 58,567 triangles, 114,232 far triangles left out, 66 opaque materials ->
  46 textures, collision done, at `import` at 03:44.
- Arena button run 5: ok, 2,015 ticks, longest pause 0.26 s (`tests/addon/button5.json`). Its game test `t7` is slow
  (two builds run beside it).
- NOT done: Rhen Var has no blend layers for its terrain (layers 2 / 3 left out as duplicates), no invisible
  blockers, no command posts; its look is the "overcast" preset, not its own sky.

### 03:44 - 03:47 the 2UV template is NOT the fix; opaque-with-relief moves to the template that tiles
- `review/t7/calib_tpl2.png` (BFII as seen): the 2UV preset slab renders, lit, domes raised, but relief only inside
  the unit square in BOTH directions (worse than the 1UV preset, which wraps U). Its game mesh has TexCoord1 equal to
  TexCoord0, unclamped (`tmp/probe/uv1_look.py`), so it is the shader. My sampler-set reading of the database was too
  coarse to tell this (the 2UV and the cut-out preset reach the same sampler set, yet only the cut-out one tiles) -
  retracted as a selection method; what counts is the picture.
- Decision (logged, not asked): `hosts/kamino_01.OPAQUE_RELIEF_TEMPLATE = 'emissive'`. An OPAQUE material whose normal
  map has real relief (`texpack.has_relief`) is built on `SS_InteriorDetails_01` with a black emission texture and
  multiplier 0 (seen tiling in t6 / t7); materials with a flat normal map stay on the 1UV preset. Price, stated in
  the plan note of each such material: that template mesh has no RadiosityTexCoord (Pos / BinormalSign / Normal /
  Tangent / TexCoord0 only), so those pieces cannot take a lightmap later - probe lighting for them. The alternative
  kept in mind for the bake step: cut the geometry along whole-number V lines and stay on the 1UV preset.
- `opaque2` (2UV) stays in the host file as a documented dead end, reachable only by the test override.

### 03:56 - 04:13 results of the queue; importer-refused piece files; zip; path scan
- **Arena `review/ta8` (BFII as seen):** relief in every cell on walls, crates, `calib_auto_*` (raised) and
  `calib_keep_*` (dents, the control). Plan: 4 materials on the tiling template, 2 on plain opaque, 2 cut-out.
- **Door check 1 (`work/DoorTest/review/dt1`):** all doors shut in 10 frames, but the player stood at game
  (-90, 409, 42) = the middle of the hall: the stage's generated spawns were used, not only my six. No evidence
  either way. Scene changed to `auto_spawns = False` (and a clear sky; the overcast preset is very dark indoors).
- **Rhen Var, import blocked twice:**
  1. run `rv1`: piece `RH_0446` refused on every try: "mismatch in the amount of LODs defined in the imported file".
     Bisection (`tmp/probe/lod_bisect*.py`): any 224 of its 226 triangles import, 225+ do not; positions and normals
     do not matter; the same uvs written one-per-corner instead of shared import. -> piece files now carry unshared uvs.
     Also found: my new general retry had re-run the whole import three times per round (12 min) - fixed.
  2. run `rv2` (unshared uvs): 5 OTHER pieces refused (4 x LOD mismatch, 1 x "at least one UV channel"), all between
     270,076 and 292,540 bytes; of 719 accepted files only 8 are over 250 kB. No geometric feature separates them
     (`tmp/probe/piece_census.py`: no repeated corners, no tiny triangles; duplicate faces and zero-uv triangles occur
     in accepted pieces too). Padding the header by 0 .. 16 bytes changes nothing; the same five written with zlib
     arrays all import (`tmp/probe/container_test.py`).
  Conclusion (UNKNOWN cause, measured behaviour): the importer's FBX reader fails on some files depending on their
  bytes; no single encoding is safe (zlib-1 lost 2 of 514 on Mustafar). Rule: `fbx.VARIANTS` (5 lossless encodings),
  `importer` rewrites a refused piece in the next one and imports it again, 6 rounds, and reports the encodings used.
  `tests/test_fbx_variants.py`: positions / normals / uvs identical in all five, tangents within 5e-6.
- Add-on zip rebuilt (49 files, 157,193 bytes) and `tests/addon/test_install.py` passed (13 setup lines ok).
- **No hard-coded paths (acceptance item 2), `python tools/grep_paths.py`:** scanned `addon/bf2_map_tool` and
  `runtime/bf2maptool` for drive paths, the user name and the names of every map / port: NOT ALLOWED 0; allowed 9
  (5 comments saying where a rule was measured, 4 in the host module `hosts/kamino_01*`, whose job is to name shipped
  assets). One real hit fixed on the way: a default path in the vendored `rvmdb.py` self-test.
- Hunters arena prepared with the add-on's guessing only (`tests/prep_scene.py`; 14 objects set aside, 926 left):
  `check` = 0 errors, 1 warning (29 mirrored objects), 62 info lines; export with bake 125.6 s; build started.

### 04:23 - 04:39 queue results; bake stage written and run
- **Rhen Var `rv3`:** the encoding rule worked - round 0: 695 pieces, round 1 (zlib-1): the other 29, 724 / 724;
  navmesh 2,407 polygons (main part 1,375 / 28,435 m2), spawns placed. Then the audit FAILED: lint
  `added-res-unbundled` x 10 = the mesh resource + shader-block depot of the 5 refused first tries, left behind by the
  importer under the old names. Fix: before a retry the importer's leftovers are reverted (`res revert` of
  `<piece>_mesh`, `<piece>_mesh_mesh/blocks`, `<piece>_physics_win32`). Run `rv4` started 04:38.
- **Hunters arena `ha1`:** split 540 pieces / 687,768 triangles, 299 textures (emissive 43 / opaque 79 / cut-out 18,
  own shader database merged for the cut-outs), collision 687,648 triangles in 48 s; import STOPPED with the old
  importer code (process started before the encoding rule): 21 pieces refused, incl. a new message
  "Object reference not set ... (FBXImporter.ProcessSection)". Run `ha2` with the new rule started 04:38.
- **Door check 2 (`dt2`):** all bots fight in the middle section; the spawn map showed why that proves nothing: with
  auto spawns off the 27 NEUTRAL spawn slots were still generated mid-hall. Rule changed: auto spawns off = neutral
  and hero slots take the placed team spawns. (My first patch reused a variable name and crashed the stage - `dt3`;
  fixed.) `dt4` is built (rc 0) and waits in the game-test queue.
- **Game-test queue:** `tools/game_test.py --wait <s>` (a lock directory with the owner's pid; a dead owner's lock is
  cleared; a game that is up and not the queue's is only waited for). `tests/run_scene_test3.sh`,
  `tests/run_modes.sh` use it.
- **Modes on the arena (`review/tam_*`):** HeroesVersusVillains loads (HELD, no faults, 0 bots - none expected),
  SkirmishOnslaught loads with 10 bots, Blast running. Mode names from the shell's own declaration: SkirmishBlast,
  SkirmishOnslaught, SkirmishDuel, Blast, HeroesVersusVillains.
- **BAKE LIGHTING - `runtime/bf2maptool/stages/bake.py`** (generalised from the Mustafar port's `work/mbl/*`; the
  leak filter `tools/leakmask` copied, exe shipped as `runtime/bf2maptool/bin/leakmask.exe`): 13 sub-steps with
  markers (keys, geo, classify, charts, regeo, plan, materials, probes, sky, scene, flux, leak, stats, apply), the
  chain `... kill -> bake -> audit -> mod` when a build is started as a bake (`python -m bf2maptool bake <scene>`
  or `port --bake draft|normal|high`); Port map alone leaves the bake out. Presets (first values, to be measured):
  draft 3 texels / m, 4 M texels, 64 samples; normal 6 / 12 M / 256; high 9 / 24 M / 512 + filter 5. Free-memory gate
  6 GB. Lightmapped = opaque pieces on the lightmap-capable template; opaque-with-relief pieces (on the tiling
  template) go into the scene as non-emitting occluders; emissive pieces as emitters with radiance = mean emission
  texel x strength x bake factor x the host's emissive scale.
  **First run, arena, draft (`logs/bake1.log`): all steps ok, 97 s.** 8 lightmapped pieces, 60 charts, 0.17 M
  texels, atlas 4096 x 788; 20 probe volumes / 1,456 probes; baker 10.1 s (625,168 covered texels); leak filter 318
  texels rewritten; **0 NaN**; irradiance / pi p1 904, median 2,454, p99 7,065; `bake-apply`: END 4096 x 788, 10 mesh
  records, 23 probe sets, FLS 2,671 probes, 0 non-finite. NOT yet: seen in game, END / FLS structure checks of the
  port (only sizes and non-finite counts are checked), the three presets timed on a real map.
  Changed after that run: below the horizon the bake's sky is open ground of albedo 0.2 lit by the upper sky (the
  engine's lookup repeats the horizon row there; as a light source that made the lower hemisphere 2.3x brighter
  than the upper).

### 04:42 - 04:58 doors proven, modes, baked lighting seen in game, both maps past the importer
- **Doors (BFII as seen), `work/DoorTest/review/dt4/`:** every spawn slot on the six placed spawns (3 m from doors A
  and B). Player stood at game (-115.75, 409.84, 47.45) = scene (-29.8, -0.5), i.e. at team 1's spawn.
  `door_a_spawn_side.png`: droids at the spawn, door A's panel raised above the wall, doorway open, the far control
  door shut. `door_b_01.png`: door B (sideways) open, soldiers walking through. Both teams met on each other's side.
  The red hatched border in `door_b_*` = the deployed player under fire (explains the earlier sightings).
- **Modes, arena (`review/tam_*`):** HeroesVersusVillains loads (0 bots), SkirmishOnslaught loads (10 bots), Blast
  loads - all rc 0, no faults. With SkirmishBlast that is every mode the shell declares except SkirmishDuel.
- **Baked lighting in game (BFII as seen), `review/bk1/`:** arena, draft preset, full build (bake 104 s, audit clean,
  mod 12,940,497 bytes). Shadow-side walls and slab backs show their relief in dim light instead of pure black
  (`crates_wall_normalmap.png`, `calib_back.png`, `door.png`); floors lightmapped, no seams or leaks seen in 29
  pictures. The first picture of a run (`overview.png`) is taken while the exposure still adapts (far too bright
  here, too dark in `ta8`): the automatic viewpoints now take the overview twice.
- **END check ported into the stage** (`bake.end_check`): arena = 10 mesh records for 10 planned charts, sorted,
  unique, 0 uv mismatches; 23 probe sets all in the shipped volume form (3 host "darkroom" sets kept), probes
  inside their boxes, offsets contiguous. FLS: 2,671 probes, 0 non-finite. NOT ported: the comparison of the kept
  darkroom probes with the host's own FLS file (needs that file exported).
- **Importer rule on two maps:** Rhen Var 724 / 724 (round 0: 693, round 1 zlib-1: 29, round 2 zlib-6: 2);
  Hunters arena 540 / 540 (500 + 39 + 1; a new message "Object reference not set ... ProcessSection" was among the
  refusals and went away with the re-encoding too). Hunters templates: 476 pieces on the tiling template (emissive
  + opaque with relief), 48 cut-out, 16 plain opaque; navmesh 2,165 polygons over 1.36 M collision triangles.

### 04:58 - 05:03 RHEN VAR IN GAME (map 2, never ported) - passed-for-now, for you to confirm by eye
- Build `rv4` from `tests/rhenvar/rhenvar.blend` (the source scene + map name / sky / fog + the add-on's Guess roles;
  no spawn, no class, no collision setting by hand): export 833 objects / 172,301 triangles -> 724 pieces / 58,567
  triangles (114,232 far terrain triangles left out), 66 materials -> 46 textures, exact collision on all 724,
  import 724 / 724 (3 rounds), navmesh 2,407 polygons, spawns generated, audit clean, mod
  `work/RhenVar/mods/RhenVar.fbmod` 28,167,674 bytes.
- Game test `work/RhenVar/review/rv4/` (SkirmishBlast): world up after 95.7 s, 14 bots with a soldier, the deployed
  player stands still at game (-19.5, 395.0, 94.3) (height drop 0.0 over 9 s), 16 pictures: `overview_01.png` the
  whole citadel on its snow, `base1_to_base2.png` wall + snow textures close up, `hvv1_look_090.png` tower, stairs.
  Looks dim and flat: overcast preset, no bake yet (bake `rvb1` running).
- The test marked this run "not ok" for one line `fault: code 0x4001000A at KERNELBASE.dll` at 04:59:20, during the
  launch. That code is Windows' "a debug string was printed" exception (DBG_PRINTEXCEPTION_WIDE_C), not a crash; the
  level loaded and ran after it. `tools/game_test.py` now lists 0x40010006 / 0x4001000A apart (`debug_prints`) and
  does not fail on them.
- NOT in this build of Rhen Var: its terrain blend layers 2 / 3, invisible blockers, its own sky, command posts.
  Not tested yet: HvV / Onslaught loads, weapon fire, walking around.
- Mustafar for test point 2: `tests/mustafar/make_tagged.py` -> `tests/mustafar/mustafar_tagged.blend` (the port's
  fixed blend, opened and never saved back; 50 material classes from the port's plan, 18 doors, volcanic sky, sun
  off), then ONLY the tool: `sh tests/run_scene.sh ... MustafarT ... mt2 draft` (export, checks, port, bake, audit,
  mod, game test). Started 04:59: 546 pieces / 50,032 triangles, 51 materials.
- `python -m bf2maptool bake` now runs the pre-flight checks too (it skipped them).

### 05:03 - 05:31 Hunters arena and baked Rhen Var and Mustafar (tool only) in game; navmesh from collision; wall cutting
- **Hunters arena (map 3, another game) IN GAME, `work/HuntersArena/review/ha2/`:** mod 255,850,856 bytes, audit clean
  (lint 0 errors / 92 warnings). World up after 80.5 s, 12 bots with a soldier, player stands (drop 0.0), no faults.
  `base1_to_base2.png`: sand with its normal map (tiles, raised), towers, arcades, market; `overview_01.png` the whole
  arena. 140 materials, all packed by the tool (138 baked from node groups); not judged one by one. A dark stray
  cube floats above the map (an object of the source scene the role guess did not catch). Unbaked: dark shadows.
- **Rhen Var baked (draft), `review/rvb1/`:** bake stage 224 s: 724 lightmapped pieces, 10,206 charts, 4.68 M texels
  (budget 4 M reached: 3 -> 1.9 texels / m near the floors, 0.5 far), atlas 4096 x 1984, 245 probe volumes / 34,336
  probes, baker 27.9 s (64 samples), leak filter 156,522 texels rewritten, 0 NaN, END check clean (726 records).
  In game: world up (388 s - the PC ran four builds beside it), 14 bots; `hvv1_look_090.png` against `rv4`'s same
  view: walls and snow lit softly by the overcast sky, interiors in shade. Pictures sent.
- **TEST POINT 2 - Mustafar through the tool only, with bake: passed-for-now, for you to confirm by eye.**
  `tests/mustafar/mustafar_tagged.blend` -> `sh tests/run_scene.sh ... MustafarT ... mt2 draft`: 546 pieces / 50,032
  triangles, 51 materials (28 opaque, 14 emissive, 5 cut-out, 4 lava as emissive placeholder), 18 doors, bake 175 s
  (319 lightmapped pieces, 210 emitters, 4.29 M texels, 20,640 probes, baker 24.9 s, 0 NaN, END 321 records),
  audit clean. Game `work/MustafarT/review/mt2/`: world up 130.6 s, 14 bots, player stands, no faults.
  `overview_01.png` refinery over glowing lava under the volcanic sky; `hvv1_look_000.png` control room lit by its
  ceiling strips and screens. NOT the port's look: the lava is the static emissive placeholder, the lit-blend maps
  the port packed by hand are not in the tagged scene, emissive / lava strengths are the provisional factor.
- **Hunters bake (draft) built, `logs/hab1_build.log`:** 161 s, but only **16 of 540 pieces lightmapped** - every
  other opaque piece has a normal map and sat on the non-lightmap template (my 03:47 decision). Probe volumes only
  stood over those 16 pieces' floors. That decision is what the next item replaces.
- **Normal-mapped walls keep their lightmaps (NEW, replaces the 03:47 fallback where it applies):** the split now cuts
  the triangles of opaque materials with a normal-map image along the whole-number v lines and moves every part into
  v 0..1 (`split.cut_v_bands`, host flag `OPAQUE_V_CLAMPED`), so the "wrap U, clamp V" opaque surface shows the relief
  everywhere and the piece stays lightmapped. `tests/test_v_bands.py` on the arena data: 2 materials banded, 204
  triangles -> 1,076, every banded piece's v inside 0..1, PASS; plan: walls on `opaque` again. Costs triangles on
  surfaces that repeat a texture many times. NOT yet seen in game (run `ta9`); half a texel at each cut line is
  sampled clamped instead of wrapped - a faint line is possible.
- **Navmesh from collision (found by the collision check scene):** `navmesh build` read the VISIBLE meshes of solid
  pieces; on the check scene it sat on the visible box 1 m under the custom collision top. Now `--collision`
  (server physics bodies decoded from the Havok resources): 264 collision triangles in = the collision stage's
  count; the custom podium's real top is walkable; spawns pass.
- **Hull collision for static objects** was documented but did nothing ("nothing here") - a static object set to
  Hull fell through. Now: scipy ConvexHull of the object's vertices, written like a custom mesh.
- **Docs written:** `docs/setup.md`, `docs/quickstart.md`, `docs/troubleshooting.md` (packed into the zip as
  `bf2_map_tool/docs/`). To revise when Supremacy / the limits change. The clean-machine walk-through is NOT done.
- Silent-failure table rows to add (next turn): navmesh from render meshes; hull did nothing; neutral spawn slots
  generated with auto spawns off; probe volumes only over lightmapped floors (open).

### 05:42 - 06:12 step 5 (checks) done for pre-flight; overlay; navmesh / door fix; Hunters takes lightmaps
- **`ta9` FAILED in `spawns`** (3 team-2 spawns 15 m from the walkable part): with the navmesh now read from
  collision, the closed door's own collision split the arena, and `--skip <prefix>_Door_` was ignored in collision
  mode. CLI patched (`NavmeshCollision.Skip`, commit `7898e0c`, 0 build errors). The first restart (`ta10`) failed the
  same way because the navmesh stage called itself up to date - its inputs do not see a changed CLI (row 54). Stage
  version bumped; **`ta10` built**: bake 126.7 s, **19 lightmapped pieces (8 before the wall cutting)**, 0.31 M texels,
  baker 15.6 s, 0 NaN; audit clean. Game test queued -> `work/ToolArena/review/ta10/`.
- **Hunters rebuilt (`ha3`, running):** wall cutting + probes over all solid floors: **354 lightmapped pieces
  (16 before)**, 4.98 M texels, 10,224 probes, baker 101.9 s, bake steps 342 s. Collision 692,676 triangles.
  The import needed the encoding retry on 15 pieces. Audit running at 06:10.
- **`hab1` read:** the first baked Hunters is still dark (16 lightmapped pieces) - superseded by `ha3`.
- **Rhen Var mode sweep (`rvm`):** SkirmishOnslaught: world up 351.8 s (PC loaded), 10 bots with a soldier, 15
  pictures taken, no faults. HeroesVersusVillains: loads, no fault, alive 300 s, but 0 bots and every picture
  failed - no spawn screen ("the START event could not be fired"); the same happened on the arena. HvV = loads only.
- **Collision check scene in game (`work/ColTest/review/ct2/`), read by me:** soldiers stand on the exact podium's
  top; clones are inside team 2's walled yard, whose only openings are the not-solid red panels (none = passable).
  **Custom and hull are NOT decided by the pictures** (a clone behind the blue box could be on the floor). The
  player stood on the yard floor: camera 1.84 m over his spawn point (= eye height on a floor, the yardstick below).
  New: `make_coltest.py <out> <Name> a|b` puts ALL spawns on the custom podium (2 m, 1 m above the visible box) and in
  the hull gap; the player's camera height then reads the shape: 3.84 m = solid where the tool says, 2.84 / 1.84 =
  not. Builds `cta` / `ctb` running.
- **Step 5 - every pre-flight check has a scene that trips it: `tests/checks/`.** `make_check_scenes.py` writes 36
  tiny scenes (one floor + the one mistake), `run_checks.py` runs the tool's own `check` command on each and three
  more on patched map files (states the add-on's controls cannot produce). **39 of 39 pass** (62 s),
  `tests/checks/result.json`; `clean` reports nothing. Codes covered: geo.none, map.no_mode, map.name_clash,
  supremacy.posts, supremacy.not_built, geo.no_material, geo.no_uv, geo.degenerate, geo.scale, geo.flipped,
  scene.extent, scene.scale, class.unsupported, class.guessed, class.auto, tex.missing, tex.cutout_no_alpha,
  tex.resized, tex.uv_transform, tex.approximate, tex.unsupported_node, tex.no_principled, tex.bake_note,
  tex.bake_incomplete, kill.count, kill.walkable, spawn.in_kill, spawn.teams_close, spawn.none, spawn.in_geometry,
  spawn.no_floor, door.materials, door.shape, door.direction, door.travel, light.dynamic_budget, light.saturated,
  light.in_surface, check.internal.
  - Two real bugs found by writing the tests (rows 55, 56) and one in my own arena scene (row 57).
  - Six checks the brief lists were missing and are new (`runtime/bf2maptool/checks/space.py`, rows 57 - 63).
  - False-alarm run on the real maps (`tests/checks/preflight_maps.py`): arena 0.1 s, Mustafar 4.6 s, Rhen Var
    12.7 s, Hunters 19 s; no new error on any of them except the arena's real one.
  - Brief items handled by construction, not by a check: more than one material per face after the split and pieces
    over 3,000 triangles (the split makes both impossible), unapplied scale (the export uses the size you see).
  - NOT covered: flipped faces on open meshes; "spawn inside geometry" only sees closed solids or a surface within
    0.25 m; a name clash with another installed mod is only caught by `level create`.
- **Collision overlay (new): `addon/bf2_map_tool/overlay.py`**, Collision tab > "Show what is solid": green exact,
  blue custom, orange hull, yellow doors, red kill boxes; nothing drawn = not solid. Same rule as the collision
  stage. **`tests/test_overlay.py` on the collision scene: overlay 192 triangles / 11,470.295 m2, built collision 264
  triangles / 11,470.295 m2, 0 triangles of either side off the other (2 cm), PASS** (`tests/overlay_coltest.json`).
  The drawing itself has NOT been looked at in a Blender window yet.
- Post-build checks that run on every build today: closure audit, validate, lint, schematics pairs, group-member
  census; END / FLS / NaN inside the bake stage. **Missing: depot parameter census, shader declaration census.**

### 06:12 - 06:40 collision proven; walls lit; Supremacy stage written and built once
- **`ta10` (arena, wall cutting + draft bake) IN GAME:** world up 90.3 s, 14 bots, no faults.
  `north_wall_close.png`, `crates_wall_normalmap.png`: every tile of the walls and crates shows the raised domes AND
  the shaded wall is lit by the bake (it was black before the bake / flat-lit by probes before the cutting). No line
  at the cut rows in these views. Pictures sent.
- **`ha3` (Hunters, wall cutting + probes everywhere + draft bake) IN GAME:** world up 105.9 s, 9 bots, no faults;
  `base1_to_base2.png`: buildings and sand lit and shaded (`hab1` was near black). Flaws seen: a thin L-shaped line
  on the sand in `hvv1_look_090.png` (lightmap chart or cut seam - not looked into), the stray dark object above the
  map. Audit clean (lint 0 errors / 444 warnings, group census 542 / 542). Picture sent.
- **Mustafar mode sweep (`mtm`):** SkirmishOnslaught world up 170.5 s, 9 bots, 15 pictures, no faults
  (`base1_to_base2.png`: landing pad, refinery wall with its light strips, lava falls). HvV: loads, 0 bots, no
  spawn screen (as on the other maps).
- **COLLISION, all four modes PROVEN IN GAME (BFII as seen / measured):**
  - yardstick: a player standing on a plain floor has his camera 1.84 m over his feet (`ct2`).
  - **custom** (`work/ColTestB/review/ctb/result.json`): all team-2 spawns on the custom podium; the player's camera
    rests at scene z 3.84 m in 3 samples = feet at **2.00 m** = the collision mesh's top, 1 m above the visible box.
  - **hull** (`work/ColTestA/review/cta/`): player's feet at 2.00 m on the hull object (2 samples; later samples are
    after he was shot); `hull_side_00.png`: a droid stands in mid-air over the 2 m gap between the two pillars, level
    with the droids on the pillar tops. Before the fix the hull setting made the object not solid at all.
  - **exact**: soldiers on the 2 m podium (`ct2`); every floor of every map. **none**: clones inside team 2's
    walled yard whose only openings are the two not-solid panels (`ct2`, `cta`).
  - **overlay = solid in game:** the overlay's triangles equal the built collision (test above) and the built
    collision is what the soldiers stood on. The overlay was looked at in a Blender window
    (`tests/addon/overlay1.png`, `tests/addon/run_overlay_test.sh`): green / blue / orange as designed, the two
    "None" panels uncoloured; counts 168 / 12 / 12 = the data test.
- **Pre-flight check tests: 40 of 40** (`supremacy.not_built` is gone; new `supremacy.letters`).
- **SUPREMACY (step 8) - stage `supremacy` written: `runtime/bf2maptool/stages/supremacy.py`,
  donor facts `runtime/bf2maptool/donors/felucia_mode1.json`.**
  - Source of the recipe: the Polis Massa port's `pmf_one.py`, `pms_strip.py`, `pmf_cp.py` (read, not run; copied
    and made general). Donor facts (guids of Felucia's 5 capture points, 160 spawns, 5 groups, 2 HQ sets, shapes)
    are the file that port session read from the shipped level; the stage checks them against the level it creates
    and stops when one is missing. Nothing else is carried over: connection indices, layer indices, cinematic
    objects and the finder-shape points are read from dumps of the level just created.
  - Decisions (logged, not asked): no forced bot fill (the port's later finding: the cloned bot logic overrode the
    server's bot settings; offline Instant Action fills its own) - TO VERIFY in game; headquarters = the middle of the
    placed team spawns, else between posts A+B / D+E; team spawn areas moved only (size, order, y = floor + 3 as
    proven on Polis Massa); neutral finder shapes and the donor's vehicle set pieces untouched; Kyber told Mode1 only.
  - Wiring: optional stage after `bake` (`chain.ORDER`), on when Supremacy is ticked; `audit` now runs closure +
    root-late repair + validate for BOTH levels; one mod holds both.
  - **First build (arena + 5 posts, `tests/scenes/arena_sup.blend`, `logs/tas1_build.log`):** `level create` 98 s,
    31 layers shared; **501 donor connections, 3 layers, 83 cinematic objects, 11 visual-environment references
    taken out - the same four counts as the Polis Massa port**; 5 posts (snap 1.1 - 9.4 m: post C stood on the
    narrow bridge), 160 spawns on 374 safe spots, 3,155 edits, 0 errors in the log, capture points read back where
    they were put. Closure of both levels: 0 unresolved. Menu entry `UI/Data/GameModes/Levels/ToolArena_02` in 4
    lists + the Instant Action list (item 95, verified to list Felucia). NOT YET SEEN IN GAME.
  - First attempt stopped loudly: the Instant Action list was dumped shallow and showed no entries (fixed: deep dump).
- Test scenes: `tests/mustafar/mustafar_sup.blend` (Mustafar + 5 posts on the line between the two team bases of the
  last build, `tests/plan_posts.py`, `tests/add_posts.py`) is ready for TEST POINT 3.
- Add-on zip rebuilt at 06:20 for the overlay test (59 files, 238,553 bytes) - before the Supremacy stage: stale again.

### 06:40 - 06:52 SUPREMACY IN GAME (arena); depot census; light test started
- **`tas1`: `ToolArena_02`, mode Mode8 (offline Instant Action Supremacy), launched by the harness:** build
  `logs/tas1_build.log` rc 0 (supremacy stage 110 s; audit of both levels 994 s on a loaded PC: closure 0 unresolved
  x 2, validate "no issues" x 2, lint 0 errors / 26 warnings; mod 14,488,236 bytes with both levels).
  Game: LOADED LEVEL `ToolArena_02`, world up 80.6 s, **spawn screen came up and the player deployed**, 12 bots with a
  soldier at the first count, 26 pictures, no faults. `overview.png`: five command-post beams at the five posts
  placed in Blender (C where the stage snapped it, off the narrow bridge), three with a blue team emblem, one with a
  capture bubble, soldiers of both sides, blaster fire, a red lightsaber, a jet trooper (`platform_ramp.png`).
  **No forced bot fill was needed** (decision of 06:27 confirmed for offline). One of the donor's vehicle set pieces
  is parked just outside the arena wall (they are left in place on purpose - Excluded they crashed Polis Massa).
  Grade: BFII as seen in my screenshots; capture / ownership read from icons and beams, not from game state.
  NOT checked: the Instant Action MENU entry (the harness loads the level directly), a round played to its end,
  Mode1 (online), spawn at a chosen post from the spawn screen.
- **Post-build depot census written: `runtime/bf2maptool/census.py`** (`python -m bf2maptool census <map.json>
  --settings ...` runs it on a finished build). Per placed piece, from `mesh depot-dump` of the BUILT project: the
  depot exists; all pieces of one material carry the same texture parameters; those differ from the template mesh's
  own in at least as many slots as the tool fills (a piece whose overrides were dropped by `port regeo` shows the
  template's textures); the surface permutation it draws with is one its shipped template mesh uses.
  Arena (baked, 23 pieces, 8 materials, 4 templates): 23 depots read, 0 problems; one surface per template
  (opaque c7d30ee4, opaque2 9ad10bd5, emissive b572d4cd, cut-out e8c6b391) = the templates' own.
  **`tests/test_census.py`: 5 of 5** - as built passes; parameters lost on one piece, one piece different from its
  material, a missing depot and a foreign surface permutation are each reported.
  This covers "depot parameter census" and, in the form that can be read from the build, "shader declaration
  census": it proves a piece uses a permutation the template uses, NOT that the level's shader database holds a
  program for it (that is verified once, when a surface is merged, by the `shaderdb` stage).
  NOT yet part of the audit stage: first being run on Rhen Var and Hunters to see that it raises no false alarm.
- **Light-mode test scene `tests/scenes/make_lighttest.py`:** four roofed bays under a night sky, no sun; the same
  1000 W point lamp as Baked / Dynamic / Both / none; `lighttest_measure.py` reads the floor brightness of each bay
  from the pictures. In the arena pictures (`ta10`) the Dynamic lamp shows a pool of light, the Baked lamp shows
  nothing I can tell from the sunlit floor - hence a dark test. Build `lt1` (draft bake) started.
- Docs: quickstart (overlay, Supremacy), troubleshooting (the new check messages, known limits) updated.

### 06:52 - 06:55 census on every build; Supremacy scenes for the other maps
- **Depot census, no false alarm on the two big maps:** Rhen Var 724 / 724 depots, 66 materials, 1 template, 0
  problems (25 s); Hunters 542 / 542 depots, 140 materials, 3 templates (354 opaque, 140 emissive, 48 cut-out
  pieces, one surface permutation each = the template's), 0 problems. **Now part of the `audit` stage of every build**
  (`finish.run_audit`, audit version 3: its problems stop the build; counts are in `audit.json` under `post.depots`).
  First build that runs it inside the audit: `rvs1`.
- Add-on, command post box: only what the Supremacy stage uses is shown (letter, capture radius) with three lines
  saying which side a letter is on, that posts start neutral, and that starting owner / vehicles are not in this
  version. (Before, it showed Starting owner, Order, Vehicles and "spawns parented to the post" - none of them used.)
- Scenes with five posts on the line between the team bases of the last build: `tests/rhenvar/rhenvar_sup.blend`,
  `tests/maps/hunters_sup.blend`. `tests/scenes/arena.blend` regenerated (spawn off the slab).

### 06:55 - 07:28 TEST POINT 3; light modes in game; preset timing started
- **TEST POINT 3 - Mustafar in Instant Action Supremacy: passed-for-now, for you to confirm by eye.**
  `sh tests/run_sup.sh tests/mustafar/mustafar_sup.blend MustafarT tests/auto_shots.py mts1 draft`: full rebuild
  (the split changed), navmesh from collision 1,977 polygons (main part 12,665 m2 of 30,521; 183 parts), supremacy
  stage 139.5 s (501 / 3 / 83 / 11 taken out as on the arena; posts snapped 1.8 / 8.2 / 18.7 / 17.4 / 2.6 m - C and D
  were put on the straight line between the bases, which crosses lava; 639 safe spots), audit of both levels clean,
  mod 42,343,298 bytes. Game: LOADED `MustafarT_02` in Mode8, world up 100.7 s, player deployed, 14 bots with a
  soldier, 16 pictures, no faults. `overview_01.png`: post beams and red / blue team icons on the walkways and the
  landing pad; `base1_to_base2.png` the pad and the refinery wall. Picture sent.
  NOT checked: the Instant Action menu entry, a round to its end, whether bots reach every post across the 183
  separate navmesh parts.
- **Rhen Var Supremacy (`rvs1`):** supremacy stage done 161 s (same four strip counts; posts snapped 1.3 / 14.7 /
  13.2 / 2.7 / 1.9 m); audit running at 07:26. This is the first build whose audit runs the depot census.
- **Hunters Supremacy (`has1`):** started 07:23 (full rebuild).
- The donor file's temporary second key is dropped; path / name scan: `NOT ALLOWED: 0` (07:16).
- **LIGHT MODES IN GAME (`work/LightTest/review/lt1/`, level `LightTest`, night sky, no sun, draft bake 76 s):**
  world up 90.6 s, 14 bots. `overview.png` (sent): the Baked bay is lit softly with no real-time light in it, the
  Dynamic and Both bays are lit hard, the bay without a lamp is dark. So all three modes do what the table in
  `stages/lights.py` says: Baked = direct + bounce from the lightmap; Dynamic = real-time only; Both = real-time
  direct + baked bounce. Measured floor brightness of the same picture region (8-bit, tone-mapped):
  baked 103.0, dynamic 229.6, both 233.4, none 1.1 (`tests/scenes/lighttest_lt1.json`).
  **Finding: the same 1000 W (10,000 lm) lamp is much dimmer baked than real-time**, and the real-time bays are near
  white, so a ratio cannot be read from this picture. Calibration row `lc1` (`tests/scenes/lightcal.blend`: baked
  1000 W beside dynamic 30 / 60 / 125 / 250 W and an empty bay) is building: the dynamic wattage whose bay matches
  the baked one gives the factor. Where the lumens go: the real-time light is the entity's `Intensity`; the bake reads
  the same record through `enlighten flux-scene` (`FluxScene.FromRecord`) - nothing in the tool scales one against
  the other today.
- Bake presets: `tests/time_presets.sh` runs the bake stage alone per preset; `tp1` = arena normal + high, running.

### 07:28 - 07:56 Rhen Var Supremacy; presets timed; round-trip; table; glass; kill test
- **Rhen Var in offline Supremacy (`work/RhenVar/review/rvs1/`):** build rc 0 (645 pieces after the keep-distance
  crop; audit: closure 0 x 2, validate clean x 2, lint 0 errors / 656 warnings, group census 645 / 645, **depot census
  645 / 645 pieces, 66 materials** - the first audit that ran it). Game: LOADED `RhenVar_02`, world up 267.5 s (PC
  loaded), player deployed, 14 bots, no faults; `overview_01.png` (sent): post beams in the citadel, one held.
  The diamond pattern on the snow is the source terrain texture (it is in `rvb1` too).
- **Bake presets timed on the arena (`tests/time_presets.sh`, `work/ToolArena/bake_<preset>.json`), 19 lightmapped
  pieces, 1,760 probes:**

  | preset | texels / m | samples | lightmap texels | baker | all bake steps |
  |---|---|---|---|---|---|
  | draft | 3 | 64 | 0.31 M | 8.3 - 15.6 s | 81 - 127 s |
  | normal | 6 | 256 | 1.20 M | 46.6 s | 140 s |
  | high | 9 | 512 | 2.61 M | 75.0 s | 160 s |

  0 NaN, END check clean in all three. Draft on the real maps: Mustafar 175 s, Rhen Var 224 s, Hunters 342 s; normal /
  high were NOT run on a real map (expect the baker's share to grow about 4x / 8x).
- **Light calibration row 1 (`lc1`): not usable as a number.** Baked 1000 W wall 0.424; dynamic 250 W wall 0.401,
  125 W 0.114 - but every lamp's reach followed its strength (radius = sqrt(lumens / 40)), so the weak lamps were cut
  off before the wall. Row 2 (`lc2`, `tests/scenes/lightcal2.blend`): baked 1000 W beside dynamic 50 / 100 / 200 / 400
  / 800 W, every lamp with the same 20 m reach. Building.
- **Channel packing round trip per class: `tests/test_roundtrip.py` PASS** (`tests/roundtrip.json`): known colour,
  roughness, metal, normal, emission and cut-out mask -> source images -> `texpack.pack` with the class's template
  slots -> `texpack.unpack` -> the same within one 8-bit step, for opaque (CS + NAM), emissive (CS + NAM + E), cut-out
  and two-sided cut-out (CS + NW with the mask), lava (emissive slots); a normal map handed in green-down is detected
  as DirectX and packs to the same bytes as its green-up twin. Glass takes no texture.
- **Silent-failure table: every row now has its name in the tool and its state** (rows 1 - 39 filled from the code,
  not from memory: e.g. the door sound rule, the build lock, the two emission fields, the 6 GB bake gate were looked up).
  Honest gaps written into the rows: zero-uv triangles, doorway / riser measurement, flipped faces on open meshes,
  a clash with another installed mod, program-per-declaration only at merge time, the memory limit not measured.
- **Lava says what it is:** new check `class.placeholder` (warning) + test scene.
- **GLASS class written (07:53):** `hosts/kamino_01.TEMPLATES['glass']` = the host's single-material window-glass mesh
  `o_kam_wallchamber_windowglass_01` on `Levels/MP/Kamino_01/Shaders/SS_KaminoGlass_01` (found with `ls --type
  RigidMeshAsset`, `port mesh-info`, a deep dump of its material: no textures, no parameters). The class takes
  nothing from the Blender material - every pane is this host's window glass; the Check tab says so (`class.limited`,
  info). Solid (Exact), left out of the light bake. `tests/scenes/make_glasstest.py`: a glass wall with a red and a
  blue pillar behind it and a red one beside it. Build `gl1` running. UNKNOWN until seen: whether it draws at all on
  an imported mesh, and whether it is see-through.
- **Kill volume test:** `tests/scenes/make_killtest.py` (team 2 spawns on a hatch - a door lying flat - over a 2.8 m
  pit filled by a kill volume) + `tools/kill_probe.py` (deploys the player three times, follows camera height and
  whether he still has a soldier). The game put the player on team 2 in both collision runs, hence team 2. Build
  `kt1` running.
- Slip: two of my patch scripts wrote an apostrophe into a single-quoted string (`checks/__init__.py`, then
  `test_roundtrip.py`); the first one broke the import for 2 minutes and stopped the first `gl1` start. Fixed,
  import and tests re-run. No build that was running used the broken file.
- Check scenes regenerated (38 + 4 patched map files); suite re-running.

### 07:56 - 08:20 user awake; Hunters Supremacy; calibration numbers; glass seen by the user
- **The user woke at 08:05.** Asked where things stand (answered), for pictures of all maps but the test one (9 + 12 +
  3 sent: Mustafar, Rhen Var, Hunters), then "let me play on hunters map".
- **Hunters in offline Supremacy (`work/HuntersArena/review/has1/`):** build rc 0 (supremacy stage: 501 / 3 / 83, 2,231
  safe spots; audit clean, lint 0 errors / 442 warnings, group census 542 / 542, depot census 542 / 542, 140
  materials). Game: world up 90.5 s, player deployed, 14 bots, no faults; `base1_to_base2.png`: post beam and capture
  bubbles. **Supremacy is now seen on Mustafar, Rhen Var and Hunters.**
  FLAW, clearly visible in that picture: a thin white line across the sand in the shape of a polygon edge (a lightmap
  chart seam or a v-band cut). Open.
- **Blast (the online mode name) loads:** Mustafar `mtb_Blast` rc 0, Rhen Var `rvbl_Blast` rc 0. Hunters: not run.
- **Check suite: 42 of 42** (`class.limited` for glass, `class.placeholder` for lava, `class.unsupported` through a
  patched map file).
- **Light calibration row 2 (`work/LightCal/review/lc2/`, every lamp with a 20 m reach), same picture region per bay:**

  | bay | floor (8-bit) | back wall (linearised display) |
  |---|---|---|
  | baked 1000 W | 103.9 | 0.430 |
  | dynamic 50 W | 80.4 | 0.160 |
  | dynamic 100 W | 115.2 | 0.310 |
  | dynamic 200 W | 155.1 | 0.527 |
  | dynamic 400 W | 194.4 | 0.759 |
  | dynamic 800 W | 225.6 | 0.918 |
  | no lamp | 1.3 | 0.016 |

  Matching by interpolation in log-watts: the baked 1000 W bay looks like a real-time lamp of **80 W on the floor**
  and **147 W on the wall**. The two differ because the floor carries the real-time light's highlight (the lightmap
  has none) and the baked bay includes bounce. So: **a baked lamp is 7 to 12 times too dim against the same lamp
  real-time; best single figure about 9** (INFERRED from two matches with those corrections, not a clean number).
  Where it comes from is UNKNOWN: the bake scene converts a light record by the engine's own export rule
  (`FluxScene.FromRecord`: colour x Intensity / (area x pi), punctual sphere area 4), so the factor sits either in my
  lightmap write-back or in how the engine scales a punctual light on screen. The same hidden factor would have been
  absorbed "by eye" in the emissive strength (x 280) and the sky brightness.
  PLAN (not done): a per-lamp "Brightness in the bake" field + one host constant applied to the bake's own light list
  (`enlighten flux-scene --lights <json>`), then the four-bay scene at 100 W to confirm baked = dynamic on the wall.
- **Glass:** `gl1` built (rc 0); its game test had just loaded the level when the user asked to play. The user saw
  that level on screen and wrote **"glass panel works"** - glass is confirmed by the user's eye. My own pictures of
  it were not taken (the quick shots did not write files before I closed the game).
- **For the user's play session (08:17 - 08:19):** I stopped my two queued game tests (`kt1` kill volume, `gl1`
  glass; both my own processes), ended my own pending launch, closed the GlassTest game I had started, removed the
  stale queue lock, then launched `HuntersArena_02` in Mode8 hands-off (`write_play_args.py` + FrostyAutoTest) and
  watched it with the new `tools/watch_launch.py` (answers the EA sync box if it appears): LOADED after 38 s, no fault,
  no EA box. **Rule while the user plays: no game launch, no game command from me.**

### 08:20 - 08:32 bake lamp scale; WIP bundle for the user's friend
- **Bake lamp scale (the plan of 08:20, now written):** `hosts/kamino_01.BAKE_LAMP_SCALE = 9.0` (INFERRED from the two
  matches above); per-lamp field "Brightness in the bake" (`bake_strength`, Lighting tab, lamps in Baked / Both mode);
  the bake stage now reads the level's light records itself (`level export-lights`), multiplies each lamp's Intensity
  by the host factor and the lamp's own, and hands that list to `enlighten flux-scene --lights` - the real-time light
  entity is not touched. Bake stage version 4. First run (`logs/lt2_build.log`, the four-bay scene at 100 W, 20 m
  reach, `tests/scenes/lighttest100.blend`): 151 light records in the level, 2 baked, Intensity 1000 -> 9000 lm in the
  bake list, bake 108 s, 0 NaN. NOT yet seen in game (the user is playing) - whether baked now equals dynamic is
  UNKNOWN until `lt2` is looked at.
- **The user asked for a WIP zip / every dependency for a friend's PC.** `tools/make_bundle.py` ->
  **`dist/BF2MapTool_WIP_2026-10-12.zip` (78.1 MB, 202 entries):** the add-on zip (60 files, 244,190 bytes; holds
  supremacy, census, checks/space, overlay, donor file, leakmask), `FrostyCLI/` (the Release folder of my worktree
  build WITHOUT `Caches`), `FrostyPlugins/` (a copy of the user's Frosty Editor Plugins folder, 143 files - said so to
  the user), `HavokCollisionTool/`, `collision_template/`, `fluxbake/`, `scenes/arena.blend`, `docs/`, `README.txt`
  (what he must install himself: game, Blender 5.x, Python + numpy / pillow / scipy / xatlas, .NET 8, Frosty Mod
  Manager; which path goes in which preference; known limits). Too big for the remote upload (30 MiB limit): the
  user was told to take it from the folder. Path / name scan before packing: `NOT ALLOWED: 0`.
- **Bundle smoke test `tools/test_bundle.py` (`tests/bundle_test.json`): PASS on this PC.** Unzipped into a new
  folder; the EXTRACTED FrostyCLI with the EXTRACTED plugins built its game index from nothing and answered in 243 s;
  the add-on's `check-setup` with every path pointing into the extracted folder: 13 of 13 lines ok.
  Not covered: another PC, another game patch, a Python without the four packages.
- Slips: the Bash heredoc drops one backslash level - three patch scripts of mine wrote a broken quote / backslash
  (checks, round-trip test, bundle test). Each was caught by the next import or run and fixed within minutes.
  From now on patch text with quotes or backslashes goes through the Write / Edit tools, not a heredoc.

### 08:32 - 08:40 stopped by the user; add-on installed in their Blender
- The user's game session on Hunters Supremacy ended 08:26 (exit code 0x71A in the game log, no new crash dump: read
  as a normal quit - NOT confirmed by the user). I said I would resume my game tests and started `kt1`, `gl1`, `hbl`.
- **08:33 the user: "wait stop im gonna try this plugin myself".** Stopped everything of mine: the three runner
  scripts and their game test, my pending FrostyAutoTest run, the game my `hbl` test had just started
  (HuntersArena_01, started 08:32:25 by me), and my three builds in flight (`LightTest` at lint, `KillTest` at the
  kill stage, `GlassTest` at the closure audit) with their FrostyCLI processes; queue lock removed. A Blender started
  08:32:09 without arguments was the user's - not touched. Those three work folders hold a half-run build: the next
  build of each resumes (the runner's rule), nothing else depends on them.
- **08:38 the user: "install into blender".** No Blender was running, so: `tools/install_for_user.py` run headless
  WITHOUT --factory-startup = the user's own profile (`%APPDATA%/Blender Foundation/Blender/5.2`): add-on installed from
  `dist/bf2_map_tool-0.1.0.zip` (the 08:25 build), enabled, the seven paths + Python filled from `work/settings.json`,
  Check setup run (13 of 13 ok), preferences saved. This is the only change made outside `I:\BF2MapTool` tonight,
  and it was asked for.
- The user also confirmed by eye: "glass panel works" (GlassTest level on their screen, 08:17).

### 08:40 - 09:15 the user's own scene (Veranda); three "one click" fixes; new zip
- The user installed nothing by hand: the add-on was in their Blender (my install at 08:38). They opened their own
  scene **Veranda** (46 objects, 664,083 triangles, 61 materials - an architectural scene, not a game map) and
  pressed Port map.
- **Pre-flight stopped it with 5 errors** (their screenshot): 2 objects without a UV map (`Buildingrods`, `Water`),
  2 cut-out materials without holes (`Gordijn`, `Material #62` - 45 of the scene's classes were guesses, so probably
  from "Guess classes"; not confirmed for those two), team spawns 17 m apart, one spawn with no floor under it.
  They fixed those by hand and pressed again.
- **Second run stopped in `spawns`:** "Only 1 safe spawn spots ... (411 m2 in its largest connected part)". Measured on
  their export: navmesh 175 polygons in 10 parts, 731 m2, main part 412 m2; 98 % of the sampled spots were within
  2.5 m of a floor edge (median 0.61 m) - a furnished veranda, every prop with Exact collision. A limit of my planner
  (fixed 2.5 m edge clearance, 30 spots or stop), not a fault of the scene.
- **Fix 1 - spawn planner adapts to tight maps (`stages/spawns.py`, inputs v5):** small floors are sampled more
  densely (up to 2 spots / m2); clearance ladder (2.5 m, 3.0 m headroom) -> (1.5, 3.0) -> (1.5, 2.2) -> (1.0, 2.2) ->
  (0.7, 2.2) until 30 spots are found (stop only under 8); spot spacing follows the clearance; a team base with fewer
  spots than slots shares them; a team only takes generated spots nearer to its own base than to the enemy's; hero
  starts that fall together move to the team bases; the 40 m between bases becomes min(40 m, half the walkable
  length). On Veranda: 612 sampled, **48 safe spots at 1.5 m**, Blast bases 40 m apart (the user's two spawns), HvV
  starts 40 m apart. The numbers used are written to `spawns.json` (`candidates.edge_clearance_m` ...).
- I finished their build from the spawn stage myself (`python -m bf2maptool port work/Veranda/export/map.json`):
  spawns 33 s, kill 31 s, audit 447 s (closure 0, validate clean, lint 0 errors / 641 warnings), mod 14 s ->
  **`work/Veranda/mods/Veranda.fbmod`, 84,899,710 bytes, rc 0 at 09:13.** 602 pieces, 1,047,093 triangles.
  NOT launched, NOT seen in game by anyone yet. While it ran the user pressed Port map once: refused by the build
  lock ("Another build of this map is already running") - as designed, explained to them.
- **Fix 2 - objects without a UV map are unwrapped by the export** (box projection, 1 uv unit = 1 m,
  `export_scene.mesh_arrays`), reported as warning `geo.auto_uv`; `geo.no_uv` stays for old exports.
- **Fix 3 - a cut-out class that was only GUESSED and whose image has no hole is built opaque** with a `class.auto`
  note (`texpack.effective_class`); a cut-out the user chose himself still stops with `tex.cutout_no_alpha`.
- **Check suite: 43 of 43** (new cases `geo_auto_uv`, `class_guess_fallback`; both must report no error).
- **New zip + bundle at 09:11** (`dist/bf2_map_tool-0.1.0.zip` 245,872 bytes; `dist/BF2MapTool_WIP_2026-10-12.zip`
  78.1 MB); path / name scan `NOT ALLOWED: 0`. The three changed files were also copied into the add-on installed in
  the user's Blender profile (the build runs outside Blender, so no restart is needed).
- FINDING, open: the wall cutting (`split.cut_v_bands`) turned 26,920 triangles of six materials into 410,367 on this
  scene (15x), and 12,470 faces had bad uvs. Too heavy for dense meshes: it needs a cap (leave a material on the
  probe-lit template when cutting it would multiply its triangles by more than a few times).
- User questions answered: where the mod lands; how to restart after a failed stage (press Port map again); why a
  46-object scene takes long (664k triangles -> 602 pieces).

### 09:20 - 09:45 "match the scene" lighting: written, first calibration build started
- **The user's request (09:20):** "Do blenders world as sky ... I basically want it to be able to light a whole map in
  blender sun values sky and be able to port that as accurate as possible to bf2 you can auto test and do anything
  I'm gonna be afk again". The loop is on again for THIS task; the earlier open items wait.
- **World -> sky (`addon/bf2_map_tool/worldbake.py`):** the scene's World is rendered alone with a 360 degree camera
  (Cycles, 16 samples, 2048 x 1024) to `export/world.hdr`, scene-linear - whatever the World is (HDRI with rotation,
  Sky Texture, colour, nodes). `tests/lightmatch/test_worldbake.py`: a World that shows its own direction as colour
  decodes to `worldbake.direction(u, v)` within 0.69 degrees (144 samples); a plain (0.2, 0.4, 0.8) x 3 World reads
  (0.594, 1.188, 2.391) = the .hdr format's 8-bit mantissa. PASS.
- **New sky choice "Blender World (match the scene)"** - now the DEFAULT for a scene that never set a sky; "Light level"
  (Automatic / Daylight / Overcast / Dusk / Interior / Night). The export also records Blender's view transform,
  exposure and gamma, the sun lamp's angle, and a lamp's colour temperature.
- **One rule for every light (`runtime/bf2maptool/photometry.py`), used when the sky is the World:**
  D = nits per Blender unit of radiance = 1.2 x 2^EV x 2^(Blender exposure); sky texel = radiance x D; sun lux =
  Strength x D; point lumens = Watts x D / pi; spot = Watts x D / (4 pi); area = Watts x D / 4; emissive multiplier =
  Strength x D. These are how Blender and a physically based engine DEFINE those lights; whether this engine follows
  them is what the calibration measures - every constant is 1.0 = "by the formula" and graded UNKNOWN until measured.
  The game's EV only places the level among the game's own effects; the picture does not depend on it.
- **In matched mode the look stage also:** takes the sun's strength and colour from the Sun lamp (before: the donor
  level's 60,000 lux x a preset factor, white), pins the exposure (MinEV = MaxEV, AutomaticExposure off), switches the
  donor level's colour-grading LUT off, hides the game's own sun disc (the World panorama shows what the sky shows).
- **BUG FOUND in the bake's sky lookup:** `bake._atan2_approx_hq` (the port's transcription of the engine's fast
  atan2) returns wrong panorama columns in 5 of 8 octants (e.g. azimuth -45 and -135 degrees both -> 0.125). Invisible
  while every sky was the same all the way round; an HDRI would have lit the bake from scrambled directions. Replaced
  by the exact form the three self-consistent octants give: column u = 0.75 - azimuth / 360 (`look.pano_u`), used by
  the visible sky AND the bake. Round trip World -> panorama -> the bake's lookup: 0.00 degrees over 1,920 random
  directions. The 0.75 and the sign are INFERRED - to be confirmed in game by the coloured sky markers. Bake v5.
- **BUG FOUND in the export:** the headless export ran with the user's Blender profile, so the add-on INSTALLED there
  (older) registered the scene settings and the new ones were missing. The export now runs with --factory-startup.
- **Light-match scene (`tests/lightmatch/`):** `make_scene.py` (8 grey patches 0.02 .. 0.8 in the sun, the same 8 in
  a wall's shadow, five closed rooms: point / baked point / spot / area lamp 100 W and a glowing panel x 5, a
  generated HDRI World with four coloured markers, sun 3 W/m2 at 50 degrees), `ref_render.py` (Cycles, the scene-
  linear radiance of every view -> `ref/reference.json`, 32 views), `shots.py` (the same 34 views for the game),
  `measure.py` (reads the game's tone curve off the sunlit ramp and every other light against it).
  Blender's numbers (Standard view): sunlit 0.8 patch 1.016, 0.18 patch 0.227; shade 0.18 patch 0.0645 (sky + bounce =
  28 % of sun + sky); point-lamp room floor 0.109, spot 0.103, area 0.406, glow room 0.316, the panel itself 5.0.
  Note: an Environment Texture maps the HDRI differently from the 360 camera, so the "+X red" marker shows in
  Blender when looking along -Y; the comparison is view against view, not against the label.

### 09:45 - 10:10 light match round 1 (`lm1`) - first numbers, game against Blender
- User, 09:50: "how long" (answered: first numbers ~10:00, calibrated ~11:00 - 11:30, a real map around midday).
  User, 09:58: after the test level, match it on "the padme room map" so it is not one map only; 10:02: "its verenda"
  = `work/Veranda` / their Veranda blend. Queued as the step after the test level.
- `lm1` build: lights stage (matched units, EV 13, D = 9,830 nits per Blender unit): 100 W point = 312,911 lm, spot
  78,228 lm, area 245,760 lm; look: horizon 7,628 nits; bake draft 83 s. Game: loaded, 34 pictures.
- **`python tests/lightmatch/measure.py work/LightMatch/review/lm1 ...` -> `tests/lightmatch/lm1_result.json`:**

  | what | game against Blender | reading |
  |---|---|---|
  | sunlit ramp, 8 reflectances | monotone; mid patch (0.18) Blender sRGB 132 -> game 94 | exposure: game shows 0.48 of Blender's Standard display (median of 8 patches, display-linear) |
  | shade ramp (sky + bounce, baked) against sun | **0.99** (5 patches on the ramp: 0.86 .. 1.06) | the sky's light and the bake's scale are right against the sun - no correction |
  | sun colour (r/g, b/g of the sunlit 0.18 patch) | 0.877 / 1.220 against Blender 0.901 / 1.185 | right within 3 % |
  | point lamp room (Both), floor | 0.55 | too dim |
  | baked point lamp room, floor | 0.31 | too dim (with the x 9 bake lamp scale in) |
  | spot lamp room, floor | 0.14 (below the ramp, extrapolated) | too dim |
  | area lamp room, floor | 0.18; wall 0.001 | too dim; the wall beside a down-facing area lamp is black in game |
  | glowing panel's room | 0.75 (floor 0.91, wall 0.60) | about right |
  | glowing panel itself (strength 5) | black (sRGB 1 1 1) | BUG: lights the bake, does not show (`bay_glow_panel.png`) |
  | sky markers | every view shows the colour Blender shows a quarter turn away | panorama zero off by 90 degrees |
  | sky straight up | Blender 137 179 243, game 158 165 174 | brighter and greyer than Blender's - open |

  Grade: MEASURED once, on one scene, draft bake; readings "on the ramp" are interpolated through the game's own
  response curve, "extrapolated" ones are weaker.
- **Corrections made from it:** `photometry.WHITE = 2.08` (exposure); `look.PANO_U0 = 0.0` (was 0.75; the direction of
  turn was right - the four markers were in the right order).
- **Round 2 scene** (`make_scene.py`): lamp rooms with near-black walls (0.02) so a patch shows the lamp's direct light
  almost alone; point / spot / area each as Dynamic and as Baked (6 rooms); the glow room; a lampless room with three
  small panels of strength 0.05 / 0.2 / 1.0 to find at which strength a glowing surface shows and how bright; lamps
  with radius 0. 94 objects, 38 views.
- Slip: my first `lm2` start raced the scene regeneration and exported the OLD scene (54 objects) - seen in the
  export line, stopped, restarted (81 objects exported).

### 10:10 - 11:05 light match rounds 2 and 3; Veranda against Blender (`vm1`); user reports purple + no glow

**Rounds 2 and 3 on the test level (game against Blender, same views)** - `tests/lightmatch/lm2_result.json`, `lm3_result.json`.
- VERIFIED (`lm3`): sunlit mid grey 125/133/146 in game against 125/132/142 in Blender; exposure 0.97; shade 0.88 - 1.07;
  the coloured sky markers sit where Blender has them (panorama rule `u = 0.0 - azimuth/360`, zero measured in game).
- VERIFIED (`lm3`), then corrected, correction UNTESTED until `lm4`: lamps seen live read 2.35 (point) / 2.02 (spot) /
  1.29 (area) of Blender -> `C_POINT 3.9`, `C_SPOT 3.7`, `C_AREA 4.4`. Baked lamps read 3.3 - 4.5 (point), 0.48 (spot),
  1.62 (area) -> the bake's lamp scale is now per kind (`BAKE_LAMP_SCALE` 30 / 205 / 39 on the host).
- VERIFIED (`lm3`): a glowing panel lights its room at 0.80 - 0.88 of Blender. The panel ITSELF: on a black base colour it is
  black, on a 0.8 base it shows at 0.77 - 0.83, at Blender strength 5 it is black again. Two causes, both measured:
  the emissive surface shows `emission texture x colour texture`, and the multiplier overflows above ~65,504.
- Sky seen directly: 1.97 of Blender with the host's gradient on top, 0.59 without. Gradient now written x0.3. UNTESTED.
- View transforms (`tests/lightmatch/view_transforms.json`): the game's curve is closest to AgX; Filmic x1.12, Khronos x0.85.

**Veranda (`vm1`, baked, World sky)** - sheets in `work/Veranda/review/vm1/side/`. Structure, sun direction and sun colour
agree. NOT matching: walls lilac, teal / magenta islands on surfaces in shadow, dotted white lines on seams. The user saw
the same in their own run ("some of textures etc are purple") and reported "also emissives dont woerk" / "when i first
made the map emissives didnt show at all then as well".
- Ruled out by reading the files: colour textures (correct), the lightmap atlas (warm, clean, no overflow, the three
  direction maps agree), missing lightmap UVs (only the 14 glowing pieces have none, by design).
- UNKNOWN: the cause. Test running: the same scene built WITHOUT a bake (`vm2`) - purple there too = material / normals,
  purple gone = the lightmap path.

**Glow fix (written, UNTESTED until `lm4` / `vm2`)**
- `stages/textures.py` v11: for a glowing material the glow's hue goes into the colour texture (`_CSglow`) and its level
  into the emission texture (`_Eglow`), so their product is the Blender emission whatever the base colour is.
- `stages/importer.py`: multiplier capped at 60,000 (`emissive_at_ceiling` in place.json says how many hit it). The old
  rule (x280 per unit) made Veranda's lamps (strength 2.9 / 3.1) far too dim to see - that is why they never showed.
- `stages/bake.py` v8: a glowing piece's radiance for the bake is read from emission x colour.

**Mistake logged:** started `vm2` through a script patch that had not applied (it would have baked with quality `-`);
killed my own run after 1.5 min, patched `compare_scene.sh` with the editor (`-` = build without bake), restarted.

### 11:05 - 12:15 round 4 on the test level; Veranda with its own World; hunting the rainbow shading

**Round 4 (`lm4`, test level)** - `tests/lightmatch/lm4_result.json`, game / Blender:
- VERIFIED: glowing panels show at 0.99 of Blender (black base, white base and strength 5 alike) - the glow fix works.
- VERIFIED: shade 1.01, plain sky 0.96; lamps live 0.85 (point) / 0.90 (spot) / 0.98 (area); lamps baked 0.83 / 1.23 / 0.96;
  room lit by a glowing panel 0.84. Fine-tuned from these, UNTESTED: `C_POINT 4.6`, `C_SPOT 4.1`, `C_AREA 4.5`,
  bake spot scale 151, `C_EMISSION_BAKE 1.19`.

**Veranda: what was actually wrong first** - VERIFIED from `export/map.json`: the scene was saved with Sky = "sunset"
(a preset), not "Blender World", so `vm1` / `vm2` and the user's own run had the preset's pink sky and a full-strength
sun the Blender scene does not have. `tests/lightmatch/set_sky.py` sets the choice in my test copy.
- `vm3` (World sky, draft bake): mean colour of the eight views within 1 - 24 of 255 of Blender, the overview
  (`above_1`) 117/111/102 against 119/109/97. The glowing lamps show. Sheets: `work/Veranda/review/vm3/side/`.
- Mistake logged: `vm2` was NOT an unbaked build - the tool keeps its work folder per map name, so the old bake stayed in
  the level. A second attempt (`VerandaNB` as export folder only) also wrote into `work/Veranda` and was killed after
  1 min; `work/Veranda` now needs its import stage again. The real unbaked build uses a renamed scene copy
  (`tests/lightmatch/set_props.py ... name=VerandaNB`), running as `vnb`.

**Still wrong in `vm3` (UNKNOWN cause):** some trim pieces (materials "#72" column sleeves, "#132" arch trim, "#63",
"#364") shade teal / magenta / green, and thin white lines cross walls and stairs.
Ruled out, each by measurement:
- colour textures as the game stores them (exported and decoded, every mip flat and the right colour);
- the lightmap as compressed into the level (decoded with Microsoft texconv: relative error median 1 %, hue shift 0.4 %);
- the three colour-direction maps (same direction, cos >= 0.97);
- mesh normals / tangents in the game mesh (unit length, perpendicular, normals agree with the faces);
- vertex colours (none exported); material inputs (plain, same as healthy materials);
- a reflection volume (`rf1`: one distant volume in Dynamic mode added - no change. Note: the tool removes all 30 host
  reflection volumes and adds none; what the game then reflects is UNKNOWN);
- the surface's detail map tiling (`rf3`: `GlobalTilingDetailmap` 2 -> 2048 on those pieces - no change).
Lead: the "#72" pieces have 848 of 1511 triangles with no UV area; the healthy "#61.002" pieces have none.

### 12:15 - 13:10 the rainbow shading found and fixed; round 5: every light kind within 5 % of Blender

**The purple / teal / magenta shading (user: "some of textures etc are purple")** - cause found, VERIFIED in game.
- A truly unbaked build (`vnb`, scene copy renamed `VerandaNB`) still had it -> not the lightmaps.
- The pieces that showed it all carry the tool's 4 x 4 one-value textures (flat colour, flat normal / metal). Re-importing
  the 27 4 x 4 textures of the built level at 64 x 64 (`rf5`, nothing else changed) removes ALL of it: the arch trim is
  salmon as in Blender, the column sleeves cream, the glowing-white moulding bands (a wrong metal / smoothness read) are
  gone. `work/Veranda/review/rf5/side/` (sent to the user).
- Why a 4 x 4 texture reads wrongly in game: UNKNOWN. The file itself is right (exported from the project, decoded with
  texconv: all three mips flat and the right colour), so it is how the game reads the mips of a one-block texture. It
  showed only where the UVs spread over a surface (smaller mips sampled), which is why flat walls and the box-built test
  level never showed it.
- Fix in the tool: `texpack.FLAT_SIZE = 64`; flat textures and the smallest side of any packed texture are 64 px
  (`texpack.near_pow2`), textures stage inputs v12. Every earlier map built by the tool has this fault on surfaces with
  spread-out UVs and a flat colour until it is rebuilt.
- Also ruled out on the way (each by an in-game variant): reflection volumes (`rf1` Dynamic: metals go black; `rf2`
  Static: no change), the surface's detail-map tiling (`rf3`), the Enlighten sky box (`rf4`).

**The thin white lines** - VERIFIED from the geometry (`tmp/probe/ver_pick2.py`): at those pixels two wall meshes of the
scene ("Walls" and "WallHallways") lie 0.2 - 0.6 m apart and cross each other; the line is where the hidden one pokes
through. Blender shows both with the same shading so nothing is seen; in game each has its own baked light. A scene
matter, not a port fault. Not fixed; a check for crossing surfaces is a possible later addition.

**Round 5 on the test level (`lm5`)**, game / Blender, `tests/lightmatch/lm5_result.json` - all VERIFIED:
sunlit exposure 0.975; shade 1.007; plain sky 0.963; point lamp live 1.05 / baked 1.02; spot live 1.03 / baked 0.95;
area live 1.00 / baked 0.99; room lit by a glowing panel 1.01; the panel itself 0.985.
Constants now: `C_POINT 4.6`, `C_SPOT 4.1`, `C_AREA 4.5`, `C_EMISSION_BAKE 1.19`, bake lamp scale 30 / 151 / 39.

**New check** `look.sky_not_world` (warning): the scene's World holds a picture but Sky is a preset. VERIFIED on a copy of
Veranda set back to "sunset". The exporter records `look.world_kind`.

**Open, known:**
- Metals (Veranda's gold statues) are darker than in Blender: a ported level has no reflection capture of its own; the
  game falls back to the sky, masked indoors. Needs an own baked reflection cubemap - not built.
- Blender's Filmic view lifts shadows more than the game's curve: interiors read 5 - 18 of 255 darker with the same light.
- The draft bake is blotchy on big curved ceilings; `vm4` uses the normal preset.

### 13:10 - 13:55 Veranda final rebuild (`vm4`); own reflections; the "big shadow"

**`vm4` (Veranda, World sky, normal bake, 64 px flat textures)** - `work/Veranda/review/vm4/side/`. VERIFIED: no purple /
rainbow anywhere, lamps glow, mean colour of the views within 2 - 14 of 255 of Blender (`stand_0` +3, `stand_2` -9/-5/-3).
Normal bake: 4.54 M texels, baker 100 s, all bake steps 294 s; the ceiling is smooth.

**Own reflections (new)** - without one a ported level reflects only the sky, so indoor metals were dark.
- `addon/bf2_map_tool/reflbake.py`: in "Blender World" mode the exporter renders the whole scene from a free point at eye
  height near the play centre (`pick_point`: 14 rays, >= 0.6 m clear) to `export/reflection.hdr` (512 x 256, 15 s on Veranda).
- `runtime/bf2maptool/reflection.py`: panorama -> 128 px RGBA16F cube with 6 blurred mips (the format of the host's own
  reflection volumes, measured), values in nits (x D).
- `stages/look.py` (inputs v5): the cube is imported as `<prefix>/Lighting/T_<Name>_Reflection_01`, one
  `PbrDistantReflectionVolumeEntityData` (Baked) is added to the light layer; read back into `look.json` `reflection`.
- VERIFIED in game by hand (`rf6`): Veranda's gold statue goes from dark brown to bright gold as in Blender, indoors
  and outdoors. VERIFIED that the built-in path runs: `MustafarW` look stage, `in_level: true`.
- UNKNOWN: the game's exact blur per mip (mine is a plain widening lobe); one capture point serves the whole map, so a
  map with very different rooms reflects the wrong room in some of them. Ruled out as alternatives: Dynamic mode (metals
  go black), Static mode (no effect).

**The user's "big shadow" in the first picture** - VERIFIED cause: the game's shadow is where a sharp sun puts it
(`tmp/probe/ver_shadowmap.py`: the roof's shadow from the exported geometry has the same outline). Blender does not
show it because the scene's Sun lamp has Angle 33 degrees, which blurs that edge over about 4 m. The game cannot do
that: `SunAngularRadius` 2 or 16.5 (`rf7`) and the Pcss filter (`rf8`) leave the edge as sharp.
- New check `look.sun_soft` (warning) when the Sun's Angle is over 5 degrees; `SunAngularRadius` capped at 3.
- Not solved: a soft sun as Blender draws it. Would need the sun baked into the lightmap with its real size.

### 13:55 - 14:50 third scene (Mustafar, "Blender World"): three more general rules; packaging

**`mw1` -> `mw2` -> `mw3` (MustafarW: 108 objects, 6 Sun lamps, 24 point / spot lamps, glowing screens, AgX)**,
sheets in `work/MustafarW/review/mw3/side/`. Each run showed one thing the first two scenes could not:
- `mw1`: the map was hazy and had no sun. Causes: the add-on's fog (default on) - a Blender render has none; and the
  scene's old "Sun" switch was off although the scene has a Sun lamp. Rules now (exporter): in "Blender World" mode a
  visible Sun lamp always shines, the strongest one that shines downwards is THE sun (`look.suns` lists all; new check
  `look.one_sun` names the ones not carried over), and there is no fog unless **Game fog** (`fog_on`, new) is ticked.
  The Lighting tab hides Sky brightness / Sun in that mode. VERIFIED `mw2`: outdoor view matches (sunlit floor, shadows).
- `mw2`: a control room that is black in Blender was sunlit in game - the game's sun passes through faces that look
  away from it (a ceiling modelled facing down); Cycles does not. New `addon/bf2_map_tool/sunseal.py`: every exported
  triangle that looks away from the sun and whose back the sun reaches (one ray per triangle, helpers stepped over) gets
  a mirrored twin; materials with Backface Culling are left alone. Mustafar: 1,872 of 50,191 triangles, Veranda: 250.
  VERIFIED `mw3`: the room is dark, lit by its screens, as in Blender (`stand_3` mean 14/14/10 against 2/4/4 - the
  draft bake's bounce noise and lava glow remain).
- Reflections built in ran on all three builds (`look.json` `reflection.in_level: true`).

**Checks suite**: 43 of 43 pass (`tests/checks/result.json`, 169 s). `grep_paths`: NOT ALLOWED 0.
New warnings: `look.sky_not_world`, `look.sun_soft`, `look.one_sun` - each VERIFIED on a real scene, none has a suite case yet.

**Packaging** (all from the current sources, 14:46):
- `dist/bf2_map_tool-0.1.0.zip` (65 files, 265 kB) and `dist/BF2MapTool_WIP_2026-10-12.zip` (78.1 MB) rebuilt.
- Installed into the user's Blender 5.2 profile (`tools/install_for_user.py`: enabled, setup check all ok).
  VERIFIED: `check` run through the INSTALLED copy's exporter on a Veranda copy works (world bake, reflection capture,
  sun seal, the new warning). The user must restart Blender to load it.
- NOT re-run: `tools/test_bundle.py` on the new bundle (the CLI inside is unchanged since the 09:11 bundle, which passed).

### 14:50 - 15:15 final Veranda build (`vm5`); suite cases for the new warnings

- `vm5` (Veranda, World sky, normal bake, reflections + sun seal built in): `work/Veranda/review/vm5/side/`, sent to the
  user. VERIFIED by eye and numbers: `stand_0` +5/+7/+8, `stand_2` +6/+9/+14, `stand_3` +3/+6/+11, `stand_4` +2/+4/+15,
  `above_1` -10/-8/-5 of 255; gold statues gold; glossy floor reflects. `stand_5` is NOT usable (a bot's shield bubble
  fills the view). `stand_1` -18/-15/-12: the sharp roof shadow (the scene's 33 degree sun), as logged.
- Checks suite: three new cases (`look_sky_not_world`, `look_sun_soft`, `look_one_sun`), 46 of 46 pass.
- The user asked what changed since they last used the tool; answered in chat.

### Running in the background
Nothing.

### NEXT (exact)
1. `python tools/test_bundle.py dist/BF2MapTool_WIP_2026-10-12.zip work/settings.json tmp/bundle_test tmp/bundle_test.json`.
2. Open lighting items: soft sun (bake the sun with its real size), Filmic's shadows (grading table), several
   reflection points, sky seen directly on a real sky, sun only in the World, a check for crossing meshes.
3. Older open items: kill-volume death test, `lt2`, Hunters Blast / HvV, wall-cut triangle count, clean-machine
   walk-through; earlier maps (ToolArena, MustafarT, RhenVar, HuntersArena) still carry 4 px flat textures until rebuilt.

## Downloads
- 2026-10-12 11:50 texconv.exe (Microsoft DirectXTex, official GitHub release https://github.com/microsoft/DirectXTex/releases/latest/download/texconv.exe, 966,480 bytes, sha256 dcfdec10244e02cf5037fba089c55fb7e1326b1c8181742d77d15fa5cb5eef06) -> tools/thirdparty/texconv/ ; used only to decode textures for checks, not shipped.

## Log
- 2026-10-12 ~00:30 prompt pasted without a message; asked once; "Start the job now"; loop started. (Times in this
  file are the machine clock from here on.)
- first turn, read: prompt (on-disk copy = the pasted text), PORT_METHOD.md (all), MustafarPort STATUS.md (all 879
  lines), split_mus, pack_mus, gen_mus, mesh_collision, dump_raw_obj, gen_look_mus, gen_mus9, gen_mus9t,
  gen_lights_mus, gen_spawns_mus, door_export_mus, door_gen_mus, kill_plan, schem_pairs, cut_decl, grp_members,
  flat_check, cli.sh, run_full.sh, build_mod.sh, mbl/{classify, charts, gen_regeo, gen_mbl_materials, gen_mbl_probes,
  run_bake2.sh, run_mbl_apply.sh, checks/*}, leakmask header + build.cmd, lava_split header; PolisMassa STATUS from
  "Supremacy map (PolisMassa_02)" to "Online", pmf_one, pms_strip, pms_bots, pmf_rootlate, pmf_cp (head), Mygeeto's
  supremacy note. NOT yet read (owed at their step): lava_split.py body, leakmask.cpp body, mgeo.py, sky_cube.py,
  pmf_cp.py rest, my_cp.py, door_gen.py (Polis Massa), Mygeeto bot-collision slabs, `SkirmishShell.cs`.
- created `I:\BF2MapTool\{addon,runtime,tests,tmp,logs,tools}`; FrostyCLI man pages saved to `logs/man/`.
- 00:35 own CLI built. 00:36 - 00:42 FBX writer + import experiment (default 1 adopted). `meshgeo.py` = copy of
  Mustafar's `work/mbl/mgeo.py` (no path in it). Disk: `I:\BF2MapTool` 54 MB + cli-wt 371 MB.
