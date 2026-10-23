"""Game look: show the scene in the viewport with the textures the build really packed for the game, so what the
game will get (colour, smoothness, metal, normal map, cut-out mask, glow) can be judged in Material Preview / Rendered
view before going in game.

On: every map material that the last build packed (<work>/tex/plan.json) gets a COPY, "<name> [game]", whose nodes
read the packed textures (<work>/tex/*.png); the objects' material slots point to the copies. Off: the slots point to
the originals again and the copies are removed (their images are left without users and are not saved). The originals are never edited, and the export
always uses the originals (export_scene.undo_game_look), so a build started while the game look is on is the same.

Layouts (runtime texpack.py):  CS rgb colour, a smoothness | NAM rg normal (green up), a metal | NW rg normal, a mask |
E rgb emission | HC rgb hologram glow
"""
import json
import os

import bpy

SUFFIX = ' [game]'
KEY = 'bf2_game_look_of'           # on a copy: the name of its original material
NOT_PACKED = ('glass', 'lava')     # classes the game builds from its own templates: the original stays


def is_on(scene=None):
    return bool((scene or bpy.context.scene).get('bf2_game_look'))


def load_plan(work):
    p = os.path.join(work, 'tex', 'plan.json')
    if not os.path.isfile(p):
        return None
    with open(p, encoding='utf-8') as f:
        return json.load(f)


def plan_entry(plan, name):
    """the plan record of a material: its own, else its first tinted copy's (LEGO rips tint per object)"""
    if name in plan:
        return plan[name]
    for k in sorted(plan):
        if k.startswith(name + '#'):
            return plan[k]
    return None


def _image(work, tex, colour):
    path = os.path.join(work, 'tex', tex + '.png')
    if not os.path.isfile(path):
        return None
    img = bpy.data.images.load(path, check_existing=True)
    img.colorspace_settings.name = 'sRGB' if colour else 'Non-Color'
    img.alpha_mode = 'CHANNEL_PACKED'          # the alpha is data (smoothness, metal, mask), not transparency
    img['bf2_game_look'] = True
    return img


def _normal(nt, tex_node, x, y):
    """packed rg normal (green up) -> a Normal Map node fed with the rebuilt z"""
    N, L = nt.nodes, nt.links
    sep = N.new('ShaderNodeSeparateColor'); sep.location = (x, y)
    L.new(tex_node.outputs['Color'], sep.inputs['Color'])
    # z = sqrt(1 - nx^2 - ny^2), n = 2c - 1;  blue = z / 2 + 1 / 2
    def math(op, a, b=None, loc=(0, 0)):
        m = N.new('ShaderNodeMath'); m.operation = op; m.location = loc
        for i, v in enumerate((a, b)):
            if v is None:
                continue
            if isinstance(v, (int, float)):
                m.inputs[i].default_value = v
            else:
                L.new(v, m.inputs[i])
        return m.outputs[0]
    nx = math('MULTIPLY_ADD', sep.outputs['Red'], 2.0, (x + 180, y + 80)); nx.node.inputs[2].default_value = -1.0
    ny = math('MULTIPLY_ADD', sep.outputs['Green'], 2.0, (x + 180, y - 80)); ny.node.inputs[2].default_value = -1.0
    s = math('ADD', math('MULTIPLY', nx, nx, (x + 360, y + 80)), math('MULTIPLY', ny, ny, (x + 360, y - 80)), (x + 540, y))
    z = math('SQRT', math('MAXIMUM', math('SUBTRACT', 1.0, s, (x + 720, y)), 0.0, (x + 900, y)), None, (x + 1080, y))
    b = math('MULTIPLY_ADD', z, 0.5, (x + 1260, y)); b.node.inputs[2].default_value = 0.5
    comb = N.new('ShaderNodeCombineColor'); comb.location = (x + 1440, y)
    L.new(sep.outputs['Red'], comb.inputs['Red']); L.new(sep.outputs['Green'], comb.inputs['Green']); L.new(b, comb.inputs['Blue'])
    nm = N.new('ShaderNodeNormalMap'); nm.location = (x + 1620, y)
    L.new(comb.outputs['Color'], nm.inputs['Color'])
    return nm.outputs['Normal']


def game_copy(orig, rec, work):
    """the copy of one material wired to its packed textures, or None when the plan has nothing to show"""
    if rec is None or rec.get('class') in NOT_PACKED:
        return None
    slots = {s.get('layout'): s.get('texture') for s in rec.get('slots') or [] if s.get('texture')}
    if not slots:
        return None
    m = orig.copy()
    m.name = orig.name + SUFFIX
    m[KEY] = orig.name
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    N, L = nt.nodes, nt.links
    out = N.new('ShaderNodeOutputMaterial'); out.location = (2600, 0)
    bsdf = N.new('ShaderNodeBsdfPrincipled'); bsdf.location = (2300, 0)
    L.new(bsdf.outputs['BSDF'], out.inputs['Surface'])
    y = 600
    for layout, tex in sorted(slots.items()):
        img = _image(work, tex, layout in ('CS', 'E', 'HC'))
        if img is None:
            continue
        t = N.new('ShaderNodeTexImage'); t.image = img; t.location = (-400, y); t.label = '%s  %s' % (layout, tex)
        if layout == 'CS':
            L.new(t.outputs['Color'], bsdf.inputs['Base Color'])
            inv = N.new('ShaderNodeMath'); inv.operation = 'SUBTRACT'; inv.inputs[0].default_value = 1.0; inv.location = (0, y - 200)
            L.new(t.outputs['Alpha'], inv.inputs[1])
            L.new(inv.outputs[0], bsdf.inputs['Roughness'])
        elif layout in ('NAM', 'NW'):
            L.new(_normal(nt, t, 0, y - 300), bsdf.inputs['Normal'])
            if layout == 'NAM':
                L.new(t.outputs['Alpha'], bsdf.inputs['Metallic'])
            else:
                cut = N.new('ShaderNodeMath'); cut.operation = 'GREATER_THAN'; cut.inputs[1].default_value = 0.5; cut.location = (0, y - 700)
                L.new(t.outputs['Alpha'], cut.inputs[0])
                L.new(cut.outputs[0], bsdf.inputs['Alpha'])
        elif layout in ('E', 'HC'):
            L.new(t.outputs['Color'], bsdf.inputs['Emission Color'])
            bsdf.inputs['Emission Strength'].default_value = 1.0
            if layout == 'HC':
                bsdf.inputs['Base Color'].default_value = (0.0, 0.0, 0.0, 1.0)
                bsdf.inputs['Alpha'].default_value = 0.6
        y -= 1100
    return m


def apply(context, work):
    """switch the game look on -> (materials shown with game textures, materials kept as they are, message or '')"""
    plan = load_plan(work)
    if plan is None:
        return 0, 0, 'No packed textures yet (%s/tex/plan.json): build the map first.' % work
    restore(context)
    made, kept = {}, set()
    for o in context.scene.objects:
        if o.type != 'MESH' or o.bf2.role not in ('static', 'door'):
            continue
        for s in o.material_slots:
            mt = s.material
            if mt is None or mt.get(KEY):
                continue
            if mt.name not in made and mt.name not in kept:
                c = game_copy(mt, plan_entry(plan, mt.name), work)
                if c is None:
                    kept.add(mt.name)
                else:
                    made[mt.name] = c
            if mt.name in made:
                s.material = made[mt.name]
    context.scene['bf2_game_look'] = True
    return len(made), len(kept), ''


def restore(context=None, scene=None):
    """switch the game look off: slots back to the originals, copies and their images removed -> slots restored"""
    scene = scene or (context or bpy.context).scene
    n = 0
    for o in scene.objects:
        for s in o.material_slots:
            mt = s.material
            if mt is not None and mt.get(KEY) and mt[KEY] in bpy.data.materials:
                s.material = bpy.data.materials[mt[KEY]]
                n += 1
    if 'bf2_game_look' in scene:
        del scene['bf2_game_look']
    for mt in [m for m in bpy.data.materials if m.get(KEY) and m.users == 0]:
        bpy.data.materials.remove(mt)
    # the packed images are NOT removed here: the viewport may still be loading them on another thread (removing them
    # crashed Blender 5.2 in image_acquire_ibuf, Material Preview on). With no users left they are not saved with the
    # .blend, and a later "Game look" reuses them (images.load check_existing)
    return n
