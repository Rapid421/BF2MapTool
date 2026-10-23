"""Sidebar panel "BF2 Map": one tab per stage of the work - Map, Materials, Collision, Gameplay, Lighting, Check, Build."""
import bpy
from bpy.props import EnumProperty, IntProperty

from . import build, ops, overlay, props
from . import prefs as P

TABS = [('map', 'Map', ''), ('materials', 'Materials', ''), ('collision', 'Collision', ''), ('gameplay', 'Gameplay', ''),
        ('lighting', 'Lighting', ''), ('check', 'Check', ''), ('build', 'Build', '')]
MODE_NEEDS = {'supremacy': 5}        # command posts a Supremacy map needs


def _count(context, role):
    return sum(1 for o in context.view_layer.objects if o.bf2.role == role)


class BF2_UL_materials(bpy.types.UIList):
    def draw_item(self, context, layout, data, item, icon, active_data, active_propname, index):
        row = layout.row(align=True)
        row.label(text=item.name, icon='MATERIAL')
        row.prop(item.bf2, 'cls', text='')
        if item.bf2.cls_guessed:
            row.label(text='', icon='QUESTION')


class BF2_PT_main(bpy.types.Panel):
    bl_idname = 'BF2_PT_main'
    bl_label = 'BF2 Map'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'BF2 Map'

    def draw(self, context):
        layout = self.layout
        wm = context.window_manager
        try:
            p = P.prefs(context)
            unset = [k for k in P.PATH_KEYS + ('work_dir',) if not getattr(p, k)]
        except (KeyError, AttributeError):
            unset = []
        if unset:
            box = layout.box()
            box.label(text='First set up the tool (%d path(s) not set)' % len(unset), icon='ERROR')
            box.operator('bf2.find_paths', icon='VIEWZOOM')
            box.operator('preferences.addon_show', text='Open the preferences', icon='PREFERENCES').module = P.ADDON
        layout.prop(wm, 'bf2_tab', expand=True)
        getattr(self, 'tab_' + wm.bf2_tab)(context, layout.column())

    # ---- Map
    def tab_map(self, context, col):
        s = context.scene.bf2
        col.prop(s, 'name')
        ok = s.name.isalnum() and s.name[:1].isalpha() and 2 <= len(s.name) <= 24
        if not ok:
            col.label(text='2-24 letters or digits, starting with a letter', icon='ERROR')
        else:
            box = col.box()
            box.label(text='Level: AddedContent/%s/Levels/%s_01' % (s.name, s.name))
            box.label(text='Assets: Objects/Ported/%s' % s.name)
        col.prop(s, 'display_name'); col.prop(s, 'planet'); col.prop(s, 'description'); col.prop(s, 'era')
        row = col.row()
        row.prop(s, 'menu_picture')
        if s.menu_picture:
            from . import reflbake
            cam = reflbake.menu_camera(context.scene)
            col.label(text=('Loading picture: from camera "%s"' % cam.name) if cam else 'Loading picture: no camera - the host level\'s screen',
                      icon='CAMERA_DATA' if cam else 'INFO')
        col.separator()
        col.label(text='Game modes')
        spawns = _count(context, 'spawn'); heroes = _count(context, 'hero_spawn'); cps = _count(context, 'command_post')
        auto = ' (the rest are generated)' if s.auto_spawns else ''
        for key, label, need in (('mode_blast', 'Blast', '%d spawns placed%s' % (spawns, auto)),
                                 ('mode_hvv', 'Heroes vs Villains', '%d hero spawns placed%s' % (heroes, auto)),
                                 ('mode_arcade', 'Arcade', 'uses the Blast spawns'),
                                 ('mode_supremacy', 'Supremacy', '%d command posts, %d placed' % (MODE_NEEDS['supremacy'], cps))):
            row = col.row()
            row.prop(s, key)
            if getattr(s, key):
                row.label(text=need, icon='ERROR' if key == 'mode_supremacy' and cps != MODE_NEEDS['supremacy'] else 'NONE')
        col.label(text='Host shell: Kamino (chosen by the tool)', icon='INFO')
        col.separator()
        col.label(text='Scale: 1 Blender unit = 1 metre')
        row = col.row(align=True)
        row.operator('bf2.add_marker', text='Add a 1.8 m reference figure', icon='OUTLINER_OB_ARMATURE').kind = 'figure'
        row.operator('bf2.scale_map', text='Scale map', icon='FULLSCREEN_ENTER')
        col.operator('bf2.prepare_import', icon='IMPORT')
        col.operator('bf2.guess_roles', icon='VIEWZOOM')
        col.prop(s, 'play_radius')
        col.prop(s, 'keep_distance')
        col.separator()
        box = col.box()
        row = box.row()
        row.label(text='Size of the build', icon='MEMORY')
        row.operator('bf2.estimate', text='', icon='FILE_REFRESH')
        try:
            import json
            e = json.loads(context.window_manager.bf2_estimate or '{}')
        except ValueError:
            e = {}
        if e:
            box.label(text='%d triangles, %d materials' % (e['triangles'], e['materials']))
            box.label(text='about %d pieces (a level holds about %d)' % (e['pieces'], e['piece_limit']),
                      icon='ERROR' if e['pieces'] > e['piece_limit'] else 'NONE')
            box.label(text='about %.0f MB of textures' % e['texture_mb'])
            box.label(text='%d real-time lamps (budget %d)' % (e['realtime_lights'], e['light_budget']),
                      icon='ERROR' if e['realtime_lights'] > e['light_budget'] else 'NONE')
            if e['pieces'] > e['piece_limit']:
                box.label(text='Too many pieces: raise "Triangles per piece" or leave far parts out')
        else:
            box.label(text='Press the arrow to estimate (a few seconds)')

    # ---- Materials
    def tab_materials(self, context, col):
        mats = ops.scene_materials(context)
        col.operator('bf2.guess_classes', icon='VIEWZOOM')
        row = col.row(align=True)
        row.prop(context.scene.bf2, 'bake_materials'); row.prop(context.scene.bf2, 'bake_max_size', text='')
        col.label(text='%d materials in the map (a question mark = guessed, not confirmed)' % len(mats))
        col.template_list('BF2_UL_materials', '', bpy.data, 'materials', context.window_manager, 'bf2_material_index', rows=8)
        i = context.window_manager.bf2_material_index
        if 0 <= i < len(bpy.data.materials):
            m = bpy.data.materials[i]
            b = m.bf2
            box = col.box()
            box.label(text=m.name, icon='MATERIAL')
            box.prop(b, 'cls')
            if b.cls == 'opaque':
                box.prop(b, 'cls_auto')
            if b.cls_guessed:
                box.prop(b, 'cls_guessed', text='Guessed (untick when you have checked it)')
            if b.cls in ('emissive', 'lava', 'cutout_emissive'):
                box.prop(b, 'emission_screen'); box.prop(b, 'emission_bake')
            if b.cls == 'lava':
                box.prop(b, 'lava_color'); box.prop(b, 'lava_brightness'); box.prop(b, 'lava_crust'); box.prop(b, 'lava_speed')
            if b.cls == 'holo':
                box.prop_search(b, 'holo_image', bpy.data, 'images'); box.prop(b, 'holo_speed'); box.prop(b, 'holo_tiling'); box.prop(b, 'holo_brightness')
            if b.cls not in ('cutout2s',):
                box.prop(b, 'double_sided')
            box.prop(b, 'normal_format')
            box.prop(b, 'skip')
            box.label(text='Packed automatically from the Principled BSDF:')
            lines = {'opaque': ('CS = colour + smoothness', 'NAM = normal (DirectX) + metal'),
                     'emissive': ('CS = colour + smoothness', 'NAM = normal + metal', 'E = emission'),
                     'cutout': ('CS = colour + smoothness', 'NW = normal + cut-out mask (alpha)'),
                     'cutout2s': ('CS = colour + smoothness', 'NW = normal + cut-out mask', 'reversed faces added'),
                     'glass': ('the glass template keeps its own textures',), 'lava': ('the lava preset supplies its own textures',),
                     'holo': ('HC = glow colour (the image, or the emission) + flicker', 'scrolls in game; scan lines from the shader'),
                     'cutout_emissive': ('a cut-out (CS + NW, holes from the alpha)', 'plus a glowing layer 2 mm in front: emission x alpha')}
            for l in lines.get(b.cls, ()):
                box.label(text='   ' + l)

    # ---- Collision
    def tab_collision(self, context, col):
        o = context.active_object
        col.label(text='Collision of the selected objects')
        row = col.row(align=True)
        for k, label, _ in props.COLLISION:
            row.operator('bf2.set_collision', text=label).mode = k
        if o and o.type == 'MESH':
            box = col.box()
            box.label(text=o.name, icon='OBJECT_DATA')
            box.prop(o.bf2, 'collision')
            if o.bf2.collision == 'custom':
                box.prop(o.bf2, 'collision_object')
                box.label(text='or name the collision mesh "%s_col" / "UCX_%s"' % (o.name, o.name))
            box.label(text={'exact': 'Every render triangle is solid (walk + weapon fire).', 'custom': 'The collision mesh replaces the render triangles.',
                            'hull': 'One convex shape (moving parts).', 'none': 'Not solid.'}[o.bf2.collision])
        n = {k: 0 for k, _, _ in props.COLLISION}
        for x in context.view_layer.objects:
            if x.type == 'MESH' and x.bf2.role == 'static':
                n[x.bf2.collision] += 1
        col.label(text='Map objects: %d exact, %d custom, %d hull, %d none' % (n['exact'], n['custom'], n['hull'], n['none']))
        col.label(text='Lava materials are never solid.', icon='INFO')
        col.separator()
        overlay.draw_ui(context, col)

    # ---- Gameplay
    def tab_gameplay(self, context, col):
        s = context.scene.bf2
        col.label(text='Add at the 3D cursor')
        g = col.grid_flow(columns=2, align=True)
        for k, label in (('spawn1', 'Team 1 spawn'), ('spawn2', 'Team 2 spawn'), ('spawn0', 'Neutral spawn'), ('hero1', 'Hero spawn 1'),
                         ('hero2', 'Hero spawn 2'), ('cp', 'Command post'), ('kill', 'Kill volume'), ('nowalk', 'Not walkable'),
                         ('combat', 'Combat area'), ('probe', 'Reflection probe'), ('wall', 'Invisible wall')):
            g.operator('bf2.add_marker', text=label).kind = k
        col.prop(s, 'auto_spawns')
        col.label(text='Placed: %d spawns, %d hero spawns, %d command posts, %d kill volumes, %d doors' % (
            _count(context, 'spawn'), _count(context, 'hero_spawn'), _count(context, 'command_post'), _count(context, 'kill_volume'),
            _count(context, 'door')))
        o = context.active_object
        if o:
            box = col.box()
            box.label(text=o.name, icon='OBJECT_DATA')
            box.prop(o.bf2, 'role')
            r = o.bf2.role
            if r in ('spawn', 'hero_spawn'):
                box.prop(o.bf2, 'team')
            elif r == 'command_post':
                box.prop(o.bf2, 'cp_letter'); box.prop(o.bf2, 'cp_radius')
                # only what the Supremacy stage really uses is shown: the letter, the place and the capture radius
                box.label(text='A, B: team 1\'s side. C: the middle. D, E: team 2\'s side.')
                box.label(text='All posts start neutral; 13 spawns are put around each.')
                box.label(text='Starting owner, vehicles at a post: not in this version.')
            elif r == 'door':
                box.prop(o.bf2, 'door_axis'); box.prop(o.bf2, 'door_distance')
            elif r == 'collision':
                box.label(text='Solid, never drawn. Named by another object\'s Custom mesh: that object\'s shape;')
                box.label(text='named by none: an invisible wall / blocker of its own.')
            elif r in ('sound', 'fx'):
                box.prop(o.bf2, 'preset')
        col.operator('bf2.mark_selected', text='Selected mesh is a door').role = 'door'
        wf = build.work_folder(context)
        if wf:
            col.label(text='Spawn map after a build: %s/spawn_map.png' % wf)

    # ---- Lighting
    def tab_lighting(self, context, col):
        s = context.scene.bf2
        col.prop(s, 'sky')
        if s.sky == 'hdr':
            col.prop(s, 'sky_hdr')
        if s.sky == 'game':
            box = col.box()
            box.prop(s, 'game_sky')
            box.prop(s, 'game_sky_asset')
            box.prop(s, 'exposure_mode', expand=True)
            sun = next((o for o in context.view_layer.objects if o.type == 'LIGHT' and o.data.type == 'SUN' and not o.hide_get()), None)
            row = box.row()
            row.prop(s, 'sun_from_scene')
            row.enabled = sun is not None
            box.label(text=('Sun direction: lamp "%s"' % sun.name) if (sun and s.sun_from_scene) else 'Sun direction: the game sky\'s own',
                      icon='LIGHT_SUN')
            box.prop(s, 'exposure', text='Exposure offset (EV)')
            box.label(text='Sky, sun strength, fog and colour grading come from the game level.', icon='INFO')
        if s.sky == 'world':
            w = context.scene.world
            col.label(text='Sky = World "%s"' % w.name if w else 'The scene has no World: an overcast sky is used', icon='WORLD' if w else 'ERROR')
            col.label(text='Sun, lamps and emission keep their Blender strengths.')
            col.prop(s, 'ev_level')
        if s.sky == 'world':
            col.prop(s, 'exposure')
            col.prop(s, 'fog_on')
        elif s.sky != 'game':
            col.prop(s, 'sky_strength'); col.prop(s, 'sun_on'); col.prop(s, 'exposure')
        if (s.sky not in ('world', 'game')) or (s.sky == 'world' and s.fog_on):
            col.prop(s, 'fog_color'); col.prop(s, 'fog_start'); col.prop(s, 'fog_end'); col.prop(s, 'fog_density')
        col.separator()
        lights = [o for o in context.view_layer.objects if o.type == 'LIGHT' and o.data.type != 'SUN']
        n = {'baked': 0, 'dynamic': 0, 'both': 0}
        for o in lights:
            n[o.data.bf2.mode] += 1
        col.label(text='Lights: %d baked, %d dynamic, %d both' % (n['baked'], n['dynamic'], n['both']))
        dyn = n['dynamic'] + n['both']
        col.prop(s, 'dynamic_light_budget')
        if dyn > s.dynamic_light_budget:
            col.label(text='%d real-time lights: over the budget' % dyn, icon='ERROR')
            col.operator('bf2.fit_light_budget', icon='LIGHT')
        o = context.active_object
        if o and o.type == 'LIGHT':
            box = col.box()
            box.label(text=o.name, icon='LIGHT')
            box.prop(o.data.bf2, 'mode')
            if o.data.bf2.mode != 'baked':
                box.prop(o.data.bf2, 'shadows')
            if o.data.bf2.mode != 'dynamic':
                box.prop(o.data.bf2, 'bake_strength')
            box.label(text='%.0f W in Blender' % o.data.energy)
        col.label(text='Emissive materials light the bake automatically.', icon='INFO')

    # ---- Check
    def tab_check(self, context, col):
        st = build.state(context)
        row = col.row()
        row.enabled = not st.running
        row.operator('bf2.check', icon='VIEWZOOM')
        errs = sum(1 for r in st.results if r.severity == 'error'); warns = sum(1 for r in st.results if r.severity == 'warning')
        if st.results:
            col.label(text='%d error(s), %d warning(s)%s' % (errs, warns, ' - errors block the build' if errs else ''),
                      icon='ERROR' if errs else 'CHECKMARK')
        for i, r in enumerate(st.results):
            box = col.box()
            row = box.row()
            row.label(text=r.message[:120], icon={'error': 'CANCEL', 'warning': 'ERROR'}.get(r.severity, 'INFO'))
            if r.objects or r.materials:
                row.operator('bf2.select_result', text='', icon='RESTRICT_SELECT_OFF').index = i
            if r.code in ops.FIXES:
                row.operator(ops.FIXES[r.code], text='Fix', icon='TOOL_SETTINGS')
            if len(r.message) > 120:
                box.label(text='   ' + r.message[120:240])
            if r.fix:
                box.label(text='   ' + r.fix[:140])

    # ---- Build
    def tab_build(self, context, col):
        st = build.state(context)
        s = context.scene.bf2
        row = col.row()
        row.scale_y = 1.6
        row.enabled = not st.running
        row.operator('bf2.port', icon='EXPORT')
        row = col.row(align=True)
        row.prop(s, 'bake_on_build')
        row.prop(s, 'bake_quality', text='')
        if not s.bake_on_build:
            col.label(text='Without the light bake the level has no bounce light and looks flat.', icon='ERROR')
        row = col.row(align=True)
        row.enabled = not st.running
        row.operator('bf2.bake', icon='LIGHT_SUN')
        row = col.row(align=True)
        row.enabled = not st.running
        row.operator('bf2.rebuild_mod', icon='FILE_REFRESH')
        row = col.row(align=True)
        row.operator('bf2.open_log', icon='TEXT')
        sub = row.row()
        sub.enabled = st.running
        sub.operator('bf2.cancel', icon='CANCEL')
        col.operator('bf2.bug_report', icon='URL')
        if st.running or st.message:
            box = col.box()
            m, sec = divmod(int(st.elapsed), 60)
            box.label(text=('%s  -  %d:%02d' % (st.message, m, sec))[:120], icon='TIME' if st.running else ('CHECKMARK' if st.ok else 'ERROR'))
            if st.running and st.tail:
                box.label(text=st.tail[:110])
            if not st.running and not st.ok and len(st.message) > 120:
                for k in range(120, min(len(st.message), 480), 120):
                    box.label(text=st.message[k:k + 120])
        icons = {'start': 'PLAY', 'done': 'CHECKMARK', 'skip': 'CHECKBOX_HLT', 'fail': 'CANCEL'}
        for r in st.stages:
            row = col.row()
            row.label(text=r.name, icon=icons.get(r.status, 'DOT'))
            row.label(text={'skip': 'up to date', 'start': 'running...', 'fail': 'stopped'}.get(r.status, '%.0f s' % r.seconds))
        if st.fbmod:
            box = col.box()
            box.label(text='Mod written:', icon='FILE')
            box.label(text=st.fbmod)
            box.operator('bf2.open_folder', icon='FILE_FOLDER')


def _dummy(self, context):
    return None


CLASSES = (BF2_UL_materials, BF2_PT_main)


def register_props():
    bpy.types.WindowManager.bf2_tab = EnumProperty(items=TABS, default='map')
    bpy.types.WindowManager.bf2_material_index = IntProperty()
    bpy.types.WindowManager.bf2_estimate = bpy.props.StringProperty()


def unregister_props():
    del bpy.types.WindowManager.bf2_tab
    del bpy.types.WindowManager.bf2_material_index
    del bpy.types.WindowManager.bf2_estimate
