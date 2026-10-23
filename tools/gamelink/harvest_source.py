"""Name event/field ids from the engine source in the frostbite-research index.

The FIFA-branch source names events and fields in code as event_<Name> / fieldhash_<Name>
(and EventSpec/PropertyId literals), so every such identifier is a real name, not a guess.
Each is hashed (DJB2-xor, seed 5381) and matched against unknown_ids.txt; hits are
appended to event_hashes_extra.csv without a "~".

usage: python harvest_source.py [--db <frostbite-brain.sqlite>]   (after harvest_hashes.py)
"""
import argparse, os, re, sqlite3

HERE = os.path.dirname(os.path.abspath(__file__))
_ap = argparse.ArgumentParser()
_ap.add_argument('--db', default=r'C:\Users\rubes\.claude\skills\frostbite-research\assets\brain\frostbite-brain.sqlite',
                 help="the frostbite-research index (documents table with 'frostbite-source/...' paths)")
DB = _ap.parse_args().db
UNKNOWN = os.path.join(HERE, 'unknown_ids.txt')
OUT = os.path.join(HERE, 'event_hashes_extra.csv')
PATTERNS = re.compile(r'\b(?:event_|fieldhash_|eventhash_|propertyhash_|EVENT_|FIELDHASH_)([A-Za-z_][A-Za-z0-9_]{1,63})')
QUOTED = re.compile(r'"([A-Z][A-Za-z0-9_]{2,63})"')


def djb2(s):
    h = 5381
    for c in s.encode('latin1', 'replace'):
        h = ((h * 33) ^ c) & 0xFFFFFFFF
    return h


def main():
    unknown = {int(x, 16) for x in open(UNKNOWN).read().split()}
    have = set()
    if os.path.exists(OUT):
        for row in open(OUT, encoding='utf-8').read().splitlines()[1:]:
            if row:
                have.add(int(row.split(',')[0], 16))
    con = sqlite3.connect(DB)
    names = set()
    rows = 0
    for (text,) in con.execute("select content from documents where path like 'frostbite-source/%'"):
        rows += 1
        if not text:
            continue
        names.update(PATTERNS.findall(text))
        names.update(QUOTED.findall(text))
    print(f'{rows} source documents, {len(names)} candidate names')

    found = {}
    for n in names:
        for s in (n, n[:1].upper() + n[1:]):
            h = djb2(s)
            if h in unknown and h not in have and h not in found:
                found[h] = s
    with open(OUT, 'a', encoding='utf-8') as f:
        for h, s in sorted(found.items()):
            f.write('0x%08X,%d,%s\n' % (h, h - (1 << 32) if h >= 1 << 31 else h, s))
    print(f'appended {len(found)} names from source')


if __name__ == '__main__':
    main()
