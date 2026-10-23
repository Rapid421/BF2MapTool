"""Light match: the game's pictures against Blender's own render, view by view.
    python measure.py <game review folder> <reference.json> <out.json>

Every view looks at one grey patch (or a glowing panel, or a piece of sky). Blender's number is the scene-linear radiance
Cycles renders there; the game's number is the screenshot's centre, 8-bit sRGB -> display-linear.
The game tone-maps its picture, so the two cannot be divided directly. The SUNLIT RAMP (eight reflectances under the
same light) gives the game's response curve: display value against Blender radiance. Every other view is then read
back through that curve: "this patch looks, in game, like a sunlit patch of radiance X" - and X against Blender's
radiance for the view is the error of that light against sunlight (1.0 = same balance as in Blender).

  exposure     how bright the game shows the sunlit ramp against Blender's display (Standard view: display = radiance)
  shade        sky + bounce light against sunlight
  bays         point / spot / area lamps, the baked lamp, the glowing panel's light
  glow_direct  the panel itself; sky views: the sky itself and whether each view shows the colour Blender shows there
"""
import json
import math
import os
import sys

import numpy as np
from PIL import Image

D, REF, OUT = sys.argv[1:4]
ref = json.load(open(REF))
LUM = np.array([0.2126, 0.7152, 0.0722])


def eotf(c):
    c = np.asarray(c, np.float64)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def oetf(x):
    x = np.clip(np.asarray(x, np.float64), 0.0, 1.0)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * x ** (1 / 2.4) - 0.055)


def game(name):
    p = os.path.join(D, name + '.png')
    if not os.path.exists(p):
        return None
    im = np.asarray(Image.open(p).convert('RGB'), np.float64) / 255.0
    h, w = im.shape[:2]
    c = im[int(h * 0.46):int(h * 0.54), int(w * 0.47):int(w * 0.53)]
    srgb = c.reshape(-1, 3).mean(0)
    return {'srgb8': [round(float(x) * 255, 1) for x in srgb], 'linear': [float(x) for x in eotf(srgb)], 'spread8': round(float(c.reshape(-1, 3).mean(1).std() * 255), 1)}


bexp = 2.0 ** float(ref.get('exposure', 0.0))
rows = {}
for name, v in ref['views'].items():
    g = game(name)
    x = np.array(v['linear_rgb']) * bexp
    rows[name] = {'kind': v['kind'], 'blender_linear': [round(float(a), 5) for a in x], 'blender_lum': float(x @ LUM),
                  'blender_srgb8': [round(float(a) * 255, 1) for a in oetf(x)], 'game': g,
                  'game_lum': float(np.array(g['linear']) @ LUM) if g else None, **{k: v[k] for k in ('reflectance', 'bay', 'expect') if k in v}}
res = {'folder': D, 'views': rows}

# the game's response on the sunlit ramp: Blender radiance -> game display-linear
ramp = sorted((r['blender_lum'], r['game_lum']) for r in rows.values() if r['kind'] == 'ramp_sun' and r['game_lum'] is not None)
if len(ramp) >= 4:
    xs, gs = np.array([a for a, _ in ramp]), np.array([b for _, b in ramp])
    res['sun_ramp'] = [{'blender_lum': round(float(a), 4), 'game_lum': round(float(b), 4), 'game_over_blender': round(float(b / a), 3)} for a, b in ramp]
    usable = (gs > 0.01) & (gs < 0.75) & (np.diff(np.concatenate([[-1.0], gs])) > 0)
    res['exposure'] = {'game_over_blender_display': round(float(np.median(gs[usable] / xs[usable])), 3) if usable.any() else None,
                       'usable_patches': int(usable.sum()),
                       'note': '1.0 = a sunlit surface is as bright on the game screen as in Blender (Standard view); the mid patches only'}
    mono = np.all(np.diff(gs) > 0)

    def back(g):
        """Blender radiance of the sunlit patch that looks like g in game (log-log interpolation along the ramp)"""
        if g is None or g <= 0:
            return None, 'black'
        lg, lx = np.log(np.maximum(gs, 1e-6)), np.log(xs)
        if g < gs[0] * 0.98:
            k = (lx[1] - lx[0]) / max(lg[1] - lg[0], 1e-9)
            return float(np.exp(lx[0] + (math.log(g) - lg[0]) * k)), 'below the ramp (extrapolated)'
        if g > gs[-1] * 1.02:
            k = (lx[-1] - lx[-2]) / max(lg[-1] - lg[-2], 1e-9)
            return float(np.exp(lx[-1] + (math.log(g) - lg[-1]) * k)), 'above the ramp (extrapolated)'
        return float(np.exp(np.interp(math.log(g), lg, lx))), 'on the ramp'
    res['ramp_monotone'] = bool(mono)
    groups = {}
    for name, r in rows.items():
        if r['kind'] == 'ramp_sun' or r['game_lum'] is None:
            continue
        x, how = back(r['game_lum'])
        r['looks_like_sunlit_radiance'] = round(x, 5) if x else None
        r['read'] = how
        r['against_blender'] = round(x / r['blender_lum'], 3) if x and r['blender_lum'] > 0 else None
        key = r['kind'] if r['kind'] != 'bay' else 'bay_' + r['bay']
        if r['against_blender'] is not None and how == 'on the ramp':
            groups.setdefault(key, []).append(r['against_blender'])
    res['against_sunlight'] = {k: {'median': round(float(np.median(v)), 3), 'min': round(float(min(v)), 3), 'max': round(float(max(v)), 3), 'n': len(v)}
                               for k, v in sorted(groups.items())}
# colour of the sunlit mid patch and of each sky view
mid = rows.get('sun_3')
if mid and mid['game']:
    g, b = np.array(mid['game']['linear']), np.array(mid['blender_linear'])
    res['sun_colour'] = {'game_r_over_g': round(float(g[0] / g[1]), 3), 'blender_r_over_g': round(float(b[0] / b[1]), 3),
                         'game_b_over_g': round(float(g[2] / g[1]), 3), 'blender_b_over_g': round(float(b[2] / b[1]), 3)}
sky = {}
for name, r in rows.items():
    if r['kind'] in ('sky_marker', 'sky_plain') and r['game']:
        g, b = np.array(r['game']['linear']), np.array(r['blender_linear'])
        sky[name] = {'blender_brightest_channels': [int(i) for i in np.argsort(-b)[:2]] if r['kind'] == 'sky_marker' else None,
                     'game_srgb8': r['game']['srgb8'], 'blender_srgb8': r['blender_srgb8'],
                     'same_hue': bool(np.argmax(g) == np.argmax(b) and np.argmin(g) == np.argmin(b))}
res['sky'] = sky
json.dump(res, open(OUT, 'w'), indent=1)
print('%-18s %-11s %10s %14s %14s %10s  %s' % ('view', 'kind', 'refl', 'Blender sRGB', 'game sRGB', 'vs sun', 'read'))
for name, r in rows.items():
    g = r['game']
    print('%-18s %-11s %10s %14s %14s %10s  %s' % (name, r['kind'], r.get('reflectance', ''), '%3.0f %3.0f %3.0f' % tuple(r['blender_srgb8']),
                                                 ('%3.0f %3.0f %3.0f' % tuple(g['srgb8'])) if g else 'no picture', r.get('against_blender', ''), r.get('read', '')))
print('exposure', res.get('exposure'))
print('against sunlight', json.dumps(res.get('against_sunlight'), indent=1))
print('sun colour', res.get('sun_colour'))
print('sky', json.dumps(res.get('sky')))
