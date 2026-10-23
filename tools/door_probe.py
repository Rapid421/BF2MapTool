"""Does a tool-built door open? Run while the level is up (game_test.py --then "python door_probe.py <map.json> <settings.json>").

  1 the door prefabs' live wires (`game wires Door`) -> door_wires.txt
  2 what the door's controller reads right now (`game inspect`): its three triggers (enabled? how many players
    inside?), the two state bools, the open / close blend value -> door_probe.json 'before'
  3 a picture, then the controller's own "held open" bool is set by hand (deliver Bool.SetTrue: the same input its
    ForceOpen wire drives), then four pictures 1.5 s apart and the same readings again ('after')
What that tells: if the panel moves in 3, the moving part of the door works and a shut door means nobody the trigger
counts stood in it; if it does not move, the panel / transform wiring is at fault. Nothing here decides anything.
The node names are those of the Kamino host's door logic (PF_AutomaticDoor_Controller).
"""
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'runtime')))
from bf2maptool import config

CTRL = 'Gameplay/GameModes/Shared/Doors/PF_AutomaticDoor_Controller'
READ = ['GeometryTrigger[3]', 'GeometryTrigger[4]', 'GeometryTrigger[5]', 'Bool[1a]', 'Bool[1b]', 'And[14]', 'Or[3d]', 'And[f]', 'FloatInterpolator[2f]',
        'ConditionalFloat[29]']
HOLD_OPEN = ('Bool[1b].SetTrue', 'Bool[1a].SetTrue')

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
out = os.environ.get('BF2_TEST_OUT') or job.path('tmp')
S = job.settings
CLI = [S['frostycli'], '--game', 'swbf2', '--game-path', S['game_path'], '--handler-plugins', S['plugins_path'], 'game']


def game(args, timeout=60):
    try:
        r = subprocess.run(CLI + args, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        return (r.stdout or '') + (r.stderr or '')
    except subprocess.TimeoutExpired:
        return 'TIMEOUT'


def readings():
    d = {}
    for n in READ:
        o = game(['inspect', n, '@' + CTRL], 40)
        keep = [l.strip() for l in o.splitlines() if l.strip() and not l.startswith('[') and 'DirectCompute' not in l and 'oo2core' not in l]
        d[n] = keep[:14]
    return d


res = {'steps': []}
doors = [o for o in job.map['objects'] if o.get('role') == 'door']
if not doors:
    print('no doors in this map'); sys.exit(0)
d = doors[0]
org, ax = d['door']['origin'], d['door']['axes']
res['door'] = d['name']
w = game(['wires', 'Door'], 60)
open(out + '/door_wires.txt', 'w', encoding='utf-8').write(w)
res['wires'] = len([l for l in w.splitlines() if '->' in l])
res['players'] = [l.strip() for l in game(['send', 'players'], 40).splitlines() if l.strip()][-24:]
res['where'] = [l.strip() for l in game(['where'], 40).splitlines() if 'pos' in l.lower()][:4]
game(['trace', 'on', 'Door'], 30)                      # inspect / deliver are carried out inside a feed hook
res['before'] = readings()
n = ax[0] if isinstance(ax, list) and len(ax) == 3 and isinstance(ax[0], list) else [1.0, 0.0, 0.0]   # a panel is thin along its local x in the test scene
at = [org[i] - 9.0 * n[i] for i in range(3)]; at[2] = org[2] + 0.5
ga, gl = job.to_game(at), job.to_game(org)
res['door_game_pos'] = [round(v, 2) for v in gl]


def shot(name):
    png = '%s/%s.png' % (out, name)
    game(['shot', '--at', ','.join('%.2f' % v for v in ga), '--look', ','.join('%.2f' % v for v in gl), '--out', png], 90)
    return os.path.exists(png)


res['shots'] = [{'name': 'door_fire_00', 'ok': shot('door_fire_00'), 't': time.strftime('%H:%M:%S')}]
res['delivered'] = []
for ev in HOLD_OPEN:
    o = game(['deliver', ev, '@' + CTRL, 'all'], 40)
    res['delivered'].append({'event': ev, 'answer': [l.strip() for l in o.splitlines() if l.strip()][-1:]})
for k in range(1, 5):
    time.sleep(1.5)
    res['shots'].append({'name': 'door_fire_%02d' % k, 'ok': shot('door_fire_%02d' % k), 't': time.strftime('%H:%M:%S')})
res['after'] = readings()
res['trace_tail'] = [l for l in game(['trace', '60'], 30).splitlines() if '->' in l and 'Door' in l][-30:]
game(['trace', 'off'], 30)
json.dump(res, open(out + '/door_probe.json', 'w'), indent=1)
print(json.dumps(res, indent=1)[:6000])
