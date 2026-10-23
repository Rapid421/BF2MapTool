"""LSWTSS .dno collision shapes (big-endian), see lswtss_collision.py for the layout."""
import struct

import numpy as np

_dno_cache = {}


def read_dno(path):
    if path in _dno_cache:
        return _dno_cache[path]
    b = path.read_bytes()
    s = b.find(b'SERIALISED')
    meshes = []
    o = max(s, 0)
    while o < len(b) - 24:
        t, nv, a, nt, c, one = struct.unpack_from('>6I', b, o)
        if t == 6 and 0 < nv < 200000 and 0 < nt < 400000 and 1 <= one <= 64 and o + 24 + 12 * nv + 10 * nt <= len(b):
            v = np.frombuffer(b, '>f4', 3 * nv, o + 24).reshape(nv, 3).astype(np.float64)
            r = o + 24 + 12 * nv
            rec = np.frombuffer(b, np.uint8, 10 * nt, r).reshape(nt, 10)
            idx = (rec[:, 0:8:2].astype(np.int64) << 8 | rec[:, 1:8:2]).reshape(nt, 4)
            ok = (idx[:, :3] < nv).all(1) & np.isfinite(v).all()
            tris = v[idx[ok][:, :3]]
            quads = idx[ok][:, 3] != idx[ok][:, 2]
            if quads.any():                                     # a real quad: its second half (i0 i2 i3)
                q = idx[ok][quads]
                tris = np.concatenate([tris, v[q[:, [0, 2, 3]]]])
            meshes.append(tris)
            o = r + 10 * nt
            continue
        o += 1
    res = np.concatenate(meshes) if meshes else np.zeros((0, 3, 3))
    _dno_cache[path] = res
    return res


