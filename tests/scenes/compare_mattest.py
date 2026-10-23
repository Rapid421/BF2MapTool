"""MatTest, panel by panel: the game's picture against Blender's, at each test object's centre.
    python compare_mattest.py <game shots dir> <ref dir> <mattest.blend.panels.json> <mattest.blend.views.json> <out.json> [vertical fov 55]
Each view's camera (at / look, vertical fov, any picture size) projects every panel centre of its row to a pixel; the
median of a small window there is read from both pictures (8-bit sRGB). Also written: <out>.png, the two pictures of
each row one above the other with the sampled windows boxed and numbered.
Reading it: within a row the ORDER and the RATIOS matter more than absolute levels (the game tone-maps its picture);
the light match measured the overall balance before (within 5 % per light kind).
"""
import json
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

G, R, PANELS, VIEWS, OUT = sys.argv[1:6]
FOV = float(sys.argv[6]) if len(sys.argv) > 6 else 55.0
panels = json.load(open(PANELS))['panels']
views = {v['name']: v for v in json.load(open(VIEWS))['views']}


def project(at, look, p, w, h):
    at, look, p = np.array(at, float), np.array(look, float), np.array(p, float)
    f = look - at; f /= np.linalg.norm(f)
    r = np.cross(f, [0, 0, 1.0]); r /= np.linalg.norm(r)
    u = np.cross(r, f)
    d = p - at
    x, y, z = d @ r, d @ u, d @ f
    t = math.tan(math.radians(FOV) / 2)
    return (x / (z * t * w / h) + 1) / 2 * w, (1 - y / (z * t)) / 2 * h


def sample(img, px, py, half):
    """over the object's area: mean colour, the brightest 5 % (highlights: shininess), the spread (relief, reflections)"""
    a = np.asarray(img.convert('RGB'), np.float32)
    x0, x1 = int(max(px - half, 0)), int(min(px + half + 1, a.shape[1]))
    y0, y1 = int(max(py - half, 0)), int(min(py + half + 1, a.shape[0]))
    q = a[y0:y1, x0:x1].reshape(-1, 3)
    lum = q @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    return {'mean': [round(float(c), 1) for c in q.mean(0)], 'p95': round(float(np.percentile(lum, 95)), 1), 'spread': round(float(lum.std()), 1)}


rows, sheets = {}, []
for name, v in views.items():
    gp, rp = os.path.join(G, name + '.png'), os.path.join(R, name + '.png')
    if not (os.path.exists(gp) and os.path.exists(rp)):
        rows[name] = {'missing': [x for x in (gp, rp) if not os.path.exists(x)]}
        continue
    gi, ri = Image.open(gp), Image.open(rp)
    out = []
    pics = []
    for img in (ri, gi):
        pics.append(img.convert('RGB').resize((960, 540)))
    dr = [ImageDraw.Draw(p) for p in pics]
    for k, p in enumerate([q for q in panels if q['row'] == v.get('row', name)]):
        res = {}
        for tag, img, d in (('blender', ri, dr[0]), ('game', gi, dr[1])):
            w, h = img.size
            px, py = project(v['at'], v['look'], p['centre'], w, h)
            # half the object's size on screen (0.9 m radius / half side), a little inside its outline
            ex, _ = project(v['at'], v['look'], [p['centre'][0] + 0.6, p['centre'][1], p['centre'][2]], w, h)
            half = max(3, int(abs(ex - px)))
            res[tag] = sample(img, px, py, half)
            sx, sy = px * 960 / w, py * 540 / h
            d.rectangle([sx - 6, sy - 6, sx + 6, sy + 6], outline=(255, 0, 255), width=2)
            d.text((sx + 8, sy - 8), str(k + 1), fill=(255, 0, 255))
        res['name'] = p['name']
        out.append(res)
    rows[name] = out
    sheet = Image.new('RGB', (960, 1100), (30, 30, 30))
    sheet.paste(pics[0], (0, 0)); sheet.paste(pics[1], (0, 560))
    ImageDraw.Draw(sheet).text((8, 542), '%s: Blender (top) / game (bottom)' % name, fill=(255, 255, 255))
    sheets.append(sheet)
json.dump({'fov': FOV, 'rows': rows}, open(OUT, 'w'), indent=1)
if sheets:
    big = Image.new('RGB', (960 * len(sheets), 1100))
    for i, s in enumerate(sheets):
        big.paste(s, (i * 960, 0))
    big.save(os.path.splitext(OUT)[0] + '.png')
for name, out in rows.items():
    print(name)
    if isinstance(out, dict):
        print('   ', out); continue
    for r in out:
        print('    %-24s blender %-48s game %s' % (r['name'], r['blender'], r['game']))
