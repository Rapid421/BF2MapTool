# BF2 Map Tool - quick start

Sidebar (`N`) > **BF2 Map**. The tabs follow the order of the work.

## 1 Map
- **Level name**: 2-24 letters or digits. It becomes the level (`AddedContent/<Name>/Levels/<Name>_01`) and the asset folder
  (`Objects/Ported/<Name>`). A different name = a different level beside your other mods.
- Display name, planet, description, era, and the **game modes** as tick boxes.
- **1 Blender unit = 1 metre.** **BF2 soldier (1.8 m)** (Add > BF2 Map, the pie menu, or "Add BF2 soldier (1.8 m)"
  here) puts a soldier-sized figure at the 3D cursor to judge doorways, steps and corridors by. It is never built into
  the level, whatever you do to it.
- **Prepare imported map** (for scenes that were not made for this tool: FBX / OBJ / glTF rips, other games' levels):
  in one go it scales a scene that is clearly not in metres, marks sky domes, backdrops, duplicates and helpers as
  "Leave out" (also a far-away sky MATERIAL inside one big mesh), guesses the material classes, fits the real-time
  lamps to the budget, wires the alpha of tree / grass card images, picks a **game sky** (the planet named most often
  in the file, object, material and image names; Tatooine when none is) with **Interior** exposure when most of the
  floor is under a roof, turns **Light bake on Build** and Auto spawns on. Everything it did is listed under Check;
  Ctrl+Z undoes it. **Guess roles** does only the role part. File > Import > **BF2 Map (FBX / OBJ / glTF, prepared)**
  imports a file with Blender's own importer and then runs the same preparation.
- **Play radius** (0 = measured from your spawns, else from the built part of the scene) and **Keep geometry within**
  (geometry farther than this outside the play area is left out - a 2 km terrain under a 200 m map).
- **Size of the build** (the arrow): triangles, game pieces against what a level holds (about 2,000), texture memory and
  real-time lamps, in a few seconds and before any build. Measured against three real builds: triangles within 5 %,
  pieces within -4 % to +29 % - an early warning, not an exact count.

## 2 Materials
**Game look** (top of the tab, after a build): the map shows the textures the build really packed for the game -
colour, smoothness, metal, normal map, cut-out mask, glow - on copies of your materials (named "... [game]"); the
viewport switches to Material Preview. Press it again to go back. Your materials are never changed, and a build started
while it is on still reads your own materials. Glass and lava keep their Blender look (the game makes those from its
own templates); a LEGO-style tinted material shows its first tint.

Nothing to export or pack by hand: each material's **Principled BSDF** is read (also inside node groups) and packed
into the game's textures - colour + smoothness, normal + metal, emission, cut-out mask.
- **Class**: Opaque, Emissive, Cut-out, Cut-out two-sided, Lava, Glass. Leave a material on Opaque with **Detect from
  the material** ticked and the tool builds it as emissive when it really emits light and as a cut-out when its wired
  alpha really has holes. **Guess classes** fills the list from the node setups.
- **Bake materials** (When needed / Always / Never): an input that is more than an image or a value - a mix, a colour
  ramp, a Bump node, a Mapping node that tiles the image, a procedural texture, a shader that is not a Principled
  BSDF - is rendered to a texture exactly as Blender shows it. A material that reads object coordinates, vertex
  colours or a second UV map cannot be exact; the Check tab says so.
- **Normal map format**: Automatic works it out from the image; override per material if a map looks pressed in.
- Images of any size are accepted (they are resampled to powers of two).
- Values you set by hand on the Principled BSDF (a colour, a roughness) are always used as they are; images are only
  looked up by name for inputs whose wiring the tool cannot follow (custom game shaders).
- Packed maps as glTF and game rips wire them work: one ORM image through Separate Color (roughness = G, metal = B), a
  gloss map through Invert, a Normal Map node's Strength.
- **Vertex-colour tints** (LEGO rips and others multiply a material by a Color Attribute): on every object whose colour
  attribute is ONE colour, the faces get a copy of the material with that colour baked in (up to **Tints per material**
  copies, then the nearest). Objects whose colours vary keep the plain material (Check says which).
- The game's detail layers (a tiling cloth / metal pattern the shipped surfaces can add) are always off: a material
  looks as Blender shows it.

## 3 Collision
Per object: **Exact** (every render triangle is solid - the default), **Custom mesh** (pick another object, or name it
`<object>_col` / `UCX_<object>`), **Hull** (one convex shape), **None**. Lava is never solid.
**Show what is solid** draws it in the viewport: green = exact, blue = custom mesh, orange = hull, yellow = doors,
red boxes = kill volumes. What shows no colour is not solid in game. Press the refresh arrow after changes.

## 4 Gameplay
Add at the 3D cursor: team spawns, hero spawns, kill volumes (a box), not-walkable boxes, a combat area outline.
"Selected mesh is a door" turns a mesh into a door that slides open (up / down / left / right, distance) when a
soldier is near. With **Auto spawns** on, the tool fills in the spawns you did not place from the walkable floor; off,
soldiers spawn only where you put spawns. After a build `spawn_map.png` in the work folder shows all of them, and **Show the build** (bottom of the tab) draws in
the viewport what the level really holds, read back from the work folder:
- the navmesh: cyan = the main walkable part, orange = parts not connected to it (bots and players there cannot walk
  to the rest), grey = other parts;
- every spawn point as a 1.8 m post in its team colour (blue team 1, red team 2, white neutral) with a tick the way it
  faces, on a ring: green = on the main part, orange = on a cut-off part, magenta = no navmesh under its feet;
- the spawn-finder areas in the team colour; one that reaches navmesh floors at other heights (a finder area with
  Height 0 finds spots on every floor inside its outline) gets yellow posts down / up to them;
- thin white outlines: the play areas.
The lines under the button list what is wrong (spawns on islands, finder areas reaching other floors). Press the
arrow after a new build.

Pie menu: Shift Alt A in the 3D view opens Add BF2 object as a pie (spawns, command post,
soldier, not walkable, invisible wall; at the bottom kill volume, hero spawns, combat area, reflection probe). Change the key in
Preferences > Keymap > Add-ons.

**Supremacy**: tick it in the Map tab and add five command posts lettered A to E (A, B on team 1's side, C in the
middle, D, E on team 2's). The tool builds a second level `<Name>_02` that shares everything with the first, puts
the posts, their capture zones (the post's Capture radius) and 160 spawns on your map and adds it to the Instant
Action Supremacy list. Each post is moved to the nearest safe spot of the walkable floor; the log says how far.
Headquarters are where your team spawns are, or between A+B and D+E when you placed none.

**Invisible wall** (Add > BF2 Map, or the Gameplay tab): a box that is solid but never drawn - keeps players inside,
closes gaps, blocks shots. Any mesh whose role is "Collision mesh" and that no object names as its Custom mesh works
the same way, whatever its shape.

**Instances** are part of the map: collection instances, Geometry Nodes instances and particle / hair instances are
exported where Blender draws them (0.2.0). Gameplay roles (spawns, doors ...) inside an instance are not read - make
those instances real (Object > Apply > Make Instances Real).

## 5 Lighting
**Sky = "Game sky (from a shipped level)"** gives the map the lighting of one of DICE's own levels - its sky picture,
sun (strength, colour, angle), fog, automatic exposure and colour grading, exactly as the game ships them:
- **Game sky**: Tatooine, Mos Eisley, Endor, Hoth, Jakku, Kamino, Kashyyyk, Naboo, Starkiller, Takodana, Yavin 4,
  Sullust - sunny, sunset, foggy, stormy and night versions (23 in all). **Other game sky** takes the asset path of
  any other VisualEnvironmentBlueprint the game ships.
- **Exposure**: Outdoor (the sky's own, EV 12.5 - 15 by day) or **Interior** (EV 8 - 10.5, what DICE uses inside)
  for an enclosed map lit by its lamps and glowing panels - with the outdoor exposure an interior stays dark.
- **Sun direction from the scene**: a Sun lamp in the scene turns the game's sun to shine the same way (its strength
  and colour stay the game's). No Sun lamp: the sky's own sun.
- **Exposure (EV)**, shown as the exposure offset, brightens (+) or darkens (-) the whole picture in EV steps.
- The light bake lights the map with that sky and sun, glowing materials are scaled to the sky's exposure, and the
  level's reflections come from the game sky.
- This is what **Prepare imported map** picks for ripped maps.

**Light bake on Build** (Build tab, on by default): Build also bakes the lighting - bounce light, soft shading, light
probes for the soldiers. Without it a level has only direct sun and lamp light and looks flat. It adds a few minutes.

**Sky = "Blender World (match the scene)"** carries the scene's own lighting over instead: light the map in Blender
and the game gets the same picture (for scenes lit on purpose in Blender).
- The World (colour, HDRI, sky texture, with its rotation and strength) becomes the game's sky and the sky light of the bake.
- The Sun lamp gives the game sun its direction, strength and colour.
- Point, spot and area lamps keep their power and colour (per lamp: Baked, Dynamic or Both); an emissive material
  glows with its Emission colour and Strength and lights the bake.
- The picture's brightness is the scene's: Colour Management > Exposure and the view transform are read; **Light
  level** sets how bright "white" is in game - leave it on Auto unless the whole map comes out too dark or too bright.
- Keep **Light bake on Build** on (or press **Bake lighting**) - without a bake only the sun and the lamps light the map.
- Measured on a test scene, game against a Cycles render of the same view: sunlit and shaded surfaces, the sky, every
  lamp type (live and baked) and glowing surfaces are within 5 % of Blender.
- Reflections: the export renders the scene once from a free point near the middle of the play area; the game uses
  that picture for what shiny and metal surfaces reflect (one picture for the whole map).
- One sun: the game has a single sun. With several Sun lamps the strongest one that shines downwards is used; Check
  names the others.
- A roof or wall whose faces look away from the sun still blocks it, as in Blender: the export gives such faces a
  second side (a few percent more triangles on ripped maps).
- No fog unless you tick **Game fog** - a Blender render has none.
- What the game cannot do:
  - soft sun shadows. Its sun shadows are sharp; a Sun lamp with a large Angle hides shadow edges in Blender that
    will show in game (Check warns above 5 degrees). Set the Angle to about 1 degree to see what the game will show.
  - Filmic's lifted shadows. The game's tone curve is closest to AgX; judge the scene with AgX or Standard.
  - one reflection picture for rooms that look very different from the middle of the map - unless you add a
    **Reflection probe** (Gameplay tab or Add > BF2 Map) in each such room: the sphere is the space it serves (scale
    it to the room), the picture is taken at its centre. Inside a probe's sphere metal and shiny surfaces reflect that
    room; elsewhere the map-wide picture is used.

The generated Sky presets (Overcast, Clear day, Sunset ...) and an `.hdr` file are older, simpler skies: far dimmer
than the game's own (a tenth of the sun, a fixed exposure) - use a game sky instead. The sun follows your Sun lamp,
**Sun** switches it, and the fog settings apply.

**Many lamps** (ripped maps bring hundreds): the game draws a limited number in real time (Dynamic light budget, 64).
**Fit lights to budget** keeps the strongest ones real-time and makes the rest Baked - they then light the map
through **Bake lighting**.

## 6 Check
**Check scene** runs every test without building: errors block the build and say how to fix them; click the arrow of
a line to select what it is about. When one note applies to many materials it is one line ("N of these: ...") and its
arrow selects them all. A missing image is a warning: the material is built without it. Lines with a safe automatic
fix have a **Fix** button: too many real-time lamps (Fit lights to budget), guessed classes (Confirm guessed classes),
command posts lettered wrong (Letter the command posts A to E), a level name the game already uses (Pick a free level
name).

## 7 Build
- **Port map**: builds the level and writes the `.fbmod`. Blender stays usable; the stage list shows progress;
  **Cancel** stops it. Pressing Port map again redoes only what changed, and a cancelled or crashed build carries on
  where it stopped.
- **Bake lighting**: the same build with baked lighting (lightmaps + light probes). Draft / Normal / High. Without a
  bake, surfaces the sun does not reach are black.
- **Rebuild mod**, **Open log**, **Open mod folder**.
- **Make bug report**: a small zip of the logs and settings (no meshes, no textures) for whoever helps you.
- **Clean build caches**: frees the disk space this map's build keeps to skip work next time (game pieces, packages,
  projects, the light bake, navmesh, collision - several GB on a big map). The built mods and the exported scene stay;
  it says how much it freed. The next build then takes as long as the first one.

Typical times on the development PC: a 500-piece map about 15 minutes for a first build (most of it the final
consistency checks), a re-build after a small change 2-10 minutes, a draft bake 2-4 minutes on top.

## 8 Several maps
Every map mod carries its own copy of the game's menu lists, and when several are loaded together the last one wins:
only its map shows. **BF2 Map Menu Maker.exe** (in the bundle, next to FrostyCLI) fixes that for any maps made with
this tool, by anyone:
1. Open it, press **Add map mods...** and pick the map `.fbmod` files (two or more).
2. Check the game folder (found by itself) and where to save, then press **Make menu mod** (about a minute).
3. In Frosty Mod Manager or the Kyber mod list, load the map mods and then the new menu mod **below all of them**
   (last). Every map then shows in Skirmish / Instant Action.

Adding a map later: make the menu mod again with the new list. Only the small menu mod changes, never the maps.
Without a window: `"BF2 Map Menu Maker.exe" --make Maps_Menu.fbmod a.fbmod b.fbmod`.
