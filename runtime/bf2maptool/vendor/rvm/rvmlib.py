"""rvmlib - fast reader/analyser for BFII RVM shader databases (Hoth port, R2).

Container (BFII_EXACT, handover 2026-09-20 s6):
    @0x00  ResMeta: u32 version, u32 offset (= blocks payload length), u32 size (= header length), u32 maybe_hash
    @0x10  blocks, grouped by type in type-table order, block i = counts[i] elements of SIZES[type] bytes
    @0x10+offset header:
        u8[16] guid; u64 db_hash; u32 unk1; u32 counts_len; u32 unk3; u16 unk4; u16 type_count
        type_count * {u32 name_hash; u32 block_count; u64 index}
        u64 unk6
        counts_len * u16 element count per block

Content addressing: a reference is CityHash64WithSeed(block bytes, type hash); the game matches the
low 47 bits.  Bits 48..63 of a stored reference are a u16 "ordinal" (bit 47 is 0 in every stored
reference measured here) - see ptrclass.py for what that u16 means per field.

Hashing uses the C `cityhash` module, checked equal to the verified pure-Python port
(tools/cityhash64.py, 517/517 crate vectors) on 411 random inputs of 0..65536 bytes.
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

import numpy as np
from cityshim import cityhash

sys.path.insert(0, str(Path(__file__).resolve().parent))
from rvm_sizes import SIZES, NAMES  # noqa: E402

KEY_BITS = 47
KEY_MASK = (1 << KEY_BITS) - 1
LOW48 = (1 << 48) - 1
BYTECODE_T = 0x602A31A0
SURFACE_T = 0x20149F19
PERMSET_T = 0x7B05F918


def tname(th: int) -> str:
    n = NAMES.get(th)
    if n is None:
        return "0x%08X" % th
    return n.replace("RvmSerializedDb_ns_", "").replace("RvmLegacyInstructions_ns_", "")


def T(short: str) -> int:
    for h, n in NAMES.items():
        if n.endswith("_" + short) or n == short:
            return h
    raise KeyError(short)


def find_header(payload: bytes):
    """Locate the trailing header in a payload-only export (the length identity + known type hashes)."""
    n = len(payload)
    lo = max(0, n - 4_000_000)
    known = set(SIZES)
    for h in range(n - 56, lo, -1):
        counts_len = struct.unpack_from("<I", payload, h + 28)[0]
        type_count = struct.unpack_from("<H", payload, h + 38)[0]
        if not (0 < type_count < 512) or not (0 < counts_len < 4_000_000):
            continue
        hlen = 40 + type_count * 16 + 8 + counts_len * 2
        if h + hlen != n:
            continue
        good = sum(1 for i in range(type_count)
                   if struct.unpack_from("<I", payload, h + 40 + i * 16)[0] in known)
        if good >= max(8, int(type_count * 0.9)):
            return h, hlen
    return None, None


def read_image(path) -> tuple[bytes, bool]:
    """Returns (full framed image, was_framed).  A payload-only export gets a reconstructed meta whose
    version and maybe_hash are 0 (NOT the shipped values) - never ship such an image."""
    d = Path(path).read_bytes()
    v, off, size, mh = struct.unpack_from("<4I", d, 0)
    if off + size + 16 == len(d):
        return d, True
    h, hlen = find_header(d)
    if h is None:
        raise ValueError("%s: neither framed nor a recognisable payload" % path)
    return struct.pack("<4I", 0, h, hlen, 0) + d, False


class RvmDb:
    def __init__(self, image: bytes, name: str = "?"):
        self.name = name
        self.data = image
        d = image
        self.version, self.offset, self.size, self.maybe_hash = struct.unpack_from("<4I", d, 0)
        self.payload_end = 0x10 + self.offset
        t = self.payload_end
        self.hdr_guid = d[t:t + 16]
        self.db_hash, = struct.unpack_from("<Q", d, t + 16)
        self.unk1, self.counts_len, self.unk3 = struct.unpack_from("<3I", d, t + 24)
        self.unk4, self.type_count = struct.unpack_from("<2H", d, t + 36)
        self.types = [struct.unpack_from("<IIQ", d, t + 40 + i * 16) for i in range(self.type_count)]
        self.unk6, = struct.unpack_from("<Q", d, t + 40 + self.type_count * 16)
        co = t + 40 + self.type_count * 16 + 8
        self.counts = np.frombuffer(d, dtype="<u2", count=self.counts_len, offset=co).astype(np.int64)
        self.header_end = co + 2 * self.counts_len
        self.p2t = [th for th, _, _ in self.types]
        self.t2p = {th: i for i, th in enumerate(self.p2t)}
        missing = [th for th in self.p2t if th not in SIZES]
        if missing:
            raise KeyError("no element size for " + ", ".join("%08X" % m for m in missing))
        # walk
        n = self.counts_len
        btype = np.empty(n, dtype=np.int64)
        bpos = np.empty(n, dtype=np.int64)        # type-table position of the block's type
        bgi = np.empty(n, dtype=np.int64)         # index within its type group
        esz = np.empty(n, dtype=np.int64)
        k = 0
        for p, (th, cnt, _idx) in enumerate(self.types):
            btype[k:k + cnt] = th
            bpos[k:k + cnt] = p
            bgi[k:k + cnt] = np.arange(cnt)
            esz[k:k + cnt] = SIZES[th]
            k += cnt
        if k != n:
            raise ValueError("sum(type counts) %d != counts_len %d" % (k, n))
        blen = self.counts * esz
        boff = 0x10 + np.concatenate(([0], np.cumsum(blen)[:-1]))
        self.btype, self.bpos, self.bgi, self.esz, self.blen, self.boff = btype, bpos, bgi, esz, blen, boff
        self.walk_end = int(boff[-1] + blen[-1]) if n else 0x10
        self.tiles_exactly = (self.walk_end == self.payload_end) and (self.header_end == len(d))
        self.first_block = {}
        k = 0
        for p, (th, cnt, _idx) in enumerate(self.types):
            self.first_block[th] = k
            k += cnt
        self._keys = None
        self._key2b = None

    # ------------------------------------------------------------------ blocks
    def __len__(self):
        return self.counts_len

    def raw(self, i) -> bytes:
        o = int(self.boff[i])
        return self.data[o:o + int(self.blen[i])]

    def blocks_of(self, th):
        p = self.t2p.get(th)
        if p is None:
            return range(0)
        s = self.first_block[th]
        return range(s, s + self.types[p][1])

    def keys(self) -> np.ndarray:
        """47-bit content key of every block."""
        if self._keys is None:
            d = self.data
            out = np.empty(self.counts_len, dtype=np.uint64)
            ch = cityhash.CityHash64WithSeed
            bo, bl, bt = self.boff.tolist(), self.blen.tolist(), self.btype.tolist()
            for i in range(self.counts_len):
                o = bo[i]
                out[i] = ch(d[o:o + bl[i]], bt[i] & 0xFFFFFFFF) & KEY_MASK
            self._keys = out
        return self._keys

    def key2block(self) -> dict:
        if self._key2b is None:
            ks = self.keys().tolist()
            m = {}
            dup = 0
            for i, k in enumerate(ks):
                if k in m:
                    dup += 1
                    continue
                m[k] = i
            self._key2b = m
            self.dup_keys = dup
        return self._key2b

    # ------------------------------------------------------------------ edges
    def edges(self, types=None):
        """Every stored reference: yields numpy arrays per source type:
        (src_block, byte offset in block, word, target_block).  A word is a reference when it is
        non-zero and its low 47 bits are some block's key (element-aligned 8-byte words only)."""
        k2b = self.key2block()
        keys_sorted = np.array(sorted(k2b.keys()), dtype=np.uint64)
        blocks_by_sorted = np.array([k2b[int(k)] for k in keys_sorted], dtype=np.int64)
        out = {}
        for p, (th, cnt, _idx) in enumerate(self.types):
            if types is not None and th not in types:
                continue
            es = SIZES[th]
            if th == BYTECODE_T or es < 8 or cnt == 0:
                continue
            s = self.first_block[th]
            e = s + cnt
            start = int(self.boff[s])
            end = int(self.boff[e - 1] + self.blen[e - 1])
            if end <= start:
                continue
            # element-aligned 8-byte words: offsets j*es + 8*m, m < es//8
            nel = (end - start) // es
            if es % 8 == 0:
                nw = (end - start) // 8
                wofs = start + 8 * np.arange(nw, dtype=np.int64)
                words = np.frombuffer(self.data, dtype="<u8", count=nw, offset=start).astype(np.uint64)
            else:
                base = start + np.arange(nel, dtype=np.int64) * es
                wofs = (base[:, None] + 8 * np.arange(es // 8, dtype=np.int64)[None, :]).ravel()
                buf = np.frombuffer(self.data, dtype=np.uint8)
                idx = wofs[:, None] + np.arange(8, dtype=np.int64)[None, :]
                words = buf[idx].copy().view("<u8").ravel()
            nz = words != 0
            wk = words & np.uint64(KEY_MASK)
            pos = np.searchsorted(keys_sorted, wk)
            pos[pos >= len(keys_sorted)] = 0
            hit = nz & (keys_sorted[pos] == wk)
            wofs_h = wofs[hit]
            tgt = blocks_by_sorted[pos[hit]]
            src = np.searchsorted(self.boff[s:e], wofs_h, side="right") - 1 + s
            out[th] = (src, wofs_h - self.boff[src], words[hit], tgt)
        return out

    # ------------------------------------------------------------------ surfaces
    def surfaces(self):
        """[(block, name_hash, md5 id bytes)]"""
        res = []
        for b in self.blocks_of(SURFACE_T):
            o = int(self.boff[b])
            n = int(self.counts[b])
            for e in range(n):
                eo = o + e * 80
                nh, = struct.unpack_from("<I", self.data, eo + 8)
                res.append((b, nh, self.data[eo + 0x40:eo + 0x50]))
        return res

    def permsets(self):
        res = []
        for b in self.blocks_of(PERMSET_T):
            o = int(self.boff[b])
            n = int(self.counts[b])
            for e in range(n):
                res.append((b, self.data[o + e * 104:o + e * 104 + 16]))
        return res


def load(path, name=None) -> RvmDb:
    img, framed = read_image(path)
    db = RvmDb(img, name or Path(path).stem)
    db.was_framed = framed
    return db
