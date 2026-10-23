"""Test scene for the material bake: one small square per material, each material built the way real scenes build them
where an image-behind-the-socket reading is wrong. The source pictures are saved as .npy so check_bake.py can work out
what every baked texture MUST be.

    blender -b --factory-startup --python make_bake_scene.py -- <out.blend> <addon dir> <npy dir>
"""
import math
import os
import sys

import bpy
import numpy as np

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, ADDON, NPY = argv[0], argv[1], argv[2]
sys.path.insert(0, ADDON)
import props
props.register()
os.makedirs(NPY, exist_ok=True)
for o in list(bpy.data.objects):
    bpy.data.objects.remove(o)
sc = bpy.context.scene
sc.bf2.name = 'BakeTest'
N = 256
rng = np.random.default_rng(3)


def smooth(seed, ch):
    r = np.random.default_rng(seed)
    y, x = np.mgrid[0:N, 0:N].astype(np.float32) / N * 2 * math.pi
    out = np.zeros((N, N, ch), np.float32)
    for c in range(ch):
        for k in range(1, 5):                       # periodic, so the picture tiles
            a, b, p, q = r.random(4)
            out[..., c] += np.sin(k * x + 6 * p) * a / k + np.cos(k * y + 6 * q) * b / k + np.sin(k * (x + y) + 3 * a) * 0.3 / k
    out -= out.min((0, 1)); out /= out.max((0, 1))
    return out


A = np.dstack([smooth(1, 3) * 0.8 + 0.1, np.ones((N, N, 1), np.float32)])
B = np.dstack([smooth(2, 3) * 0.8 + 0.1, np.ones((N, N, 1), np.float32)])
G = np.dstack([np.repeat(smooth(5, 1), 3, 2), np.ones((N, N, 1), np.float32)])
yy, xx = np.mgrid[0:N, 0:N].astype(np.float32)
c = N / 4
H1 = np.sqrt(np.clip((c * 0.36) ** 2 - ((xx % c) - c / 2) ** 2 - ((yy % c) - c / 2) ** 2, 0, None)) / (c * 0.36)
H = np.dstack([np.repeat(H1[..., None], 3, 2), np.ones((N, N, 1), np.float32)])
for nm, arr in (('A', A), ('B', B), ('G', G), ('H', H)):
    np.save(os.path.join(NPY, nm + '.npy'), arr)


def image(name, rgba, colorspace):
    img = bpy.data.images.new(name, N, N, alpha=True)
    img.colorspace_settings.name = colorspace
    img.pixels.foreach_set(np.ascontiguousarray(rgba[::-1], np.float32).ravel())
    img.pack()
    return img


IA, IB = image('bk_a_c', A, 'sRGB'), image('bk_b_c', B, 'sRGB')
IG, IH = image('bk_g', G, 'Non-Color'), image('bk_h', H, 'Non-Color')


def mat(name, cls='opaque'):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    m.bf2.cls = cls
    nt = m.node_tree
    return m, nt, next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')


def tex(nt, img):
    t = nt.nodes.new('ShaderNodeTexImage'); t.image = img
    return t


def mix(nt, a, b, fac=0.5, blend='MIX'):
    m = nt.nodes.new('ShaderNodeMix'); m.data_type = 'RGBA'; m.blend_type = blend
    m.inputs[0].default_value = fac
    for sock, v in ((m.inputs[6], a), (m.inputs[7], b)):
        if isinstance(v, tuple):
            sock.default_value = v
        else:
            nt.links.new(v, sock)
    return m.outputs[2]


mats = []
# 0 control: an image straight into the socket - must NOT be baked
m, nt, p = mat('bk_plain'); nt.links.new(tex(nt, IA).outputs['Color'], p.inputs['Base Color']); mats.append(m)
# 1 the image tiled twice by a Mapping node
m, nt, p = mat('bk_tiled')
t = tex(nt, IA); mp = nt.nodes.new('ShaderNodeMapping'); tc = nt.nodes.new('ShaderNodeTexCoord')
mp.inputs['Scale'].default_value = (2.0, 2.0, 1.0)
nt.links.new(tc.outputs['UV'], mp.inputs['Vector']); nt.links.new(mp.outputs['Vector'], t.inputs['Vector'])
nt.links.new(t.outputs['Color'], p.inputs['Base Color']); mats.append(m)
# 2 two images mixed half and half
m, nt, p = mat('bk_mix'); nt.links.new(mix(nt, tex(nt, IA).outputs['Color'], tex(nt, IB).outputs['Color']), p.inputs['Base Color']); mats.append(m)
# 3 roughness through a colour ramp (0.25 .. 0.75)
m, nt, p = mat('bk_ramp')
nt.links.new(tex(nt, IA).outputs['Color'], p.inputs['Base Color'])
r = nt.nodes.new('ShaderNodeValToRGB'); r.color_ramp.elements[0].position = 0.25; r.color_ramp.elements[1].position = 0.75
nt.links.new(tex(nt, IG).outputs['Color'], r.inputs['Fac']); nt.links.new(r.outputs['Color'], p.inputs['Roughness']); mats.append(m)
# 4 relief from a height picture through a Bump node (raised domes)
m, nt, p = mat('bk_bump')
nt.links.new(tex(nt, IA).outputs['Color'], p.inputs['Base Color'])
bp = nt.nodes.new('ShaderNodeBump'); bp.inputs['Strength'].default_value = 1.0; bp.inputs['Distance'].default_value = 0.02
nt.links.new(tex(nt, IH).outputs['Color'], bp.inputs['Height']); nt.links.new(bp.outputs['Normal'], p.inputs['Normal']); mats.append(m)
# 5 emission = two images multiplied, strength 5
m, nt, p = mat('bk_emit', 'emissive')
nt.links.new(tex(nt, IA).outputs['Color'], p.inputs['Base Color'])
nt.links.new(mix(nt, tex(nt, IA).outputs['Color'], tex(nt, IB).outputs['Color'], 1.0, 'MULTIPLY'), p.inputs.get('Emission Color') or p.inputs['Emission'])
p.inputs['Emission Strength'].default_value = 5.0; mats.append(m)
# 6 no Principled BSDF at all: a Diffuse BSDF
m, nt, p = mat('bk_diffuse')
d = nt.nodes.new('ShaderNodeBsdfDiffuse'); nt.links.new(tex(nt, IA).outputs['Color'], d.inputs['Color'])
out = next(n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL'); nt.links.new(d.outputs[0], out.inputs['Surface']); nt.nodes.remove(p); mats.append(m)
# 7 the Principled BSDF inside a node group that also mixes the colour with a constant
g = bpy.data.node_groups.new('bk_group', 'ShaderNodeTree')
g.interface.new_socket('Colour', in_out='INPUT', socket_type='NodeSocketColor')
g.interface.new_socket('Shader', in_out='OUTPUT', socket_type='NodeSocketShader')
gi, go, gp = g.nodes.new('NodeGroupInput'), g.nodes.new('NodeGroupOutput'), g.nodes.new('ShaderNodeBsdfPrincipled')
g.links.new(mix(g, gi.outputs[0], (0.5, 0.5, 0.5, 1.0)), gp.inputs['Base Color']); g.links.new(gp.outputs[0], go.inputs[0])
m, nt, p = mat('bk_grouped')
gn = nt.nodes.new('ShaderNodeGroup'); gn.node_tree = g
nt.links.new(tex(nt, IA).outputs['Color'], gn.inputs[0])
out = next(n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL'); nt.links.new(gn.outputs[0], out.inputs['Surface']); nt.nodes.remove(p); mats.append(m)
# 8 a cut-out whose mask is a hard step of a grey picture
m, nt, p = mat('bk_alpha', 'cutout')
nt.links.new(tex(nt, IA).outputs['Color'], p.inputs['Base Color'])
r = nt.nodes.new('ShaderNodeValToRGB'); r.color_ramp.interpolation = 'CONSTANT'; r.color_ramp.elements[1].position = 0.5
nt.links.new(tex(nt, IG).outputs['Color'], r.inputs['Fac']); nt.links.new(r.outputs['Color'], p.inputs['Alpha']); mats.append(m)
# 9 a procedural texture on its own UVs
m, nt, p = mat('bk_noise')
n = nt.nodes.new('ShaderNodeTexNoise'); tc = nt.nodes.new('ShaderNodeTexCoord'); nt.links.new(tc.outputs['UV'], n.inputs['Vector'])
nt.links.new(n.outputs['Color'], p.inputs['Base Color']); mats.append(m)

for i, m in enumerate(mats):
    me = bpy.data.meshes.new('sq_%d' % i)
    me.from_pydata([(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0)], [], [(0, 1, 2, 3)])
    uv = me.uv_layers.new(name='UVMap')
    for k, cuv in enumerate(((0, 0), (1, 0), (1, 1), (0, 1))):
        uv.data[k].uv = cuv
    me.materials.append(m)
    o = bpy.data.objects.new('sq_%s' % m.name, me)
    o.location = (3.0 * i, 0.0, 0.0)
    sc.collection.objects.link(o)
bpy.ops.wm.save_as_mainfile(filepath=OUT)
print('BAKE SCENE DONE', OUT, len(mats), 'materials')
