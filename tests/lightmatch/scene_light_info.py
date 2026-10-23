"""What lights a scene (nothing is changed):  blender -b --factory-startup <scene.blend> --python scene_light_info.py -- <add-on folder>"""
import os
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
sys.path.insert(0, os.path.dirname(argv[0]))
mod = __import__(os.path.basename(argv[0]) + '.props', fromlist=['props'])
if not hasattr(bpy.types.Scene, 'bf2'):
    mod.register()
sc = bpy.context.scene
w = sc.world
print('INFO name', sc.bf2.name, '| sky', sc.bf2.sky, '| world', w.name if w else None,
      [(n.type, round(n.inputs['Strength'].default_value, 3) if n.type == 'BACKGROUND' else '') for n in w.node_tree.nodes] if w and w.node_tree else None)
print('INFO lamps', [(o.name, o.data.type, round(o.data.energy, 2), o.hide_render) for o in bpy.data.objects if o.type == 'LIGHT'][:12])
em = []
for m in bpy.data.materials:
    if m.node_tree:
        for n in m.node_tree.nodes:
            if n.type == 'BSDF_PRINCIPLED' and n.inputs['Emission Strength'].default_value > 0 and max(n.inputs['Emission Color'].default_value[:3]) > 0:
                em.append((m.name, round(n.inputs['Emission Strength'].default_value, 2)))
print('INFO emissive', em[:10])
vs = sc.view_settings
print('INFO view', vs.view_transform, vs.look, vs.exposure, '| objects', len([o for o in sc.objects if o.type == 'MESH']))
