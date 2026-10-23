"""What the scene looks like from one point, all around: the picture the game's reflections are made from.

    capture(scene, point, out_path, width) -> {'file', 'width', 'height', 'point'} or None

The whole scene (its meshes, lamps and World) is rendered with Cycles to an equirectangular panorama from `point`.
Scene-linear radiance, no view transform, Radiance .hdr. Same layout as worldbake (`worldbake.direction`).
The scene itself is not touched: a temporary scene links the same collections and objects.
"""
import math
import os

import bpy

SAMPLES = 48


EYE = 1.6            # height of the capture above a floor point
CLEAR = 0.6          # free space wanted around it


def pick_point(scene, floor_points, centre):
    """a free point at eye height: over the play centre if that is clear, else between the centre and a floor point
    (spawn, command post), else over one of them. -> (point, clearance in metres)"""
    from mathutils import Vector
    dg = bpy.context.evaluated_depsgraph_get()
    c = Vector(centre)
    cands = [c + Vector((0, 0, EYE))]
    pts = sorted((Vector(q) for q in floor_points), key=lambda q: (q - c).length)
    cands += [(c + q) / 2 + Vector((0, 0, EYE)) for q in pts] + [q + Vector((0, 0, EYE)) for q in pts]
    dirs = [Vector(d).normalized() for d in ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1), (1, 1, 0.4), (-1, 1, 0.4),
                                             (1, -1, 0.4), (-1, -1, 0.4), (1, 1, -0.4), (-1, 1, -0.4), (1, -1, -0.4), (-1, -1, -0.4))]
    best = None
    for q in cands[:60]:
        free = 1e9
        for d in dirs:
            hit, loc, _n, _i, _o, _m = scene.ray_cast(dg, q, d, distance=50.0)
            if hit:
                free = min(free, (loc - q).length)
        if best is None or free > best[1]:
            best = (q, free)
        if free >= CLEAR:
            break
    return [float(v) for v in best[0]], float(min(best[1], 50.0))


def capture(scene, point, out_path, width=512, log=print):
    tmp = bpy.data.scenes.new('bf2_refl_capture')
    cam_data = bpy.data.cameras.new('bf2_refl_cam')
    cam = bpy.data.objects.new('bf2_refl_cam', cam_data)
    try:
        tmp.world = scene.world
        for c in scene.collection.children:
            tmp.collection.children.link(c)
        for o in scene.collection.objects:
            tmp.collection.objects.link(o)
        tmp.collection.objects.link(cam)
        tmp.camera = cam
        cam.location = tuple(float(x) for x in point)
        cam.rotation_euler = (math.pi / 2.0, 0.0, 0.0)          # looks along +Y, +Z up (as worldbake)
        cam_data.type = 'PANO'
        cam_data.clip_start, cam_data.clip_end = 0.02, 100000.0
        if hasattr(cam_data, 'panorama_type'):
            cam_data.panorama_type = 'EQUIRECTANGULAR'
        else:
            cam_data.cycles.panorama_type = 'EQUIRECTANGULAR'
        r = tmp.render
        r.engine = 'CYCLES'
        tmp.cycles.device = 'CPU'
        tmp.cycles.samples = SAMPLES
        tmp.cycles.use_adaptive_sampling = True
        try:
            tmp.cycles.use_denoising = True
        except Exception:
            pass
        r.resolution_x, r.resolution_y, r.resolution_percentage = int(width), int(width) // 2, 100
        r.film_transparent = False
        r.image_settings.file_format = 'HDR'
        r.image_settings.color_mode = 'RGB'
        tmp.view_settings.view_transform = 'Standard'
        tmp.view_settings.look = 'None'
        tmp.view_settings.exposure = 0.0
        tmp.view_settings.gamma = 1.0
        r.filepath = out_path
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
        bpy.ops.render.render(scene=tmp.name, write_still=True)
    finally:
        bpy.data.scenes.remove(tmp)
        bpy.data.objects.remove(cam)
        bpy.data.cameras.remove(cam_data)
    if not os.path.exists(out_path):
        log('reflection capture: no file was written')
        return None
    return {'file': out_path.replace('\\', '/'), 'width': int(width), 'height': int(width) // 2, 'point': [float(x) for x in point]}


MENU_SECONDS = 60.0       # render time limit of the loading picture (Cycles stops there and keeps what it has)


def menu_camera(scene):
    """the camera the loading picture is taken with: one named "Menu camera" (any case), else the scene camera"""
    for o in scene.objects:
        if o.type == 'CAMERA' and o.name.strip().lower() in ('menu camera', 'menu_camera', 'menucamera'):
            return o
    return scene.camera if scene.camera is not None and scene.camera.type == 'CAMERA' else None


def menu_picture(scene, cam, out_path, log=print):
    """the loading-screen picture: 1920 x 1080 from `cam`, Cycles on the CPU with denoising, at most MENU_SECONDS"""
    tmp = bpy.data.scenes.new('bf2_menu_capture')
    try:
        tmp.world = scene.world
        for c in scene.collection.children:
            tmp.collection.children.link(c)
        for o in scene.collection.objects:
            tmp.collection.objects.link(o)
        tmp.camera = cam
        r = tmp.render
        r.engine = 'CYCLES'
        tmp.cycles.device = 'CPU'
        tmp.cycles.samples = 64
        tmp.cycles.use_adaptive_sampling = True
        if hasattr(tmp.cycles, 'time_limit'):
            tmp.cycles.time_limit = MENU_SECONDS
        try:
            tmp.cycles.use_denoising = True
        except Exception:
            pass
        r.resolution_x, r.resolution_y, r.resolution_percentage = 1920, 1080, 100
        r.film_transparent = False
        r.image_settings.file_format = 'PNG'
        r.image_settings.color_mode = 'RGB'
        tmp.view_settings.view_transform = scene.view_settings.view_transform
        tmp.view_settings.look = scene.view_settings.look
        tmp.view_settings.exposure = scene.view_settings.exposure
        r.filepath = out_path
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
        bpy.ops.render.render(scene=tmp.name, write_still=True)
    finally:
        bpy.data.scenes.remove(tmp)
    if not os.path.exists(out_path):
        log('loading picture: no file was written')
        return None
    return {'file': os.path.basename(out_path), 'camera': cam.name, 'width': 1920, 'height': 1080}
