"""Viewpoints for a game test from a build's own spawn plan: eye-level views at both Blast bases and both hero starts
looking at the middle of the map, and two overviews. python make_shots.py <work dir of the map> <out shots.json>"""
import json
import sys

w, out = sys.argv[1], sys.argv[2]
p = json.load(open(w + '/spawns.json'))
b1, b2 = p['blast']['base1'], p['blast']['base2']
h1, h2 = p['hvv']['start1'], p['hvv']['start2']
mid = [(b1[i] + b2[i]) / 2 for i in range(3)]
eye = lambda q: [q[0], q[1] + 1.7, q[2]]
shots = [
    {'name': 'base1_to_mid', 'at': eye(b1), 'look': eye(mid)},
    {'name': 'base2_to_mid', 'at': eye(b2), 'look': eye(mid)},
    {'name': 'hero1_to_mid', 'at': eye(h1), 'look': eye(mid)},
    {'name': 'hero2_to_mid', 'at': eye(h2), 'look': eye(mid)},
    {'name': 'over_base1', 'at': [b1[0], b1[1] + 45.0, b1[2]], 'look': mid},
    {'name': 'over_mid', 'at': [mid[0] + 60.0, mid[1] + 70.0, mid[2] + 60.0], 'look': mid},
]
json.dump(shots, open(out, 'w'), indent=1)
print(len(shots), 'viewpoints ->', out)
