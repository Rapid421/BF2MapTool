"""Binary FBX (7.x) reader / writer: the generic record tree, nothing scene-specific.

A node is ``[name, props, children]``; a property is ``(type char, value)`` where the value of an array type
(``f d l i b c``) is a numpy array and of ``S`` / ``R`` is ``bytes``. Reading then writing a file gives the same tree
(array compression aside), which is what the piece writer relies on.
"""
import struct
import zlib

import numpy as np

MAGIC = b'Kaydara FBX Binary  \x00\x1a\x00'
_ARR = {'f': '<f4', 'd': '<f8', 'l': '<i8', 'i': '<i4', 'b': '?', 'c': 'u1'}
_SCALAR = {'Y': '<h', 'C': '<?', 'I': '<i', 'F': '<f', 'D': '<d', 'L': '<q'}
# the footer Blender's exporter writes (io_scene_fbx/encode_bin.py); readers only need its length and layout
_FOOT_ID = b'\xfa\xbc\xab\x09\xd0\xc8\xd4\x66\xb1\x76\xfb\x83\x1c\xf7\x26\x7e'
_FOOT_MAGIC = b'\xf8\x5a\x8c\x6a\xde\xf5\xd9\x7e\xec\xe9\x0c\xe3\x75\x8f\x29\x0b'


def _read_prop(b, p):
    t = chr(b[p]); p += 1
    if t in _SCALAR:
        f = _SCALAR[t]
        return (t, struct.unpack_from(f, b, p)[0]), p + struct.calcsize(f)
    if t in ('S', 'R'):
        n, = struct.unpack_from('<I', b, p)
        return (t, bytes(b[p + 4:p + 4 + n])), p + 4 + n
    if t in _ARR:
        n, enc, clen = struct.unpack_from('<III', b, p); p += 12
        raw = b[p:p + clen]
        if enc:
            raw = zlib.decompress(raw)
        return (t, np.frombuffer(raw, _ARR[t], n).copy()), p + clen
    raise ValueError('unknown FBX property type %r at %d' % (t, p))


def _read_node(b, p, wide):
    if wide:
        end, nprops, plen = struct.unpack_from('<QQQ', b, p); p += 24
    else:
        end, nprops, plen = struct.unpack_from('<III', b, p); p += 12
    nlen = b[p]; p += 1
    name = bytes(b[p:p + nlen]).decode('ascii'); p += nlen
    if end == 0:
        return None, p
    props = []
    for _ in range(nprops):
        v, p = _read_prop(b, p)
        props.append(v)
    kids = []
    while p < end:
        k, p = _read_node(b, p, wide)
        if k is not None:
            kids.append(k)
    return [name, props, kids], end


def load(path):
    """-> (version, [top-level nodes])"""
    b = open(path, 'rb').read()
    if b[:len(MAGIC)] != MAGIC:
        raise ValueError('%s is not a binary FBX file' % path)
    version, = struct.unpack_from('<I', b, len(MAGIC))
    wide = version >= 7500
    p = len(MAGIC) + 4
    nodes = []
    while True:
        n, p = _read_node(b, p, wide)
        if n is None:
            break
        nodes.append(n)
    return version, nodes


def _write_prop(out, t, v, level=None):
    out.append(t.encode('ascii'))
    if t in _SCALAR:
        out.append(struct.pack(_SCALAR[t], v))
    elif t in ('S', 'R'):
        out.append(struct.pack('<I', len(v))); out.append(bytes(v))
    else:
        # Arrays are written UNCOMPRESSED (encoding 0). Measured on the Mustafar regression: with zlib level-1 streams
        # (valid: this module and zlib read them back) Frosty's FBX importer rejected 2 of 514 pieces on every try
        # ("Mesh must be exported with at least one UV channel"); the same data at level 6 or uncompressed imports.
        a = np.ascontiguousarray(np.asarray(v, _ARR[t]))
        raw = a.tobytes()
        if level is None or len(raw) <= 128:
            out.append(struct.pack('<III', a.size, 0, len(raw))); out.append(raw)
        else:                                # zlib stream, as Blender's exporter writes arrays over 128 bytes (it uses level 1)
            import zlib
            z = zlib.compress(raw, level)
            out.append(struct.pack('<III', a.size, 1, len(z))); out.append(z)


def _node_bytes(node, start, wide, level=None):
    name, props, kids = node
    head = 25 if wide else 13
    pb = []
    for t, v in props:
        _write_prop(pb, t, v, level)
    pb = b''.join(pb)
    nm = name.encode('ascii')
    body = [bytes([len(nm)]), nm, pb]
    pos = start + head + len(nm) + len(pb)
    for k in kids:
        kb = _node_bytes(k, pos, wide, level)
        body.append(kb); pos += len(kb)
    if kids or not props:               # a nested list (even an empty one) ends with a null record
        body.append(b'\x00' * head); pos += head
    hdr = struct.pack('<QQQ' if wide else '<III', pos, len(props), len(pb))
    return hdr + b''.join(body)


def save(path, version, nodes, level=None):
    """level: None = arrays uncompressed, 1..9 = zlib level for arrays over 128 bytes"""
    wide = version >= 7500
    out = [MAGIC, struct.pack('<I', version)]
    pos = len(MAGIC) + 4
    for n in nodes:
        nb = _node_bytes(n, pos, wide, level)
        out.append(nb); pos += len(nb)
    out.append(b'\x00' * (25 if wide else 13)); pos += 25 if wide else 13
    out.append(_FOOT_ID); out.append(b'\x00' * 4); pos += 20
    pad = (16 - pos % 16) % 16 or 16
    out.append(b'\x00' * pad)
    out.append(struct.pack('<I', version)); out.append(b'\x00' * 120); out.append(_FOOT_MAGIC)
    with open(path, 'wb') as f:
        f.write(b''.join(out))


def find(nodes, *names):
    """first node at the given path of names, or None"""
    cur = nodes
    node = None
    for nm in names:
        node = next((n for n in cur if n[0] == nm), None)
        if node is None:
            return None
        cur = node[2]
    return node


def describe(nodes, depth=0, max_depth=6, out=None):
    """readable outline of a tree (arrays shown by type and length)"""
    out = [] if out is None else out
    for name, props, kids in nodes:
        ps = []
        for t, v in props:
            if t in _ARR:
                ps.append('%s[%d]' % (t, len(v)))
            elif t in ('S', 'R'):
                ps.append(repr(v[:60]))
            else:
                ps.append(repr(v))
        out.append('%s%s: %s' % ('  ' * depth, name, ', '.join(ps)))
        if depth < max_depth:
            describe(kids, depth + 1, max_depth, out)
    return out
