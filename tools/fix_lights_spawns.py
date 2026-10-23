"""Lamps -> Baked (all but `keep_dynamic` named ones), drop named spawns. Saves a copy.
    blender -b --factory-startup <in.blend> --python fix_lights_spawns.py -- <addon dir> <out.blend> [drop=Name,Name] [keep_dynamic=Name,...]
"""
import os
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
ADDON, OUT = argv[:2]
opt = dict(a.split('=', 1) for a in argv[2:] if '=' in a)
sys.path.insert(0, os.path.dirname(ADDON))
props = __import__(os.path.basename(ADDON) + '.props', fromlist=['props'])
if not hasattr(bpy.types.Scene, 'bf2'):
    props.register()
keep = {n for n in opt.get('keep_dynamic', '').split(',') if n}
n_baked = 0
for o in bpy.data.objects:
    if o.type == 'LIGHT' and o.data.type != 'SUN' and o.name not in keep:
        if o.data.bf2.mode != 'baked':
            o.data.bf2.mode = 'baked'
            n_baked += 1
dropped = []
for name in [n for n in opt.get('drop', '').split(',') if n]:
    o = bpy.data.objects.get(name)
    if o is not None:
        bpy.data.objects.remove(o, do_unlink=True)
        dropped.append(name)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
print('FIXED lights to baked %d, dropped %s -> %s' % (n_baked, dropped, OUT))
