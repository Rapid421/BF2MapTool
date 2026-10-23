"""A small arena inside a 2.2 km backdrop shell without collision (does far, huge geometry load?).
    blender -b --factory-startup <arena.blend> --python make_fartest.py -- <add-on folder> <out.blend> [shell size m] [name]
"""
import os
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
sys.path.insert(0, os.path.dirname(argv[0]))
mod = __import__(os.path.basename(argv[0]) + '.props', fromlist=['props'])
if not hasattr(bpy.types.Scene, 'bf2'):
    mod.register()
size = float(argv[2]) if len(argv) > 2 else 2200.0
sc = bpy.context.scene
sc.bf2.name = argv[3] if len(argv) > 3 else 'FarTest'
sc.bf2.keep_distance = size
bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0, 0, 100))
o = bpy.context.active_object
o.name = 'backdrop_shell'
o.scale = (size, size, 400.0)
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.mesh.flip_normals()                       # seen from inside
bpy.ops.mesh.subdivide(number_cuts=3)
bpy.ops.object.mode_set(mode='OBJECT')
m = bpy.data.materials.new('backdrop_mat'); m.use_nodes = True
b = m.node_tree.nodes['Principled BSDF']
b.inputs['Base Color'].default_value = (0.3, 0.2, 0.2, 1)
b.inputs['Emission Color'].default_value = (1.0, 0.4, 0.3, 1)
b.inputs['Emission Strength'].default_value = 2.0
o.data.materials.append(m)
o.bf2.collision = 'none'
bpy.ops.wm.save_as_mainfile(filepath=argv[1])
print('FARTEST SAVED', argv[1], size)
