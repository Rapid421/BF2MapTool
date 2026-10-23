"""Unit tests of addon/bf2_map_tool/detect.py (no Blender needed): python test_detect.py [scratch dir]
Fake bundles / game folders are made in the scratch dir; the real registry of this PC is read once (the game is installed here)."""
import json
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', '..', 'addon', 'bf2_map_tool'))
import detect  # noqa: E402

scratch = sys.argv[1] if len(sys.argv) > 1 else tempfile.mkdtemp()
if os.path.isdir(scratch):
    shutil.rmtree(scratch)
os.makedirs(scratch)
results = []


def case(name, cond, detail=''):
    results.append((name, bool(cond), detail))
    print('%-4s %-44s %s' % ('PASS' if cond else 'FAIL', name, detail))


def touch(*parts):
    p = os.path.join(scratch, *parts)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    open(p, 'wb').close()
    return p


# a bundle laid out as tools/make_bundle.py writes it, one folder down (as an unzip of BF2MapTool_WIP_x.zip gives)
b = os.path.join(scratch, 'BF2MapTool_WIP_test', 'BF2MapTool_WIP_test')
for rel in ('FrostyCLI/FrostyCLI.exe', 'FrostyPlugins/MeshSetPlugin.dll', 'HavokCollisionTool/HavokCollisionTool.dll',
            'collision_template/template.res', 'collision_template/template.resmeta', 'fluxbake/fluxbake_src.exe'):
    touch('BF2MapTool_WIP_test', 'BF2MapTool_WIP_test', *rel.split('/'))
roots = detect.bundle_roots([os.path.join(scratch, 'BF2MapTool_WIP_test')])
t = detect.find_tools(roots)
case('bundle: nested folder found', any(os.path.normcase(r) == os.path.normcase(b) for r in roots), '%d roots' % len(roots))
case('bundle: all five tool paths', set(t) == set(detect.BUNDLE), sorted(t))
case('bundle: paths point into it', all(os.path.normcase(v).startswith(os.path.normcase(b.replace('\\', '/'))) for v in t.values()))

# template without its .resmeta is not usable
touch('half', 'collision_template', 'template.res')
case('template needs .resmeta', 'havok_template' not in detect.find_tools([os.path.join(scratch, 'half')]))

# plugins folder must hold MeshSetPlugin.dll
os.makedirs(os.path.join(scratch, 'noplug', 'FrostyPlugins'), exist_ok=True)
case('plugins need MeshSetPlugin.dll', 'plugins_path' not in detect.find_tools([os.path.join(scratch, 'noplug')]))

# a game folder is one with the exe
g = os.path.join(scratch, 'Game')
touch('Game', detect.GAME_EXE)
case('game folder with exe ok', detect._ok('game_path', g))
case('game folder without exe refused', not detect._ok('game_path', os.path.join(scratch, 'noplug')))

# Steam's library list
vdf = '"libraryfolders"\n{\n "0"\n {\n  "path"\t\t"C:\\\\Program Files (x86)\\\\Steam"\n }\n "1"\n {\n  "path"\t\t"E:\\\\SteamLibrary"\n }\n}\n'
case('steam libraryfolders.vdf parsed', detect.vdf_paths(vdf) == ['C:\\Program Files (x86)\\Steam', 'E:\\SteamLibrary'], detect.vdf_paths(vdf))

# the Windows Store python stub is refused without being started; a real one is accepted
case('WindowsApps python stub refused', detect._python_works('C:/Users/x/AppData/Local/Microsoft/WindowsApps/python.exe') == (False, ''))
case('missing python refused', detect._python_works(os.path.join(scratch, 'nope', 'python.exe'))[0] is False)
ok, v = detect._python_works(sys.executable)
case('this python accepted', ok and v.startswith('3.'), v)
fp = detect.find_python()
case('find_python finds a working one', bool(fp) and detect._python_works(fp)[0], fp)

# find_all keeps what works and fills what does not
cur = {'game_path': g, 'frostycli': '', 'plugins_path': 'Z:/nowhere', 'havok_tool': t['havok_tool'], 'havok_template': '', 'fluxbake': ''}
fa = detect.find_all(cur, [os.path.join(scratch, 'BF2MapTool_WIP_test')])
case('find_all keeps working values', 'game_path' not in fa and 'havok_tool' not in fa, sorted(fa))
case('find_all fills empty + broken', {'frostycli', 'plugins_path', 'havok_template', 'fluxbake'} <= set(fa), sorted(fa))
case('find_all says how', all(how for _, how in fa.values()))

# nothing to find -> nothing filled (no guess)
empty = os.path.join(scratch, 'empty'); os.makedirs(empty)
fa2 = detect.find_tools([empty])
case('empty folder fills nothing', fa2 == {}, fa2)

# the real machine: the game is found from its install entry
real = detect.find_game()
case('this PC: game found', detect._ok('game_path', real), real)

n_ok = sum(1 for r in results if r[1])
json.dump({'pass': n_ok, 'total': len(results), 'cases': results}, open(os.path.join(scratch, 'result.json'), 'w'), indent=1)
print('%d of %d pass' % (n_ok, len(results)))
sys.exit(0 if n_ok == len(results) else 1)
