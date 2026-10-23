"""Prepare a LEGO Star Wars: The Skywalker Saga level export (LSWTSS_GSC exporter .blend) for the BF2 Map Tool.

The exporter stores each source material's shader graph (`lswtss_shader_graph`) and render state (`lswtss_render_state`)
as custom properties. From those this script sets, on a COPY of the scene:
  * material classes: additive / unlit animated materials (Panner -> Time) -> Hologram with the source scroll speed and
    tiling of their main texture; other additive / alpha-blended glows -> Hologram (static); alpha-test foliage ->
    cut-out; transparent windows -> glass; sky, sun sprites, shadow impostors -> left out;
  * object roles: the tool's own role guess (sky domes, duplicates, helpers) + objects whose every material is left out;
    no collision for scenery far from the play area;
  * team spawns on the walkable platform (ray casts), which also fix the play centre; scene settings.
Every decision is written to <out>.prep.json.

  blender -b --factory-startup <in.blend> --python prepare_lswtss.py -- <addon dir> <out.blend> <settings.json>
settings.json: {"name", "display_name", "planet", "description", "era", "play_radius", "keep_distance", "bake_quality",
                "spawns": {"team1": [[x, y], ...], "team2": [[x, y], ...]}, "spawn_z": [lo, hi], "far_no_collision": m,
                "overrides": {material name fragment: {"cls": ..., "speed": [u, v], "brightness": b, "skip": bool}}}
"""
import json
import math
import os
import re
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
ADDON, OUT, CFG = argv[0], argv[1], argv[2]
sys.path.insert(0, os.path.dirname(ADDON))
pkg = os.path.basename(ADDON)
props = __import__(pkg + '.props', fromlist=['props'])
guess = __import__(pkg + '.guess', fromlist=['guess'])
if not hasattr(bpy.types.Scene, 'bf2'):
    props.register()
cfg = json.load(open(CFG, encoding='utf-8-sig'))
report = {'materials': {}, 'roles': {}, 'spawns': [], 'notes': []}

# words in a texture path that mark a helper layer (scan lines, gradients, noise) rather than the picture itself
HELPER_TEX = ('scanline', 'gradient', 'noise', 'mask', 'fade', 'smoke', 'cloud_gradient')
SKIP_MATS = ('skydome', 'skybase', 'sunflare', 'lw_sun_', 'shadowimpostor', 'hyperspace', '_ttshadermaterial', 'kyberbrick', 'speedcone',
             # soft additive light shafts: their texture is a two-channel mask; the game draws them as solid magenta walls
             'lightbeam', 'lightshaft', 'light_shaft', 'godray', 'lightray')


def graph(m):
    try:
        return json.loads(m.get('lswtss_shader_graph') or '{}')
    except (TypeError, ValueError):
        return {}


def state(m):
    try:
        return json.loads(m.get('lswtss_render_state') or '{}')
    except (TypeError, ValueError):
        return {}


def fnum(s, default=0.0):
    try:
        return float(str(s).strip())
    except ValueError:
        return default


def upstream(g, node, want):
    """first node of a type in `want` feeding `node` (breadth first over the graph links)"""
    into = {}
    for l in g.get('links', []):
        into.setdefault(l['nodeTo'], []).append(l['nodeFrom'])
    types = {n['name']: n for n in g.get('nodes', [])}
    seen, todo = set(), [node]
    while todo:
        cur = todo.pop(0)
        for src in into.get(cur, []):
            if src in seen:
                continue
            seen.add(src)
            if types.get(src, {}).get('type') in want:
                return types[src]
            todo.append(src)
    return None


def main_texture(g):
    """(texture path, panner node or None, mesh-uv node or None) of the picture a material shows: the sampled texture
    feeding Emissive (else Albedo) that is not a helper layer"""
    nodes = {n['name']: n for n in g.get('nodes', [])}
    into = {}
    for l in g.get('links', []):
        into.setdefault(l['nodeTo'], []).append(l)
    for socket in ('Emissive', 'Albedo'):
        start = [l['nodeFrom'] for l in g.get('links', []) if l['nodeTo'] == 'StandardMaterial' and l['socketTo'] == socket]
        seen, todo, found = set(), list(start), []
        while todo:
            cur = todo.pop(0)
            if cur in seen:
                continue
            seen.add(cur)
            n = nodes.get(cur, {})
            if n.get('type') == 'sampleTexture2D':
                found.append(n)
            for l in into.get(cur, []):
                if l['socketTo'] != 'UV' or nodes.get(cur, {}).get('type') != 'sampleTexture2D':
                    todo.append(l['nodeFrom'])
        pics = [n for n in found if not any(w in n['values'].get('tex', '').lower() for w in HELPER_TEX)]
        if pics:
            t = pics[0]
            uvsrc = [l['nodeFrom'] for l in into.get(t['name'], []) if l['socketTo'] == 'UV']
            pan = nodes.get(uvsrc[0]) if uvsrc else None
            if pan is not None and pan.get('type') != 'Panner':
                pan = upstream(g, uvsrc[0], ('Panner',)) if pan.get('type') not in ('Mesh UV',) else None
            mesh_uv = None
            if uvsrc:
                n0 = nodes.get(uvsrc[0], {})
                mesh_uv = n0 if n0.get('type') == 'Mesh UV' else upstream(g, uvsrc[0], ('Mesh UV',))
            return t['values'].get('tex', ''), pan if (pan and pan.get('type') == 'Panner') else None, mesh_uv
    return None, None, None


def time_scale(g, panner):
    """the factor between scene time and a Panner's Time input (Time -> mul(constant) -> Panner)"""
    nodes = {n['name']: n for n in g.get('nodes', [])}
    links = g.get('links', [])
    src = next((l['nodeFrom'] for l in links if l['nodeTo'] == panner and l['socketTo'] == 'Time'), None)
    k = 1.0
    for _ in range(4):
        n = nodes.get(src or '', {})
        if n.get('type') != 'mul':
            break
        ins = [l['nodeFrom'] for l in links if l['nodeTo'] == src]
        consts = [nodes[i] for i in ins if nodes.get(i, {}).get('type') == 'constant1']
        rest = [i for i in ins if nodes.get(i, {}).get('type') != 'constant1']
        for c in consts:
            k *= fnum(c['values'].get('A', 1), 1.0)
        src = rest[0] if rest else None
    return k


def blender_image(m, tex_path):
    """the Blender image of a source texture path, looked up among the material's own image nodes"""
    stem = os.path.splitext(os.path.basename(tex_path))[0].lower().replace('#', '_')
    stem2 = stem.split('_')[0] if False else stem
    best = None
    for tree in [m.node_tree] + [n.node_tree for n in m.node_tree.nodes if n.type == 'GROUP' and n.node_tree]:
        for n in tree.nodes:
            if n.type == 'TEX_IMAGE' and n.image is not None:
                nm = (n.image.name + ' ' + (n.image.filepath or '')).lower().replace('#', '_')
                if stem2 in nm:
                    return n.image
                if best is None and os.path.basename(tex_path).lower().split('#')[0].replace('.texture', '') in nm:
                    best = n.image
    return best


def classify(m):
    name = m.name.lower()
    st, g = state(m), graph(m)
    types = {n['type'] for n in g.get('nodes', [])}
    emissive_link = any(l['nodeTo'] == 'StandardMaterial' and l['socketTo'] == 'Emissive' for l in g.get('links', []))
    for frag, ov in (cfg.get('overrides') or {}).items():
        if frag.lower() in name:
            return dict(ov, why='override "%s"' % frag)
    if any(w in name for w in SKIP_MATS):
        return {'skip': True, 'why': 'sky / sun sprite / shadow impostor (the level has its own sky and shadows)'}
    blend, lit = st.get('blend'), st.get('lit')
    if 'gsc:' in name:
        if 'transparent' in name and 'hologram' in name:
            return {'cls': 'holo', 'why': 'LEGO transparent piece of a hologram sign'}
        if 'custom_animated' in name:
            return {'cls': 'holo', 'why': 'LEGO animated screen texture page'}
        if 'transparent' in name:
            return {'cls': 'glass', 'why': 'LEGO transparent piece'}
        return {'why': 'LEGO piece: tool default (opaque, emissive when it glows)'}
    if blend == 'additive' or (blend == 'alpha_blend' and not lit and emissive_link):
        return {'cls': 'holo', 'why': '%s, %s%s' % (blend, 'unlit' if not lit else 'lit', ', animated (Panner)' if 'Panner' in types else '')}
    if blend == 'alpha_blend' and 'fog' in name:
        return {'cls': 'holo', 'brightness': 0.35, 'why': 'alpha-blended cloud / fog sheet: a faint scrolling glow'}
    if blend == 'alpha_blend' and 'water' in name:
        return {'cls': 'holo', 'brightness': 0.25, 'why': 'alpha-blended water: a faint scrolling shimmer'}
    if blend == 'alpha_blend' and 'window' in name:
        return {'cls': 'glass', 'why': 'transparent window'}
    if blend == 'alpha_blend':
        return {'cls': 'holo', 'why': 'alpha-blended effect surface'}
    if not lit and 'Panner' in types and emissive_link:
        return {'cls': 'holo', 'why': 'unlit animated screen'}
    if lit and 'Panner' in types and emissive_link and any(w in name for w in ('shopsign', 'logo')):
        return {'cls': 'holo', 'why': 'lit sign whose glow is an animated screen'}
    if st.get('alpha') == 'alpha_test':
        two = any(w in name for w in ('foliage', 'branch', 'bamboo', 'ivy', 'leaf', 'plant', 'tree'))
        return {'cls': 'cutout2s' if two else 'cutout', 'why': 'alpha-tested%s' % (' foliage (two-sided)' if two else '')}
    return {'why': 'tool default (opaque, emissive when it glows)'}


used = {}
for o in bpy.data.objects:
    if o.type == 'MESH':
        for s in o.material_slots:
            if s.material:
                used[s.material.name] = s.material
for mname, m in sorted(used.items()):
    c = classify(m)
    b = m.bf2
    rec = {'why': c['why']}
    if c.get('skip'):
        b.skip = True
        rec['skip'] = True
    if c.get('cls'):
        b.cls = c['cls']
        rec['cls'] = c['cls']
    if b.cls == 'holo':
        g = graph(m)
        tex, pan, muv = main_texture(g)
        sp = [0.0, 0.0]
        if pan is not None:
            k = time_scale(g, pan['name'])
            sp = [fnum(pan['values'].get('Speed U', 0)) * k, fnum(pan['values'].get('Speed V', 0)) * k]
        if 'speed' in c:
            sp = list(c['speed'])
        til = [1.0, 1.0]
        if muv is not None:
            til = [fnum(muv['values'].get('U Tiling', 1), 1.0), fnum(muv['values'].get('V Tiling', 1), 1.0)]
        b.holo_speed = sp
        b.holo_tiling = til
        b.holo_brightness = float(c.get('brightness', 1.0))
        # one picture -> that image as it is; a picture built from several textures (a gradient through masks...) ->
        # left to the tool, which bakes the material's emission exactly as Blender shows it
        pics = {n['values'].get('tex', '').lower() for n in g.get('nodes', []) if n.get('type') == 'sampleTexture2D'
                and not any(w in n['values'].get('tex', '').lower() for w in HELPER_TEX)}
        img = blender_image(m, tex) if (tex and len(pics) == 1 and 'normal' not in tex.lower() and not tex.lower().endswith('_n.texture')) else None
        if img is not None:
            b.holo_image = img.name
        rec.update(texture=tex, image=img.name if img else None, speed=sp, tiling=til, brightness=b.holo_brightness)
    report['materials'][mname] = rec

# ---- roles
skip_names = {n for n, r in report['materials'].items() if r.get('skip')}
for o in bpy.data.objects:
    if o.type != 'MESH':
        continue
    cols = ' '.join(c.name for c in o.users_collection).lower()
    if 'unresolved' in cols or 'shadow prox' in cols or 'disabled' in cols:
        o.bf2.role = 'skip'; report['roles'][o.name] = 'collection: %s' % cols; continue
    mats = [s.material.name for s in o.material_slots if s.material]
    if mats and all(m in skip_names for m in mats):
        o.bf2.role = 'skip'; report['roles'][o.name] = 'every material is left out'
# ---- LEGO gameplay props are left out (user, 2026-10-23: "remove the lego props on the import"): the shared game
# mechanics and pickups (terminals, ladders, wall switches, minikits, studs, crates, weapons, hats ...) by their source
# folder, and a level's own LEGO mechanics by their model name (build-it piles "_bu", sabre-climb walls, grapple gears,
# wall cuts, hidden platforms, climb / ledge helpers). The level's set dressing (fans, screens, pipes ...) stays.
# Settings: "keep_lego_props": true keeps everything; "lego_prop_patterns": [...] adds name fragments.
LEGO_PROP_FOLDERS = ('assets/construction/gamemechanics/', 'assets/core/minikit/', 'assets/core/collectables/', 'chars/')
LEGO_PROP_NAMES = ('_bu_', '_bu.', 'buildit', 'sabrewallclimb', 'grapplegears', 'wallcut', 'hiddenplatform', 'autoclimb',
                   'ledgegrab', 'ladderdoor', 'wallswitch', 'netclimb', 'zipline', 'minikit', 'spintarget', 'stud')
props_out = {}
if not cfg.get('keep_lego_props'):
    names = LEGO_PROP_NAMES + tuple(p.lower() for p in cfg.get('lego_prop_patterns', []))
    for o in bpy.data.objects:
        res = str(o.get('lswtss_gsc_resource') or '').lower()
        if o.type != 'MESH' or not res:
            continue
        model = res.rsplit('/', 1)[-1]
        stem = model.split('.')[0].replace('_dx11', '')          # utapau_bridgebu_dx11.gsc -> utapau_bridgebu
        build_it = stem.endswith('bu') or '_bu_' in stem         # LEGO build-it piles and what they build
        if res.startswith(LEGO_PROP_FOLDERS) or build_it or any(n in model for n in names):
            o.bf2.role = 'skip'
            report['roles'][o.name] = 'LEGO gameplay prop (%s)' % model
            props_out[model] = props_out.get(model, 0) + 1
    report['lego_props_left_out'] = dict(sorted(props_out.items(), key=lambda kv: -kv[1]))
    report['notes'].append('LEGO gameplay props left out: %d object(s) of %d model(s)' % (sum(props_out.values()), len(props_out)))
keep_pat = [p.lower() for p in cfg.get('keep_objects', [])]
for o, role, why in guess.guess_roles(list(bpy.context.scene.objects)):
    if role == 'skip' and 'backdrop' in why and any(p in o.name.lower() for p in keep_pat):
        report['notes'].append('kept as scenery (settings keep_objects): %s - the guess said: %s' % (o.name, why))
        continue
    o.bf2.role = role
    report['roles'][o.name] = why

# ---- spawns: ray cast down onto the platform at each requested spot
sc = bpy.context.scene
dg = bpy.context.evaluated_depsgraph_get()
zlo, zhi = cfg.get('spawn_z', [-20.0, 40.0])
coll = bpy.data.collections.get('BF2 gameplay') or bpy.data.collections.new('BF2 gameplay')
if coll.name not in sc.collection.children:
    sc.collection.children.link(coll)
spawn_cfg = cfg.get('spawns') or {}


def solid(ob, idx):
    """a floor a soldier can stand on in game: map geometry with collision, and the face's material is not one the tool
    builds without collision (hologram, lava) or leaves out (seen 2026-10-23, LSWTSS Utapau: 3 spawns on such faces
    failed the check spawn.no_floor)"""
    orig = getattr(ob, 'original', ob)
    if orig.bf2.role != 'static' or orig.bf2.collision == 'none':
        return False
    me = ob.evaluated_get(dg).data if hasattr(ob, 'evaluated_get') else ob.data
    try:
        m = orig.material_slots[me.polygons[idx].material_index].material
    except (IndexError, AttributeError):
        return True
    return m is None or (m.bf2.cls not in ('holo', 'lava') and not m.bf2.skip)


if spawn_cfg.get('auto'):
    # every free, flat, up-facing floor point on a grid around the given centre; team 1 takes the west end, team 2 the
    # east end (along the longer side of the floor), points at least `gap` apart
    a = spawn_cfg['auto']
    cx, cy, R, step, n, gap = a['centre'][0], a['centre'][1], a['radius'], a.get('step', 4.0), a.get('per_team', 6), a.get('gap', 8.0)
    floor = []
    for gx in range(int(-R / step), int(R / step) + 1):
        for gy in range(int(-R / step), int(R / step) + 1):
            x, y = cx + gx * step, cy + gy * step
            if (x - cx) ** 2 + (y - cy) ** 2 > R * R:
                continue
            z0 = zhi + 2.0
            for _ in range(8):
                ok, loc, nrm, idx, ob, mtx = sc.ray_cast(dg, Vector((x, y, z0)), Vector((0, 0, -1)), distance=z0 - zlo)
                if not ok:
                    break
                if nrm.z > 0.9 and zlo <= loc.z <= zhi and solid(ob, idx):
                    ok2, *_ = sc.ray_cast(dg, loc + Vector((0, 0, 0.05)), Vector((0, 0, 1)), distance=2.5)
                    # room around: four 1 m horizontal rays at chest height must be free
                    free = not ok2 and not any(sc.ray_cast(dg, loc + Vector((0, 0, 1.2)), Vector(d), distance=1.0)[0]
                                               for d in ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0)))
                    if free:
                        floor.append((x, y, loc.z))
                    break
                z0 = loc.z - 0.05
    report['notes'].append('auto spawns: %d free floor points' % len(floor))
    xs = sorted(floor, key=lambda p: p[0])
    teams = {'team1': xs, 'team2': list(reversed(xs))}
    spawn_cfg = {}
    for team, cand in teams.items():
        pick = []
        for p in cand:
            if all((p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2 >= gap * gap for q in pick):
                pick.append(p)
            if len(pick) >= n:
                break
        spawn_cfg[team] = [[p[0], p[1]] for p in pick]
for team, pts in spawn_cfg.items():
    for i, (x, y) in enumerate(pts):
        z0, hit_z = zhi + 2.0, None
        for _ in range(12):
            ok, loc, nrm, idx, ob, mtx = sc.ray_cast(dg, Vector((x, y, z0)), Vector((0, 0, -1)), distance=z0 - zlo)
            if not ok:
                break
            if nrm.z > 0.8 and zlo <= loc.z <= zhi and solid(ob, idx):
                # standing room: nothing within 2.2 m above
                ok2, *_ = sc.ray_cast(dg, loc + Vector((0, 0, 0.05)), Vector((0, 0, 1)), distance=2.2)
                if not ok2:
                    hit_z = loc.z
                    break
            z0 = loc.z - 0.05
        if hit_z is None:
            report['spawns'].append({'team': team, 'xy': [x, y], 'placed': False})
            continue
        e = bpy.data.objects.new('BF2_Spawn_%s_%d' % (team, i + 1), None)
        e.empty_display_type = 'ARROWS'
        e.location = (x, y, hit_z + 0.1)
        coll.objects.link(e)
        e.bf2.role = 'spawn'
        e.bf2.team = team
        report['spawns'].append({'team': team, 'xy': [x, y], 'z': round(hit_z, 2), 'placed': True})

# ---- no collision for far scenery (no soldier reaches it; keeps the collision stage small)
far = float(cfg.get('far_no_collision', 0.0))
if far > 0:
    sp_pts = [Vector((s['xy'][0], s['xy'][1], 0)) for s in report['spawns'] if s.get('placed')]
    c = sum(sp_pts, Vector()) / len(sp_pts) if sp_pts else Vector()
    n_far = 0
    for o in bpy.data.objects:
        if o.type == 'MESH' and o.bf2.role == 'static':
            ws = [o.matrix_world @ Vector(b) for b in o.bound_box]
            near = min((Vector((w.x, w.y, 0)) - c).length for w in ws)
            if near > far:
                o.bf2.collision = 'none'; n_far += 1
    report['notes'].append('%d objects farther than %.0f m from the spawns: collision none' % (n_far, far))

# ---- scene settings
s = sc.bf2
for k in ('name', 'display_name', 'planet', 'description', 'era', 'play_radius', 'keep_distance', 'bake_quality', 'sky',
          'mode_blast', 'mode_hvv', 'mode_arcade', 'mode_supremacy', 'auto_spawns', 'fog_on', 'bake_max_size', 'bake_materials'):
    if k in cfg:
        setattr(s, k, cfg[k])
# lighting: a game sky (the planet from the names, interior exposure under a roof) and the light bake on Build - the same
# choice "Prepare imported map" makes; settings.json can name the sky ("game_sky") or keep the old one ("sky")
ops = __import__(pkg + '.ops', fromlist=['ops'])
if 'sky' not in cfg:
    gs, why = ops.guess_sky(bpy.context)
    s.sky = gs
    if gs == 'game':
        s.game_sky = cfg.get('game_sky') or why['game_sky']
        s.exposure_mode = cfg.get('exposure_mode') or why['exposure_mode']
    report['notes'].append('sky: %s %s (%s; %s)' % (s.sky, s.game_sky, why.get('planet_why'), why.get('cover_why')))
s.bake_on_build = bool(cfg.get('bake_on_build', True))
# the game draws about 64 lamps in real time: the weakest over that are baked (LEGO Utapau has 114)
n_baked, n_live = ops.fit_lights(bpy.context, s.dynamic_light_budget)
report['notes'].append('lights: %d made Baked, %d real-time (budget %d)' % (n_baked, n_live, s.dynamic_light_budget))
# scale: LSWTSS builds its levels for minifigs, and with the export's units a 1.8 m soldier is dwarfed by them (counters
# at his chest, doors and decks far too big - seen in game 2026-10-23). The whole scene, spawns included, shrinks by
# map_scale (0.75: corridor ceilings around the spawns 3.7 -> 2.75 m, the narrowest openings 2.1 -> 1.6 m).
f = float(cfg.get('map_scale', 0.75))
if abs(f - 1.0) > 1e-6:
    n_top = ops.scale_scene(bpy.context, f)
    s.play_radius *= f
    s.keep_distance *= f
    report['notes'].append('scaled the map by %g (%d top objects); play radius %.0f m, keep within %.0f m' % (f, n_top, s.play_radius, s.keep_distance))
report['scene'] = {k: getattr(s, k) for k in ('name', 'display_name', 'play_radius', 'keep_distance', 'bake_quality', 'sky')}
report['scene'].update(game_sky=s.game_sky, exposure_mode=s.exposure_mode, map_scale=f, bake_on_build=s.bake_on_build)
from collections import Counter
report['summary'] = {'classes': dict(Counter(r.get('cls', 'default') for r in report['materials'].values())),
                     'materials_left_out': len(skip_names), 'objects_left_out': len(report['roles']),
                     'spawns_placed': sum(1 for x in report['spawns'] if x.get('placed')),
                     'holo_without_image': sorted(n for n, r in report['materials'].items() if r.get('cls') == 'holo' and not r.get('image'))}
json.dump(report, open(os.path.splitext(OUT)[0] + '.prep.json', 'w', encoding='utf-8'), indent=1)
bpy.ops.wm.save_as_mainfile(filepath=OUT, copy=True)
print('PREP DONE', json.dumps(report['summary']))
