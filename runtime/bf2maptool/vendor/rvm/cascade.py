"""Content-hash cascade patcher for BFII RVM shader databases.

Every intra-file reference in an RVM database is the content hash of the block it
points at: `CityHash64WithSeed(block_bytes, type_hash)` (Glacier `reader.rs:154`).
Measured on Tatooine's database, 747,218 of 747,218 non-null pointer fields resolve.

Editing any byte of a block therefore invalidates every reference to it, and the
referring blocks are themselves content-hashed, so the repair has to walk up the DAG
until it reaches blocks nothing points at.

## How wide is the key?

Glacier masks references to 40 bits (`reader.rs:46-48`, `mod.rs:33`), which is enough
to disambiguate 149,109 blocks, so its reader is self-consistent at 40 bits. **The game
is not.** Measured over all 754,091 resolvable pointers in the Tatooine database, the
stored u64 agrees with the raw CityHash on the low **47** bits in 754,091 / 754,091
cases, and never on fewer; agreement above bit 47 decays exactly like coin flips
(59.6% stop at 47, then 21%, 10%, 5% ...), so bits 47..63 are tag bits and bits 0..46
are the key.

Repairing only the low 40 bits leaves bits 40..46 stale. The database still reads
cleanly through Glacier and stays self-consistent under a 40-bit model, but the game
takes an ACCESS_VIOLATION during level load with `rcx = rbp = 0xFFFFFFFFFFFF` — a
48-bit "not found" sentinel — and `rax = 0`. That was the cause of every Gate 3 crash
in this session; a 4-byte edit to a SurfaceShader block (a root, so no references to
repair) loaded fine, while a single byte in a bytecode block, which needs a cascade,
did not.

So: match on 47 bits, rewrite 47 bits, preserve bits 47..63.
"""
import sys
from collections import deque
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import rvmdb  # noqa: E402
from cityhash64 import city_hash_64_with_seed  # noqa: E402

BYTECODE_TYPE = 0x602A31A0
KEY_BITS = 47
KEY_MASK = (1 << KEY_BITS) - 1          # 0x00007FFFFFFFFFFF
LOW40_MASK = 0xFFFFFFFFFF
BYTE5_KEY_MASK = 0x7F                   # bits 40..46 live in file byte 5
BYTE5_TAG_MASK = 0x80


def raw_hash(buf, start, length, type_hash):
    # slice first: hashing reads only this block, and handing the hasher the whole
    # 42 MB image would copy it on every call
    return city_hash_64_with_seed(bytes(buf[start:start + length]), type_hash & 0xFFFFFFFF)


def key_of(raw):
    return raw & KEY_MASK


def pattern5(raw):
    """The 5 fully-determined bytes of a pointer to a block with this hash."""
    return (raw & LOW40_MASK).to_bytes(5, "little")


def find_all(buf, pat, lo, hi):
    out = []
    i = buf.find(pat, lo, hi)
    while i != -1:
        out.append(i)
        i = buf.find(pat, i + 1, hi)
    return out


class Cascade:
    def __init__(self, db, buf, verbose=True):
        self.db = db
        self.buf = buf              # bytearray, the full meta+data image
        self.verbose = verbose
        self.lo = db.payload_start
        self.hi = db.walk_end
        self.cur = {}               # block index -> the raw hash pointers currently carry
        self.stats = dict(edits=0, refs_rewritten=0, blocks_rehashed=0, roots=0,
                          near_misses=0, skipped_bytecode_hits=0)

    def _raw(self, bi):
        if bi not in self.cur:
            b = self.db.blocks[bi]
            self.cur[bi] = raw_hash(self.buf, b.file_offset, b.byte_len, b.type_hash)
        return self.cur[bi]

    def edit_block(self, bi, offset_in_block, new_bytes):
        b = self.db.blocks[bi]
        assert offset_in_block + len(new_bytes) <= b.byte_len, "edit overruns the block"
        self._raw(bi)                           # capture pre-edit hash
        p = b.file_offset + offset_in_block
        self.buf[p:p + len(new_bytes)] = new_bytes
        self.stats["edits"] += 1
        return bi

    def repair(self, dirty):
        """Walk the reference DAG upward until every handle matches its target again."""
        q = deque(dict.fromkeys(dirty))
        rounds = 0
        while q:
            bi = q.popleft()
            b = self.db.blocks[bi]
            old = self.cur[bi]
            new = raw_hash(self.buf, b.file_offset, b.byte_len, b.type_hash)
            self.stats["blocks_rehashed"] += 1
            if key_of(new) == key_of(old):
                continue

            old_pat = pattern5(old)
            old_b5 = (old >> 40) & BYTE5_KEY_MASK
            new_low40 = (new & LOW40_MASK).to_bytes(5, "little")
            new_b5 = (new >> 40) & BYTE5_KEY_MASK

            referrers = []
            for p in find_all(self.buf, old_pat, self.lo, self.hi):
                if b.file_offset <= p < b.file_offset + b.byte_len:
                    continue                    # the block's own bytes
                if p + 5 >= self.hi:
                    continue
                if (self.buf[p + 5] & BYTE5_KEY_MASK) != old_b5:
                    # the low 40 bits coincide but bits 40..46 do not, so this is not
                    # a pointer to that block - 47 bits is the key, 40 is not enough
                    self.stats["near_misses"] += 1
                    continue
                rb = self.db.offset_to_block(p)
                if rb is None:
                    continue
                if self.db.blocks[rb].type_hash == BYTECODE_TYPE:
                    self.stats["skipped_bytecode_hits"] += 1
                    continue
                self._raw(rb)                   # capture BEFORE we modify it
                self.buf[p:p + 5] = new_low40
                self.buf[p + 5] = (self.buf[p + 5] & BYTE5_TAG_MASK) | new_b5
                self.stats["refs_rewritten"] += 1
                referrers.append(rb)

            self.cur[bi] = new
            if not referrers:
                self.stats["roots"] += 1
            for rb in dict.fromkeys(referrers):
                q.append(rb)
            rounds += 1
            if self.verbose and rounds % 200 == 0:
                print(f"    ... {rounds} blocks repaired, "
                      f"{self.stats['refs_rewritten']} refs rewritten, queue {len(q)}")
        return self.stats


def verify(path_or_bytes):
    """Every pointer must carry its target's current 47-bit key. Returns (ok, bad)."""
    import rvm_sizes
    data = path_or_bytes if isinstance(path_or_bytes, (bytes, bytearray)) \
        else Path(path_or_bytes).read_bytes()
    db = rvmdb.Db(None, data=bytes(data))
    keys = {}
    for i, b in enumerate(db.blocks):
        keys[key_of(raw_hash(db.data, b.file_offset, b.byte_len, b.type_hash))] = i
    ok = bad = 0
    for b in db.blocks:
        if b.type_hash == BYTECODE_TYPE or rvm_sizes.SIZES.get(b.type_hash, 0) < 8:
            continue
        for j in range(b.byte_len // 8):
            p = b.file_offset + j * 8
            v = int.from_bytes(db.data[p:p + 8], "little")
            if not v:
                continue
            if key_of(v) in keys:
                ok += 1
            elif (v & LOW40_MASK) and (v >> 40):
                bad += 1
    return ok, bad
