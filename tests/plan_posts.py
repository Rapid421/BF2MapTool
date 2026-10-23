"""Five command post positions for a scene that was built once: on the line between its two team bases.
    python plan_posts.py <work/Name folder> <settings.json> <out posts.json>"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'runtime')))
from bf2maptool import config

W = sys.argv[1]
job = config.Job(os.path.join(W, 'export', 'map.json'), config.load_settings(sys.argv[2]))
with open(os.path.join(W, 'spawns.json'), encoding='utf-8') as f:
    b = json.load(f)['blast']
a, e = np.array(job.from_game(b['base1']), float), np.array(job.from_game(b['base2']), float)
out = [[L, [round(float(v), 2) for v in a + (e - a) * t], team]
       for L, t, team in (('A', 0.0, 'team1'), ('B', 0.25, 'team1'), ('C', 0.5, 'neutral'), ('D', 0.75, 'team2'), ('E', 1.0, 'team2'))]
json.dump(out, open(sys.argv[3], 'w'), indent=1)
print(out)
