"""Generic game-test viewpoints for ANY built map, from its own spawn plan (game coordinates, no scene knowledge):
each team base from 6 m up and 12 m behind, looking at the other base; the middle from high up; each base at eye
height looking across.    python plan_shots.py <work dir of the level> <out shots.json>"""
import json
import sys

import numpy as np

W, OUT = sys.argv[1], sys.argv[2]
p = json.load(open(W + '/spawns.json'))
b1, b2 = np.array(p['blast']['base1'], float), np.array(p['blast']['base2'], float)
mid = (b1 + b2) / 2
d = b2 - b1
d[1] = 0.0
d /= max(np.linalg.norm(d), 1e-6)
shots = [{'name': 'warm_up', 'at': list(mid + [0, 25, 0]), 'look': list(mid), 'repeat': 2, 'gap': 3.0}]
for name, b, other, s in (('base1_high', b1, b2, -1), ('base2_high', b2, b1, 1)):
    shots.append({'name': name, 'at': list(b + [0, 6, 0] + s * d * 12), 'look': list(other)})
for name, b, other in (('base1_eye', b1, b2), ('base2_eye', b2, b1)):
    shots.append({'name': name, 'at': list(b + [0, 1.7, 0]), 'look': list(other + [0, 1.7, 0])})
side = np.array([-d[2], 0, d[0]])
span = float(np.linalg.norm(b2 - b1))
shots.append({'name': 'middle_high', 'at': list(mid + [0, max(20.0, span * 0.5), 0] + side * span * 0.6), 'look': list(mid)})
json.dump(shots, open(OUT, 'w'), indent=1)
print(len(shots), 'viewpoints')
