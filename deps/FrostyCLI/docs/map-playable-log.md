# Map playable — custom BFII levels that play like shipped maps — LOG

The run log of the map-playable work (2026-09-25): every build and in-game run, what changed, the verdict and any
crash address. Paths such as `shots/...`, `work/...` and `mods/...` refer to the map-playable workspace
(`F:\Frosty\projects\map-playable`); the scripts named are in `tools/gametest/map-playable/`.

Every build: what changed, the verdict, any crash address. Newest last. Grades: **BFII_EXACT** (shipped data, the
dump, or shown in game), **INFERRED**, **UNKNOWN**. Prompt: `F:\Frosty\PROMPT-MAP-PLAYABLE.md`.

## 2026-09-25 05:35 — setup

- Worktree `F:\Frosty\projects\map-playable\frostycli-wt`, branch `feat/map-playable` off `feat/navmesh-build` @ bd7e084
  (the newest level-create line; `feat/enlighten-authoring` is PR #42 and is merged in only if a test needs relight).
- Harness: claimed by the pathfinding session (13-map bot batch, until ~06:45). Offline research until the claim is gone.
  My claims say `owner: map-playable session`. Never build (CLI or mod) while the game runs.

## 2026-09-25 05:40-06:00 — offline research (shipped EBX, both dialects in the brain's raw dumps)

### Q1 play-area boundary (BFII_EXACT, shipped EBX)

- The out-of-bounds rule is not placed in the level. It is a `WSShared.CombatAreaEntityData` + `CombatAreaManagerEntityData`
  pair inside each mode's logic prefab. Skirmish: `Gameplay/GameModes/Skirmish/PF_Skirmish_Logic`:
  - CombatArea: IsGhost true, TeamSpecific false, IgnoreAI true, **IgnoreHeight false**, Enabled true.
  - Manager: DamageDelay **10 s**, Damage **900000** (a kill), UnlockAsset `gameplay/equipment/common/u_outofcombat_deathscreen`,
    DistToEdgeMax 200, DistToEdgeMin 10, AngleFadeMax 0.2, BuildUIData true. HUD: `ui/ingame/hud/soldier/prefabs/pf_hud_outsidecombatarea`
    (local players 0 and 1).
  - BFII's `CombatAreaEntityData` has no shape field (type dump: DisabledTimeout, IsGhost, TeamSpecific, IgnoreAI,
    IgnoreHeight, Enabled). The shape arrives through the prefab's interface link `CombatArea` (hash -1150572636 =
    hashQuick("CombatArea")).
- Tatooine wires it per mode sub-world, in the sub-world partition's LinkConnections:
  - Skirmish: `TeamDeathmatch_Skirmish` : `PF_TeamDeathmatch_Skirmish` ref c4ad5c71 `.CombatArea` ->
    `TeamDeathmatch_Skirmish_Logic` `SpawnLocationFinderShapeData` 6c6a5ce9: 34 points at y 57.81, **Height 40**,
    x 68.6..175.5, z 23.6..145.3 (the Blast arena on the Mos Eisley streets).
  - Blast online: `TeamDeathmatch` : sub-world ref `TeamDeathmatch_Online` 75d3241e `.CombatArea` -> `TeamDeathmatch_Shapes`
    `VolumeVectorShapeData` 364f5871: 127 points at y 92.16, Height 0, x 23.7..218.9, z -6.4..178.2.
- So a `level create --skirmish-from Tatooine` level inherits the Skirmish arena polygon at the host's coordinates
  (kept partition TeamDeathmatch_Skirmish_Logic) and the Online polygon too (TeamDeathmatch_Shapes is kept) with nothing
  linked to it (the Online sub-world is left out). INFERRED until checked on a built level: the polygon's vertical span is
  y 57.8..97.8, so ground outside that band inside the arena counts as outside (IgnoreHeight false).

### Q2 minimap (BFII_EXACT, shipped EBX)

- The HUD minimap is `UI/InGame/Hud/Radar/RadarWidget`: a ShapeMaskingContainer (mask `ui/shapes/ingame/hud/radar/radarmask`)
  holding RadarGridElementData (SquareSize 20, MaxRange 50), **RadarCombatAreaElementData** (CombatAreaType 0, MaxRange 50,
  LineWidth 2), RadarCaptureAreaElementData, objective collectors and icons. **No texture/image element** (same object set
  in both dialects). It is player-centred with a 50 m range and draws the level's combat area and capture areas.
- No per-level top-down map texture ships anywhere in the dump (file-name search: minimap, overview, topdown, *_map*,
  tacticalmap, levelmap; the only "MinimapType" assets are the Skirmish enemy-visibility option Regular/AlwaysOn/Off).
- Consequence: there is no image or world-to-map transform to author. The new layout reaches the minimap through its combat
  area (and capture areas); a top-down capture has no consumer in this HUD.

### Q3/Q5 menus, name, loading screen (BFII_EXACT, shipped EBX)

- Arcade level lists: `UI/Data/GameModes/<Mode>` (`GameModeInformationAsset`, e.g. SkirmishBlast: GameModeId SkirmishBlast,
  NumberOfPlayers 20, `Levels` = 18 refs) -> `UI/Data/GameModes/Levels/<Name>` (`LevelInformationAsset`: Key `Tatooine_01`,
  LevelId `Levels/MP/Tatooine_01/Tatooine_01`, Planet `UI/Data/GameModes/Planets/Tatooine` (PlanetIndex 6), LevelName /
  LevelDescription `LocalizedStringId`, LevelAurebesh `LocalizedString` "TATOOINE", per-mode LevelDescriptionOverrides,
  GridImage / LoadingScreenImage empty on every shipped level).
- The level's own `LevelDescriptionAsset` (`<level>/description`) lists the modes it supports (`Categories` ->
  GameMode inclusion: 13 on Tatooine), its bundles and superbundles.
- Loading screens: `UI/Configuration/WSUILoadingScreenSettings.MPLoadLevelScreenOverrides[]` keyed by **level path**
  (`LevelName`) -> `LoadingScreenAssetName` (`UI/LoadingScreens/MPLoadLevel/MPLoadLevel<Planet>Asset`, a
  WSUILoadingScreenAsset: ScreenData, a Lua widget, IntroMovie/Movie `UI/Video/Loading/Planet_<Planet>_01`), IntroMovieName,
  MovieName, DisplayTimeout 60.
- Corpus: one community map mod edits these (`Mygeeto Release ACTUAL.fbmod`): it keeps Kashyyyk_02's identity, rewrites
  LevelInformationAsset keys/strings, the loading-screen settings and a planet movie chunk. None adds a new level path to a
  mode's list.

### Kyber declaration (BFII_EXACT: plugin source, pinned)

- Correction to the memory note "Kyber custom map declaration": `ArmchairDevelopers__KyberLevelDeclaratorPlugin` keeps
  the JSON in the project as chunk `BEBEBEBE-...`, but at export it REMOVES that chunk (`Overrides.cs`: WriteProject
  prefix `RemoveAsset`) and ships the JSON as an embedded .fbmod resource named `KyberLevelDeclarationData` (an
  AddResource postfix after "Screenshot3"). That matches what Bioniip Laboratories.fbmod carries. JSON fields (current
  plugin): `version, maps[{name, id, image, supportedModes}], modes[{name, id, maxPlayers, image}], modeMappings,
  modeNameOverrides`; image = base64 JPEG, max 1024 px, quality 80.
- `FrostyModWriter.AddResource` appends to a list `WriteProject` never clears (1.0.6.3 source), so resources added
  before WriteProject ship; no Harmony needed.

### Custom Arcade pickers (BFII_EXACT, `UI/Metadata/OptionsEnumUIMetaData`)

| picker (TelemetryId) | mode | levels | label list |
|---|---|---|---|
| BlastLevelReferencesToPlay | SkirmishBlast (Team Battle) | 17 | BlastLevelToPlay |
| OnSlaughtReferencesLevelToPlay | SkirmishOnslaught | 17 | OnslaughtLevelToPlay |
| DuelReferencesLevelToPlay | SkirmishDuel | 17 | DuelLevelToPlay |
| InstantActionLevelReference | Mode8 (Instant Action) | 13 (Supremacy maps: geonosis_02, naboo_03, kamino_03, kashyyyk_02, felucia_01, paintball_01, takodana_02, jakku_02, greattemple, tatooine_02, scarif_02, hoth_02, commandsectornorth) | InstantActionLevelToPlay |
| ModeELevelRef / ModeFLevelRef | ModeE / ModeF (Missions attack / defend) | 21 | ModeELevels / ModeFLevels |
| StarfighterReferencesLevelTopPlay | starfighter arcade | 6 | Starfighter*LevelToPlay |

MosEisley (Tatooine_01) sits in Team Battle, Onslaught, Duel, ModeE and ModeF. The old AddMenuEntry joined every list
holding the host, so a Skirmish-only level would also have shown in the Missions pickers.

### Loading movies (BFII_EXACT, corpus_vanilla)

`UI/Video/Loading/Planet_Tatooine_01`: MovieTexture2Asset, `MovieFilename Planet_Tatooine_01.webm`, one chunk
b26ee9f6-..., 2,150,834 B. ffmpeg 7.1 is available (imageio_ffmpeg).

## 2026-09-25 06:00-06:15 — CLI (built only once the harness is free; nothing compiled yet)

- `level combat-area <level> [--area rect:x0,z0,x1,z1 | x,z;...] [--floor y] [--height h] [--field f]`
  (FrostyCLI.Core/CombatArea.cs, FrostyCLI.Commands/LevelCombatArea.cs): lists every CombatArea/CombatAreaOnline/
  CombatAreaSoldier link of a level (source, shape, points, x/z bounds, y band) and rewrites the linked shapes; counts
  spawns left outside. One loaded EbxAsset per partition (GetEbx parses an unmodified shipped asset afresh each call).
- `level create ... --combat-area <spec> [--combat-area-floor y] [--combat-area-height h]`; the Skirmish navmesh region
  then follows the new area.
- `kyber declare|show|remove` (KyberCommand.cs, Core/KyberDeclaration.cs) + `mod build` embeds the declaration as
  `KyberLevelDeclarationData` and leaves chunk BEBEBEBE out (HeadlessProject.WriteWithEmbedded); `mod build --embed
  name=file` for any embedded resource.
- Menu entry: joins only the lists and Custom Arcade pickers of the level's own modes (LevelDuplicator.MenuModes,
  PickerMode).
- Tests: CombatAreaTests (10), KyberDeclarationTests (7).
- Skirmish wiring on Tatooine, all link fields named (portnames.txt): the Skirmish sub-world ref (TeamDeathmatch) gets
  `FriendZones` (3 SpawnLocationFinderShapeData in TeamDeathmatch_Shapes), `Spawns` (30 AlternateSpawnEntityData) and
  `SpawnPointGroups`; inside TeamDeathmatch_Skirmish the prefab ref gets `CombatArea` (-> 6c6a5ce9), `FriendZones`,
  `Spawns`, `Spawners`, `AIPlayerProvider`, `AreaVolume` (defend areas). 6c6a5ce9 is linked only as CombatArea, so
  rewriting it moves the boundary and nothing else.
- BFII_EXACT: the Custom Arcade settings screen (`UI/Frontend/screens/Play/Skirmish/SkirmishSettingsScreen`) looks the
  picked level up by `LevelInformationAsset.Key` in a `PlanetLevelAssetLookupEntityData` (23 rows: LevelKey ->
  PlanetIdData {PlanetId hash, Tatooine_01 791492677} + FactionIdCollection {FactionId x2}): the planet beside the
  picker and the factions offered. `InstantActionSettingsScreen` has two such tables. A new Key has no row -- the likely
  reason the 2026-09-22 menu shot showed Jakku's planet for "New Level 01". The menu entry now copies the host's row
  under the new Key (`LevelDuplicator.AddLookupRow`).
- `LevelInformationAsset.GridImage` / `LoadingScreenImage` are CStrings (asset names), empty on every shipped level.

### The radar on a new level, before any fix (BFII_EXACT, a prior run's frame)

`shots/tk16_predeploy_radar.png` (crop of navmesh-authoring `tk16_predeploy.png`, TK16 = `level create` Tatooine shell,
2026-09-24): the octagonal HUD radar (bottom-left, x 60..255 / y 680..875 of 1600x900) draws a dotted 20 m grid, the
player marker at the centre, N/S labels rotating with the view, red radial wedges (the RadarZones sectors) and, as light
lines, the combat-area outline -- here the stair-stepped Mos Eisley street polygon inherited from Tatooine, drawn over
open desert. So the radar's picture of a level is its combat-area outline (plus capture areas), and a `level create`
level shows the old city's streets on it until its boundary is fitted. `scripts/radar_crop.py` crops and tiles radars.

## 06:30 — mode survey (background agent, shipped dbx; report `work/mode_survey.md`)

- Every Tatooine mode is streamed by `GameModes_Flow`: InclusionSetting(mode ids) -> And(GameModeStreamIn) -> SyncedBool
  -> SubWorldRef.StreamIn in `GameModes`. Blast -> TeamDeathmatch + StartGameModeOnline -> TDM_Online; Arcade ->
  TeamDeathmatch + StartGameModeSkirmish -> TDM_Skirmish; HvV -> HeroArena (+HvsVIntro); Mode6 -> Mode6 (+intro);
  Mode9/ModeDefend/ModeE/ModeF -> Mode9 (one shared sub-world); ModeC -> ModeC; PlanetaryBattles (GA) -> FantasyBattle;
  PlanetaryMissions (Strike) -> PlanetaryMissions. Navmesh streams per mode: 386940c0 Arcade, 5c0e8973 Mode9 group,
  8b6c5747 Strike/ModeC/GA.
- Boundaries: Blast = TDM_Online.CombatArea (VVS 364f5871); HvV = pf_heroesversusvillains_tdm.CombatAreaSoldier (VVS
  af9360eb, 100 pts); Mode6 = CombatAreaSoldier (edb21065); ModeC = CombatArea (SLF 48e1529f); Strike = two VVS into
  pf_strike_bombs through unnamed ports (#-1784016275 ...); Mode9 = 8 VVS in Mode9_OOBTeam1/2 into pf_mode9 through 16
  unnamed ports (#-1609661850, #1432407218, #-1108738971, #775000401, #97765348, #-1609661851, #-1108738970,
  #775000402, #902571232, #97765351, and ids of names that only collide in portnames.txt); GA = 9 VVS into
  pf_gamemode_conquest_staged (#-2126574220, #330836540 ...). `level combat-area --field` now takes raw ids.
- Instant Action (Mode8) has no sub-world of its own anywhere: Kamino_03 streams its Mode1 (Supremacy) sub-world for
  [Mode1, Mode8] and adds AI teams (pf_pve_teams_kam_pha_02) for Mode8. So IA needs a Supremacy host; a Tatooine_01
  shell cannot run it.

## 06:40-06:50 — CLI for more modes (built with the first harness slot)

- BFII_EXACT: each `GameModes` sub-world reference carries its modes: `InclusionSettings` -> SubWorldInclusionSettings
  `.Settings[]` -> SubWorldInclusionSetting `.EnabledOptions` (Tatooine: TeamDeathmatch [Blast, SkirmishBlast,
  SkirmishOnslaught, SkirmishDuel]; HeroArena [HeroesVersusVillains]; Mode9 [Mode9, ModeDefend, ModeE, ModeF]; Mode6;
  ModeC; FantasyBattle [PlanetaryBattles]; PlanetaryMissions; Mode1 and GameModes_Automation have none).
- From the pathfinding session (their dbx trace): ModeE / ModeF ARE Instant Action on the co-op maps
  (`Gameplay/Profiles/InstantActionParams`: ModeELevelToPlay / ModeFLevelToPlay; InstantActionModeToPlay options
  ID_MODE8_OPTION_*), reading the second navmesh stream (Tatooine 5c0e8973); Mode8 is Instant Action on the 13 Supremacy
  maps (Mode1 sub-world).
- `level create --skirmish-from <map> --modes Blast,HeroesVersusVillains,ModeE,ModeF,...`: Blast keeps TeamDeathmatch's
  Online half; every other mode brings its GameModes sub-world tree (art layers `props/architecture/.../_collision/runup/
  livingworld` left out, its static model groups cleared) into the mode graft (spawns stood at the median spawn height);
  the modes join the description; `--navgen` also builds the navmesh into each extra mode's own stream.
- Bots for online modes: Tatooine has no AutoPlayerManagerEntityData anywhere; with `--bots-team1/2`, one is added to each
  kept online PvP mode logic layer (TDM_Online_Logic, HeroArena_Logic, Mode6_Logic, ...; never an always-loaded layer,
  which would add auto-players to Arcade), HvV 3 + 4 and Hero Showdown 1 + 2 whatever was asked (hero modes start only at
  their team size). Fields as FOSD ships them (78d5c95b): PlayerCount -1, OrphanTimeSeconds 180, Flags = guid low 24 bits
  | 0x04 << 24.
- Test plan: `scripts/run_mp3.sh` (MP3 = MP1 + those modes; one build; hookspawn Blast, HeroesVersusVillains, ModeE with
  `mode_probe.py`: sub-levels, players per team, AI movement, shots).
- BFII_EXACT for Kyber (pinned `ArmchairDevelopers__Kyber`): `Packages/kyber_collection/lib/src/frosty/frosty_mod_reader.dart`
  reads a mod's declaration only when its category is exactly `Map`, `Maps` or `Gameplay`, and then takes it BY POSITION:
  the 6th resource (after the icon and four screenshots), JSON `CustomFrostyData` {version, maps[{name, id, image,
  supportedModes}], modes (required list), modeMappings?, modeNameOverrides?}. The launcher and CLI
  (`maxima_instance.dart`) also filter on category in ['gameplay', 'maps', 'map']. So `mod build` now sets category `Map`
  when a declaration is embedded (`--mod-category` otherwise) and writes the declaration as resource 6.

## 07:19 — harness taken; CLI built

- 07:19:42 claimed (pathfinding released 07:19:34). CLI build clean (4 old warnings, 0 errors). Unit tests: **405
  passed, 0 failed**, 160 integration skipped (22 new: CombatArea 11, KyberDeclaration 7, LoadingScreen 2, MenuEntry 2).
- MP1 = `level create AddedContent/MapPlayable/Levels/MP1/MP1 --skirmish-from Levels/MP/Tatooine_01/Tatooine_01
  --terrain-res work/tat/terrain.res --navgen <navgen> --combat-area rect:35,5,200,175 --combat-area-floor 50
  --combat-area-height 0` (terrain = the navmesh session's Tatooine test terrain). Test plan `work/mp1_plan.json`.

## 07:21-07:28 — MP1 in game (LOADED; test cut short by AI)

- MP1: `level create` log: the level inherits exactly one boundary link, `TeamDeathmatch_Skirmish`: PF_TeamDeathmatch_Skirmish
  `.CombatArea` -> SpawnLocationFinderShapeData 6c6a5ce9 (34 points, x 68.6..175.5, z 23.6..145.3, y 57.81, Height 40:
  inside 57.8 < y <= 97.8) -- **BFII_EXACT, the answer to "what a level-create level inherits"**; rewritten to the 4-point
  rect x 35..200, z 5..175 at y 50, no vertical limit; 0 of 81 spawns outside. Navmesh region = the area +-2 m: 168
  polygons, 35,920 B. `level validate` clean. mod 2.88 MB.
- **LOADED 07:23:22** (hookspawn SkirmishBlast). The local soldier deployed and the probe's `teleport ... direct`
  (ServerCharacterEntity::teleportTo) moved him to (122, 68.4, 84) -- teleports work on the human player.
- Then the Skirmish AI killed him at the first point ("DEFEATED BY Aran Gunnar", `shots/MP1_in_centre.png`); dead soldiers
  cannot be teleported and the harness cannot redeploy during a hold, so the boundary checks stopped. Not a boundary
  result. Next: MP1b = MP1 with the level's AIPlayerProviderEntityData teams removed (no AI).

## 07:40-07:50 — MP3 (modes) built offline, in the other session's no-game builds window

- MP3 = MP1 + `--modes Blast,HeroesVersusVillains,ModeE,ModeF --bots-team1 7 --bots-team2 8`. Plan: TeamDeathmatch (15
  partitions, Online half kept), Mode9 for ModeE/ModeF (16), HeroArena for HvV (9); navmesh stream 5c0e8973 for
  ModeE/ModeF; auto-player managers added to HeroArena_Logic (3 + 4) and TeamDeathmatch_Online_Logic (7 + 8); three
  boundaries rewritten (Skirmish CombatArea, TDM_Online CombatArea, HvV CombatAreaSoldier). Validate clean, 3.74 MB.
- Found on the way: 96 of 265 spawns lie outside the arena rect -- the co-op / IA missions spread over x -265..310
  (Tatooine's wider mission area; their own staged out-of-bounds volumes, unnamed ports, stay as shipped). With one median
  height they stood metres under the new hills. Fixed in LevelCreator: on a terrain of the level's own every moved spawn
  stands on it (ground + 0.5 m; MP3: Mode9 team 1 ground 63.1..81.6 m). The extra streams' navmesh now covers every spawn
  of the level + 30 m (MP3 co-op/IA: x -235..301, z -31..214, 305 polygons) instead of the arena only.

## 07:52 — MP1b (no AI): CRASH

- MP1b = MP1 with the level's `AIPlayerProviderEntityData` (TeamDeathmatch_Skirmish_Schematics) Teams list emptied.
  Harness verdict EXIT_LATE ("game exited 2 s after the world came up"), but gamelink.log shows a real crash:
  **ACCESS_VIOLATION at game+0x1B1483F, read from 0x0**, 07:53:04, ~2.5 s after world-up. An AI provider with no teams is
  not survivable. Next: MP1c = MP1 with PF_TeamDeathmatch_Skirmish's AIPlayerProviderManagerEntityData.MaxAIPlayerCount
  20 -> 2 (test-only, the shared prefab).
- BFII_EXACT: `UI/Video/Loading/Planet_Tatooine_01` chunk b26ee9f6 exported and probed: WebM, **VP8 1920x1080, 30 fps,
  10.03 s, no audio, 1.7 Mb/s**. `level loading-screen --image` now encodes the same (VP8, 1080p30, 10 s, 2 Mb/s).

## 08:09-08:16 — MP1c in game: the fitted boundary holds inside; the radar draws it (PROVEN)

- MP1c = MP1 + PF_TeamDeathmatch_Skirmish AIPlayerProviderManager.MaxAIPlayerCount 2 (test only). **LOADED 08:11:24.**
  The cap did not cut the AI count (the kill feed shows many AI names: the Arcade team sizes decide it), but the player
  survived this time.
- Inside checks, teleported (probe `teleport ... direct`) to ground + 1.5 m, 5 s each: centre (122, 84), 4 m inside
  each edge -- west (39, 90), east (196, 90), south (120, 9), north (120, 171) --, 5-6 m inside the SW (41, 11) and NE
  (194, 169) corners, the mesa top (108.6, 71.4 m, 100.6) and the pit floor (159.6, 57.7 m, 67.6). **Alive at every
  point, no out-of-bounds warning on any frame** (`shots/MP1c_in_*.png`). West4, south4, north4 and both corners lie
  outside Tatooine's inherited outline (x 68.6..175.5, z 23.6..145.3), and the mesa top / pit floor probe the vertical
  band: so the old outline and its 57.8..97.8 m band are gone and the new area rules.
- **Radar (Q2), `shots/MP1c_in_centre_radar_sheet.png`:** at the centre (>= 78 m from every edge) no boundary; 4 m
  from an edge a straight line 4 m from the player marker; at the SW and NE corners two lines meeting at a right angle at
  the marker; at the pit floor (40 m from the east edge) a line near the rim. The out-of-bounds side is filled with the
  dotted grid, the inside is plain. The radar turns with the view (S at the top: facing south). So the minimap of a new
  level is its fitted combat area, drawn player-centred at 50 m range -- no image, no transform to author.
- Views taken for the art: `MP1c_overview.png`, `MP1c_arena_high.png`, `MP1c_mesa_sw.png` (Night variant rolled; the old
  city's night lamp fixtures still hang over the sand -- the relight pipeline's `level clear --only-type` drop).
- The outside step did not run: after the views, `hud 1` (a heap scan) kept the game link busy and boundary_test's next
  connection failed (probe script crash, no JSON). Fix: retries after the views, results saved per point.

## 08:16-08:37 — MP3 in game: three modes on the new level (mod 0925.04)

- **Blast (08:16, LOADED 08:18:33):** `TeamDeathmatch_Online` streams (6400 entities), the round sits at pre-round
  "PLAYERS NEEDED: 3"; 2 players (the local one + one empty slot), no bots joined, the soldier never deployed. Verdict:
  **the Online half runs on the new level, but the added auto-player manager filled nothing.**
- **Heroes vs Villains (08:22, LOADED 08:25:32):** the hero select screen ("PRE-ROUND: EQUIP AND UNLOCK CARDS",
  "PLAYERS NEEDED: 5"; `shots/MP3_hvv_mode_mid.png`); again no bots. Same cause.
- **Instant Action / ModeE (08:29, LOADED 08:32:21): PLAYS.** 29 players (team 1: 18, team 2: 10, local on 2), 23 AI moved
  during the 120 s probe, the local soldier deployed at once and lived; HUD "CAPTURE A COMMAND POST", posts A/B/C, 4:39
  timer, a live kill feed (`shots/MP3_modee_mode_*.png`). The co-op stream (navmesh 5c0e8973, regenerated over the
  spawns) and the terrain-snapped spawns work. The local spawn was (261.6, 65.0, 95.4): x > 200, outside the arena rect,
  with no warning -- ModeE keeps Tatooine's own staged co-op area (its unnamed ports stay as shipped), so its play space
  is the shell's mission layout on the new ground, not the arena.
- **Why no bots (BFII_EXACT):** every shipped level that carries an AutoPlayerManagerEntityData fires its
  `ApplySettings` event -- Tatooine_02 1c9b260a, Kamino_03 b1ccfc44, Jakku_02 3ede46be, FOSD 78d5c95b: a GameModes_Flow
  SyncedBool (GameModeStreamIn AND InclusionSetting Mode1/Mode8 on Tatooine_02).OnTrue -> ApplySettings [18156C34],
  TargetType Server -- the same event that streams the mode's sub-world in. The managers are not network-registered.
  `level create` added a manager but nothing fired it. Fix in LevelCreator: next to the manager, a DelayEntityData
  (AutoStart, RunOnce, Delay 1 s, Realm_Server, TimeDeltaType_World) and, in the world partition that includes the layer
  (every shipped layer's connections live there), `delay.Out -> manager.ApplySettings`, Server. Flags measured over
  176 shipped auto-start server delays: 0x04 << 24 is set on 36 of the 38 that are event targets (the other two are
  targets of Client-side connections only) and on none of the 138 that are not; 0x01 << 24 splits 59/58 with no relation
  to the connections -- so the new delay, a source only, carries the guid low 24 bits only.

## 08:42-09:13 — waiting for the harness; one lost slot

- The pathfinding session tests map after map and re-claims within a second of each release, so a 15 s (then 3 s) poll
  never saw a gap until 09:13:08. That slot was lost: the waiting runner had been orphaned when its task was stopped
  (to restart it with the shorter poll; killing it was not permitted), and without a console its `dotnet build` could not
  start (rc 0xC0000142, STATUS_DLL_INIT_FAILED, 2 s, 0 compile errors), so the batch released at once (09:13:10; nothing
  built, nothing launched). Now `scripts/fastclaim.py` (0.25 s poll, the same exclusive create + verify) takes the gap and
  the batch runs from a fresh background task. A stray waiter from that restart (`go_mp2.sh`, Windows pid 37788) sleeps
  until `work/go_mp3b.log` gets a "done" line -- nothing writes that log any more.

## 09:24-09:45 — MP3b (mod 0925.06): the started manager fills the teams; the bots do not spawn

- CLI rebuilt (0 errors), unit tests **407 passed, 0 failed** (+2: AutoPlayerTests, EntityFlags). MP3 rebuilt in 2 min:
  `HeroArena_Logic: added an auto-player manager, bots 3 + 4, started 1 s after the sub-world streams in (HeroArena:
  delay.Out -> ApplySettings)`, the same for TeamDeathmatch_Online_Logic (7 + 8).
- **Blast (LOADED 09:28:19):** 17 players -- team 1: 8 (the local one + 7), team 2: 8, + the empty slot 0: **the manager
  fills the teams now.** The round starts ("DEFEAT THE IMPERIALS", 100 / 100) and the local soldier deploys after 5 s on
  the new sand, the mesa in view (`shots/MP3b_blast_mode_mid.png`). But no bot has a soldier at either sample (150 s
  apart): they joined and never spawned.
- **HvV (LOADED 09:34:29):** 9 players, 4 v 4; the local hero deployed at once; again no bot soldier.
- Why (BFII_EXACT, shipped bot logic): filled auto-players act only once made gameplay bots. Supremacy / Instant Action
  `addons/mode1/Gameplay/PF_Bot_Logic` (in every Supremacy map's Mode1_Gameplay): an AutoPlayerEntityData (Realm_Server,
  AutoStart off) whose `Enable` fires on a ModeState compare, `OnActivated -> AutoplayerIsGameplayEntityData.PlayerEvent`.
  Co-op `Gameplay/GameModes/Mode9/Prefabs/PvE_BotLogic` (the bots that fought in MP3's ModeE): the same entity enabled
  through gates, `OnActivated` and `OnSpawned` -> team filters -> two AutoplayerIsGameplay `PlayerEvent`s. Fix in
  LevelCreator: the manager's layer also gets an AutoPlayerEntity and an AutoplayerIsGameplayEntity; the start delay
  fires `Enable` (before ApplySettings), `OnActivated` and `OnSpawned` -> `PlayerEvent`. Next build: MP3c (0925.08).
- **MP1d walk-out (MP1c mod, LOADED 09:40:40): no result.** At deploy the soldier was at 6 HP under Arcade fire and died
  before the first inside check ("DEFEATED BY Reeech", `shots/MP1d_*.png`) -- the Arcade AI again, not the boundary. The
  walk-out moves to MP3's Blast round (0925.06), where the bots join but never spawn: the player is alone in a live
  round whose boundary is the fitted rect (TeamDeathmatch_Online CombatArea 364f5871, rewritten).

## 09:50-09:56 — MP3w: the boundary warns and kills (Blast, mod 0925.06) — PROVEN

- MP3 (0925.06) in Blast: the bots join but never spawn, so the local soldier is alone in a live round ("DEFEAT THE
  IMPERIALS", 100 / 100). Its boundary is TeamDeathmatch_Online's CombatArea (364f5871), rewritten to the arena rect
  x 35..200, z 5..175 at y 50, no vertical limit. **LOADED 09:51:51**, deployed at (109.1, 63.6, 163.3).
- Inside, west4 (39, 63.6, 90 -- 4 m inside the west edge): alive, normal picture, no warning; the radar draws the edge
  as a line 4 m from the marker (`shots/MP3w_in_west4.png`).
- Outside, x 20 (15 m past the west edge), teleported (`teleport ok ... via direct`): **4 s later the picture is
  desaturated and a countdown ring shows "1"** (`shots/MP3w_outback_out.png`); at ~5 s the soldier is dead (no
  character), the teleport back is refused ("no character"), the class-select screen follows and the Rebels' count drops
  100 -> 99 (`shots/MP3w_outback_back.png`). So the Online mode's rule is a ~5 s countdown, then death (Skirmish's
  PF_Skirmish_Logic: 10 s). The out-and-back (the warning clearing on return) needs a return inside ~3 s: not measured.

## 09:56-10:10 — MP3c (mod 0925.08): gameplay-bot pair added; the bots still do not spawn

- CLI 0 errors, **407 passed, 0 failed**. MP3c create log: both managers `... AutoPlayerEntity enabled with it, its bots
  made gameplay bots`. Blast (LOADED 10:00:06): 17 players (8 v 8 + slot 0), the local soldier deployed at once, no bot
  soldier in 150 s. HvV (LOADED 10:06:11): 4 v 4, the same. So Enable + OnActivated/OnSpawned -> PlayerEvent is not
  what they lack. Next lead: the co-op logic that makes MP3's ModeE bots spawn also applies AutoPlayerSettings
  (AllowRespawn = true from a BoolEntity, MaxSpawnsPerUpdate from GameSettings AP_MaxSpawnsPerUpdate, Initial/Respawn
  delays per Mode9Attack/Mode9Defend/Mode8), which PF_Bot_Logic does not; their defaults may keep bots from spawning.

## 10:12-10:38 — MP2 (mod 0925.07): the level from the game's own menus — PROVEN

- MP2 = MP1 + `--menu-name "Dune Sea Arena" --planet-name Tatooine --menu-description ... --loading-screen
  work/mp2_loading.png --kyber "Dune Sea Arena"`. Build: picker labels "TATOOINE - DUNE SEA ARENA" in 3 label lists,
  menu entry UI/Data/GameModes/Levels/MP2 (planet UI/Data/GameModes/Planets/MP2) in 5 list assets, Kyber declared for
  SkirmishBlast/Onslaught/Duel; the .fbmod carries `KyberLevelDeclarationData` as resource #5 (the 6th, after the icon
  and 4 screenshots), 39,540 B. Harness menu mode (launch only, no module), driven by clicks.
- Main menu -> PLAY -> SINGLE PLAYER -> ARCADE -> CUSTOM ARCADE: GAME MODE Team Battle, LOCATION one step right of
  "TATOOINE - MOS EISLEY" = **"TATOOINE - DUNE SEA ARE…"** (the picker field cuts the label); the info panel **"Dune Sea
  Arena"** + our description, Tatooine's planet, faction Galactic Empire (the copied lookup row)
  (`shots/MP2_menu_06.png`). MINIMAP option = RADAR / PRECISE (radar accuracy, no map image).
- PLAY -> "Are you sure you want to play Arcade with these settings?" OK -> **the loading screen is our own**: the art
  from the heightmap (VP8 still with the push-in), with the game's overlay **"Tatooine / Dune Sea Arena"** and a
  gameplay tip (`shots/MP2_ld_00.png`; the user saw it on screen too). Class select -> START: Team Battle "DEFEAT THE
  REBELS" 94 / 94 on the new sand by the mesa, AI on both sides, a live kill feed (`shots/MP2_ingame_01.png`). Pause menu:
  **"TEAM BATTLE AT Dune Sea Arena, Tatooine"** + the description (`shots/MP2_pause.png`).
- Quit to Custom Arcade (the level stays selected) -> GAME MODE Onslaught (LOCATION resets to Mos Eisley) -> one step
  right = Dune Sea Arena again -> PLAY/OK -> Onslaught loads (hero/enforcer select), deployed as a Death Trooper: 100
  eliminations, the 00:40 start timer, rebels around the mesa (`shots/MP2_ons_1.png`). Esc did not open the pause menu
  from the Onslaught class select; the harness closed the game at the end of the hold.
- Cosmetic, not fixed: the picker label is longer than the field ("DUNE SEA ARE…"); the shipped names are upper case
  ("MOS EISLEY"), ours prints as written ("Dune Sea Arena").

## 10:32-10:50 — the MP2 game outlived its harness task; closed through the harness

- FrostyAutoTest runs under a 20 min execution limit (`ExecutionTimeLimit PT20M`, Last Result 0x41306 terminated). The
  MP2 menu session asked for a 1500 s hold, so Windows ended the script at 10:32:23 -- before its own Stop-Game at the
  hold's end (10:37:54) -- and the game was left running (the user asked whether it would close). Killing processes is
  not permitted to this session, and the game runs elevated, so it was closed the sanctioned way: under our claim the
  task was run once more in menu mode with a 60 s hold (its start-up Stop-Game closed the stuck game at 10:44; it then
  re-applied the pack, relaunched, held 60 s and stopped again), then the claim was released.
- Fixed: `harness.launch` caps any hold at 720 s (pack apply up to ~5 min + load + hold must fit in 20 min);
  `run_menu.py` defaults to 720 s; the queued final turn asks for 720 s.

## 10:48-10:54 — MP3o: the warning clears on return; the countdown is ~7-8 s (MP3 0925.08, Blast, alone)

- **LOADED 10:50:11.** west4 (4 m inside): alive, no warning. Out to x 20 (15 m past the west edge, `teleport ok ...
  direct`, server position (20, 63.8, 90)): **2 s later grey picture and the countdown ring at "5"**; back to x 45 (via
  the frame route: the direct teleport FAULTED 0xC0000005 inside the probe this time -- caught, the game lived on):
  **13 s later alive, normal picture, "DEFEAT THE IMPERIALS", no ring** -- the warning clears on return and nothing
  kills. Then 15 m past the north edge (120, 190): ring at "5" and **"RETURN TO OBJECTIVE"** (~3 s), then **"DEFEATED"**,
  Rebels 100 -> 99 (`shots/MP3o_*.png`, sheet `shots/MP3o_sheet.png`); last alive sample 8.2 s, first dead 12.8 s
  (the shots stretch the sampling).
- Correction to MP3w: its "1 at 4 s" shot was taken ~5-6 s after crossing (sleep + the capture), so Blast's countdown
  is ~7-8 s from crossing to death, not ~5 s. Still shorter than Skirmish's prefab (DamageDelay 10 s).

## 10:54-11:21 — ModeF plays; MP4 (everything in one `level create`) from the Instant Action menu — PROVEN

- **MP3f, ModeF (Instant Action defend mission), MP3 0925.08, LOADED 10:55:52:** 29 players (10 v 18 + slot 0), 26 AI
  moved in 120 s, the local soldier alive; HUD "DEFEND THE COMMAND POSTS" A/B/C, 02:39, AI heroes as squadmates, by the
  sinkhole (`shots/MP3c_modef_mode_*.png`).
- **MP4 (0925.09)** = the MP3 modes + the MP2 menu entry, loading screen and Kyber, one `level create`
  (`scripts/build_mp4.sh`): picker labels in 5 lists (Team Battle, Onslaught, Duel, Missions Attack, Missions Defend),
  menu entry in 9 list assets, Kyber declared for 7 modes (resource #5, 39,587 B). Menu mode, hold 720 s.
- Single Player -> Instant Action: MODE "MISSIONS (ATTACK)"; the LOCATION picker lists, in step, KESSEL / Coaxium Mine,
  TATOOINE / Mos Eisley, **"TATOOINE - DUNE SEA ARE…" / Dune Sea Arena**, TATOOINE / Jabba's Palace, HOTH / Outpost Delta,
  ENDOR / Research Station 9, YAVIN 4 / The Great Temple, DEATH STAR II / Command Sector North (`shots/MP4_ia_loc_sheet.png`,
  `MP4_ia_back_sheet.png`; on opening, the info panel shows the last-played level until the picker moves -- a UI quirk).
- PLAY (no confirmation in IA) -> our loading screen -> **CRASH 11:07:33: ACCESS_VIOLATION in amdxx64.dll+0x106fd6
  reading 0xffffffffffffffff** (the AMD DX11 user-mode driver, RX 6800; `CrashDumps/CrashDump_2026.09.25_11.07.33.429.mdmp`)
  -- the same driver address HANDOVER-NEW-SHADER-2026-09-20 §22.8 met at a level load. Relaunched (11:10), same path:
  **loaded** (pre-round class select), START -> **the attack mission plays on the new level**: 05:54, "CAPTURE A COMMAND
  POST" A/B/C, a TK squad advancing over the dunes (`shots/MP4b_play.png`). So the crash was a one-off in the driver.
- The game closed at the hold's end (11:20:35, inside the task's 20 min) and the claim was released.
- Offline modes (UI/Data/GameModes, BFII_EXACT): SkirmishBlast (Team Battle) 18 levels, SkirmishOnslaught, SkirmishDuel
  17, ModeE / ModeF (Instant Action missions) 26 each -- all list Mos Eisley, so a Tatooine_01-shell level can play them
  -- and Mode8 (Instant Action proper) 15 Supremacy/GA levels, not Mos Eisley: Tatooine_01's GameModes has a Mode1
  sub-world reference, but its Mode1 is an empty stub (0 objects). Mode8 plays in a Supremacy map's Mode1 sub-world
  (Tatooine_02: 11 layers, 143 spawns, 15 prefab placements, PvE_BotLogic + PF_Bot_Logic, a Mode8_Shapes layer; its layout
  spans x -416..1991, z -990..1259, the new terrain x -390..634, z -428..596).
- CLI: the AutoPlayerEntity/AutoplayerIsGameplay pair (MP3c, no effect) removed again; build 0 errors, **407 passed**.

## 11:24-11:28 — MP4 in Arcade Duel (SkirmishDuel) — PROVEN; the last offline mode

- Claimed in the gap the Mos Tarrak build left (11:24:04). **LOADED 11:25:35.** 21 players (10 v 10 + slot 0), the local
  player deployed at once, 13 AI moved in 90 s; HUD "DEFEAT THE LIGHT SIDE" 77 / 60, heroes and troopers fighting on the
  dunes by the mesa, the player on a killstreak (`shots/MP4_duel_mode_mid.png`).
- The game window was gone at ~11:27:40, before the 240 s hold ended, after the probe's samples: no crash dump, no Windows
  application error, no harness CRASH line (the keep loop's last line: deploy input attempt 6, 11:27:41). Cause UNKNOWN.
  The claim was released at 11:27:50; the pathfinding session claimed at 11:27:52.
- So every offline mode a Tatooine_01-shell level can offer is proven on the new level: Arcade Team Battle, Onslaught,
  Duel; Instant Action Missions Attack and Defend.
