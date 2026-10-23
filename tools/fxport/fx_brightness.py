"""Measure particle brightness in the in-game FX shots (measurement only -- the emitters stay byte-exact).

  python fx_brightness.py <tag> <views.json> [out.json]

fx_views_client.py shoots every view twice, PAIR_GAP seconds of game time apart, with the camera, exposure and every
static pixel shared. Pixels whose luminance changes between the two frames are moving things -- the particles (and any
AI soldier walking through). Per view:
  moving_pct        share of the frame that moved (particle coverage proxy)
  bg_median         median luminance of the static pixels (the lit scene the particles sit on)
  moving_hi_mean    mean of the brighter of the two frames over the moving pixels (particle over background, for
                    additive/lit-bright particles) and moving_lo_mean the darker one (dark smoke over background)
  delta_mean        mean |a - b| over the moving pixels
  clipped_pct       share of moving pixels at >= 250 in either frame (blown out)
  hi_over_bg        moving_hi_mean / bg_median
Luminance = Rec.709 weights on the 8-bit sRGB values (display-referred, what the player sees).
"""
import json
import os
import sys

import numpy as np
from PIL import Image
from scipy import ndimage

SHOTS = r'I:\HothPort\shots'
THRESH = 6.0        # 8-bit luminance change counted as motion (TAA/compression noise stays below it)
MIN_BLOB = 12       # pixels; smaller changed islands are edge shimmer, not particles


def lum(path):
    a = np.asarray(Image.open(path).convert('RGB'), dtype=np.float32)
    return a[..., 0] * 0.2126 + a[..., 1] * 0.7152 + a[..., 2] * 0.0722, a


def measure(pa, pb):
    la, ra = lum(pa)
    lb, rb = lum(pb)
    if la.shape != lb.shape:
        return {'error': 'size %s vs %s' % (la.shape, lb.shape)}
    d = np.abs(la - lb)
    m = ndimage.binary_opening(d > THRESH, structure=np.ones((2, 2)))
    lab, n = ndimage.label(m)
    if n:
        sizes = ndimage.sum(m, lab, index=np.arange(1, n + 1))
        keep = np.zeros(n + 1, bool)
        keep[1:] = sizes >= MIN_BLOB
        m = keep[lab]
    static = ~ndimage.binary_dilation(m, iterations=3)
    out = {'moving_pct': round(100.0 * m.mean(), 3), 'bg_median': round(float(np.median(la[static])), 1)}
    if m.any():
        hi = np.maximum(la, lb)[m]
        lo = np.minimum(la, lb)[m]
        clip = (np.maximum(ra.max(-1), rb.max(-1))[m] >= 250)
        out.update({'moving_hi_mean': round(float(hi.mean()), 1), 'moving_lo_mean': round(float(lo.mean()), 1),
                    'moving_hi_p95': round(float(np.percentile(hi, 95)), 1),
                    'delta_mean': round(float(d[m].mean()), 1), 'clipped_pct': round(100.0 * float(clip.mean()), 2),
                    'hi_over_bg': round(float(hi.mean()) / max(1.0, out['bg_median']), 3)})
    return out


def main():
    tag, views = sys.argv[1], json.load(open(sys.argv[2]))
    res = {}
    for v in views:
        name = v[0]
        pa = os.path.join(SHOTS, '%s_%s.png' % (tag, name))
        pb = os.path.join(SHOTS, '%s_%s_b.png' % (tag, name))
        if not (os.path.exists(pa) and os.path.exists(pb)):
            res[name] = {'error': 'missing shot(s)'}
            continue
        res[name] = measure(pa, pb)
    out = sys.argv[3] if len(sys.argv) > 3 else os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                                                             'brightness_%s.json' % tag)
    json.dump(res, open(out, 'w'), indent=1)
    cols = ('moving_pct', 'bg_median', 'moving_hi_mean', 'moving_lo_mean', 'moving_hi_p95', 'delta_mean', 'clipped_pct', 'hi_over_bg')
    print('%-34s ' % 'view' + ' '.join('%14s' % c for c in cols))
    for name, r in res.items():
        print('%-34s ' % name + ' '.join('%14s' % r.get(c, '-') for c in cols) + ('  ' + r['error'] if 'error' in r else ''))
    print('->', out)


if __name__ == '__main__':
    main()
