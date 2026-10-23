"""Viewpoints for the door check scene. python doortest_shots.py <map.json> <settings.json> <out shots.json>"""
import json
import os
import sys

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime')))
from bf2maptool import config

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
G = job.to_game
shots = [{'name': 'door_a', 'at': list(G((-14.0, -6.0, 2.5))), 'look': list(G((-25.0, 0.0, 1.7))), 'repeat': 4, 'gap': 4.0},
         {'name': 'door_b', 'at': list(G((14.0, -6.0, 2.5))), 'look': list(G((25.0, 0.0, 1.7))), 'repeat': 4, 'gap': 4.0},
         {'name': 'door_a_far', 'at': list(G((-14.0, 8.0, 2.5))), 'look': list(G((-25.0, 11.0, 1.7))), 'repeat': 2, 'gap': 4.0},
         {'name': 'hall_from_above', 'at': list(G((0.0, -15.0, 22.0))), 'look': list(G((0.0, 2.0, 0.0)))},
         {'name': 'door_a_spawn_side', 'at': list(G((-36.0, -6.0, 2.5))), 'look': list(G((-25.0, 0.0, 1.7)))},
         {'name': 'door_b_spawn_side', 'at': list(G((36.0, -6.0, 2.5))), 'look': list(G((25.0, 0.0, 1.7)))}]
json.dump(shots, open(sys.argv[3], 'w'), indent=1)
print(len(shots), 'viewpoints, offset', job.offset())
