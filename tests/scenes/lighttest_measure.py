"""Floor brightness of the four bays of the light test, from the game pictures (linear luminance of the same picture
region in every bay picture: the floor in front of the back wall).   python lighttest_measure.py <review folder> <out.json>"""
import json
import os
import sys

import numpy as np
from PIL import Image

D, OUT = sys.argv[1], sys.argv[2]
TAGS = [b['tag'] for b in json.load(open(sys.argv[3]))] if len(sys.argv) > 3 else ['baked', 'dynamic', 'both', 'none']
res = {}
for tag in TAGS:
    for k in ('01', '00'):
        p = os.path.join(D, 'bay_%s_%s.png' % (tag, k))
        if os.path.exists(p):
            break
    else:
        res[tag] = None; continue
    im = np.asarray(Image.open(p).convert('RGB'), np.float64) / 255.0
    lin = np.where(im <= 0.04045, im / 12.92, ((im + 0.055) / 1.055) ** 2.4)
    lum = lin @ np.array([0.2126, 0.7152, 0.0722])
    h, w = lum.shape
    floor = lum[int(h * 0.62):int(h * 0.80), int(w * 0.35):int(w * 0.65)]
    wall = lum[int(h * 0.30):int(h * 0.45), int(w * 0.40):int(w * 0.60)]
    res[tag] = {'picture': os.path.basename(p), 'floor_linear': round(float(floor.mean()), 5), 'wall_linear': round(float(wall.mean()), 5),
                'floor_8bit': round(float(im[int(h * 0.62):int(h * 0.80), int(w * 0.35):int(w * 0.65)].mean() * 255), 1)}
if len(sys.argv) > 3:      # a calibration row: just the table (pictures are tone-mapped: compare by matching, not by ratio)
    json.dump(res, open(OUT, 'w'), indent=1)
    for t in TAGS:
        print('%-14s floor 8-bit %6.1f   wall linear %.4f' % (t, res[t]['floor_8bit'], res[t]['wall_linear']) if res[t] else t + ' (no picture)')
    sys.exit(0)
n = (res.get('none') or {}).get('floor_linear') or 0.0
for tag in ('baked', 'dynamic', 'both'):
    if res.get(tag):
        res[tag]['floor_minus_none'] = round(res[tag]['floor_linear'] - n, 5)
b, d, bo = [(res.get(t) or {}).get('floor_minus_none') for t in ('baked', 'dynamic', 'both')]
res['ratios'] = {'baked_over_dynamic': round(b / d, 3) if b is not None and d else None,
                 'both_over_baked_plus_dynamic': round(bo / (b + d), 3) if None not in (b, d, bo) and (b + d) else None}
json.dump(res, open(OUT, 'w'), indent=1)
print(json.dumps(res, indent=1))
