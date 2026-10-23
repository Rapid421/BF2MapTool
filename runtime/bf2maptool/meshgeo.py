"""MESHGEO1 reader / writer (FrostyCLI.Adapters.Mesh/MeshGeo.cs) + vertex element codec for the formats our pieces use."""
import struct, numpy as np

USAGE = {0x01: 'Pos', 0x06: 'Normal', 0x07: 'Tangent', 0x08: 'Binormal', 0x09: 'BinormalSign', 0x1E: 'Color0', 0x1F: 'Color1',
         0x21: 'TexCoord0', 0x22: 'TexCoord1', 0x23: 'TexCoord2', 0x24: 'TexCoord3', 0x2A: 'RadiosityTexCoord', 0x34: 'TangentSpace',
         0x02: 'BoneIndices', 0x04: 'BoneWeights'}
FMT = {0x01: ('<f4', 1), 0x02: ('<f4', 2), 0x03: ('<f4', 3), 0x04: ('<f4', 4), 0x05: ('<f2', 1), 0x06: ('<f2', 2), 0x07: ('<f2', 3),
       0x08: ('<f2', 4), 0x0B: ('i1', 4), 0x0C: ('u1', 4), 0x0D: ('u1', 4), 0x13: ('<i2', 2), 0x15: ('<i2', 4), 0x18: ('<u2', 2),
       0x19: ('<u2', 4), 0x1A: ('<i4', 1), 0x20: ('<u4', 1)}
FMT_NAME = {0x01: 'Float', 0x02: 'Float2', 0x03: 'Float3', 0x04: 'Float4', 0x05: 'Half', 0x06: 'Half2', 0x07: 'Half3', 0x08: 'Half4',
            0x0B: 'Byte4N', 0x0C: 'UByte4', 0x0D: 'UByte4N', 0x13: 'Short2N', 0x15: 'Short4N', 0x18: 'UShort2N', 0x19: 'UShort4N'}


class R:
    def __init__(self, b): self.b = b; self.p = 0
    def u(self, f):
        v = struct.unpack_from(f, self.b, self.p); self.p += struct.calcsize(f); return v if len(v) > 1 else v[0]
    def s(self):
        n = self.u('<H'); v = self.b[self.p:self.p + n].decode('utf8'); self.p += n; return v
    def raw(self, n):
        v = self.b[self.p:self.p + n]; self.p += n; return v


def load(path):
    r = R(open(path, 'rb').read())
    assert r.raw(8) == b'MESHGEO1', path
    g = {'path': r.s(), 'game': r.s(), 'lods': []}
    for _ in range(r.u('<i')):
        lod = {'flags': r.u('<I'), 'cats': [], 'sections': []}
        for _ in range(r.u('<i')):
            lod['cats'].append(r.raw(r.u('<i')))
        for _ in range(r.u('<i')):
            s = {'name': r.s()}
            s['mat'], s['prim'], s['vcount'], s['pcount'], s['lmidx'] = r.u('<iiiiI')
            s['ratios'] = r.u('<6f')
            s['elements'] = [r.u('<BBBB') for _ in range(r.u('<i'))]      # usage, format, offset, stream
            s['stride'] = r.u('<i')
            s['verts'] = r.raw(r.u('<i'))
            ni = r.u('<i')
            s['indices'] = np.frombuffer(r.raw(4 * ni), '<u4').copy()
            lod['sections'].append(s)
        g['lods'].append(lod)
    g['trailer'] = r.b[r.p:]
    return g


def save(g, path):
    o = [b'MESHGEO1']
    def ws(x):
        b = x.encode('utf8'); o.append(struct.pack('<H', len(b))); o.append(b)
    ws(g['path']); ws(g['game']); o.append(struct.pack('<i', len(g['lods'])))
    for lod in g['lods']:
        o.append(struct.pack('<Ii', lod['flags'], len(lod['cats'])))
        for c in lod['cats']:
            o.append(struct.pack('<i', len(c))); o.append(bytes(c))
        o.append(struct.pack('<i', len(lod['sections'])))
        for s in lod['sections']:
            ws(s['name'])
            o.append(struct.pack('<iiiiI', s['mat'], s['prim'], s['vcount'], s['pcount'], s['lmidx']))
            o.append(struct.pack('<6f', *s['ratios']))
            o.append(struct.pack('<i', len(s['elements'])))
            for e in s['elements']:
                o.append(struct.pack('<BBBB', *e))
            o.append(struct.pack('<i', s['stride']))
            o.append(struct.pack('<i', len(s['verts']))); o.append(bytes(s['verts']))
            o.append(struct.pack('<i', len(s['indices']))); o.append(np.asarray(s['indices'], '<u4').tobytes())
    o.append(bytes(g['trailer']))
    open(path, 'wb').write(b''.join(o))


def element(s, usage):
    for u, f, off, st in s['elements']:
        if USAGE.get(u) == usage and st == 0:
            return f, off
    return None


def decode(s, usage):
    """float array (vcount, n) of one element; normalised integer formats are returned raw (callers here only read floats)."""
    e = element(s, usage)
    if e is None:
        return None
    f, off = e
    dt, n = FMT[f]
    a = np.frombuffer(s['verts'], np.uint8).reshape(s['vcount'], s['stride'])
    w = np.dtype(dt).itemsize * n
    return np.ascontiguousarray(a[:, off:off + w]).view(dt).reshape(s['vcount'], n).astype(np.float64)


def decl(s):
    return ' '.join('%s@%d/%s' % (USAGE.get(u, hex(u)), off, FMT_NAME.get(f, hex(f))) + ('/s%d' % st if st else '') for u, f, off, st in s['elements']) + ' stride=%d' % s['stride']
