"""The original 2005 Conquest command posts of every OG map -> five Supremacy posts A..E (tests/add_posts.py format).
Reads the marker survey (tools/survey_og.py): the Conquest layer is the parent that holds the most `cpN` empties
(OG Coruscant: cp1..cp6 under Instances_4; CTF / Hunt layers are other parents). Team ownership is not in the blends
(it lived in the 2005 Lua), so the posts are ordered along the map's long axis: A, B = team 1's end, C neutral, D, E =
team 2's end. Five are picked spread along that line; a map with fewer gets the missing ones half-way between neighbours.
    python og_posts.py <survey dir> <out dir>      -> <out>/<Map>_posts.json + posts_summary.md
"""
import json
import os
import re
import sys
from collections import defaultdict

import numpy as np


# the mode a command post model belongs to, from its parent (the 2005 instance) name. Conquest is what is left.
OTHER_MODE = re.compile(r'ctf|flag|tdm|camp|^cam_|death|extraspawn|ridespawn|rebelleader|^team|_obj|hunt|inviso|flg|assault|hero', re.I)
CONQUEST_NAMED = re.compile(r'^(?:con_)?cp\d+a?(?:con)?$', re.I)


def conquest_posts(rows):
    """the Conquest posts: command post MODELS (com_bldg_controlzone*) whose parent is a Conquest instance - `cpN`,
    `cpNcon`, `con_cpN` (Coruscant, Polis Massa, Utapau) - else every model whose parent names no other mode (Yavin's
    posts are named for places: bazaar, temple, viaduct). One per place (copies within 5 m are one post)."""
    # first choice: the Conquest layer's `cpN` empties (the layer that holds the most of them) - Jabba's post MODELS
    # hang off the scene root, so their parent names say nothing
    groups = defaultdict(list)
    for r in rows:
        if r['type'] == 'EMPTY' and re.fullmatch(r'cp\d+', r['name'].lower()):
            groups[r['parent'] or ''].append(r)
    if groups:
        layer = max(groups, key=lambda k: len(groups[k]))
        if len(groups[layer]) >= 4:
            return layer, sorted(groups[layer], key=lambda r: int(r['name'][2:]))
    models = [r for r in rows if r['type'] == 'MESH' and r['name'].lower().startswith('com_bldg_controlzone') and r['parent']]
    named = [r for r in models if CONQUEST_NAMED.match(r['parent'])]
    pick, layer = (named, 'cpN posts') if len(named) >= 4 else ([r for r in models if not OTHER_MODE.search(r['parent'])], 'named posts')
    out = []
    for r in sorted(pick, key=lambda r: r['parent']):
        if all(np.linalg.norm(np.array(r['loc'][:2]) - np.array(o['loc'][:2])) > 5.0 for o in out):
            out.append({'name': r['parent'], 'loc': r['loc']})
    return layer, out


def five(cps):
    p = np.array([c['loc'] for c in cps], np.float64)
    xy = p[:, :2] - p[:, :2].mean(0)
    axis = np.linalg.svd(xy, full_matrices=False)[2][0] if len(p) > 1 else np.array([1.0, 0.0])
    order = np.argsort(xy @ axis)
    p, names = p[order], [cps[i]['name'] for i in order]
    while len(p) < 5:                                    # fill the widest gap
        gaps = np.linalg.norm(np.diff(p, axis=0), axis=1)
        i = int(np.argmax(gaps))
        p = np.insert(p, i + 1, (p[i] + p[i + 1]) / 2, axis=0)
        names.insert(i + 1, '(between %s and %s)' % (names[i], names[i + 1]))
    pick = np.round(np.linspace(0, len(p) - 1, 5)).astype(int)
    teams = ['team1', 'team1', 'neutral', 'team2', 'team2']
    return [[l, [round(float(x), 2) for x in p[i]], t] for l, i, t in zip('ABCDE', pick, teams)], [names[i] for i in pick], len(cps)


def main(survey, out):
    os.makedirs(out, exist_ok=True)
    lines = ['| map | layer | OG posts | A, B (team 1) | C | D, E (team 2) |', '|---|---|---|---|---|---|']
    for f in sorted(os.listdir(survey)):
        if not (f.startswith('OG') and f.endswith('.json')):
            continue
        name = f[:-5]
        layer, cps = conquest_posts(json.load(open(os.path.join(survey, f)))['rows'])
        if len(cps) < 2:
            lines.append('| %s | %s | %d | (too few posts in the blend: automatic placement) | | |' % (name, layer or '-', len(cps)))
            continue
        posts, used, n = five(cps)
        json.dump(posts, open(os.path.join(out, name + '_posts.json'), 'w'), indent=1)
        lines.append('| %s | %s | %d | %s | %s | %s |' % (name, layer, n, ', '.join(used[:2]), used[2], ', '.join(used[3:])))
    open(os.path.join(out, 'posts_summary.md'), 'w').write('\n'.join(lines) + '\n')
    print('\n'.join(lines))


if __name__ == '__main__':
    main(*sys.argv[1:3])
