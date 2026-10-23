"""Set tool properties of a scene and save it under another name (test copies of a scene).
    blender -b --factory-startup <scene.blend> --python set_props.py -- <add-on folder> <out.blend> key=value [key=value ...]
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
for a in argv[2:]:
    k, v = a.split('=', 1)
    old = getattr(sp, k)
    setattr(sp, k, type(old)(v) if not isinstance(old, bool) else v in ('1', 'true', 'True'))
    print('SET', k, old, '->', getattr(sp, k))
bpy.ops.wm.save_as_mainfile(filepath=argv[1], copy=True)
print('SAVED', argv[1])
