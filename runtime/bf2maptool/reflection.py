"""A reflection cube for the game from an equirectangular capture of the scene.

    cube(pano, scale) -> list of mips, each (6, S, S, 3) float32, S = 128, 64, ... 4
    write_dds(path, mips)   RGBA16F cube, 6 mips - the layout the game's own reflection volumes ship
                            (128 x 128, R16G16B16A16_FLOAT, measured on the host's volumes)

Panorama layout = the exporter's (worldbake.direction): column u -> azimuth, row v -> elevation, Blender axes.
Game axes: scene (x, y, z) = game (x, -z, y).
Each smaller mip is a wider blur of the surroundings (rougher surfaces read smaller mips). The game's exact filter per
mip is NOT known; the widths below are a plain widening lobe (LOBE_DEG) - close for rough metals, a guess in between.
"""
import math
import struct

import numpy as np

SIZE = 128
MIPS = 6
LOBE_DEG = (0.0, 6.0, 14.0, 28.0, 50.0, 80.0)        # half-width of the blur per mip
HALF_MAX = 65504.0


def face_dirs(size):
    """game-space unit directions of a cube's texels, (6, size, size, 3); faces +X -X +Y -Y +Z -Z, D3D layout"""
    c = (np.arange(size, dtype=np.float64) + 0.5) / size * 2.0 - 1.0
    s, t = np.meshgrid(c, c)                          # s right, t down
    one = np.ones_like(s)
    f = [(one, -t, -s), (-one, -t, s), (s, one, t), (s, -one, -t), (s, -t, one), (-s, -t, -one)]
    d = np.stack([np.stack(x, -1) for x in f])
    return d / np.linalg.norm(d, axis=-1, keepdims=True)


def pano_uv(game_dirs):
    """panorama position (u, v in 0..1) of game-space directions"""
    x, y, z = game_dirs[..., 0], -game_dirs[..., 2], game_dirs[..., 1]       # -> scene axes
    el = np.arcsin(np.clip(z, -1.0, 1.0))
    az = np.arctan2(-x, y)
    return (0.5 - az / (2.0 * math.pi)) % 1.0, np.clip(0.5 - el / math.pi, 0.0, 1.0)


def pano_dirs(h, w):
    """game-space directions and solid angles of a panorama's texels"""
    u = (np.arange(w) + 0.5) / w; v = (np.arange(h) + 0.5) / h
    az = (0.5 - u) * 2.0 * math.pi; el = (0.5 - v) * math.pi
    ce = np.cos(el)[:, None]
    sx = -np.sin(az)[None, :] * ce; sy = np.cos(az)[None, :] * ce; sz = np.repeat(np.sin(el)[:, None], w, 1)
    d = np.stack([sx, sz, -sy], -1)                   # scene (x, y, z) -> game (x, z, -y)
    return d, np.repeat(ce, w, 1) * (math.pi / h) * (2.0 * math.pi / w)


def _sample(pano, u, v):
    h, w = pano.shape[:2]
    x = u * w - 0.5; y = v * h - 0.5
    x0 = np.floor(x).astype(np.int64); y0 = np.floor(y).astype(np.int64)
    fx = (x - x0)[..., None]; fy = (y - y0)[..., None]
    x1 = (x0 + 1) % w; x0 %= w
    y1 = np.clip(y0 + 1, 0, h - 1); y0 = np.clip(y0, 0, h - 1)
    return (pano[y0, x0] * (1 - fx) + pano[y0, x1] * fx) * (1 - fy) + (pano[y1, x0] * (1 - fx) + pano[y1, x1] * fx) * fy


def _shrink(pano, w):
    """area-weighted reduction of a panorama to width w (height w / 2)"""
    H, W = pano.shape[:2]
    kx, ky = max(1, W // w), max(1, H // (w // 2))
    return pano[:(H // ky) * ky, :(W // kx) * kx].reshape(H // ky, ky, W // kx, kx, 3).mean((1, 3))


def cube(pano, scale=1.0):
    pano = np.asarray(pano, np.float64)[..., :3] * float(scale)
    out = []
    for m in range(MIPS):
        size = SIZE >> m
        d = face_dirs(size)
        if LOBE_DEG[m] <= 0.0:
            u, v = pano_uv(d)
            out.append(_sample(pano, u, v).astype(np.float32))
            continue
        small = _shrink(pano, 128 if LOBE_DEG[m] < 20 else 64)
        sd, sw = pano_dirs(*small.shape[:2])
        sd = sd.reshape(-1, 3); col = small.reshape(-1, 3) * sw.reshape(-1, 1); sw = sw.reshape(-1)
        k = math.log(2.0) / (1.0 - math.cos(math.radians(LOBE_DEG[m])))       # half weight at the half-width
        dd = d.reshape(-1, 3)
        res = np.empty_like(dd)
        for i in range(0, len(dd), 2048):
            wgt = np.exp(k * (np.clip(dd[i:i + 2048] @ sd.T, -1.0, 1.0) - 1.0))
            res[i:i + 2048] = (wgt @ col) / np.maximum(wgt @ sw, 1e-12)[:, None]
        out.append(res.reshape(6, size, size, 3).astype(np.float32))
    return out


def write_dds(path, mips):
    size = mips[0].shape[1]
    DDSD = 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000          # caps, height, width, pixel format, mip count, linear size
    head = struct.pack('<4sIIIIIII44x', b'DDS ', 124, DDSD, size, size, size * size * 8, 0, len(mips))
    head += struct.pack('<II4sIIIII', 32, 0x4, b'DX10', 0, 0, 0, 0, 0)            # pixel format: a FourCC
    head += struct.pack('<IIII4x', 0x8 | 0x1000 | 0x400000, 0x200 | 0xFC00, 0, 0)    # complex texture with mips; a cube, all faces
    head += struct.pack('<IIIII', 10, 3, 0x4, 1, 0)             # R16G16B16A16_FLOAT, 2D, cube, one cube
    assert len(head) == 148, len(head)
    with open(path, 'wb') as f:
        f.write(head)
        for face in range(6):
            for m in mips:
                a = np.concatenate([np.clip(m[face], 0.0, HALF_MAX), np.ones(m[face].shape[:2] + (1,), np.float32)], -1)
                f.write(a.astype('<f2').tobytes())
