"""Pure-Python DXBC container checksum (the 16 bytes at offset 4..20 of a `DXBC` blob).

`fxc.exe` / D3DCompiler sign every container they emit with a *modified* MD5.  The
message is the container from offset 20 to the end (i.e. everything after the magic
and the checksum field), and the compression function, round constants, shift
amounts and initial state are all bit-for-bit standard MD5.  Only the *finalisation*
differs, which is why `hashlib` cannot be used: MD5's padding is baked into it.

Standard MD5 finalises a message of `n` bytes by appending `0x80`, then zero bytes
until the length is 56 mod 64, then the 64-bit little-endian bit count.  DXBC
instead does this (`n = len(data)`, `rest = n % 64`):

  * `num_bits  = n * 8`                       (32-bit, wraps)
  * `num_bits2 = (num_bits >> 2) | 1`         (32-bit)

  * `rest >= 56`  — two extra blocks.  The `rest` trailing bytes are followed by
    `0x80` and zeros to complete the current block, then one further, otherwise
    empty, block whose first dword is `num_bits` and whose last dword is
    `num_bits2`.

  * `rest < 56`   — one extra block, laid out as
        dword 0          = num_bits          <- the length goes *first*, not last
        bytes 4..4+rest  = the trailing data bytes
        next byte        = 0x80
        zeros            up to byte 60
        dword 15         = num_bits2

Note that in the `rest < 56` case the length prefix means the trailing data bytes
are hashed at a *shifted* offset inside the block, which is the single detail that
makes the result differ from plain MD5 for almost every input.

Verified against 3,769 containers with zero mismatches:

  * 3,570 real Frostbite/BFII shaders carved out of `work/tat_dx11_full.bin`,
  * the 9,644-byte shipped reference shader `work/dxbc/ref4.cso`,
  * 186 shaders compiled on the spot by `fxc.exe` (123 distinct sizes, 220..3944
    bytes, with and without `/Qstrip_reflect`),
  * the 12 `work/cso/*.cso` flat-colour shaders.

Every dword-aligned residue of `(size - 20) % 64` is covered -- 0, 4, ... 60, with
at least 200 samples each -- so both branches are exercised (residues 56 and 60 hit
the `rest >= 56` path, 448 shipped + 23 fxc containers).  Container sizes are always
a multiple of 4, so the other 48 residues are unreachable in practice; the
compression function itself was separately cross-checked against `hashlib.md5` over
207 message lengths, which pins down the code path they would share.

`fix_checksum` was additionally validated end-to-end against D3DCompiler itself:
`fxc /dumpbin` refuses a container whose checksum field is wrong, and it accepted
every container this module re-signed -- including ones whose shader body had been
edited (a float immediate patched inside SHEX), i.e. bodies `fxc` never signed.
"""

import struct

__all__ = ["dxbc_checksum", "fix_checksum", "check_container", "md5_raw"]

# ---------------------------------------------------------------------------
# Standard MD5 primitives
# ---------------------------------------------------------------------------

_MASK = 0xFFFFFFFF

# Per-round left-rotation amounts.
_S = (
    7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
    5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
    4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
    6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
)

# K[i] = floor(abs(sin(i + 1)) * 2**32), the usual MD5 sine table.
_K = (
    0xD76AA478, 0xE8C7B756, 0x242070DB, 0xC1BDCEEE,
    0xF57C0FAF, 0x4787C62A, 0xA8304613, 0xFD469501,
    0x698098D8, 0x8B44F7AF, 0xFFFF5BB1, 0x895CD7BE,
    0x6B901122, 0xFD987193, 0xA679438E, 0x49B40821,
    0xF61E2562, 0xC040B340, 0x265E5A51, 0xE9B6C7AA,
    0xD62F105D, 0x02441453, 0xD8A1E681, 0xE7D3FBC8,
    0x21E1CDE6, 0xC33707D6, 0xF4D50D87, 0x455A14ED,
    0xA9E3E905, 0xFCEFA3F8, 0x676F02D9, 0x8D2A4C8A,
    0xFFFA3942, 0x8771F681, 0x6D9D6122, 0xFDE5380C,
    0xA4BEEA44, 0x4BDECFA9, 0xF6BB4B60, 0xBEBFBC70,
    0x289B7EC6, 0xEAA127FA, 0xD4EF3085, 0x04881D05,
    0xD9D4D039, 0xE6DB99E5, 0x1FA27CF8, 0xC4AC5665,
    0xF4292244, 0x432AFF97, 0xAB9423A7, 0xFC93A039,
    0x655B59C3, 0x8F0CCC92, 0xFFEFF47D, 0x85845DD1,
    0x6FA87E4F, 0xFE2CE6E0, 0xA3014314, 0x4E0811A1,
    0xF7537E82, 0xBD3AF235, 0x2AD7D2BB, 0xEB86D391,
)

_INIT = (0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476)


def _compress(state, block):
    """One standard MD5 compression: fold a 64-byte `block` into `state`."""
    a, b, c, d = state
    m = struct.unpack("<16I", block)
    a0, b0, c0, d0 = a, b, c, d

    for i in range(64):
        if i < 16:
            f = (b & c) | (~b & d)
            g = i
        elif i < 32:
            f = (d & b) | (~d & c)
            g = (5 * i + 1) & 15
        elif i < 48:
            f = b ^ c ^ d
            g = (3 * i + 5) & 15
        else:
            f = c ^ (b | (~d & _MASK))
            g = (7 * i) & 15

        tmp = d
        d = c
        c = b
        rot = (a + (f & _MASK) + _K[i] + m[g]) & _MASK
        s = _S[i]
        b = (b + (((rot << s) | (rot >> (32 - s))) & _MASK)) & _MASK
        a = tmp

    return ((a0 + a) & _MASK, (b0 + b) & _MASK,
            (c0 + c) & _MASK, (d0 + d) & _MASK)


def md5_raw(state, data):
    """Fold every complete 64-byte block of `data` into `state` (no padding)."""
    for off in range(0, len(data) - 63, 64):
        state = _compress(state, data[off:off + 64])
    return state


# ---------------------------------------------------------------------------
# The DXBC-specific finalisation
# ---------------------------------------------------------------------------

def _dxbc_md5(data):
    """Modified MD5 over `data`; returns the 16-byte digest."""
    n = len(data)
    rest = n & 0x3F                       # n % 64 -- bytes past the last full block
    body = n - rest

    state = md5_raw(_INIT, data[:body])   # all complete blocks, unmodified MD5

    num_bits = (n * 8) & _MASK
    num_bits2 = ((num_bits >> 2) | 1) & _MASK

    if rest >= 56:
        # Not enough room to also carry the length: finish the current block with
        # 0x80 + zeros, then emit one more block holding only the two lengths.
        blk = data[body:] + b"\x80" + b"\x00" * (63 - rest)
        state = _compress(state, blk)

        tail = struct.pack("<I", num_bits) + b"\x00" * 56 + struct.pack("<I", num_bits2)
        state = _compress(state, tail)
    else:
        # Single final block: length first, then the tail bytes, then 0x80 +
        # zeros, then the derived second length in the last dword.
        blk = (struct.pack("<I", num_bits)
               + data[body:]
               + b"\x80"
               + b"\x00" * (55 - rest)
               + struct.pack("<I", num_bits2))
        state = _compress(state, blk)

    return struct.pack("<4I", *state)


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def dxbc_checksum(container):
    """Return the 16-byte DXBC checksum for `container`.

    `container` is the full shader blob starting with the `DXBC` magic.  The
    checksum is computed over `container[20:]`, so whatever the checksum field
    currently holds is irrelevant.
    """
    if len(container) < 20:
        raise ValueError("container too short (%d bytes)" % len(container))
    if container[:4] != b"DXBC":
        raise ValueError("not a DXBC container (magic %r)" % container[:4])
    return _dxbc_md5(bytes(container[20:]))


def fix_checksum(container):
    """Return `container` with bytes 4..20 replaced by the correct checksum."""
    b = bytearray(container)
    b[4:20] = dxbc_checksum(b)
    return bytes(b)


def check_container(container):
    """True if the container's stored checksum matches the recomputed one."""
    return bytes(container[4:20]) == dxbc_checksum(container)


if __name__ == "__main__":
    import sys

    if len(sys.argv) < 2:
        sys.exit("usage: dxbc_hash.py <file.cso> [...]   "
                 "-- verify the stored checksum of each container")
    bad = 0
    for path in sys.argv[1:]:
        data = open(path, "rb").read()
        want = data[4:20]
        got = dxbc_checksum(data)
        ok = want == got
        bad += not ok
        print("%-50s %s stored=%s computed=%s" % (
            path, "OK  " if ok else "FAIL", want.hex(), got.hex()))
    sys.exit(1 if bad else 0)
