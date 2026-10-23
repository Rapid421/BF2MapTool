"""One tiny scene per pre-flight check, each built to trip exactly that check (plus `clean`, which must trip none).
    blender -b --factory-startup --python make_check_scenes.py -- <out dir> <addon dir>
Writes <out dir>/<case>.blend and <out dir>/cases.json = {case: [codes that must be reported]}.
"""
import json
import math
import os
import sys

import bmesh
import bpy
import numpy as np

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, ADDON = argv[0], argv[1]
sys.path.insert(0, ADDON)
import props
os.makedirs(OUT, exist_ok=True)
CASES = {}
registered = False


def reset(name):
    global registered, sc
    bpy.ops.wm.read_factory_settings(use_empty=True)
    if registered:
        props.unregister()
    props.register()
    registered = True
    sc = bpy.context.scene
    sc.bf2.name = 'Chk' + ''.join(w.capitalize() for w in name.replace('.', '_').split('_'))[:18]
    return sc


def image(name, n=64, rgb=(0.4, 0.4, 0.4), alpha=1.0, size=None):
    w, h = size or (n, n)
    img = bpy.data.images.new(name, w, h, alpha=True)
    px = np.empty((h, w, 4), np.float32); px[..., :3] = rgb; px[..., 3] = alpha
    px[::8, :, :3] *= 0.8
    img.pixels.foreach_set(px.ravel())
    img.pack()
    return img


def mat(name, cls='opaque', img=None, with_image=True):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    m.bf2.cls = cls
    nt = m.node_tree
    p = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
    if with_image:
        t = nt.nodes.new('ShaderNodeTexImage'); t.image = img or image(name + '_c')
        nt.links.new(t.outputs['Color'], p.inputs['Base Color'])
    return m, nt, p


def box(name, size, loc, material, role='static', uv=True, flip=False):
    bm = bmesh.new()
    if uv:
        bm.loops.layers.uv.new('UVMap')                # create_cube only fills an existing UV layer
    bmesh.ops.create_cube(bm, size=1.0, calc_uvs=uv)
    for v in bm.verts:
        v.co.x *= size[0]; v.co.y *= size[1]; v.co.z *= size[2]
    if flip:
        bmesh.ops.reverse_faces(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me); bm.free()
    if not uv:
        while me.uv_layers:
            me.uv_layers.remove(me.uv_layers[0])
    if material is not None:
        me.materials.append(material)
    o = bpy.data.objects.new(name, me)
    o.location = loc
    sc.collection.objects.link(o)
    o.bf2.role = role
    return o


def spawn(name, loc, team):
    o = bpy.data.objects.new(name, None)
    o.location = loc
    sc.collection.objects.link(o)
    o.bf2.role, o.bf2.team = 'spawn', team
    return o


def lamp(name, loc, color=(1, 1, 1), mode='baked'):
    d = bpy.data.lights.new(name, 'POINT'); d.energy = 500.0; d.color = color
    d.bf2.mode = mode
    o = bpy.data.objects.new(name, d); o.location = loc
    sc.collection.objects.link(o)
    return o


def base():
    """a 60 x 40 m floor with one good material - passes every check on its own"""
    m, _, _ = mat('floor')
    box('floor', (60.0, 40.0, 1.0), (0.0, 0.0, -0.5), m)
    return m


def case(name, codes):
    def deco(fn):
        reset(name)
        fn()
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, name + '.blend'))
        CASES[name] = codes
        return fn
    return deco


@case('clean', [])
def _():
    base()


@case('geo_none', ['geo.none'])
def _():
    o = bpy.data.objects.new('just_an_empty', None); sc.collection.objects.link(o)


@case('map_no_mode', ['map.no_mode'])
def _():
    base()
    for k in ('mode_blast', 'mode_hvv', 'mode_arcade', 'mode_supremacy'):
        setattr(sc.bf2, k, False)


@case('supremacy_posts', ['supremacy.posts'])
def _():
    base()
    sc.bf2.mode_supremacy = True
    for i in range(2):
        o = bpy.data.objects.new('cp_%d' % i, None); o.location = (10.0 * i, 0, 0); sc.collection.objects.link(o)
        o.bf2.role = 'command_post'


@case('supremacy_letters', ['supremacy.letters'])
def _():
    base()
    sc.bf2.mode_supremacy = True
    for i, letter in enumerate('AABCD'):                 # five posts, but A twice and no E
        o = bpy.data.objects.new('cp_%d' % i, None); o.location = (-20 + 10.0 * i, 0, 0); sc.collection.objects.link(o)
        o.bf2.role = 'command_post'; o.bf2.cp_letter = letter


@case('geo_no_material', ['geo.no_material'])
def _():
    base(); box('bare', (2, 2, 2), (5, 0, 1), None)


@case('geo_auto_uv', ['geo.auto_uv'])                # an object without a UV map is unwrapped by the export, with a warning
def _():
    m = base(); box('no_uv', (2, 2, 2), (5, 0, 1), m, uv=False)


@case('geo_degenerate', ['geo.degenerate'])
def _():
    m = base()
    me = bpy.data.meshes.new('slivers')
    verts = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)] + [(5 + i, 5, 0) for i in range(12)]
    faces = [(0, 1, 2, 3)] + [(4 + i, 5 + i, 6 + i) for i in range(10)]          # ten triangles on one line: no area
    me.from_pydata(verts, [], faces)
    me.uv_layers.new(name='UVMap'); me.materials.append(m)
    o = bpy.data.objects.new('slivers', me); o.location = (0, 0, 0.01); sc.collection.objects.link(o)


@case('geo_mirrored', ['geo.scale'])
def _():
    m = base(); o = box('mirrored', (2, 2, 2), (5, 0, 1), m); o.scale = (-1.0, 1.0, 1.0)


@case('scene_extent', ['scene.extent'])
def _():
    m = base(); box('far_away', (4, 4, 4), (9000.0, 0, 2), m)
    sc.bf2.keep_distance = 0.0


@case('scene_scale', ['scene.scale'])
def _():
    m, _, _ = mat('tiny'); box('tiny_map', (3.0, 2.0, 0.2), (0, 0, 0), m)


@case('class_limited', ['class.limited'])
def _():
    base(); m, _, _ = mat('window', 'glass'); box('pane', (2, 0.1, 2), (5, 0, 1), m)


@case('class_placeholder', ['class.placeholder'])
def _():
    base(); m, _, _ = mat('molten', 'lava'); box('pool', (4, 4, 0.2), (5, 0, 0.1), m)


@case('class_guessed', ['class.guessed'])
def _():
    base(); m, _, _ = mat('guessed'); m.bf2.cls_guessed = True; box('g', (2, 2, 2), (5, 0, 1), m)


@case('class_auto', ['class.auto'])
def _():
    base(); m, nt, p = mat('glows')
    (p.inputs.get('Emission Color') or p.inputs['Emission']).default_value = (1.0, 0.5, 0.1, 1.0)
    p.inputs['Emission Strength'].default_value = 5.0
    box('lamp_block', (2, 2, 2), (5, 0, 1), m)


@case('tex_missing', ['tex.missing'])
def _():
    base()
    path = os.path.join(OUT, 'gone.png')
    img = image('tmp'); img.filepath_raw = path; img.file_format = 'PNG'; img.save()
    bpy.data.images.remove(img)
    ext = bpy.data.images.load(path)
    m, nt, p = mat('lost', with_image=False)
    t = nt.nodes.new('ShaderNodeTexImage'); t.image = ext
    nt.links.new(t.outputs['Color'], p.inputs['Base Color'])
    box('lost_box', (2, 2, 2), (5, 0, 1), m)
    CASES['_delete_after_save'] = [path]


@case('tex_cutout_no_alpha', ['tex.cutout_no_alpha'])
def _():
    base(); m, _, _ = mat('solid_cutout', 'cutout'); box('c', (2, 0.1, 2), (5, 0, 1), m)


@case('class_guess_fallback', ['class.auto', 'class.guessed'])        # a GUESSED cut-out without holes is built opaque, no error
def _():
    base(); m, _, _ = mat('guessed_cutout', 'cutout'); m.bf2.cls_guessed = True; box('c', (2, 0.1, 2), (5, 0, 1), m)


@case('tex_resized', ['tex.resized'])
def _():
    base(); m, _, _ = mat('odd', img=image('odd_c', size=(300, 200))); box('o', (2, 2, 2), (5, 0, 1), m)


@case('tex_uv_transform', ['tex.uv_transform'])
def _():
    base(); sc.bf2.bake_materials = 'never'
    m, nt, p = mat('tiled')
    t = next(n for n in nt.nodes if n.type == 'TEX_IMAGE')
    mp = nt.nodes.new('ShaderNodeMapping'); mp.inputs['Scale'].default_value = (3.0, 3.0, 1.0)
    tc = nt.nodes.new('ShaderNodeTexCoord')
    nt.links.new(tc.outputs['UV'], mp.inputs['Vector']); nt.links.new(mp.outputs['Vector'], t.inputs['Vector'])
    box('t', (2, 2, 2), (5, 0, 1), m)


@case('tex_approximate', ['tex.approximate'])
def _():
    base(); sc.bf2.bake_materials = 'never'
    m, nt, p = mat('mixed')
    t = next(n for n in nt.nodes if n.type == 'TEX_IMAGE')
    t2 = nt.nodes.new('ShaderNodeTexImage'); t2.image = image('mixed_b', rgb=(0.7, 0.2, 0.2))
    mx = nt.nodes.new('ShaderNodeMix'); mx.data_type = 'RGBA'
    nt.links.new(t.outputs['Color'], mx.inputs[6]); nt.links.new(t2.outputs['Color'], mx.inputs[7])
    nt.links.new(mx.outputs[2], p.inputs['Base Color'])
    box('m', (2, 2, 2), (5, 0, 1), m)


@case('tex_unsupported_node', ['tex.unsupported_node'])
def _():
    base(); sc.bf2.bake_materials = 'never'
    m, nt, p = mat('ramped')
    t = next(n for n in nt.nodes if n.type == 'TEX_IMAGE')
    r = nt.nodes.new('ShaderNodeValToRGB')
    nt.links.new(t.outputs['Color'], r.inputs['Fac']); nt.links.new(r.outputs['Color'], p.inputs['Roughness'])
    box('r', (2, 2, 2), (5, 0, 1), m)


@case('tex_no_principled', ['tex.no_principled'])
def _():
    base(); sc.bf2.bake_materials = 'never'
    m, nt, p = mat('diffuse_only')
    d = nt.nodes.new('ShaderNodeBsdfDiffuse')
    out = next(n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL'); nt.links.new(d.outputs[0], out.inputs['Surface']); nt.nodes.remove(p)
    box('d', (2, 2, 2), (5, 0, 1), m)


@case('tex_bake_note', ['tex.bake_note'])
def _():
    base()
    m, nt, p = mat('object_space', with_image=False)
    n = nt.nodes.new('ShaderNodeTexNoise')                      # no vector input: it reads object coordinates
    nt.links.new(n.outputs['Color'], p.inputs['Base Color'])
    box('n', (2, 2, 2), (5, 0, 1), m)


@case('kill_count', ['kill.count'])
def _():
    m = base()
    for i in range(4):
        box('kill_%d' % i, (2, 2, 2), (-20 + 10.0 * i, 12, -3), m, role='kill_volume')


@case('spawn_in_kill', ['spawn.in_kill'])
def _():
    m = base(); box('kill', (6, 6, 6), (10, 0, 1), m, role='kill_volume'); spawn('in_it', (10, 0, 0.5), 'team1')
    spawn('other', (-25, 0, 0), 'team2'); spawn('other1', (25, 12, 0), 'team1')


@case('spawn_teams_close', ['spawn.teams_close'])
def _():
    base(); spawn('a', (-3, 0, 0), 'team1'); spawn('b', (3, 0, 0), 'team2')


@case('spawn_none', ['spawn.none'])
def _():
    base(); sc.bf2.auto_spawns = False


@case('door_materials', ['door.materials'])
def _():
    m = base()
    o = box('door', (0.3, 3, 3), (5, 0, 1.5), m, role='door'); o.bf2.door_axis = 'up'
    m2, _, _ = mat('second'); o.data.materials.append(m2)
    o.data.polygons[0].material_index = 1


@case('door_shape', ['door.shape'])
def _():
    m = base(); o = box('door_cube', (3, 3, 3), (5, 0, 1.5), m, role='door'); o.bf2.door_axis = 'up'


@case('light_budget', ['light.dynamic_budget'])
def _():
    base(); sc.bf2.dynamic_light_budget = 1
    lamp('l1', (0, 0, 3), mode='dynamic'); lamp('l2', (5, 0, 3), mode='both')


@case('light_saturated', ['light.saturated'])
def _():
    base(); lamp('pure_red', (0, 0, 3), color=(1.0, 0.0, 0.0))


@case('map_name_clash', ['map.name_clash'])
def _():
    base(); sc.bf2.name = 'Kamino'                      # the game ships Kamino_01


@case('spawn_in_geometry', ['spawn.in_geometry'])
def _():
    m = base(); box('crate', (2, 2, 2), (5, 0, 1), m); spawn('in_the_crate', (5, 0, 0), 'team1')


@case('spawn_no_floor', ['spawn.no_floor'])
def _():
    base(); spawn('in_the_air', (0, 0, 10), 'team1'); spawn('off_the_map', (100, 0, 0), 'team1')


@case('kill_walkable', ['kill.walkable'])
def _():
    m = base(); box('kill_over_floor', (10, 10, 4), (0, 0, 0), m, role='kill_volume')
    spawn('a', (-25, 0, 0), 'team1'); spawn('b', (25, 0, 0), 'team2')


@case('light_in_surface', ['light.in_surface'])
def _():
    base(); lamp('on_the_floor', (0, 0, 0.2))


@case('door_travel', ['door.travel'])
def _():
    m = base(); o = box('door_short', (0.3, 3, 3), (5, 0, 1.5), m, role='door'); o.bf2.door_axis = 'up'; o.bf2.door_distance = 0.5


@case('geo_flipped', ['geo.flipped'])
def _():
    m = base(); box('inside_out', (2, 2, 2), (5, 0, 1), m, flip=True)


def sun(name, energy=3.0, angle_deg=0.5, rot=(0.6, 0.0, 0.5)):
    d = bpy.data.lights.new(name, 'SUN'); d.energy = energy; d.angle = math.radians(angle_deg)
    o = bpy.data.objects.new(name, d); o.location = (0, 0, 20); o.rotation_euler = rot
    sc.collection.objects.link(o)
    return o


@case('look_sky_not_world', ['look.sky_not_world'])
def _():
    base()
    w = bpy.data.worlds.new('sky_world'); w.use_nodes = True
    n = w.node_tree.nodes.new('ShaderNodeTexSky')
    w.node_tree.links.new(n.outputs[0], w.node_tree.nodes['Background'].inputs['Color'])
    sc.world = w
    sc.bf2.sky = 'clear'


@case('look_sun_soft', ['look.sun_soft'])
def _():
    base(); sun('wide_sun', angle_deg=30.0)


@case('look_one_sun', ['look.one_sun'])
def _():
    base(); sun('main_sun', 3.0); sun('fill_sun', 0.3, rot=(2.6, 0.0, 0.5))


for p in CASES.pop('_delete_after_save', []):
    if os.path.exists(p):
        os.remove(p)
json.dump(CASES, open(os.path.join(OUT, 'cases.json'), 'w'), indent=1)
print('CHECK SCENES DONE', len(CASES))
