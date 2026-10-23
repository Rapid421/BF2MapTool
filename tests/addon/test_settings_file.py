"""P3-3: Save settings to a file in one profile, load them in a NEW profile: real paths come over, a path that does not
exist on "this PC" is refused and listed.
    blender -b --factory-startup --python test_settings_file.py -- <zip> <bundle dir> <work dir> <file.json> save|load
"""
import json
import os
import sys
import traceback

import bpy

zip_path, bundle, work, path, mode = sys.argv[sys.argv.index('--') + 1:][:5]
ok = False
try:
    bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)
    bpy.ops.preferences.addon_enable(module='bf2_map_tool')
    from bf2_map_tool import prefs as P
    p = P.prefs()
    if mode == 'save':
        p.tools_dir, p.work_dir, p.author = bundle, work, 'Night Tester'
        bpy.ops.bf2.find_paths()
        bpy.ops.bf2.export_settings(filepath=path)
        d = json.load(open(path))
        d['prefs']['fluxbake'] = 'Q:/not/on/this/pc/fluxbake.exe'      # one path the other PC does not have
        json.dump(d, open(path, 'w'), indent=1)
        ok = all(d['prefs'][k] for k in ('game_path', 'frostycli', 'havok_tool')) and d['prefs']['author'] == 'Night Tester'
        print('SETTINGS_SAVE', json.dumps(d['prefs']))
    else:
        r = bpy.ops.bf2.import_settings(filepath=path)
        got = {k: getattr(p, k) for k in P.PATH_KEYS + ('author', 'work_dir')}
        refused = [(l.item, l.message) for l in p.setup if not l.ok]
        ok = (got['author'] == 'Night Tester' and os.path.isfile(got['frostycli']) and os.path.isdir(got['game_path'])
              and got['fluxbake'] == '' and any('Light baker' in x[0] for x in refused))
        print('SETTINGS_LOAD', json.dumps({'got': got, 'refused': refused}))
except Exception:
    traceback.print_exc()
print('SETTINGS_TEST ok=%s' % ok)
sys.stdout.flush()
os._exit(0 if ok else 1)
