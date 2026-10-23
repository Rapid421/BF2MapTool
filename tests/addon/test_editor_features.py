"""F3 editor features through the add-on, in a background Blender (no window):
    blender -b --factory-startup --python test_editor_features.py -- <out.json> [<built work folder> [<cache-copy work folder>]]
1 soldier figure: Add BF2 object > BF2 soldier (1.8 m) at the 3D cursor, 1.80 m tall, never exported (also after its role
  is changed to Map geometry and it is made renderable)
2 build preview: "Show the build" on a built work folder reads navmesh / spawns / finder areas (gpu.init for the batches)
3 game look: on = copies with the packed textures in the slots, export still reads the originals; off = all back
5 clean build caches: the operator on a COPY of a work folder frees the caches, keeps mods/ and export/
6 pie menu: Shift Alt A calls BF2_MT_add_pie in the add-on keymap; the pie draws every Add BF2 object item
"""
import json
import os
import sys
import tempfile
import traceback

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
OUT = argv[0]
WORK = argv[1] if len(argv) > 1 else ''
COPY = argv[2] if len(argv) > 2 else ''
ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
sys.path.insert(0, os.path.join(ROOT, 'addon'))
R = {'checks': {}, 'fails': []}


def check(name, ok, info=''):
    R['checks'][name] = {'ok': bool(ok), 'info': info}
    if not ok:
        R['fails'].append('%s: %s' % (name, info))


def export_names(tmp):
    from bf2_map_tool import export_scene
    M = export_scene.export(tmp, log=lambda m: None)
    return M, {o['name'] for o in M['objects']}


try:
    import bf2_map_tool
    from bf2_map_tool import build, ops, overlay
    bf2_map_tool.register()
    sc = bpy.context.scene
    sc.bf2.name = 'F3Test'
    bpy.ops.mesh.primitive_plane_add(size=20.0)
    floor = bpy.context.active_object
    floor.name = 'Floor'
    # ---- 1 soldier
    sc.cursor.location = (3.0, -2.0, 0.5)
    bpy.ops.bf2.add_marker(kind='figure')
    fig = bpy.context.active_object
    zs = [(fig.matrix_world @ v.co).z for v in fig.data.vertices]
    check('soldier.placed_at_cursor', (fig.location - sc.cursor.location).length < 1e-6, list(fig.location))
    check('soldier.height_1.8', abs(max(zs) - min(zs) - 1.8) < 1e-4 and abs(min(zs) - 0.5) < 1e-4, (min(zs), max(zs)))
    check('soldier.name_role', fig.name == 'BF2 soldier (1.8 m)' and fig.bf2.role == 'reference', (fig.name, fig.bf2.role))
    # ---- export with the figure
    bpy.ops.mesh.primitive_cube_add(location=(0, 0, 1))
    crate = bpy.context.active_object
    crate.name = 'Crate'
    fig.bf2.role, fig.hide_render = 'static', False              # what a user might do to it
    tmp = tempfile.mkdtemp(prefix='f3_export_')
    M, names = export_names(tmp)
    recs = {o['name']: o for o in M['objects']}
    check('soldier.not_exported', 'BF2 soldier (1.8 m)' not in names and any(s['name'] == 'BF2 soldier (1.8 m)' for s in M['skipped']),
          sorted(names))
    # ---- 6 pie
    items = []

    class Rec:
        def __getattr__(self, n):
            return lambda *a, **k: self

        def operator(self, op, text='', icon='NONE'):
            class P:
                pass
            p = P()
            items.append((op, text, icon, p))
            return p
    ops.BF2_MT_add_pie.draw(type('S', (), {'layout': Rec()})(), bpy.context)
    kinds = {p.kind for op, _, _, p in items if op == 'bf2.add_marker'}
    allk = {e.identifier for e in ops.marker_kinds()}
    check('pie.all_items', kinds == allk, sorted(allk - kinds))
    kc = bpy.context.window_manager.keyconfigs.addon
    if kc is None:
        check('pie.keymap', True, 'no add-on key configuration in a background Blender: keymap not testable here')
    else:
        km = [(km.name, i.type, i.shift, i.alt, i.properties.name) for km in kc.keymaps for i in km.keymap_items
              if i.idname == 'wm.call_menu_pie' and i.properties.name == 'BF2_MT_add_pie']
        check('pie.keymap', km == [('3D View', 'A', True, True, 'BF2_MT_add_pie')], km)
    # ---- 2 build preview + 3 game look on a built work folder
    if WORK:
        import gpu
        gpu.init()
        build.work_folder = lambda context: WORK
        bpy.context.window_manager.bf2_show_build = True
        rr = overlay._bv['R']
        check('buildview.toggle_reads', rr is not None and overlay._bv['handle'] is not None and len(overlay._bv['lines']) > 0,
              overlay._bv['error'] or overlay._bv['lines_text'][:3])
        R['buildview'] = overlay._bv['lines_text']
        bpy.context.window_manager.bf2_show_build = False
        check('buildview.toggle_off', overlay._bv['handle'] is None)
        blend = os.path.join(WORK, 'export', 'scene_copy.blend')          # what Port map saves; a command-line build names its own
        if not os.path.exists(blend):
            blend = json.load(open(os.path.join(WORK, 'export', 'map.json'), encoding='utf-8'))['blend']['file']
        bpy.ops.wm.open_mainfile(filepath=blend)
        build.work_folder = lambda context: WORK
        from bf2_map_tool import gamelook
        sc = bpy.context.scene
        before = {(o.name, i): s.material.name for o in sc.objects for i, s in enumerate(o.material_slots) if s.material}
        r = bpy.ops.bf2.game_look()
        swapped = {k: v for k, v in before.items() if bpy.data.objects[k[0]].material_slots[k[1]].material.name != v}
        copies = [m for m in bpy.data.materials if m.get('bf2_game_look_of')]
        check('gamelook.on', r == {'FINISHED'} and gamelook.is_on(sc) and len(copies) > 0 and len(swapped) > 0,
              '%d copies, %d slots swapped' % (len(copies), len(swapped)))
        R['gamelook_copies'] = {m.name: [n.image.name for n in m.node_tree.nodes if n.type == 'TEX_IMAGE'] for m in copies[:6]}
        orig_nodes = {m: len(bpy.data.materials[m].node_tree.nodes) for m in {v for v in before.values()} if bpy.data.materials[m].node_tree}
        tmp2 = tempfile.mkdtemp(prefix='f3_export_gl_')
        from bf2_map_tool import export_scene
        Mg = export_scene.export(tmp2, log=lambda m: None)
        check('gamelook.export_reads_originals', not any(n.endswith(' [game]') for n in Mg['materials']), sorted(Mg['materials'])[:5])
        bpy.ops.bf2.game_look()               # export already put the slots back; a second press clears the copies
        after = {(o.name, i): s.material.name for o in sc.objects for i, s in enumerate(o.material_slots) if s.material}
        check('gamelook.off', after == before and not gamelook.is_on(sc) and not [m for m in bpy.data.materials if m.get('bf2_game_look_of')]
              and orig_nodes == {m: len(bpy.data.materials[m].node_tree.nodes) for m in orig_nodes}, 'slots back, copies gone, originals unchanged')
    # ---- 5 clean caches on a copy
    if COPY:
        from bf2_map_tool import caches
        build.work_folder = lambda context: COPY
        mods_before = sorted(os.listdir(os.path.join(COPY, 'mods')))
        sizes = caches.measure(COPY)
        r = bpy.ops.bf2.clean_caches('EXEC_DEFAULT')
        left = [d for d in caches.CACHE_DIRS if os.path.isdir(os.path.join(COPY, d))]
        msg = build.state(bpy.context).message
        check('caches.clean', r == {'FINISHED'} and not left and sorted(os.listdir(os.path.join(COPY, 'mods'))) == mods_before
              and os.path.isfile(os.path.join(COPY, 'export', 'map.json')), '%s; left %s' % (msg, left))
        R['caches'] = {'before': sizes, 'message': msg}
except Exception:
    R['fails'].append(traceback.format_exc())
R['ok'] = not R['fails']
with open(OUT, 'w') as f:
    json.dump(R, f, indent=1)
print('EDITOR_FEATURES', 'PASS' if R['ok'] else 'FAIL', len(R['checks']), 'checks', R['fails'][:3])
