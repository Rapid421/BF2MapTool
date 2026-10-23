"""The build preview reads a real work folder back: navmesh parts, spawns on / off the main part, finder areas that
reach other floors.  python test_buildview.py [<work>/<Map>]  (default: LEGO Utapau of 2026-10-23, where Team 2's
spawns sat on navmesh islands and the finder areas (Height 0) reached a floor far below)"""
import os
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'addon', 'bf2_map_tool'))
import buildview  # noqa: E402

work = sys.argv[1] if len(sys.argv) > 1 else 'E:/BF2MapTool_night/w_m1b/LegoUtapau3'
if not os.path.isdir(work):
    print('SKIP: %s not on this PC' % work)
    sys.exit(0)
R = buildview.load(work)
for line in buildview.summary(R):
    print(line)
fails = []
# the transform is the job's: a spawn read back from the level sits where the scene had it (to_game inverse)
off = R['offset']
for s in R['spawns'][:3]:
    g, b = s['game'], s['pos']
    if abs(b[0] + off[0] - g[0]) > 1e-6 or abs(b[2] + off[1] - g[1]) > 1e-6 or abs(-b[1] + off[2] - g[2]) > 1e-6:
        fails.append('transform: %s -> %s' % (g, b))
# components counted as the spawns stage counts them (spawns.json)
import json  # noqa: E402
sj = os.path.join(work, 'spawns.json')
if os.path.exists(sj):
    want = json.load(open(sj, encoding='utf-8'))['navmesh']['components']
    if R['nav_parts'] != want:
        fails.append('navmesh parts %d, spawns stage counted %d' % (R['nav_parts'], want))
if not R['spawns'] or not R['finders']:
    fails.append('no spawns / finder areas read')
if os.path.basename(work.rstrip('/')) == 'LegoUtapau3':
    # what was seen in game on 2026-10-23: spawns on islands, finder areas over several floors
    if not any(s['status'] != 'main' for s in R['spawns']):
        fails.append('Utapau: every spawn on the main part (in game some were on islands)')
    if not any('reaches floors' in p for p in R['problems']):
        fails.append('Utapau: no finder area reported as reaching other floors')
print('FAIL: ' + '; '.join(fails) if fails else 'PASS')
sys.exit(1 if fails else 0)
