"""Viewpoints for the light test scene: one into each bay from the south, at the same distance and height.
python lighttest_shots.py <map.json> <settings.json> <out shots.json>"""
import json
import os
import sys

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime')))
from bf2maptool import config

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
G = job.to_game
V = [('warm_up', (0.0, -20.0, 10.0), (0.0, 8.0, 0.0), 2)]
bays = json.load(open(job.map['blend']['file'] + '.bays.json'))
V += [('bay_' + b['tag'], (b['cx'], -4.0, 2.2), (b['cx'], 8.0, 0.8), 2) for b in bays]
V += [('overview', (0.0, -26.0, 14.0), (0.0, 8.0, 0.0), 1)]
shots = []
for name, at, look, rep in V:
    s = {'name': name, 'at': list(G(at)), 'look': list(G(look))}
    if rep > 1:
        s.update(repeat=rep, gap=3.0)
    shots.append(s)
json.dump(shots, open(sys.argv[3], 'w'), indent=1)
print(len(shots), 'viewpoints')
