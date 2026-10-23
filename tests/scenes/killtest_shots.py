"""Viewpoints for the kill volume test. python killtest_shots.py <map.json> <settings.json> <out shots.json>"""
import json
import os
import sys

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime')))
from bf2maptool import config

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
G = job.to_game
V = [('warm_up', (0.0, -14.0, 9.0), (20.0, 0.0, 0.0), 2), ('hatch_side', (10.0, -6.0, 3.0), (20.0, 0.0, -0.5), 4),
     ('pit_from_above', (20.0, -3.0, 7.0), (20.0, 0.0, -2.8), 3), ('overview', (-5.0, -20.0, 16.0), (5.0, 0.0, 0.0), 1)]
shots = []
for name, at, look, rep in V:
    s = {'name': name, 'at': list(G(at)), 'look': list(G(look))}
    if rep > 1:
        s.update(repeat=rep, gap=3.0)
    shots.append(s)
json.dump(shots, open(sys.argv[3], 'w'), indent=1)
print(len(shots), 'viewpoints')
