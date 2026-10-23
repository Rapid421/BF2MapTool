"""Picture of the build preview overlay, drawn by the add-on's own draw callback into an off-screen buffer of a
BACKGROUND Blender (no window: gpu.init()). The scene's meshes are drawn grey under it for context.
    blender -b --factory-startup [<scene.blend>] --python test_buildview_picture.py -- <work>/<Map> <out prefix>
-> <out prefix>_top.png (oblique, from above), <out prefix>_side.png (level, from the side: the heights) and
<out prefix>.json (what the overlay read and drew).
"""
import json
import math
import os
import sys
import traceback

import bpy
import numpy as np

argv = sys.argv[sys.argv.index('--') + 1:]
work, prefix = argv[0], argv[1]
ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'addon'))
R = {'work': work, 'scene': bpy.data.filepath, 'pictures': []}
W, H = 1600, 1000


def look_at(eye, target, up=(0.0, 0.0, 1.0)):
    from mathutils import Matrix, Vector
    eye, target, up = Vector(eye), Vector(target), Vector(up)
    f = (target - eye).normalized()
    s = f.cross(up).normalized()
    u = s.cross(f)
    m = Matrix(((s.x, s.y, s.z, -s.dot(eye)), (u.x, u.y, u.z, -u.dot(eye)), (-f.x, -f.y, -f.z, f.dot(eye)), (0, 0, 0, 1)))
    return m


def perspective(fov_y, aspect, near, far):
    from mathutils import Matrix
    t = 1.0 / math.tan(fov_y / 2)
    return Matrix(((t / aspect, 0, 0, 0), (0, t, 0, 0), (0, 0, (far + near) / (near - far), 2 * far * near / (near - far)), (0, 0, -1, 0)))


def scene_batch():
    """the scene's visible meshes, grey, lit from one side"""
    import gpu
    from gpu_extras.batch import batch_for_shader
    dg = bpy.context.evaluated_depsgraph_get()
    P, C = [], []
    L = np.array([0.4, -0.5, 0.75]); L /= np.linalg.norm(L)
    for o in bpy.context.view_layer.objects:
        if o.type != 'MESH' or o.hide_get() or o.get('bf2_reference') or o.bf2.role not in ('static', 'door'):
            continue
        me = o.evaluated_get(dg).to_mesh()
        me.calc_loop_triangles()
        n = len(me.loop_triangles)
        if n:
            v = np.empty(len(me.vertices) * 3, np.float32); me.vertices.foreach_get('co', v)
            t = np.empty(n * 3, np.int32); me.loop_triangles.foreach_get('vertices', t)
            mw = np.array(o.matrix_world, np.float32)
            v = v.reshape(-1, 3) @ mw[:3, :3].T + mw[:3, 3]
            tri = v[t.reshape(-1, 3)]
            nr = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
            nr /= np.maximum(np.linalg.norm(nr, axis=1, keepdims=True), 1e-12)
            g = 0.25 + 0.45 * np.abs(nr @ L)
            P.append(tri.reshape(-1, 3)); C.append(np.repeat(np.stack([g, g, g * 1.04, np.ones_like(g)], 1), 3, 0))
        o.evaluated_get(dg).to_mesh_clear()
    if not P:
        return None, None, 0
    sh = gpu.shader.from_builtin('SMOOTH_COLOR')
    return sh, batch_for_shader(sh, 'TRIS', {'pos': np.concatenate(P), 'color': np.concatenate(C).astype(np.float32)}), sum(len(p) for p in P) // 3


def picture(path, view, proj, scene):
    import gpu
    off = gpu.types.GPUOffScreen(W, H)
    with off.bind():
        fb = gpu.state.active_framebuffer_get()
        fb.clear(color=(0.08, 0.09, 0.11, 1.0), depth=1.0)
        gpu.matrix.load_matrix(view)
        gpu.matrix.load_projection_matrix(proj)
        if scene[1] is not None:
            gpu.state.depth_test_set('LESS_EQUAL'); gpu.state.depth_mask_set(True)
            scene[0].bind()
            scene[1].draw(scene[0])
        from bf2_map_tool import overlay
        overlay.draw_build()
        buf = fb.read_color(0, 0, W, H, 4, 0, 'FLOAT')
    off.free()
    buf.dimensions = W * H * 4
    img = bpy.data.images.new(os.path.basename(path), W, H, alpha=True)
    img.pixels.foreach_set(np.asarray(buf, np.float32))
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    R['pictures'].append(path)


try:
    import gpu
    gpu.init()
    import bf2_map_tool
    bf2_map_tool.register()
    from bf2_map_tool import overlay
    rr = overlay.build_rebuild(work=work)
    if rr is None:
        raise RuntimeError(overlay._bv['error'])
    R['summary'] = overlay._bv['lines_text']
    R['batches'] = {'triangle_sets': len(overlay._bv['tris']), 'line_sets': len(overlay._bv['lines'])}
    R['spawn_status'] = rr['spawn_counts']
    sc = scene_batch()
    R['scene_triangles'] = sc[2]
    pts = np.concatenate([np.array(rr['nav']['main']).reshape(-1, 3)] + [np.array([s['pos'] for s in rr['spawns']]).reshape(-1, 3)])
    lo, hi = pts.min(0), pts.max(0)
    c = (lo + hi) / 2
    r = float(np.linalg.norm(hi - lo)) / 2
    lo_all = min([lo[2]] + [f['floor_range_blender'][0] for f in rr['finders'] if f.get('floor_range_blender')])
    proj = perspective(math.radians(50), W / H, 0.5, 5000.0)
    picture(prefix + '_top.png', look_at(c + np.array([0.55, -0.75, 0.9]) * r * 1.6, c), proj, sc)
    cs = c.copy(); cs[2] = (lo_all + hi[2]) / 2
    span = max(r, (hi[2] - lo_all))
    picture(prefix + '_side.png', look_at(cs + np.array([0.15, -1.0, 0.12]) * span * 1.5, cs), proj, sc)
except Exception:
    R['error'] = traceback.format_exc()
with open(prefix + '.json', 'w') as f:
    json.dump(R, f, indent=1)
print('BUILDVIEW_PICTURE', 'ERROR' if 'error' in R else 'OK', prefix)
