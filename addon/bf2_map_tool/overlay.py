"""Viewport overlay: what will be SOLID in game, by the same rule as the build's collision stage.

  green    exact    the object's own triangles (faces with a lava-class material are left out)
  blue     custom   the collision mesh that replaces them
  orange   hull     one convex shape around the object
  yellow   door     a door panel: solid, and it moves
  red      kill volumes (wire boxes)
An object set to None draws nothing: what shows no colour is not solid. `solid_triangles` is the data behind the
picture; tests/test_overlay.py compares it with the collision the build wrote.
"""
import bmesh
import bpy
import numpy as np
from bpy.props import BoolProperty

COLORS = {'exact': (0.15, 0.85, 0.30, 0.30), 'custom': (0.20, 0.50, 1.00, 0.40), 'hull': (1.00, 0.55, 0.10, 0.40),
          'door': (1.00, 0.90, 0.10, 0.40), 'kill': (1.00, 0.10, 0.10, 1.00)}
NO_COLLISION_CLASSES = ('lava', 'holo')        # = runtime stages/collision.py
_handle = None
_cache = {'tris': [], 'lines': None, 'count': {}}


def _X():
    try:
        from . import export_scene
    except ImportError:                 # run as a plain script by the tests
        import export_scene
    return export_scene


def hull(tris):
    """triangles (H, 3, 3) of the convex hull of a triangle soup, or None for a flat / empty one"""
    pts = np.unique(np.round(np.asarray(tris, np.float64).reshape(-1, 3), 4), axis=0)
    if len(pts) < 4:
        return None
    bm = bmesh.new()
    try:
        for p in pts:
            bm.verts.new(p)
        r = bmesh.ops.convex_hull(bm, input=bm.verts)
        faces = [f for f in r['geom'] if isinstance(f, bmesh.types.BMFace)]
        if not faces:
            return None
        bmesh.ops.triangulate(bm, faces=faces)
        bm.faces.ensure_lookup_table()
        out = np.array([[v.co[:] for v in f.verts] for f in bm.faces if len(f.verts) == 3], np.float64)
    finally:
        bm.free()
    return out if len(out) else None


def solid_triangles(context=None):
    """[(kind, object name, world triangles (T, 3, 3))] for everything the build makes solid, from the scene as it is"""
    X = _X()
    context = context or bpy.context
    scene = context.scene
    dg = context.evaluated_depsgraph_get()
    vl = set(o.name for o in context.view_layer.objects)
    objs = [o for o in scene.objects if o.name in vl]
    col_mesh = set()
    for o in objs:
        if o.type == 'MESH' and o.bf2.role in ('static', 'door') and o.bf2.collision == 'custom':
            c = X.find_collision_object(o, objs)
            if c is not None:
                col_mesh.add(c.name)
    out = []
    for o in sorted(objs, key=lambda x: x.name):
        b = o.bf2
        if b.role not in ('static', 'door') or o.name in col_mesh or o.hide_render:
            continue
        if o.type != 'MESH' and not (o.type in ('CURVE', 'SURFACE', 'FONT', 'META') and b.role == 'static'):
            continue
        if b.role == 'door':                       # a door gets one convex shape where it is built
            a = X.mesh_arrays(o, dg)
            h = hull(a['pos']) if a is not None else None
            if h is not None:
                out.append(('door', o.name, h))
            continue
        if b.collision == 'none':
            continue
        if b.collision == 'custom':
            c = X.find_collision_object(o, objs)
            a = X.mesh_arrays(c, dg) if c is not None else None
            if a is not None:
                out.append(('custom', o.name, a['pos']))
            continue
        a = X.mesh_arrays(o, dg)
        if a is None:
            continue
        if b.collision == 'hull':
            h = hull(a['pos'])
            if h is not None:
                out.append(('hull', o.name, h))
            continue
        lava = np.array([bool(n) and n in bpy.data.materials and bpy.data.materials[n].bf2.cls in NO_COLLISION_CLASSES for n in map(str, a['slots'])], bool)
        t = a['pos'][~lava[a['mat']]]
        if len(t):
            out.append(('exact', o.name, t))
    return out


def kill_boxes(context=None):
    """line segments (N, 2, 3) of the kill volumes' boxes"""
    context = context or bpy.context
    E = ((0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7))
    seg = []
    for o in context.view_layer.objects:
        if o.bf2.role == 'kill_volume':
            c = [np.array(o.matrix_world @ __import__('mathutils').Vector(v)) for v in o.bound_box]
            seg += [(c[i], c[j]) for i, j in E]
    return np.array(seg, np.float32).reshape(-1, 2, 3)


def rebuild(context=None):
    """read the scene again and make the draw batches (called when the overlay is switched on and by Refresh)"""
    import gpu
    from gpu_extras.batch import batch_for_shader
    sh = gpu.shader.from_builtin('UNIFORM_COLOR')
    by_kind, count = {}, {}
    for kind, _, t in solid_triangles(context):
        n = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])
        n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
        by_kind.setdefault(kind, []).append((t + n[:, None, :] * 0.03).reshape(-1, 3))        # 3 cm off the surface: less flicker
        count[kind] = count.get(kind, 0) + len(t)
    _cache['tris'] = [(COLORS[k], batch_for_shader(sh, 'TRIS', {'pos': np.concatenate(v).astype(np.float32)})) for k, v in by_kind.items()]
    seg = kill_boxes(context)
    _cache['lines'] = batch_for_shader(sh, 'LINES', {'pos': seg.reshape(-1, 3)}) if len(seg) else None
    _cache['count'] = count


def _draw():
    import gpu
    sh = gpu.shader.from_builtin('UNIFORM_COLOR')
    gpu.state.blend_set('ALPHA')
    gpu.state.depth_test_set('LESS_EQUAL')
    gpu.state.depth_mask_set(False)
    gpu.state.face_culling_set('NONE')
    sh.bind()
    for color, batch in _cache['tris']:
        sh.uniform_float('color', color)
        batch.draw(sh)
    if _cache['lines'] is not None:
        gpu.state.line_width_set(2.0)
        sh.uniform_float('color', COLORS['kill'])
        _cache['lines'].draw(sh)
        gpu.state.line_width_set(1.0)
    gpu.state.depth_mask_set(True)
    gpu.state.depth_test_set('NONE')
    gpu.state.blend_set('NONE')


def _toggle(self, context):
    global _handle
    if self.bf2_show_collision and _handle is None:
        rebuild(context)
        _handle = bpy.types.SpaceView3D.draw_handler_add(_draw, (), 'WINDOW', 'POST_VIEW')
    elif not self.bf2_show_collision and _handle is not None:
        bpy.types.SpaceView3D.draw_handler_remove(_handle, 'WINDOW')
        _handle = None
    for area in context.screen.areas if context.screen else ():
        if area.type == 'VIEW_3D':
            area.tag_redraw()


class BF2_OT_overlay_refresh(bpy.types.Operator):
    bl_idname = 'bf2.overlay_refresh'
    bl_label = 'Refresh'
    bl_description = 'Read the scene again for the collision overlay (after moving objects or changing a Collision setting)'

    def execute(self, context):
        rebuild(context)
        for area in context.screen.areas:
            if area.type == 'VIEW_3D':
                area.tag_redraw()
        return {'FINISHED'}


def draw_ui(context, col):
    wm = context.window_manager
    row = col.row(align=True)
    row.prop(wm, 'bf2_show_collision', toggle=True, icon='MOD_PHYSICS')
    if wm.bf2_show_collision:
        row.operator('bf2.overlay_refresh', text='', icon='FILE_REFRESH')
        c = _cache['count']
        col.label(text='Solid triangles: %d exact (green), %d custom (blue)' % (c.get('exact', 0), c.get('custom', 0)))
        col.label(text='%d hull (orange), %d door (yellow); kill volumes red' % (c.get('hull', 0), c.get('door', 0)))
        col.label(text='No colour = not solid in game.')


# ------------------------------------------------------------------ the build, read back (buildview.py)
#   navmesh   cyan = the main part, orange = parts not connected to it that hold spawns or finder areas, grey = the rest
#   spawns    a 1.8 m post in the team colour (blue team 1, red team 2, white neutral) with a tick the way it faces, on
#             a ring: green = on the main part, orange = on an island, magenta = no navmesh under the feet
#   finders   outline in the team colour at its own height; when it covers navmesh floors at other heights, yellow
#             posts down / up to them and the outline again at the lowest and highest floor it reaches
#   play      thin white outline of each play / defend area
BV_COLORS = {'main': (0.10, 0.80, 0.95, 0.35), 'island': (1.00, 0.45, 0.00, 0.50), 'other': (0.55, 0.55, 0.55, 0.20),
             'team1': (0.25, 0.50, 1.00, 1.0), 'team2': (1.00, 0.25, 0.25, 1.0), 'neutral': (0.92, 0.92, 0.92, 1.0),
             'ok': (0.20, 1.00, 0.20, 1.0), 'island_spawn': (1.00, 0.50, 0.00, 1.0), 'off': (1.00, 0.00, 1.00, 1.0),
             'reach': (1.00, 0.90, 0.10, 1.0), 'play': (1.00, 1.00, 1.00, 0.35)}
_bv = {'handle': None, 'R': None, 'tris': [], 'lines': [], 'error': '', 'lines_text': []}


def _ring(c, r, n=12):
    a = np.linspace(0, 2 * np.pi, n + 1)
    p = np.stack([c[0] + r * np.cos(a), c[1] + r * np.sin(a), np.full(n + 1, c[2])], 1)
    return np.stack([p[:-1], p[1:]], 1)


def _loop(P, dz=0.0):
    P = np.asarray(P, np.float64) + (0.0, 0.0, dz)
    return np.stack([P, np.roll(P, -1, 0)], 1)


def build_segments(R):
    """({colour key: (S, 2, 3) line segments}, {colour key: (T*3, 3) triangle corners}) of a buildview.load() result"""
    seg, tri = {}, {}

    def add(k, s):
        if len(s):
            seg.setdefault(k, []).append(np.asarray(s, np.float64).reshape(-1, 2, 3))
    for k, t in R['nav'].items():
        if len(t):
            n = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])
            n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
            n *= np.where(n[:, 2:3] < 0, -1.0, 1.0)                           # lifted towards the up side
            tri[k] = (t + n[:, None, :] * 0.08).reshape(-1, 3)
            add(k + '_edge', np.concatenate([t[:, [0, 1]], t[:, [1, 2]], t[:, [2, 0]]]) + (0.0, 0.0, 0.08))
    for s in R['spawns']:
        p, d = np.array(s['pos']), np.array(s.get('fwd') or (0.0, -1.0, 0.0))
        add(s['team'], [[p, p + (0, 0, 1.8)], [p + (0, 0, 1.6), p + (0, 0, 1.6) + d * 0.7]])
        add({'main': 'ok', 'island': 'island_spawn', 'off': 'off'}[s['status']], _ring(p + (0, 0, 0.05), 0.6))
    for f in R['finders']:
        P = np.array(f['outline'])
        add(f['team'], _loop(P, 0.1))
        fr = f.get('floor_range_blender')
        if fr and (fr[1] - fr[0] > 0.5 or abs(fr[0] - P[0, 2]) > 0.5):
            lo, hi = min(fr[0], P[:, 2].min()), max(fr[1], P[:, 2].max())
            for z in sorted({round(fr[0], 2), round(fr[1], 2)}):
                Q = P.copy(); Q[:, 2] = z
                add('reach', _loop(Q, 0.1))
            add('reach', [[(q[0], q[1], lo), (q[0], q[1], hi)] for q in P[::max(1, len(P) // 8)]])
    for a in R['areas']:
        add('play', _loop(np.array(a['outline']), 0.0))
    return {k: np.concatenate(v) for k, v in seg.items()}, tri


def build_rebuild(context=None, work=None):
    """read the build again (the work folder of the scene's level, or `work`) and make the draw batches"""
    from . import buildview
    _bv['tris'], _bv['lines'], _bv['R'], _bv['error'], _bv['lines_text'] = [], [], None, '', []
    if work is None:
        from . import build
        work = build.work_folder(context or bpy.context)
    if not work:
        _bv['error'] = 'Set the work folder and the level name first.'
        return None
    try:
        R = buildview.load(work)
    except buildview.NoBuild as e:
        _bv['error'] = str(e)
        return None
    _bv['R'] = R
    _bv['lines_text'] = buildview.summary(R)
    seg, tri = build_segments(R)
    import gpu
    from gpu_extras.batch import batch_for_shader
    sh = gpu.shader.from_builtin('UNIFORM_COLOR')
    lsh = gpu.shader.from_builtin('POLYLINE_UNIFORM_COLOR')
    _bv['tris'] = [(BV_COLORS[k], batch_for_shader(sh, 'TRIS', {'pos': v.astype(np.float32)})) for k, v in tri.items()]
    for k, v in seg.items():
        edge = k.endswith('_edge')
        col = BV_COLORS[k[:-5] if edge else k]
        if edge:
            col = (col[0], col[1], col[2], min(1.0, col[3] * 2.0))
        _bv['lines'].append((col, 1.0 if edge or k == 'play' else 3.0, edge,
                             batch_for_shader(lsh, 'LINES', {'pos': v.reshape(-1, 3).astype(np.float32)})))
    return R


def draw_build():
    """the draw callback (POST_VIEW); tests/addon/test_buildview_picture.py calls it into an off-screen picture"""
    import gpu
    sh = gpu.shader.from_builtin('UNIFORM_COLOR')
    lsh = gpu.shader.from_builtin('POLYLINE_UNIFORM_COLOR')
    gpu.state.blend_set('ALPHA')
    gpu.state.face_culling_set('NONE')
    gpu.state.depth_mask_set(False)
    sh.bind()
    # the navmesh: faint through walls (a part under a roof still shows), full where it is in sight
    for depth, k in (('NONE', 0.3), ('LESS_EQUAL', 1.0)):
        gpu.state.depth_test_set(depth)
        for color, batch in _bv['tris']:
            sh.uniform_float('color', (color[0], color[1], color[2], color[3] * k))
            batch.draw(sh)
    vp = gpu.state.viewport_get()
    lsh.bind()
    lsh.uniform_float('viewportSize', (float(vp[2]), float(vp[3])))
    for color, width, depth, batch in _bv['lines']:
        gpu.state.depth_test_set('LESS_EQUAL' if depth else 'NONE')       # spawns and finder areas show through walls
        lsh.uniform_float('lineWidth', width)
        lsh.uniform_float('color', color)
        batch.draw(lsh)
    gpu.state.depth_mask_set(True)
    gpu.state.depth_test_set('NONE')
    gpu.state.blend_set('NONE')


def _toggle_build(self, context):
    if self.bf2_show_build and _bv['handle'] is None:
        build_rebuild(context)
        _bv['handle'] = bpy.types.SpaceView3D.draw_handler_add(draw_build, (), 'WINDOW', 'POST_VIEW')
    elif not self.bf2_show_build and _bv['handle'] is not None:
        bpy.types.SpaceView3D.draw_handler_remove(_bv['handle'], 'WINDOW')
        _bv['handle'] = None
    for area in context.screen.areas if context.screen else ():
        if area.type == 'VIEW_3D':
            area.tag_redraw()


class BF2_OT_build_view_refresh(bpy.types.Operator):
    bl_idname = 'bf2.build_view_refresh'
    bl_label = 'Read the build again'
    bl_description = 'Read the navmesh, spawns and spawn-finder areas of the last build again from the work folder'

    def execute(self, context):
        build_rebuild(context)
        for area in context.screen.areas if context.screen else ():
            if area.type == 'VIEW_3D':
                area.tag_redraw()
        if _bv['error']:
            self.report({'WARNING'}, _bv['error'])
        return {'FINISHED'}


def draw_build_ui(context, col):
    wm = context.window_manager
    row = col.row(align=True)
    row.prop(wm, 'bf2_show_build', toggle=True, icon='MOD_MESHDEFORM')
    if not wm.bf2_show_build:
        return
    row.operator('bf2.build_view_refresh', text='', icon='FILE_REFRESH')
    if _bv['error']:
        col.label(text=_bv['error'][:120], icon='ERROR')
        return
    for k, line in enumerate(_bv['lines_text'][:12]):
        col.label(text=line[:120], icon='ERROR' if k >= 3 else 'NONE')
    if len(_bv['lines_text']) > 12:
        col.label(text='... and %d more' % (len(_bv['lines_text']) - 12))
    col.label(text='Navmesh: cyan = main part, orange = cut-off parts. Spawn rings: green = on the main part,')
    col.label(text='orange = on a cut-off part, magenta = no navmesh. Yellow = a finder area reaching other floors.')


CLASSES = (BF2_OT_overlay_refresh, BF2_OT_build_view_refresh)


def register_props():
    bpy.types.WindowManager.bf2_show_collision = BoolProperty(
        name='Show what is solid', description='Draw in the viewport what the build makes solid: the same rule the collision stage uses',
        default=False, update=_toggle)
    bpy.types.WindowManager.bf2_show_build = BoolProperty(
        name='Show the build', description='Draw where the last build really put the navmesh, the spawn points and the spawn-finder '
        'areas (read back from the work folder)', default=False, update=_toggle_build)


def unregister_props():
    global _handle
    if _handle is not None:
        bpy.types.SpaceView3D.draw_handler_remove(_handle, 'WINDOW')
        _handle = None
    if _bv['handle'] is not None:
        bpy.types.SpaceView3D.draw_handler_remove(_bv['handle'], 'WINDOW')
        _bv['handle'] = None
    for p in ('bf2_show_collision', 'bf2_show_build'):
        if hasattr(bpy.types.WindowManager, p):
            delattr(bpy.types.WindowManager, p)
