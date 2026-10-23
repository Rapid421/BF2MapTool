"""Pre-flight checks: everything that can be known to fail BEFORE a build, said in plain words, with the objects or
materials it is about so the add-on can select them. An error blocks the build; a warning does not.

Each check is a function ``(job, scene) -> list of results``; a result is
``{'code', 'severity': 'error'|'warning'|'info', 'message', 'objects': [...], 'materials': [...], 'fix'}``.
`scene` caches the exported geometry so every check reads it once.
"""
import math

import numpy as np

from .. import hosts, shipped, texpack
from ..stages import doors as doors_stage
from ..stages.spawns import in_box


TEAMS_APART = 30.0      # metres between the two teams' spawn centres: small arenas (the game's Heroes vs Villains maps)
                        # put them 30 - 40 m apart; 40 stopped a scaled LEGO platform 40 m across


def R(code, severity, message, objects=(), materials=(), fix=''):
    return {'code': code, 'severity': severity, 'message': message, 'objects': list(objects)[:200], 'materials': list(materials)[:200], 'fix': fix}


class Scene:
    def __init__(self, job):
        self.job = job
        self._geo = {}

    def geo(self, obj):
        n = obj['name']
        if n not in self._geo:
            z = np.load(self.job.src(obj['geo']), allow_pickle=False)
            self._geo[n] = {k: z[k] for k in z.files}
        return self._geo[n]

    def objects(self, role='static'):
        return [o for o in self.job.map['objects'] if o.get('role') == role]


def check_map(job, sc):
    out = []
    m = job.map
    if not sc.objects('static'):
        out.append(R('geo.none', 'error', 'The scene has no map geometry.', fix='Add mesh objects; their role must be "Map geometry".'))
    if not job.modes:
        out.append(R('map.no_mode', 'error', 'No game mode is ticked.', fix='Tick at least one mode in the Map tab.'))
    lost = getattr(job, 'missing_images', None) or {}
    if lost:
        names = sorted(lost)
        out.append(R('tex.missing', 'warning', '%d material(s) have a missing image (not packed, not on disk): %s. They are built without it '
                     '(a missing colour map = grey, a missing normal map = flat).' % (
                         len(names), '; '.join('%s: %s' % (n, ', '.join(lost[n])) for n in names[:4]) + (' ...' if len(names) > 4 else '')),
                     materials=names, fix='Find the image files (File > External Data > Find Missing Files) or pack them into the .blend.'))
    clash = shipped.clash(job)
    if clash:
        out.append(R('map.name_clash', 'error', 'The level name "%s" is taken by a level the game ships (%s).' % (job.name, clash),
                     fix='Choose another level name in the Map tab.'))
    if 'supremacy' in job.modes:
        cps = m.get('command_posts') or []
        letters = sorted(str(c.get('letter', '')).upper() for c in cps)
        if len(cps) != 5:
            out.append(R('supremacy.posts', 'error', 'Supremacy needs 5 command posts; %d are placed.' % len(cps), [c['name'] for c in cps],
                         fix='Add command posts from the Gameplay tab (letters A to E).'))
        elif letters != list('ABCDE'):
            out.append(R('supremacy.letters', 'error', 'The five command posts must be lettered A, B, C, D, E once each; they are %s.' % ', '.join(letters),
                         [c['name'] for c in cps], fix="Set each post's letter in the Gameplay tab: A and B on team 1's side, C in the middle, D and E on team 2's."))
    look = m.get('look') or {}
    if look.get('sky') == 'game':
        out.append(R('look.game_sky', 'info', 'Sky: the game\'s own "%s" - its sky, sun, fog, exposure and grading%s.' % (
            (look.get('game_sky_asset') or look.get('game_sky') or 'tatooine_sunny').split('/')[-1],
            ', with interior exposure' if look.get('exposure_mode') == 'interior' else ''),
            fix='Nothing to do. Pick another game sky (or Exposure: Interior for an enclosed map) in the Lighting tab.'))
    if look.get('sky') not in (None, 'world', 'game') and look.get('world_kind') == 'image':
        # a real case (2026-10-12): a scene lit by its World was ported with the "sunset" preset and came out pink,
        # with a hard sun the scene does not have
        out.append(R('look.sky_not_world', 'warning', 'The scene has its own World (a sky image), but Sky is set to "%s": the game gets that '
                     'preset sky and its light, not what you see in Blender.' % look.get('sky'),
                     fix='Set Sky to "Blender World (match the scene)" in the Lighting tab to carry the scene\'s sky, sun and exposure over.'))
    sun = look.get('sun') or {}
    extra = [n for n in (look.get('suns') or []) if n != sun.get('name')]
    if extra:
        out.append(R('look.one_sun', 'warning', 'The scene has %d Sun lamps; the game has one sun. "%s" is used (the strongest that shines '
                     'downwards); not carried over: %s.' % (len(extra) + 1, sun.get('name'), ', '.join(extra[:6])), extra[:20],
                     fix='Nothing to do if the others are weak fill lights. Otherwise light those places with point, spot or area lamps.'))
    if look.get('sky') == 'world' and look.get('sun_on', True) and float(sun.get('angle') or 0.0) > math.radians(5.0):
        # seen in game 2026-10-12: a 33 degree sun blurs a roof's shadow away in Blender; the game drew its hard edge across the floor
        out.append(R('look.sun_soft', 'warning', 'The Sun lamp "%s" has an Angle of %.0f degrees: Blender blurs its shadows over metres, '
                     'the game draws sun shadows sharp, so shadow edges will show in game that you do not see in Blender.'
                     % (sun.get('name', 'Sun'), math.degrees(float(sun['angle']))), [sun.get('name')] if sun.get('name') else [],
                     fix='Set the Sun lamp\'s Angle to about 1 degree and look at the render again - that is what the game will show.'))
    return out


def check_geometry(job, sc):
    out = []
    host = hosts.get(job.host)
    no_mat, no_uv, degenerate, neg, scaled, big = [], [], [], [], [], []
    lo, hi = np.full(3, 1e18), np.full(3, -1e18)
    for o in sc.objects('static') + sc.objects('door'):
        g = sc.geo(o)
        used = {str(g['slots'][i]) for i in np.unique(g['mat'])}
        if '' in used:
            no_mat.append(o['name'])
        if not bool(g['has_uv']):
            no_uv.append(o['name'])
        p = g['pos']
        a = np.linalg.norm(np.cross(p[:, 1] - p[:, 0], p[:, 2] - p[:, 0]), axis=1) / 2
        if (a < 1e-10).sum() > max(2, 0.02 * len(a)):
            degenerate.append('%s (%d)' % (o['name'], int((a < 1e-10).sum())))
        if o.get('negative_scale'):
            neg.append(o['name'])
        s = o.get('scale') or [1, 1, 1]
        if max(abs(abs(x) - 1.0) for x in s) > 1e-3:
            scaled.append(o['name'])
        lo = np.minimum(lo, p.reshape(-1, 3).min(0)); hi = np.maximum(hi, p.reshape(-1, 3).max(0))
    if no_mat:
        out.append(R('geo.no_material', 'error', '%d object(s) have faces without a material.' % len(no_mat), no_mat,
                     fix='Give every face a material (Edit mode: select the faces, Assign).'))
    dm = job.map.get('default_material_objects') or []
    if dm:
        out.append(R('geo.no_material', 'warning', '%d object(s) have faces without a material: they are built plain grey, as Blender draws '
                     'them.' % len(dm), dm, fix='Give those faces a material (Edit mode: select the faces, Assign) if grey is not right.'))
    if no_uv:
        out.append(R('geo.no_uv', 'error', '%d object(s) have no UV map.' % len(no_uv), no_uv, fix='Unwrap them (UV > Smart UV Project is enough for a start).'))
    auto_uv = [o['name'] for o in sc.objects('static') + sc.objects('door') if o.get('auto_uv')]
    if auto_uv:
        out.append(R('geo.auto_uv', 'warning', '%d object(s) have no UV map and were unwrapped by the tool (a box projection, 1 m per texture '
                     'repeat): %s.' % (len(auto_uv), ', '.join(auto_uv[:8])), auto_uv,
                     fix='Fine for tiling materials. Unwrap them yourself (UV > Smart UV Project) if the texture must sit a certain way.'))
    if degenerate:
        out.append(R('geo.degenerate', 'warning', 'Faces without area: %s. They are dropped.' % ', '.join(degenerate[:6]),
                     [d.rsplit(' (', 1)[0] for d in degenerate], fix='Mesh > Clean Up > Degenerate Dissolve.'))
    if neg:
        out.append(R('geo.scale', 'warning', '%d object(s) are mirrored (negative scale). Their faces are flipped back on export; check that they look right.' % len(neg), neg,
                     fix='Apply the scale (Ctrl+A > Scale) and recalculate the normals if a face looks inside-out.'))
    if scaled:
        out.append(R('geo.scale', 'info', '%d object(s) have an unapplied scale. The export uses the size you see, so nothing changes in game.' % len(scaled), scaled))
    if sc.objects('static'):
        ext = hi - lo
        c, r = job.play()
        far = float(max(np.abs(lo[:2] - np.asarray(c)[:2]).max(), np.abs(hi[:2] - np.asarray(c)[:2]).max()))
        keep = float((job.map.get('play') or {}).get('keep_distance') or 0.0)
        if keep > 0 and far > r + keep:
            out.append(R('scene.cropped', 'info', 'Geometry reaches %.0f m from the play centre; everything beyond %.0f m (play radius + '
                         '"Keep geometry within") is left out, and nothing past the host\'s %.0f m.' % (far, r + keep, host.MAX_EXTENT)))
            far = min(r + keep, host.MAX_EXTENT)
        if far > host.MAX_EXTENT:
            out.append(R('scene.extent', 'error', 'The map reaches %.0f m from its play centre; the host level leaves room for %.0f m.' % (far, host.MAX_EXTENT),
                         fix='Remove far backdrop geometry or move it closer; a sky belongs in the sky panorama, not in the mesh.'))
        if max(ext[:2]) < 20.0:
            out.append(R('scene.scale', 'warning', 'The whole map is only %.1f x %.1f m. Is the scene modelled in metres?' % (ext[0], ext[1]),
                         fix='Add the 1.8 m reference figure (Map tab) next to a doorway to check the scale.'))
        if ext[2] > 2000:
            out.append(R('scene.scale', 'warning', 'The map is %.0f m tall. Is the scene modelled in metres?' % ext[2]))
    lim = float(getattr(host, 'FAR_LIMIT', 0.0) or 0.0)
    if lim > 0 and hi[0] > lo[0]:
        (cx, cy, cz), radius = job.play()
        reach = float(max(abs(lo[0] - cx), abs(hi[0] - cx), abs(lo[1] - cy), abs(hi[1] - cy)))
        keep = float((job.map.get('play') or {}).get('keep_distance') or 0.0)
        cut_at = max(lim, radius + 60.0)
        if reach > cut_at and (keep <= 0 or radius + keep > cut_at):
            out.append(R('geo.far_cut', 'warning', 'The scene reaches %.0f m from the play centre. The game level this tool builds on '
                         'crashes while loading with geometry that far out, so nothing beyond %.0f m is built (backdrop shells, far '
                         'skylines, distant terrain).' % (reach, cut_at),
                         fix='Nothing to do. For a far backdrop, put it in the World (the sky picture) instead of in meshes.'))
        if radius > lim:
            out.append(R('geo.play_far', 'warning', 'The play area is %.0f m in radius; levels have only been seen to load with geometry up '
                         'to about %.0f m from the centre. The whole play area is built, but the level may not load.' % (radius, lim),
                         fix='Move the spawns and command posts closer together, or set a smaller Play radius.'))
    return out


def check_materials(job, sc):
    out = []
    host = hosts.get(job.host)
    src = texpack.Source(job.src)
    used = set()
    for o in sc.objects('static') + sc.objects('door'):
        g = sc.geo(o)
        used |= {str(g['slots'][i]) for i in np.unique(g['mat'])} - {''}
    guessed = []
    for name, why in sorted((getattr(job, 'glow_settled', None) or {}).items()):
        out.append(R('class.glow_dropped', 'info', 'Material "%s": %s.' % (name, why), materials=[name],
                     fix='Nothing to do. If it should glow, check its Emission colour / strength (or the mask feeding them) in Blender.'))
    for name in sorted(used):
        rec = job.map['materials'].get(name)
        if rec is None or rec.get('skip'):
            continue
        if rec.get('class_guessed'):
            guessed.append(name)
        cls, auto = texpack.effective_class(rec, src)
        if auto:
            out.append(R('class.auto', 'info', 'Material "%s" is built as %s: %s.' % (name, {'cutout': 'a cut-out', 'cutout2s': 'a two-sided cut-out',
                         'emissive': 'emissive'}.get(cls, cls), auto), materials=[name],
                         fix='Nothing to do. To overrule it, pick the class yourself in the Materials tab.'))
        if cls == 'glass':
            out.append(R('class.limited', 'info', 'Material "%s": glass is built as the window glass of the host level - see-through and solid, '
                         'but its colour, tint and roughness are not taken from the material.' % name, materials=[name]))
        if cls == 'lava':
            out.append(R('class.placeholder', 'warning', 'Material "%s": lava is built as a still, glowing, not-solid surface in this version '
                         '(the flowing lava preset is not built in).' % name, materials=[name],
                         fix='Nothing to do; put a kill volume under the lava surface so soldiers who fall in die.'))
        if cls == 'holo':
            sp = (rec.get('holo') or {}).get('speed') or [0, 0]
            out.append(R('class.holo', 'info', 'Material "%s" is an animated hologram: glowing, see-through, not solid, not in the light bake; '
                         'it scrolls %g / %g per second and the game adds scan lines and flicker.' % (name, sp[0], sp[1]), materials=[name]))
        tname, T = host.template_for(cls)
        if T is None:
            out.append(R('class.unsupported', 'error', 'Material "%s": the class "%s" is not available in this version of the tool yet.' % (name, cls),
                         materials=[name], fix='Choose another class for now (Materials tab).'))
            continue
        inp = rec.get('inputs', {})
        try:
            size, sizes = texpack.base_size(inp, src)
            for x in texpack.resize_notes(size, sizes):
                out.append(R('tex.resized', 'info', 'Material "%s": %s.' % (name, x), materials=[name], fix='Nothing to do.'))
            for key, r in inp.items():
                if isinstance(r, dict) and 'image' in r:
                    src.rgba(r)
            if cls in ('cutout', 'cutout2s'):
                has, frac = texpack.has_cutout_alpha(inp, src)
                if not has or frac <= 0.0:
                    raise texpack.PackError('tex.cutout_no_alpha', 'it is a cut-out but its image has no see-through texels')
        except texpack.PackError as e:
            fix = {'tex.missing': 'Find the image file (File > External Data > Find Missing Files) or pack it into the .blend.',
                   'tex.size': 'Resize the image to a power of two (256, 512, 1024, 2048, 4096).',
                   'tex.mismatch': 'Use images of the same shape for all inputs of one material.',
                   'tex.cutout_no_alpha': 'Give the image an alpha channel with holes, or change the class to Opaque.'}.get(e.code, '')
            out.append(R(e.code, 'error', 'Material "%s": %s.' % (name, e), materials=[name], fix=fix))
            continue
        nrm = inp.get('normal')
        if isinstance(nrm, dict) and 'image' in nrm and not nrm.get('baked') and (rec.get('normal_format') or 'auto') == 'auto':
            try:
                px, _ = src.rgba(nrm)
                _, conv = texpack.normal_convention(px[..., :3])
            except texpack.PackError:
                conv = None
            if conv == 'directx':
                # MatTest 2026-10-23: a DirectX dome map is pressed in in Blender (which reads every map as OpenGL) and
                # raised in game (the tool reads it as what it is)
                out.append(R('tex.normal_directx', 'info', 'Material "%s": its normal map is DirectX (green down). The game shows the relief as it '
                             'was made; Blender reads every map as OpenGL, so there it looks pressed in.' % name, materials=[name],
                             fix='Nothing to do - or set Normal map format to OpenGL (Materials tab) to get what Blender shows.'))
        for key, r in inp.items():
            if not isinstance(r, dict) or r.get('baked'):
                continue
            what = key.replace('_', ' ')
            if r.get('unsupported'):
                out.append(R('tex.unsupported_node', 'warning', 'Material "%s": the %s input goes through a "%s" node and was not baked; '
                             'the image behind it is used as it is.' % (name, what, r['unsupported']), materials=[name],
                             fix='Set "Bake materials" to "When needed" in the Materials tab.'))
            elif r.get('uv_transform'):
                out.append(R('tex.uv_transform', 'warning', 'Material "%s": %s for the %s input and it was not baked; the image will sit on the map '
                             'untiled / unturned.' % (name, r['uv_transform'], what), materials=[name],
                             fix='Set "Bake materials" to "When needed" in the Materials tab.'))
            elif r.get('approximate'):
                out.append(R('tex.approximate', 'warning', 'Material "%s", %s input: %s (not baked).' % (name, what, r['approximate']), materials=[name],
                             fix='Set "Bake materials" to "When needed" in the Materials tab.'))
        bk = rec.get('baked') or {}
        for note in bk.get('notes', []):
            out.append(R('tex.bake_note', 'warning', 'Material "%s" was baked, but %s.' % (name, note), materials=[name],
                         fix='Drive that part of the material from its UV map, or accept the difference.'))
        if bk.get('not_baked'):
            out.append(R('tex.bake_incomplete', 'warning', 'Material "%s": the %s input(s) could not be baked; the image behind the nodes is used.' % (
                name, ', '.join(bk['not_baked'])), materials=[name], fix='Simplify the node chain in front of that input.'))
        if rec.get('no_principled') and not bk:
            out.append(R('tex.no_principled', 'warning', 'Material "%s" has no Principled BSDF and was not baked: only its viewport colour is used.' % name,
                         materials=[name], fix='Set "Bake materials" to "When needed", or rebuild the material on a Principled BSDF.'))
    if guessed:
        out.append(R('class.guessed', 'warning', '%d material class(es) were guessed and not confirmed.' % len(guessed), materials=guessed,
                     fix='Look through them in the Materials tab and untick "Guessed".'))
    return out


def check_gameplay(job, sc):
    out = []
    m = job.map
    host = hosts.get(job.host)
    kills = m.get('kill_volumes') or []
    if len(kills) > len(host.KILL_OBBS):
        out.append(R('kill.count', 'error', 'The scene has %d kill volumes; this host level has %d kill boxes.' % (len(kills), len(host.KILL_OBBS)),
                     [k['name'] for k in kills], fix='Merge volumes: one box can cover several pits.'))
    bad = [s['name'] for s in m.get('spawns') or [] if any(in_box(s['pos'], k) for k in kills)]
    if bad:
        out.append(R('spawn.in_kill', 'error', '%d spawn(s) are inside a kill volume.' % len(bad), bad, fix='Move the spawn or the kill volume.'))
    t1 = [s['pos'] for s in m.get('spawns') or [] if s['team'] == 'team1' and not s.get('hero')]
    t2 = [s['pos'] for s in m.get('spawns') or [] if s['team'] == 'team2' and not s.get('hero')]
    if t1 and t2:
        d = float(np.linalg.norm(np.mean(t1, 0) - np.mean(t2, 0)))
        if d < TEAMS_APART:
            out.append(R('spawn.teams_close', 'error', 'The two teams\' spawns are only %.0f m apart (%.0f m is the least).' % (d, TEAMS_APART),
                         [s['name'] for s in m['spawns'] if s['team'] in ('team1', 'team2')], fix='Move the team spawns further apart.'))
    if not (m.get('spawns') or []) and not (m.get('options') or {}).get('auto_spawns', True):
        out.append(R('spawn.none', 'error', 'No spawn is placed and Auto spawns is off.', fix='Tick Auto spawns (Gameplay tab) or place spawns.'))
    for o in sc.objects('door'):
        g = sc.geo(o)
        if len({str(g['slots'][i]) for i in np.unique(g['mat'])}) != 1:
            out.append(R('door.materials', 'error', 'Door "%s" has more than one material; a door panel takes one.' % o['name'], [o['name']],
                         fix='Join the door\'s materials into one, or split the door into separate panels.'))
            continue
        axis = (o.get('door') or {}).get('axis')
        if axis not in doors_stage.AXES:
            out.append(R('door.direction', 'error', 'Door "%s": the slide direction is not set.' % o['name'], [o['name']], fix='Pick Up, Down, Left or Right.'))
            continue
        ms = doors_stage.measure(g['pos'], axis)
        if ms['flatness'] > 0.25:
            out.append(R('door.shape', 'error', 'Door "%s" is not one flat panel.' % o['name'], [o['name']],
                         fix='A door must be a single slab; separate frames and other parts into their own objects.'))
    lights = [l for l in m.get('lights') or [] if l['type'] != 'sun' and not l.get('hidden')]
    dyn = [l['name'] for l in lights if l.get('mode') in ('dynamic', 'both')]
    budget = int((m.get('options') or {}).get('dynamic_light_budget', 64))
    if len(dyn) > budget:
        out.append(R('light.dynamic_budget', 'error', '%d lights are real-time; the budget is %d.' % (len(dyn), budget), dyn,
                     fix='Press "Fit lights to budget" in the Lighting tab (the weakest lamps become Baked), or set lamps to Baked by hand.'))
    sat = [l['name'] for l in lights if max(l['color'][:3]) > 0 and min(l['color'][:3]) / max(l['color'][:3]) < 0.05]
    if sat:
        out.append(R('light.saturated', 'warning', '%d light(s) have a fully saturated colour; in game they read much stronger than in Blender.' % len(sat), sat,
                     fix='Mix the colour towards white, or lower the power.'))
    return out


from . import space                                         # noqa: E402  (it imports R from this module)

CHECKS = (check_map, check_geometry, check_materials, check_gameplay) + space.CHECKS


def preflight(job):
    sc = Scene(job)
    out = []
    for c in CHECKS:
        try:
            out += c(job, sc)
        except Exception as e:                       # a check must never take the build down with a traceback
            out.append(R('check.internal', 'error', 'The check "%s" failed internally: %s: %s' % (c.__name__, type(e).__name__, e)))
    order = {'error': 0, 'warning': 1, 'info': 2}
    return sorted(group(out), key=lambda r: order.get(r['severity'], 3))


GROUP_OVER = 5          # more lines than this of one code (one per material) become one line


def group(results):
    """one line per code instead of hundreds: a ripped map gave 283 'tex.bake_note' lines, one per material, which buries
    the two errors that matter. The first message stays as the example; every material / object is kept for Select."""
    by = {}
    for r in results:
        by.setdefault((r['code'], r['severity']), []).append(r)
    out = []
    for (code, sev), rs in by.items():
        if len(rs) <= GROUP_OVER:
            out += rs
            continue
        mats = sorted({m for r in rs for m in r.get('materials', [])})
        objs = sorted({o for r in rs for o in r.get('objects', [])})
        g = dict(rs[0])
        g['message'] = '%d of these: %s  (and %d more like it - Select shows all)' % (len(rs), rs[0]['message'], len(rs) - 1)
        g['materials'], g['objects'], g['grouped'] = mats, objs, len(rs)
        out.append(g)
    return out
