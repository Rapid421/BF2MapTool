"""Package the add-on: one zip that Blender's "Install from Disk" takes, with the runtime inside it
(bf2_map_tool/runtime/bf2maptool). python make_zip.py [out dir]  ->  <out>/bf2_map_tool-<version>.zip
The same files, without the top folder, go to <out>/bf2_map_tool-<version>-extension.zip: the Blender 4.2+ extension
form (blender_manifest.toml at the root; Get Extensions > Install from Disk, or `blender --command extension install-file`).
The version in __init__.py and in blender_manifest.toml must agree (checked here)."""
import os
import re
import sys
import zipfile

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, 'dist')
os.makedirs(out, exist_ok=True)
init = open(os.path.join(ROOT, 'addon', 'bf2_map_tool', '__init__.py'), encoding='utf-8').read()
ver = '.'.join(re.search(r"'version': \((\d+), (\d+), (\d+)\)", init).groups())
man = open(os.path.join(ROOT, 'addon', 'bf2_map_tool', 'blender_manifest.toml'), encoding='utf-8').read()
mver = re.search(r'^version = "([^"]+)"', man, re.M).group(1)
if mver != ver:
    sys.exit('version mismatch: __init__.py %s, blender_manifest.toml %s' % (ver, mver))
files = []
for base, arc in ((os.path.join(ROOT, 'addon', 'bf2_map_tool'), ''),
                  (os.path.join(ROOT, 'runtime', 'bf2maptool'), 'runtime/bf2maptool'),
                  (os.path.join(ROOT, 'docs'), 'docs')):
    for d, dirs, fs in os.walk(base):
        dirs[:] = [x for x in dirs if x != '__pycache__']
        for f in fs:
            if f.endswith(('.pyc', '.pyo', '.blend1')):
                continue
            full = os.path.join(d, f)
            rel = os.path.relpath(full, base).replace('\\', '/')
            files.append((full, (arc + '/' + rel) if arc else rel))
for name, prefix in (('bf2_map_tool-%s.zip' % ver, 'bf2_map_tool/'), ('bf2_map_tool-%s-extension.zip' % ver, '')):
    path = os.path.join(out, name)
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
        for full, rel in files:
            z.write(full, prefix + rel)
    print('%s: %d files, %d bytes' % (path, len(files), os.path.getsize(path)))
