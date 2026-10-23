"""fbmod_embedded.py <file.fbmod> [name] [--out file]: list a .fbmod's embedded resources (position, name, size), or print
/ save one by name (e.g. KyberLevelDeclarationData). Resource table parsed by read_fbmod.py (FrostyToolsuite format); data
from the manifest: at dataOffset, dataCount x (offset i64, size i64), then the blobs (FrostyModReader.GetResourceData).
"""
import json
import struct
import sys

sys.path.insert(0, r'F:\Frosty\Frosty_1.0.7_Animations_Release\PluginSource\_tools')
import read_fbmod  # noqa: E402


def entries(path):
    m = read_fbmod.read_mod(path)
    with open(path, 'rb') as f:
        head = f.read(24)
    data_offset = struct.unpack_from('<q', head, 12)[0]
    data_count = struct.unpack_from('<i', head, 20)[0]
    # read_mod drops the first five embedded entries (icon + screenshots) from 'entries'; rebuild the full list order
    return m, data_offset, data_count


def blob(path, data_offset, data_count, index):
    with open(path, 'rb') as f:
        f.seek(data_offset + index * 16)
        off, size = struct.unpack('<qq', f.read(16))
        f.seek(data_offset + data_count * 16 + off)
        return f.read(size)


if __name__ == '__main__':
    path = sys.argv[1]
    name = sys.argv[2] if len(sys.argv) > 2 and not sys.argv[2].startswith('--') else None
    m, doff, dcount = entries(path)
    emb = [e for e in m['entries'] if e['type'] == 'embedded']
    print('%s: %d resources, %d embedded after the icon/screenshot header, header embedded %d'
          % (m['file'], m['resourceCount'], len(emb), m['embeddedHeader']))
    for e in emb:
        print('  #%d %-28s resourceIndex %d size %s' % (e['index'], e.get('name'), e['resourceIndex'], e.get('size')))
    if name:
        e = next((x for x in emb if x.get('name') == name), None)
        if not e:
            sys.exit('no embedded resource ' + name)
        data = blob(path, doff, dcount, e['resourceIndex'])
        if '--out' in sys.argv:
            open(sys.argv[sys.argv.index('--out') + 1], 'wb').write(data)
        try:
            j = json.loads(data.decode('utf-8'))
            for mp in j.get('maps', []):
                if mp.get('image'):
                    mp['image'] = '<%d base64 chars>' % len(mp['image'])
            print(json.dumps(j, indent=1))
        except Exception:
            print(data[:200])
