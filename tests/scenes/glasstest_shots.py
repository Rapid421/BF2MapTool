"""Viewpoints for the glass test. python glasstest_shots.py <map.json> <settings.json> <out shots.json>"""
import json
import os
import sys

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime')))
from bf2maptool import config

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
G = job.to_game
V = [('warm_up', (0.0, -14.0, 6.0), (0.0, 0.0, 1.5), 2), ('through_the_pane', (0.0, -9.0, 1.8), (0.0, 5.0, 1.5), 2),
     ('pane_and_beside', (6.0, -12.0, 2.5), (8.0, 5.0, 1.5), 2), ('along_the_pane', (-12.0, -1.5, 1.8), (8.0, 0.0, 1.6), 1),
     ('overview', (0.0, -22.0, 14.0), (0.0, 2.0, 0.0), 1)]
shots = []
for name, at, look, rep in V:
    s = {'name': name, 'at': list(G(at)), 'look': list(G(look))}
    if rep > 1:
        s.update(repeat=rep, gap=3.0)
    shots.append(s)
json.dump(shots, open(sys.argv[3], 'w'), indent=1)
print(len(shots), 'viewpoints')
