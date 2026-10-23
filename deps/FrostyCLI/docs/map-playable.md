# A custom level that plays like a shipped map — living spec

The living spec of the map-playable work: each question, its answer, the evidence grade and what was proven in
game. Paths such as `shots/...` and `work/...` refer to the map-playable workspace (`F:\Frosty\projects\map-playable`);
the test scripts are in `tools/gametest/map-playable/`, the run-by-run evidence in `docs/map-playable-log.md`.

Evidence is in `docs/map-playable-log.md`. Grades: **BFII_EXACT** (shipped data, the dump, or shown in game), **INFERRED**, **UNKNOWN**.
Proven in game is marked **PROVEN (run)**.

## At a glance (2026-09-25, test level "Dune Sea Arena" on the Tatooine_01 shell, AMD RX 6800 / DX11)

| Question | Answer | State |
|---|---|---|
| 1 Boundary | the mode prefab owns the rule, the level the outline; `level combat-area` / `--combat-area` fits it | PROVEN inside, outside (warning, countdown, death), return |
| 2 Minimap | a 50 m radar drawing the combat area; no image exists to author | PROVEN (edges and corners on the radar) |
| 3 Menus | `--menu-name/--planet-name/--menu-description`: entry, pickers in step, lookup rows; `kyber declare` | PROVEN offline (Custom Arcade, Instant Action); Kyber UNKNOWN (not installed) |
| 4 Modes | `--modes` grafts the shell's mode sub-worlds; Tatooine_01 offers Arcade, IA missions, Blast, HvV, ... | PROVEN every offline mode (Team Battle, Onslaught, Duel, Missions Attack/Defend); Blast/HvV load; Mode8 not offered |
| 5 Loading/name/art | `--loading-screen` (own video from a still), the entry's name; heightmap art | PROVEN |

Known limits: Mode8 (Instant Action proper) needs a Supremacy donor; offline Blast/HvV bots join but do not spawn
(they need PvE_BotLogic); the picker label is cut at ~22 characters; shipped level names are upper case.

## 1. Play-area boundary

- BFII_EXACT: the rule is in each mode's logic prefab, not in the level. Skirmish = `PF_Skirmish_Logic`: CombatArea
  (ghost, not team-specific, AI ignored, **height checked**) + manager (10 s delay, then 900000 damage, the
  out-of-combat death screen, HUD `PF_HUD_OutsideCombatArea`).
- BFII_EXACT: the level supplies the outline through an interface link `CombatArea` / `CombatAreaOnline` /
  `CombatAreaSoldier` from the prefab or sub-world reference to a `VolumeVectorShapeData` (or `SpawnLocationFinderShapeData`,
  its subclass) in one of the mode's layers.
- INFERRED (engine source, FIFA branch): inside = even-odd point-in-polygon on x/z, and for Height > 0 also
  `centre.y < y <= centre.y + Height`; Height 0 = no vertical limit.
- BFII_EXACT (MP1, `level create` + `level combat-area` listing of the built level): a `level create --skirmish-from
  Levels/MP/Tatooine_01/Tatooine_01` level inherits exactly one boundary link -- `TeamDeathmatch_Skirmish`:
  PF_TeamDeathmatch_Skirmish `.CombatArea` -> SpawnLocationFinderShapeData 6c6a5ce9 in its `TeamDeathmatch_Skirmish_Logic`:
  Tatooine's 34-point arena outline at Tatooine's coordinates (x 68.6..175.5, z 23.6..145.3), inside only for 57.8 < y <=
  97.8 -- the old city streets, not the new layout (on the TK16 radar: the stair-stepped street outline over open desert).
  The Online half's outline (TeamDeathmatch_Shapes 364f5871) is copied but unlinked (the Online sub-world is left out).
  `level combat-area <level>` lists every boundary of a level.
- Authoring: `level combat-area <level> --area rect:x0,z0,x1,z1 | x,z;x,z;... [--floor y] [--height h]`, or
  `level create ... --combat-area <same> [--combat-area-floor y] [--combat-area-height h]` (also sizes the navmesh
  region to the new area). Reports spawns left outside the new outline.
- **PROVEN (MP1c, 08:11)** inside: the fitted rect x 35..200, z 5..175 (floor 50, no height) -- alive, no warning at
  the centre, 4 m inside each edge, 5-6 m inside two corners, the mesa top and the pit floor, five of them outside the
  inherited outline or band.
- **PROVEN (MP3w 09:52, MP3o 10:50, Blast)** outside: 15 m past an edge the picture desaturates, a countdown ring runs
  ("5" ~2-3 s after crossing) with "RETURN TO OBJECTIVE"; staying out ends in "DEFEATED" ~7-8 s after crossing (Rebels
  100 -> 99). Back inside after 2 s: the ring and the grey go, no death (alive 13 s later). 4 m inside: nothing.
  (Skirmish's prefab: 10 s.)

## 2. Minimap

- BFII_EXACT: the HUD minimap is a radar (`UI/InGame/Hud/Radar/RadarWidget`), player-centred, 50 m range, 20 m grid,
  that draws the level's combat area (`RadarCombatAreaElementData`) and capture areas. It has no image element, and no
  shipped level has a top-down map texture. There is no per-level UI asset to author: the radar follows the level's
  combat area and capture areas. The Arcade MINIMAP option only sets the radar's accuracy (RADAR / PRECISE). So the
  prompt's image route (a top-down capture, `import-texture --template`) has no target; the top-down image made from the
  heightmap (`scripts/map_art.py`) serves as the loading-screen and Kyber art instead.
- **PROVEN (MP1c, `shots/MP1c_in_centre_radar_sheet.png`)**: 4 m from an edge the radar draws a straight line 4 m from
  the marker, at a corner two lines at a right angle, at the centre nothing; the outside is the dotted fill. The
  minimap of a new level is its fitted combat area -- nothing else to author.

## 3. Menus

- BFII_EXACT: Arcade lists = `UI/Data/GameModes/<Mode>.Levels` (GameModeInformationAsset) + the Custom Arcade pickers in
  `UI/Metadata/OptionsEnumUIMetaData` (level references and their labels, two parallel lists per picker: Team Battle,
  Onslaught, Duel, Instant Action, Missions E/F, Starfighter). Entry = `LevelInformationAsset` (Key, LevelId = level
  path, Planet, LevelName/LevelDescription strings).
- BFII_EXACT: the Custom Arcade screen (`SkirmishSettingsScreen`) also looks the picked Key up in a
  `PlanetLevelAssetLookupEntityData` (planet shown + factions offered); `InstantActionSettingsScreen` has two.
- CLI: `level create --menu-name <n> [--planet-name <p>] [--menu-description <d>]` clones the host's entry (and planet),
  joins the lists and pickers of the level's own modes only (labels inserted in step), and copies the host's lookup row
  under the new Key.
- **PROVEN (MP2, 10:15-10:30, menu mode):** Custom Arcade > LOCATION lists "TATOOINE - DUNE SEA ARE…" right after
  Mos Eisley in Team Battle and in Onslaught; the info panel shows "Dune Sea Arena", its description, Tatooine and the
  Galactic Empire; PLAY loads it and both modes play (Team Battle with AI, Onslaught as an enforcer); the pause menu reads
  "TEAM BATTLE AT Dune Sea Arena, Tatooine". Cosmetic: the label is wider than the picker field; shipped names are upper
  case.
- **PROVEN (MP4, 11:05-11:15):** with the extra modes, Single Player > Instant Action > MODE "Missions (Attack)" lists
  "TATOOINE - DUNE SEA ARE…" -> Dune Sea Arena in step between Mos Eisley and Jabba's Palace; PLAY loads it (one AMD
  driver crash, amdxx64.dll+0x106fd6, on the first try; the retry loaded) and the mission plays.
- Online: Kyber (not installed on this machine). `kyber declare` writes Kyber Level Declarator's JSON; `mod build` ships
  it as the embedded resource `KyberLevelDeclarationData` (the plugin's export behaviour). BFII_EXACT to Kyber's reader
  source: MP2.fbmod has it as resource #5 (the 6th, the position Kyber reads), category Map, 39,540 B with the tile. The
  mod manager applied and launched it. In Kyber itself: UNKNOWN (not installed here).

## 4. Modes

- BFII mode ids: SkirmishBlast/SkirmishOnslaught/SkirmishDuel = Arcade; Blast = online Blast; HeroesVersusVillains;
  Mode1 = Supremacy; Mode8 = Instant Action (Supremacy maps); ModeE/ModeF = Instant Action missions attack/defend (co-op
  maps); Mode9/ModeDefend = Co-op; PlanetaryBattles = Galactic Assault; PlanetaryMissions = Strike; Mode6 = Hero Showdown;
  ModeC = Jetpack Cargo.
- BFII_EXACT (survey, `work/mode_survey.md`): a map streams one GameModes sub-world per mode family, named by the
  reference's `InclusionSettings.Settings[].EnabledOptions`. Tatooine: TeamDeathmatch (Blast + Arcade; Online / Skirmish
  children started by StartGameModeOnline / StartGameModeSkirmish), HeroArena (HvV), Mode9 (Mode9, ModeDefend, ModeE,
  ModeF; its own navmesh stream 5c0e8973), Mode6, ModeC, FantasyBattle (GA), PlanetaryMissions (Strike). Mode8 has no
  sub-world anywhere: Kamino_03 plays it in its Mode1 sub-world with AI teams, so Supremacy-map Instant Action needs a
  Supremacy host.
- What each needs from the level: spawns per team + FriendZones (SpawnLocationFinderShapeData) + a combat area; Co-op /
  GA / Strike also capture points and bomb/objective locators, vehicle spawners (E-Web, MarkII), staged per-team
  out-of-bounds volumes (unnamed prefab ports); AI modes (Arcade, Co-op/IA missions) a navmesh through their stream;
  online PvP modes players (offline: auto-player bots).
- CLI: `level create --skirmish-from <map> --modes <ids> [--bots-team1 n --bots-team2 m]` (see LOG 06:40).
- **Offline modes on a Tatooine_01-shell level (all PROVEN):** Team Battle and Onslaught picked
  from Custom Arcade (MP2), Duel played (MP4, 11:25: "DEFEAT THE LIGHT SIDE", heroes and AI fighting), Instant Action Missions Attack picked from the Instant Action menu and played (MP4, 11:14),
  Missions Defend played (MP3f). Not offered: Mode8 (Instant Action proper) -- Mos Eisley is not a Mode8 level; it needs a
  Supremacy map's Mode1 sub-world grafted and its layout (5 posts, 143 spawns, ~2.4 x 2.2 km on Tatooine_02) fitted to the
  new terrain (~1 x 1 km).
- In game (MP3, mod 0925.04, 08:16-08:37): **ModeE (Instant Action) PLAYS** -- 29 players, 23 AI moving, HUD
  "CAPTURE A COMMAND POST" with posts A/B/C, kill feed; its play space is Tatooine's co-op mission layout placed on the
  new ground (spawns snapped to it), not the arena. **Blast and HvV load** (the Online sub-world / the hero select) but
  wait at pre-round "PLAYERS NEEDED" -- the added auto-player manager filled nothing.
- BFII_EXACT: a manager does nothing until its `ApplySettings` event fires; every shipped one is fired by the event that
  streams its mode in (GameModes_Flow SyncedBool.OnTrue, Server). `level create` now adds a self-starting delay (1 s after
  the mode's sub-world streams in) wired to it. **PROVEN (MP3b, 09:28 / 09:34): Blast fills 8 v 8 and its round starts
  ("DEFEAT THE IMPERIALS"), HvV fills 4 v 4; the local player deploys in both.** But the bots never spawn.
- BFII_EXACT: the shipped bot logic (PF_Bot_Logic, PvE_BotLogic) enables an AutoPlayerEntity whose OnActivated
  (/OnSpawned) feed AutoplayerIsGameplayEntity.PlayerEvent. That pair next to the manager, enabled by the same delay, was
  tried (MP3c, 10:00): **not enough -- 8 v 8 join, no bot ever spawns**; removed from `level create` again.
- Known limitation (left, with the user's agreement): fighting bots in offline Blast/HvV. INFERRED lead: they need the
  gameplay-bot logic `Gameplay/GameModes/Mode9/Prefabs/PvE_BotLogic` (in every co-op map's Mode9 and every Supremacy
  map's Mode1_Gameplay, network-registered; started by a StreamIn event; applies AllowRespawn, MaxSpawnsPerUpdate,
  respawn delays ...) grafted into the online mode's sub-world with its registry entries -- MP3's ModeE bots, which have
  it, spawn and fight. Blast and HvV themselves run on the new level; online, players fill them.
- Prior: HvV on a `level create` level loaded with bots and hero spawns (V5, 2026-09-22, Test Range shell).
- The Frosty MCP tools (read, not run -- they write into the live editor project): `frosty_duplicategamemode` wraps
  Nuuby4's DuplicateGameModePlugin (`DupeSubworld`: a SubWorldData and the LayerData it depends on copied to a new path,
  layer references and connection endpoints remapped) behind a clean-project gate, a confirmation, a post-copy
  `validateworld` and a full reset on failure; bundles, network registries and mesh-variation databases are left to a
  bundle manager ("run Complete Level Bundles" next). `level create --modes` does the same graft headlessly and also those
  (sub-world bundles, registries, navmesh streams, spawns on the new ground, the description, bots), so it was extended
  rather than calling the plugin. `frosty_plangameplayport` plans hero/weapon/ability dependency closures; the modes on a
  shipped shell need none (their heroes and kits come in through the modes' own shipped bundles). INFERRED (source read).

## 5. Loading screen, name, map-select art

- BFII_EXACT: `UI/Configuration/WSUILoadingScreenSettings.MPLoadLevelScreenOverrides[]` keyed by level path ->
  WSUILoadingScreenAsset (a full-screen intro + loop video, `MovieTexture2Asset` = one chunk holding a .webm) named by
  string; loaded from the UI superbundle's `loadingscreens_bundle`. Without an override a level gets the default screen
  (Yavin placeholder videos).
- CLI: `level loading-screen <level> --like <shipped level> [--movie <webm> | --image <file> [--seconds s]]`: the host's
  screen, or its own (movie and screen assets cloned into the host's bundles, a new chunk with the video; --image encodes
  a still with a slow push-in to VP8 WebM through ffmpeg).
- Name: the menu entry's LevelName (and the planet name). Map-select art: Custom Arcade shows the planet of the lookup
  row (the host's) and has no per-level tile; `GridImage`/`LoadingScreenImage` are empty on every shipped level. Kyber
  shows the declaration's image.
- **PROVEN (MP2, 10:18):** picked from Custom Arcade, the level loads behind its own loading screen (the heightmap
  art with the push-in) under the game's overlay "Tatooine / Dune Sea Arena" and a tip; the name shows in the Custom
  Arcade panel and the pause menu.
