"""Read-only: what gameplay markers does an OG 2005 blend still carry? Command posts (cp*, *controlzone*), spawn paths,
regions, flags, vehicle spawns - by name and by object type (empties, curves).
    blender -b <file.blend> --python survey_og_markers.py -- <out.json>
"""
import json
import re
import sys
from collections import Counter

import bpy

out = sys.argv[sys.argv.index('--') + 1]
PAT = re.compile(r'(^cp\d|_cp\d|cp_|command|control|capture|spawn|path|region|flag|vehicle|veh_|turret|hub|node|team|ctf|hunt|assault)', re.I)
rows = []
for o in bpy.context.scene.objects:
    if PAT.search(o.name) or o.type in ('EMPTY', 'CURVE'):
        rows.append({'name': o.name, 'type': o.type, 'loc': [round(x, 2) for x in o.matrix_world.translation],
                     'dim': [round(x, 1) for x in o.dimensions], 'parent': o.parent.name if o.parent else None,
                     'collections': [c.name for c in o.users_collection],
                     'props': {k: str(o[k])[:80] for k in o.keys() if not k.startswith('_')}})
stems = Counter(re.sub(r'[\d._]+$', '', r['name'].lower()) for r in rows)
json.dump({'file': bpy.data.filepath, 'markers': len(rows), 'stems': stems.most_common(40),
           'collections': [c.name for c in bpy.data.collections], 'rows': rows}, open(out, 'w'), indent=1)
print('SURVEYED', len(rows))
