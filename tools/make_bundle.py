"""A work-in-progress bundle for a second PC: the add-on zip plus the tools its preferences point at, taken from the
paths in this PC's settings, and a README that says what the other person must install himself.
    python make_bundle.py <settings.json> [out dir, default <root>/dist]   ->  <out>/BF2MapTool_WIP_<date>.zip

What goes in:  the add-on zip (add-on + runtime + docs), FrostyCLI (its Release folder WITHOUT Caches - the game index
is rebuilt on first use), HavokCollisionTool, the collision template, the light baker, the Frosty plugins folder the
CLI loads, the test arena scene. What does not: the game, Blender, Python, the .NET runtimes, Frosty Mod Manager.
"""
import json
import os
import subprocess
import sys
import time
import zipfile

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
S = json.load(open(sys.argv[1], encoding='utf-8'))
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, 'dist')
os.makedirs(out, exist_ok=True)
r = subprocess.run([sys.executable, os.path.join(ROOT, 'tools', 'make_zip.py'), out], capture_output=True, text=True, stdin=subprocess.DEVNULL)
print(r.stdout.strip())
addon = sorted((f for f in os.listdir(out) if f.startswith('bf2_map_tool-') and f.endswith('.zip') and '-extension' not in f),
               key=lambda f: os.path.getmtime(os.path.join(out, f)))[-1]
name = 'BF2MapTool_WIP_%s' % time.strftime('%Y-%m-%d')
path = os.path.join(out, name + '.zip')

README = '''BF2 Map Tool 0.2.0 - bundle (%(date)s)
==================================================

A Blender add-on that turns a Blender scene into a new Star Wars Battlefront II (2017) level and writes a .fbmod.
THIS IS A WORK-IN-PROGRESS BUILD. It has only ever run on ONE PC (the author's). Expect rough edges; nothing in it
changes your game files - it only writes into a work folder you choose and produces a .fbmod.

What is in this bundle
----------------------
  %(addon)s      the add-on (install this in Blender)
  FrostyCLI\\                     the command-line builder the add-on drives (its game index is built on first use:
                                 the first Check setup / build takes a few minutes longer)
  FrostyPlugins\\                 the Frosty plugins folder FrostyCLI loads
  HavokCollisionTool\\            collision converter (needs the .NET 8 runtime)
  collision_template\\            template.res + template.resmeta
  fluxbake\\                      the light baker (only for "Bake lighting")
  scenes\\arena.blend             a small test scene made for the tool - try this first
  docs\\                          setup.md, quickstart.md, troubleshooting.md
  BF2 Map Menu Maker.exe          combine map mods: makes the small menu mod to load BELOW them, so every map shows
                                 in the menu (offline and on Kyber) - see docs\\quickstart.md, "8 Several maps"

What you must have yourself
---------------------------
  1. Star Wars Battlefront II (2017), installed and up to date (EA app, Origin, Steam or Epic - found by itself).
  2. Blender 4.2 or newer from blender.org (tested 4.3, 4.5, 5.0, 5.2). No separate Python is needed.
  3. .NET 8 runtime (for HavokCollisionTool) and .NET Framework 4.8 (Windows 10 / 11 have it).
  4. Frosty Mod Manager, to apply and play the .fbmod.
  5. About 10 GB free on one drive for the work folder, and an internet connection once (Python packages).

Steps (five clicks)
-------------------
  1. Unzip this bundle somewhere with a short path, e.g. D:\\BF2MapTool.
  2. Blender > Edit > Preferences > Add-ons > Install from Disk > %(addon)s > tick "BF2 Map Tool".
  3. In the add-on's preferences: "Tool bundle folder" = this unzipped folder, then press "Find everything".
     It fills the game folder and every tool path by itself and proposes a work folder.
  4. Press "Install Python packages" (about a minute; numpy, Pillow, scipy, xatlas go into <work folder>\\_python).
  5. Press "Check setup". Every line must say ok; a line that does not says what is wrong and what to do.
  Then: open scenes\\arena.blend, press N in the 3D view, tab "BF2 Map" > Build > "Port map" (about 20 minutes the
  first time: FrostyCLI builds its game index once). The .fbmod is written to <work folder>\\ToolArena\\mods\\.
  Import it in Frosty Mod Manager, apply, launch; the level is AddedContent/ToolArena/Levels/ToolArena_01/ToolArena_01
  (Arcade / Blast). docs\\quickstart.md explains the tabs for your own scenes; for a ripped map use
  File > Import > "BF2 Map (FBX / OBJ / glTF, prepared)" or the Map tab's "Prepare imported map".
  Something wrong? Build tab > "Make bug report" and send the zip it writes.

Known limits of this build (read before reporting a bug)
--------------------------------------------------------
  * Tested on the author's PC only (fresh Blender profiles, fresh work folders, Blender 4.3 to 5.2). If Check setup
    fails on a line, that line's message is the thing to send back.
  * Lava is a still glowing surface (no flow). Glass is the host level's window glass (no tint from the material).
  * Light strengths and sky presets are first guesses. Heroes vs Villains loads but was never played.
  * Supremacy works offline (tick Supremacy, add five command posts A..E); the menu entry has not been checked.
  * Cut-out materials need DX11.
  * A thin bright seam line can show on large lightmapped ground after a bake.
'''


def add_tree(z, src, arc, skip=()):
    n = 0
    for d, dirs, files in os.walk(src):
        dirs[:] = [x for x in dirs if x not in skip and x != '__pycache__']
        for f in files:
            full = os.path.join(d, f)
            z.write(full, '%s/%s' % (arc, os.path.relpath(full, src).replace('\\', '/')))
            n += 1
    return n


counts = {}
with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    z.writestr(name + '/README.txt', README % {'date': time.strftime('%Y-%m-%d %H:%M'), 'addon': addon})
    z.write(os.path.join(out, addon), '%s/%s' % (name, addon))
    counts['FrostyCLI'] = add_tree(z, os.path.dirname(S['frostycli']), name + '/FrostyCLI', skip=('Caches',))
    counts['FrostyPlugins'] = add_tree(z, S['plugins_path'], name + '/FrostyPlugins')
    counts['HavokCollisionTool'] = add_tree(z, os.path.dirname(S['havok_tool']), name + '/HavokCollisionTool')
    tpl = S['havok_template']
    for f in (tpl, os.path.splitext(tpl)[0] + '.resmeta'):
        z.write(f, '%s/collision_template/%s' % (name, os.path.basename(f)))
    z.write(S['fluxbake'], '%s/fluxbake/%s' % (name, os.path.basename(S['fluxbake'])))
    counts['docs'] = add_tree(z, os.path.join(ROOT, 'docs'), name + '/docs')
    # the Menu Maker app (PyInstaller --onefile of tools/menu_maker/menu_maker.py): at the top, next to FrostyCLI\ and
    # FrostyPlugins\, where it looks for them
    mm = next((f for f in (S.get('menu_maker_exe') or '', os.path.join(out, 'BF2 Map Menu Maker.exe')) if f and os.path.isfile(f)), None)
    if not mm:
        sys.exit('BF2 Map Menu Maker.exe not found: build it (tools/menu_maker) and put it in %s' % out)
    z.write(mm, '%s/BF2 Map Menu Maker.exe' % name)
    for f in os.listdir(out):                         # the Blender 4.2+ extension form of the add-on too
        if f.startswith('bf2_map_tool-') and f.endswith('-extension.zip') and f.replace('-extension', '') == addon:
            z.write(os.path.join(out, f), '%s/%s' % (name, f))
    arena = os.path.join(ROOT, 'tests', 'scenes', 'arena.blend')
    if os.path.exists(arena):
        z.write(arena, name + '/scenes/arena.blend')
print('%s: %.1f MB  %s' % (path, os.path.getsize(path) / 1e6, counts))
