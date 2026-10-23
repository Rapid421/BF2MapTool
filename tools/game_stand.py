"""Does a deployed soldier stand on the floor? Deploy in the running game, then read the camera position several
times; a falling player's height drops, a standing one's does not. Every call is time-limited.
python game_stand.py <out.json> [samples 5] [gap seconds 3]"""
import json
import re
import subprocess
import sys
import time

CLI = ['I:/BF2MapTool/cli-wt/FrostyCLI/bin/x64/Release/FrostyCLI.exe', '--game', 'swbf2', '--game-path', 'F:/STAR WARS Battlefront II',
       '--handler-plugins', 'F:/Frosty/Frosty Editor/Plugins', 'game']


def game(args, timeout):
    try:
        r = subprocess.run(CLI + args, capture_output=True, text=True, timeout=timeout, stdin=subprocess.DEVNULL)
        return (r.stdout or '') + (r.stderr or '')
    except subprocess.TimeoutExpired:
        return 'TIMEOUT'


n = int(sys.argv[2]) if len(sys.argv) > 2 else 5
gap = float(sys.argv[3]) if len(sys.argv) > 3 else 3.0
res = {'deploy': [l for l in game(['deploy', '--timeout', '60'], 110).splitlines() if 'deploy' in l.lower()][-2:], 'samples': []}
for i in range(n):
    t = time.time()
    o = game(['where'], 40)
    m = re.search(r'camera pos ([-\d.]+) ([-\d.]+) ([-\d.]+)', o)
    res['samples'].append({'t': time.strftime('%H:%M:%S'), 'pos': [float(x) for x in m.groups()] if m else None})
    time.sleep(max(0.0, gap - (time.time() - t)))
pl = game(['send', 'players'], 40)
m = re.search(r"'RAPID421'[^\n]*\n\s*controlled (0x[0-9A-Fa-f]+)", pl)
res['local_player_controls'] = m.group(1) if m else None
res['bots_with_a_soldier'] = len(re.findall(r"localId 255[^\n]*\n\s*controlled 0x[0-9A-Fa-f]*[1-9A-Fa-f]", pl))
ys = [s['pos'][1] for s in res['samples'] if s['pos']]
res['height_range'] = [min(ys), max(ys)] if ys else None
json.dump(res, open(sys.argv[1], 'w'), indent=1)
print(json.dumps(res, indent=1))
