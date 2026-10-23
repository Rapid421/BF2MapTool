"""map_art.py <heights.r32> <out.png> <title> [--area x0,z0,x1,z1] [--view x0,z0,x1,z1] [--size 1920x1080]

Art for a new level's loading screen and Kyber tile, made from its own heightmap: a shaded relief in Tatooine sand
colours (sun from the north-west), the play-area boundary drawn on it, and the level's name. North (+z) is up.
"""
import json
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont


def main(argv):
    src, out, title = argv[:3]
    opt = lambda k, d=None: argv[argv.index(k) + 1] if k in argv else d
    meta = json.load(open(src + '.json'))
    n = meta['width']
    h = np.fromfile(src, dtype='<f4').reshape(meta['height'], n)
    x0w, x1w, z0w, z1w = meta['worldMinX'], meta['worldMaxX'], meta['worldMinZ'], meta['worldMaxZ']
    W, H = [int(v) for v in opt('--size', '1920x1080').split('x')]
    area = [float(v) for v in opt('--area', '35,5,200,175').split(',')]
    view = [float(v) for v in opt('--view', '-60,-40,300,220').split(',')]
    # fit the view to the image aspect
    vx0, vz0, vx1, vz1 = view
    cx, cz = (vx0 + vx1) / 2, (vz0 + vz1) / 2
    span_x, span_z = vx1 - vx0, vz1 - vz0
    if span_x / span_z < W / H:
        span_x = span_z * W / H
    else:
        span_z = span_x * H / W
    vx0, vx1, vz0, vz1 = cx - span_x / 2, cx + span_x / 2, cz - span_z / 2, cz + span_z / 2
    # sample the heightfield on the image grid (row 0 = north = max z)
    xs = np.linspace(vx0, vx1, W)
    zs = np.linspace(vz1, vz0, H)
    fi = (xs - x0w) / (x1w - x0w) * (n - 1)
    fj = (zs - z0w) / (z1w - z0w) * (n - 1)
    I, J = np.meshgrid(np.clip(fi, 0, n - 1.001), np.clip(fj, 0, n - 1.001))
    i0, j0 = np.floor(I).astype(int), np.floor(J).astype(int)
    tx, tz = I - i0, J - j0
    g = (h[j0, i0] * (1 - tx) + h[j0, i0 + 1] * tx) * (1 - tz) + (h[j0 + 1, i0] * (1 - tx) + h[j0 + 1, i0 + 1] * tx) * tz
    # hillshade: metres per pixel on each axis, light from the north-west, 45 degrees up
    mx, mz = span_x / W, span_z / H
    gz, gx = np.gradient(g, mz, mx)
    gz = -gz                                   # image rows run south
    nx, nz, ny = -gx, -gz, np.ones_like(g)
    ln = np.sqrt(nx * nx + ny * ny + nz * nz)
    L = np.array([-0.5, 0.707, 0.5]); L = L / np.linalg.norm(L)
    shade = np.clip((nx * L[0] + ny * L[1] + nz * L[2]) / ln, 0, 1)
    rel = (g - g.min()) / max(1e-6, g.max() - g.min())
    sand_lo, sand_hi = np.array([150, 104, 62]), np.array([236, 196, 140])
    base = sand_lo + (sand_hi - sand_lo) * rel[..., None]
    rgb = base * (0.45 + 0.75 * shade[..., None])
    img = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), 'RGB').filter(ImageFilter.GaussianBlur(0.6))
    d = ImageDraw.Draw(img, 'RGBA')
    px = lambda x, z: ((x - vx0) / span_x * W, (vz1 - z) / span_z * H)
    a0, a1 = px(area[0], area[3]), px(area[2], area[1])
    d.rectangle([a0, a1], outline=(255, 244, 214, 230), width=4)
    d.rectangle([(0, 0), (W, H)], outline=(0, 0, 0, 0))
    # vignette + title
    vg = Image.new('L', (W, H), 0)
    ImageDraw.Draw(vg).ellipse([-W * 0.15, -H * 0.2, W * 1.15, H * 1.2], fill=255)
    vg = vg.filter(ImageFilter.GaussianBlur(120))
    img = Image.composite(img, Image.new('RGB', (W, H), (18, 12, 8)), vg)
    d = ImageDraw.Draw(img, 'RGBA')
    try:
        font = ImageFont.truetype('bahnschrift.ttf', 92)
        small = ImageFont.truetype('bahnschrift.ttf', 34)
    except OSError:
        font = small = ImageFont.load_default()
    d.text((80, H - 190), title.upper(), font=font, fill=(255, 246, 226, 240))
    d.text((84, H - 86), 'TATOOINE  •  DUNE SEA', font=small, fill=(240, 220, 190, 200))
    img.save(out)
    print(out, img.size, 'view x %.0f..%.0f z %.0f..%.0f' % (vx0, vx1, vz0, vz1))


if __name__ == '__main__':
    main(sys.argv[1:])
