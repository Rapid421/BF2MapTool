"""Read-only look at a scene before tagging it: the biggest objects, name groups, materials with alpha / emission,
lights, empties. blender -b <file.blend> --python list_objects.py -- <out.json>"""
import json
import re
import sys
from collections import Counter

import bpy

out = sys.argv[sys.argv.index('--') + 1]
objs = list(bpy.context.scene.objects)
rows = []
for o in objs:
    d = o.dimensions
    rows.append({'name': o.name, 'type': o.type, 'dim': [round(x, 1) for x in d], 'loc': [round(x, 1) for x in o.matrix_world.translation],
                 'tris': sum(len(p.vertices) - 2 for p in o.data.polygons) if o.type == 'MESH' else 0,
                 'mats': [m.name for m in o.data.materials if m] if o.type == 'MESH' else [], 'hidden': o.hide_render or o.hide_get(),
                 'parent': o.parent.name if o.parent else None})
types = Counter(r['type'] for r in rows)
stems = Counter(re.sub(r'[\d._]+$', '', r['name'].lower()) for r in rows)
big = sorted((r for r in rows if r['type'] == 'MESH'), key=lambda r: -max(r['dim']))[:25]
res = {'file': bpy.data.filepath, 'objects': len(rows), 'types': dict(types), 'name_stems': stems.most_common(60), 'biggest': big,
       'lights': [r for r in rows if r['type'] == 'LIGHT'], 'empties': [r for r in rows if r['type'] == 'EMPTY'][:60],
       'hidden': sum(1 for r in rows if r['hidden']), 'collections': [c.name for c in bpy.data.collections],
       'world': bool(bpy.context.scene.world), 'rows': rows}
json.dump(res, open(out, 'w'), indent=1)
print('LISTED', len(rows), dict(types))
