"""A shipped BFII sky for a port: DICE's panorama (the upper half of the sphere, BC6H HDR exported with FrostyCLI and
converted by texconv) -> a full equirectangular .hdr the tool's HDR sky mode reads (sky on top, a dim ground below),
and the sun direction measured on the panorama itself (its brightest spot), so the baked and real-time sun sit exactly
where the sky's sun glow is.
    python dice_sky.py <sky_half.hdr> <out_full.hdr> [--ground 0.05]  -> prints the sun azimuth / elevation (json)
"""
import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'runtime'))
from bf2maptool import hdr


def main(argv):
    ground = 0.05
    if '--ground' in argv:
        i = argv.index('--ground'); ground = float(argv[i + 1]); del argv[i:i + 2]
    src, out = argv[0], argv[1]
    half = hdr.read(src).astype(np.float32)               # (H, W, 3), H = W / 4: horizon at the bottom row
    H, W = half.shape[:2]
    lum = half @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    # the sun: centroid of the brightest 0.01 % of pixels (a single hot pixel would be noise)
    k = max(16, lum.size // 10000)
    idx = np.argpartition(lum.ravel(), -k)[-k:]
    ys, xs = np.unravel_index(idx, lum.shape)
    w = lum.ravel()[idx]
    # azimuth is circular: average on the unit circle
    ang = xs / W * 2 * math.pi
    az = math.atan2((np.sin(ang) * w).sum(), (np.cos(ang) * w).sum()) % (2 * math.pi)
    y = float((ys * w).sum() / w.sum())
    elev = 90.0 * (1.0 - (y + 0.5) / H)                   # row 0 = zenith, last row = horizon
    full = np.zeros((H * 2, W, 3), np.float32)
    full[:H] = half
    horizon = half[-max(1, H // 64):].mean(axis=(0, 1))    # the ground: the horizon's own colour, dimmed
    full[H:] = horizon * ground
    hdr.write(out, full)
    info = {'sun_azimuth_deg_of_panorama_u': round(math.degrees(az), 2), 'sun_u': round(az / (2 * math.pi), 4),
            'sun_elevation_deg': round(elev, 2), 'sky_peak': float(lum.max()), 'sky_mean': float(lum.mean()), 'out': out,
            'size': [W, H * 2]}
    print(json.dumps(info))


if __name__ == '__main__':
    main(sys.argv[1:])
