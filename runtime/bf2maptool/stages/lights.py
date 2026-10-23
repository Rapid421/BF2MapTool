"""Stage `lights`: the scene's point / spot / area lights -> the game's light entities, per light Baked, Dynamic or Both.

  point -> PbrSphereLightEntityData      spot -> PbrSpotLightEntityData      area -> PbrRectangularLightEntityData
Every field a scene light does not decide keeps the value the host level's own lights ship with
(hosts/<host>_lights.json, read from the game with `level export-lights`).

Mode          Enabled  AffectRadiosity  DirectLightmapEnable   meaning
  dynamic     True     False            False                  real-time only; nothing of it in the bake
  both        True     True             False                  real-time direct light, its bounce baked
  baked       True     True             True                   direct and bounce from the lightmaps (needs a bake)
Shadows (dynamic / both only): CastShadows = QualityScalableEnabled_Low, as the host's own shadow-casting spots.

CONVERSION (PROVISIONAL until measured in game - graded UNKNOWN; every number is written to lights.json so the panel
can show it next to the light):
  lumens  = Blender watts x LM_PER_WATT, times the look's light scale
  radius  = the light's custom distance when set, else clamp(sqrt(lumens / 40), 3, 40) m
  colour  = the light's colour scaled so its largest channel is 32 (the scale the ports' working lights used)
  spot    = OuterAngle = Blender spot size, InnerAngle = spot size x (1 - blend); the entity shines along -Forward
            (INFERRED from the host's lights: 92 % of its spots have Forward pointing up)
  area    = Width / Height = the area light's sizes, same axis rule
"""
import json
import math
import os
import shutil

import numpy as np

from .. import chain, cli, hosts, photometry
from ..runner import StageError

LM_PER_WATT = 10.0
TYPES = {'point': 'PbrSphereLightEntityData', 'spot': 'PbrSpotLightEntityData', 'area': 'PbrRectangularLightEntityData'}
MODES = {'dynamic': ('True', 'False', 'False'), 'both': ('True', 'True', 'False'), 'baked': ('True', 'True', 'True')}


def scene_lights(job):
    return [l for l in job.map.get('lights') or [] if l['type'] in TYPES and not l.get('hidden')]


def inputs(job):
    U = photometry.units(job) if photometry.matched(job) else None
    return {'lights': scene_lights(job), 'offset': job.offset(), 'scale': (job.map.get('look') or {}).get('light_scale'),
            'rule': (LM_PER_WATT, 3), 'match': [U['D'], U['ev_level'], sorted(U['constants'].items())] if U else None}


def outputs(job):
    return [job.project('lights'), job.work + '/lights.json']


def game_dir(v):
    return np.array([v[0], v[2], -v[1]], float)


def basis_for(direction):
    """right / up / forward of an entity that shines along -Forward"""
    f = -np.asarray(direction, float)
    f = f / max(np.linalg.norm(f), 1e-9)
    ref = np.array([0.0, 1.0, 0.0]) if abs(f[1]) < 0.95 else np.array([1.0, 0.0, 0.0])
    r = np.cross(ref, f); r = r / np.linalg.norm(r)
    u = np.cross(f, r)
    return [float(x) for x in r], [float(x) for x in u], [float(x) for x in f]


def convert(job, l, templates, scale, U=None):
    T = TYPES[l['type']]
    F = dict(templates[T]['fields'])
    c = np.clip(np.asarray(l['color'][:3], float), 0.0, None)
    if U is not None:
        # matched to the scene: lumens from the lamp's Blender watts by its kind (photometry.py), the colour's own
        # strength kept in the lumens, and a reach where the lamp no longer shows
        cm = max(float(c.max()), 1e-6)
        lm = photometry.lamp_lumens(l['type'], float(l['energy']) * cm, U) * scale
        c = c / cm
        radius = float(l['cutoff']) if l.get('cutoff') else float(np.clip(photometry.reach(photometry.lamp_candela(l['type'], lm), U), 3.0, 80.0))
    else:
        lm = float(l['energy']) * LM_PER_WATT * scale
        radius = float(l['cutoff']) if l.get('cutoff') else float(np.clip(math.sqrt(max(lm, 1.0) / 40.0), 3.0, 40.0))
        c = c / max(float(c.max()), 1e-6) * 32.0
    en, rad, dlm = MODES[l.get('mode', 'both')]
    F.update({'Enabled': en, 'AffectRadiosity': rad, 'DirectLightmapEnable': dlm, 'AffectDiffuse': 'True', 'AffectSpecular': 'True',
              'Color': '%.4f,%.4f,%.4f' % tuple(c), 'Intensity': '%.1f' % lm, 'AttenuationRadius': '%.2f' % radius, 'Dimmer': '1',
              'LightUnit': 'LightUnitType_LuminousPower',
              'CastShadows': 'QualityScalableEnabled_Low' if (l.get('shadows') and l.get('mode') != 'baked') else 'QualityScalableEnabled_Disabled'})
    right, up, fwd = [1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]
    if l['type'] == 'point':
        F['SphereRadius'] = '%.3f' % min(max(float(l.get('radius') or 0.0), 0.0), 2.0)
    else:
        right, up, fwd = basis_for(game_dir(l['dir']))
    if l['type'] == 'spot':
        outer = math.degrees(float(l.get('spot_size', math.radians(45.0))))
        F['OuterAngle'] = '%.3f' % outer
        F['InnerAngle'] = '%.3f' % (outer * (1.0 - float(l.get('spot_blend', 0.15))))
        F['AttenuationOffset'] = '0'
    if l['type'] == 'area':
        F['Width'] = '%.4f' % float(l.get('size', 1.0))
        F['Height'] = '%.4f' % float(l.get('size_y', l.get('size', 1.0)))
        F['OuterAngle'] = '120'
    rec = {'Type': T, 'Source': 'scene:' + l['name'], 'Blueprint': None, 'Right': right, 'Up': up, 'Forward': fwd,
           'Trans': [round(float(x), 4) for x in job.to_game(l['pos'])], 'Fields': F}
    shown = {'name': l['name'], 'type': l['type'], 'mode': l.get('mode', 'both'), 'watts': l['energy'], 'lumens': round(lm, 1), 'radius_m': round(radius, 2),
             'colour': [round(float(x), 3) for x in c], 'shadows': F['CastShadows'] != 'QualityScalableEnabled_Disabled'}
    return rec, shown


def run(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    src, out = chain.base(job, 'lights'), job.project('lights')
    lights = scene_lights(job)
    with open(os.path.join(os.path.dirname(os.path.abspath(hosts.__file__)), job.host + '_lights.json'), encoding='utf-8') as f:
        templates = json.load(f)
    scale = float((job.map.get('look') or {}).get('light_scale') or 1.0)
    if not lights:
        shutil.copyfile(src, out)
        with open(job.work + '/lights.json', 'w', encoding='utf-8') as f:
            json.dump({'lights': [], 'rule': {'lm_per_watt': LM_PER_WATT, 'graded': 'UNKNOWN (provisional)'}}, f, indent=1)
        log('lights: none in the scene')
        return {'lights': 0}
    recs, shown = [], []
    U = photometry.units(job) if photometry.matched(job) else None
    for l in lights:
        r, s = convert(job, l, templates, scale, U)
        recs.append(r); shown.append(s)
    rf = job.path('layers', 'lights.json')
    os.makedirs(os.path.dirname(rf), exist_ok=True)
    with open(rf, 'w', encoding='utf-8') as f:
        json.dump({'source': job.name + ' scene lights', 'count': len(recs), 'skipped': 0, 'lights': recs}, f, indent=1)
    layer = job.level_dir + '/' + host.LIGHT_LAYER
    dump = job.path('dumps', 'lights_%s.json' % host.LIGHT_LAYER)
    r = clix.run('lights', [cli.load(src), {'op': 'level-import-lights', 'ebx': layer, 'from': rf},
                            {'op': 'ebx-dump', 'ebx': layer, 'deep': True, 'out': dump}, cli.save(out)])
    # read-back: one entity per scene light, of the planned type, with the planned lumens
    with open(dump, encoding='utf-8') as f:
        txt = json.load(f)
    found = []

    def walk(o):
        if isinstance(o, dict):
            if str(o.get('__type', '')).startswith('Pbr') and str(o.get('__type', '')).endswith('LightEntityData'):
                found.append(o)
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(txt)
    by_type = {}
    for o in found:
        by_type.setdefault(o['__type'], []).append(round(float(o.get('Intensity', -1)), 1))
    want = {}
    for rec in recs:
        want.setdefault(rec['Type'], []).append(round(float(rec['Fields']['Intensity']), 1))
    def same(a, b):
        # the level stores single-precision numbers: a planned 5,987,875.3 lm reads back as 5,987,875.5
        return len(a) == len(b) and all(abs(x - y) <= max(0.06, 2e-6 * abs(x)) for x, y in zip(sorted(a), sorted(b)))
    bad = [t for t in want if not same(want[t], by_type.get(t, []))]
    rule = {'lm_per_watt': LM_PER_WATT, 'light_scale': scale, 'graded': 'UNKNOWN (provisional, to be measured)'}
    if U is not None:
        rule = {'matched_to_scene': True, 'nits_per_blender_unit': U['D'], 'ev': U['ev'], 'point_lm_per_watt': photometry.lamp_lumens('point', 1.0, U),
                'spot_lm_per_watt': photometry.lamp_lumens('spot', 1.0, U), 'area_lm_per_watt': photometry.lamp_lumens('area', 1.0, U),
                'light_scale': scale, 'graded': U['grades']}
    info = {'lights': shown, 'layer': layer, 'rule': rule,
            'modes': {m: sum(1 for s in shown if s['mode'] == m) for m in MODES}, 'readback': {t: len(v) for t, v in by_type.items()}}
    with open(job.work + '/lights.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    if bad or r.errors:
        raise StageError('The lights did not come out as planned (%s)  %s  [log: %s]' % (
            ', '.join('%s: %d planned, %d found' % (t, len(want[t]), len(by_type.get(t, []))) for t in bad), ' | '.join(r.errors[:2])[:300], r.log_path))
    log('lights: %d (%s) -> %s; %s' % (len(shown), ', '.join('%d %s' % (v, k) for k, v in info['modes'].items() if v), host.LIGHT_LAYER,
                                       ', '.join('%s %.0f W = %.0f lm, r %.1f m' % (s['name'], s['watts'], s['lumens'], s['radius_m']) for s in shown[:4])))
    return {'lights': len(shown), 'modes': info['modes']}
