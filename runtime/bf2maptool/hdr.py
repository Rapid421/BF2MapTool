"""Radiance .hdr (RGBE) reader / writer in numpy: exact linear values, no colour management.
read(path) -> float32 (H, W, 3), row 0 = top of the image; write(path, array) with row 0 = top."""
import numpy as np


def read(path):
    with open(path, 'rb') as f:
        d = f.read()
    if not d.startswith(b'#?'):
        raise ValueError('%s is not a Radiance .hdr file' % path)
    i = d.index(b'\n\n') + 2
    j = d.index(b'\n', i)
    dims = d[i:j].decode().split()
    if len(dims) != 4 or dims[0] != '-Y' or dims[2] != '+X':
        raise ValueError('%s: unsupported .hdr orientation "%s"' % (path, ' '.join(dims)))
    h, w = int(dims[1]), int(dims[3])
    p = j + 1
    img = np.zeros((h, w, 4), np.uint8)
    for y in range(h):
        if w >= 8 and d[p] == 2 and d[p + 1] == 2 and (d[p + 2] << 8 | d[p + 3]) == w:
            p += 4
            for c in range(4):
                x = 0
                while x < w:
                    n = d[p]; p += 1
                    if n > 128:
                        n -= 128
                        img[y, x:x + n, c] = d[p]; p += 1
                    else:
                        img[y, x:x + n, c] = np.frombuffer(d[p:p + n], np.uint8); p += n
                    x += n
        else:
            img[y] = np.frombuffer(d[p:p + 4 * w], np.uint8).reshape(w, 4); p += 4 * w
    e = img[..., 3].astype(np.int32)
    return np.where(e[..., None] > 0, img[..., :3] * np.ldexp(1.0, (e - 136))[..., None], 0).astype(np.float32)


def write(path, arr):
    a = np.maximum(np.asarray(arr, np.float32), 0)
    h, w = a.shape[:2]
    m = a.max(2)
    e = np.where(m > 1e-32, np.floor(np.log2(np.maximum(m, 1e-32))) + 1, -128).astype(np.int32)
    e = np.clip(e, -128, 127)
    sc = np.ldexp(1.0, 8 - e)
    rgb = np.clip(np.floor(a * sc[..., None]), 0, 255).astype(np.uint8)
    ex = np.where(m > 1e-32, e + 128, 0).astype(np.uint8)
    with open(path, 'wb') as f:
        f.write(b'#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y %d +X %d\n' % (h, w))
        f.write(np.concatenate([rgb, ex[..., None]], 2).tobytes())
