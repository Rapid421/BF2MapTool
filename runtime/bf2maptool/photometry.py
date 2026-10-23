"""Blender light values -> game light values, for the "Blender World (match the scene)" mode.

One rule for everything that gives light, so the BALANCE between sun, sky, lamps and glowing surfaces is Blender's:

    D = nits per Blender scene-linear unit of radiance  =  1.2 x 2^EV x WHITE x 2^(Blender exposure)

  sky        panorama texel (nits)        = World radiance x D
  sun        illuminance (lux)            = sun Strength (W/m2) x D            [a white wall facing the sun: S / pi in
                                                                                Blender, lux / pi nits in game]
  point      luminous power (lm)          = Power (W) x D / pi                 [Blender: I = P / (4 pi^2) W/sr; game: I = lm / (4 pi)]
  spot       luminous power (lm)          = Power (W) x D / (4 pi)             [Blender spot = a point lamp behind a cone; game: I = lm / pi]
  area       luminous power (lm)          = Power (W) x D / 4                  [Blender: L = P / (4 pi A); game: L = lm / (pi A)]
  emission   surface luminance (nits)     = Emission colour x Strength x D

EV is the game's exposure for the level (photographic EV at ISO 100: a surface of 1.2 x 2^EV nits is "white"). Because
every light is multiplied by the same D, the picture does not depend on which EV is chosen; EV only places the level
among the game's own effects (blaster bolts, explosions, markers), which have fixed brightness. `ev_level` picks it.

The right-hand formulas are how Blender and a physically based engine DEFINE those lamps. Whether this engine follows
them is MEASURED on the light-match scene (tests/lightmatch): each constant below is 1.0 by the formula and is replaced
by the measured correction; `GRADES` says which of them have been measured.
"""
import math
import os

EV_LEVELS = {'day': 13.0, 'overcast': 11.5, 'dusk': 9.0, 'interior': 7.0, 'night': 3.0}
# measured corrections (game / formula). 1.0 = the formula as it stands.
WHITE = 2.08         # measured (LightMatch lm1): at 1.0 the sunlit grey ramp showed at 0.48 of Blender's Standard display
C_SKY = 1.0
C_SUN = 1.0
# real-time lamps, measured in near-black rooms (LightMatch lm2): by the formula a 100 W point lamp lit its patch at 0.109
# of Blender's radiance, a spot at 0.133, an area lamp at 0.178 (all three readings just under the grey ramp's range)
# round 3 (lm3, readings ON the ramp): with 9.2 / 7.5 / 5.6 the three showed at 2.35 / 2.02 / 1.29 of Blender's
C_POINT = 4.6
C_SPOT = 4.1
C_AREA = 4.5
C_EMISSION = 1.0     # the emissive template's multiplier per nit
C_EMISSION_BAKE = 1.19   # a glowing surface in the light bake (lm4: its room read 0.84 of Blender)
# The scene's view transform. WHITE was measured against Standard; the game's own tone curve then shows the sunlit grey
# ramp (sRGB red channel, reflectance 0.02 .. 0.8) as 55 74 97 125 152 174 188 205, where Blender shows
#   Standard 42 68 95 125 158 190 216 246, AgX 35 63 93 125 152 172 183 194, Filmic 44 75 106 134 160 179 191 203,
#   Khronos PBR Neutral 11 40 78 114 150 184 207 216      (tests/lightmatch/view_transforms.json, lm2_result.json)
# i.e. the game's curve is AgX's from mid grey up. A factor per transform puts the mid greys together.
VIEW_WHITE = {'Standard': 1.0, 'AgX': 1.0, 'Filmic': 1.12, 'Khronos PBR Neutral': 0.85}
GRADES = {'sky': 'UNKNOWN', 'sun': 'UNKNOWN', 'point': 'UNKNOWN', 'spot': 'UNKNOWN', 'area': 'UNKNOWN', 'emission': 'UNKNOWN',
          'white': 'UNKNOWN'}


def matched(job):
    """is this build lit from the scene's own World and light values?"""
    L = job.map.get('look') or {}
    f = L.get('world_hdr') or ''
    return L.get('sky') == 'world' and bool(f) and os.path.exists(f)


def ev_level(job, sky_display_median=None):
    """(EV, how it was chosen)"""
    L = job.map.get('look') or {}
    lvl = L.get('ev_level') or 'auto'
    if lvl in EV_LEVELS:
        return EV_LEVELS[lvl], lvl
    sun = L.get('sun') or {}
    if L.get('sun_on', True) and sun and float(sun.get('energy', 0.0)) > 0.0:
        return EV_LEVELS['day'], 'auto: the scene has a sun'
    m = sky_display_median
    if m is None:
        return EV_LEVELS['overcast'], 'auto: no sun'
    if m > 0.3:
        return EV_LEVELS['overcast'], 'auto: no sun, bright sky'
    if m > 0.02:
        return EV_LEVELS['dusk'], 'auto: no sun, dim sky'
    return EV_LEVELS['night'], 'auto: no sun, dark sky'


_MEDIAN = {}


def sky_median(job):
    """median radiance of the World's upper half x 2^(Blender exposure): how bright the sky is on Blender's screen"""
    L = job.map.get('look') or {}
    f = L.get('world_hdr')
    if f not in _MEDIAN:
        import numpy as np
        from . import hdr
        a = hdr.read(f)
        _MEDIAN[f] = float(np.median(a[: a.shape[0] // 2] @ np.array([0.2126, 0.7152, 0.0722])))
    return _MEDIAN[f] * 2.0 ** float((L.get('blender') or {}).get('exposure', 0.0))


def _sky_factor(job):
    """a game sky settles at its own exposure: glows are scaled by 2^(EV - the EV the host numbers were set at), so a
    panel that reads as glowing at EV 11.5 still does at Tatooine noon (12.5) or Kashyyyk night (6.75)"""
    from .stages import look as look_stage
    ev = look_stage.settled_ev(job.map.get('look') or {})
    return 1.0 if ev is None else 2.0 ** (ev - look_stage.EV_REFERENCE)


def emissive_per_unit(job, host):
    """the emissive surface multiplier per unit of Blender emission strength"""
    v = (job.map.get('look') or {}).get('emissive_scale')
    if v:
        return float(v)
    if matched(job):
        U = units(job)
        return emission_nits(1.0, U)
    return float(host.EMISSIVE_PER_UNIT) * _sky_factor(job)


def emission_bake_per_unit(job, host):
    """radiance (nits) a glowing surface gives the light bake per unit of Blender emission strength"""
    v = (job.map.get('look') or {}).get('emissive_scale')
    if v:
        return float(v)
    if matched(job):
        return units(job)['D'] * C_EMISSION_BAKE
    return float(host.EMISSIVE_PER_UNIT) * _sky_factor(job)


def units(job, sky_display_median=None, const=None):
    """-> {'ev', 'ev_from', 'D', 'blender_exposure', ...} for a matched build"""
    c = dict(WHITE=WHITE, C_SKY=C_SKY, C_SUN=C_SUN, C_POINT=C_POINT, C_SPOT=C_SPOT, C_AREA=C_AREA, C_EMISSION=C_EMISSION)
    c.update(const or {})
    c.update((job.map.get('look') or {}).get('match_constants') or {})      # a calibration build may set them
    L = job.map.get('look') or {}
    bl = L.get('blender') or {}
    bexp = float(bl.get('exposure', 0.0))
    if sky_display_median is None and matched(job):
        sky_display_median = sky_median(job)
    ev, how = ev_level(job, sky_display_median)
    ev_game = ev - float(L.get('exposure', 0.0))                # the add-on's Exposure: + is brighter
    vw = float(VIEW_WHITE.get(bl.get('view_transform') or 'Standard', 1.0))
    D = 1.2 * 2.0 ** ev * c['WHITE'] * vw * 2.0 ** bexp
    return {'ev': ev_game, 'ev_level': ev, 'ev_from': how, 'D': D, 'blender_exposure': bexp, 'constants': c, 'view_white': vw,
            'view_transform': bl.get('view_transform'), 'grades': dict(GRADES)}


def sun_lux(strength, U):
    return float(strength) * U['D'] * U['constants']['C_SUN']


def sky_nits(U):
    return U['D'] * U['constants']['C_SKY']


def lamp_lumens(kind, watts, U):
    c = U['constants']
    if kind == 'point':
        return float(watts) * U['D'] / math.pi * c['C_POINT']
    if kind == 'spot':
        return float(watts) * U['D'] / (4.0 * math.pi) * c['C_SPOT']
    if kind == 'area':
        return float(watts) * U['D'] / 4.0 * c['C_AREA']
    raise ValueError(kind)


def lamp_candela(kind, lumens):
    """peak luminous intensity of a game lamp of that many lumens"""
    return lumens / (4.0 * math.pi) if kind == 'point' else lumens / math.pi


def emission_nits(strength, U):
    return float(strength) * U['D'] * U['constants']['C_EMISSION']


def reach(candela, U, floor=0.004):
    """metres at which a lamp adds less than `floor` of display white to a white wall (the engine cuts a lamp off at a
    radius; Blender does not): E = I / d^2 lux, wall luminance E / pi, white = 1.2 x 2^EV x 2^(Blender exposure) nits"""
    white = 1.2 * 2.0 ** U['ev_level']
    return math.sqrt(max(candela, 1e-9) / (math.pi * floor * white))
