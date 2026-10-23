"""Which game assets do several .fbmod files BOTH change? (Frosty Mod Manager keeps only one mod's copy of each.)
Reads the fbmod header + resource table only (format from FrostyToolsuite 1.0.6.3 FrostyModReader / *Resource.cs).
    python mod_clash.py <a.fbmod> <b.fbmod> [...] [--json out.json] [--list]
An ADDED asset (flag 0x08) that two mods both add is a clash too (same name, two contents).
Bundle-only entries (no data, just "add this asset to bundle X") are merged by the manager and are not clashes.
"""
import json
import struct
import sys
from collections import defaultdict

MAGIC = 0x01005954534F5246
TYPES = {0: 'embedded', 1: 'ebx', 2: 'res', 3: 'chunk', 4: 'bundle'}


class R:
    def __init__(self, f):
        self.f = f

    def u8(self):
        return self.f.read(1)[0]

    def i32(self):
        return struct.unpack('<i', self.f.read(4))[0]

    def u32(self):
        return struct.unpack('<I', self.f.read(4))[0]

    def i64(self):
        return struct.unpack('<q', self.f.read(8))[0]

    def u64(self):
        return struct.unpack('<Q', self.f.read(8))[0]

    def cstr(self):
        b = bytearray()
        while True:
            c = self.f.read(1)
            if not c or c == b'\0':
                return b.decode('utf-8', 'replace')
            b += c


def read_mod(path):
    with open(path, 'rb') as f:
        r = R(f)
        if r.u64() != MAGIC:
            raise ValueError('not an fbmod: ' + path)
        ver = r.u32()
        r.i64(); r.i32()                      # data offset, data count
        profile = f.read(r.u8()).decode()
        r.i32()                               # game version
        details = [r.cstr() for _ in range(6 if ver >= 5 else 5)]
        out = []
        for _ in range(r.i32()):
            t = TYPES.get(r.u8(), '?')
            idx = r.i32()
            name = r.cstr() if (ver > 3 or idx != -1) else None
            sha1 = flags = None
            if idx != -1:
                sha1 = f.read(20).hex()
                r.i64(); flags = r.u8(); r.i32()
                if ver >= 3:
                    r.cstr()
            if ver <= 3 and idx != -1:
                for _ in range(r.i32()):
                    r.i32()
            nb = r.i32()
            for _ in range(nb):
                r.i32()
            if t == 'bundle':
                name = r.cstr(); r.i32()
            elif t == 'chunk':
                f.read(24)
            elif t == 'res':
                r.u32(); r.u64(); f.read(r.i32())
            out.append({'type': t, 'name': name, 'data': idx != -1, 'added': bool(flags and flags & 0x08), 'sha1': sha1, 'nb': nb})
        return {'path': path, 'version': ver, 'profile': profile, 'title': details[0], 'details': details, 'resources': out}


def main(argv):
    js = None
    if '--json' in argv:
        i = argv.index('--json'); js = argv[i + 1]; del argv[i:i + 2]
    show = '--list' in argv
    argv = [a for a in argv if a != '--list']
    mods = [read_mod(p) for p in argv]
    owners = defaultdict(list)
    for m in mods:
        for res in m['resources']:
            if res['data'] and res['type'] in ('ebx', 'res', 'chunk'):
                owners[(res['type'], res['name'].lower())].append((m['title'], res['added'], res['sha1']))
    clashes = []
    for (t, n), who in sorted(owners.items()):
        if len({w[0] for w in who}) > 1:
            same = len({w[2] for w in who}) == 1
            clashes.append({'type': t, 'name': n, 'mods': [w[0] for w in who], 'added': [w[1] for w in who], 'identical': same})
    for m in mods:
        c = defaultdict(int)
        for res in m['resources']:
            c[res['type'] + ('+data' if res['data'] else '')] += 1
        print('%-20s v%d  %s  bundle memberships %d' % (m['title'], m['version'], dict(c), sum(x['nb'] for x in m['resources'])))
    real = [c for c in clashes if not c['identical']]
    print('assets changed by more than one mod: %d (%d with DIFFERENT contents = real clashes)' % (len(clashes), len(real)))
    for c in (clashes if show else real[:80]):
        print('  %s %-5s %s  <- %s%s' % ('SAME' if c['identical'] else 'DIFF', c['type'], c['name'], ', '.join(c['mods']),
                                       '  (added)' if all(c['added']) else ''))
    if js:
        json.dump({'mods': [{k: v for k, v in m.items() if k != 'resources'} for m in mods], 'clashes': clashes}, open(js, 'w'), indent=1)


if __name__ == '__main__':
    main(sys.argv[1:])
