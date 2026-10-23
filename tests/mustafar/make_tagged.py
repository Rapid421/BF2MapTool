"""Prepare the Mustafar scene as a user of the add-on would have it: the port's fixed blend (opened, never saved back)
tagged with the tool's properties - material classes from the port's plan, the 18 door panels as doors that rise, a
volcanic sky - and saved as a COPY. From there on only the tool is used.

  blender -b <mus_fixed.blend> --python make_tagged.py -- <out.blend> <plan_mus.json> [LevelName]
"""
import json
import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, '..', '..', 'addon', 'bf2_map_tool')))
import props

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, PLAN = argv[0], json.load(open(argv[1]))
NAME = argv[2] if len(argv) > 2 else 'MustafarT'
props.register()
sc = bpy.context.scene
sc.bf2.name = NAME
sc.bf2.display_name = 'Mustafar: Refinery (tool)'
sc.bf2.planet = 'Mustafar'
sc.bf2.description = 'A Separatist mining refinery above the lava rivers of Mustafar.'
sc.bf2.mode_supremacy = False
sc.bf2.sky = 'volcanic'
sc.bf2.sun_on = False
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
    names = [o.name for o in lc.collection.all_objects if o.type == 'MESH']
    for nm in names:
        o = bpy.data.objects[nm]
        o.bf2.role = 'door'
        o.bf2.door_axis = 'up'
        o.hide_render = False
        nd += 1
bpy.context.view_layer.update()
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
print('TAGGED', OUT, 'materials', n, 'doors', nd)
