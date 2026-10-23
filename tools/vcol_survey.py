"""How do a scene's materials use Color Attributes (vertex colours)? Per material that reads one: how many objects use
it, and whether each object's colours are ONE colour (a per-object tint) or vary across the object.
    blender -b <scene.blend> --python vcol_survey.py -- <out.json>
"""
import json
import sys

import bpy
import numpy as np

out = sys.argv[sys.argv.index('--') + 1]


def reads_vcol(m):
    if not m or not m.use_nodes or not m.node_tree:
        return None
    seen, stack = set(), [m.node_tree]
    while stack:
        t = stack.pop()
        if t.name in seen:
            continue
        seen.add(t.name)
        for n in t.nodes:
            if n.type in ('VERTEX_COLOR',) or (n.type == 'ATTRIBUTE' and n.attribute_type == 'GEOMETRY'):
                return getattr(n, 'layer_name', '') or getattr(n, 'attribute_name', '') or '(active)'
            if n.type == 'GROUP' and n.node_tree:
                stack.append(n.node_tree)
    return None


mats = {m.name: reads_vcol(m) for m in bpy.data.materials}
vm = {k for k, v in mats.items() if v is not None}
rep = {'materials_reading_vcol': len(vm), 'objects': 0, 'constant': 0, 'varying': 0, 'no_attribute': 0, 'examples_varying': [], 'spread': []}
for o in bpy.context.scene.objects:
    if o.type != 'MESH' or o.bf2.role != 'static':
        continue
    used = [s.material.name for s in o.material_slots if s.material and s.material.name in vm]
    if not used:
        continue
    rep['objects'] += 1
    me = o.data
    ca = me.color_attributes.active_color if me.color_attributes else None
    if ca is None or not len(ca.data):
        rep['no_attribute'] += 1
        continue
    c = np.empty(len(ca.data) * 4, np.float32); ca.data.foreach_get('color', c); c = c.reshape(-1, 4)
    spread = float((c.max(0) - c.min(0))[:3].max())
    rep['spread'].append(round(spread, 3))
    if spread < 0.02:
        rep['constant'] += 1
    else:
        rep['varying'] += 1
        if len(rep['examples_varying']) < 12:
            rep['examples_varying'].append([o.name, used[:2], round(spread, 3), ca.domain, len(ca.data)])
s = np.array(rep['spread'] or [0])
rep['spread_pct'] = {p: round(float(np.percentile(s, p)), 3) for p in (10, 50, 90, 99)}
del rep['spread']
json.dump(rep, open(out, 'w'), indent=1)
print('VCOL ' + json.dumps(rep))
