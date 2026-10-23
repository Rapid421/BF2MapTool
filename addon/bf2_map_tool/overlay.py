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


CLASSES = (BF2_OT_overlay_refresh,)


def register_props():
    bpy.types.WindowManager.bf2_show_collision = BoolProperty(
        name='Show what is solid', description='Draw in the viewport what the build makes solid: the same rule the collision stage uses',
        default=False, update=_toggle)


def unregister_props():
    global _handle
    if _handle is not None:
        bpy.types.SpaceView3D.draw_handler_remove(_handle, 'WINDOW')
        _handle = None
    if hasattr(bpy.types.WindowManager, 'bf2_show_collision'):
        del bpy.types.WindowManager.bf2_show_collision
