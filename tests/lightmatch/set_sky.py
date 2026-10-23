"""Set the tool's Sky choice in a scene file and save it (for test copies of a scene).
    blender -b --factory-startup <scene.blend> --python set_sky.py -- <add-on folder> <sky choice>
"""
import os
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
sys.path.insert(0, os.path.dirname(argv[0]))
mod = __import__(os.path.basename(argv[0]) + '.props', fromlist=['props'])
if not hasattr(bpy.types.Scene, 'bf2'):
    mod.register()
sp = bpy.context.scene.bf2
print('SKY WAS', sp.sky)
sp.sky = argv[1]
w = bpy.context.scene.world
print('WORLD', w.name if w else None, 'nodes', [n.type for n in w.node_tree.nodes] if w and w.node_tree else None)
print('LAMPS', [(o.name, o.data.type, round(o.data.energy, 2), tuple(round(c, 2) for c in o.data.color), o.hide_render) for o in bpy.data.objects if o.type == 'LIGHT'])
vs = bpy.context.scene.view_settings
print('VIEW', vs.view_transform, vs.look, vs.exposure)
bpy.ops.wm.save_mainfile()
print('SKY NOW', sp.sky)
