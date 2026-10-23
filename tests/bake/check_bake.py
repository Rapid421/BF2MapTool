"""Compare what the exporter baked with what each test material MUST give (worked out here from the source pictures).
python check_bake.py <export dir> <npy dir> <out.json>      exit 0 = every material within tolerance"""
import json
import os
import sys

import numpy as np
from PIL import Image

EXP, NPY, OUT = sys.argv[1:4]
M = json.load(open(os.path.join(EXP, 'map.json'), encoding='utf-8'))
A, B, G, H = [np.load(os.path.join(NPY, n + '.npy')) for n in 'ABGH']


def lin(s):
    return np.where(s <= 0.04045, s / 12.92, np.power((s + 0.055) / 1.055, 2.4))


def srgb(l):
    l = np.clip(l, 0, 1)
    return np.where(l <= 0.0031308, l * 12.92, 1.055 * np.power(l, 1 / 2.4) - 0.055)


def load(rec):
    return np.asarray(Image.open(os.path.join(EXP, rec['image']['file'])).convert('RGBA')).astype(np.float32) / 255.0


def err(got, want):
    """mean and 99th percentile absolute error in 8-bit steps"""
    if got.shape != want.shape:
        return 999.0, 999.0, 'size %s, expected %s' % (got.shape[:2], want.shape[:2])
    d = np.abs(got - want) * 255.0
    return float(d.mean()), float(np.percentile(d, 99)), ''


res, ok = [], True


def case(name, key, want, tol=(1.5, 6.0), must_bake=True):
    global ok
    m = M['materials'][name]
    r = m['inputs'].get(key)
    row = {'material': name, 'input': key, 'baked': bool(isinstance(r, dict) and r.get('baked')), 'why': (m.get('baked') or {}).get('why', {}).get(key)}
    if want is None:                                   # must NOT be baked
        row['pass'] = not row['baked']
    elif not row['baked'] or 'image' not in r:
        row['pass'] = False; row['note'] = 'not baked to an image: %s' % json.dumps(r)[:160]
    else:
        got = load(r)
        g = got[..., :3] if want.ndim == 3 else got[..., 0]
        mean, p99, note = err(g, want)
        row.update(mean_err=round(mean, 2), p99_err=round(p99, 2), size=r['image']['size'], note=note)
        row['pass'] = mean <= tol[0] and p99 <= tol[1]
    ok = ok and row['pass']
    res.append(row)
    return row


LA, LB = lin(A[..., :3]), lin(B[..., :3])
case('bk_plain', 'base_color', None)
case('bk_tiled', 'base_color', np.tile(A[..., :3], (2, 2, 1)))
case('bk_mix', 'base_color', srgb(0.5 * LA + 0.5 * LB))
case('bk_ramp', 'roughness', np.clip((G[..., 0] - 0.25) / 0.5, 0, 1))
case('bk_diffuse', 'base_color', A[..., :3])
case('bk_grouped', 'base_color', srgb(0.5 * LA + 0.5 * 0.5))      # a socket colour is scene-linear
case('bk_alpha', 'alpha', (G[..., 0] > 0.5).astype(np.float32), tol=(3.0, 255.0))
# emission: colour x strength; the picture is stored normalised and the peak is the strength
E = LA * LB * 5.0
row = case('bk_emit', 'emission', srgb(E / E.max()))
es = M['materials']['bk_emit']['inputs'].get('emission_strength', {})
row['strength'] = es.get('value'); row['strength_expected'] = round(float(E.max()), 4)
if row['strength'] is None or abs(row['strength'] - E.max()) > 0.03 * E.max():
    row['pass'] = False; ok = False
# bump: raised domes -> a normal map in OpenGL convention: over the upper half of a dome green is above 0.5
m = M['materials']['bk_bump']; r = m['inputs'].get('normal')
row = {'material': 'bk_bump', 'input': 'normal', 'baked': bool(isinstance(r, dict) and r.get('baked')), 'format': (r or {}).get('normal_format')}
if row['baked'] and 'image' in r:
    n = load(r)
    S = n.shape[0]; c = S / 4
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    dx, dy = (xx % c) - c / 2, (yy % c) - c / 2
    ring = (dx * dx + dy * dy < (c * 0.33) ** 2) & (dx * dx + dy * dy > (c * 0.12) ** 2)
    up, down, left, right = ring & (dy < -c * 0.1), ring & (dy > c * 0.1), ring & (dx < -c * 0.1), ring & (dx > c * 0.1)
    row.update(green_upper=round(float(n[..., 1][up].mean()), 3), green_lower=round(float(n[..., 1][down].mean()), 3),
               red_left=round(float(n[..., 0][left].mean()), 3), red_right=round(float(n[..., 0][right].mean()), 3), size=r['image']['size'])
    # a raised dome: the normal leans outwards = up (green > .5) on its upper side, left (red < .5) on its left side
    row['pass'] = row['green_upper'] > 0.52 and row['green_lower'] < 0.48 and row['red_left'] < 0.48 and row['red_right'] > 0.52 and row['format'] == 'opengl'
else:
    row['pass'] = False
ok = ok and row['pass']; res.append(row)
# procedural: no reference picture; it must be baked to an image that is not flat
m = M['materials']['bk_noise']; r = m['inputs'].get('base_color')
row = {'material': 'bk_noise', 'input': 'base_color', 'baked': bool(isinstance(r, dict) and r.get('baked'))}
if row['baked'] and 'image' in r:
    n = load(r); row.update(size=r['image']['size'], spread=round(float(n[..., :3].std()), 3)); row['pass'] = row['spread'] > 0.02
else:
    row['pass'] = False
ok = ok and row['pass']; res.append(row)
json.dump({'ok': ok, 'bake': M.get('bake'), 'cases': res}, open(OUT, 'w'), indent=1)
for r in res:
    print('%-5s %s' % ('PASS' if r['pass'] else 'FAIL', json.dumps(r)))
print('bake:', json.dumps(M.get('bake')))
sys.exit(0 if ok else 1)
