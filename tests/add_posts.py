"""Add five command posts (A..E) to a scene and tick Supremacy - what a user does by hand in the Gameplay tab.
The posts are put on the line between the two team bases of an earlier build of the same scene (work/<Name>/spawns.json);
the build snaps each to the walkable floor.
    blender -b <scene.blend> --python add_posts.py -- <addon dir> <out.blend> <posts.json>
posts.json = [[letter, [x, y, z] scene metres, team], ...]   (written by tests/plan_posts.py)
"""
import json
import math
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
sys.path.insert(0, argv[0])
import props
try:
    props.register()
except Exception:
    pass
sc = bpy.context.scene
# --no-mode: the posts only (they set the play area and the floor spawns go on); Supremacy is ticked later, and then
# only the supremacy stage runs - the posts, and with them the play area, stay where they are
sc.bf2.mode_supremacy = '--no-mode' not in argv
for letter, pos, team in json.load(open(argv[2])):
    o = bpy.data.objects.new('cp_' + letter, None)
    o.empty_display_type = 'SINGLE_ARROW'
    o.location = pos
    o.rotation_euler = (math.radians(90.0), 0.0, 0.0)
    sc.collection.objects.link(o)
    o.bf2.role, o.bf2.team, o.bf2.cp_letter = 'command_post', team, letter
bpy.ops.wm.save_as_mainfile(filepath=argv[1])
print('POSTS ADDED', argv[1])
