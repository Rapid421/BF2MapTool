"""Stage `look`: the level's own visual environment - sky, sun, fog, exposure - instead of the host's weather.

  sky       an HDR panorama file (equirectangular .hdr; its upper half is the sky) or a preset; written as the game's
            sky panorama (4096 x 1024, zenith row first) + the sky gradient (512 x 256) and imported with the format of
            a shipped sky texture. Texel x LuminanceScale = nits.
  exposure  the preset's EV + the scene's exposure offset; MinEV / MaxEV = EV - 0.5 / EV + 1.
  fog       colour = the scene's fog colour x the sky's horizon luminance, start / end in metres, amount = the most the
            fog may hide (0..1). Height fog off.
  sun       on / off; direction from the scene's sun lamp; strength = the donor environment's own sun x the preset's
            factor.
The environment is a clone of a shipped one (`DONOR`); every VisualEnvironment reference of the level is pointed at
the clone. An override only applies when its field is listed in the component's PropertyOverrides - the two the donor
lacks are added, at indices CHECKED against a dump of the clone, not assumed.
Read-back: the clone is dumped and the values compared with the plan.

PRESET NUMBERS ARE PROVISIONAL (graded UNKNOWN until each preset has been looked at in game).
"""
import json
import math
import os

import numpy as np

from .. import chain, cli, config, hdr, hosts, photometry, reflection
from ..runner import StageError

DONOR = 'Levels/SP/A2/M3SUL/Lighting/VE_A2M3_Cloudy_02'
PANO_TPL = 'Levels/Lighting/Sullust/T_Sullust_Cloudy_01_C'
GRAD_TPL = 'Levels/Lighting/Sullust/T_Sullust_Cloudy_01_Fog_02_C'
ALPHA = 'Systems/Sky/Textures/DefaultPanoramaAlpha'
LUM = np.array([0.2126, 0.7152, 0.0722])
LSCALE = 100.0                      # LuminanceScale: a texel of 1.0 is 100 nits
PW, PH, GW, GH = 4096, 1024, 512, 256
# name: zenith nits rgb, horizon nits rgb, falloff exponent, EV, sun factor, cloud contrast
PRESETS = {
    'clear':    ((900.0, 1900.0, 4200.0), (6500.0, 7200.0, 7800.0), 0.45, 13.0, 1.0, 0.00),
    'overcast': ((1700.0, 1800.0, 1950.0), (2600.0, 2700.0, 2800.0), 0.80, 11.5, 0.12, 0.25),
    'sunset':   ((120.0, 170.0, 420.0), (2400.0, 950.0, 300.0), 0.35, 10.0, 0.45, 0.15),
    'night':    ((0.012, 0.016, 0.035), (0.05, 0.06, 0.10), 0.60, 1.0, 0.004, 0.10),
    'space':    ((0.002, 0.002, 0.003), (0.002, 0.002, 0.003), 1.00, 9.0, 1.0, 0.00),
    'volcanic': ((0.15, 0.03, 0.01), (11.0, 3.4, 0.5), 0.40, 4.0, 0.0, 0.30),
}


def _noise(h, w, rng, octaves=4):
    """smooth value noise that wraps in x (a panorama must tile around the horizon)"""
    out = np.zeros((h, w), np.float32)
    amp, tot = 1.0, 0.0
    for o in range(octaves):
        gw, gh = 8 * 2 ** o, 2 * 2 ** o
        g = rng.random((gh + 1, gw)).astype(np.float32)
        g = np.concatenate([g, g[:, :1]], 1)
        x = np.linspace(0, gw, w, endpoint=False); y = np.linspace(0, gh, h, endpoint=False)
        x0 = np.floor(x).astype(int); y0 = np.floor(y).astype(int)
        fx = (x - x0)[None, :]; fy = (y - y0)[:, None]
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy)
        a = g[y0][:, x0]; b = g[y0][:, x0 + 1]; c = g[y0 + 1][:, x0]; d = g[y0 + 1][:, x0 + 1]
        out += amp * ((a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy)
        tot += amp; amp *= 0.5
    return out / tot


def preset_sky(name):
    """-> panorama (PH, PW, 3) in nits, zenith row first; EV; sun factor"""
    z, hz, k, ev, sun, clouds = PRESETS[name]
    el = (1.0 - (np.arange(PH) + 0.5) / PH)[:, None]             # 1 at the zenith, 0 at the horizon
    t = np.power(el, k)                                         # how much of the zenith colour
    pano = np.asarray(hz)[None, None, :] * (1 - t)[..., None] + np.asarray(z)[None, None, :] * t[..., None]
    pano = np.repeat(pano, PW, axis=1).astype(np.float32)
    if clouds:
        n = _noise(PH, PW, np.random.default_rng(7))
        pano *= (1.0 + clouds * (n - 0.5) * 2.0)[..., None]
    return pano, ev, sun


def file_sky(path, strength):
    """the upper half of an equirectangular .hdr -> panorama in nits (the file's own units x strength x LSCALE)"""
    a = hdr.read(path)
    h, w = a.shape[:2]
    if abs(w - 2 * h) > 2 and abs(w - 4 * h) > 2:
        raise StageError('The sky panorama "%s" is %d x %d; an equirectangular panorama is twice as wide as it is high.' % (os.path.basename(path), w, h))
    top = a[: h // 2] if abs(w - 2 * h) <= 2 else a
    ys = ((np.arange(PH) + 0.5) / PH * top.shape[0]).astype(int).clip(0, top.shape[0] - 1)
    xs = ((np.arange(PW) + 0.5) / PW * top.shape[1]).astype(int).clip(0, top.shape[1] - 1)
    pano = top[ys][:, xs].astype(np.float32) * float(strength) * LSCALE
    med = float(np.median(pano @ LUM))
    ev = float(np.clip(math.log2(max(med, 1e-4) / 0.125) - 1.0, -2.0, 15.0))    # the median sky texel one stop over mid grey
    return pano, ev, 1.0


# The panorama column a game direction falls into: u = PANO_U0 - azimuth / 360 (mod 1), azimuth = atan2(x, z) in game
# axes, the convention the sun's SunRotationX uses. The direction of turn is the one the port's transcription of the
# engine's fast atan2 gives where it is self-consistent; the zero was MEASURED with the coloured sky markers of
# tests/lightmatch (the transcription's own zero was a quarter turn off). One function for the sky the player sees and
# the sky the bake lights with.
PANO_U0 = 0.0        # measured in game 2026-10-12 (LightMatch lm1): with 0.75 every sky marker sat a quarter turn off
PANO_AZ_SIGN, PANO_AZ_OFFSET = 1.0, 0.0     # a correction on top (1, 0 = none); set by a calibration build only


def pano_u(gx, gz):
    """panorama column 0..1 of a game direction"""
    return (PANO_U0 - np.arctan2(gx, gz) / (2.0 * math.pi)) % 1.0


def pano_azimuths():
    """game azimuth (radians) each panorama column looks along: the inverse of pano_u"""
    u = (np.arange(PW) + 0.5) / PW
    return (PANO_U0 - u) * 2.0 * math.pi


def world_pano(world, sign=PANO_AZ_SIGN, offset_deg=PANO_AZ_OFFSET):
    """the scene's World (equirectangular, scene-linear, laid out as the add-on's worldbake.direction says) -> the game's
    sky panorama layout (PH x PW, zenith row first, the upper hemisphere), still in scene-linear units.
    Scene (x, y, z) = game (x, -z, y)."""
    H, W = world.shape[:2]
    el = (math.pi / 2.0) * (1.0 - (np.arange(PH) + 0.5) / PH)
    az = sign * pano_azimuths() + math.radians(offset_deg)
    ce, se = np.cos(el)[:, None], np.sin(el)[:, None]
    gx, gy, gz = np.sin(az)[None, :] * ce, se * np.ones((1, PW)), np.cos(az)[None, :] * ce
    x, y, z = gx, -gz, gy
    azb = np.arctan2(-x, y)                                          # worldbake.direction inverted
    u = (0.5 - azb / (2.0 * math.pi)) % 1.0
    v = np.clip(0.5 - np.arcsin(np.clip(z, -1.0, 1.0)) / math.pi, 0.0, 1.0)
    fx, fy = u * W - 0.5, np.clip(v * H - 0.5, 0.0, H - 1.0)
    x0 = np.floor(fx).astype(int); y0 = np.floor(fy).astype(int)
    tx, ty = (fx - x0)[..., None], (fy - y0)[..., None]
    x1, y1 = (x0 + 1) % W, np.minimum(y0 + 1, H - 1)
    x0 = x0 % W
    return ((world[y0, x0] * (1 - tx) + world[y0, x1] * tx) * (1 - ty) + (world[y1, x0] * (1 - tx) + world[y1, x1] * tx) * ty).astype(np.float32)


def gradient(pano):
    prof = pano.mean(1)                                          # (PH, 3) zenith -> horizon
    hor = pano[-12:].mean((0, 1))
    g = np.zeros((GH, GW, 3), np.float32)
    for r in range(GH):
        e = 90.0 - 180.0 * (r + 0.5) / GH
        g[r] = prof[min(int((1 - e / 90.0) * (PH - 1)), PH - 1)] if e >= 0 else hor
    return g, hor


def inputs(job):
    L = dict(job.map.get('look') or {})
    f = L.get('sky_hdr')
    w = L.get('world_hdr')
    rfile = (L.get('reflection') or {}).get('file')
    return {'look': L, 'hdr': config.file_hash(f) if (L.get('sky') == 'hdr' and f and os.path.exists(f)) else None, 'presets': PRESETS,
            'world': config.file_hash(w) if (L.get('sky') == 'world' and w and os.path.exists(w)) else None,
            'photometry': [photometry.WHITE, photometry.C_SKY, photometry.C_SUN, PANO_U0, PANO_AZ_SIGN, PANO_AZ_OFFSET],
            'reflection': [config.file_hash(rfile) if (L.get('sky') == 'world' and rfile and os.path.exists(rfile)) else None,
                           reflection.SIZE, reflection.LOBE_DEG, getattr(hosts.get(job.host), 'REFLECTION_TEXTURE', None)],
            'probes': [[config.file_hash(p['file']), p.get('pos'), p.get('radius')] for p in (job.map.get('reflection_probes') or [])
                       if p.get('file') and os.path.exists(p['file'])], 'v': 9,
            'game_sky': [game_sky_asset(L), INTERIOR_EXPOSURE] if L.get('sky') == 'game' else None}


def outputs(job):
    return [job.project('look'), job.work + '/look.json']


def _find(dump, type_name):
    """(index in Object.Components, component dict) of the clone's dump"""
    comps = (dump.get('Object') or {}).get('Components') or dump.get('Components') or []
    for i, c in enumerate(comps):
        if isinstance(c, dict) and c.get('__type') == type_name:
            return i, c
    return None, None


# Shipped skies a level can take whole (the game's own sky texture, sun, fog, exposure and grading): id -> (name, asset).
# Numbers of the shipped ones (dumped 2026-10-23): sun 120,000 - 180,000, LuminanceScale 25,000 - 60,000, automatic
# exposure EV 12.5 - 15 with +1 .. +2 compensation - ten times what the generated presets gave, which is why every
# preset-lit map looked dark and flat.
GAME_SKIES = {
    'tatooine_sunny':  ('Tatooine - sunny', 'Levels/Lighting/Tatooine/Sunny/VE_PV_Tatooine_Sunny_01'),
    'tatooine_sunset': ('Tatooine - sunset', 'Levels/Lighting/Tatooine/Sunset/VE_Tatooine_Sunset_01'),
    'tatooine_night':  ('Tatooine - night', 'Levels/Lighting/Tatooine/Night/VE_Tatooine_Night_01'),
    'mos_eisley':      ('Mos Eisley - sunny', 'Levels/MP/Tatooine_01/Lighting/VE_PV_Desert_MosEisley_Sunny_01'),
    'endor_sunny':     ('Endor - sunny', 'Levels/Lighting/Endor/Sunny_01/VE_PV_Endor_Sunny_01'),
    'endor_foggy':     ('Endor - foggy', 'Levels/Lighting/Endor/Foggy_01/VE_PV_Endor_Foggy_01'),
    'endor_night':     ('Endor - night', 'Levels/Lighting/Endor/night_01/VE_PV_Endor_Night_01'),
    'hoth_sunny':      ('Hoth - sunny', 'Levels/Lighting/Hoth/Sunny_01/VE_Sky_Arctic_Sunny_01'),
    'hoth_sunset':     ('Hoth - sunset', 'Levels/Lighting/Hoth/Sunset_01/VE_PV_Hoth_Sunset_01'),
    'jakku_sunny':     ('Jakku - sunny', 'Levels/Lighting/Jakku/Sunny_01/VE_PV_Jakku_Sunny_01'),
    'jakku_sunset':    ('Jakku - sunset', 'Levels/MP/Jakku_01/Lighting/VE_PV_Jakku_sunset_01'),
    'kamino_stormy':   ('Kamino - stormy', 'Levels/Lighting/Kamino/Stormy_01/VE_Kamino_Stormy_01'),
    'kashyyyk_overcast': ('Kashyyyk - overcast', 'Levels/Lighting/Kashyyyk/Overcast_01/VE_PV_Kashyyyk_01_Overcast_01'),
    'kashyyyk_night':  ('Kashyyyk - night', 'Levels/Lighting/Kashyyyk/Overcast_01/VE_PV_Kashyyyk_01_Night_01'),
    'naboo_cloudy':    ('Naboo - cloudy', 'Levels/Lighting/Naboo/Cloudy/VE_Naboo_Cloudy_03'),
    'naboo_dusk':      ('Naboo - dusk', 'Levels/Lighting/Naboo/Dusk/VE_PV_Naboo_Dusk_01'),
    'naboo_sunset':    ('Naboo - sunset', 'Levels/Lighting/Naboo/Sunset/VE_Naboo_01_Sunset_01'),
    'starkiller_cloudy': ('Starkiller - cloudy', 'Levels/Lighting/Starkiller/Cloudy_01/VE_PV_StarKiller_Cloudy_01'),
    'takodana_cloudy': ('Takodana - cloudy', 'Levels/Lighting/Takodana/VE_PV_Takodana_Cloudy_01'),
    'takodana_dawn':   ('Takodana - misty dawn', 'Levels/Lighting/Takodana/VE_PV_Takodana_MistyDawn_01'),
    'yavin_sunny':     ('Yavin 4 - sunny', 'Levels/Lighting/Yavin/Sunny_01/VE_PV_Yavin_Sunny_01'),
    'yavin_sunset':    ('Yavin 4 - sunset', 'Levels/Lighting/Yavin/Sunset_01/VE_PV_Yavin_Sunset_01'),
    'sullust_cloudy':  ('Sullust - cloudy', DONOR),
}


BASE_GRADE = 'Levels/Lighting/Global/T_Walrus_BaseGrade_01'   # the game-wide colour grade (always loaded)
INTERIOR_EXPOSURE = (8.0, 10.5, 1.25)      # MinEV, MaxEV, compensation of an enclosed map (shipped interiors, above)
# the exposure each game sky settles at: (MinEV + MaxEV) / 2 - ExposureCompensation, read from deep dumps 2026-10-23
GAME_SKY_EV = {'tatooine_sunny': 12.5, 'tatooine_sunset': 9.25, 'tatooine_night': 7.3, 'mos_eisley': 12.5, 'endor_sunny': 12.25,
               'endor_foggy': 8.5, 'endor_night': 7.4, 'hoth_sunny': 11.5, 'hoth_sunset': 8.95, 'jakku_sunny': 12.0, 'jakku_sunset': 9.93,
               'kamino_stormy': 8.5, 'kashyyyk_overcast': 12.0, 'kashyyyk_night': 6.75, 'naboo_cloudy': 12.88, 'naboo_dusk': 9.0,
               'naboo_sunset': 10.15, 'starkiller_cloudy': 10.5, 'takodana_cloudy': 11.0, 'takodana_dawn': 9.5, 'yavin_sunny': 11.25,
               'yavin_sunset': 9.75, 'sullust_cloudy': 10.0}
EV_REFERENCE = 11.5      # the exposure the host's emissive / lamp numbers were set at (the old fixed preset EV)


def settled_ev(L):
    """the EV a game-sky level settles at (None for other skies, or a typed-in asset whose numbers are not known)"""
    if L.get('sky') != 'game':
        return None
    if L.get('exposure_mode') == 'interior':
        lo, hi, comp = INTERIOR_EXPOSURE
        return (lo + hi) / 2 - comp - float(L.get('exposure', 0.0))
    if (L.get('game_sky_asset') or '').strip():
        return None
    ev = GAME_SKY_EV.get(L.get('game_sky') or 'tatooine_sunny')
    return None if ev is None else ev - float(L.get('exposure', 0.0))


def game_sky_asset(L):
    """the shipped visual environment the look asks for: a GAME_SKIES id, or any asset path typed in"""
    custom = (L.get('game_sky_asset') or '').strip().strip('/')
    if custom:
        return custom
    return GAME_SKIES.get(L.get('game_sky') or 'tatooine_sunny', GAME_SKIES['tatooine_sunny'])[1]


def _refs(o, out):
    """every asset path a dump refers to"""
    if isinstance(o, dict):
        p = o.get('__path')
        if isinstance(p, str) and p:
            out.add(p)
        for v in o.values():
            _refs(v, out)
    elif isinstance(o, list):
        for v in o:
            _refs(v, out)
    return out


def _resample(a, h, w):
    """nearest-row / box-column resample of a panorama to h x w"""
    ys = np.minimum((np.arange(h) + 0.5) * a.shape[0] / h, a.shape[0] - 1).astype(int)
    a = a[ys]
    if a.shape[1] % w == 0:
        return a.reshape(h, w, a.shape[1] // w, a.shape[2]).mean(2)
    xs = np.minimum((np.arange(w) + 0.5) * a.shape[1] / w, a.shape[1] - 1).astype(int)
    return a[:, xs]


def run_game(job, log, host, clix, L, src, out):
    """sky 'game': the level takes a SHIPPED visual environment whole - its sky panorama and gradient, sun (strength,
    colour, angle), fog, automatic exposure and colour grading - cloned under the level's name. Only the sun's direction
    follows the scene's sun lamp, when there is one (and "sun from the scene" is on); an exposure offset adds to the
    shipped compensation. The sky panorama is exported to the level's sky file, so the light bake and the info line
    work from the same sky the player sees."""
    asset = game_sky_asset(L)
    VE = job.prefix + '/Lighting/VE_%s_01' % job.name
    B = job.bundle
    probe = job.path('dumps', 'look_clone.json')
    pre = job.project('look_0')
    r = clix.run('look_clone', [cli.load(src), {'op': 'ebx-clone', 'as': VE, 'source': asset},
                                {'op': 'ebx-dump', 'ebx': VE, 'deep': True, 'out': probe}, cli.save(pre)], check=False)
    if not os.path.exists(probe) or not os.path.exists(pre):
        raise StageError('The game sky "%s" could not be copied - is the asset path right? (Pick one from the list, or type a '
                         'VisualEnvironmentBlueprint path the game ships.)  [log: %s]' % (asset, r.log_path))
    with open(probe, encoding='utf-8') as f:
        cd = json.load(f)
    si, sc = _find(cd, 'SkyComponentData')
    oi, oc = _find(cd, 'OutdoorLightComponentData')
    ti, tc = _find(cd, 'TonemapComponentData')
    if sc is None or oc is None:
        raise StageError('"%s" has no sky or no outdoor light: it is not a full sky environment. Pick another game sky.' % asset)
    pano_asset = (sc.get('PanoramicTexture') or {}).get('__path')
    lscale = float(sc.get('LuminanceScale') or 1.0)
    # the sky the bake lights with: the shipped panorama in nits, in the level's sky file units (texel x LSCALE = nits)
    d = job.path('layers', 'sky')
    os.makedirs(d, exist_ok=True)
    pf, raw = d + '/sky_pano.hdr', d + '/game_sky_raw.hdr'
    if os.path.exists(raw):
        os.remove(raw)
    rx = clix.run('look_sky_export', [{'op': 'export', 'ebx': pano_asset, 'out': raw, 'format': 'hdr'}], check=False) if pano_asset else None
    if not os.path.exists(raw) or os.path.getsize(raw) == 0:
        full = rx is not None and 'not enough space' in rx.text.lower()
        raise StageError('The sky picture of "%s" (%s) could not be exported for the light bake%s.' % (
            asset, pano_asset, ': the work folder\'s drive is FULL - free some space and build again' if full else ''))
    pano = _resample(hdr.read(raw).astype(np.float32)[..., :3], PH, PW) * lscale
    hdr.write(pf, pano / LSCALE)
    sun = L.get('sun') or {}
    az = el = None
    if sun.get('dir') and L.get('sun_from_scene', True):
        dx, dy, dz = sun['dir']
        gx, gy, gz = -dx, -dz, dy
        az = math.degrees(math.atan2(gx, gz)) % 360.0
        el = math.degrees(math.asin(max(-1.0, min(1.0, gy / max(math.sqrt(gx * gx + gy * gy + gz * gz), 1e-9)))))

    def s(obj, prop, value):
        return {'op': 'ebx-set', 'ebx': VE, 'object': obj, 'prop': prop, 'value': value, 'on-error': 'continue'}
    steps = [cli.load(pre),
             {'op': 'bundle-add', 'ebx': VE, 'bundle': B, 'with_resources': True, 'on-error': 'continue'}]
    # everything the shipped environment points at (its textures, curves, class + schematics) goes in the level's bundle
    refs = sorted(_refs(cd, set()) | {asset, asset.lower() + '_class', asset.lower() + '_class_schematics'})
    steps += [{'op': 'bundle-add', 'ebx': p, 'bundle': B, 'with_resources': True, 'on-error': 'continue'} for p in refs]
    if az is not None:
        steps += [s('OutdoorLightComponentData', 'SunRotationX', az), s('OutdoorLightComponentData', 'SunRotationY', el),
                  s('OutdoorLightComponentData', 'ShadowSunRotationX', az), s('OutdoorLightComponentData', 'ShadowSunRotationY', el)]
    # the colour grade: a level's own grading LUT is a small 3D texture whose data the level's bundle holds inline - the
    # bundle copy brings its header but not its data, and a LUT without data turns EVERY pixel black (Naboo cloudy,
    # 2026-10-23: all shots 0; with grading off the picture was there). The game-wide base grade is always loaded.
    if not L.get('game_sky_cloud_shadows', False):
        # the shipped cloud-shadow texture is laid over the SHIPPED level's own map; over a ported map it shaded the sun
        # out everywhere (Naboo cloudy: no sun-lit side, no shadow anywhere - the old sky code had it off too)
        steps.append(s('OutdoorLightComponentData', 'CloudShadowEnable', False))
    if L.get('game_sky_grading') is False:
        steps.append(s('ColorCorrectionComponentData', 'ColorGradingEnable', False))
    elif _find(cd, 'ColorCorrectionComponentData')[1] is not None:
        steps.append({'op': 'ebx-set-ref', 'ebx': VE, 'object': 'ColorCorrectionComponentData', 'prop': 'HdrColorGradingLut',
                      'target': BASE_GRADE, 'on-error': 'continue'})
    comp0 = float((tc or {}).get('ExposureCompensation') or 0.0)
    interior = L.get('exposure_mode') == 'interior'
    if interior and tc is not None:
        # an enclosed map: the exposure the shipped INTERIOR environments use (Hoth 8.5-10.5 +1.5, Kamino 8-10 +1.5,
        # Naboo 8-10 +1, Yavin hangar 8.5-12 +0.5) instead of the open sky's 12.5-15 - indoors the sky is not what lights you
        lo, hi, comp0 = INTERIOR_EXPOSURE
        steps += [s('TonemapComponentData', 'AutomaticExposure', True), s('TonemapComponentData', 'MinEV', lo),
                  s('TonemapComponentData', 'MaxEV', hi)]
    if (float(L.get('exposure', 0.0)) or interior) and tc is not None:
        steps.append(s('TonemapComponentData', 'ExposureCompensation', comp0 + float(L.get('exposure', 0.0))))
    for lay in host.VE_REF_LAYERS:
        a = job.level if lay == '' else job.level_dir + '/' + lay
        steps.append({'op': 'ebx-set-ref', 'ebx': a, 'object': 'VisualEnvironmentReferenceObjectData', 'prop': 'Blueprint', 'target': VE, 'on-error': 'continue'})
    rb = job.path('dumps', 'look_ve.json')
    steps += [{'op': 'ebx-dump', 'ebx': VE, 'deep': True, 'out': rb}, cli.save(out)]
    r = clix.run('look', steps, check=False)
    if not os.path.exists(out):
        raise StageError('The game sky could not be written  [log: %s]' % r.log_path)
    os.remove(pre)
    with open(rb, encoding='utf-8') as f:
        vd = json.load(f)
    _, sky_c = _find(vd, 'SkyComponentData'); _, od = _find(vd, 'OutdoorLightComponentData'); _, tm = _find(vd, 'TonemapComponentData')
    got = {'pano': ((sky_c or {}).get('PanoramicTexture') or {}).get('__path'), 'sun': (od or {}).get('SunIntensity'),
           'sun_rot': [(od or {}).get('SunRotationX'), (od or {}).get('SunRotationY')], 'auto_exposure': (tm or {}).get('AutomaticExposure'),
           'min_max_ev': [(tm or {}).get('MinEV'), (tm or {}).get('MaxEV')], 'compensation': (tm or {}).get('ExposureCompensation')}
    bad = []
    if (got['pano'] or '').lower() != (pano_asset or '').lower():
        bad.append('sky panorama reads back "%s"' % got['pano'])
    if az is not None and (got['sun_rot'][0] is None or abs(float(got['sun_rot'][0]) - az) > 0.01):
        bad.append('sun direction reads back %s' % got['sun_rot'])
    _, cc = _find(vd, 'ColorCorrectionComponentData')
    lut = ((cc or {}).get('HdrColorGradingLut') or {}).get('__path')
    got['grading_lut'] = lut
    if cc is not None and L.get('game_sky_grading') is not False and (lut or '').lower() != BASE_GRADE.lower():
        bad.append('the colour grade reads back "%s", not the game-wide one (a level LUT without its data turns the screen black)' % lut)
    if bad:
        raise StageError('The game sky does not read back as planned: %s  [log: %s]' % ('; '.join(bad), r.log_path))
    hor = pano[-12:].mean((0, 1))
    info = {'sky': 'game', 'game_sky': asset, 've': VE, 'donor': asset, 'pano': pano_asset, 'luminance_scale': lscale,
            'horizon_nits': [round(float(x), 1) for x in hor], 'zenith_nits': [round(float(x), 1) for x in pano[:40].mean((0, 1))],
            'sun_on': float(got['sun'] or 0) > 0, 'sun_intensity': got['sun'], 'sun_from_scene': az is not None,
            'sun_az_el': [round(az, 1), round(el, 1)] if az is not None else got['sun_rot'], 'ev': got['min_max_ev'],
            'exposure_compensation': got['compensation'], 'auto_exposure': got['auto_exposure'], 'bundled_refs': len(refs),
            'match': None, 'notes': [], 'reflection': None, 'probes': [], 'readback': got, 'failed_steps': len(r.errors)}
    with open(job.work + '/look.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    log('look: game sky %s - sun %s, exposure auto %s..%s +%s, horizon %.0f nits%s; %d step(s) reported errors' % (
        asset.split('/')[-1], got['sun'], got['min_max_ev'][0], got['min_max_ev'][1], got['compensation'], float(hor @ LUM),
        ', sun direction from the scene' if az is not None else '', len(r.errors)))
    return {'sky': 'game', 'ev': got['min_max_ev'], 'horizon_nits': info['horizon_nits'], 'sun_on': info['sun_on'], 'failed_steps': len(r.errors)}


def run(job, log):
    host = hosts.get(job.host)
    clix = cli.Cli(job, log)
    L = job.map.get('look') or {}
    src, out = chain.base(job, 'look'), job.project('look')
    sky = L.get('sky') or 'overcast'
    if sky == 'game':
        return run_game(job, log, host, clix, L, src, out)
    match = None                                        # the photometric units of a build lit from the scene itself
    notes = []
    if sky == 'world' and not photometry.matched(job):
        notes.append('the scene has no World to take the sky from: the overcast preset is used')
        sky = 'overcast'
    if sky == 'world':
        world = hdr.read(L['world_hdr'])
        mc = L.get('match_constants') or {}
        wp = world_pano(world, float(mc.get('PANO_SIGN', PANO_AZ_SIGN)), float(mc.get('PANO_OFFSET', PANO_AZ_OFFSET)))
        match = photometry.units(job)
        pano = wp * photometry.sky_nits(match) * float(L.get('sky_strength', 1.0))
        ev0, sunf = match['ev'], 1.0
    elif sky == 'hdr':
        f = L.get('sky_hdr') or ''
        if not f or not os.path.exists(f):
            raise StageError('The sky is set to "HDR panorama file" but the file was not found: "%s".' % f)
        if not f.lower().endswith('.hdr'):
            raise StageError('The sky panorama must be a Radiance .hdr file ("%s" is not). Save it as .hdr from Blender or any image tool.' % os.path.basename(f))
        pano, ev0, sunf = file_sky(f, L.get('sky_strength', 1.0))
    elif sky in PRESETS:
        pano, ev0, sunf = preset_sky(sky)
        pano *= float(L.get('sky_strength', 1.0))
    else:
        raise StageError('Unknown sky preset "%s".' % sky)
    grad, hor = gradient(pano)
    d = job.path('layers', 'sky')
    os.makedirs(d, exist_ok=True)
    pf, gf = d + '/sky_pano.hdr', d + '/sky_grad.hdr'
    hdr.write(pf, pano / LSCALE)
    # Matched to the scene: the gradient is written black. The engine shows panorama + gradient: with both, the sky
    # straight up read 1.97 x Blender's (LightMatch lm1 and lm2) while everything the sky LIGHTS was right.
    # lm3 with the gradient black: the sky straight up read 0.59 x Blender's. 0.3 of it puts the zenith near 1.0;
    # the sky still shows greyer than Blender's - open.
    hdr.write(gf, (grad * (0.3 if match else 1.0)) / LSCALE)
    ev = ev0 if match else ev0 + float(L.get('exposure', 0.0))
    fog = L.get('fog') or {}
    fog_rgb = np.asarray(fog.get('color', (0.5, 0.55, 0.6)), float) * float(hor @ LUM)
    sun_on = bool(L.get('sun_on', True)) and sunf > 0
    sun = L.get('sun') or {}
    az, el = -140.0, 30.0
    if sun.get('dir'):
        dx, dy, dz = sun['dir']                                     # the lamp shines along dir: the sun is at -dir
        gx, gy, gz = -dx, -dz, dy                                   # scene (x, y, z) -> game (x, z, -y), negated
        az = math.degrees(math.atan2(gx, gz)); el = math.degrees(math.asin(max(-1.0, min(1.0, gy / max(math.sqrt(gx * gx + gy * gy + gz * gz), 1e-9)))))
    VE = job.prefix + '/Lighting/VE_%s_01' % job.name
    PANO, GRAD = job.prefix + '/Lighting/T_%s_Sky_Pano' % job.name, job.prefix + '/Lighting/T_%s_Sky_Grad' % job.name
    B = job.bundle
    # 1 clone + dump (indices of the two components whose override lists get a new entry are read, not assumed)
    probe = job.path('dumps', 'look_clone.json')
    pre = job.project('look_0')
    clix.run('look_clone', [cli.load(src), {'op': 'ebx-clone', 'as': VE, 'source': DONOR},
                            {'op': 'ebx-dump', 'ebx': VE, 'deep': True, 'out': probe}, cli.save(pre)])
    with open(probe, encoding='utf-8') as f:
        cd = json.load(f)
    si, sc = _find(cd, 'SkyComponentData')
    hi, shc = _find(cd, 'ShadowsComponentData')
    oi, oc = _find(cd, 'OutdoorLightComponentData')
    if sc is None or oc is None:
        raise StageError('The donor environment has no sky / outdoor light component (dump %s).' % probe)
    donor_sun = float(oc.get('SunIntensity', 1.0))
    sun_rgb, sun_int = (1.0, 1.0, 1.0), (donor_sun * sunf if sun_on else donor_sun * 0.0001)
    ev_lo, ev_hi, disc, extra = ev - 0.5, ev + 1.0, (1.0 if sun_on else 0.0), []
    if match:
        # the sun lamp's own strength and colour; no sun lamp (or the Sun box unticked) = no sun
        c = np.clip(np.asarray((sun.get('color') or (1.0, 1.0, 1.0))[:3], float), 0.0, None)
        cm = max(float(c.max()), 1e-6)
        strength = float(sun.get('energy', 0.0)) * cm if (sun and L.get('sun_on', True)) else 0.0
        sun_on = strength > 0.0
        sun_rgb = tuple(float(x) for x in c / cm)
        sun_int = max(photometry.sun_lux(strength, match), 0.001)
        ev_lo = ev_hi = ev                               # the exposure is the scene's, not the game's meter
        disc = 0.0                                       # a Blender sun lamp shows no disc; the World panorama holds what the sky shows

    def s(obj, prop, value):
        return {'op': 'ebx-set', 'ebx': VE, 'object': obj, 'prop': prop, 'value': value}

    def sr(obj, prop, target):
        return {'op': 'ebx-set-ref', 'ebx': VE, 'object': obj, 'prop': prop, 'target': target}
    if match:
        extra = [s('ColorCorrectionComponentData', 'ColorGradingEnable', False),      # the donor level's grading is not the scene's
                 s('TonemapComponentData', 'AutomaticExposure', False)]
        if sun.get('angle'):
            extra.append(s('OutdoorLightComponentData', 'SunAngularRadius', min(max(math.degrees(float(sun['angle'])) / 2.0, 0.05), 3.0)))   # the game's shadows stay sharp whatever this is (rf7 / rf8); kept small for the highlights
    steps = [cli.load(pre),
             {'op': 'bundle-add', 'ebx': VE, 'bundle': B, 'with_resources': True, 'on-error': 'continue'},
             {'op': 'bundle-add', 'ebx': DONOR, 'bundle': B, 'with_resources': True, 'on-error': 'continue'},
             # the donor's class + class-schematics PAIR
             {'op': 'bundle-add', 'ebx': DONOR.lower() + '_class', 'bundle': B, 'with_resources': True, 'on-error': 'continue'},
             {'op': 'bundle-add', 'ebx': DONOR.lower() + '_class_schematics', 'bundle': B, 'with_resources': True, 'on-error': 'continue'},
             {'op': 'import-texture', 'file': pf, 'as': PANO, 'template': PANO_TPL},
             {'op': 'import-texture', 'file': gf, 'as': GRAD, 'template': GRAD_TPL},
             {'op': 'bundle-add', 'ebx': PANO, 'bundle': B, 'with_resources': True},
             {'op': 'bundle-add', 'ebx': GRAD, 'bundle': B, 'with_resources': True},
             sr('SkyComponentData', 'PanoramicTexture', PANO), sr('SkyComponentData', 'SkyGradientTexture', GRAD),
             s('SkyComponentData', 'PanoramicUVMinX', 0.0), s('SkyComponentData', 'PanoramicUVMaxX', 1.0),
             s('SkyComponentData', 'PanoramicUVMinY', 0.0), s('SkyComponentData', 'PanoramicUVMaxY', 1.0),
             s('SkyComponentData', 'SkyGradientFollowsPanoramicUVs', False), s('SkyComponentData', 'PanoramicRotation', 0.0),
             s('SkyComponentData', 'LuminanceScale', LSCALE), s('SkyComponentData', 'SunScale', disc),
             s('SkyComponentData', 'AlphaOutput', 'AlphaOutputMode_Clear')]
    if 'PanoramicAlphaTexture' not in (sc.get('PropertyOverrides') or []):
        n = len(sc.get('PropertyOverrides') or [])
        steps += [{'op': 'ebx-array-add', 'ebx': VE, 'prop': 'Object.Components[%d].PropertyOverrides' % si, 'type': 'CString'},
                  {'op': 'ebx-set', 'ebx': VE, 'prop': 'Object.Components[%d].PropertyOverrides[%d]' % (si, n), 'value': 'PanoramicAlphaTexture'}]
    steps += [sr('SkyComponentData', 'PanoramicAlphaTexture', ALPHA),
              {'op': 'bundle-add', 'ebx': ALPHA, 'bundle': B, 'with_resources': True, 'on-error': 'continue'},
              s('OutdoorLightComponentData', 'SunColor.x', sun_rgb[0]), s('OutdoorLightComponentData', 'SunColor.y', sun_rgb[1]), s('OutdoorLightComponentData', 'SunColor.z', sun_rgb[2]),
              s('OutdoorLightComponentData', 'SunIntensity', sun_int),
              s('OutdoorLightComponentData', 'SunRotationX', az), s('OutdoorLightComponentData', 'SunRotationY', el),
              s('OutdoorLightComponentData', 'ShadowSunRotationX', az), s('OutdoorLightComponentData', 'ShadowSunRotationY', el),
              s('OutdoorLightComponentData', 'CloudShadowEnable', False),
              s('FogComponentData', 'FogColorEnable', True),
              s('FogComponentData', 'FogColor.x', float(fog_rgb[0])), s('FogComponentData', 'FogColor.y', float(fog_rgb[1])), s('FogComponentData', 'FogColor.z', float(fog_rgb[2])),
              s('FogComponentData', 'Start', float(fog.get('start', 40.0))), s('FogComponentData', 'End', float(fog.get('end', 800.0))),
              s('FogComponentData', 'FogColorStart', float(fog.get('start', 40.0))), s('FogComponentData', 'FogColorEnd', float(fog.get('end', 800.0))),
              s('FogComponentData', 'Curve.x', 0.0), s('FogComponentData', 'Curve.y', 0.0), s('FogComponentData', 'Curve.z', float(fog.get('density', 0.3))),
              s('FogComponentData', 'Curve.w', 0.0), s('FogComponentData', 'ForwardLightScatteringEnabled', False),
              s('FogComponentData', 'FogGradientEnable', False), s('FogComponentData', 'HeightFogEnable', False),
              s('EnlightenComponentData', 'SkyBoxSkyColor.x', float(pano[..., 0].mean() * 0.3)), s('EnlightenComponentData', 'SkyBoxSkyColor.y', float(pano[..., 1].mean() * 0.3)),
              s('EnlightenComponentData', 'SkyBoxSkyColor.z', float(pano[..., 2].mean() * 0.3)), s('EnlightenComponentData', 'SunScale', 1.0 if sun_on else 0.0),
              s('TonemapComponentData', 'MinEV', ev_lo), s('TonemapComponentData', 'MaxEV', ev_hi), s('TonemapComponentData', 'EV', ev)] + extra
    for st in steps:
        if st.get('op') in ('ebx-set', 'ebx-set-ref') and 'on-error' not in st:
            st['on-error'] = 'continue'
    for lay in host.VE_REF_LAYERS:
        asset = job.level if lay == '' else job.level_dir + '/' + lay
        steps.append({'op': 'ebx-set-ref', 'ebx': asset, 'object': 'VisualEnvironmentReferenceObjectData', 'prop': 'Blueprint', 'target': VE, 'on-error': 'continue'})
    # the level's own reflection capture: one volume over the whole map, its cube made from the scene's own picture
    # (seen in game 2026-10-12: without it a ported level reflects only the sky, and indoor metals go dark)
    refl = None
    rf = (L.get('reflection') or {}) if match else {}
    rtpl = getattr(host, 'REFLECTION_TEXTURE', None)
    rlayer = job.level_dir + '/' + host.LIGHT_LAYER
    rdump = job.path('dumps', 'look_reflection.json')
    if rf.get('file') and os.path.exists(rf['file']) and rtpl:
        RTEX = '%s/Lighting/T_%s_Reflection_01' % (job.prefix, job.name)
        rdds = d + '/reflection.dds'
        mips = reflection.cube(hdr.read(rf['file']), match['D'])
        reflection.write_dds(rdds, mips)
        o = job.offset(); q = rf['point']
        rpos = (o[0] + q[0], o[1] + q[2], o[2] - q[1])           # scene (x, y, z) -> game (x, z, -y)
        size = max(400.0, 4.0 * float((job.map.get('play') or {}).get('radius') or 100.0))
        rsteps = [{'op': 'import-texture', 'file': rdds, 'as': RTEX, 'template': rtpl},
                  {'op': 'bundle-add', 'ebx': RTEX, 'bundle': B, 'with_resources': True},
                  {'op': 'level-add-entity', 'ebx': rlayer, 'type': 'PbrDistantReflectionVolumeEntityData', 'pos': '%.3f,%.3f,%.3f' % rpos,
                   'scale': '%g' % size,
                   'set': {'Mode': 'LocalIBLMode_Baked', 'LocationType': 'DistantIBLLocationType_Indoor', 'CaptureDistance': 20000.0,
                           'CaptureSky': True, 'CaptureSkyMask': True, 'UseSkyVisibilityAsAO': False, 'UseSkyVisibilityAsMask': False,
                           'Enabled': True, 'IsVisible': True, 'UpdateWhenMoving': False, 'DoNotUpdateBakedTexture': True}},
                  {'op': 'ebx-set-ref', 'ebx': rlayer, 'object': 'PbrDistantReflectionVolumeEntityData', 'prop': 'BakedTexture', 'target': RTEX},
                  {'op': 'ebx-dump', 'ebx': rlayer, 'deep': True, 'out': rdump}]
        for st in rsteps:
            st['on-error'] = 'continue'                           # reflections are an extra: the level is built without them too
        steps += rsteps
        refl = {'texture': RTEX, 'point_scene': [round(float(x), 2) for x in q], 'point_game': [round(float(x), 2) for x in rpos],
                'clearance_m': rf.get('clearance'), 'mean_nits': round(float(mips[0].mean()), 1), 'peak_nits': round(float(mips[0].max()), 1)}
    # extra Reflection probe objects: one sphere volume each (the host level's own kind for a room), its cube made from
    # the picture taken at the probe; the sphere's radius = the probe's drawn size. A point inside a sphere reflects
    # what the probe saw; elsewhere the map-wide capture above is used.
    probes = [p for p in (job.map.get('reflection_probes') or []) if p.get('file') and os.path.exists(p['file'])] if (match and rtpl) else []
    probe_plan = []
    if probes:
        o = job.offset()
        for k, pr in enumerate(probes):
            ptex = '%s/Lighting/T_%s_ReflectionProbe_%02d' % (job.prefix, job.name, k + 1)
            pdds = d + '/reflection_probe_%02d.dds' % (k + 1)
            pm = reflection.cube(hdr.read(pr['file']), match['D'])
            reflection.write_dds(pdds, pm)
            q = pr['pos']
            gp = (o[0] + q[0], o[1] + q[2], o[2] - q[1])
            r = float(pr.get('radius') or 10.0)
            ps = [{'op': 'import-texture', 'file': pdds, 'as': ptex, 'template': rtpl},
                  {'op': 'bundle-add', 'ebx': ptex, 'bundle': B, 'with_resources': True},
                  {'op': 'level-add-entity', 'ebx': rlayer, 'type': 'PbrSphereReflectionVolumeEntityData', 'pos': '%.3f,%.3f,%.3f' % gp,
                   'scale': '%g' % r,
                   'set': {'Mode': 'LocalIBLMode_Baked', 'CaptureDistance': max(3.0 * r, 20.0), 'CaptureSky': True, 'CaptureSkyMask': True,
                           'UseSkyVisibilityAsAO': False, 'UseSkyVisibilityAsMask': False, 'Enabled': True, 'IsVisible': True,
                           'UpdateWhenMoving': False, 'DoNotUpdateBakedTexture': True}},
                  {'op': 'ebx-set-ref', 'ebx': rlayer, 'object': 'PbrSphereReflectionVolumeEntityData[%d]' % k, 'prop': 'BakedTexture', 'target': ptex}]
            for st in ps:
                st['on-error'] = 'continue'
            steps += ps
            probe_plan.append({'name': pr['name'], 'texture': ptex, 'point_game': [round(float(x), 2) for x in gp], 'radius': r,
                               'mean_nits': round(float(pm[0].mean()), 1)})
        if refl is None:                                      # the readback dump below is otherwise only made for the main capture
            steps.append({'op': 'ebx-dump', 'ebx': rlayer, 'deep': True, 'out': rdump, 'on-error': 'continue'})
        else:
            steps.remove(next(st for st in steps if st.get('op') == 'ebx-dump' and st.get('out') == rdump))
            steps.append({'op': 'ebx-dump', 'ebx': rlayer, 'deep': True, 'out': rdump, 'on-error': 'continue'})
    rb = job.path('dumps', 'look_ve.json')
    steps += [{'op': 'ebx-dump', 'ebx': VE, 'deep': True, 'out': rb}, cli.save(out)]
    r = clix.run('look', steps, check=False)
    if not os.path.exists(out):
        raise StageError('The visual environment could not be written  [log: %s]' % r.log_path)
    os.remove(pre)
    with open(rb, encoding='utf-8') as f:
        vd = json.load(f)
    _, sky_c = _find(vd, 'SkyComponentData'); _, tm = _find(vd, 'TonemapComponentData'); _, fg = _find(vd, 'FogComponentData')
    _, od = _find(vd, 'OutdoorLightComponentData')
    got = {'pano': ((sky_c or {}).get('PanoramicTexture') or {}).get('__path'), 'LuminanceScale': (sky_c or {}).get('LuminanceScale'),
           'EV': (tm or {}).get('EV'), 'fog_end': (fg or {}).get('End'), 'sun': (od or {}).get('SunIntensity'),
           'alpha_override': 'PanoramicAlphaTexture' in ((sky_c or {}).get('PropertyOverrides') or [])}
    if refl is not None:
        back = None
        if os.path.exists(rdump):
            with open(rdump, encoding='utf-8') as f:
                for ob in (json.load(f).get('Objects') or []):
                    if isinstance(ob, dict) and ob.get('__type') == 'PbrDistantReflectionVolumeEntityData':
                        back = ((ob.get('BakedTexture') or {}).get('__path') or '')
        refl['in_level'] = bool(back) and back.lower() == refl['texture'].lower()
        if not refl['in_level']:
            notes.append('the reflection volume did not read back (%s): the level reflects only the sky' % back)
    if probe_plan:
        spheres = []
        if os.path.exists(rdump):
            with open(rdump, encoding='utf-8') as f:
                for ob in (json.load(f).get('Objects') or []):
                    if isinstance(ob, dict) and ob.get('__type') == 'PbrSphereReflectionVolumeEntityData':
                        t = ob.get('Transform') or {}
                        spheres.append({'texture': ((ob.get('BakedTexture') or {}).get('__path') or '').lower(),
                                        'pos': [round(float((t.get('trans') or {}).get(a, 0.0)), 2) for a in 'xyz'],
                                        'radius': round(float(sum(float((t.get('right') or {}).get(a, 0.0)) ** 2 for a in 'xyz') ** 0.5), 2)})
        for p in probe_plan:
            hit = next((s for s in spheres if s['texture'] == p['texture'].lower()), None)
            p['in_level'] = bool(hit) and max(abs(a - b) for a, b in zip(hit['pos'], p['point_game'])) < 0.05 and abs(hit['radius'] - p['radius']) < 0.05
            if not p['in_level']:
                notes.append('reflection probe "%s" did not read back as planned (%s): that room reflects the map-wide capture' % (p['name'], hit))
    info = {'sky': sky, 've': VE, 'donor': DONOR, 'ev': round(ev, 2), 'horizon_nits': [round(float(x), 2) for x in hor],
            'zenith_nits': [round(float(x), 2) for x in pano[:40].mean((0, 1))], 'fog_colour': [round(float(x), 2) for x in fog_rgb],
            'sun_on': sun_on, 'sun_az_el': [round(az, 1), round(el, 1)], 'sun_intensity': sun_int if sun_on else 0.0, 'sun_colour': [round(x, 4) for x in sun_rgb],
            'match': match, 'notes': notes, 'reflection': refl, 'probes': probe_plan,
            'donor_sun_intensity': donor_sun, 'readback': got, 'failed_steps': len([e for e in r.errors]), 'graded': 'see match.grades' if match else 'UNKNOWN (preset numbers provisional)'}
    with open(job.work + '/look.json', 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    bad = []
    if (got['pano'] or '').lower() != PANO.lower():
        bad.append('sky panorama reads back "%s"' % got['pano'])
    if got['EV'] is None or abs(float(got['EV']) - ev) > 0.01:
        bad.append('EV reads back %s, planned %.2f' % (got['EV'], ev))
    if got['LuminanceScale'] is None or abs(float(got['LuminanceScale']) - LSCALE) > 0.01:
        bad.append('LuminanceScale reads back %s' % got['LuminanceScale'])
    if not got['alpha_override']:
        bad.append('the PanoramicAlphaTexture override is not listed')
    if bad:
        raise StageError('The visual environment does not read back as planned: %s  [log: %s]' % ('; '.join(bad), r.log_path))
    log('look: sky "%s", horizon %.1f nits, EV %.2f, fog %s .. %s m, sun %s (az %.0f, el %.0f); %d step(s) reported errors' % (
        sky, float(hor @ LUM), ev, fog.get('start', 40.0), fog.get('end', 800.0), 'on' if sun_on else 'off', az, el, len(r.errors)))
    return {k: info[k] for k in ('sky', 'ev', 'horizon_nits', 'sun_on', 'failed_steps')}
