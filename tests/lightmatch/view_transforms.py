"""How Blender's view transforms show the sunlit grey ramp: the display value of each patch under Standard, Filmic, AgX
and Khronos PBR Neutral.   blender -b <lightmatch.blend> --python view_transforms.py -- <views.json> <out.json>
Each ramp view is rendered once (scene-linear EXR); the same picture is then SAVED through each view transform, so the
numbers differ only by the transform. Used to give every transform its own exposure match in the game.
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
tmp = os.path.join(os.path.dirname(OUT), 'vt_tmp')
os.makedirs(tmp, exist_ok=True)
sc = bpy.context.scene
cam_data = bpy.data.cameras.new('vt_cam'); cam = bpy.data.objects.new('vt_cam', cam_data)
sc.collection.objects.link(cam); sc.camera = cam
cam_data.angle = math.radians(20.0)
r = sc.render
r.engine = 'CYCLES'; sc.cycles.device = 'CPU'; sc.cycles.samples = 128; sc.cycles.use_denoising = False
r.resolution_x = r.resolution_y = 32; r.resolution_percentage = 100
want = []
for n in ('Standard', 'Filmic', 'AgX', 'Khronos PBR Neutral'):        # the list is dynamic: keep the ones this Blender accepts
    try:
        sc.view_settings.view_transform = n
        want.append(n)
    except TypeError:
        pass
res = {'available': want, 'patches': []}


def px(path):
    img = bpy.data.images.load(path)
    w, h = img.size
    a = np.empty(w * h * 4, np.float32); img.pixels.foreach_get(a)
    bpy.data.images.remove(img)
    return a.reshape(h, w, 4)[h // 4: 3 * h // 4, w // 4: 3 * w // 4, :3].reshape(-1, 3).mean(0)


for v in info['views']:
    if v['kind'] != 'ramp_sun':
        continue
    cam.location = v['at']
    cam.rotation_euler = (Vector(v['look']) - Vector(v['at'])).to_track_quat('-Z', 'Y').to_euler()
    row = {'name': v['name'], 'reflectance': v['reflectance'], 'display_srgb': {}}
    for vt in want:
        sc.view_settings.view_transform = vt
        sc.view_settings.look = 'None'
        r.image_settings.file_format = 'PNG'
        f = os.path.join(tmp, '%s_%s.png' % (v['name'], vt.replace(' ', '_')))
        r.filepath = f
        bpy.ops.render.render(write_still=True)
        img = bpy.data.images.load(f)
        img.colorspace_settings.name = 'Non-Color'            # read the stored display values as they are
        w, h = img.size
        a = np.empty(w * h * 4, np.float32); img.pixels.foreach_get(a)
        bpy.data.images.remove(img)
        c = a.reshape(h, w, 4)[h // 4: 3 * h // 4, w // 4: 3 * w // 4, :3].reshape(-1, 3).mean(0)
        row['display_srgb'][vt] = [round(float(x) * 255, 1) for x in c]
    res['patches'].append(row)
json.dump(res, open(OUT, 'w'), indent=1)
print('VT', json.dumps(res['available']))
for p in res['patches']:
    print('VT %-6s refl %.2f  %s' % (p['name'], p['reflectance'], '  '.join('%s %3.0f %3.0f %3.0f' % ((k,) + tuple(v)) for k, v in p['display_srgb'].items())))
