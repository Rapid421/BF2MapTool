"""Viewpoints for the collision check scene. python coltest_shots.py <map.json> <settings.json> <out shots.json>"""
import json
import os
import sys

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime')))
from bf2maptool import config

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
G = job.to_game
V = [('warm_up', (0.0, -18.0, 14.0), (0.0, 0.0, 0.0), 2), ('exact_side', (-27.0, -19.0, 2.5), (-27.0, -9.0, 2.0), 3),
     ('custom_side', (-27.0, 19.0, 2.5), (-27.0, 9.0, 2.0), 3), ('exact_custom_from_east', (-14.0, 0.0, 4.0), (-27.0, 0.0, 1.5), 2),
     ('hull_side', (30.0, -10.0, 2.5), (30.0, 0.0, 2.0), 3), ('hull_from_west', (22.0, -6.0, 4.5), (30.0, 0.0, 1.5), 2),
     ('none_wall_outside', (10.0, 4.0, 2.2), (20.0, 4.0, 1.5), 4), ('yard_from_above', (30.0, -4.0, 22.0), (27.0, 0.0, 0.0), 2),
     ('overview', (0.0, -30.0, 30.0), (0.0, 0.0, 0.0), 2)]
shots = []
for name, at, look, rep in V:
    s = {'name': name, 'at': list(G(at)), 'look': list(G(look))}
    if rep > 1:
        s.update(repeat=rep, gap=4.0)
    shots.append(s)
json.dump(shots, open(sys.argv[3], 'w'), indent=1)
print(len(shots), 'viewpoints, offset', job.offset())
