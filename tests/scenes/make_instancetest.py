"""P0-6 test scene: every way Blender draws geometry that is not a plain object, with known triangle counts.
    blender -b --factory-startup --python make_instancetest.py -- <out.blend> <addon dir>
Writes <out.blend> and <out.blend>.expect.json = what the export must contain.
  plain cube                      12 tris (a normal object)
  array modifier cube x3          36 tris (evaluated mesh)
  collection instance x3          3 instances of a 12-tri cube (the collection is excluded from the view layer)
  nested collection instance x1   1 instance of a collection that instances the cube collection twice -> 2 instances
  Geometry Nodes 4 points         4 instances of a GN cube (12 tris each)
  hair particles x5               5 instances of an icosphere (20 tris each)
  instance of a "Leave out" obj   2 instances, skipped
"""
import json
import os
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, ADDON = argv[0], argv[1]
sys.path.insert(0, ADDON)
import props  # noqa: E402

bpy.ops.wm.read_factory_settings(use_empty=True)
props.register()
sc = bpy.context.scene
sc.bf2.name = 'InstTest'
root = sc.collection


def mat(name, rgb):
    m = bpy.data.materials.new(name); m.use_nodes = True
    m.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (*rgb, 1)
    return m


def cube(name, coll, loc=(0, 0, 0), size=2.0, m=None):
    bpy.ops.mesh.primitive_cube_add(size=size, location=loc)
    o = bpy.context.active_object; o.name = name
    for c in list(o.users_collection):
        c.objects.unlink(o)
    coll.objects.link(o)
    if m:
        o.data.materials.append(m)
    return o


grey, red, blue, green = mat('Grey', (0.5, 0.5, 0.5)), mat('Red', (0.8, 0.1, 0.1)), mat('Blue', (0.1, 0.2, 0.8)), mat('Green', (0.1, 0.7, 0.2))
# floor + plain cube
bpy.ops.mesh.primitive_plane_add(size=60); fl = bpy.context.active_object; fl.name = 'Floor'; fl.data.materials.append(grey)
cube('Plain', root, (0, 0, 1), m=grey)
# array modifier
a = cube('Arrayed', root, (6, 0, 1), m=red)
md = a.modifiers.new('Array', 'ARRAY'); md.count = 3
# collection instances
props_c = bpy.data.collections.new('PropsLib'); root.children.link(props_c)
cube('LibCube', props_c, (0, 0, 1), m=blue)
for i in range(3):
    e = bpy.data.objects.new('CubeInst%d' % i, None)
    e.instance_type, e.instance_collection = 'COLLECTION', props_c
    e.location = (-10, -10 + i * 5, 0)
    root.objects.link(e)
# nested: a collection that instances PropsLib twice
nest = bpy.data.collections.new('NestLib'); root.children.link(nest)
for i in range(2):
    e = bpy.data.objects.new('NestInner%d' % i, None)
    e.instance_type, e.instance_collection = 'COLLECTION', props_c
    e.location = (i * 4, 0, 0)
    nest.objects.link(e)
e = bpy.data.objects.new('NestOuter', None)
e.instance_type, e.instance_collection = 'COLLECTION', nest
e.location = (-10, 10, 0)
root.objects.link(e)
# Geometry Nodes: 4 points, a cube on each
bpy.ops.mesh.primitive_plane_add(size=1, location=(10, 10, 0)); g = bpy.context.active_object; g.name = 'GNScatter'
ng = bpy.data.node_groups.new('Scatter', 'GeometryNodeTree')
ng.interface.new_socket('Geometry', in_out='INPUT', socket_type='NodeSocketGeometry')
ng.interface.new_socket('Geometry', in_out='OUTPUT', socket_type='NodeSocketGeometry')
N = ng.nodes
gi, go = N.new('NodeGroupInput'), N.new('NodeGroupOutput')
line = N.new('GeometryNodeMeshLine'); line.inputs['Count'].default_value = 4
line.inputs['Offset'].default_value = (3.0, 0.0, 0.0)
cb = N.new('GeometryNodeMeshCube')
sm = N.new('GeometryNodeSetMaterial'); sm.inputs['Material'].default_value = green
iop = N.new('GeometryNodeInstanceOnPoints')
ng.links.new(cb.outputs['Mesh'], sm.inputs['Geometry'])
ng.links.new(line.outputs['Mesh'], iop.inputs['Points'])
ng.links.new(sm.outputs['Geometry'], iop.inputs['Instance'])
ng.links.new(iop.outputs['Instances'], go.inputs[0])
gm = g.modifiers.new('GN', 'NODES'); gm.node_group = ng
# hair particles rendering an icosphere
bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1, radius=0.5, location=(0, 30, -20)); ico = bpy.context.active_object; ico.name = 'Pebble'
ico.data.materials.append(red)
ico.hide_render = False
ico_c = bpy.data.collections.new('PebbleLib'); root.children.link(ico_c)
for c in list(ico.users_collection):
    c.objects.unlink(ico)
ico_c.objects.link(ico)
bpy.ops.mesh.primitive_plane_add(size=4, location=(-10, 20, 0)); h = bpy.context.active_object; h.name = 'HairEmitter'
h.data.materials.append(grey)
h.modifiers.new('Hair', 'PARTICLE_SYSTEM')
ps = h.particle_systems[0].settings
ps.type = 'HAIR'; ps.count = 5; ps.hair_length = 1.0; ps.render_type = 'OBJECT'; ps.instance_object = ico; ps.use_rotation_instance = False
ps.display_method = 'RENDER'
h.show_instancer_for_render = True
h.show_instancer_for_viewport = True
# an instanced object that is "Leave out": its instances are skipped
skip_c = bpy.data.collections.new('SkipLib'); root.children.link(skip_c)
so = cube('SkipMe', skip_c, (0, 0, 1), m=red); so.bf2.role = 'skip'
for i in range(2):
    e = bpy.data.objects.new('SkipInst%d' % i, None)
    e.instance_type, e.instance_collection = 'COLLECTION', skip_c
    e.location = (20, i * 5, 0)
    root.objects.link(e)
# the library collections are not drawn themselves
for name in ('PropsLib', 'NestLib', 'PebbleLib', 'SkipLib'):
    bpy.context.view_layer.layer_collection.children[name].exclude = True
# spawns so the play area is defined
for t, x in (('team1', -20), ('team2', 20)):
    s = bpy.data.objects.new('Spawn_' + t, None); s.location = (x, 0, 0.1); s.bf2.role = 'spawn'; s.bf2.team = t; root.objects.link(s)
sc.world = bpy.data.worlds.new('W')
bpy.ops.wm.save_as_mainfile(filepath=OUT)
exp = {'plain_objects': {'Floor': 2, 'Plain': 12, 'Arrayed': 36, 'HairEmitter': 2, 'GNScatter': 0},
       'instances': {'LibCube': 3 + 2, 'GNScatter': 4, 'Pebble': 5}, 'tris_per_instance': {'LibCube': 12, 'GNScatter': 12, 'Pebble': 20},
       'skipped_instances': 2, 'materials': ['Blue', 'Green', 'Grey', 'Red']}
json.dump(exp, open(OUT + '.expect.json', 'w'), indent=1)
print('MADE', OUT)
