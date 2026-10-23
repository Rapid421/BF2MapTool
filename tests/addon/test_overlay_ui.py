"""Switch the collision overlay on in a windowed Blender and photograph the window.
    set BLENDER_USER_SCRIPTS=<profile with the add-on installed>
    blender --factory-startup <scene.blend> --python test_overlay_ui.py -- <out.png> <out.json>
The picture is for eyes; the json holds what the overlay counted (compare with tests/test_overlay.py) and any error.
"""
import json
import sys
import traceback

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
png, out = argv[0], argv[1]
R = {'scene': bpy.data.filepath, 'steps': []}
S = {'n': 0}


def view3d():
    for win in bpy.context.window_manager.windows:
        for area in win.screen.areas:
            if area.type == 'VIEW_3D':
                return win, area, next(r for r in area.regions if r.type == 'WINDOW')
    return None, None, None


def tick():
    S['n'] += 1
    try:
        win, area, region = view3d()
        if S['n'] == 1:
            bpy.ops.preferences.addon_enable(module='bf2_map_tool')
            from bf2_map_tool import overlay
            wm = bpy.context.window_manager
            wm.bf2_tab = 'collision'
            with bpy.context.temp_override(window=win, area=area, region=region):
                area.spaces.active.show_region_ui = True
                bpy.ops.view3d.view_all()
                area.spaces.active.shading.type = 'SOLID'
                wm.bf2_show_collision = True
            R['count'] = dict(overlay._cache['count'])
            R['handler'] = overlay._handle is not None
            R['steps'].append('overlay on')
            return 2.0
        if S['n'] == 2:
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.screen.screenshot(filepath=png)
            R['steps'].append('screenshot')
            return 0.5
    except Exception:
        R['error'] = traceback.format_exc()
    with open(out, 'w') as f:
        json.dump(R, f, indent=1)
    bpy.ops.wm.quit_blender()
    return None


bpy.app.timers.register(tick, first_interval=2.0)
