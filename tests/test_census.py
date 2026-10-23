"""The depot census must catch what it is for. Uses the depot dumps of a finished build (work/<Name>/dumps/depots).
    python test_census.py <work/Name folder> <settings.json>
  1 as built: no problem
  2 one piece given its template's own texture values (= its material parameters lost): reported
  3 one piece of a material given another texture value than its siblings: reported
  4 one piece's depot taken away: reported
  5 one piece given a surface permutation its template does not use: reported
"""
import copy
import json
import os
import sys

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'runtime')))
from bf2maptool import census, config

W = sys.argv[1]
job = config.Job(os.path.join(W, 'export', 'map.json'), config.load_settings(sys.argv[2]))
with open(os.path.join(W, 'import.json'), encoding='utf-8') as f:
    build = json.load(f)['pieces']
host, piece_res, tpl_res = census.res_names(job, build)
got = {}
dd = os.path.join(W, 'dumps', 'depots')
for f in sorted(os.listdir(dd)):
    with open(os.path.join(dd, f), encoding='utf-8', errors='replace') as fh:
        got.update(census._parse(fh.read()))
ok = True


def case(name, got2, want):
    global ok
    res = census.analyse(host, build, piece_res, tpl_res, got2)
    hit = any(want in p for p in res['problems']) if want else not res['problems']
    ok = ok and hit
    print('%-4s %-34s %s' % ('PASS' if hit else 'FAIL', name, (res['problems'] or ['no problem'])[0][:150]))


case('as built', got, None)
mats = {}
for b in build:
    mats.setdefault((b['material'], b.get('template')), []).append(b)
first = build[0]
g = copy.deepcopy(got)
g[piece_res[first['as']]]['tex'] = dict(got[tpl_res[first['template']]]['tex'])
case('parameters lost on one piece', g, "template's own textures")
multi = next((v for v in mats.values() if len(v) > 1), None)
if multi:
    g = copy.deepcopy(got)
    k = next(iter(g[piece_res[multi[0]['as']]]['tex']))
    g[piece_res[multi[0]['as']]]['tex'][k] = 'FF' * 16
    case('one piece differs from its material', g, 'different texture parameters')
g = copy.deepcopy(got)
del g[piece_res[first['as']]]
case('depot missing', g, 'no shader-block depot')
g = copy.deepcopy(got)
g[piece_res[first['as']]]['rvm'] = {'00000000-0000-0000-0000-000000000000'}
case('foreign surface permutation', g, 'surface permutation')
print('PASS' if ok else 'FAIL')
sys.exit(0 if ok else 1)
