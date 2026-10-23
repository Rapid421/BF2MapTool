"""Make the scrolling hologram surface FX/Shaders/Ambient/Holo/SS_Holo_Ticker_01 TEXTURE-COLOURED inside a level's OWN
merged DX11 database (the host's database and the shipped game are never touched). Generalised from the Mygeeto port
(`I:/2005_Map_Ports/MygeetoPort/work/holo_texcolor.py`, seen in game there: green force fields, cyan energy strips).

Shipped pixel shaders (both permutations; dumps in holo/ps_*.cso, from the SP root level's database):
    sample_indexable r0.zw, r0.zwzz, t2.zwxy, s0          r0.z = tex.r, r0.w = tex.g
    mul    r1.xyzw, r0.wzzz, l(10.0, 13.862385, 1.762180, 0.0)
    mad    r0.x, r0.x, r0.y, r1.x                           flicker / scan term (tex.g * 10)
    mad    r0.xyz, cb1[0].xyzx, r0.xxxx, r1.yzwy            colour = tint * term + r1.yzw (fixed orange * tex.r)
Same-size token edits (instruction count, declarations, temp count unchanged):
    sample dest r0.zw -> r1.xyzw, resource swizzle .zwxy -> .xyzw     r1 = tex.rgba
    mul source r0.wzzz -> r1.wxyz, immediate -> (10, K, K, K)          r1.x = tex.a * 10, r1.yzw = tex.rgb * K
    cb1[0].xyzx -> .yyyy                                               the scan flicker in neutral grey
=> colour on screen = texture RGB * K (+ a grey flicker from texture ALPHA). K is the level's brightness for a texel of 1.
DXBC checksums recomputed; the 47-bit content-hash chain repaired (cascade); verified.

    python holo_texcolor.py <merged.full in> <out.full> <K>          prints one JSON line
"""
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cascade  # noqa: E402
import rvmdb  # noqa: E402
from dxbc_hash import dxbc_checksum  # noqa: E402

K0 = 13.862385
f2u = lambda f: struct.unpack('<I', struct.pack('<f', f))[0]
u2f = lambda u: struct.unpack('<f', struct.pack('<I', u))[0]


def tok(ncomp, mode, sel, otype, idim):
    return ncomp | (mode << 2) | (sel << 4) | (otype << 12) | (idim << 20)


def sw(a, b, c, d):
    return a | (b << 2) | (c << 4) | (d << 6)


R0_ZW = tok(2, 0, 0xC, 0, 1)
T2_ZWXY = tok(2, 1, sw(2, 3, 0, 1), 7, 1)
R0_WZZZ = tok(2, 1, sw(3, 2, 2, 2), 0, 1)
CB_XYZX = tok(2, 1, sw(0, 1, 2, 0), 8, 2)


def patch_program(orig, K):
    w = list(struct.unpack('<%dI' % (len(orig) // 4), orig))
    rs = [i for i in range(len(w) - 1) if (w[i] & ~(1 << 31)) == T2_ZWXY and w[i + 1] == 2]
    assert len(rs) == 1, ('t2.zwxy', rs)
    r = rs[0]
    ds = [i for i in range(r - 12, r) if w[i] == R0_ZW and w[i + 1] == 0]
    assert len(ds) == 1, ('sample dest r0.zw', ds)
    d = ds[0]
    w[d] = tok(2, 0, 0xF, 0, 1); w[d + 1] = 1
    w[r] = (w[r] & (1 << 31)) | tok(2, 1, sw(0, 1, 2, 3), 7, 1)
    hits = [i for i in range(len(w) - 3) if w[i] == f2u(10.0) and abs(u2f(w[i + 1]) - K0) < 1e-3
            and abs(u2f(w[i + 2]) - 1.76218) < 1e-3 and w[i + 3] == 0]
    assert len(hits) == 1, ('immediate', hits)
    i = hits[0]
    w[i + 1:i + 4] = [f2u(K)] * 3
    ms = [j for j in range(i - 6, i) if w[j] == R0_WZZZ and w[j + 1] == 0]
    assert len(ms) == 1, ('mul source r0.wzzz', ms)
    m = ms[0]
    w[m] = tok(2, 1, sw(3, 0, 1, 2), 0, 1); w[m + 1] = 1
    cs = [j for j in range(len(w) - 2) if w[j] == CB_XYZX and w[j + 1] == 1 and w[j + 2] == 0]
    assert len(cs) == 1, ('cb1[0].xyzx', cs)
    w[cs[0]] = tok(2, 1, sw(1, 1, 1, 1), 8, 2)
    new = bytearray(struct.pack('<%dI' % len(w), *w))
    new[4:20] = dxbc_checksum(bytes(new))
    assert len(new) == len(orig)
    return bytes(new)


def main(src, out, K):
    db = rvmdb.load(src)
    buf = bytearray(db.data)
    c = cascade.Cascade(db, buf, verbose=False)
    dirty, progs = [], []
    for cso in sorted((Path(__file__).resolve().parent / 'holo').glob('ps_*.cso')):
        orig = cso.read_bytes()
        off = bytes(buf).find(orig)
        if off < 0:
            raise SystemExit('%s: the shipped hologram program is not in the database' % cso.name)
        if bytes(buf).find(orig, off + 1) >= 0:
            raise SystemExit('%s: found more than once' % cso.name)
        new = patch_program(orig, K)
        bi = db.offset_to_block(off)
        b = db.blocks[bi]
        assert b.type_hash == cascade.BYTECODE_TYPE
        c.edit_block(bi, off - b.file_offset, new)
        dirty.append(bi)
        progs.append(cso.name)
    st = c.repair(dirty)
    Path(out).write_bytes(bytes(buf))
    # cascade.verify counts 8-byte words that resolve to a block key (ok) and words that look like a pointer but do not
    # (bad; plain data trips it too). The patch is sound when it changes neither count.
    ok0, bad0 = cascade.verify(src)
    ok1, bad1 = cascade.verify(out)
    ok = ok1 == ok0 and bad1 == bad0
    print(json.dumps({'programs': progs, 'K': K, 'blocks_rehashed': st['blocks_rehashed'], 'refs_rewritten': st['refs_rewritten'],
                      'verify': ok, 'resolved': [ok0, ok1], 'unresolved': [bad0, bad1]}))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1], sys.argv[2], float(sys.argv[3])))
