"""Re-pack a WIP bundle into parts small enough to upload (each under a size limit). Every part keeps the bundle's
folder paths, so unzipping ALL parts into the same place gives the same folder as the one big zip.
    python split_bundle.py <bundle.zip> [limit MiB, default 26]   ->  <bundle>_part1of N.zip ...
"""
import os
import sys
import zipfile
import zlib

src = sys.argv[1]
limit = float(sys.argv[2]) * 1048576 if len(sys.argv) > 2 else 26 * 1048576
z = zipfile.ZipFile(src)
parts, cur, size = [], [], 0
for i in sorted((i for i in z.infolist() if not i.is_dir()), key=lambda i: i.filename):
    cs = i.compress_size + len(i.filename) * 2 + 120          # what the entry costs in a zip, roughly
    if cur and size + cs > limit:
        parts.append(cur); cur, size = [], 0
    cur.append(i); size += cs
if cur:
    parts.append(cur)
base = os.path.splitext(src)[0]
for old in os.listdir(os.path.dirname(src)):
    if old.startswith(os.path.basename(base) + '_part'):
        os.remove(os.path.join(os.path.dirname(src), old))
out = []
for k, part in enumerate(parts):
    path = '%s_part%dof%d.zip' % (base, k + 1, len(parts))
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as w:
        for i in part:
            w.writestr(i, z.read(i))
    out.append(path)
    tops = sorted({'/'.join(i.filename.split('/')[1:2]) for i in part})
    print('%s  %.1f MiB  %d files  %s' % (os.path.basename(path), os.path.getsize(path) / 1048576, len(part), ', '.join(tops)[:150]))
# proof: the parts together hold exactly the files of the bundle, byte for byte
want = {i.filename: i.CRC for i in z.infolist() if not i.is_dir()}
got = {}
for p in out:
    with zipfile.ZipFile(p) as w:
        got.update({i.filename: i.CRC for i in w.infolist()})
print('parts hold %d of %d files, all checksums equal: %s' % (len(got), len(want), got == want))
