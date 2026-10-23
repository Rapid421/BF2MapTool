"""Classify every stored reference's u16 ordinal (bits 48..63) per (source type, field offset).

Each reference is a u64: low 47 bits = CityHash key of the target block, bit 47 = 0 (measured on
every reference), bits 48..63 = a u16 whose meaning depends on the FIELD:
  ZERO  - always 0                                  (plain reference; lengths live elsewhere)
  COUNT - the target block's element count         (array reference: the reader needs the length)
  POS   - the target type's TYPE-TABLE POSITION    (polymorphic reference: the reader needs the type)
POS ordinals are database-local: the same type sits at different positions in different databases
(e.g. Hoth_01 carries LodFadeInstructionData at position 77, Starkiller_01 does not), so a block
copied between databases carries a foreign ordinal unless it is renumbered.

  python ptrclass.py <db> [<db> ...]        (framed .full or payload-only export)
"""
import sys
import collections
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import rvmlib  # noqa: E402
from rvmlib import tname, SIZES  # noqa: E402


def classify(db, verbose=True, show_blendstate=False):
    """Returns {(src_type, field_off): (kind, n, exceptions)}."""
    E = db.edges()
    res = {}
    for th, (src, off, words, tgt) in E.items():
        es = SIZES[th]
        fo = (off % es).astype(np.int64)
        top = (words >> np.uint64(48)).astype(np.int64)
        tcount = db.counts[tgt]
        tpos = db.bpos[tgt]
        for f in np.unique(fo):
            m = fo == f
            tp, tc, tt = top[m], tcount[m], tpos[m]
            n = int(m.sum())
            z, c, p = (tp == 0), (tp == tc), (tp == tt)
            # the kind with the fewest exceptions; a kind that explains every instance wins outright
            cand = [("ZERO", int((~z).sum())), ("COUNT", int((~c).sum())), ("POS", int((~p).sum()))]
            kind, exc = min(cand, key=lambda kv: kv[1])
            if exc:
                kind += "?"
            elif sum(1 for _, e in cand if e == 0) > 1:
                kind += "~"          # two explanations coincide on every instance (ambiguous site)
            ttypes = collections.Counter(db.btype[tgt[m]].tolist())
            res[(th, int(f))] = (kind, n, exc, ttypes, int((z & c).sum()), int((c & p).sum()))
    if verbose:
        tot = sum(v[1] for v in res.values())
        kinds = collections.Counter()
        for v in res.values():
            kinds[v[0]] += v[1]
        print("%s: %d types, %d references in %d (type, field) sites -> %s" %
              (db.name, db.type_count, tot, len(res), dict(kinds)))
        for (th, f), (kind, n, exc, ttypes, zc, cp) in sorted(res.items(), key=lambda kv: (kv[1][0] != "POS", -kv[1][1])):
            if tname(th) == "Dx11BlendStateData" and not show_blendstate and f > 0:
                continue
            tts = ", ".join("%s x%d" % (tname(t), c) for t, c in ttypes.most_common(5))
            print("  %-6s %-46s +0x%02X n=%-7d exc=%-5d -> %s" % (kind, tname(th), f, n, exc, tts))
        nb = sum(1 for (th, f) in res if tname(th) == "Dx11BlendStateData")
        if nb > 1 and not show_blendstate:
            print("  (Dx11BlendStateData: %d further fields, all -> Dx11SerializedBlendState at position %d)" %
                  (nb - 1, db.t2p.get(rvmlib.T("Dx11SerializedBlendState"), -1)))
    return res


def pos_fields(db):
    """The (type, field) sites whose u16 is a type-table position."""
    return sorted(k for k, v in classify(db, verbose=False).items() if v[0] == "POS")


if __name__ == "__main__":
    for p in sys.argv[1:]:
        db = rvmlib.load(p)
        print("loaded %s: %d blocks, %d types, tiles %s, framed %s" %
              (db.name, len(db), db.type_count, db.tiles_exactly, db.was_framed))
        classify(db)
        print()
