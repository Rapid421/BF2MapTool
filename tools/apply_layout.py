"""Put a designed layout (design_layout.py) into a prepared scene: command posts A..E, team headquarters spawns, hero
spawns; turn Supremacy on. Earlier tool-made spawns / posts (collection "BF2 gameplay") are replaced.
    blender -b --factory-startup <in.blend> --python apply_layout.py -- <addon dir> <layout.json> <out.blend> [k=v scene settings]
"""
import json
import math
import os
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
ADDON, LAYOUT, OUT = argv[:3]
sys.path.insert(0, os.path.dirname(ADDON))
props = __import__(os.path.basename(ADDON) + '.props', fromlist=['props'])
if not hasattr(bpy.types.Scene, 'bf2'):
    props.register()
L = json.load(open(LAYOUT))
sc = bpy.context.scene
coll = bpy.data.collections.get('BF2 gameplay') or bpy.data.collections.new('BF2 gameplay')
if coll.name not in sc.collection.children:
    sc.collection.children.link(coll)
for o in list(coll.objects):
    bpy.data.objects.remove(o, do_unlink=True)


def empty(name, pos, yaw=0.0, kind='ARROWS', size=1.0):
    e = bpy.data.objects.new(name, None)
    e.empty_display_type = kind
    e.empty_display_size = size
    e.location = (pos[0], pos[1], pos[2] + 0.1)
    e.rotation_euler = (0.0, 0.0, math.radians(yaw))
    coll.objects.link(e)
    return e


for p in L['posts']:
    e = empty('BF2_CP_%s' % p['letter'], p['pos'], kind='CIRCLE', size=p['radius'])
    e.rotation_euler = (0.0, 0.0, 0.0)
    b = e.bf2
    b.role = 'command_post'; b.cp_letter = p['letter']; b.cp_radius = p['radius']; b.team = p['owner']
    b.cp_order = 'ABCDE'.index(p['letter'])
n = 0
for team, sp in L['hq'].items():
    if not any(s['hero'] for s in sp):          # heroes start from the headquarters too (Heroes vs Villains)
        sp = sp + [dict(s, hero=True) for s in sp[:4]]
    for i, s in enumerate(sp):
        e = empty('BF2_%s_%s_%02d' % ('Hero' if s['hero'] else 'HQ', team, i + 1), s['pos'], s['yaw'])
        e.bf2.role = 'hero_spawn' if s['hero'] else 'spawn'
        e.bf2.team = team
        n += 1
s = sc.bf2
for kv in argv[3:]:
    k, v = kv.split('=', 1)
    old = getattr(s, k)
    setattr(s, k, (v in ('1', 'true', 'True')) if isinstance(old, bool) else type(old)(v))
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
print('LAYOUT APPLIED', len(L['posts']), 'posts', n, 'spawns ->', OUT)
