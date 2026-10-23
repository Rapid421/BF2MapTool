"""Blender side: is the World baked to a panorama with the layout worldbake.direction() states, and in scene-linear units?
    blender -b --factory-startup --python test_worldbake.py -- <addon dir> <out dir>
World 1 shows its own direction as colour (rgb = direction * 0.5 + 0.5): every pixel must decode to direction(u, v).
World 2 is a plain colour (0.2, 0.4, 0.8) x strength 3: every pixel must be (0.6, 1.2, 2.4).
Writes <out dir>/worldbake_test.json; the numbers are read from the .hdr files by plain Python afterwards too.
"""
import json
import math
import os
import sys

import bpy
import numpy as np

argv = sys.argv[sys.argv.index('--') + 1:]
sys.path.insert(0, argv[0])
OUT = argv[1]
os.makedirs(OUT, exist_ok=True)
import worldbake

sc = bpy.context.scene


def load(path):
    img = bpy.data.images.load(path)
    w, h = img.size
    a = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(a)
    bpy.data.images.remove(img)
    return a.reshape(h, w, 4)[::-1, :, :3]            # row 0 = top


res = {}
# world 1: direction as colour
w = bpy.data.worlds.new('dir_world'); w.use_nodes = True
nt = w.node_tree
bg = next(n for n in nt.nodes if n.type == 'BACKGROUND')
tc = nt.nodes.new('ShaderNodeTexCoord')
ma = nt.nodes.new('ShaderNodeVectorMath'); ma.operation = 'MULTIPLY_ADD'
ma.inputs[1].default_value = (0.5, 0.5, 0.5); ma.inputs[2].default_value = (0.5, 0.5, 0.5)
nt.links.new(tc.outputs['Generated'], ma.inputs[0]); nt.links.new(ma.outputs[0], bg.inputs['Color'])
bg.inputs['Strength'].default_value = 1.0
sc.world = w
f1 = os.path.join(OUT, 'world_dir.hdr')
res['bake1'] = worldbake.bake(sc, f1, 512)
a = load(f1)
h, wd = a.shape[:2]
err = []
for v in np.linspace(0.08, 0.92, 9):
    for u in np.linspace(0.03, 0.97, 16):
        px = a[int(v * h), int(u * wd)] * 2.0 - 1.0
        px = px / max(float(np.linalg.norm(px)), 1e-9)
        want = np.array(worldbake.direction((int(u * wd) + 0.5) / wd, (int(v * h) + 0.5) / h))
        err.append(math.degrees(math.acos(float(np.clip(px @ want, -1.0, 1.0)))))
res['direction_error_deg'] = {'max': round(max(err), 2), 'mean': round(float(np.mean(err)), 2), 'samples': len(err)}
# for the record: where the picture's centre, left quarter and top look
res['centre_pixel_direction'] = [round(float(x), 3) for x in (a[h // 2, wd // 2] * 2 - 1)]
res['left_quarter_direction'] = [round(float(x), 3) for x in (a[h // 2, wd // 4] * 2 - 1)]
res['top_direction'] = [round(float(x), 3) for x in (a[2, wd // 2] * 2 - 1)]
# world 2: plain colour x strength
w2 = bpy.data.worlds.new('flat_world'); w2.use_nodes = True
bg2 = next(n for n in w2.node_tree.nodes if n.type == 'BACKGROUND')
bg2.inputs['Color'].default_value = (0.2, 0.4, 0.8, 1.0); bg2.inputs['Strength'].default_value = 3.0
sc.world = w2
f2 = os.path.join(OUT, 'world_flat.hdr')
res['bake2'] = worldbake.bake(sc, f2, 256)
b = load(f2)
res['flat_mean_rgb'] = [round(float(x), 4) for x in b.mean((0, 1))]
res['flat_expected_rgb'] = [0.6, 1.2, 2.4]
res['pass'] = res['direction_error_deg']['max'] < 2.0 and max(abs(x - y) / y for x, y in zip(res['flat_mean_rgb'], res['flat_expected_rgb'])) < 0.02
json.dump(res, open(os.path.join(OUT, 'worldbake_test.json'), 'w'), indent=1)
print('WORLDBAKE', json.dumps(res))
