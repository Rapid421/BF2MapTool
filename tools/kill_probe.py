"""Does a kill volume kill? Run while the level is up (game_test.py --then "python kill_probe.py <map.json> <settings.json>").

The test scene puts the player's spawns on a hatch over a pit that a kill volume fills. Three times over: deploy the
player, then every second for 14 s read his camera position (`game where`) and whether his player still controls a
soldier (`game send players`: the entry whose localId is not 255, `controlled` 0x0 = no soldier).
Verdict per round: he had a soldier, his camera went down by more than 1 m (he fell through the open hatch), and then
he had no soldier while the camera was under the hatch = the kill volume took him. Nothing here decides the build;
the numbers are written to kill_probe.json in the review folder.
"""
import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'runtime')))
from bf2maptool import config

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
out = os.environ.get('BF2_TEST_OUT') or job.path('tmp')
S = job.settings
CLI = [S['frostycli'], '--game', 'swbf2', '--game-path', S['game_path'], '--handler-plugins', S['plugins_path'], 'game']


def game(args, timeout=40):
    try:
        r = subprocess.run(CLI + args, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        return (r.stdout or '') + (r.stderr or '')
    except subprocess.TimeoutExpired:
        return 'TIMEOUT'


def sample():
    w = game(['where'])
    m = re.search(r'camera pos ([-\d.]+) ([-\d.]+) ([-\d.]+)', w)
    cam = job.from_game([float(x) for x in m.groups()]) if m else None
    p = game(['send', 'players'])
    me = re.search(r"localId (?!255)\d+[^\n]*\n\s*controlled 0x([0-9A-Fa-f]+)", p)
    return {'t': round(time.time() - T0, 1), 'camera_scene': [round(float(v), 2) for v in cam] if cam is not None else None,
            'has_soldier': (int(me.group(1), 16) != 0) if me else None}


kills = job.map.get('kill_volumes') or []
res = {'kill_volumes': [k['name'] for k in kills], 'rounds': []}
T0 = time.time()
for rnd in range(3):
    d = game(['deploy', '--timeout', '25'], 60)
    r = {'deploy': (d.strip().splitlines() or ['?'])[-1][:160], 'samples': []}
    for _ in range(14):
        t = time.time()
        r['samples'].append(sample())
        time.sleep(max(0.0, 1.0 - (time.time() - t)))
    zs = [s['camera_scene'][2] for s in r['samples'] if s['camera_scene'] and s['has_soldier']]
    alive = [s['has_soldier'] for s in r['samples']]
    r['had_soldier'] = True in alive
    r['camera_z_with_soldier'] = [min(zs), max(zs)] if zs else None
    r['fell_m'] = round(max(zs) - min(zs), 2) if zs else None
    after = alive[alive.index(True):] if True in alive else []
    r['lost_soldier_after_having_one'] = False in after
    r['killed_in_pit'] = bool(r['had_soldier'] and r['lost_soldier_after_having_one'] and (r['fell_m'] or 0) > 1.0)
    res['rounds'].append(r)
    print('round %d: had soldier %s, camera z with a soldier %s, fell %s m, lost the soldier %s -> killed in the pit: %s' % (
        rnd + 1, r['had_soldier'], r['camera_z_with_soldier'], r['fell_m'], r['lost_soldier_after_having_one'], r['killed_in_pit']), flush=True)
res['verdict'] = sum(1 for r in res['rounds'] if r['killed_in_pit'])
with open(out + '/kill_probe.json', 'w', encoding='utf-8') as f:
    json.dump(res, f, indent=1)
print('killed in the pit in %d of %d rounds' % (res['verdict'], len(res['rounds'])))
