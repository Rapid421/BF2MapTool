"""Unit test of checks.group(): many lines of one code become one line that keeps every material for Select; few
lines and other codes stay as they are.  python test_check_group.py"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime'))
from bf2maptool import checks  # noqa: E402

rs = [checks.R('tex.bake_note', 'warning', 'Material "M%d" was baked ...' % i, materials=['M%d' % i]) for i in range(8)]
rs += [checks.R('tex.resized', 'info', 'Material "R%d" resized' % i, materials=['R%d' % i]) for i in range(3)]
rs += [checks.R('light.dynamic_budget', 'error', '114 lights are real-time', ['L1', 'L2'])]
g = checks.group(rs)
by = {}
for r in g:
    by.setdefault(r['code'], []).append(r)
fails = []
if len(by['tex.bake_note']) != 1:
    fails.append('bake_note not grouped: %d lines' % len(by['tex.bake_note']))
else:
    x = by['tex.bake_note'][0]
    if x['materials'] != ['M%d' % i for i in range(8)] or x.get('grouped') != 8 or not x['message'].startswith('8 of these'):
        fails.append('grouped line wrong: %s' % x)
if len(by['tex.resized']) != 3:
    fails.append('3 lines must stay 3: %d' % len(by['tex.resized']))
if len(by['light.dynamic_budget']) != 1 or by['light.dynamic_budget'][0]['objects'] != ['L1', 'L2']:
    fails.append('single error changed')
if len(g) != 5:
    fails.append('total %d lines, expected 5' % len(g))
print('FAIL' if fails else 'PASS', fails)
sys.exit(1 if fails else 0)
