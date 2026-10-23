"""Read-only: exact size of the 2005 command post model (com_bldg_controlzone, 1.7 m wide on every correctly scaled OG
blend) and the scale of every root object - to find a blend saved at the wrong scale.
    blender -b <file.blend> --python measure_scale.py
"""
import bpy
cps = [o for o in bpy.context.scene.objects if o.type == 'MESH' and o.name.lower().startswith('com_bldg_controlzone')]
if cps:
    d = cps[0].dimensions
    print('CP %s dims %.4f %.4f %.4f  -> factor to 1.7 m: %.4f' % (cps[0].name, d.x, d.y, d.z, 1.7 / max(d.x, 1e-9)))
roots = [o for o in bpy.context.scene.objects if o.parent is None]
sc = {}
for o in roots:
    k = tuple(round(v, 4) for v in o.scale)
    sc[k] = sc.get(k, 0) + 1
print('root objects %d; scales %s' % (len(roots), sorted(sc.items(), key=lambda kv: -kv[1])[:6]))
print('unit scale', bpy.context.scene.unit_settings.scale_length)
