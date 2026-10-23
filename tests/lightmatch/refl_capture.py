"""The add-on's reflection capture, run by hand on a scene (to test it):
    blender -b --factory-startup <scene.blend> --python refl_capture.py -- <add-on folder> <out.hdr> x,y,z [width]
"""
import os
import sys
import time

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
sys.path.insert(0, os.path.dirname(argv[0]))
mod = __import__(os.path.basename(argv[0]) + '.reflbake', fromlist=['reflbake'])
t = time.time()
r = mod.capture(bpy.context.scene, [float(v) for v in argv[2].split(',')], argv[1], int(argv[3]) if len(argv) > 3 else 512)
print('REFL CAPTURE', r, 'seconds %.1f' % (time.time() - t))
