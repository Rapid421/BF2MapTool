"""Drive an already running, link-injected game (one I started): deploy, where, one shot per viewpoint - every call
with its own limit, and an overall limit. python game_shots.py <shots.json> <out dir> [--no-deploy] [--limit 420]"""
import json
import os
import subprocess
import sys
import time

CLI = ['I:/BF2MapTool/cli-wt/FrostyCLI/bin/x64/Release/FrostyCLI.exe', '--game', 'swbf2', '--game-path', 'F:/STAR WARS Battlefront II',
       '--handler-plugins', 'F:/Frosty/Frosty Editor/Plugins', 'game']
NOISE = ('Loading', 'Initial', 'PluginRegistry', 'HandlerPlugins', 'SDK init', 'Reflection', 'oo2core', 'LanguageFormat', 'localization')


def game(args, timeout):
    try:
        r = subprocess.run(CLI + args, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        out = [l for l in ((r.stdout or '') + (r.stderr or '')).splitlines() if l.strip() and not any(n in l for n in NOISE)]
        return r.returncode, out
    except subprocess.TimeoutExpired:
        return 124, ['TIMEOUT after %d s' % timeout]


shots = json.load(open(sys.argv[1]))
out = sys.argv[2]
limit = int(sys.argv[sys.argv.index('--limit') + 1]) if '--limit' in sys.argv else 420
os.makedirs(out, exist_ok=True)
t0 = time.time()
res = {'started': time.strftime('%H:%M:%S')}
if '--no-deploy' not in sys.argv:
    rc, o = game(['deploy', '--timeout', '60'], 110)
    res['deploy'] = {'rc': rc, 'out': o[-4:]}
rc, o = game(['where'], 40)
res['where'] = o[-3:]
res['shots'] = []
for s in shots:
    if time.time() - t0 > limit:
        res['cut_short'] = True
        break
    png = '%s/%s.png' % (out, s['name'])
    if os.path.exists(png):
        os.remove(png)
    rc, o = game(['shot', '--at', ','.join('%.2f' % v for v in s['at']), '--look', ','.join('%.2f' % v for v in s['look']), '--out', png], 90)
    res['shots'].append({'name': s['name'], 'ok': os.path.exists(png), 'rc': rc, 'out': o[-2:]})
rc, o = game(['crash'], 40)
res['crash_report'] = o[-6:]
res['seconds'] = round(time.time() - t0, 1)
json.dump(res, open(out + '/shots_result.json', 'w'), indent=1)
print(json.dumps(res, indent=1))
