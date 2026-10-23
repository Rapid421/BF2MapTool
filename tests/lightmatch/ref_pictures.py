"""Blender's own pictures of a scene from given views, as the scene is set up (its engine settings are kept where they
can be: Cycles, its view transform, exposure, world, lamps), for laying beside the game's pictures.
    blender -b --factory-startup <scene.blend> --python ref_pictures.py -- <views.json> <out dir> [vertical fov degrees]
Writes <out dir>/<view name>.png (960 x 540) and ref.json (what was used).
"""
import json
import math
import os
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
info = json.load(open(argv[0]))
OUT = argv[1]
FOV = float(argv[2]) if len(argv) > 2 else 55.0
os.makedirs(OUT, exist_ok=True)
sc = bpy.context.scene
cam_data = bpy.data.cameras.new('cmp_cam')
cam = bpy.data.objects.new('cmp_cam', cam_data)
sc.collection.objects.link(cam)
sc.camera = cam
cam_data.sensor_fit = 'VERTICAL'
cam_data.angle_y = math.radians(FOV)
cam_data.clip_start, cam_data.clip_end = 0.05, 5000.0
r = sc.render
r.engine = 'CYCLES'
sc.cycles.device = 'CPU'
sc.cycles.samples = 96
sc.cycles.use_adaptive_sampling = True
try:
    sc.cycles.use_denoising = True
except Exception:
    pass
r.resolution_x, r.resolution_y, r.resolution_percentage = 960, 540, 100
r.film_transparent = False
r.image_settings.file_format = 'PNG'
used = {'view_transform': sc.view_settings.view_transform, 'look': sc.view_settings.look, 'exposure': sc.view_settings.exposure,
        'world': sc.world.name if sc.world else None, 'fov_vertical': FOV, 'samples': 96, 'views': []}
for v in info['views']:
    if v.get('kind') != 'picture':
        continue
    cam.location = v['at']
    cam.rotation_euler = (Vector(v['look']) - Vector(v['at'])).to_track_quat('-Z', 'Y').to_euler()
    r.filepath = os.path.join(OUT, v['name'] + '.png')
    bpy.ops.render.render(write_still=True)
    used['views'].append(v['name'])
    print('REF PICTURE', v['name'], flush=True)
json.dump(used, open(os.path.join(OUT, 'ref.json'), 'w'), indent=1)
print('REF PICTURES DONE', len(used['views']))
