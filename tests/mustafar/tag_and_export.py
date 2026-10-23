"""THROW-AWAY regression driver (work order step 3), not part of the shipped tool: tags the Mustafar port's fixed blend
in memory with the tool's properties (the blend is opened read-only and never saved) and runs the tool's own scene
export on it.

  blender -b <mus_fixed.blend> --python tag_and_export.py -- <out dir> <plan_mus.json>

Material classes and the 2005 Doublesided flag come from the port's own plan (work/plan_mus.json); the two image-less
console-fx materials the port dropped are left out here too.
"""
import json
import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..', 'addon', 'bf2_map_tool')))
import props
import export_scene

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, PLAN = argv[0], json.load(open(argv[1]))
props.register()
sc = bpy.context.scene
sc.bf2.name = 'MustafarT'
sc.bf2.display_name = 'Mustafar: Refinery (tool)'
sc.bf2.planet = 'Mustafar'
sc.bf2.description = 'A Separatist mining refinery above the lava rivers of Mustafar.'
sc.bf2.mode_supremacy = False
CLS = {'opaque': 'opaque', 'glow': 'emissive', 'cutout': 'cutout', 'lava': 'lava'}
DROP = ('mus1_prop_console_fx_1', 'mus1_prop_console_fx_16777232', 'ConversionAssets_SWBFStandard')
n = 0
for m in bpy.data.materials:
    if m.name in DROP:
        m.bf2.skip = True
        continue
    r = PLAN.get(m.name)
    if r:
        m.bf2.cls = CLS[r['cls']]
        m.bf2.double_sided = bool(r.get('doublesided'))
        n += 1
print('tagged materials', n)
# the port's 18 door panels sit in a collection the fixed blend keeps out of the view layer: bring it in (in memory)
# and tag every panel as a door that slides up (the user's 2005 screenshots: every Mustafar door rises)


def find_lc(lc, name):
    if lc.collection.name == name:
        return lc
    for c in lc.children:
        r = find_lc(c, name)
        if r:
            return r
    return None


lc = find_lc(bpy.context.view_layer.layer_collection, 'MUS_DOORS')
nd = 0
if lc:
    lc.exclude = False
    for o in [x for x in bpy.data.objects if x.name in {y.name for y in lc.collection.all_objects}]:
        if o.type == 'MESH':
            o.bf2.role = 'door'
            o.bf2.door_axis = 'up'
            o.hide_render = False
            nd += 1
bpy.context.view_layer.update()
print('tagged doors', nd)
export_scene.export(OUT)
