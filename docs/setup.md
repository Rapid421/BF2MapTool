# BF2 Map Tool - setup

The tool is a Blender add-on that turns a Blender scene into a new Star Wars Battlefront II (2017) level and writes a
`.fbmod`. Blender only describes the scene; the build itself runs outside Blender, so Blender never freezes.

## What you need

| What | Notes |
|---|---|
| Blender 4.2 or newer (tested: 4.3, 4.5, 5.0, 5.2) | from blender.org. A Microsoft Store Blender cannot be started by other programs: see "Blender for the export" below |
| Star Wars Battlefront II (2017), installed | EA app, Origin, Steam or Epic - found by itself |
| The tool bundle (`BF2MapTool_WIP_<date>.zip`) | holds FrostyCLI, the Frosty plugins, HavokCollisionTool, the collision template and the light baker |
| .NET 8 runtime | for HavokCollisionTool (dotnet.microsoft.com); Check setup says if it is missing |
| About 10 GB free on one drive | for the work folder |
| Frosty Mod Manager | to play the `.fbmod` |

You do **not** need a separate Python: the build runs in the Python that comes with Blender, and the add-on installs
the packages it needs (numpy, Pillow, scipy, xatlas, and imageio-ffmpeg for the loading picture) into a folder of its own with one button.

## Install (five clicks)

1. Unzip the tool bundle somewhere with a short path, e.g. `D:\BF2MapTool`.
2. Blender > Edit > Preferences > Add-ons > **Install from Disk** > `bf2_map_tool-<version>.zip` > tick **BF2 Map Tool**.
   (Blender 4.2+ also takes `bf2_map_tool-<version>-extension.zip` under Get Extensions > Install from Disk.)
3. In the add-on's preferences: **Tool bundle folder** = the unzipped bundle, then **Find everything**. It fills the
   game folder (from the game's install entry), every tool path from the bundle and proposes a work folder on the drive
   with the most free space. Every path it filled is listed with how it was found.
4. **Install Python packages** (about a minute, about 100 MB into `<work folder>/_python`; Blender stays usable).
5. **Check setup**: every line must say ok; a line that does not says what is wrong and what to do.

Then: 3D viewport > `N` > **BF2 Map** tab. Until the paths are set, the tab shows a "First set up the tool" box with the
same Find everything button.

Advanced preferences (leave empty unless you need them):
- **Python**: a python.exe 3.10+ of your own that already has the packages, instead of Blender's.
- **Blender for the export**: another blender.exe for the headless export (needed with a Microsoft Store Blender). It
  must be the same version as this Blender or newer - an older one cannot open the scene (the build says so).
- **Runtime folder**: only for development copies.

The add-on writes your paths to `settings.json` in the work folder. Nothing else on the PC is changed; the game's own
files are never modified - a build only writes into the work folder and produces a `.fbmod`.

## Playing the result

Import the `.fbmod` in Frosty Mod Manager, apply, launch. The level is started with its own name: the Build tab shows
the level path (`AddedContent/<Name>/Levels/<Name>_01/<Name>_01`) and the mode names. Blast and Onslaught run offline
with bots (Arcade); Heroes vs Villains loads without bots. With Supremacy ticked the mod also holds
`<Name>_02`, listed under Instant Action > Supremacy.

## When something goes wrong
Build tab > **Make bug report**: one small zip (logs, settings, the scene's description - no meshes or textures) to
send to whoever helps you. `troubleshooting.md` lists every message and what to do.

## What the tool does not do (yet)

It says so instead of half doing it: the animated lava preset (lava is a still, glowing, not-solid surface), terrain
blend layers, vehicles, a command post's starting owner, sound and effect markers, DX12-only
surfaces (cut-out materials are merged into the level's own DX11 shader database: play in DX11).
