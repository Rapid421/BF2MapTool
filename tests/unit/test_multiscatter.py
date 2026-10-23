"""texpack.multiscatter_lift: rough metals are lifted toward what Blender's multiscatter GGX shows, never darker, never
past white, hue kept; dielectrics and smooth metals untouched.  python test_multiscatter.py"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime'))
from bf2maptool import texpack as T  # noqa: E402


def lift(c, r, m):
    px = np.tile(T.srgb_encode(np.array(c)) * 255.0, (2, 2, 1)).astype(np.float32)
    return px[0, 0], T.multiscatter_lift(px, np.full((2, 2), r), np.full((2, 2), m))[0, 0]


fails = []
i, o = lift((0.56, 0.57, 0.58), 0.8, 1.0)
if not (o > i + 20).all():
    fails.append('rough steel not lifted: %s -> %s' % (i, o))
i, o = lift((0.56, 0.57, 0.58), 0.15, 1.0)
if np.abs(o - i).max() > 1.0:
    fails.append('smooth steel changed: %s -> %s' % (i, o))
i, o = lift((0.5, 0.5, 0.5), 0.8, 0.0)
if np.abs(o - i).max() > 1e-3:
    fails.append('dielectric changed: %s -> %s' % (i, o))
i, o = lift((1.0, 0.766, 0.336), 0.8, 1.0)
if (o < i - 1e-3).any() or o.max() > 255.001:
    fails.append('gold darkened or past white: %s -> %s' % (i, o))
i, o = lift((0.3, 0.6, 0.9), 0.9, 1.0)                     # hue kept: the channel ratios in linear light
ci, co = T.srgb_decode(i / 255.0), T.srgb_decode(o / 255.0)
if np.abs(co / co.max() - ci / ci.max()).max() > 0.01 or co.max() > 1.0 + 1e-6:
    fails.append('hue not kept / past white: %s -> %s' % (ci, co))
i, o = lift((0.56, 0.57, 0.58), 0.8, 0.5)                  # half metal: half way
_, full = lift((0.56, 0.57, 0.58), 0.8, 1.0)
if not (i < o).all() or not (o < full).all():
    fails.append('half metal not in between: %s < %s < %s' % (i, o, full))
print('PASS' if not fails else 'FAIL', fails)
sys.exit(1 if fails else 0)
