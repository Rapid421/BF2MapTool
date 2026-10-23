"""Viewpoints for the game test of ANY tool-built level, from what the build itself measured (work/<Name>/spawns.json):
an overview from above, both Blast bases at eye height looking at each other, both HvV starts looking around.
python auto_shots.py <map.json> <settings.json> <out shots.json>"""
import json
import math
import os
import sys

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'runtime')))
from bf2maptool import config

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
S = json.load(open(job.work + '/spawns.json'))
b1, b2 = S['blast']['base1'], S['blast']['base2']
mid = [(a + b) / 2 for a, b in zip(b1, b2)]
d = max(math.dist(b1, b2), 30.0)
eye = lambda p, h=1.8: [p[0], p[1] + h, p[2]]
shots = [{'name': 'overview', 'repeat': 2, 'gap': 4.0, 'at': [mid[0] - 0.5 * d, mid[1] + 0.7 * d, mid[2] - 0.5 * d], 'look': mid},
         {'name': 'top_down', 'at': [mid[0] + 0.5, mid[1] + 1.2 * d, mid[2]], 'look': mid},
         {'name': 'base1_to_base2', 'at': eye(b1), 'look': eye(b2)}, {'name': 'base2_to_base1', 'at': eye(b2), 'look': eye(b1)},
         {'name': 'base1_high', 'at': eye(b1, 12.0), 'look': mid}, {'name': 'base2_high', 'at': eye(b2, 12.0), 'look': mid}]
for tag, key in (('hvv1', 'start1'), ('hvv2', 'start2')):
    p = (S.get('hvv') or {}).get(key)
    if p:
        for k, ang in enumerate((0, 90, 180, 270)):
            a = math.radians(ang)
            shots.append({'name': '%s_look_%03d' % (tag, ang), 'at': eye(p), 'look': [p[0] + 20 * math.sin(a), p[1] + 1.8, p[2] + 20 * math.cos(a)]})
json.dump(shots, open(sys.argv[3], 'w'), indent=1)
print(len(shots), 'viewpoints; bases', d, 'm apart')
