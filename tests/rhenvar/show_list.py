"""print the listing made by list_objects.py in a compact form: python show_list.py <objects.json>"""
import json
import sys
from collections import Counter

d = json.load(open(sys.argv[1]))
rows = d['rows']
mesh = [r for r in rows if r['type'] == 'MESH']
print('objects', d['objects'], d['types'], 'world', d['world'])
print('BIGGEST meshes:')
for r in d['biggest'][:18]:
    print('  %-44s dim %-26s tris %6d mats %s' % (r['name'][:44], r['dim'], r['tris'], r['mats'][:3]))
print('LIGHTS', [(r['name'], r['loc']) for r in d['lights']])
st = Counter()
for r in mesh:
    n = r['name'].lower()
    k = 'collision-named' if 'collision' in n else 'sky / dome-named' if any(x in n for x in ('sky', 'dome')) else 'shadow-named' if 'shadow' in n else \
        'lowres / lod-named' if any(x in n for x in ('lowres', 'lod', '_far')) else 'other'
    st[k] += 1
print('MESH name kinds', dict(st))
em = Counter()
for r in rows:
    if r['type'] == 'EMPTY':
        n = r['name'].lower()
        em['collision' if 'collision' in n else 'hp_ / hardpoint' if n.startswith('hp_') else 'dummy' if 'dummy' in n else 'other'] += 1
print('EMPTY name kinds', dict(em))
print('sample "other" empties', [r['name'] for r in rows if r['type'] == 'EMPTY' and 'collision' not in r['name'].lower()][:40])
print('mesh parents:', Counter(('parented' if r['parent'] else 'root') for r in mesh))
mats = Counter(m for r in mesh for m in r['mats'])
print('materials', len(mats), mats.most_common(12))
# the playable part: bounding box of meshes smaller than 400 m
small = [r for r in mesh if max(r['dim']) < 400]
xs = [r['loc'][0] for r in small]; ys = [r['loc'][1] for r in small]; zs = [r['loc'][2] for r in small]
print('meshes under 400 m: %d; origins x %.0f..%.0f y %.0f..%.0f z %.0f..%.0f' % (len(small), min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))
