"""ops.guess_class on the node setup of a ripped Unreal map (Hunters Bastion): the Principled BSDF sits inside a node
group, Alpha comes from a Mix (factor = a group input) of an image channel that is 1.0 everywhere, and Emission is
the colour masked by another channel. Such a material is NOT a cut-out; one whose channel really has holes is.
    blender -b --factory-startup --python test_guess_alpha.py"""
import sys
import os

import bpy

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'addon'))
import bf2_map_tool  # noqa: E402
bf2_map_tool.register()
from bf2_map_tool import ops  # noqa: E402


def image(name, blue_holes):
    img = bpy.data.images.new(name, 8, 8, alpha=True)
    px = []
    for i in range(64):
        px += [0.7, 0.0, 0.0 if (blue_holes and i < 16) else 1.0, 1.0]
    img.pixels.foreach_set(px)
    return img


def group():
    g = bpy.data.node_groups.new('UE C/N/MEO', 'ShaderNodeTree')
    g.interface.new_socket('BSDF', in_out='OUTPUT', socket_type='NodeSocketShader')
    for n, t in (('C_RGB', 'NodeSocketColor'), ('MEO_RGB', 'NodeSocketColor'), ('Alpha_Enabled', 'NodeSocketFloat')):
        g.interface.new_socket(n, in_out='INPUT', socket_type=t)
    N, L = g.nodes, g.links
    gi, go = N.new('NodeGroupInput'), N.new('NodeGroupOutput')
    p = N.new('ShaderNodeBsdfPrincipled')
    p.inputs['Emission Strength'].default_value = 1.0          # the ripped group sets it (its default is 0)
    sep = N.new('ShaderNodeSeparateColor')
    L.new(gi.outputs['MEO_RGB'], sep.inputs[0])
    amix = N.new('ShaderNodeMix'); amix.data_type = 'FLOAT'
    amix.inputs['A'].default_value = 1.0
    L.new(gi.outputs['Alpha_Enabled'], amix.inputs['Factor'])
    L.new(sep.outputs['Blue'], amix.inputs[3])                 # B (float)
    L.new(amix.outputs[0], p.inputs['Alpha'])
    emix = N.new('ShaderNodeMix'); emix.data_type = 'RGBA'
    L.new(sep.outputs['Green'], emix.inputs['Factor'])
    L.new(gi.outputs['C_RGB'], emix.inputs[7])                 # B (colour)
    L.new(emix.outputs[2], p.inputs['Emission Color'])
    L.new(gi.outputs['C_RGB'], p.inputs['Base Color'])
    L.new(p.outputs['BSDF'], go.inputs['BSDF'])
    return g


g = group()


def material(name, holes):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    N, L = m.node_tree.nodes, m.node_tree.links
    for n in list(N):
        if n.type != 'OUTPUT_MATERIAL':
            N.remove(n)
    out = next(n for n in N if n.type == 'OUTPUT_MATERIAL')
    gn = N.new('ShaderNodeGroup'); gn.node_tree = g
    gn.inputs['Alpha_Enabled'].default_value = 1.0
    c = N.new('ShaderNodeTexImage'); c.image = image(name + '_C', False)
    meo = N.new('ShaderNodeTexImage'); meo.image = image(name + '_MEO', holes)
    L.new(c.outputs['Color'], gn.inputs['C_RGB'])
    L.new(meo.outputs['Color'], gn.inputs['MEO_RGB'])
    L.new(gn.outputs['BSDF'], out.inputs['Surface'])
    return m


ok = True
for name, holes, want in (('wall', False, 'emissive'), ('grate', True, 'cutout_emissive')):
    cls, why = ops.guess_class(material(name, holes))
    good = cls == want
    ok &= good
    print('%-4s %-6s guessed %-16s want %-16s (%s)' % ('ok' if good else 'FAIL', name, cls, want, why))
print('PASS' if ok else 'FAIL')
sys.stdout.flush()
os._exit(0 if ok else 1)
