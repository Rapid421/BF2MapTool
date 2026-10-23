"""Round trip of the channel packing, per material class: known channels -> source images -> texpack.pack() with the
class's own template slots -> texpack.unpack() -> the same channels (within one 8-bit step).

Also: a normal map handed in with green DOWN (DirectX) must be detected and come out green UP, equal to the same map
handed in green up; a class the host has no template for (glass) must say so.
    python test_roundtrip.py <scratch dir> <out.json>
"""
import json
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'runtime')))
from bf2maptool import hosts, texpack

D, OUT = sys.argv[1], sys.argv[2]
os.makedirs(D, exist_ok=True)
host = hosts.get('kamino_01')
N = 128
rng = np.random.default_rng(3)
y, x = np.mgrid[0:N, 0:N].astype(np.float32) / N


def save(name, arr, mode):
    p = os.path.join(D, name + '.png')
    Image.fromarray(arr, mode).save(p)
    return {'image': {'file': p, 'name': name}}


# the known channels (8-bit, so the round trip can be exact)
colour = (np.dstack([x, y, 0.5 + 0.5 * np.sin(8 * x)]) * 255).astype(np.uint8)
rough = (np.clip(0.2 + 0.6 * y, 0, 1) * 255).astype(np.uint8)
metal = ((np.sin(10 * x) > 0) * 255).astype(np.uint8)
alpha = ((np.hypot(x - 0.5, y - 0.5) < 0.35) * 255).astype(np.uint8)
emis = (np.dstack([y, 0.3 * x, 0 * x]) * 255).astype(np.uint8)
# a height field -> a normal map with green UP (image row 0 is the top: +y of the texture is -row)
hgt = np.exp(-((x - 0.5) ** 2 + (y - 0.5) ** 2) * 40) + 0.3 * np.sin(12 * x) * np.cos(9 * y)
dhdx = np.gradient(hgt, axis=1) * N
dhdy = -np.gradient(hgt, axis=0) * N
nz = 1.0 / np.sqrt(1.0 + (0.08 * dhdx) ** 2 + (0.08 * dhdy) ** 2)
n_gl = np.dstack([-0.08 * dhdx * nz, -0.08 * dhdy * nz, nz])
gl8 = np.clip(np.round((n_gl * 0.5 + 0.5) * 255), 0, 255).astype(np.uint8)
dx8 = gl8.copy(); dx8[..., 1] = 255 - gl8[..., 1]

rgba = np.dstack([colour, alpha])
I = {'base_color': save('rt_colour', colour, 'RGB'), 'roughness': dict(save('rt_rough', rough, 'L'), channel='R'),
     'metallic': dict(save('rt_metal', metal, 'L'), channel='R'), 'normal': save('rt_normal_gl', gl8, 'RGB'),
     'emission': save('rt_emis', emis, 'RGB'), 'base_color_alpha': save('rt_colour_alpha', rgba, 'RGBA')}
I_dx = save('rt_normal_dx', dx8, 'RGB')
src = texpack.Source()
res, ok_all = {}, True


def close(a, b, tol=1.0 / 255 + 1e-6):
    return float(np.abs(np.asarray(a, np.float32) - np.asarray(b, np.float32)).max()) <= tol


for cls in ('opaque', 'emissive', 'cutout', 'cutout2s', 'lava', 'glass'):
    tname, T = host.template_for(cls)
    if not T['slots']:
        res[cls] = {'template': tname, 'pass': cls == 'glass', 'note': 'takes no texture: nothing to pack (the window glass surface of the host as it is)'}
        ok_all = ok_all and cls == 'glass'
        continue
    inputs = {'base_color': I['base_color_alpha'] if cls.startswith('cutout') else I['base_color'], 'roughness': I['roughness'],
              'metallic': I['metallic'], 'normal': I['normal'], 'emission': I['emission']}
    r = {'template': tname, 'layouts': {}, 'pass': True}
    for param, layout, const_b, _ in T['slots']:
        notes = []
        tex = texpack.pack(layout, inputs, src, (N, N), const_b, notes)
        u = texpack.unpack(layout, tex)
        c = {}
        if layout == 'CS':
            c['base_colour'] = close(u['base_color_srgb'], colour / 255.0)
            c['roughness'] = close(u['roughness'], rough / 255.0)
        elif layout in ('NAM', 'NW'):
            c['normal_xy_green_up'] = close(u['normal_xy'], gl8[..., :2] / 255.0 * 2 - 1, 2.0 / 255 + 1e-6)
            c['constant_blue'] = u['const_b'] == const_b
            if layout == 'NAM':
                c['metallic'] = close(u['metallic'], metal / 255.0)
            else:
                c['cutout_mask'] = close(u['alpha'], alpha / 255.0)
            c['convention_detected'] = bool(notes) and notes[0]['detected'] == 'opengl'
            # the same map handed in green down must be detected and come out the same
            notes2 = []
            tex2 = texpack.pack(layout, dict(inputs, normal=I_dx), src, (N, N), const_b, notes2)
            c['directx_detected'] = bool(notes2) and notes2[0]['detected'] == 'directx'
            c['directx_packs_to_the_same'] = bool(np.abs(tex2.astype(int) - tex.astype(int)).max() <= 1)
        elif layout == 'E':
            c['emission'] = close(u['emission_srgb'], emis / 255.0)
        r['layouts']['%s (%s)' % (layout, param)] = c
        r['pass'] = r['pass'] and all(c.values())
    res[cls] = r
    ok_all = ok_all and r['pass']
    print('%-5s %-9s template %-9s %s' % ('PASS' if r['pass'] else 'FAIL', cls, tname, {k: all(v.values()) for k, v in r['layouts'].items()}))
print('%-5s %-9s %s' % ('PASS' if res['glass']['pass'] else 'FAIL', 'glass', res['glass']['note']))
json.dump({'ok': ok_all, 'classes': res}, open(OUT, 'w'), indent=1)
print('PASS' if ok_all else 'FAIL')
sys.exit(0 if ok_all else 1)
