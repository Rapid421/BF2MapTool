import numpy as np
def bc3_decode(buf, w, h):
    """Decode BC3/DXT5 mip0 to RGBA uint8 (h, w, 4)."""
    bw, bh = (w + 3) // 4, (h + 3) // 4
    n = bw * bh
    b = np.frombuffer(buf[:n * 16], dtype=np.uint8).reshape(n, 16)
    # alpha
    a0 = b[:, 0].astype(np.int32); a1 = b[:, 1].astype(np.int32)
    abits = np.zeros(n, dtype=np.uint64)
    for i in range(6):
        abits |= b[:, 2 + i].astype(np.uint64) << np.uint64(8 * i)
    idx = np.stack([((abits >> np.uint64(3 * k)) & np.uint64(7)).astype(np.int32) for k in range(16)], axis=1)
    pal = np.zeros((n, 8), dtype=np.int32)
    pal[:, 0] = a0; pal[:, 1] = a1
    gt = a0 > a1
    for k in range(1, 7):
        pal[:, 1 + k] = np.where(gt, ((7 - k) * a0 + k * a1) // 7, pal[:, 1 + k])
    for k in range(1, 5):
        pal[:, 1 + k] = np.where(~gt, ((5 - k) * a0 + k * a1) // 5, pal[:, 1 + k])
    pal[:, 6] = np.where(~gt, 0, pal[:, 6]); pal[:, 7] = np.where(~gt, 255, pal[:, 7])
    alpha = np.take_along_axis(pal, idx, axis=1)
    # color
    c0 = b[:, 8].astype(np.int32) | (b[:, 9].astype(np.int32) << 8)
    c1 = b[:, 10].astype(np.int32) | (b[:, 11].astype(np.int32) << 8)
    def rgb(c):
        r = ((c >> 11) & 31) * 255 // 31; g = ((c >> 5) & 63) * 255 // 63; bb = (c & 31) * 255 // 31
        return np.stack([r, g, bb], axis=1)
    p0, p1 = rgb(c0), rgb(c1)
    cols = np.stack([p0, p1, (2 * p0 + p1) // 3, (p0 + 2 * p1) // 3], axis=1)
    cbits = b[:, 12].astype(np.uint32) | (b[:, 13].astype(np.uint32) << 8) | (b[:, 14].astype(np.uint32) << 16) | (b[:, 15].astype(np.uint32) << 24)
    cidx = np.stack([((cbits >> (2 * k)) & 3).astype(np.int32) for k in range(16)], axis=1)
    color = np.take_along_axis(cols, cidx[:, :, None].repeat(3, axis=2), axis=1)
    px = np.concatenate([color, alpha[:, :, None]], axis=2).astype(np.uint8)  # (n,16,4)
    px = px.reshape(bh, bw, 4, 4, 4).transpose(0, 2, 1, 3, 4).reshape(bh * 4, bw * 4, 4)
    return px[:h, :w]
