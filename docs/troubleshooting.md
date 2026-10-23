# BF2 Map Tool - troubleshooting

Every build keeps its log: Build tab > **Open log** (`<work folder>/<Name>/logs/build.log`; each stage also has its
own log beside it). A stage that stops says what it found and names the log.

| What you see | Cause | What to do |
|---|---|---|
| **Check setup** line not `ok` | a path in the preferences is wrong or a tool is missing | the line names the path and what is expected there |
| Build "blocked ... check(s) failed" | the Check tab found an error | open Check, fix the listed objects / materials, build again |
| "The two teams' spawns are only N m apart" | placed spawns of both teams closer than 40 m | move them apart, or delete them and use Auto spawns |
| "spawn(s) are inside or pressed against something solid" | a spawn point is inside a closed object or closer than 0.25 m to a wall | move it onto open floor; the point is where the feet go |
| "spawn(s) have no solid floor within 3 m under them" | the spawn floats, or its floor has Collision None / a lava material | put it on a solid floor |
| "Kill volume ... holds N m2 of floor at play height" | the kill box reaches across a walkway | lower or shrink it so it lies under the walkways |
| "light(s) sit inside or within 0.5 m of a surface" (warning) | a lamp inside its fixture burns a hot spot or is swallowed | move it 0.5 m off; make the fixture glow with an emissive material |
| "Door ... slides only N m" | the door's Distance is shorter than half the panel | set Distance to 0 (automatic) |
| "closed object(s) have their faces pointing inwards" (warning) | flipped normals: the object is seen from inside only | Mesh > Normals > Recalculate Outside (leave it if it is a room) |
| "The level name ... is taken by a level the game ships" | e.g. Kamino, Hoth | choose another name |
| "No game mode is ticked" / "Supremacy needs 5 command posts" / "must be lettered A, B, C, D, E" | Map tab / Gameplay tab | tick a mode; add or re-letter the posts |
| "material(s) have a missing image" (warning) | an image is neither packed nor on disk; the material is built without it (grey colour / flat normal) | File > External Data > Find Missing Files, or pack it, then build again |
| "N lights are real-time; the budget is 64" | ripped maps arrive with every lamp real-time | Lighting tab > **Fit lights to budget** (the weakest lamps become Baked), then **Bake lighting** |
| "Blender could not open the scene ... saved by a newer Blender" | "Blender for the export" in the preferences is older than the Blender the scene was saved with | leave that preference empty (this Blender is used) |
| Check setup: "Python: numpy / PIL / scipy / xatlas not installed" | the build's Python lacks its packages | preferences > **Install Python packages** (into `<work folder>/_python`; Blender's own Python is enough) |
| Check setup: "Work folder ... has only N GB free" | the drive is full | pick a work folder on another drive (Find everything proposes the emptiest one) |
| Check shows "N of these: ... (and N-1 more like it)" | the same note for many materials, grouped into one line | press the arrow to select them all |
| Parts of a kitbashed scene are missing in game | (before 0.2.0) collection / Geometry Nodes / particle instances were left out | build with 0.2.0; instances of a "Leave out" object stay out (Check lists them) |
| A material looks different from Blender | its nodes were not baked (Bake materials = Never), or it reads object coordinates / vertex colours / a second UV map | set Bake materials to "When needed"; drive the material from its UV map |
| A normal map looks pressed in | the image's green direction was read the wrong way | Materials > Normal map format > OpenGL or DirectX |
| A surface has no relief although it has a normal map | - | should not happen: the tool builds such materials on a surface that repeats the normal map; report the material |
| Everything the sun does not reach is black | no baked lighting | Build > Bake lighting |
| The map is pink / orange / grey although the scene is not, or has a hard sun the scene does not have | Sky is set to a preset while the scene is lit by its World (Check says "The scene has its own World ...") | Lighting tab > Sky > "Blender World (match the scene)" |
| Plain-coloured surfaces shimmer teal / purple by viewing angle, mouldings glow white | a map built with a tool version before 2026-10-12 (its one-colour textures were 4 px; the game misreads those) | build the map again with this version |
| Thin bright lines run across a wall | two meshes of the scene lie on top of each other or cross (Blender shades both alike, the game bakes each its own light) | delete or move the hidden one |
| Glowing materials are black or do not glow | Emission Strength is 0, or the Emission colour is black | set both on the Principled BSDF; very high strengths are capped at the game's maximum |
| A shadow edge crosses the floor that Blender does not show | the Sun lamp's Angle is large: Blender blurs the shadow away, the game's sun shadows are sharp | set the Angle to about 1 degree in Blender and relight until it looks right there |
| A place is lit in Blender but dark in game | it is lit by a second Sun lamp (the game has one sun - see Check) | light it with point, spot or area lamps |
| Metal reflects the wrong room | one reflection picture serves the whole map, taken near the middle of the play area | known limit |
| Soldiers fall through an object | its Collision is None, or it is Lava | Collision tab |
| Bots do not enter an area | no walkable floor connects it (too steep, too narrow, a gap), or a not-walkable box | look at `spawn_map.png`; widen / bridge the path |
| A door stays shut | nobody the door counts stands within about 5 m of it | walk up to it; check the door axis and distance in the Gameplay tab |
| "import: N piece(s) not imported ... round k, encoding k" in the log | the game's importer refuses some piece files depending on their bytes | nothing: the piece is written again in another lossless encoding and imported; only a piece that fails every round stops the build |
| "FrostyCLI run ... stopped on a transient failure - run 2 of 4" | a random failure inside the importer | nothing: the step is run again in a fresh process |
| Bake: "Only N GB of memory is free" | the baker dies silently when memory runs out | close the game or other programs, bake again |
| The game shows "Your latest sync was interrupted" (EA app) | the EA app's cloud check failed | choose Launch Game if your local saves are intact; not caused by the mod |
| The level loads to a black screen / endless loading | a mod built for another game version, or two mods that add the same level name | apply only this mod; rebuild after a game update |
| Cut-out materials are invisible under DX12 | merged surfaces only exist in the level's DX11 shader database | play in DX11 |

## Asking for help
Build tab > **Make bug report** writes `bug_report_<date>.zip` into the work folder: the logs, the scene export's
description (no meshes, no textures, no .blend), your settings and the last results. Send that file.

## Starting over
Delete `<work folder>/<Name>` to rebuild a map from nothing. Deleting only `<work folder>/<Name>/build.json` makes
the next build redo every stage but keeps the level shell.

## Known limits of this version
The animated lava preset (lava is a still glowing surface), terrain blend layers, vehicles, sound and effect markers
and a command post's starting owner are not built in. Reflection probes exist since 0.2.0. Glass is the host
level's window glass (no tint from the material). Invisible walls exist since 0.2.0 (Add > BF2 Map > Invisible wall). Light strengths (watts -> lumens, emission -> nits) and the
sky presets use first-guess factors. Heroes vs Villains loads but was never played. Supremacy uses the Felucia
donor: its intro cinematic timing, its vehicle set pieces (far from your map) and its neutral spawn areas stay.
Flipped faces on open meshes and a level name used by another installed mod are not detected before the build.
