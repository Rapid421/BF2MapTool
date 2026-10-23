"""Pure-Python Google CityHash64 / CityHash64WithSeed(s).

Transcribed 1:1 from the C++ reference vendored by cityhash-sys 1.0.6 (src/google/city.cc):
    Rotate / ShiftMix / HashLen16 / HashLen0to16 / HashLen17to32 / WeakHashLen32WithSeeds /
    HashLen33to64 / CityHash64 / CityHash64WithSeed / CityHash64WithSeeds
  .../src/google/city.h  Hash128to64 (kMul = 0x9ddfea08eb382d69)

Little-endian host assumed (Fetch64/Fetch32 are plain unaligned loads there).

Self-test (`python cityhash64.py`) replays the crate's own vectors from
cityhash-sys-1.0.6/tests/hash_64.rs + tests/lipsum.rs.
"""

from __future__ import annotations

import struct

M = 0xFFFFFFFFFFFFFFFF

K0 = 0xC3A5C85C97CB3127
K1 = 0xB492B66FBE98F273
K2 = 0x9AE16A3B2F90404F
KMUL = 0x9DDFEA08EB382D69

_u64 = struct.Struct("<Q").unpack_from
_u32 = struct.Struct("<I").unpack_from


def _rot(val: int, shift: int) -> int:
    if shift == 0:
        return val
    return ((val >> shift) | (val << (64 - shift))) & M


def _shift_mix(val: int) -> int:
    return val ^ (val >> 47)


def _bswap64(v: int) -> int:
    return int.from_bytes(v.to_bytes(8, "little"), "big")


def _hash128to64(low: int, high: int) -> int:
    a = ((low ^ high) * KMUL) & M
    a ^= a >> 47
    b = ((high ^ a) * KMUL) & M
    b ^= b >> 47
    b = (b * KMUL) & M
    return b


def _hash_len16(u: int, v: int, mul: int | None = None) -> int:
    if mul is None:
        return _hash128to64(u, v)
    a = ((u ^ v) * mul) & M
    a ^= a >> 47
    b = ((v ^ a) * mul) & M
    b ^= b >> 47
    b = (b * mul) & M
    return b


def _hash_len0to16(s: bytes, off: int, ln: int) -> int:
    if ln >= 8:
        mul = (K2 + ln * 2) & M
        a = (_u64(s, off)[0] + K2) & M
        b = _u64(s, off + ln - 8)[0]
        c = (_rot(b, 37) * mul + a) & M
        d = ((_rot(a, 25) + b) * mul) & M
        return _hash_len16(c, d, mul)
    if ln >= 4:
        mul = (K2 + ln * 2) & M
        a = _u32(s, off)[0]
        return _hash_len16((ln + (a << 3)) & M, _u32(s, off + ln - 4)[0], mul)
    if ln > 0:
        a = s[off]
        b = s[off + (ln >> 1)]
        c = s[off + ln - 1]
        y = (a + (b << 8)) & 0xFFFFFFFF
        z = (ln + (c << 2)) & 0xFFFFFFFF
        return (_shift_mix(((y * K2) & M) ^ ((z * K0) & M)) * K2) & M
    return K2


def _hash_len17to32(s: bytes, off: int, ln: int) -> int:
    mul = (K2 + ln * 2) & M
    a = (_u64(s, off)[0] * K1) & M
    b = _u64(s, off + 8)[0]
    c = (_u64(s, off + ln - 8)[0] * mul) & M
    d = (_u64(s, off + ln - 16)[0] * K2) & M
    return _hash_len16(
        (_rot((a + b) & M, 43) + _rot(c, 30) + d) & M,
        (a + _rot((b + K2) & M, 18) + c) & M,
        mul,
    )


def _weak_hash32_seeds(w: int, x: int, y: int, z: int, a: int, b: int) -> tuple[int, int]:
    a = (a + w) & M
    b = _rot((b + a + z) & M, 21)
    c = a
    a = (a + x) & M
    a = (a + y) & M
    b = (b + _rot(a, 44)) & M
    return (a + z) & M, (b + c) & M


def _weak_hash32_seeds_s(s: bytes, off: int, a: int, b: int) -> tuple[int, int]:
    w, x, y, z = struct.unpack_from("<4Q", s, off)
    return _weak_hash32_seeds(w, x, y, z, a, b)


def _hash_len33to64(s: bytes, off: int, ln: int) -> int:
    mul = (K2 + ln * 2) & M
    a = (_u64(s, off)[0] * K2) & M
    b = _u64(s, off + 8)[0]
    c = _u64(s, off + ln - 24)[0]
    d = _u64(s, off + ln - 32)[0]
    e = (_u64(s, off + 16)[0] * K2) & M
    f = (_u64(s, off + 24)[0] * 9) & M
    g = _u64(s, off + ln - 8)[0]
    h = (_u64(s, off + ln - 16)[0] * mul) & M
    u = (_rot((a + g) & M, 43) + ((_rot(b, 30) + c) * 9)) & M
    v = ((((a + g) & M) ^ d) + f + 1) & M
    w = (_bswap64(((u + v) * mul) & M) + h) & M
    x = (_rot((e + f) & M, 42) + c) & M
    y = ((_bswap64(((v + w) * mul) & M) + g) * mul) & M
    z = (e + f + c) & M
    a = (_bswap64((((x + z) * mul) + y) & M) + b) & M
    b = (_shift_mix((((z + a) * mul) + d + h) & M) * mul) & M
    return (b + x) & M


def city_hash_64(data: bytes, off: int = 0, ln: int | None = None) -> int:
    """CityHash64 over data[off:off+ln]."""
    if ln is None:
        ln = len(data) - off

    if ln <= 32:
        if ln <= 16:
            return _hash_len0to16(data, off, ln)
        return _hash_len17to32(data, off, ln)
    if ln <= 64:
        return _hash_len33to64(data, off, ln)

    end = off + ln
    x = _u64(data, end - 40)[0]
    y = (_u64(data, end - 16)[0] + _u64(data, end - 56)[0]) & M
    z = _hash_len16((_u64(data, end - 48)[0] + ln) & M, _u64(data, end - 24)[0])
    v0, v1 = _weak_hash32_seeds_s(data, end - 64, ln & M, z)
    w0, w1 = _weak_hash32_seeds_s(data, end - 32, (y + K1) & M, x)
    x = ((x * K1) + _u64(data, off)[0]) & M

    n = (ln - 1) & ~63
    p = off
    while True:
        b0, b1, b2, b3, b4, b5, b6, b7 = struct.unpack_from("<8Q", data, p)
        x = (_rot((x + y + v0 + b1) & M, 37) * K1) & M
        y = (_rot((y + v1 + b6) & M, 42) * K1) & M
        x ^= w1
        y = (y + v0 + b5) & M
        z = (_rot((z + w0) & M, 33) * K1) & M
        v0, v1 = _weak_hash32_seeds(b0, b1, b2, b3, (v1 * K1) & M, (x + w0) & M)
        w0, w1 = _weak_hash32_seeds(b4, b5, b6, b7, (z + w1) & M, (y + b2) & M)
        z, x = x, z
        p += 64
        n -= 64
        if n == 0:
            break

    return _hash_len16(
        (_hash_len16(v0, w0) + ((_shift_mix(y) * K1) & M) + z) & M,
        (_hash_len16(v1, w1) + x) & M,
    )


def city_hash_64_with_seeds(data: bytes, seed0: int, seed1: int,
                            off: int = 0, ln: int | None = None) -> int:
    return _hash_len16((city_hash_64(data, off, ln) - seed0) & M, seed1 & M)


def city_hash_64_with_seed(data: bytes, seed: int, off: int = 0, ln: int | None = None) -> int:
    """cityhash_sys::city_hash_64_with_seed == CityHash64WithSeeds(s, len, k2, seed)."""
    return city_hash_64_with_seeds(data, K2, seed, off, ln)


def hash_from_binary(h: int) -> int:
    """reader.rs:46-48  fn hash_from_binary(h: u64) -> u64 { u64::from_be(h & 0xffffffffff) }

    On a little-endian host `u64::from_be` is a byte swap, so this keeps the low 40 bits of
    the CityHash and reverses their byte order into the top of the u64 (low 3 bytes = 0).
    """
    return _bswap64(h & 0x000000FFFFFFFFFF)


def block_hash(block_bytes: bytes, type_hash: int) -> int:
    """reader.rs:154  hash_from_binary(city_hash_64_with_seed(&block_data, hash as u64))"""
    return hash_from_binary(city_hash_64_with_seed(block_bytes, type_hash & 0xFFFFFFFF))


# ---------------------------------------------------------------- self-test

import sys


def _load_vectors():
    """Pull the two 256-entry tables out of cityhash-sys-1.0.6/tests/hash_64.rs."""
    import glob
    import re

    pats = sys.argv[1:]          # path(s) of cityhash-sys tests/hash_64.rs
    path = None
    for p in pats:
        hits = glob.glob(p)
        if hits:
            path = hits[0]
            break
    if path is None:
        return None
    text = open(path, encoding="utf-8").read()
    out = {}
    for name in ("CITY_HASH_64_RESULTS", "CITY_HASH_64_WITH_SEED_RESULT"):
        m = re.search(name + r"\s*:\s*\[u64;\s*256\]\s*=\s*\[(.*?)\]\s*;", text, re.S)
        if not m:
            return None
        out[name] = [int(v, 16) for v in re.findall(r"0x[0-9a-fA-F]+", m.group(1))]
    lip = re.search(r'pub static LIPSUM: &str = "(.*?)";',
                    open(path.replace("hash_64.rs", "lipsum.rs"), encoding="utf-8").read(), re.S)
    out["LIPSUM"] = lip.group(1).replace("\\\n", "").encode() if lip else None
    return out


def _self_test() -> int:
    vecs = _load_vectors()
    if vecs is None:
        print("could not locate cityhash-sys test vectors; skipping")
        return 1

    key = bytearray(256)
    bad = 0
    for i in range(256):
        key[i] = i
        s = bytes(key[:i])
        got = city_hash_64(s)
        want = vecs["CITY_HASH_64_RESULTS"][i]
        if got != want:
            bad += 1
            print(f"CityHash64 len={i}: got {got:#018x} want {want:#018x}")
        got = city_hash_64_with_seed(s, i)
        want = vecs["CITY_HASH_64_WITH_SEED_RESULT"][i]
        if got != want:
            bad += 1
            print(f"CityHash64WithSeed len={i} seed={i}: got {got:#018x} want {want:#018x}")

    # doctest vectors from cityhash-sys-1.0.6/src/cityhash_portable.rs:52,68,84
    checks = [
        (city_hash_64(bytes([0, 1, 2, 3, 4])), 0xB4BFA9E87732C149, "city_hash_64 doc"),
        (city_hash_64_with_seed(bytes([0, 1, 2, 3, 4]), 123), 0xCE1706019C5E61A7, "with_seed doc"),
        (city_hash_64_with_seeds(bytes([0, 1, 2, 3, 4]), 123, 456), 0xD83C81881E3A35E3, "with_seeds doc"),
    ]
    if vecs.get("LIPSUM"):
        lip = vecs["LIPSUM"]
        checks += [
            (city_hash_64(lip), 0x999AC74ADDCD6DC7, f"lipsum len={len(lip)}"),
            (city_hash_64_with_seed(lip, 0x5555555555555555), 0x6976F8F604619566, "lipsum seeded"),
        ]
    for got, want, label in checks:
        if got != want:
            bad += 1
            print(f"{label}: got {got:#018x} want {want:#018x}")

    print(f"self-test: {512 + len(checks) - bad}/{512 + len(checks)} vectors OK")
    return 0 if bad == 0 else 3


if __name__ == "__main__":
    raise SystemExit(_self_test())
