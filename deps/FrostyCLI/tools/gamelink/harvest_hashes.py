"""Grow the event/field-name table the trace uses (gl.py follow).

Event and field ids are DJB2-xor hashes of the name (h = h*33 ^ c, seed 5381). Frosty's
own dictionary leaves many ids unnamed in the EBX dumps. This collects every id the dumps
show without a name, hashes candidate strings from places names live (the game exe's
strings, the reflection registry, the names Frosty did resolve, and "On"/"Set" style
variants of all of them), and keeps the matches.

usage: python harvest_hashes.py --xml-root <Frosty EBX XML dump> --exe <dumped game exe>
                                [--registry <gamelink registry dump>]
       -> writes event_hashes_extra.csv (and unknown_ids.txt / known_names.txt for
          harvest_combo.py) next to this file. Each input is optional; the defaults are the
          author's machine.
"""
import argparse, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
BASE_CSV = os.path.join(HERE, 'field_hashes.csv')
OUT = os.path.join(HERE, 'event_hashes_extra.csv')

ap = argparse.ArgumentParser()
ap.add_argument('--xml-root', default=r'C:\Users\rubes\.claude\skills\frostbite-research\assets\raw\ebx-xml',
                help="a Frosty XML dump of the game's EBX (names show as 'Name [HEX]')")
ap.add_argument('--exe', default=r'F:\RE-Tools\work\bfii_dumped.exe', help='an unpacked dump of the game exe')
ap.add_argument('--registry', default=r'F:\Frosty\bfii-live-registry.txt', help='a reflection registry dump')
_args = ap.parse_args()
XML_ROOT, EXE, REGISTRY = _args.xml_root, _args.exe, _args.registry


def djb2(s):
    h = 5381
    for c in s.encode('latin1', 'replace'):
        h = ((h * 33) ^ c) & 0xFFFFFFFF
    return h


def main():
    known = {}
    for row in open(BASE_CSV, encoding='utf-8').read().splitlines()[1:]:
        p = row.split(',')
        if len(p) >= 3:
            known[int(p[0], 16)] = p[2]

    # every id the dumps show: "Name [HEX]" (named) or a bare hex id (unnamed)
    named_re = re.compile(r'>([A-Za-z_][A-Za-z0-9_]*) \[([0-9A-F]{8})\]<')
    bare_re = re.compile(r'>(?:0x)?([0-9A-Fa-f]{8})<')
    unknown, candidates = set(), set()
    files = 0
    for root, _, names in os.walk(XML_ROOT):
        for n in names:
            if not n.endswith('.xml'):
                continue
            files += 1
            text = open(os.path.join(root, n), encoding='utf-8', errors='replace').read()
            for name, hx in named_re.findall(text):
                candidates.add(name)
                known.setdefault(int(hx, 16), name)
            for line in text.splitlines():
                if '<Id>' in line or 'FieldId>' in line or '<SourceField>' in line or '<TargetField>' in line:
                    for hx in bare_re.findall(line):
                        v = int(hx, 16)
                        if v not in known:
                            unknown.add(v)
    print(f'{files} xml files, {len(known)} named ids, {len(unknown)} unnamed ids')

    # candidate strings: exe identifiers, registry names
    if os.path.exists(EXE):
        data = open(EXE, 'rb').read()
        for m in re.finditer(rb'[A-Za-z_][A-Za-z0-9_]{1,63}', data):
            candidates.add(m.group().decode('latin1'))
    else:
        print(f'no exe dump at {EXE} - skipping its strings')
    if os.path.exists(REGISTRY):
        for line in open(REGISTRY, encoding='utf-8', errors='replace'):
            p = line.rstrip('\n').split('\t')
            candidates.update(x for x in p[1:3] if x and re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', x))
    else:
        print(f'no registry dump at {REGISTRY} - skipping it')
    print(f'{len(candidates)} candidate strings')

    found = {}
    def test(s):
        h = djb2(s)
        if h in unknown and h not in found:
            found[h] = s
    for s in candidates:
        test(s)
        for pre in ('On', 'Set', 'Get', 'Is', 'Trigger', 'Start', 'Stop', 'Enable', 'Disable'):
            test(pre + s)
        if s[:1].islower():
            test(s[:1].upper() + s[1:])
    print(f'named {len(found)} of {len(unknown)} unnamed ids')

    with open(OUT, 'w', encoding='utf-8') as f:
        f.write('hash_hex,hash_i32,name\n')
        for h, s in sorted(found.items()):
            f.write('0x%08X,%d,%s\n' % (h, h - (1 << 32) if h >= 1 << 31 else h, s))
        # names the dumps resolved but the base table lacks
        base = {int(r.split(',')[0], 16) for r in open(BASE_CSV, encoding='utf-8').read().splitlines()[1:] if r}
        extra = 0
        for h, s in sorted(known.items()):
            if h not in base and h not in found:
                f.write('0x%08X,%d,%s\n' % (h, h - (1 << 32) if h >= 1 << 31 else h, s))
                extra += 1
    print(f'wrote {OUT} ({len(found)} new + {extra} from dumps)')

    # what is still unnamed, and every name known, for harvest_combo.py
    left = os.path.join(os.path.dirname(OUT), 'unknown_ids.txt')
    with open(left, 'w') as f:
        for h in sorted(unknown - set(found)):
            f.write('%08x\n' % h)
    words = os.path.join(os.path.dirname(OUT), 'known_names.txt')
    with open(words, 'w', encoding='utf-8') as f:
        for s in sorted(set(known.values()) | set(found.values())):
            f.write(s + '\n')
    print(f'wrote {left} and {words}')


if __name__ == '__main__':
    main()
