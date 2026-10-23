"""mode_probe.py <label> [watch seconds]: what a game mode does on a level that is up under the harness (probe module).

Records the streamed sub-levels, every server player (team, local id, alive, position) twice <watch> s apart -- AI and
auto-player bots count as moving when they covered more than 3 m -- and game-window shots right away, at half time and at
the end. Writes work/<label>_mode.json and shots/<label>_mode_*.png.
"""
import json
import math
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, r'I:\SullustArchive\collisionprobe')
import collisionprobe as cp  # noqa: E402
from shot import shot  # noqa: E402

ROOT = os.path.dirname(HERE)


def players():
    try:
        rows, _ = cp.server_players()
    except Exception as ex:
        return [], str(ex)
    return rows, None


def main(label, watch=90):
    out = {'label': label, 'level': cp.send('levelname').strip()[:200]}
    t0 = time.time()
    # give the round a chance to start and the local player to deploy
    local = None
    while time.time() - t0 < 120:
        rows, err = players()
        local = next((r for r in rows if r['localId'] == 0), None)
        if local and local['char']:
            break
        time.sleep(5)
    out['deployed_after_s'] = round(time.time() - t0, 1) if local and local['char'] else None
    out['sublevels'] = cp.send('sublevels')[:3000]
    shot('%s_mode_start' % label)
    first, _ = players()
    out['first'] = [{k: r[k] for k in ('name', 'localId', 'team', 'char', 'pos')} for r in first]
    time.sleep(watch / 2)
    shot('%s_mode_mid' % label)
    time.sleep(watch / 2)
    last, _ = players()
    out['last'] = [{k: r[k] for k in ('name', 'localId', 'team', 'char', 'pos')} for r in last]
    shot('%s_mode_end' % label)
    moved, still = 0, 0
    before = {r['name']: r for r in first}
    for r in last:
        b = before.get(r['name'])
        if not b or not b['pos'] or not r['pos'] or r['localId'] == 0:
            continue
        d = math.dist(b['pos'], r['pos'])
        if d > 3:
            moved += 1
        else:
            still += 1
    teams = {}
    for r in last:
        teams.setdefault(str(r['team']), 0)
        teams[str(r['team'])] += 1
    out['summary'] = {'players': len(last), 'teams': teams, 'ai_moved': moved, 'ai_still': still,
                      'local_alive': bool(next((r for r in last if r['localId'] == 0 and r['char']), None))}
    json.dump(out, open(os.path.join(ROOT, 'work', label + '_mode.json'), 'w'), indent=1)
    print(json.dumps(out['summary']))
    print('deployed after', out['deployed_after_s'])
    print(out['sublevels'][:800])
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 90))
