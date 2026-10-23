"""Blender's own answer for every view of the light-match scene: what Cycles renders there, in scene-linear numbers.
    blender -b <scene.blend> --python ref_render.py -- <scene.blend>.views.json <out dir> [samples]
Measuring views: a 20 degree camera at the view's position looking at its target; the mean of the whole small picture
(the patch fills it) is written as scene-linear RGB -> <out dir>/reference.json.
Picture views: a 640 x 360 PNG with the scene's own view transform -> <out dir>/ref_<name>.png (for people).
"""
import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
info = json.load(open(argv[0]))
OUT = argv[1]
SAMPLES = int(argv[2]) if len(argv) > 2 else 256
os.makedirs(OUT, exist_ok=True)
sc = bpy.context.scene
cam_data = bpy.data.cameras.new('ref_cam')
cam = bpy.data.objects.new('ref_cam', cam_data)
sc.collection.objects.link(cam)
sc.camera = cam
r = sc.render
r.engine = 'CYCLES'
sc.cycles.device = 'CPU'
sc.cycles.use_denoising = False
sc.cycles.use_adaptive_sampling = False
sc.cycles.max_bounces = 8
r.resolution_percentage = 100
r.film_transparent = False
res = {'samples': SAMPLES, 'view_transform': sc.view_settings.view_transform, 'exposure': sc.view_settings.exposure, 'views': {}}


def aim(at, look):
    cam.location = at
    d = Vector(look) - Vector(at)
    cam.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()


for v in info['views']:
    aim(v['at'], v['look'])
    if v['kind'] == 'picture':
        cam_data.sensor_fit = 'HORIZONTAL'
        cam_data.angle = math.radians(70.0)
        r.resolution_x, r.resolution_y = 640, 360
        sc.cycles.samples = 96
        r.image_settings.file_format = 'PNG'
        r.filepath = os.path.join(OUT, 'ref_%s.png' % v['name'])
        bpy.ops.render.render(write_still=True)
        continue
    cam_data.angle = math.radians(20.0)
    r.resolution_x, r.resolution_y = 48, 48
    sc.cycles.samples = SAMPLES * (3 if v['kind'] in ('bay',) else 1)
    r.image_settings.file_format = 'OPEN_EXR'
    r.image_settings.color_depth = '32'
    f = os.path.join(OUT, 'ref_%s.exr' % v['name'])
    r.filepath = f
    bpy.ops.render.render(write_still=True)
    img = bpy.data.images.load(f)
    w, h = img.size
    a = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(a)
    bpy.data.images.remove(img)
    a = a.reshape(h, w, 4)[..., :3]
    c = a[h // 4: 3 * h // 4, w // 4: 3 * w // 4]
    res['views'][v['name']] = {'kind': v['kind'], 'linear_rgb': [float(x) for x in c.reshape(-1, 3).mean(0)],
                               'linear_std': float(c.reshape(-1, 3).mean(1).std()), **{k: v[k] for k in ('reflectance', 'bay', 'expect', 'strength') if k in v}}
    os.remove(f)
json.dump(res, open(os.path.join(OUT, 'reference.json'), 'w'), indent=1)
print('REFERENCE DONE', len(res['views']), 'views')
