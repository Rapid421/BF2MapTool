"""THROW-AWAY regression helper (work order step 3), not part of the shipped tool: writes into the exported map.json the
choices the Mustafar port made by hand, so the runtime's result can be compared with the port's MUS28:
  * play centre / radius;
  * per material, the lit blend's maps the port packed (its table BF205 in work/pack_mus.py, read from the port's own
    plan here): 2K colour upscale, roughness map (+ power), metal, normal, emission map and strength; roughness 1.0
    where the lit blend has no map; the 2005 glow rule (colour x alpha) pre-multiplied into an image for glow
    materials without an emission map.
  python patch_map.py <export dir> <lit blend textures dir> <port plan_mus.json>
"""
import json
import os
import re
import sys

import numpy as np
from PIL import Image

EXP, BF, PLAN = sys.argv[1], sys.argv[2].replace('\\', '/'), json.load(open(sys.argv[3]))
# the port's table (pack_mus.py BF205), verbatim
BF205 = {
    'mus1_bldg_control01':        {'c': 'FloorAndSteps.png', 'n': 'FloorAndSteps_nrm.png', 'r': 'FloorAndSteps_rgh.png', 'rpow': 1.0, 'metal': False},
    'mus1_bldg_control011_floor': {'c': 'Floors.png', 'n': 'Floors_nrm.png', 'r': 'Floors_rgh.png', 'rpow': 0.25, 'metal': True},
    'mus1_bldg_control02':        {'c': 'screensAndFlooring.png', 'n': 'screensAndFlooring_nrm.png', 'e': 'screensAndFlooring_em.png', 'estr': 3.0},
    'mus1_bldg_control06':        {'c': 'interior_and_lights_upscale.png', 'e': 'interior_and_lights_upscale_em.png', 'estr': 5.0},
    'mus1_bldg_control07':        {'c': 'screens_upscale.png', 'e': 'screens_upscale_em.png', 'estr': 3.0},
    'mus1_bldg_control08':        {'c': 'StepsAndTrim.png', 'n': 'StepsAndTrim_nrm.png', 'r': 'StepsAndTrim_rgh.png', 'rpow': 1.0, 'metal': True,
                                   'e': 'StepsAndTrim_em.png', 'estr': 5.0},
    'mus1_bldg_control09_tile':   {'c': 'Metal.png', 'n': 'Metal_nrm.png'},
    'mus1_bldg_control010_details': {'c': 'MetalTrimming.png', 'n': 'MetalTrimming_nrm.png'},
    'mus1_bldg_lavawalk':         {'c': 'MetalGrateAndLight.png', 'n': 'MetalGrateAndLight_Nrm.png', 'r': 'MetalGrateAndLight_Rgh.png', 'rpow': 1.0,
                                   'metal': True, 'e': 'MetalGrateAndLight_em.png', 'estr': 10.0},
    'mus1_bldg_grate_1':          {'c': 'grate01.png'},
    'mus1_bldg_control15_grill':  {'c': 'grate02.png'},
    'mus1_main_1':                {'sconst': 0.26},
}
p = EXP + '/map.json'
m = json.load(open(p, encoding='utf-8'))
m['play'] = {'centre': [10.0, 30.0, 68.0], 'radius': 160.0, 'measured': 'Mustafar port value'}


def img(fn):
    return {'image': {'name': fn, 'file': BF + '/' + fn, 'missing': not os.path.exists(BF + '/' + fn)}, 'channel': 'RGB', 'invert': False, 'power': 1.0, 'scale': 1.0}


n = 0
for name, rec in m['materials'].items():
    if rec.get('skip'):
        continue
    if name not in PLAN:
        # door-only materials (the port packed them by hand in door_gen_mus.py: 2005 pixels, smoothness 0, flat normal)
        rec['inputs']['roughness'] = {'value': 1.0}
        rec['inputs']['metallic'] = {'value': 0.0}
        rec['inputs'].pop('normal', None)
        rec['inputs'].pop('alpha', None)
        continue
    base = re.sub(r'_\d+$', '', name)
    b = BF205.get(base, {})
    inp = rec['inputs']
    c2005 = inp['base_color']                      # the rip's own 2005 texture
    if 'c' in b:
        inp['base_color'] = img(b['c'])
    if 'r' in b:
        inp['roughness'] = dict(img(b['r']), power=b['rpow'])
        inp['metallic'] = img(b['r']) if b.get('metal') else {'value': 0.0}
    else:
        inp['roughness'] = {'value': 1.0 - b.get('sconst', 0.0)}
        inp['metallic'] = {'value': 0.0}
    if 'n' in b:
        inp['normal'] = img(b['n'])
    else:
        inp.pop('normal', None)
    cls = rec['class']
    if cls in ('emissive', 'lava'):
        if 'e' in b:
            inp['emission'] = img(b['e']); strength = b['estr']
        elif cls == 'emissive':
            # 2005 glow rule: colour x alpha (at the colour map's resolution)
            col = Image.open(BF + '/' + b['c']) if 'c' in b else Image.open(EXP + '/' + c2005['image']['file'])
            col = np.asarray(col.convert('RGBA')).astype(np.float32)
            o = Image.open(EXP + '/' + c2005['image']['file']).convert('RGBA')
            if o.size != (col.shape[1], col.shape[0]):
                o = o.resize((col.shape[1], col.shape[0]), Image.LANCZOS)
            a = np.asarray(o).astype(np.float32)[..., 3:4] / 255
            fn = 'tex/%s_glow2005.png' % base
            Image.fromarray(np.dstack([np.round(col[..., :3] * a), np.full(col.shape[:2], 255.0)]).astype(np.uint8), 'RGBA').save(EXP + '/' + fn)
            inp['emission'] = {'image': {'name': base + '_glow2005.png', 'file': fn}, 'channel': 'RGB', 'invert': False, 'power': 1.0, 'scale': 1.0}
            strength = 5.0
        else:
            inp['emission'] = dict(inp['base_color']); strength = 3.0
        inp['emission_strength'] = {'value': strength}
    if cls in ('cutout', 'cutout2s'):
        inp['alpha'] = dict(inp['base_color'], channel='A')
    else:
        inp.pop('alpha', None)
    n += 1
json.dump(m, open(p, 'w', encoding='utf-8'), indent=1)
print('patched materials', n, 'play', m['play'])

# the port's navmesh height band (game y 385..430 = scene z 45..90) and its last emissive scale (MUS20: K 280)
m = json.load(open(p, encoding='utf-8'))
m['navmesh'] = {'z_range': [45.0, 90.0]}
m.setdefault('look', {})['emissive_scale'] = 280.0
json.dump(m, open(p, 'w', encoding='utf-8'), indent=1)

# the port's three lava kill boxes (work/kill_plan.json, game coordinates with the port's offset (-96, 340, 77)):
# game (x, y, z) = (bx - 96, bz + 340, 77 - by)  ->  scene (bx, by, bz); half (x, y, z) -> scene half (x, z, y)
KP = os.path.join(os.path.dirname(sys.argv[3]), 'kill_plan.json')
if os.path.exists(KP):
    m = json.load(open(p, encoding='utf-8'))
    m['kill_volumes'] = []
    for name, b in json.load(open(KP)).items():
        cx, cy, cz = b['centre']; hx, hy, hz = b['half']
        m['kill_volumes'].append({'name': 'lava_' + name, 'centre': [cx + 96.0, 77.0 - cz, cy - 340.0], 'half': [hx, hz, hy],
                                  'axes': [[1, 0, 0], [0, 1, 0], [0, 0, 1]]})
    json.dump(m, open(p, 'w', encoding='utf-8'), indent=1)
    print('kill volumes', [k['name'] for k in m['kill_volumes']])
