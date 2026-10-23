"""Game look, rendered: the built scene with its own materials, then with "Game look" on (the packed game textures),
from the same camera, in a background Blender (EEVEE, no window).
    blender -b <work>/<Map>/export/scene_copy.blend --python test_gamelook_picture.py -- <work>/<Map> <out prefix>
-> <prefix>_blender.png, <prefix>_game.png, <prefix>.json
"""
import json
import math
import os
import sys
import traceback

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
work, prefix = argv[0], argv[1]
ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'addon'))
R = {'work': work}
try:
    import bf2_map_tool
    from bf2_map_tool import build, gamelook
    bf2_map_tool.register()
    build.work_folder = lambda context: work
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_EEVEE_NEXT' if 'BLENDER_EEVEE_NEXT' in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items] else 'BLENDER_EEVEE'
    sc.render.resolution_x, sc.render.resolution_y, sc.render.resolution_percentage = 1280, 720, 100
    from mathutils import Vector
    pts = [o.matrix_world @ Vector(c) for o in sc.objects if o.type == 'MESH' and o.bf2.role == 'static' and not o.hide_render for c in o.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    c = (lo + hi) / 2
    cam_d = bpy.data.cameras.new('F3 test camera')
    cam = bpy.data.objects.new('F3 test camera', cam_d)
    sc.collection.objects.link(cam)
    cam.location = c + Vector((0.35, -0.75, 0.45)) * (hi - lo).length * 0.55
    cam.rotation_euler = (c - cam.location).to_track_quat('-Z', 'Y').to_euler()
    sc.camera = cam
    for tag in ('blender', 'game'):
        if tag == 'game':
            made, kept, err = gamelook.apply(bpy.context, work)
            R['game_look'] = {'made': made, 'kept': kept, 'error': err}
            R['copies'] = {m.name: sorted(n.image.name for n in m.node_tree.nodes if n.type == 'TEX_IMAGE')
                           for m in bpy.data.materials if m.get('bf2_game_look_of')}
        sc.render.filepath = '%s_%s.png' % (prefix, tag)
        bpy.ops.render.render(write_still=True)
        R[tag] = sc.render.filepath
    R['restored_slots'] = gamelook.restore(bpy.context)
except Exception:
    R['error'] = traceback.format_exc()
with open(prefix + '.json', 'w') as f:
    json.dump(R, f, indent=1)
print('GAMELOOK_PICTURE', 'ERROR' if 'error' in R else 'OK', prefix)
