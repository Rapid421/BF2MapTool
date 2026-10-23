"""Do the bots move? Run by game_test.py --then while the level is up: lists the soldier entities with their positions
(gamelink `entities <filter>`), waits, lists them again, and reports how far each one went.
Writes <BF2_TEST_OUT>/bot_moves.json.   python bot_move_probe.py [--filter Soldier] [--gap 25] [--rounds 3]"""
import json
import math
import os
import re
import subprocess
import sys
import time

CLI = ['I:/BF2MapTool/cli-wt/FrostyCLI/bin/x64/Release/FrostyCLI.exe', '--game', 'swbf2', '--game-path', 'F:/STAR WARS Battlefront II',
       '--handler-plugins', 'F:/Frosty/Frosty Editor/Plugins', 'game', 'send']
ROW = re.compile(r'^(0x[0-9A-Fa-f]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (\S+) (.+)$')


def arg(name, default):
    return type(default)(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default


def entities(filt):
    r = subprocess.run(CLI + ['entities ' + filt], capture_output=True, text=True, timeout=60, stdin=subprocess.DEVNULL,
                       creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    rows = {}
    for line in r.stdout.splitlines():
        m = ROW.match(line.strip())
        if m:
            rows[m.group(1)] = {'pos': [float(m.group(i)) for i in (2, 3, 4)], 'name': m.group(6).strip()}
    return rows, r.stdout[-400:]


def main():
    out = os.environ.get('BF2_TEST_OUT') or '.'
    filt, gap, rounds = arg('--filter', 'Soldier'), arg('--gap', 25.0), arg('--rounds', 3)
    snaps, tail = [], ''
    for k in range(rounds):
        rows, tail = entities(filt)
        snaps.append({'t': time.strftime('%H:%M:%S'), 'rows': rows})
        if k + 1 < rounds:
            time.sleep(gap)
    first, last = snaps[0]['rows'], snaps[-1]['rows']
    moves = {e: round(math.dist(first[e]['pos'], last[e]['pos']), 2) for e in first if e in last}
    names = sorted({v['name'] for v in first.values()})
    res = {'filter': filt, 'gap_s': gap, 'rounds': rounds, 'soldiers_seen': [len(s['rows']) for s in snaps], 'names': names[:20],
           'moved_over_2m': sum(1 for d in moves.values() if d > 2.0), 'still_under_0_5m': sum(1 for d in moves.values() if d < 0.5),
           'moves_m': sorted(moves.values()), 'last_reply_tail': tail}
    json.dump({'summary': res, 'snapshots': snaps}, open(os.path.join(out, 'bot_moves.json'), 'w'), indent=1)
    print(json.dumps(res))


if __name__ == '__main__':
    main()
