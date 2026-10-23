"""Camera views for comparing a real scene in Blender and in game: standing positions on the level's own walkable
floor (from a finished build's navmesh), each looking across the map, plus two from above.
    python scene_views.py <work/Name folder> <settings.json> <out views.json> [how many, default 6]
Views are in SCENE coordinates ({'name', 'at', 'look', 'kind': 'picture'}), the format ref_render.py and shots.py read.
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..', 'runtime')))
from bf2maptool import config
from bf2maptool.stages import nav, spawns

W, SETTINGS, OUT = sys.argv[1:4]
N = int(sys.argv[4]) if len(sys.argv) > 4 else 6
job = config.Job(os.path.join(W, 'export', 'map.json'), config.load_settings(SETTINGS))
polys, geo = nav.files(job)
P = spawns.Planner(job, spawns.Floor(polys, geo), np.random.default_rng(3))
C = P.C
mid = C.mean(0)
pick = [C[int(np.argmax(np.linalg.norm(C - mid, axis=1)))]]
d = np.full(len(C), 1e9)
while len(pick) < N:
    d = np.minimum(d, np.linalg.norm(C - pick[-1], axis=1))
    pick.append(C[int(np.argmax(d))])
views = []
for k, p in enumerate(pick):
    # look at the safe spot farthest away on about the same level: a long view across the floor
    far = C[int(np.argmax(np.hypot(*(C[:, [0, 2]] - p[[0, 2]]).T) - 4.0 * np.abs(C[:, 1] - p[1])))]
    at = np.array(job.from_game((p[0], p[1] + 1.7, p[2])), float)
    look = np.array(job.from_game((far[0], far[1] + 1.4, far[2])), float)
    views.append({'name': 'stand_%d' % k, 'at': at.tolist(), 'look': look.tolist(), 'kind': 'picture'})
m = np.array(job.from_game(mid), float)
ext = float(np.percentile(np.linalg.norm(C[:, [0, 2]] - mid[[0, 2]], axis=1), 90))
for k, (dx, dy) in enumerate(((-1.0, -1.0), (1.0, 0.6))):
    at = m + np.array([dx * ext * 1.1, dy * ext * 1.1, ext * 0.8 + 6.0])
    views.append({'name': 'above_%d' % k, 'at': at.tolist(), 'look': m.tolist(), 'kind': 'picture'})
json.dump({'views': views, 'scene': job.map['blend']['file'], 'spots': int(len(C))}, open(OUT, 'w'), indent=1)
print(len(views), 'views from', len(C), 'safe spots ->', OUT)
