"""Blender picture (left) and game picture (right) of the same view on one sheet, with the mean colour of each half
printed under it.   python side_by_side.py <blender pictures dir> <game pictures dir> <out dir>
Also writes numbers.json: per view the mean sRGB of both pictures and of their centre thirds, and the difference.
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

A, B, OUT = sys.argv[1:4]
os.makedirs(OUT, exist_ok=True)
res = {}
for f in sorted(os.listdir(A)):
    if not f.endswith('.png') or not os.path.exists(os.path.join(B, f)):
        continue
    a = Image.open(os.path.join(A, f)).convert('RGB')
    b = Image.open(os.path.join(B, f)).convert('RGB').resize(a.size, Image.LANCZOS)
    w, h = a.size
    sheet = Image.new('RGB', (w * 2 + 12, h + 34), (20, 20, 20))
    sheet.paste(a, (0, 0)); sheet.paste(b, (w + 12, 0))
    na, nb = np.asarray(a, np.float64), np.asarray(b, np.float64)

    def stat(x):
        c = x[h // 3: 2 * h // 3, w // 3: 2 * w // 3]
        return [round(float(v), 1) for v in x.reshape(-1, 3).mean(0)], [round(float(v), 1) for v in c.reshape(-1, 3).mean(0)]
    ma, ca = stat(na); mb, cb = stat(nb)
    d = ImageDraw.Draw(sheet)
    d.text((6, h + 10), 'Blender   mean %s   centre %s' % (ma, ca), fill=(230, 230, 230))
    d.text((w + 18, h + 10), 'game   mean %s   centre %s' % (mb, cb), fill=(230, 230, 230))
    sheet.save(os.path.join(OUT, f))
    res[f[:-4]] = {'blender_mean': ma, 'game_mean': mb, 'blender_centre': ca, 'game_centre': cb,
                   'mean_difference': [round(y - x, 1) for x, y in zip(ma, mb)]}
    print('%-12s Blender %s  game %s  difference %s' % (f[:-4], ma, mb, res[f[:-4]]['mean_difference']))
json.dump(res, open(os.path.join(OUT, 'numbers.json'), 'w'), indent=1)
print(len(res), 'sheets ->', OUT)
