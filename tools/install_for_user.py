"""Install the packaged add-on into the user's OWN Blender profile (not a throw-away one), enable it, fill the
preferences from a settings file and save the preferences - what Preferences > Add-ons > Install from Disk + typing
the paths does by hand. Run WITHOUT --factory-startup so the user's profile is the one changed:

    blender -b --python install_for_user.py -- <addon zip> <settings.json>
"""
import json
import sys

import bpy

zip_path, settings_path = sys.argv[sys.argv.index('--') + 1:][:2]
res = {'blender': bpy.app.version_string, 'config': bpy.utils.user_resource('CONFIG'), 'scripts': bpy.utils.user_resource('SCRIPTS')}
bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)
bpy.ops.preferences.addon_enable(module='bf2_map_tool')
res['enabled'] = 'bf2_map_tool' in bpy.context.preferences.addons
p = bpy.context.preferences.addons['bf2_map_tool'].preferences
s = json.load(open(settings_path, encoding='utf-8'))
for k in ('game_path', 'frostycli', 'plugins_path', 'havok_tool', 'havok_template', 'fluxbake', 'work_dir'):
    setattr(p, k, s[k])
for k in ('python', 'blender'):
    if hasattr(p, k) and s.get(k):
        setattr(p, k, s[k])
bpy.ops.bf2.check_setup()
res['setup'] = [(l.item, bool(l.ok), l.message[:70]) for l in p.setup]
bpy.ops.wm.save_userpref()
res['saved'] = True
print('USER_INSTALL ' + json.dumps(res, default=str))
