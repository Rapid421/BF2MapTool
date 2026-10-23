"""Smoke test of a WIP bundle from an EXTRACTED copy, as a second PC would use it (same game, same Python):
  1 unzip into a new folder  2 the extracted FrostyCLI lists the game's levels with the extracted plugins (this builds
  its game index from nothing)  3 the add-on's own setup check with every path pointing into the extracted folder
python test_bundle.py <bundle.zip> <settings.json> <scratch dir> <out.json>"""
import json
import os
import shutil
import subprocess
import sys
import time
import zipfile

bundle, settings, scratch, out = sys.argv[1:5]
S = json.load(open(settings, encoding='utf-8'))
if os.path.isdir(scratch):
    shutil.rmtree(scratch)
os.makedirs(scratch)
res = {'bundle': bundle, 'bytes': os.path.getsize(bundle)}
t = time.time()
with zipfile.ZipFile(bundle) as z:
    z.extractall(scratch)
root = os.path.join(scratch, os.listdir(scratch)[0]).replace(os.sep, '/')
res['unzip_s'] = round(time.time() - t, 1)
addon_zip = [f for f in os.listdir(root) if f.startswith('bf2_map_tool-') and '-extension' not in f][0]
with zipfile.ZipFile(os.path.join(root, addon_zip)) as z:
    z.extractall(os.path.join(scratch, 'addon'))
env = dict(os.environ, TMP=scratch, TEMP=scratch)
cli = root + '/FrostyCLI/FrostyCLI.exe'
t = time.time()
try:
    r = subprocess.run([cli, '--game', 'swbf2', '--game-path', S['game_path'], '--handler-plugins', root + '/FrostyPlugins', 'ls', '--type', 'LevelData', '--count'],
                       capture_output=True, text=True, timeout=1500, stdin=subprocess.DEVNULL, env=env)
    lines = [l for l in (r.stdout + r.stderr).splitlines() if l.strip()]
    res['cli'] = {'rc': r.returncode, 'seconds': round(time.time() - t, 1), 'last_lines': lines[-3:], 'cache_built': os.path.isdir(root + '/FrostyCLI/Caches')}
except subprocess.TimeoutExpired:
    res['cli'] = {'rc': 'TIMEOUT 1500 s'}
S2 = dict(S, frostycli=cli, plugins_path=root + '/FrostyPlugins', havok_tool=root + '/HavokCollisionTool/HavokCollisionTool.dll',
          havok_template=root + '/collision_template/template.res', fluxbake=root + '/fluxbake/fluxbake_src.exe', work_dir=scratch + '/work')
os.makedirs(S2['work_dir'], exist_ok=True)
sp = os.path.join(scratch, 'settings.json')
json.dump(S2, open(sp, 'w', encoding='utf-8'), indent=1)
r = subprocess.run([sys.executable, '-m', 'bf2maptool', 'check-setup', '--settings', sp], cwd=os.path.join(scratch, 'addon', 'bf2_map_tool', 'runtime'),
                   capture_output=True, text=True, timeout=600, stdin=subprocess.DEVNULL, env=env)
res['check_setup'] = {'rc': r.returncode, 'lines': [l[:200] for l in (r.stdout + r.stderr).splitlines() if l.strip()][-24:]}
json.dump(res, open(out, 'w', encoding='utf-8'), indent=1)
print(json.dumps(res, indent=1))
