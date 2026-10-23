"""Put a shipped BFII sky on a scene (saved as a COPY): the HDR sky mode with DICE's panorama, and the scene's sun lamp
aimed at the panorama's own sun (tools/dice_sky.py measures it) in DICE's sun colour - the real-time sky, the baked
sky and the sun then all agree.
    blender -b <scene.blend> --python apply_dice_sky.py -- <addon dir> <out.blend> <sky.hdr> <sun_u> <sun_elev_deg> <r> <g> <b>
Panorama column u of a game direction = -azimuth / 360 (mod 1), azimuth = atan2(x, z) in game axes (look.pano_u, PANO_U0 0).
Scene (x, y, z) = game (x, -z, y).
"""
import math
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
ADDON, OUT, SKY = argv[0], argv[1], argv[2]
u, elev = float(argv[3]), math.radians(float(argv[4]))
rgb = tuple(float(v) for v in argv[5:8])
sys.path.insert(0, ADDON)
import props
try:
    props.register()
except Exception:
    pass
sc = bpy.context.scene
b = sc.bf2
b.sky = 'hdr'
b.sky_hdr = SKY
b.sky_strength = 1.0
b.sun_on = True
az = -u * 2.0 * math.pi                                    # the inverse of look.pano_u
g = Vector((math.sin(az) * math.cos(elev), math.sin(elev), math.cos(az) * math.cos(elev)))     # game direction to the sun
to_sun = Vector((g.x, -g.z, g.y))                          # scene axes
suns = [o for o in sc.objects if o.type == 'LIGHT' and o.data.type == 'SUN']
if suns:
    sun = max(suns, key=lambda o: o.data.energy)
else:
    sun = bpy.data.objects.new('DICE_Sun', bpy.data.lights.new('DICE_Sun', 'SUN'))
    sc.collection.objects.link(sun)
    sun.data.energy = 4.0
sun.rotation_mode = 'QUATERNION'
sun.rotation_quaternion = to_sun.to_track_quat('Z', 'Y')  # a sun lamp shines along its -Z: its +Z points at the sun
sun.data.color = rgb
sun.hide_render = False
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
print('DICE SKY %s sun u %.4f elev %.1f -> scene dir %s, lamp %s' % (SKY, u, math.degrees(elev), tuple(round(v, 3) for v in to_sun), sun.name))
