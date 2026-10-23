"""Top-down overview of any scene: bounding box, size of the mesh objects, a height histogram of the faces that face up
(where the floors are) and an orthographic Workbench picture with a metre grid.
    blender -b <scene.blend> --python overview.py -- <out.png> [x0 y0 x1 y1]     -> <out.png>, <out.png>.json
"""
import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
out = argv[0]
box = [float(v) for v in argv[1:5]] if len(argv) >= 5 else None
zcut = float(argv[5]) if len(argv) >= 6 else None          # camera at this height: what is above it is not drawn
sc = bpy.context.scene
dg = bpy.context.evaluated_depsgraph_get()
pts, ups = [], []
meshes = [o for o in sc.objects if o.type == 'MESH' and not o.hide_render]
for o in meshes:
    me = o.evaluated_get(dg).data
    n = len(me.vertices)
    if not n:
        continue
    co = np.empty(n * 3, np.float32); me.vertices.foreach_get('co', co)
    M = np.array(o.matrix_world, np.float32)
    w = co.reshape(-1, 3) @ M[:3, :3].T + M[:3, 3]
    pts.append(w[:: max(1, n // 200)])
    if len(me.polygons):
        nf = len(me.polygons)
        c = np.empty(nf * 3, np.float32); me.polygons.foreach_get('center', c)
        nn = np.empty(nf * 3, np.float32); me.polygons.foreach_get('normal', nn)
        c = c.reshape(-1, 3) @ M[:3, :3].T + M[:3, 3]
        nn = nn.reshape(-1, 3) @ np.linalg.inv(M[:3, :3])
        nn /= np.maximum(np.linalg.norm(nn, axis=1, keepdims=True), 1e-9)
        ups.append(c[nn[:, 2] > 0.8][:: max(1, nf // 400)])
P = np.concatenate(pts) if pts else np.zeros((1, 3))
U = np.concatenate(ups) if ups else np.zeros((1, 3))
lo, hi = P.min(0), P.max(0)
if box is None:
    # the dense middle: 2nd..98th percentile, so a far sky dome does not decide the picture
    box = [float(np.percentile(P[:, 0], 2)), float(np.percentile(P[:, 1], 2)), float(np.percentile(P[:, 0], 98)), float(np.percentile(P[:, 1], 98))]
hist, edges = np.histogram(U[:, 2], bins=24)
info = {'objects': len(meshes), 'lights': sum(1 for o in sc.objects if o.type == 'LIGHT'), 'bbox_lo': lo.round(1).tolist(), 'bbox_hi': hi.round(1).tolist(),
        'view_box': [round(b, 1) for b in box], 'floor_heights': [[round(float(edges[i]), 1), int(hist[i])] for i in range(len(hist)) if hist[i]],
        'unit_scale': sc.unit_settings.scale_length}
# camera straight down over the view box
cx, cy = (box[0] + box[2]) / 2, (box[1] + box[3]) / 2
size = max(box[2] - box[0], box[3] - box[1]) * 1.05
cam_d = bpy.data.cameras.new('ov'); cam_d.type = 'ORTHO'; cam_d.ortho_scale = size; cam_d.clip_end = 1e6
cam = bpy.data.objects.new('ov', cam_d); sc.collection.objects.link(cam)
cam.location = (cx, cy, zcut if zcut is not None else float(hi[2]) + 50.0); cam.rotation_euler = (0, 0, 0)
cam_d.clip_start = 0.05
sc.camera = cam
if os.environ.get('OVERVIEW_ENGINE', 'eevee') == 'workbench':
    # fast, but draws each material's ACTIVE image node (often a normal map or mask), not what its nodes make
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'; sc.display.shading.color_type = 'TEXTURE'
    sc.display.shading.show_cavity = True
else:
    # the materials' real node graphs, as Material Preview shows them
    sc.render.engine = 'BLENDER_EEVEE_NEXT' if 'BLENDER_EEVEE_NEXT' in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items.keys() else 'BLENDER_EEVEE'
    try:
        sc.eevee.taa_render_samples = 16
    except AttributeError:
        pass
sc.render.resolution_x = sc.render.resolution_y = 1400
sc.render.filepath = out
bpy.ops.render.render(write_still=True)
info['camera'] = {'centre': [round(cx, 1), round(cy, 1)], 'size_m': round(size, 1), 'm_per_px': round(size / 1400, 3)}
json.dump(info, open(out + '.json', 'w'), indent=1)
print('OVERVIEW ' + json.dumps(info))
