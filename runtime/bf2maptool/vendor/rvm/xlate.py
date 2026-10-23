"""Translate blocks of one RVM database into another database's numbering.

Only two things in a block depend on the database it sits in (ptrclass.py, BFII_EXACT on Hoth_01,
Starkiller_01 and Tatooine_01: 2.57 M references, zero exceptions):
  1. the u16 in bits 48..63 of the three POLYMORPHIC reference fields
       DefaultValueRef +0x00, InstructionBatch +0x08, ValueRef +0x00
     = the target's TYPE-TABLE POSITION in that database;
  2. every reference's low 47 bits = the target's content key, which changes when (1) changes a
     descendant (content addressing: the repair cascades up the DAG).
So a donor block is carried into a host by rewriting (1) through the type hash and recomputing (2)
bottom-up.  COUNT and ZERO u16s are content and stay as they are.
"""
from __future__ import annotations

import struct
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np
from cityshim import cityhash

sys.path.insert(0, str(Path(__file__).resolve().parent))
import rvmlib  # noqa: E402
from rvmlib import KEY_MASK, SIZES, T  # noqa: E402

POS_FIELDS = {(T("DefaultValueRef"), 0), (T("InstructionBatch"), 8), (T("ValueRef"), 0)}
LOW48 = (1 << 48) - 1


class EdgeIndex:
    """Per-block list of (offset in block, stored word, target block)."""

    def __init__(self, db: rvmlib.RvmDb):
        self.db = db
        E = db.edges()
        srcs, offs, words, tgts = [], [], [], []
        for th, (s, o, w, t) in E.items():
            srcs.append(s); offs.append(o); words.append(w); tgts.append(t)
        src = np.concatenate(srcs); off = np.concatenate(offs)
        wd = np.concatenate(words); tgt = np.concatenate(tgts)
        order = np.lexsort((off, src))
        self.src, self.off, self.word, self.tgt = src[order], off[order], wd[order], tgt[order]
        self.start = np.searchsorted(self.src, np.arange(len(db) + 1))
        self.n = len(self.src)

    def out(self, b):
        a, e = self.start[b], self.start[b + 1]
        return self.off[a:e], self.word[a:e], self.tgt[a:e]

    def children(self, b):
        a, e = self.start[b], self.start[b + 1]
        return self.tgt[a:e]


def closure(ei: EdgeIndex, roots):
    seen = set(int(r) for r in roots)
    stack = list(seen)
    while stack:
        b = stack.pop()
        for c in ei.children(b).tolist():
            if c not in seen:
                seen.add(c)
                stack.append(c)
    return seen


def topo(ei: EdgeIndex, blocks):
    """Children before parents (iterative DFS post-order) over `blocks` (a closed set)."""
    blocks = set(blocks)
    order, state = [], {}
    for r in sorted(blocks):
        if r in state:
            continue
        stack = [(r, 0)]
        state[r] = 1
        while stack:
            b, i = stack[-1]
            ch = ei.children(b).tolist()
            if i < len(ch):
                stack[-1] = (b, i + 1)
                c = ch[i]
                if c in blocks and c not in state:
                    state[c] = 1
                    stack.append((c, 0))
            else:
                stack.pop()
                state[b] = 2
                order.append(b)
    return order


def translate(ei: EdgeIndex, blocks, pos_map, log=None):
    """pos_map: donor type position -> host type position (only POS fields use it).
    Returns {block: (new_bytes, new_key, changed)} for every block in `blocks` (closed set)."""
    db = ei.db
    out = {}
    fixed_pos = defaultdict(int)
    for b in topo(ei, blocks):
        raw = bytearray(db.raw(b))
        th = int(db.btype[b])
        es = SIZES[th]
        offs, words, tgts = ei.out(b)
        changed = False
        for o, w, t in zip(offs.tolist(), words.tolist(), tgts.tolist()):
            top = w >> 48
            f = o % es
            if (th, f) in POS_FIELDS:
                p = int(db.bpos[t])
                assert top == p, "POS field does not hold the target position (block %d +%d)" % (b, o)
                np_ = pos_map[p]
                if np_ != top:
                    top = np_
                    fixed_pos[(th, f)] += 1
            nk = out[t][1] if t in out else int(db.keys()[t])
            nw = (top << 48) | (w & (1 << 48) - 1 & ~KEY_MASK) | nk      # bit 47 kept as stored (always 0)
            if nw != w:
                struct.pack_into("<Q", raw, o, nw)
                changed = True
        nb = bytes(raw)
        key = (cityhash.CityHash64WithSeed(nb, th & 0xFFFFFFFF) & KEY_MASK) if changed else int(db.keys()[b])
        out[b] = (nb, key, changed)
    if log is not None:
        log["pos_rewritten"] = dict(fixed_pos)
        log["blocks_changed"] = sum(1 for v in out.values() if v[2])
    return out


def pos_map_between(donor: rvmlib.RvmDb, host_p2t_or_t2p):
    """donor position -> host position via the type hash; raises if the host lacks a donor type."""
    t2p = host_p2t_or_t2p
    m = {}
    for p, th in enumerate(donor.p2t):
        if th not in t2p:
            raise KeyError("host has no type %s" % rvmlib.tname(th))
        m[p] = t2p[th]
    return m
