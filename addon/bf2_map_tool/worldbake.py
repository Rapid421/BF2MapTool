"""The scene's World as the game's sky: the World is rendered ALONE to an equirectangular panorama, exactly as Blender
shades it (an HDRI with its rotation and strength, a Sky Texture with its sun disc, a plain colour, any node setup).

    bake(scene, out_path, width) -> {'file', 'width', 'height', 'mean', 'max'} or None when the scene has no World

The picture is scene-linear radiance (no view transform, no exposure), Radiance .hdr.
Layout (measured by tests/lightmatch/test_worldbake.py with a World that shows its own direction as colour):
    column u = 0 .. 1 left to right:  azimuth = pi - 2 pi u, measured from +Y towards +X
                                      (centre column looks along -Y ... see DIRECTION below, the test fixes the sign)
    row    v = 0 .. 1 top to bottom:  elevation = pi / 2 - pi v   (top row = straight up, +Z)
`direction(u, v)` below is the single place that states it; the runtime uses the same function.
"""
import math
import os

import bpy

SAMPLES = 16


def direction(u, v):
    """world direction (x, y, z, Blender axes, z up) of panorama position u, v in 0..1 (u right, v down)"""
    az = (0.5 - u) * 2.0 * math.pi          # 0 at the centre column
    el = (0.5 - v) * math.pi
    return (-math.sin(az) * math.cos(el), math.cos(az) * math.cos(el), math.sin(el))


def bake(scene, out_path, width=2048, log=print):
    world = scene.world
    if world is None:
        return None
    tmp = bpy.data.scenes.new('bf2_world_bake')
    cam_data = bpy.data.cameras.new('bf2_world_cam')
    cam = bpy.data.objects.new('bf2_world_cam', cam_data)
    try:
        tmp.world = world
        tmp.collection.objects.link(cam)
        tmp.camera = cam
        cam.rotation_euler = (math.pi / 2.0, 0.0, 0.0)          # looks along +Y, +Z up
        cam_data.type = 'PANO'
        if hasattr(cam_data, 'panorama_type'):
            cam_data.panorama_type = 'EQUIRECTANGULAR'
        else:
            cam_data.cycles.panorama_type = 'EQUIRECTANGULAR'
        r = tmp.render
        r.engine = 'CYCLES'
        tmp.cycles.device = 'CPU'
        tmp.cycles.samples = SAMPLES
        tmp.cycles.use_denoising = False
        tmp.cycles.use_adaptive_sampling = False
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
        log('world bake: no file was written')
        return None
    return {'file': out_path.replace('\\', '/'), 'width': int(width), 'height': int(width) // 2}
