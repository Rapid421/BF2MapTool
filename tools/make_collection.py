"""A Frosty Mod Manager collection (one file to send): a zip holding <title>.fbcollection (Frosty 1.0.6.3
FrostyModCollection.WriteCollection layout: magic 'LOCF', version 1, offsets, JSON manifest, icon, screenshots) and every
.fbmod, in load order (later mods win a shared asset - the menu mod goes last). Mod Manager: "Import" the zip.
    python make_collection.py <out.zip> <title> <a.fbmod> <b.fbmod> ... [--author A] [--version 1.0] [--description D]
                              [--icon icon.png] [--shot shot.png ...]
"""
import json
import os
import struct
import sys
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mod_clash

MAGIC, VERSION = 0x46434F4C, 1


def collection_bytes(title, author, version, description, mods, icon=b'', shots=()):
    manifest = {'link': '', 'title': title, 'author': author, 'version': version, 'description': description, 'category': 'Map',
                'mods': [os.path.basename(m) for m in mods],
                # FrostyModDetails(title, author, category, version, description, link): the version is the 4th string
                'modVersions': [mod_clash.read_mod(m)['details'][3] for m in mods]}
    head = 8 + 5 * 4
    mj = json.dumps(manifest, indent=2).encode('ascii')
    manifest_off, icon_off = head, head + len(mj)
    shots_off = icon_off + len(icon)
    body = mj + icon + struct.pack('<i', len(shots)) + b''.join(struct.pack('<i', len(s)) + s for s in shots)
    return struct.pack('<IIIiIiI', MAGIC, VERSION, manifest_off, len(mj), icon_off, len(icon), shots_off) + body, manifest


def main(argv):
    def opt(name, default=None):
        if name in argv:
            i = argv.index(name); v = argv[i + 1]; del argv[i:i + 2]; return v
        return default
    author = opt('--author', ''); version = opt('--version', '1.0'); desc = opt('--description', '')
    icon = opt('--icon'); shots = []
    while '--shot' in argv:
        shots.append(open(opt('--shot'), 'rb').read())
    out, title, mods = argv[0], argv[1], argv[2:]
    data, manifest = collection_bytes(title, author, version, desc, mods, open(icon, 'rb').read() if icon else b'', shots)
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_STORED, allowZip64=True) as z:     # fbmods are compressed already
        z.writestr(title + '.fbcollection', data)
        for m in mods:
            z.write(m, os.path.basename(m))
    print('collection %s: %d mods, %.0f MB' % (out, len(mods), os.path.getsize(out) / 1e6))
    for m, v in zip(manifest['mods'], manifest['modVersions']):
        print('  %s  (version %s)' % (m, v))


if __name__ == '__main__':
    main(sys.argv[1:])
