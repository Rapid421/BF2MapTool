"""Do to the Rhen Var scene what a user of the add-on would do, and save the result as a COPY (the source is only read):
  * map settings (name, sky, fog);
  * "Guess roles" (the add-on's own function): sky spheres, cloud domes, duplicated terrain layers, invisible helpers;
  * nothing else - no spawns, no command posts, no material classes: the tool has to manage with its defaults.
    blender -b <source.blend> --python prep.py -- <out.blend> <addon dir> <report.json> [LevelName]
"""
import json
import os
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, ADDON, REPORT = argv[0], argv[1], argv[2]
NAME = argv[3] if len(argv) > 3 else 'RhenVar'
sys.path.insert(0, ADDON)
import props
props.register()
import guess

sc = bpy.context.scene
b = sc.bf2
b.name, b.display_name, b.planet = NAME, 'Rhen Var', 'Rhen Var'
b.description = 'Rhen Var (2005), through the BF2 Map Tool with its defaults.'
b.sky = 'overcast'
b.fog_color = (0.75, 0.80, 0.88)
b.fog_start, b.fog_end, b.fog_density = 30.0, 600.0, 0.5
gs = guess.guess_roles(list(bpy.context.view_layer.objects))
guess.apply(gs)
rep = {'source': bpy.data.filepath, 'guesses': [{'object': o.name, 'role': r, 'why': w} for o, r, w in gs]}
left = [o for o in bpy.context.view_layer.objects if o.type == 'MESH' and o.bf2.role == 'static']
rep['meshes_left'] = len(left)
rep['largest_left'] = sorted(((round(max(o.dimensions), 1), o.name) for o in left), reverse=True)[:8]
os.makedirs(os.path.dirname(OUT), exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
json.dump(rep, open(REPORT, 'w'), indent=1)
print('PREP DONE', OUT, 'guesses', len(gs), 'meshes left', len(left))
