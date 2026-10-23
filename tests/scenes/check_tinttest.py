"""Compare the export of make_tinttest.py's scene with what it must be:  python check_tinttest.py <export dir> <expect.json>
- 3 tinted copies, 4 objects tinted, Grad keeps the plain material
- each copy's baked base colour image has the tint's hue (checker x tint): red copy red, green green, blue blue
"""
import json
import os
import sys

import numpy as np
from PIL import Image

d, ex = sys.argv[1], json.load(open(sys.argv[2]))
m = json.load(open(os.path.join(d, 'map.json'), encoding='utf-8'))
t = m.get('tints') or {}
fails = []
if t.get('variants') != ex['variants']:
    fails.append('variants %s, expected %s' % (t.get('variants'), ex['variants']))
if t.get('objects_tinted') != ex['objects_tinted']:
    fails.append('objects tinted %s, expected %s' % (t.get('objects_tinted'), ex['objects_tinted']))
if t.get('varying_examples') != ex['varying']:
    fails.append('varying %s, expected %s' % (t.get('varying_examples'), ex['varying']))
slots = {}
for o in m['objects']:
    z = np.load(os.path.join(d, o['geo']))
    slots[o['name']] = sorted(set(str(s) for s in z['slots']))
if slots.get('Red1') != slots.get('Red2'):
    fails.append('Red1 and Red2 do not share a copy: %s / %s' % (slots.get('Red1'), slots.get('Red2')))
if slots.get('Grad') != ['LegoPlastic']:
    fails.append('Grad must keep the plain material: %s' % slots.get('Grad'))
means = {}
for name, tint in ex['tints'].items():
    v = slots.get(name, [''])[0]
    rec = m['materials'].get(v) or {}
    bc = (rec.get('inputs') or {}).get('base_color') or {}
    f = (bc.get('image') or {}).get('file')
    if not f:
        fails.append('%s (%s): no baked base colour image: %s' % (name, v, bc))
        continue
    a = np.asarray(Image.open(os.path.join(d, f)).convert('RGB'), np.float32).reshape(-1, 3).mean(0)
    means[name] = a.round(1).tolist()
    if int(np.argmax(a)) != int(np.argmax(tint)) or a.max() < 1.6 * sorted(a)[1]:
        fails.append('%s: baked colour %s does not have the tint %s' % (name, a.round(1).tolist(), tint))
print(json.dumps({'tints': t, 'slots': slots, 'baked_means': means, 'fails': fails}, indent=1))
sys.exit(1 if fails else 0)
