"""The F3 editor features in a WINDOWED Blender, with the packaged add-on installed in a throw-away profile: each step
drives the real panel / operator and photographs the window.
    set BLENDER_USER_SCRIPTS=<profile with the add-on installed>
    blender --factory-startup <built scene.blend> --python test_f3_window.py -- <settings.json> <out dir>
-> <out>/01_map.png ... and <out>/window.json (what each step found)
"""
import json
import os
import sys
import traceback

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
settings, out = argv[0], argv[1]
os.makedirs(out, exist_ok=True)
R = {'scene': bpy.data.filepath, 'steps': []}
S = {'n': 0}


def view3d():
    for win in bpy.context.window_manager.windows:
        for area in win.screen.areas:
            if area.type == 'VIEW_3D':
                return win, area, next(r for r in area.regions if r.type == 'WINDOW')
    return None, None, None


def shot(name):
    win, area, region = view3d()
    with bpy.context.temp_override(window=win, area=area, region=region):
        bpy.ops.screen.screenshot(filepath=os.path.join(out, name + '.png'))
    R['steps'].append(name)


def frame(objs):
    win, area, region = view3d()
    for o in bpy.context.view_layer.objects:
        o.select_set(o in objs)
    if objs:
        bpy.context.view_layer.objects.active = objs[0]
    with bpy.context.temp_override(window=win, area=area, region=region):
        bpy.ops.view3d.view_selected()
        bpy.ops.view3d.zoom(delta=-4)


def tick():
    S['n'] += 1
    n = S['n']
    wm = bpy.context.window_manager
    try:
        win, area, region = view3d()
        sp = area.spaces.active
        if n == 1:
            bpy.ops.preferences.addon_enable(module='bf2_map_tool')
            p = bpy.context.preferences.addons['bf2_map_tool'].preferences
            s = json.load(open(settings))
            for k in ('game_path', 'frostycli', 'plugins_path', 'havok_tool', 'havok_template', 'fluxbake', 'work_dir'):
                setattr(p, k, s[k])
            sp.show_region_ui = True
            ui = next(r for r in area.regions if r.type == 'UI')
            # the sidebar tab cannot be picked from Python (Region.active_panel_category is read-only): for the
            # pictures the panel is registered again in the tab that shows, above Blender's own panels
            from bf2_map_tool import ui as UI
            bpy.utils.unregister_class(UI.BF2_PT_main)
            UI.BF2_PT_main.bl_category = 'Item'
            UI.BF2_PT_main.bl_order = -100
            bpy.utils.register_class(UI.BF2_PT_main)
            R['panel_tab_for_pictures'] = UI.BF2_PT_main.bl_category
            with bpy.context.temp_override(window=win, area=area, region=ui):
                for _ in range(6):
                    bpy.ops.view2d.zoom_out()       # the whole panel on the picture
            sp.shading.type = 'SOLID'
            sc = bpy.context.scene
            fig = bpy.data.objects.get('BF2 soldier (1.8 m)')
            sc.cursor.location = (14.0, -3.0, 0.0)
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.bf2.add_marker(kind='figure')
            new = bpy.context.active_object
            R['soldier'] = {'name': new.name, 'height': round(new.dimensions.z, 3), 'role': new.bf2.role}
            wm.bf2_tab = 'map'
            frame([new] + [o for o in bpy.data.objects if o.name.startswith('crate')])
            return 1.5
        if n == 2:
            shot('01_map_soldier')
            return 0.5
        if n == 3:
            wm.bf2_tab = 'gameplay'
            with bpy.context.temp_override(window=win, area=area, region=region):
                wm.bf2_show_build = True
                bpy.ops.view3d.view_all()
            from bf2_map_tool import overlay
            R['build_view'] = {'error': overlay._bv['error'], 'lines': overlay._bv['lines_text'][:6], 'handler': overlay._bv['handle'] is not None}
            return 2.0
        if n == 4:
            shot('03_gameplay_show_the_build')
            wm.bf2_show_build = False
            wm.bf2_tab = 'materials'
            with bpy.context.temp_override(window=win, area=area, region=region):
                r = bpy.ops.bf2.game_look()
            from bf2_map_tool import gamelook
            R['game_look'] = {'result': list(r), 'on': gamelook.is_on(), 'shading': sp.shading.type,
                              'copies': sum(1 for m in bpy.data.materials if m.get('bf2_game_look_of'))}
            frame([o for o in bpy.data.objects if o.name.startswith('crate')])
            return 4.0
        if n == 5:
            shot('04_materials_game_look_on')
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.bf2.game_look()
            from bf2_map_tool import gamelook
            R['game_look']['off'] = not gamelook.is_on()
            wm.bf2_tab = 'build'
            return 1.5
        if n == 6:
            shot('05_build_tab_clean_caches')
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.wm.call_menu_pie(name='BF2_MT_add_pie')
            return 1.0
        if n == 7:
            shot('06_pie_menu')
            kc = wm.keyconfigs.addon
            R['pie_keymap'] = [(km.name, i.type, bool(i.shift), bool(i.alt)) for km in kc.keymaps for i in km.keymap_items
                               if i.idname == 'wm.call_menu_pie' and i.properties.name == 'BF2_MT_add_pie']
    except Exception:
        R['error'] = traceback.format_exc()
    with open(os.path.join(out, 'window.json'), 'w') as f:
        json.dump(R, f, indent=1)
    bpy.ops.wm.quit_blender()
    return None


bpy.app.timers.register(tick, first_interval=3.0)
