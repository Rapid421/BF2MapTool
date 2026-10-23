"""Operators: setup check, build buttons, gameplay objects in the Add menu, material class guess, selection helpers."""
import json
import math
import os
import subprocess

import numpy as np

import bpy
import mathutils
from bpy.props import EnumProperty, IntProperty, StringProperty

from . import export_scene
from . import guess
from . import build, prefs as P, props


# ------------------------------------------------------------------ setup
class BF2_OT_check_setup(bpy.types.Operator):
    bl_idname = 'bf2.check_setup'
    bl_label = 'Check setup'
    bl_description = 'Run every external tool with a harmless command and show what works'

    def execute(self, context):
        p = P.prefs(context)
        p.setup.clear()
        py, rt = P.python_exe(p), P.runtime_dir(p)

        def add(item, ok, message, fix=''):
            r = p.setup.add()
            r.item, r.ok, r.message, r.fix = item, ok, message, fix
        if not py or not os.path.exists(py):
            add('Python', False, 'not found', 'Install Python 3.10 or newer from python.org and pick python.exe here.')
            return {'FINISHED'}
        if not rt or not os.path.isdir(os.path.join(rt, 'bf2maptool')):
            add('Runtime', False, 'the bf2maptool folder was not found',
                'This copy of the add-on is incomplete (it was zipped from the source folder). Remove it and install the released '
                'bf2_map_tool zip, which holds the runtime - or pick a folder that contains "bf2maptool" in Runtime folder.')
            return {'FINISHED'}
        try:
            settings = P.write_settings(p)
        except ValueError as e:
            add('Work folder', False, str(e), 'Pick a folder on a drive with several GB free.')
            return {'FINISHED'}
        env = P.runtime_env(p)
        try:
            r = subprocess.run([py, '-m', 'bf2maptool', 'check-setup', '--settings', settings], capture_output=True, text=True, env=env, cwd=rt,
                               timeout=180, stdin=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        except subprocess.TimeoutExpired:
            add('Setup check', False, 'did not finish within 3 minutes', 'A tool hangs when started: try each path by hand.')
            return {'FINISHED'}
        n = 0
        for line in (r.stdout or '').splitlines():
            if line.startswith('@@SETUP '):
                try:
                    j = json.loads(line[8:])
                except ValueError:
                    continue
                add(j.get('item', '?'), bool(j.get('ok')), j.get('message', ''), j.get('fix', ''))
                n += 1
        if n == 0:
            add('Setup check', False, 'the runtime did not answer', ((r.stderr or r.stdout or '').strip().splitlines() or [''])[-1][:200])
        bad = sum(1 for l in p.setup if not l.ok)
        self.report({'INFO'} if not bad else {'WARNING'}, 'Setup: %d ok, %d to fix' % (len(p.setup) - bad, bad))
        return {'FINISHED'}


class BF2_OT_find_paths(bpy.types.Operator):
    bl_idname = 'bf2.find_paths'
    bl_label = 'Find everything'
    bl_description = ('Fill the empty or wrong paths by themselves: the game from its install entry (EA app, Steam, Epic), the tools '
                      'from the tool bundle folder, a work folder. Paths that already work are kept')

    def execute(self, context):
        p = P.prefs(context)
        done = P.find_paths(p)
        p.setup.clear()
        for k, path, how in done:
            r = p.setup.add()
            r.item, r.ok, r.message = P.BF2Preferences.bl_rna.properties[k].name, True, '%s  (%s)' % (path, how)
        missing = [k for k in P.PATH_KEYS if not getattr(p, k)]
        for k in missing:
            r = p.setup.add()
            r.item, r.ok, r.message = P.BF2Preferences.bl_rna.properties[k].name, False, 'not found'
            r.fix = 'Pick "Tool bundle folder" (the unzipped bundle) and press Find everything again, or set it by hand.'
        self.report({'INFO'} if not missing else {'WARNING'}, 'Found %d path(s), %d still missing' % (len(done), len(missing)))
        return {'FINISHED'}


_pip = {'thread': None, 'result': None}


class BF2_OT_install_packages(bpy.types.Operator):
    bl_idname = 'bf2.install_packages'
    bl_label = 'Install Python packages'
    bl_description = ('Download numpy, Pillow, scipy and xatlas for the build\'s Python into <work folder>/_python (about 100 MB, '
                      'a minute or two). Nothing else on the PC is changed')

    @classmethod
    def poll(cls, context):
        return _pip['thread'] is None

    def execute(self, context):
        import threading
        p = P.prefs(context)
        if not p.work_dir:
            self.report({'ERROR'}, 'Set the work folder first (or press Find everything).')
            return {'CANCELLED'}
        py, lib = P.python_exe(p), P.pylib_dir(p)        # resolved here, in the main thread

        def work():
            try:
                _pip['result'] = P.install_packages(py, lib)
            except Exception as e:                        # reported by the timer; never a dead thread with no word
                _pip['result'] = (False, 'install failed: %s' % e)

        _pip['result'] = None
        _pip['thread'] = threading.Thread(target=work, daemon=True)
        _pip['thread'].start()
        line = p.setup.add()
        line.item, line.ok, line.message = 'Python packages', True, 'installing... (Blender stays usable)'

        def poll():
            t = _pip['thread']
            if t is not None and t.is_alive():
                return 1.0
            _pip['thread'] = None
            ok, msg = _pip['result'] or (False, 'no result')
            try:
                pr = P.prefs()
                ln = next((l for l in pr.setup if l.item == 'Python packages'), None) or pr.setup.add()
                ln.item, ln.ok, ln.message = 'Python packages', ok, msg
                ln.fix = '' if ok else 'Check the internet connection, or pick a python.exe that already has the packages.'
                for w in bpy.context.window_manager.windows:
                    for a in w.screen.areas:
                        a.tag_redraw()
            except Exception:
                pass
            print('BF2 Map Tool: ' + msg)
            return None
        bpy.app.timers.register(poll, first_interval=1.0)
        self.report({'INFO'}, 'Installing the Python packages in the background...')
        return {'FINISHED'}


# ------------------------------------------------------------------ build
class _Start:
    kind = 'build'
    extra = ()

    def start_kind(self, context):
        return self.kind

    def execute(self, context):
        err = build.start(context, self.start_kind(context), self.extra_args(context))
        if err:
            self.report({'ERROR'}, err)
            return {'CANCELLED'}
        return {'FINISHED'}

    def extra_args(self, context):
        return list(self.extra)


class BF2_OT_port(_Start, bpy.types.Operator):
    bl_idname = 'bf2.port'
    bl_label = 'Port map'
    bl_description = ('Build the level and the mod from this scene, with the light bake when "Light bake on Build" is on (the default: '
                      'without it a level has no bounce light or soft shadows and looks flat). Blender stays usable while it runs')
    kind = 'build'

    def start_kind(self, context):
        return 'bake' if getattr(context.scene.bf2, 'bake_on_build', True) else 'build'

    def extra_args(self, context):
        return ['--quality', context.scene.bf2.bake_quality] if self.start_kind(context) == 'bake' else []


class BF2_OT_check(_Start, bpy.types.Operator):
    bl_idname = 'bf2.check'
    bl_label = 'Check scene'
    bl_description = 'Run every check on the scene without building anything'
    kind = 'check'


class BF2_OT_bake(_Start, bpy.types.Operator):
    bl_idname = 'bf2.bake'
    bl_label = 'Bake lighting'
    bl_description = 'Bake lightmaps and probes from the scene\'s lights and emissive materials, and rebuild the mod'
    kind = 'bake'

    def extra_args(self, context):
        return ['--quality', context.scene.bf2.bake_quality]


class BF2_OT_rebuild_mod(_Start, bpy.types.Operator):
    bl_idname = 'bf2.rebuild_mod'
    bl_label = 'Rebuild mod'
    bl_description = 'Redo only the stages whose settings changed (no re-import when the geometry is the same) and write the mod'
    kind = 'build'


class BF2_OT_cancel(bpy.types.Operator):
    bl_idname = 'bf2.cancel'
    bl_label = 'Cancel'
    bl_description = 'Stop the running build. Pressing the build button again continues at the stage it stopped in'

    def execute(self, context):
        build.cancel(context)
        return {'FINISHED'}


class BF2_OT_open_log(bpy.types.Operator):
    bl_idname = 'bf2.open_log'
    bl_label = 'Open log'
    bl_description = 'Open the build log in the system\'s text viewer'

    def execute(self, context):
        wf = build.work_folder(context)
        path = (wf + '/logs/build.log') if wf else ''
        if not path or not os.path.exists(path):
            path = build.state(context).feed
        if not path or not os.path.exists(path):
            self.report({'WARNING'}, 'There is no log yet.')
            return {'CANCELLED'}
        if os.name == 'nt':
            os.startfile(path)
        else:
            subprocess.Popen(['xdg-open', path])
        return {'FINISHED'}


class BF2_OT_open_folder(bpy.types.Operator):
    bl_idname = 'bf2.open_folder'
    bl_label = 'Open mod folder'
    bl_description = 'Open the folder that holds the built .fbmod'

    def execute(self, context):
        st = build.state(context)
        d = os.path.dirname(st.fbmod) if st.fbmod else (build.work_folder(context) + '/mods')
        if not os.path.isdir(d):
            self.report({'WARNING'}, 'No mod has been built yet.')
            return {'CANCELLED'}
        if os.name == 'nt':
            os.startfile(d)
        return {'FINISHED'}


# ------------------------------------------------------------------ selection from results
class BF2_OT_select_result(bpy.types.Operator):
    bl_idname = 'bf2.select_result'
    bl_label = 'Select'
    bl_description = 'Select the objects (or show the material) this line is about'
    index: IntProperty()

    def execute(self, context):
        st = build.state(context)
        if not (0 <= self.index < len(st.results)):
            return {'CANCELLED'}
        r = st.results[self.index]
        names = [n for n in r.objects.split('\n') if n]
        if context.mode != 'OBJECT' and context.active_object:
            bpy.ops.object.mode_set(mode='OBJECT')
        for o in context.view_layer.objects:
            o.select_set(False)
        first = None
        for n in names:
            o = bpy.data.objects.get(n)
            if o and o.name in context.view_layer.objects:
                o.select_set(True)
                first = first or o
        if first:
            context.view_layer.objects.active = first
        mats = [m for m in r.materials.split('\n') if m]
        if mats and not names:
            for o in context.view_layer.objects:
                if o.type == 'MESH' and any(s.material and s.material.name in mats for s in o.material_slots):
                    o.select_set(True)
                    first = first or o
            if first:
                context.view_layer.objects.active = first
                for i, s in enumerate(first.material_slots):
                    if s.material and s.material.name in mats:
                        first.active_material_index = i
                        break
        self.report({'INFO'}, '%d object(s) selected' % sum(1 for o in context.view_layer.objects if o.select_get()))
        return {'FINISHED'}


# ------------------------------------------------------------------ materials
class _ImageRef:
    """stands in for the export's image writer while a socket is traced for a guess: nothing is written"""

    def export(self, img):
        return {'name': img.name, '_image': img}


def alpha_has_holes(sock, stack=()):
    """does a wired Alpha socket really go under one half somewhere? The chain is traced (groups, mixes, separate) to
    the image channel it reads and that channel's pixels are measured. A ripped map's node group routes a channel that
    is 1.0 everywhere to Alpha on every wall (Hunters Bastion: 47 walls guessed as glowing cut-outs). Unreadable -> True"""
    try:
        t = export_scene._trace(sock, _ImageRef(), stack)
    except Exception:                                       # noqa: BLE001 - a guess never fails on an odd node setup
        return True
    if 'value' in t:
        v = t['value']
        v = float(v[0] if isinstance(v, (list, tuple)) else v)
        a = np.array([v], np.float32)
    else:
        img = (t.get('image') or {}).get('_image')
        if img is None or not img.size[0] or not img.size[1]:
            return True
        try:
            px = np.empty(img.size[0] * img.size[1] * img.channels, np.float32)
            img.pixels.foreach_get(px)
        except Exception:                                   # noqa: BLE001
            return True
        px = px.reshape(-1, img.channels)
        ch = t.get('channel', 'RGB')
        if ch == 'RGB':
            a = px[:, :3].mean(1) if img.channels >= 3 else px[:, 0]
        else:
            i = 'RGBA'.index(ch)
            if i >= img.channels:
                return False if ch == 'A' else True         # no alpha channel: alpha reads 1.0
            a = px[:, i]
    if t.get('power', 1.0) != 1.0:
        a = np.power(np.clip(a, 0.0, None), t['power'])
    a = a * t.get('scale', 1.0)
    if t.get('invert'):
        a = 1.0 - a
    return bool((a < 0.5).any())


def scale_scene(context, f):
    """scale the whole map about the world origin: every top-level object's place and size (children follow), lamps
    keep the same light on the resized map. -> number of top-level objects"""
    roots = [o for o in context.scene.objects if o.parent is None]
    for o in roots:
        o.location = o.location * f
        o.scale = o.scale * f
        if o.type == 'LIGHT' and o.data.type != 'SUN':
            o.data.energy *= f * f
    return len(roots)


class BF2_OT_scale_map(bpy.types.Operator):
    bl_idname = 'bf2.scale_map'
    bl_label = 'Scale map'
    bl_description = ('Make the whole map bigger or smaller (every object, spawn, lamp and marker, about the world origin). '
                      'Game soldiers are 1.8 m tall: ripped maps made for other characters (LEGO minifigs) often feel too '
                      'big or too small. Ctrl+Z undoes it')
    bl_options = {'REGISTER', 'UNDO'}
    factor: bpy.props.FloatProperty(name='Factor', default=0.75, min=0.05, max=20.0, description='0.75 = three quarters of the size')

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def execute(self, context):
        n = scale_scene(context, self.factor)
        self.report({'INFO'}, 'Scaled the map by %g (%d top objects)' % (self.factor, n))
        return {'FINISHED'}


# ------------------------------------------------------------------ sky guess
# words in the file / scene / object / material names -> the game sky of that planet
PLANET_WORDS = (('tatooine_sunny', ('tatooine', 'mos_eisley', 'moseisley', 'mos eisley', 'jundland', 'dune sea', 'desert', 'sand')),
                ('hoth_sunny', ('hoth', 'echo base', 'echobase', 'snow', 'ice', 'arctic', 'frozen')),
                ('endor_sunny', ('endor', 'ewok', 'forest', 'bunker')),
                ('naboo_cloudy', ('naboo', 'theed', 'gungan', 'otoh')),
                ('kamino_stormy', ('kamino', 'tipoca')),
                ('kashyyyk_overcast', ('kashyyyk', 'wookie', 'wookiee', 'kachirho')),
                ('jakku_sunny', ('jakku', 'starship graveyard', 'utapau', 'geonosis', 'canyon', 'sinkhole')),
                ('yavin_sunny', ('yavin', 'massassi', 'jungle')),
                ('takodana_cloudy', ('takodana', 'maz')),
                ('starkiller_cloudy', ('starkiller',)),
                ('sullust_cloudy', ('sullust', 'mustafar', 'lava', 'volcan')))
COVERED_INTERIOR = 0.75     # fraction of floor points with something above them that makes a map "interior"


def _floor_cover(context, n=14):
    """(fraction of floor points under a roof, points tried): rays straight down from above a grid over the map find the
    floor, rays straight up from 1.6 m over it look for a roof within 400 m"""
    dg = context.evaluated_depsgraph_get()
    obs = [o for o in context.view_layer.objects if o.type == 'MESH' and o.bf2.role in ('static', 'door') and o.visible_get()]
    pts = []
    for o in obs:
        for c in o.bound_box:
            pts.append(o.matrix_world @ mathutils.Vector(c))
    if not pts:
        return 0.0, 0
    lo = mathutils.Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = mathutils.Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    # the middle half of the map: far edges are backdrops and sky domes
    cx, cy, ex, ey = (lo.x + hi.x) / 2, (lo.y + hi.y) / 2, (hi.x - lo.x) / 4, (hi.y - lo.y) / 4
    top = hi.z + 1.0
    sc = context.scene
    covered = tried = 0
    for i in range(n):
        for j in range(n):
            x, y = cx - ex + 2 * ex * (i + 0.5) / n, cy - ey + 2 * ey * (j + 0.5) / n
            # the LOWEST floor under this point a person could stand on: go down through roofs to the last hit
            z, floor = top, None
            for _ in range(8):
                hit, loc, nrm, _, _, _ = sc.ray_cast(dg, mathutils.Vector((x, y, z)), mathutils.Vector((0, 0, -1)), distance=z - lo.z + 1.0)
                if not hit:
                    break
                if nrm.z > 0.7:
                    floor = loc
                z = loc.z - 0.05
            if floor is None:
                continue
            tried += 1
            hit = sc.ray_cast(dg, floor + mathutils.Vector((0, 0, 1.6)), mathutils.Vector((0, 0, 1)), distance=400.0)[0]
            covered += 1 if hit else 0
    return (covered / tried if tried else 0.0), tried


def guess_sky(context):
    """-> ('game', why): a GAME sky - the planet named most often in the file / object / material / image names (Tatooine
    sunny when none is), and interior exposure when most of the floor is under a roof."""
    # an imported rip always gets a game sky: its World (a downloaded HDRI, a grey colour) was not lit for the game, and
    # matching it made every rip dark and flat (Tantive IV, Hunters, Naboo - 2026-10-23). Blender World stays a choice.
    names =' '.join([os.path.basename(bpy.data.filepath), context.scene.name, context.scene.bf2.name, context.scene.bf2.planet or ''] +
                     [o.name for o in list(context.scene.objects)[:400]] + [m.name for m in list(bpy.data.materials)[:400]] +
                     [i.filepath for i in list(bpy.data.images)[:400]]).lower().replace('\\', '/')
    # the planet named most often wins (LEGO Utapau also has a few "tatooine" props)
    best = max(((sum(names.count(k) for k in words), sky, max(words, key=names.count)) for sky, words in PLANET_WORDS), key=lambda t: t[0])
    pick, word = (best[1], best[2]) if best[0] else ('tatooine_sunny', None)
    cover, tried = _floor_cover(context)
    interior = tried >= 8 and cover >= COVERED_INTERIOR
    return 'game', {'game_sky': pick, 'planet_why': ('the names say "%s"' % word) if word else 'no planet in the names',
                    'exposure_mode': 'interior' if interior else 'outdoor',
                    'cover_why': '%.0f %% of %d floor points under a roof' % (100 * cover, tried)}


CARD_HOLES = 0.02         # fraction of see-through texels that makes an unwired image alpha a cut-out (tree cards)


def wire_image_alpha(mat):
    """OBJ / FBX importers leave a colour image's alpha unwired: tree and grass cards draw as solid squares, in Blender
    AND in game (LEGO Naboo, 2026-10-23). When the Principled BSDF's Alpha is free and its Base Color comes straight from
    an image whose own alpha has holes, that alpha is wired to Alpha. -> True when it was wired"""
    p, stack = export_scene._principled(mat)
    if p is None or stack or 'Alpha' not in p.inputs or p.inputs['Alpha'].is_linked or not p.inputs['Base Color'].is_linked:
        return False
    n = p.inputs['Base Color'].links[0].from_node
    img = n.image if n.type == 'TEX_IMAGE' else None
    if img is None or img.channels < 4 or not img.size[0] or not img.size[1]:
        return False
    try:
        px = np.empty(img.size[0] * img.size[1] * img.channels, np.float32)
        img.pixels.foreach_get(px)
    except Exception:                                       # noqa: BLE001 - an unreadable image is left alone
        return False
    if float((px[3::img.channels] < 0.5).mean()) < CARD_HOLES:
        return False
    mat.node_tree.links.new(n.outputs['Alpha'], p.inputs['Alpha'])
    # a card is seen from both sides: one-sided, its back got no light and drew near black in game (LEGO Naboo trees)
    mat['bf2_card'] = True
    mat.use_backface_culling = False
    return True


def guess_class(mat):
    """-> (class, reason) from the material's own nodes; never applied silently"""
    name = mat.name.lower()
    p, stack = export_scene._principled(mat)             # also inside node groups (game-ripped scenes)
    if p is None:
        return 'opaque', 'no Principled BSDF: treated as a plain surface'
    def sock(*names):
        return next((p.inputs[n] for n in names if n in p.inputs), None)
    em, es, al, tr = sock('Emission Color', 'Emission'), sock('Emission Strength'), sock('Alpha'), sock('Transmission Weight', 'Transmission')
    strength = es.default_value if es is not None and not es.is_linked else 1.0
    if any(k in name for k in ('lava', 'magma')):
        return 'lava', 'the name says lava'
    if tr is not None and (tr.is_linked or tr.default_value > 0.5) or any(k in name for k in ('glass', 'window')):
        return 'glass', 'transmission is on' if tr is not None and (tr.is_linked or tr.default_value > 0.5) else 'the name says glass'
    holes = al is not None and al.is_linked and alpha_has_holes(al, stack)
    if em is not None and strength > 0 and (em.is_linked or max(em.default_value[:3]) > 0.0):
        if holes:
            return 'cutout_emissive', 'emission is wired or coloured AND the wired alpha has holes: a glowing cut-out'
        return 'emissive', 'emission is wired or coloured, strength %.2g' % strength + (
            ' (alpha is wired but never under one half)' if al is not None and al.is_linked else '')
    if al is not None and (holes or (not al.is_linked and al.default_value < 0.999)):
        two = bool(mat.get('bf2_card')) or (not mat.use_backface_culling and any(
            k in name for k in ('leaf', 'leaves', 'foliage', 'grass', 'plant', 'tree', 'bush', 'cloth', 'flag', 'banner')))
        return ('cutout2s' if two else 'cutout'), ('the wired alpha has holes' if holes else 'alpha is under 1') + (
            ' and the material is two-sided foliage / cloth' if two else '')
    return 'opaque', 'no emission, no transmission, no see-through alpha'


class BF2_OT_guess_classes(bpy.types.Operator):
    bl_idname = 'bf2.guess_classes'
    bl_label = 'Guess classes'
    bl_description = 'Set the class of every material from its alpha, emission and node setup. Every guess is listed so you can correct it'

    def execute(self, context):
        st = build.state(context)
        st.results.clear()
        n = 0
        for mat in scene_materials(context):
            cls, why = guess_class(mat)
            changed = mat.bf2.cls != cls
            mat.bf2.cls = cls
            mat.bf2.cls_guessed = True
            r = st.results.add()
            r.code, r.severity = 'class.guess', 'info'
            r.message = '%s -> %s (%s)%s' % (mat.name, dict((k, v) for k, v, _ in props.MATERIAL_CLASSES)[cls], why, '' if changed else ' - unchanged')
            r.materials = mat.name
            n += 1
        self.report({'INFO'}, 'Guessed %d materials: see the list under Check' % n)
        return {'FINISHED'}


class BF2_OT_guess_roles(bpy.types.Operator):
    bl_idname = 'bf2.guess_roles'
    bl_label = 'Guess roles'
    bl_description = ('For a scene that was not made for the tool: mark sky domes, backdrops, exact duplicates and invisible helper meshes '
                      'as "Leave out". Every guess is listed under Check; nothing is deleted')

    def execute(self, context):
        st = build.state(context)
        st.results.clear()
        gs = guess.guess_roles(list(context.view_layer.objects))
        for o, role, why in gs:
            r = st.results.add()
            r.code, r.severity = 'role.guess', 'info'
            r.message = '%s -> Leave out: %s' % (o.name, why)
            r.objects = o.name
        guess.apply(gs)
        self.report({'INFO'}, 'Marked %d object(s) as "Leave out": see the list under Check' % len(gs))
        return {'FINISHED'}


def scene_materials(context):
    seen, out = set(), []
    for o in context.view_layer.objects:
        if o.type == 'MESH' and o.bf2.role in ('static', 'door'):
            for s in o.material_slots:
                if s.material and s.material.name not in seen:
                    seen.add(s.material.name)
                    out.append(s.material)
    return sorted(out, key=lambda m: m.name.lower())


# ------------------------------------------------------------------ gameplay objects
def _empty(context, name, display, size, role, team='neutral'):
    o = bpy.data.objects.new(name, None)
    o.empty_display_type, o.empty_display_size = display, size
    o.location = context.scene.cursor.location
    context.collection.objects.link(o)
    o.bf2.role, o.bf2.team = role, team
    for x in context.view_layer.objects:
        x.select_set(False)
    o.select_set(True)
    context.view_layer.objects.active = o
    return o


def _box(context, name, size, role):
    import bmesh
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co.x *= size[0]; v.co.y *= size[1]; v.co.z *= size[2]
    bm.to_mesh(me); bm.free()
    o = bpy.data.objects.new(name, me)
    o.location = context.scene.cursor.location
    o.display_type = 'WIRE'
    o.hide_render = True
    context.collection.objects.link(o)
    o.bf2.role = role
    for x in context.view_layer.objects:
        x.select_set(False)
    o.select_set(True)
    context.view_layer.objects.active = o
    return o


class BF2_OT_add_marker(bpy.types.Operator):
    bl_idname = 'bf2.add_marker'
    bl_label = 'Add BF2 object'
    bl_options = {'REGISTER', 'UNDO'}
    kind: EnumProperty(items=[('spawn1', 'Team 1 spawn', ''), ('spawn2', 'Team 2 spawn', ''), ('spawn0', 'Neutral spawn', ''),
                              ('hero1', 'Hero spawn, team 1', ''), ('hero2', 'Hero spawn, team 2', ''), ('cp', 'Command post', ''),
                              ('kill', 'Kill volume', ''), ('nowalk', 'Not walkable', ''), ('combat', 'Combat area', ''),
                              ('probe', 'Reflection probe', ''), ('figure', '1.8 m reference figure', ''),
                              # last so the numbering of the others stays
                              ('wall', 'Invisible wall', 'A box that is solid but not drawn (keeps players in, blocks shots)')])

    def execute(self, context):
        k = self.kind
        if k.startswith('spawn') or k.startswith('hero'):
            team = {'1': 'team1', '2': 'team2', '0': 'neutral'}[k[-1]]
            o = _empty(context, ('Hero spawn ' if k.startswith('hero') else 'Spawn ') + team, 'SINGLE_ARROW', 1.8,
                       'hero_spawn' if k.startswith('hero') else 'spawn', team)
            o.rotation_euler = (math.radians(90.0), 0.0, 0.0)      # the arrow lies flat and points where the soldier looks (-Y)
        elif k == 'cp':
            n = sum(1 for x in context.scene.objects if x.bf2.role == 'command_post')
            o = _empty(context, 'Command post %s' % 'ABCDEFGH'[min(n, 7)], 'CIRCLE', 9.0, 'command_post')
            o.rotation_euler = (math.radians(90.0), 0.0, 0.0)
            o.bf2.cp_letter, o.bf2.cp_order = 'ABCDEFGH'[min(n, 7)], n
        elif k == 'kill':
            _box(context, 'Kill volume', (20.0, 20.0, 6.0), 'kill_volume')
        elif k == 'nowalk':
            _box(context, 'Not walkable', (6.0, 6.0, 4.0), 'not_walkable')
        elif k == 'combat':
            o = _box(context, 'Combat area', (200.0, 200.0, 0.0), 'combat_area')
        elif k == 'probe':
            o = _empty(context, 'Reflection probe', 'SPHERE', 10.0, 'reflection_probe')      # the sphere = the room it serves
            o.location.z += 1.6
        elif k == 'figure':
            o = _box(context, 'Reference 1.8 m', (0.5, 0.3, 1.8), 'reference')
            o.location.z += 0.9
            o.display_type = 'SOLID'
        elif k == 'wall':
            # a standalone "Collision mesh": its triangles become collision of the nearest pieces, it is never drawn
            o = _box(context, 'Invisible wall', (10.0, 0.5, 6.0), 'collision')
            o.location.z += 3.0
        return {'FINISHED'}


class BF2_OT_mark_selected(bpy.types.Operator):
    bl_idname = 'bf2.mark_selected'
    bl_label = 'Set role of selected'
    bl_options = {'REGISTER', 'UNDO'}
    role: EnumProperty(items=props.ROLES)

    def execute(self, context):
        n = 0
        for o in context.selected_objects:
            o.bf2.role = self.role
            n += 1
        self.report({'INFO'}, '%d object(s) set to "%s"' % (n, dict((k, v) for k, v, _ in props.ROLES)[self.role]))
        return {'FINISHED'}


class BF2_OT_set_collision(bpy.types.Operator):
    bl_idname = 'bf2.set_collision'
    bl_label = 'Set collision of selected'
    bl_options = {'REGISTER', 'UNDO'}
    mode: EnumProperty(items=props.COLLISION)

    def execute(self, context):
        for o in context.selected_objects:
            if o.type == 'MESH':
                o.bf2.collision = self.mode
        return {'FINISHED'}


def guess_units(context):
    """-> (factor to metres or None, why). A metre scene's objects are mostly 0.2 - 20 m across; a scene modelled in
    centimetres (game rips, Unreal / Source exports without "apply unit") has them 100 times bigger. Only a clear
    case is answered - a scene that is just big (a terrain) is not "in centimetres"."""
    import numpy as np
    from mathutils import Vector
    dims = []
    lo, hi = np.full(3, 1e18), np.full(3, -1e18)
    for o in context.view_layer.objects:
        if o.type != 'MESH' or o.bf2.role not in ('static', 'door'):
            continue
        d = np.array(o.dimensions)
        if d.max() <= 0:
            continue
        dims.append(float(d.max()))
        ws = np.array([o.matrix_world @ Vector(b) for b in o.bound_box])
        lo, hi = np.minimum(lo, ws.min(0)), np.maximum(hi, ws.max(0))
    if len(dims) < 5:
        return None, 'too few objects to tell'
    med = float(np.median(dims))
    ext = float((hi - lo)[:2].max())
    sl = context.scene.unit_settings.scale_length
    if abs(sl - 1.0) > 1e-6:
        return None, 'the scene unit scale is %g: Blender already shows metres' % sl
    if med > 40.0 and ext > 2000.0:
        return 0.01, 'half of the objects are over %.0f units across and the scene is %.0f units wide: centimetres' % (med, ext)
    if med < 0.05 and ext < 20.0:
        return 100.0, 'half of the objects are under %.3f units across: the scene looks 100 times too small' % med
    return None, 'object sizes look like metres (median %.1f m, scene %.0f m wide)' % (med, ext)


PIECE_LIMIT = 1200              # the most pieces a level holds on the Kamino host (split.PIECE_BUDGET)
TEX_PER_CLASS = {'opaque': 2, 'cutout': 2, 'cutout2s': 2, 'emissive': 3, 'holo': 1, 'glass': 0, 'lava': 0}


def estimate_budget(context):
    """what a build of this scene will roughly make, read in seconds without exporting: source triangles, pieces (per
    material: its triangles / Triangles per piece, at least one per 'Piece size' cell it touches), packed texture
    memory (BC-compressed, with mips), real-time lamps. -> dict"""
    import numpy as np
    s = context.scene.bf2
    dg = context.evaluated_depsgraph_get()
    tris, cells, mats = {}, {}, {}
    ext = float(s.piece_ext)
    far_k = min(max(float(s.piece_far), 0.5), 2.0)
    MAX_EXTENT = 3500.0                          # the host's world box (runtime hosts/kamino_01.py)
    parts = []                                   # (material, triangle centres) - pieces are counted once the play area is known

    def add(ev, mw, slots_from):
        me = ev.data if ev.type == 'MESH' else None
        if me is None:
            return
        me.calc_loop_triangles()
        n = len(me.loop_triangles)
        if not n:
            return
        mi = np.empty(n, np.int32); me.loop_triangles.foreach_get('material_index', mi)
        vi = np.empty(n * 3, np.int32); me.loop_triangles.foreach_get('vertices', vi)
        co = np.empty(len(me.vertices) * 3, np.float32); me.vertices.foreach_get('co', co)
        cen = co.reshape(-1, 3)[vi].reshape(n, 3, 3).mean(1)
        M = np.array(mw, np.float32)
        cen = cen @ M[:3, :3].T + M[:3, 3]
        slots = [x.material for x in slots_from.material_slots] or [None]
        for i in np.unique(mi):
            m = slots[min(int(i), len(slots) - 1)]
            if m is not None and m.bf2.skip:
                continue
            key = m.name if m else ''
            parts.append((key, cen[mi == i]))
            mats[key] = m
    for o in context.view_layer.objects:
        if o.type == 'MESH' and o.bf2.role in ('static', 'door') and not o.hide_render:
            add(o.evaluated_get(dg), o.matrix_world, o)
    for inst in dg.object_instances:
        if inst.is_instance and inst.parent is not None and inst.object.type == 'MESH':
            src = inst.object.original
            if inst.parent.original.bf2.role == 'static' and src.bf2.role == 'static' and not src.hide_render:
                add(inst.object, inst.matrix_world, inst.object)
    # the play area as the export measures it: the spawns and posts (+40 m), else the middle of the geometry
    marks = [o.matrix_world.translation for o in context.view_layer.objects if o.bf2.role in ('spawn', 'hero_spawn', 'command_post')]
    allc = np.concatenate([c for _, c in parts]) if parts else np.zeros((1, 3))
    if marks:
        P = np.array([list(v) for v in marks]); centre = (P.min(0) + P.max(0)) / 2
        radius = float(np.linalg.norm(P - centre, axis=1).max()) + 40.0
    else:
        centre = np.median(allc, 0); radius = float(np.percentile(np.linalg.norm((allc - centre)[:, :2], axis=1), 90))
    radius = float(s.play_radius) or radius
    reach = min(radius + float(s.keep_distance), MAX_EXTENT) if s.keep_distance > 0 else MAX_EXTENT
    for key, c in parts:
        d = np.linalg.norm((c - centre)[:, :2], axis=1)
        c, d = c[d <= reach], d[d <= reach]            # cropped by "Keep geometry within" / the host's world box
        if not len(c):
            continue
        tris[key] = tris.get(key, 0) + len(c)
        # far from the play area a piece may be as large as (distance beyond the play radius) x Far piece growth
        size = np.maximum(ext, np.maximum(0.0, d - radius) * far_k)
        lvl = np.ceil(np.log2(size / ext)).astype(np.int64)
        cs = ext * (2.0 ** lvl)
        q = np.floor(c / cs[:, None]).astype(np.int64)
        cells.setdefault(key, set()).update(zip(lvl.tolist(), q[:, 0].tolist(), q[:, 1].tolist(), q[:, 2].tolist()))
    pieces = sum(max(len(cells[k]), -(-tris[k] // int(s.piece_tris))) for k in tris)
    tex = 0.0
    cap = int(s.bake_max_size)
    for k, m in mats.items():
        side = 64
        if m is not None and m.use_nodes and m.node_tree:
            for n in m.node_tree.nodes:
                if n.type == 'TEX_IMAGE' and n.image is not None:
                    side = max(side, min(cap, 1 << int(max(n.image.size[0], n.image.size[1], 1) - 1).bit_length()))
        cls = m.bf2.cls if m is not None else 'opaque'
        tex += TEX_PER_CLASS.get(cls, 2) * side * side * 1.33
    rt = sum(1 for o in context.view_layer.objects if o.type == 'LIGHT' and o.data.type != 'SUN' and not o.hide_render
             and o.data.bf2.mode in ('dynamic', 'both'))
    return {'triangles': sum(tris.values()), 'materials': len(tris), 'pieces': pieces, 'piece_limit': PIECE_LIMIT,
            'texture_mb': round(tex / 1e6, 1), 'realtime_lights': rt, 'light_budget': s.dynamic_light_budget}


class BF2_OT_estimate(bpy.types.Operator):
    bl_idname = 'bf2.estimate'
    bl_label = 'Estimate size'
    bl_description = ('How big a build of this scene will be - triangles, game pieces against the level\'s limit, texture memory, '
                      'real-time lamps - in seconds, before building')

    def execute(self, context):
        e = estimate_budget(context)
        wm = context.window_manager
        wm.bf2_estimate = json.dumps(e)
        lvl = 'WARNING' if e['pieces'] > e['piece_limit'] else 'INFO'
        self.report({lvl}, '%d triangles, about %d pieces (limit %d), about %.0f MB of textures, %d real-time lamps (budget %d)' % (
            e['triangles'], e['pieces'], e['piece_limit'], e['texture_mb'], e['realtime_lights'], e['light_budget']))
        return {'FINISHED'}


def guess_backdrop_materials(context):
    """materials that are only far away: a sky dome / box or a backdrop inside ONE big ripped mesh, which the per-object
    role guess cannot take apart (LEGO Invasion of Naboo OBJ, 2026-10-23: the whole level is one object; its sky
    material starts 350 m out while half the map's faces are within 40 m). -> [(material, why)]"""
    import numpy as np
    per_mat, allc = {}, []
    dg = context.evaluated_depsgraph_get()
    for o in context.view_layer.objects:
        if o.type != 'MESH' or o.bf2.role != 'static' or o.hide_render:
            continue
        me = o.evaluated_get(dg).data
        n = len(me.polygons)
        if not n:
            continue
        c = np.empty(n * 3, np.float32); me.polygons.foreach_get('center', c)
        M = np.array(o.matrix_world, np.float32)
        c = c.reshape(-1, 3) @ M[:3, :3].T + M[:3, 3]
        mi = np.empty(n, np.int32); me.polygons.foreach_get('material_index', mi)
        allc.append(c)
        for i, s in enumerate(o.material_slots):
            if s.material is not None:
                sel = c[mi == i]
                if len(sel):
                    per_mat.setdefault(s.material.name, []).append(sel)
    if not allc or len(per_mat) < 2:
        return []
    C = np.concatenate(allc)
    centre = np.median(C, 0)
    r50 = float(np.median(np.linalg.norm(C - centre, axis=1)))
    out = []
    for name, parts in per_mat.items():
        d = np.linalg.norm(np.concatenate(parts) - centre, axis=1)
        if float(d.min()) > max(4.0 * r50, r50 + 150.0):
            out.append((bpy.data.materials[name], 'all its faces are %.0f m or more from the middle; half of the map is within %.0f m' % (d.min(), r50)))
    return out


class BF2_OT_prepare_import(bpy.types.Operator):
    bl_idname = 'bf2.prepare_import'
    bl_label = 'Prepare imported map'
    bl_description = ('For a scene that was not made for the tool (FBX / OBJ / glTF rips, other games): fix the scale when it is clearly '
                      'not metres, Guess roles, Guess classes, Fit lights to budget, Blender World sky, Auto spawns. Everything it did '
                      'is listed under Check; Ctrl+Z undoes it')
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        st = build.state(context)
        st.results.clear()

        def note(code, msg, objects=(), materials=()):
            r = st.results.add()
            r.code, r.severity, r.message, r.objects = code, 'info', msg, '\n'.join(objects)
            r.materials = '\n'.join(materials)
        f, why = guess_units(context)
        if f:
            n = scale_scene(context, f)
            note('prep.scale', 'Scaled the scene by %g to metres (%d top objects): %s.' % (f, n, why))
        else:
            note('prep.scale', 'Scale left alone: %s.' % why)
        gs = guess.guess_roles(list(context.view_layer.objects))
        guess.apply(gs)
        note('prep.roles', '%d object(s) set to "Leave out" (sky domes, backdrops, duplicates, helpers).' % len(gs), [o.name for o, _, _ in gs])
        bd = guess_backdrop_materials(context)
        for mat, why in bd:
            mat.bf2.skip = True
        if bd:
            r = st.results.add()
            r.code, r.severity, r.materials = 'prep.backdrop', 'info', '\n'.join(m.name for m, _ in bd)
            r.message = '%d far-away material(s) set to "Leave out" (a sky / backdrop inside a mesh): %s' % (
                len(bd), '; '.join('%s - %s' % (m.name, why) for m, why in bd[:3]))
        cards = [mat for mat in scene_materials(context) if wire_image_alpha(mat)]
        if cards:
            note('prep.alpha', '%d material(s): the colour image\'s own alpha (tree / grass cards) wired to Alpha, so its '
                 'see-through parts are holes in Blender and in game.' % len(cards), materials=[m.name for m in cards])
        n_cls = {}
        for mat in scene_materials(context):
            cls, _ = guess_class(mat)
            mat.bf2.cls, mat.bf2.cls_guessed = cls, True
            n_cls[cls] = n_cls.get(cls, 0) + 1
        note('prep.classes', 'Material classes guessed: %s.' % ', '.join('%d %s' % (v, k) for k, v in sorted(n_cls.items())))
        n, still = fit_lights(context, context.scene.bf2.dynamic_light_budget)
        note('prep.lights', '%d lamp(s) made Baked, %d real-time (budget %d).' % (n, still, context.scene.bf2.dynamic_light_budget))
        s = context.scene.bf2
        s.auto_spawns = True
        sky, why = guess_sky(context)
        s.sky = sky
        if sky == 'game':
            s.game_sky, s.game_sky_asset = why['game_sky'], ''
            s.exposure_mode = why['exposure_mode']
            note('prep.scene', 'Auto spawns on; Sky = game sky "%s" (%s), %s exposure (%s). Light bake on Build: on.' % (
                dict((k, n) for k, n, _ in props.GAME_SKIES).get(s.game_sky, s.game_sky), why['planet_why'], s.exposure_mode,
                why['cover_why']))
        else:
            note('prep.scene', 'Auto spawns on; Sky = Blender World (%s). Light bake on Build: on.' % why['why'])
        s.bake_on_build = True
        context.window_manager.bf2_tab = 'check'
        self.report({'INFO'}, 'Prepared: see the Check tab, then press Check scene')
        return {'FINISHED'}


class BF2_OT_import_map(bpy.types.Operator):
    bl_idname = 'bf2.import_map'
    bl_label = 'BF2 Map (FBX / OBJ / glTF, prepared for the tool)'
    bl_description = 'Import a map file with Blender\'s own importer, then run Prepare imported map on it'
    bl_options = {'REGISTER', 'UNDO'}
    filepath: StringProperty(subtype='FILE_PATH')
    filter_glob: StringProperty(default='*.fbx;*.obj;*.glb;*.gltf', options={'HIDDEN'})

    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        ext = os.path.splitext(self.filepath)[1].lower()
        before = set(o.name for o in bpy.data.objects)
        try:
            if ext == '.fbx':
                bpy.ops.import_scene.fbx(filepath=self.filepath)
            elif ext == '.obj':
                bpy.ops.wm.obj_import(filepath=self.filepath)
            elif ext in ('.glb', '.gltf'):
                bpy.ops.import_scene.gltf(filepath=self.filepath)
            else:
                self.report({'ERROR'}, 'Not a map file this can import: %s' % ext)
                return {'CANCELLED'}
        except (RuntimeError, AttributeError) as e:
            self.report({'ERROR'}, 'The import failed: %s' % e)
            return {'CANCELLED'}
        new = [o for o in bpy.data.objects if o.name not in before]
        if context.scene.bf2.name in ('', 'MyMap'):
            base = ''.join(ch for ch in os.path.splitext(os.path.basename(self.filepath))[0] if ch.isalnum())
            if base[:1].isalpha():
                context.scene.bf2.name = base[:24] if len(base) >= 2 else 'MyMap'
        bpy.ops.bf2.prepare_import()
        self.report({'INFO'}, 'Imported %d object(s) and prepared the scene: see the Check tab' % len(new))
        return {'FINISHED'}


def import_menu(self, context):
    self.layout.operator('bf2.import_map', text='BF2 Map (FBX / OBJ / glTF, prepared)')


REPORT_TAIL = 4 * 1024 * 1024          # bytes kept from the end of each log in a bug report


def make_bug_report(context):
    """one zip with what is needed to look into a problem: the build logs (their last 4 MB each), the scene export's
    map.json and log, settings.json, the last setup check and check results, the Blender / add-on / Windows versions.
    No scene, no textures, no meshes. -> path of the zip"""
    import platform
    import time
    import zipfile
    p = P.prefs(context)
    wf = build.work_folder(context)
    base = wf or (bpy.path.abspath(p.work_dir) if p.work_dir else '')
    if not base:
        raise ValueError('Set the work folder first.')
    os.makedirs(base, exist_ok=True)
    path = os.path.join(base, 'bug_report_%s.zip' % time.strftime('%Y%m%d_%H%M%S')).replace('\\', '/')
    st = build.state(context)
    from . import bl_info as _bi                                     # noqa: F401  (the version of this add-on)
    info = {'addon_version': '.'.join(str(x) for x in _bi['version']), 'blender': bpy.app.version_string, 'os': platform.platform(),
            'python_for_build': P.python_exe(p), 'pylib': P.pylib_dir(p), 'level': context.scene.bf2.name,
            'setup': [(l.item, l.ok, l.message, l.fix) for l in p.setup],
            'last_run': {'kind': st.kind, 'ok': st.ok, 'message': st.message, 'fbmod': st.fbmod,
                         'stages': [(s.name, s.status, s.seconds, s.summary) for s in st.stages]},
            'results': [(r.severity, r.code, r.message, r.fix) for r in st.results]}
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('report.json', json.dumps(info, indent=1, default=str))
        sp = os.path.join(bpy.path.abspath(p.work_dir), 'settings.json') if p.work_dir else ''
        if sp and os.path.isfile(sp):
            z.write(sp, 'settings.json')
        if wf:
            for sub in ('logs', 'export'):
                d = os.path.join(wf, sub)
                if not os.path.isdir(d):
                    continue
                for f in sorted(os.listdir(d)):
                    full = os.path.join(d, f)
                    if not os.path.isfile(full) or not f.endswith(('.log', '.json', '.txt')):
                        continue
                    size = os.path.getsize(full)
                    with open(full, 'rb') as fh:
                        if size > REPORT_TAIL:
                            fh.seek(size - REPORT_TAIL)
                        z.writestr('%s/%s' % (sub, f), fh.read())
            for f in ('import.json', 'look.json', 'state.json'):
                full = os.path.join(wf, f)
                if os.path.isfile(full) and os.path.getsize(full) < 50 * 1024 * 1024:
                    z.write(full, f)
    return path


class BF2_OT_bug_report(bpy.types.Operator):
    bl_idname = 'bf2.bug_report'
    bl_label = 'Make bug report'
    bl_description = ('Zip the build logs, the scene export\'s description (no meshes or textures), the settings and the last results '
                      'into the work folder, to send to whoever helps you')

    def execute(self, context):
        try:
            path = make_bug_report(context)
        except (OSError, ValueError) as e:
            self.report({'ERROR'}, 'No report: %s' % e)
            return {'CANCELLED'}
        self.report({'INFO'}, 'Bug report written: %s' % path)
        if os.name == 'nt' and not bpy.app.background:
            subprocess.Popen(['explorer', '/select,', path.replace('/', '\\')])
        return {'FINISHED'}


def fit_lights(context, budget):
    """keep the `budget` strongest real-time lamps real-time, make the others Baked; -> (made baked, still real-time)"""
    lamps = [o for o in context.view_layer.objects if o.type == 'LIGHT' and o.data.type != 'SUN' and not o.hide_render
             and o.data.bf2.mode in ('dynamic', 'both')]
    # the lamps that matter most in game: the most power (a 1000 W lamp in the middle of a room before a 5 W fill light)
    lamps.sort(key=lambda o: float(o.data.energy) * max(o.data.color), reverse=True)
    demote = lamps[budget:]
    done = set()
    for o in demote:
        if o.data.name not in done:              # lamps that share one light datablock change together
            o.data.bf2.mode = 'baked'
            done.add(o.data.name)
    still = sum(1 for o in context.view_layer.objects if o.type == 'LIGHT' and o.data.type != 'SUN' and not o.hide_render
                and o.data.bf2.mode in ('dynamic', 'both'))
    return len(demote), still


class BF2_OT_fit_light_budget(bpy.types.Operator):
    bl_idname = 'bf2.fit_light_budget'
    bl_label = 'Fit lights to budget'
    bl_description = ('Keep the strongest lamps real-time (up to the Dynamic light budget) and make the rest Baked: they then light the '
                      'map through the light bake. For ripped maps that arrive with hundreds of lamps')
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        budget = context.scene.bf2.dynamic_light_budget
        n, still = fit_lights(context, budget)
        self.report({'INFO'}, '%d lamp(s) set to Baked; %d real-time (budget %d). Press Bake lighting so the Baked ones light the map.'
                    % (n, still, budget))
        return {'FINISHED'}


# ------------------------------------------------------------------ settings to / from a file (another PC, a friend)
SHARED_PREFS = P.PATH_KEYS + ('tools_dir', 'work_dir', 'python', 'blender_exe', 'author')


class BF2_OT_export_settings(bpy.types.Operator):
    bl_idname = 'bf2.export_settings'
    bl_label = 'Save settings to a file'
    bl_description = 'Write the add-on\'s paths and author name to a .json file, to load them on another PC or in another Blender'
    filepath: StringProperty(subtype='FILE_PATH', default='bf2maptool_settings.json')
    filter_glob: StringProperty(default='*.json', options={'HIDDEN'})

    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        p = P.prefs(context)
        data = {'bf2maptool_settings': 1, 'prefs': {k: getattr(p, k) for k in SHARED_PREFS}}
        with open(bpy.path.abspath(self.filepath), 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=1)
        self.report({'INFO'}, 'Settings saved to %s' % self.filepath)
        return {'FINISHED'}


class BF2_OT_import_settings(bpy.types.Operator):
    bl_idname = 'bf2.import_settings'
    bl_label = 'Load settings from a file'
    bl_description = ('Read paths saved with "Save settings to a file". A path that does not exist on this PC is not taken: '
                      'the list below says which ones to set (or press Find everything)')
    filepath: StringProperty(subtype='FILE_PATH')
    filter_glob: StringProperty(default='*.json', options={'HIDDEN'})

    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        try:
            with open(bpy.path.abspath(self.filepath), encoding='utf-8') as f:
                data = json.load(f)
            vals = data['prefs']
        except (OSError, ValueError, KeyError, TypeError) as e:
            self.report({'ERROR'}, 'Not a settings file of this add-on: %s' % e)
            return {'CANCELLED'}
        p = P.prefs(context)
        p.setup.clear()
        taken, skipped = 0, 0
        for k in SHARED_PREFS:
            v = vals.get(k)
            if not isinstance(v, str) or not v:
                continue
            is_path = k != 'author'
            if is_path and not os.path.exists(bpy.path.abspath(v)) and k != 'work_dir':
                r = p.setup.add()
                r.item, r.ok, r.message = P.BF2Preferences.bl_rna.properties[k].name, False, 'not on this PC: %s' % v
                r.fix = 'Set it by hand or press Find everything.'
                skipped += 1
                continue
            setattr(p, k, v)
            taken += 1
        self.report({'INFO'} if not skipped else {'WARNING'}, '%d setting(s) loaded, %d not on this PC' % (taken, skipped))
        return {'FINISHED'}


# ------------------------------------------------------------------ fix buttons on Check results
class BF2_OT_confirm_classes(bpy.types.Operator):
    bl_idname = 'bf2.confirm_classes'
    bl_label = 'Confirm guessed classes'
    bl_description = 'Keep every guessed material class as it is and mark it as checked (the Materials tab shows each one)'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        n = 0
        for m in scene_materials(context):
            if m.bf2.cls_guessed:
                m.bf2.cls_guessed = False
                n += 1
        self.report({'INFO'}, '%d guessed class(es) confirmed' % n)
        return {'FINISHED'}


class BF2_OT_letter_posts(bpy.types.Operator):
    bl_idname = 'bf2.letter_posts'
    bl_label = 'Letter the command posts A to E'
    bl_description = ('Give the five command posts the letters A to E along the line between the two teams\' spawns (A, B on team 1\'s '
                      'side, C in the middle, D, E on team 2\'s); without team spawns, along the longer side of the posts')
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        import numpy as np
        cps = [o for o in context.view_layer.objects if o.bf2.role == 'command_post']
        if len(cps) != 5:
            self.report({'ERROR'}, 'Supremacy needs exactly 5 command posts; there are %d' % len(cps))
            return {'CANCELLED'}
        P = np.array([list(o.matrix_world.translation)[:2] for o in cps])
        t1 = [o.matrix_world.translation for o in context.view_layer.objects if o.bf2.role == 'spawn' and o.bf2.team == 'team1']
        t2 = [o.matrix_world.translation for o in context.view_layer.objects if o.bf2.role == 'spawn' and o.bf2.team == 'team2']
        if t1 and t2:
            axis = np.mean([list(v)[:2] for v in t2], 0) - np.mean([list(v)[:2] for v in t1], 0)
        else:
            ext = P.max(0) - P.min(0)
            axis = np.array([1.0, 0.0]) if ext[0] >= ext[1] else np.array([0.0, 1.0])
        order = np.argsort(P @ axis)
        for k, i in enumerate(order):
            cps[i].bf2.cp_letter, cps[i].bf2.cp_order = 'ABCDE'[k], k
            cps[i].name = 'Command post %s' % 'ABCDE'[k]
        self.report({'INFO'}, 'Command posts lettered A to E')
        return {'FINISHED'}


class BF2_OT_rename_level(bpy.types.Operator):
    bl_idname = 'bf2.rename_level'
    bl_label = 'Pick a free level name'
    bl_description = 'The level name is taken by a level the game ships: add "Map" to it'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        s = context.scene.bf2
        s.name = (s.name or 'My')[:20] + 'Map'
        self.report({'INFO'}, 'Level name is now %s (press Check scene again)' % s.name)
        return {'FINISHED'}


# Check result code -> the operator that fixes it safely (drawn as a "Fix" button on that line)
FIXES = {'light.dynamic_budget': 'bf2.fit_light_budget', 'class.guessed': 'bf2.confirm_classes', 'supremacy.letters': 'bf2.letter_posts',
         'map.name_clash': 'bf2.rename_level'}


class BF2_MT_add(bpy.types.Menu):
    bl_idname = 'BF2_MT_add'
    bl_label = 'BF2 Map'

    def draw(self, context):
        for e in BF2_OT_add_marker.bl_rna.properties['kind'].enum_items:
            self.layout.operator('bf2.add_marker', text=e.name).kind = e.identifier


def add_menu(self, context):
    self.layout.menu('BF2_MT_add', icon='WORLD')


CLASSES = (BF2_OT_check_setup, BF2_OT_find_paths, BF2_OT_install_packages, BF2_OT_port, BF2_OT_check, BF2_OT_bake, BF2_OT_rebuild_mod, BF2_OT_cancel, BF2_OT_open_log, BF2_OT_open_folder,
           BF2_OT_select_result, BF2_OT_guess_classes, BF2_OT_guess_roles, BF2_OT_add_marker, BF2_OT_mark_selected, BF2_OT_set_collision,
           BF2_OT_fit_light_budget, BF2_OT_scale_map, BF2_OT_bug_report, BF2_OT_prepare_import, BF2_OT_import_map, BF2_OT_estimate,
           BF2_OT_confirm_classes, BF2_OT_letter_posts, BF2_OT_rename_level, BF2_OT_export_settings, BF2_OT_import_settings, BF2_MT_add)
