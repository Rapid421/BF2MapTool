"""boundary_test.py <label> <plan.json>: prove a level's play-area boundary in game.

Run while a level is up under the harness with the probe module and the player deployed (harness.launch(...,
probe=True, deploy=True)). For each inside point: teleport the local soldier there (ground + 1.5 m, from the heightmap
named in the plan), wait, record the server position and whether the soldier still lives, and shoot the game window
(the HUD shows "return to the battlefield" and a countdown when the player is outside). Then the outside point, last:
teleport out, shoot the warning, and poll until the soldier dies (the Skirmish rule: 10 s, then 900000 damage).

plan.json: {"heights": "<r32>", "inside": [{"name": .., "x": .., "z": ..}, ...], "outside": {"name": .., "x": .., "z": ..},
            "settle": 4, "death_wait": 20}
Writes work/<label>_boundary.json and shots/<label>_*.png.
"""
import json
import os
import sys
import time

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, r'I:\SullustArchive\collisionprobe')
import collisionprobe as cp  # noqa: E402
import harness  # noqa: E402
from shot import shot  # noqa: E402

ROOT = os.path.dirname(HERE)


class Heights:
    def __init__(self, path):
        meta = json.load(open(path + '.json'))
        self.n = meta['width']
        self.h = np.fromfile(path, dtype='<f4').reshape(meta['height'], meta['width'])
        self.x0, self.x1, self.z0, self.z1 = meta['worldMinX'], meta['worldMaxX'], meta['worldMinZ'], meta['worldMaxZ']

    def at(self, x, z):
        fx = (x - self.x0) / (self.x1 - self.x0) * (self.n - 1)
        fz = (z - self.z0) / (self.z1 - self.z0) * (self.n - 1)
        i, j = int(np.floor(fx)), int(np.floor(fz))
        i, j = max(0, min(self.n - 2, i)), max(0, min(self.n - 2, j))
        tx, tz = fx - i, fz - j
        h = self.h
        return float((h[j, i] * (1 - tx) + h[j, i + 1] * tx) * (1 - tz) + (h[j + 1, i] * (1 - tx) + h[j + 1, i + 1] * tx) * tz)


def retry(fn, *a, tries=12, wait=5):
    """the game link answers one command per connection and can be busy for seconds (hud 1 is a heap scan): retry"""
    last = None
    for _ in range(tries):
        try:
            return fn(*a)
        except OSError as ex:
            last = ex
            time.sleep(wait)
    raise last


def save(out, label):
    json.dump(out, open(os.path.join(ROOT, 'work', label + '_boundary.json'), 'w'), indent=1)


def local_player(limit=180):
    t0 = time.time()
    while time.time() - t0 < limit:
        try:
            rows, _ = cp.server_players()
            me = next((r for r in rows if r['localId'] == 0), None)
            if me and me['char']:
                return me
        except Exception as ex:  # the link restarts while the level streams
            print('  waiting for the game link:', ex)
        time.sleep(3)
    return None


def tp(player, x, y, z):
    reply = retry(cp.send, 'teleport 0x%X %.3f %.3f %.3f direct' % (player, x, y, z))
    if 'teleport ok' not in reply:
        print('  direct teleport:', reply[:120], '-> frame route')
        reply = cp.teleport(player, (x, y, z), 'frame')
    return reply


def main(label, plan_path):
    plan = json.load(open(plan_path))
    hm = Heights(plan['heights'])
    settle = plan.get('settle', 4)
    out = {'label': label, 'level': cp.send('levelname').strip()[:200], 'inside': [], 'outside': None}
    print(out['level'])
    me = local_player()
    if not me:
        out['error'] = 'the local player never deployed'
        json.dump(out, open(os.path.join(ROOT, 'work', label + '_boundary.json'), 'w'), indent=1)
        print(out['error'])
        return 1
    player = me['player']
    print('local player %s 0x%X at %s' % (me['name'], player, me['pos']))
    shot('%s_deployed' % label)
    for p in plan['inside']:
        g = hm.at(p['x'], p['z'])
        r = tp(player, p['x'], g + 1.5, p['z'])
        time.sleep(settle)
        char, pos = retry(cp.svpos, player)
        rec = {'name': p['name'], 'x': p['x'], 'z': p['z'], 'ground': round(g, 2), 'teleport': r[:80],
               'alive': bool(char), 'pos': pos, 'camera': retry(cp.send, 'camwhere')[:200]}
        rec['shot'] = shot('%s_in_%s' % (label, p['name']))
        time.sleep(max(0, p.get('hold', 0)))
        if p.get('hold'):
            char, pos = retry(cp.svpos, player)
            rec['alive_after_hold'] = bool(char)
            rec['pos_after_hold'] = pos
        out['inside'].append(rec)
        save(out, label)
        print('  in  %-12s ground %.1f -> %s alive %s' % (p['name'], g, pos, bool(char)), flush=True)
        if not char:
            print('  the soldier died INSIDE the area -- stopping')
            break
    if plan.get('views') and out['inside'] and out['inside'][-1]['alive']:
        import views
        out['views'] = views.take(label, plan['views'])
        time.sleep(10)                                  # hud 1 (a heap scan) keeps the link busy for a while
        char, pos = retry(cp.svpos, player)
        out['alive_after_views'] = bool(char)
        save(out, label)
    # out and back: a few seconds outside (the warning and countdown show), then inside again (they clear, no death)
    ob = plan.get('out_and_back')
    if ob and out['inside'] and out.get('alive_after_views', out['inside'][-1]['alive']):
        g = hm.at(ob['x'], ob['z'])
        r1 = tp(player, ob['x'], g + 1.5, ob['z'])
        time.sleep(ob.get('wait', 4))
        rec = {'out': [ob['x'], ob['z']], 'teleport': r1[:80], 'shot_out': shot('%s_outback_out' % label)}
        char, pos = retry(cp.svpos, player)
        rec['alive_out'] = bool(char); rec['pos_out'] = pos
        gb = hm.at(ob['back_x'], ob['back_z'])
        rec['teleport_back'] = tp(player, ob['back_x'], gb + 1.5, ob['back_z'])[:80]
        time.sleep(ob.get('wait_back', 6))
        rec['shot_back'] = shot('%s_outback_back' % label)
        time.sleep(ob.get('stay_back', 8))
        char, pos = retry(cp.svpos, player)
        rec['alive_back_after_s'] = ob.get('wait_back', 6) + ob.get('stay_back', 8)
        rec['alive_back'] = bool(char); rec['pos_back'] = pos
        rec['shot_back_late'] = shot('%s_outback_back_late' % label)
        out['out_and_back'] = rec
        save(out, label)
        print('  out-and-back: alive outside %s, alive %s s after coming back %s' % (rec['alive_out'], rec['alive_back_after_s'], rec['alive_back']), flush=True)
    o = plan.get('outside')
    if o and out['inside'] and out.get('alive_after_views', out['inside'][-1]['alive']) and out.get('out_and_back', {}).get('alive_back', True):
        g = hm.at(o['x'], o['z'])
        r = tp(player, o['x'], g + 1.5, o['z'])
        t_out = time.time()
        rec = {'name': o['name'], 'x': o['x'], 'z': o['z'], 'ground': round(g, 2), 'teleport': r[:80], 'samples': []}
        time.sleep(3)
        rec['shot_warning'] = shot('%s_out_%s_3s' % (label, o['name']))
        died = None
        # the character pointer may outlive the soldier for a moment, so sample the whole window and shoot at fixed
        # times too: 3 s (warning), 8 s (countdown), 14 s (past the 10 s delay)
        while time.time() - t_out < plan.get('death_wait', 20):
            char, pos = retry(cp.svpos, player)
            t = time.time() - t_out
            rec['samples'].append([round(t, 1), bool(char), pos])
            if not char and died is None:
                died = round(t, 1)
                time.sleep(1.5)
                rec['shot_dead'] = shot('%s_out_%s_dead' % (label, o['name']))
            if 8.0 < t and 'shot_8s' not in rec:
                rec['shot_8s'] = shot('%s_out_%s_8s' % (label, o['name']))
            if 14.0 < t and 'shot_14s' not in rec:
                rec['shot_14s'] = shot('%s_out_%s_14s' % (label, o['name']))
            time.sleep(0.5)
        rec['died_after_s'] = died
        out['outside'] = rec
        print('  out %-12s ground %.1f -> died after %s s' % (o['name'], g, died))
    json.dump(out, open(os.path.join(ROOT, 'work', label + '_boundary.json'), 'w'), indent=1)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1], sys.argv[2]))
