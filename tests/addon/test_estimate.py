"""Estimate size on an open scene with the installed add-on; prints ESTIMATE {json}.
    blender -b <scene.blend> --factory-startup --python test_estimate.py -- <zip>      (BLENDER_USER_SCRIPTS = a throw-away dir)"""
import json
import os
import sys
import time

import bpy

zip_path = sys.argv[sys.argv.index('--') + 1]
bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)
bpy.ops.preferences.addon_enable(module='bf2_map_tool')
t = time.time()
r = bpy.ops.bf2.estimate()
e = json.loads(bpy.context.window_manager.bf2_estimate)
e['seconds'] = round(time.time() - t, 1)
e['result'] = list(r)
print('ESTIMATE ' + json.dumps(e))
sys.stdout.flush()
os._exit(0)
