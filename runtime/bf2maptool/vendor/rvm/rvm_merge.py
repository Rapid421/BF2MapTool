"""rvm_merge.py - build ONE RVM shader database for a level: a host database plus surfaces carried in from
other (donor) databases, with every database-local number renumbered.

    python rvm_merge.py --host <host.full> --add <donor>=<surface>[,<surface>...] [--add ...]
                        --out <merged.full> [--report <json>]
    <surface> = 0xNAMEHASH | 32-hex MD5 id | asset path (e.g. Shaders/Presets/SS_Foo)
    python rvm_merge.py --host <host.full> --out <x.full>         (no --add: must reproduce the host byte-exactly)

What it does (LOG.md, 2026-09-25):
  1. closure of each requested donor surface: its SurfaceShader + the RvmPermutationSet carrying the same MD5,
     plus everything they reference (47-bit content keys);
  2. renumbers the only database-local data in those blocks - the u16 type-table POSITION in
     DefaultValueRef +0x00, InstructionBatch +0x08 and ValueRef +0x00 - from donor to host numbering, and
     re-keys every block above a changed one (content-hash cascade, 47 bits, tags kept);
  3. drops blocks whose translated content the host already has (same key, bytes checked);
  4. appends the rest at the END of their type groups, so every host block keeps its bytes and its index within
     its type; host types keep their table positions (a donor-only type would be appended to the table);
  5. rewrites header counts, per-block element counts and the 16-byte ResMeta (offset = payload length,
     size = header length; version, maybe_hash, header guid, db_hash and the index column kept from the host).
The output is a FRAMED image: import it with "framed": true.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
import sys
import time
from collections import defaultdict
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import rvmlib  # noqa: E402
import xlate  # noqa: E402
from rvmlib import SIZES, SURFACE_T, PERMSET_T, KEY_MASK, tname  # noqa: E402


def hash_quick_lower(s: str) -> int:
    h = 5381
    for c in s.lower().encode("utf-8"):
        h = ((h * 33) ^ c) & 0xFFFFFFFF
    return h


def md5_id(path: str) -> bytes:
    return hashlib.md5(path.lower().encode("utf-8")).digest()


def find_surface(db: rvmlib.RvmDb, spec: str):
    """-> (surface block, permset block, md5, name_hash).  The surface and its permutation set pair 1:1 on the MD5."""
    spec = spec.strip()
    want_md5 = want_nh = None
    if "/" in spec or "\\" in spec:
        want_md5 = md5_id(spec.replace("\\", "/"))
        want_nh = hash_quick_lower(spec.replace("\\", "/"))
    elif spec.lower().startswith("0x"):
        want_nh = int(spec, 16)
    elif len(spec) == 32:
        want_md5 = bytes.fromhex(spec)
    else:
        raise ValueError("surface spec %r: give 0xNAMEHASH, a 32-hex MD5 or an asset path" % spec)
    hits = [(b, nh, md5) for b, nh, md5 in db.surfaces()
            if (want_md5 is None or md5 == want_md5) and (want_nh is None or nh == want_nh)]
    if len(hits) != 1:
        raise LookupError("surface %s: %d matches in %s" % (spec, len(hits), db.name))
    b, nh, md5 = hits[0]
    if int(db.counts[b]) != 1:
        raise ValueError("surface block %d holds %d records; only single-record blocks are supported" % (b, db.counts[b]))
    ps = [pb for pb, m in db.permsets() if m == md5]
    if len(ps) != 1:
        raise LookupError("surface %s: %d permutation sets carry its MD5" % (spec, len(ps)))
    if int(db.counts[ps[0]]) != 1:
        raise ValueError("permutation-set block %d holds %d records" % (ps[0], db.counts[ps[0]]))
    return b, ps[0], md5, nh


class Merge:
    def __init__(self, host: rvmlib.RvmDb):
        if not host.was_framed:
            raise ValueError("the host must be a FRAMED export (res export --framed): its ResMeta is needed")
        if not host.tiles_exactly:
            raise ValueError("host does not tile")
        self.host = host
        self.types = [list(t) for t in host.types]      # [name_hash, count, index]
        self.t2p = dict(host.t2p)
        self.host_keys = {int(k): i for i, k in enumerate(host.keys().tolist())}
        self.new = {}                                     # key -> (type, bytes, count, origin)
        self.new_order = []
        self.report = {"host": host.name, "donors": [], "appended_types": []}

    def _pos_map(self, donor):
        # Dx11BlendStateData's 512 references to Dx11SerializedBlendState carry u16 0, which is ZERO and POS at
        # once because that type sits at position 0 in all 9 type tables seen. Refuse rather than guess if a
        # database ever moves it.
        bs = rvmlib.T("Dx11SerializedBlendState")
        if donor.t2p.get(bs, 0) != 0 or self.t2p.get(bs, 0) != 0:
            raise ValueError("Dx11SerializedBlendState is not at type position 0: its ordinal kind is undecided")
        m = {}
        for p, th in enumerate(donor.p2t):
            if th not in self.t2p:
                # a type the host lacks: appended at the end of the table, index column = its position.
                # UNTESTED in game (no Hoth merge needs it: Starkiller's types are a subset of Hoth's).
                np_ = len(self.types)
                self.types.append([th, 0, np_])
                self.t2p[th] = np_
                self.report["appended_types"].append(tname(th))
            m[p] = self.t2p[th]
        return m

    def add(self, donor: rvmlib.RvmDb, specs):
        ei = xlate.EdgeIndex(donor)
        pos_map = self._pos_map(donor)
        info = {"donor": donor.name, "types": donor.type_count,
                "pos_changes": {int(p): q for p, q in pos_map.items() if p != q}, "surfaces": []}
        roots, host_md5 = [], set(m for _, _, m in self.host.surfaces())
        host_nh = set(nh for _, nh, _ in self.host.surfaces())
        for spec in specs:
            sb, pb, md5, nh = find_surface(donor, spec)
            if md5 in host_md5:
                raise ValueError("host already carries surface %s (%s)" % (spec, md5.hex()))
            if nh in host_nh:
                raise ValueError("host already carries name hash %08X" % nh)
            roots += [sb, pb]
            info["surfaces"].append({"spec": spec, "md5": md5.hex(), "name_hash": "%08X" % nh,
                                     "surface_block": sb, "permset_block": pb})
        cl = xlate.closure(ei, roots)
        log = {}
        tr = xlate.translate(ei, cl, pos_map, log)
        shared = added = dup = 0
        by_type_new = defaultdict(int)
        for b in sorted(cl):
            nb, key, _chg = tr[b]
            th = int(donor.btype[b])
            if key in self.host_keys:
                hb = self.host_keys[key]
                if self.host.raw(hb) != nb or int(self.host.btype[hb]) != th:
                    raise RuntimeError("47-bit key collision between donor block %d and host block %d" % (b, hb))
                shared += 1
                continue
            if key in self.new:
                if self.new[key][1] != nb:
                    raise RuntimeError("47-bit key collision between two donor blocks")
                dup += 1
                continue
            self.new[key] = (th, nb, int(donor.counts[b]), (donor.name, b))
            self.new_order.append(key)
            by_type_new[tname(th)] += 1
            added += 1
        info.update(closure=len(cl), shared_with_host=shared, added=added, duplicate=dup,
                    blocks_rekeyed=log["blocks_changed"],
                    pos_rewritten={"%s+0x%02X" % (tname(t), f): n for (t, f), n in log["pos_rewritten"].items()},
                    added_by_type=dict(sorted(by_type_new.items(), key=lambda kv: -kv[1])))
        info["translated_keys"] = {int(b): tr[b][1] for b in cl}
        self.report["donors"].append(info)
        return info

    def emit(self) -> bytes:
        h = self.host
        new_by_type = defaultdict(list)
        for k in self.new_order:
            th, nb, ec, _ = self.new[k]
            if len(nb) != ec * SIZES[th]:
                raise RuntimeError("block of %s does not tile" % tname(th))
            new_by_type[th].append((nb, ec))
        payload = bytearray()
        counts = []
        table = []
        for p, (th, cnt, idx) in enumerate(self.types):
            if p < h.type_count:
                s = h.first_block[th]
                if cnt:
                    a = int(h.boff[s]); e = int(h.boff[s + cnt - 1] + h.blen[s + cnt - 1])
                    payload += h.data[a:e]
                    counts += h.counts[s:s + cnt].tolist()
            for nb, ec in new_by_type.get(th, ()):
                payload += nb
                counts.append(ec)
            table.append((th, cnt + len(new_by_type.get(th, ())), idx))
        t = h.payload_end
        hdr = bytearray(h.hdr_guid)
        hdr += struct.pack("<Q", h.db_hash)
        hdr += struct.pack("<3I", h.unk1, len(counts), h.unk3)
        hdr += struct.pack("<2H", h.unk4, len(table))
        for th, cnt, idx in table:
            hdr += struct.pack("<IIQ", th, cnt, idx)
        hdr += struct.pack("<Q", h.unk6)
        if max(counts) > 0xFFFF:
            raise RuntimeError("a block exceeds 65535 elements")
        hdr += struct.pack("<%dH" % len(counts), *counts)
        meta = struct.pack("<4I", h.version, len(payload), len(hdr), h.maybe_hash)
        return bytes(meta) + bytes(payload) + bytes(hdr)


# ----------------------------------------------------------------------------------------------------- verify
def verify(img: bytes, host: rvmlib.RvmDb, merge: Merge | None, donors=()):
    """Structural + semantic checks. Returns (ok, report dict)."""
    rep = {}
    ok = True
    m = rvmlib.RvmDb(img, "merged")
    rep["blocks"] = len(m)
    rep["types"] = m.type_count
    rep["tiles_exactly"] = bool(m.tiles_exactly)
    rep["meta"] = {"version": m.version, "offset": m.offset, "size": m.size, "maybe_hash": "%08X" % m.maybe_hash}
    rep["meta_consistent"] = (m.offset + m.size == len(img) - 16) and m.size == m.header_end - m.payload_end
    rep["counts_len_ok"] = m.counts_len == sum(t[1] for t in m.types)
    ok &= rep["tiles_exactly"] and rep["meta_consistent"] and rep["counts_len_ok"]
    # independent parser (the handover's rvmdb.py)
    import rvmdb
    old = rvmdb.Db(None, data=img)
    rep["rvmdb_py"] = {"blocks": len(old.blocks), "tiles_exactly": bool(old.tiles_exactly)}
    ok &= bool(old.tiles_exactly) and len(old.blocks) == len(m)
    # keys distinct
    keys = m.keys().tolist()
    rep["distinct_keys"] = len(set(keys))
    ok &= rep["distinct_keys"] == len(m)
    # every reference site: classify - zero exceptions, and no dangling words at reference sites
    import ptrclass
    cls = ptrclass.classify(m, verbose=False)
    bad_sites = {("%s+0x%02X" % (tname(t), f)): v[0] for (t, f), v in cls.items() if v[2] != 0}
    rep["ref_sites"] = len(cls)
    rep["refs"] = sum(v[1] for v in cls.values())
    rep["ref_sites_with_exceptions"] = bad_sites
    ok &= not bad_sites
    sites = set(cls)
    for d in donors:
        sites |= set(ptrclass.classify(d, verbose=False))
    sites |= set(ptrclass.classify(host, verbose=False))
    kset = set(keys)
    dangling = 0
    for (th, f) in sites:
        if th not in m.t2p:
            continue
        es = SIZES[th]
        for b in m.blocks_of(th):
            o = int(m.boff[b]); n = int(m.counts[b])
            for e in range(n):
                w, = struct.unpack_from("<Q", img, o + e * es + f)
                if w and (w & KEY_MASK) not in kset:
                    dangling += 1
    rep["dangling_refs"] = dangling
    ok &= dangling == 0
    # POS ordinals: every POS reference must name its target's type in THIS table (checked by classify: exc 0)
    rep["pos_refs"] = {("%s+0x%02X" % (tname(t), f)): v[1] for (t, f), v in cls.items() if v[0].startswith("POS")}
    # host preserved: every host type group is a byte-exact prefix of the merged group
    pres = True
    for p, (th, cnt, idx) in enumerate(host.types):
        if m.p2t[p] != th or m.types[p][2] != idx:
            pres = False
            break
        if cnt:
            s = host.first_block[th]; s2 = m.first_block[th]
            a = int(host.boff[s]); e = int(host.boff[s + cnt - 1] + host.blen[s + cnt - 1])
            a2 = int(m.boff[s2])
            if img[a2:a2 + (e - a)] != host.data[a:e] or m.counts[s2:s2 + cnt].tolist() != host.counts[s:s + cnt].tolist():
                pres = False
                break
    rep["host_preserved_bytewise"] = pres
    ok &= pres
    # surfaces pair 1:1 on the MD5, no duplicates
    ss = m.surfaces(); ps = m.permsets()
    smd5 = [x[2] for x in ss]; pmd5 = [x[1] for x in ps]
    rep["surfaces"] = len(ss)
    rep["permsets"] = len(ps)
    rep["surface_md5_unique"] = len(set(smd5)) == len(smd5)
    rep["surface_permset_pairing"] = sorted(smd5) == sorted(pmd5)
    ok &= rep["surface_md5_unique"] and rep["surface_permset_pairing"]
    return ok, rep, m


def semantic_check(merged: rvmlib.RvmDb, donor: rvmlib.RvmDb, donor_spec: str, host_t2p):
    """Translate the merged copy of the donor surface's closure BACK into donor numbering: it must equal the
    donor's own closure block for block (same keys)."""
    sb, pb, md5, nh = find_surface(merged, donor_spec)
    eim = xlate.EdgeIndex(merged)
    cl_m = xlate.closure(eim, [sb, pb])
    inv = {}
    for p, th in enumerate(merged.p2t):
        inv[p] = donor.t2p.get(th, -1)
    back = xlate.translate(eim, cl_m, inv)
    back_keys = sorted(v[1] for v in back.values())
    dsb, dpb, dmd5, dnh = find_surface(donor, donor_spec)
    eid = xlate.EdgeIndex(donor)
    cl_d = xlate.closure(eid, [dsb, dpb])
    dk = donor.keys()
    d_keys = sorted(int(dk[b]) for b in cl_d)
    return {"merged_closure": len(cl_m), "donor_closure": len(cl_d),
            "identical_after_back_translation": back_keys == d_keys}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", required=True)
    ap.add_argument("--add", action="append", default=[], help="<donor db>=<surface>[,<surface>...]")
    ap.add_argument("--out", required=True)
    ap.add_argument("--report")
    a = ap.parse_args()
    t0 = time.time()
    host = rvmlib.load(a.host)
    print("host  %s: %d blocks, %d types, %d surfaces, framed %s, tiles %s" %
          (host.name, len(host), host.type_count, len(host.surfaces()), host.was_framed, host.tiles_exactly))
    mg = Merge(host)
    donors = []
    for spec in a.add:
        path, _, surfs = spec.rpartition("=")
        d = rvmlib.load(path)
        donors.append((d, [s for s in surfs.split(",") if s]))
        print("donor %s: %d blocks, %d types" % (d.name, len(d), d.type_count))
        info = mg.add(d, donors[-1][1])
        print("   surfaces %s" % ", ".join("%s (md5 %s, name %s)" % (s["spec"], s["md5"], s["name_hash"]) for s in info["surfaces"]))
        print("   type positions renumbered: %d of %d (%s)" % (len(info["pos_changes"]), d.type_count,
              ", ".join("%d->%d" % kv for kv in list(info["pos_changes"].items())[:4]) + (" ..." if len(info["pos_changes"]) > 4 else "")))
        print("   closure %d blocks: %d already in host (after renumbering), %d added, %d duplicate; %d re-keyed; POS rewritten %s" %
              (info["closure"], info["shared_with_host"], info["added"], info["duplicate"], info["blocks_rekeyed"], info["pos_rewritten"]))
    img = mg.emit()
    Path(a.out).write_bytes(img)
    print("wrote %s: %d bytes (host %d, +%d)" % (a.out, len(img), len(host.data), len(img) - len(host.data)))
    ok, rep, merged = verify(img, host, mg, [d for d, _ in donors])
    if not a.add:
        rep["noop_byte_exact"] = img == host.data
        ok &= rep["noop_byte_exact"]
    sem = {}
    for d, specs in donors:
        for s in specs:
            sem[s] = semantic_check(merged, d, s, mg.t2p)
            ok &= sem[s]["identical_after_back_translation"]
    rep["semantic"] = sem
    rep["ok"] = bool(ok)
    rep["seconds"] = round(time.time() - t0, 1)
    rep["merge"] = {k: v for k, v in mg.report.items()}
    for dd in rep["merge"]["donors"]:
        dd.pop("translated_keys", None)
    for k, v in rep.items():
        if k not in ("merge", "pos_refs"):
            print("  %-26s %s" % (k, v))
    print("VERIFY:", "PASS" if ok else "FAIL")
    if a.report:
        Path(a.report).write_text(json.dumps(rep, indent=1, default=str))
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
