# BF2 Map Tool - changes

## 0.2.0 (2026-10-23)
Made for people other than the author: setup without typing paths, without installing Python.

New
- **Find everything** (preferences, and the sidebar until set up): the game from its EA app / Origin / Steam / Epic
  install entry, every tool from the unzipped bundle (also one folder down), a work folder on the drive with the most
  room. Paths that already work are kept.
- **Install Python packages**: numpy, Pillow, scipy, xatlas, imageio-ffmpeg into `<work folder>/_python/py<version>` for Blender's
  own Python (pip, in the background). No separate Python needed; a Python of your own still works.
- Blender 4.2+ **extension** package (`bf2_map_tool-<v>-extension.zip`, passes `blender --command extension validate`)
  next to the classic add-on zip.
- **Invisible wall** (Add > BF2 Map): solid, never drawn.
- **Instances are exported**: collection instances (also nested), Geometry Nodes instances, particle / hair instances.
  Before, they were silently left out.
- **Fit lights to budget** (Lighting tab): the weakest real-time lamps over the budget become Baked.
- **Make bug report** (Build tab).
- The sidebar shows "First set up the tool" until the paths are set.
- **Prepare imported map** (Map tab) and **File > Import > BF2 Map (FBX / OBJ / glTF, prepared)**: units, roles, a
  far-away sky material inside one big mesh, classes, lamps, sky, auto spawns in one go.
- **Size of the build** (Map tab): triangles, pieces, texture memory, real-time lamps before building.
- **Fix** buttons on Check lines that have a safe automatic fix.
- **Reflection probes** work: each probe takes its own reflection picture for the room inside its sphere.
- **Vertex-colour tints**: per-object Color Attribute tints (LEGO rips) are baked into tinted copies of the material.
- Preferences: **Save settings to a file** / **Load settings from a file**.
- **Game sky** (Lighting tab): take the sky, sun, fog, automatic exposure and colour grading of one of 23 shipped
  levels (Tatooine, Endor, Hoth, Naboo, Kamino ... sunny / sunset / foggy / night), or any shipped environment by its
  asset path. The bake, glows and reflections follow it. **Exposure: Interior** for enclosed maps. Prepare imported map
  picks one by itself (the planet from the names, interior when the floor is mostly under a roof).
  A game sky uses the game-wide base colour grade (a level's own grading LUT cannot be copied - without its data the
  screen went black) and no cloud shadows (the shipped cloud-shadow map belongs to the shipped level's ground; over a
  ported map it took the sun away everywhere).
- **Light bake on Build** (on by default): Build bakes the lighting too; before, it had to be a separate button and
  most builds had no bounce light at all.
- **Loading picture** (Map tab, on by default): the level's own loading screen, rendered from a camera named
  "Menu camera" (else the scene camera) and shown as a slow push-in. No camera, or no ffmpeg: the host's screen.
- Faces without a material are built plain grey (warning) instead of stopping the build.
- **Glowing cut-outs** (class "Cut-out, glowing", or guessed when a material emits AND its wired alpha really has holes): holes
  from the alpha, the rest glows - built as a cut-out plus a glowing layer just in front of it. Seen in game.
- Rough metals are lifted to make up for the game's single-scatter specular (Blender's Principled is multiscatter):
  rough steel now matches Blender in game; very bright metals (silver, gold) cannot be lifted past white and stay a
  little darker when rough.
- Holograms are calibrated to the material's Blender emission strength (they were about 150 times too bright).
- Check notes a DirectX normal map (the game shows it as made; Blender shows it pressed in).

Changed
- A missing image is a **warning**, not an error: the material is built without it (colour map -> grey, other maps ->
  default). One lost texture of a ripped map no longer stops the build.
- Check groups many lines of one kind ("N of these: ...") - a ripped map gave 528 lines before.
- The level name check works on a new PC before the first build (the game's level list ships with the tool).

Fixed
- Ripped maps (Unreal node groups) came out with black splotches: a material was guessed "Cut-out, glowing" because
  its Alpha and Emission were WIRED, and a cut-out read the colour image's alpha (roughness, on those rips) as holes.
  The guess now follows Alpha through groups and mixes and measures the pixels; a glowing layer is kept only when the
  material really glows AND really has holes (else it is built emissive, cut-out or opaque, said in the Check tab).
  Hunters Bastion: 47 false glowing cut-outs -> 7 real ones; seen in game.
- A material whose alpha is a constant under one half (invisible in Blender) is left out instead of stopping the build.
  An alpha baked from a Color Attribute is not taken for one.
- Fine detail keeps its shape: corners were snapped to a 1.6 cm grid everywhere (LEGO studs crumpled; 19 % of the
  CR90 interior's triangles collapsed). Only corners where two objects or materials meet, and corners of normal-sized
  triangles, are snapped now. The T-junction tolerance is per corner (a quarter of its shortest edge, at most 1 cm):
  LEGO Utapau split in 9 min instead of 24, 3.3 M triangles instead of 8.6 M.
- A level with more than about 1,400 map pieces crashed as it came up (seen every time at 1,441 and 1,455; the same
  geometry in 1,145 pieces loads). The budget is 1,200 now; over it the pieces grow (more triangles each, larger far
  pieces, a smaller zone kept exact) and a map that still does not fit stops with what to do, instead of building a
  level that crashes. LEGO Utapau: 1,455 -> 923 pieces, loads.
- A work folder with accents in its path ("Wörk", "é") failed every piece ("a mismatch in the amount of LODs"): the
  importer now reaches it by an ASCII-only link. Asset names are ASCII ('Béton' -> 'Beton'; names with no ASCII
  letters keep a short hash so two of them stay apart).
- Tree / grass cards of OBJ and FBX rips drew as solid squares (the importers leave the image's alpha unwired, so
  Blender shows squares too): **Prepare imported map** now wires a colour image's own alpha to Alpha when at least
  2 % of it is see-through, and lists those materials. LEGO Naboo: 15 materials, trees cut out in game.
- Hand-placed spawns on a floor island the walkable floor does not reach are left out with a warning (auto spawns on,
  every team keeps one) instead of stopping the build.
- Cut-out materials wore a cloth weave: the cut-out surface's detail layer (shipped at double strength for a carpet)
  is now off, as on every surface that has one.
- A material with a plain colour and a normal-map image got the NORMAL MAP as its colour (the image-by-name guess
  overrode the colour set by hand). Values set by hand are never replaced now.
- The play area of a one-mesh rip was measured over its sky dome (665 m instead of 174 m on LEGO Naboo).
- Exporting with a "Blender for the export" older than the scene exported Blender's EMPTY start-up scene; it now stops
  and says why. A missing scene file is an error too.
- Check setup: Python version line; package lines point at the Install button.

Tested (2026-10-23): new-user flow (fresh profile, bundle, Find everything, packages, Check setup) on Blender 5.2, 5.0,
4.5, 4.3; extension validate + install on 5.2 and 4.3; checks suite 46/46 on this PC's Python and on Blender's own
Python 3.11 (4.3) and 3.13 (5.2); instancing scene; scene / image / work folder names with spaces, accents and CJK;
packed and relative images; `tests/run_all.py` 16/16.

## 0.1.0 (2026-10-12)
First version: one-button port and light bake, materials from Principled BSDF, collision modes, doors, kill volumes,
spawns, Supremacy, sky / sun / lamp matching, reflections, checks.
