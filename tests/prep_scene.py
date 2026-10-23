"""Do to any scene what a user of the add-on would do first, and save the result as a COPY (the source is only read):
map name / sky, then the add-on's own "Guess roles" and "Guess classes" - nothing by hand.
    blender -b <source.blend> --python prep_scene.py -- <out.blend> <addon dir> <report.json> <LevelName> [sky preset] [display name]
"""
import json
import os
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, ADDON, REPORT, NAME = argv[:4]
SKY = argv[4] if len(argv) > 4 else 'overcast'
SHOWN = argv[5] if len(argv) > 5 else NAME
sys.path.insert(0, ADDON)
import props
props.register()
import guess

sc = bpy.context.scene
# A 2005 blend saved at the wrong scale (OG Geonosis: its one root object at 0.1 - the map a tenth of its size in game).
# The command post model is 1.68 m wide in every correctly scaled 2005 map: off by more than 20 % -> the root objects
# are scaled about the world origin until it is
REF_CP = 1.6808
rescale = 1.0
cps = [o for o in sc.objects if o.type == 'MESH' and o.name.lower().startswith('com_bldg_controlzone')]
if cps:
    w = max(cps[0].dimensions.x, cps[0].dimensions.y, 1e-9)
    if abs(w / REF_CP - 1.0) > 0.2:
        rescale = REF_CP / w
        for o in [o for o in sc.objects if o.parent is None]:
            o.scale = tuple(v * rescale for v in o.scale)
            o.location = tuple(v * rescale for v in o.location)
        bpy.context.view_layer.update()
        print('PREP RESCALED x%.4f (command post %.3f m -> %.3f m)' % (rescale, w, REF_CP))
b = sc.bf2
b.name, b.display_name, b.planet = NAME, SHOWN, SHOWN
b.sky = SKY
gs = guess.guess_roles(list(bpy.context.view_layer.objects))
guess.apply(gs)
# the game holds 64 real-time lights (the build's check light.dynamic_budget refuses more): a scene with more keeps its 64
# strongest real-time and the rest become Baked - what a user does in the Lighting tab (OG Naboo: 71 lamps)
BUDGET = 64
lamps = [o for o in bpy.context.view_layer.objects if o.type == 'LIGHT' and o.data.type != 'SUN' and not o.hide_render
         and o.data.bf2.mode in ('dynamic', 'both')]
to_baked = sorted(lamps, key=lambda o: -float(o.data.energy))[BUDGET:]
for o in to_baked:
    o.data.bf2.mode = 'baked'
left = [o for o in bpy.context.view_layer.objects if o.type == 'MESH' and o.bf2.role == 'static']
rep = {'source': bpy.data.filepath, 'guesses': [{'object': o.name, 'role': r, 'why': w} for o, r, w in gs], 'meshes_left': len(left),
       'largest_left': sorted(((round(max(o.dimensions), 1), o.name) for o in left), reverse=True)[:8],
       'lights': sum(1 for o in bpy.context.view_layer.objects if o.type == 'LIGHT'),
       'lights_made_baked': [o.name for o in to_baked],
       'rescaled': rescale,
       'missing_images': sorted(i.name for i in bpy.data.images if i.source == 'FILE' and not i.packed_file and not os.path.exists(bpy.path.abspath(i.filepath)))[:40]}
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
json.dump(rep, open(REPORT, 'w'), indent=1)
print('PREP DONE', OUT, 'guesses', len(gs), 'meshes left', len(left), 'missing images', len(rep['missing_images']))
