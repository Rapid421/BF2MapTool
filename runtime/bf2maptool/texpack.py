"""Channel packer: Principled-style material inputs -> the packed textures Battlefront II's shaders read.

Layouts (what every channel of a packed texture means; the block format, sRGB flag and mip chain are NOT decided here -
`import-texture` copies them from the shipped template texture of the slot):

  CS    rgb = base colour (sRGB bytes)          a = smoothness = 1 - roughness
  NAM   r, g = normal x, y (green UP, see below) b = the slot's constant (texture-array slice)   a = metal
  NW    r, g = normal x, y (green up)            b = the slot's constant                         a = cut-out mask
  E     rgb = emission colour (sRGB bytes)       a = 255

Normal maps are packed with GREEN UP (the OpenGL / Blender convention): seen in game 2026-10-12 on the tool's own
calibration slabs (raised domes, sun from the upper right): with green up they read as raised domes, bright towards
the sun, on a plain AND on a mirrored-UV face; the same map with green flipped reads as dents. (The earlier ports
flipped OpenGL maps to DirectX; that was never looked at with relief in game.) This holds for meshes that come in
through the tool's own piece FBX files - the tangent frame the importer stores is part of the result.

An input is ``{'value': v}`` or ``{'image': {'file': path}, 'channel': 'RGB'|'R'|'G'|'B'|'A', 'invert', 'power',
'scale'}`` exactly as the scene export writes it. `pack()` and `unpack()` are inverse for every layout (tests/).
"""
import hashlib
import os

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None
LAYOUTS = ('CS', 'NAM', 'NW', 'E')
MAX_SIZE = 4096


class PackError(Exception):
    """a material cannot be packed; .code names the check, the message is for the user"""

    def __init__(self, code, message):
        Exception.__init__(self, message)
        self.code = code


# Side of a texture that holds one flat value. Not 4: in game a 4 x 4 texture shades wrongly wherever a smaller mip is
# sampled (Veranda, 2026-10-12: flat-colour trims went teal / magenta by viewing angle; the same textures at
# 64 x 64 are right - review/rf5). The files themselves decode correctly, so the fault is in how the game reads
# the mips of a one-block texture.
FLAT_SIZE = 64

def srgb_encode(lin):
    lin = np.clip(lin, 0.0, 1.0)
    return np.where(lin <= 0.0031308, lin * 12.92, 1.055 * np.power(lin, 1 / 2.4) - 0.055)


def is_pow2(n):
    return n > 0 and (n & (n - 1)) == 0


class Source:
    """image files read once per build"""

    def __init__(self, resolve=lambda p: p):
        self.resolve = resolve
        self.cache = {}

    def rgba(self, rec):
        img = rec.get('image') or {}
        if img.get('missing') or not img.get('file'):
            raise PackError('tex.missing', 'image "%s" is missing (not packed in the .blend and not found on disk)' % img.get('name', '?'))
        path = self.resolve(img['file'])
        if path not in self.cache:
            if not os.path.exists(path):
                raise PackError('tex.missing', 'image file not found: %s' % path)
            im = Image.open(path)
            has_alpha = im.mode in ('RGBA', 'LA', 'PA') or 'transparency' in im.info
            self.cache[path] = (np.asarray(im.convert('RGBA')), has_alpha)
        return self.cache[path]


def _scalar(rec, src, size, default):
    """one input as a float plane (H, W) in 0..1"""
    w, h = size
    if rec is None:
        return np.full((h, w), float(default), np.float32)
    if 'value' in rec:
        v = rec['value']
        v = float(v[0] if isinstance(v, (list, tuple)) else v)
        a = np.full((h, w), v, np.float32)
    else:
        px, _ = src.rgba(rec)
        ch = rec.get('channel', 'RGB')
        if ch == 'RGB':
            p = px[..., :3].astype(np.float32).mean(-1) if not (px[..., 0] == px[..., 1]).all() else px[..., 0].astype(np.float32)
        else:
            p = px[..., 'RGBA'.index(ch)].astype(np.float32)
        a = _fit(p, size) / 255.0
    return _transform(a, rec)


def _transform(a, rec):
    if rec.get('power', 1.0) != 1.0:
        a = np.power(np.clip(a, 0.0, None), rec['power'])
    if rec.get('scale', 1.0) != 1.0:
        a = a * rec['scale']
    if rec.get('invert'):
        a = 1.0 - a
    return np.clip(a, 0.0, 1.0)


def _fit(plane, size):
    """resample a plane (H, W[, C]) to (w, h) when its resolution differs (same aspect is checked by the caller)"""
    w, h = size
    if plane.shape[1] == w and plane.shape[0] == h:
        return plane.astype(np.float32)
    if plane.ndim == 2:
        return np.asarray(Image.fromarray(plane.astype(np.float32), 'F').resize((w, h), Image.LANCZOS), np.float32)
    return np.dstack([np.asarray(Image.fromarray(plane[..., i].astype(np.float32), 'F').resize((w, h), Image.LANCZOS), np.float32)
                      for i in range(plane.shape[2])])


def _colour(rec, src, size, default=(1.0, 1.0, 1.0)):
    """a colour input as sRGB bytes-scale floats (H, W, 3) 0..255"""
    w, h = size
    if rec is None or 'value' in rec:
        v = (rec or {}).get('value', default)
        v = [float(x) for x in (v if isinstance(v, (list, tuple)) else (v, v, v))][:3]
        a = np.empty((h, w, 3), np.float32)
        a[:] = srgb_encode(np.array(v)) * 255.0          # node constants are scene-linear
        return a
    px, _ = src.rgba(rec)
    a = _fit(px[..., :3], size)
    if rec.get('scale', 1.0) != 1.0:
        a = a * rec['scale']
    return np.clip(a, 0.0, 255.0)


TARGET = 'opengl'            # the green direction the packed normal maps are written with


def normal_convention(n_rg):
    """Integrability of a tangent-space normal map: a real height field h has n.x ~ -dh/dx and (OpenGL) n.y ~ -dh/dy
    with y up the image, so d(n.x)/dy and d(n.y)/dx carry the same sign pattern; a DirectX map (green down) the opposite.
    -> (correlation, 'opengl' | 'directx' | 'flat' | 'unclear'); > 0 OpenGL, < 0 DirectX."""
    r = n_rg[..., 0].astype(np.float32) / 255.0 - 0.5
    g = n_rg[..., 1].astype(np.float32) / 255.0 - 0.5
    a = np.gradient(-r, axis=0)
    b = np.gradient(g, axis=1)
    m = (np.abs(a) + np.abs(b)) > 1e-3
    if m.sum() < 100:
        return 0.0, 'flat'
    c = float(np.corrcoef(a[m], b[m])[0, 1])
    if not np.isfinite(c):
        return 0.0, 'flat'
    return c, 'opengl' if c > 0.05 else 'directx' if c < -0.05 else 'unclear'


def _normal(rec, src, size, notes, forced=None):
    """normal input -> (H, W, 2) x, y in 0..255 with green up (TARGET), or None for a flat normal"""
    if rec is None or 'value' in rec:
        return None
    px, _ = src.rgba(rec)
    n = _fit(px[..., :3], size)
    corr, conv = normal_convention(n)
    found = conv
    if forced in ('opengl', 'directx'):
        conv = forced
    g = n[..., 1]
    if conv != TARGET and conv in ('opengl', 'directx'):
        g = 255.0 - g
    notes.append({'normal_convention': conv, 'integrability': round(corr, 3), 'detected': found, 'set_by_user': forced in ('opengl', 'directx'),
                  'action': {'opengl': 'green up already, kept', 'directx': 'green flipped (DirectX -> green up)', 'flat': 'no relief found, kept',
                             'unclear': 'convention unclear, kept as it is'}[conv]})
    out = np.dstack([n[..., 0], g])
    s = float(rec.get('strength', 1.0))
    if s != 1.0:
        out = np.clip((out - 127.5) * s + 127.5, 0.0, 255.0)
    return out


def base_size(inputs, src):
    """output resolution = the base colour image's, else the largest image input's, as the nearest powers of two
    (at most MAX_SIZE); see resize_notes for what is resampled"""
    sizes = {}
    for key, rec in inputs.items():
        if isinstance(rec, dict) and 'image' in rec:
            px, _ = src.rgba(rec)
            sizes[key] = (px.shape[1], px.shape[0])
    if not sizes:
        return (FLAT_SIZE, FLAT_SIZE), sizes
    w0, h0 = sizes.get('base_color') or max(sizes.values(), key=lambda s: s[0] * s[1])
    return (near_pow2(w0), near_pow2(h0)), sizes


def near_pow2(n):
    """the power of two nearest to n (in ratio), between FLAT_SIZE and MAX_SIZE (smaller sides are not known to be
    safe in game - see FLAT_SIZE - so a tiny image is enlarged; that costs nothing visible)"""
    import math
    return int(min(max(2 ** int(round(math.log2(max(n, 1)))), FLAT_SIZE), MAX_SIZE))


def resize_notes(size, sizes):
    """what the packer does to images that do not fit the game's rules - every image maps the same 0..1 UV square,
    so resampling one to the output size changes its sharpness, never where it sits on the mesh"""
    out = []
    for key, (w, h) in sorted(sizes.items()):
        if not (is_pow2(w) and is_pow2(h)) or max(w, h) > MAX_SIZE:
            out.append('%s image %d x %d is not a power-of-two size the game takes: resampled to %d x %d' % (key, w, h, size[0], size[1]))
        elif w * size[1] != h * size[0]:
            out.append('%s image %d x %d has another shape than the %d x %d output: resampled' % (key, w, h, size[0], size[1]))
    return out


# Rough metals: Blender's Principled BSDF uses multiscatter GGX (light that bounces between micro-facets is not lost);
# the game's is single-scatter, so a rough metal comes out darker - MatTest 2026-10-23: gold at roughness 0.8 was
# 166/159/127 in game, 206/187/135 in Blender, and 171/161/127 in Blender switched to single-scatter GGX. E_AVG = the
# single-scatter GGX albedo averaged over view angles (Monte Carlo, Smith height-correlated, F = 1) by roughness 0..1.
# A metal texel's colour c is lifted to c (1 + c (1 - E) / E) - the single-scatter colour that reflects what the
# multiscatter one does - with its hue kept where a channel would pass white (rough GOLD cannot get all of it back).
MULTISCATTER_COMP = True
E_AVG = np.array([0.998, 0.997, 0.992, 0.974, 0.938, 0.880, 0.800, 0.705, 0.601, 0.500, 0.407], np.float32)


def srgb_decode(s):
    s = np.clip(s, 0.0, 1.0)
    return np.where(s <= 0.04045, s / 12.92, np.power((s + 0.055) / 1.055, 2.4))


def multiscatter_lift(rgb255, rough, metal):
    """rgb255: sRGB colour (H, W, 3) 0..255; rough, metal: (H, W) 0..1 -> the lifted sRGB colour"""
    e = np.interp(np.clip(rough, 0.0, 1.0) * 10.0, np.arange(11), E_AVG)[..., None]
    c = srgb_decode(rgb255 / 255.0)
    comp = 1.0 + c * (1.0 - e) / e                                   # per channel
    lum = np.array([0.2126, 0.7152, 0.0722], np.float32)
    k = ((comp * c) @ lum) / np.maximum(c @ lum, 1e-6)              # one factor: the hue stays
    k = np.minimum(k[..., None], 1.0 / np.maximum(c.max(-1, keepdims=True), 1e-6))   # ...and no channel passes white
    k = np.maximum(k, 1.0)                                          # never darker than the colour itself
    out = c * (1.0 + (k - 1.0) * np.clip(metal, 0.0, 1.0)[..., None])
    return srgb_encode(out) * 255.0


def pack(layout, inputs, src, size, const_b=0, notes=None):
    """one packed texture, RGBA uint8 (H, W, 4)"""
    notes = notes if notes is not None else []
    w, h = size
    out = np.zeros((h, w, 4), np.float32)
    if layout == 'CS':
        out[..., :3] = _colour(inputs.get('base_color'), src, size)
        rough = _scalar(inputs.get('roughness'), src, size, 0.5)
        out[..., 3] = (1.0 - rough) * 255.0
        if MULTISCATTER_COMP and inputs.get('metallic') is not None:
            metal = _scalar(inputs.get('metallic'), src, size, 0.0)
            if metal.max() > 0.0:
                out[..., :3] = multiscatter_lift(out[..., :3], rough, metal)
    elif layout in ('NAM', 'NW'):
        n = _normal(inputs.get('normal'), src, size, notes, inputs.get('_normal_format'))
        out[..., 0:2] = n if n is not None else 128.0
        out[..., 2] = const_b
        if layout == 'NAM':
            out[..., 3] = _scalar(inputs.get('metallic'), src, size, 0.0) * 255.0
        else:
            out[..., 3] = alpha_plane(inputs, src, size) * 255.0
    elif layout == 'E':
        out[..., :3] = _colour(inputs.get('emission'), src, size, (0.0, 0.0, 0.0))
        out[..., 3] = 255.0
    elif layout == 'HC':
        # texture-coloured hologram (vendor/rvm/holo_texcolor.py): rgb = the glow colour x its level (premultiplied by
        # the material's alpha: exact for an additive surface), a = 10 % of the luminance (the shader's grey flicker term)
        em = inputs.get('emission')
        if em is None or ('value' in em and max((em['value'] if isinstance(em['value'], list) else [em['value']])[:3]) <= 0.0):
            em = inputs.get('base_color')
        c = _colour(em, src, size, (0.0, 0.0, 0.0))
        a = inputs.get('alpha')
        if isinstance(a, dict) and 'image' in a:
            c = c * alpha_plane(inputs, src, size)[..., None]
        c = c * float(inputs.get('_holo_scale', 1.0))
        out[..., :3] = c
        out[..., 3] = 0.1 * (np.clip(c, 0, 255) @ np.array([0.2126, 0.7152, 0.0722], np.float32))
    elif layout in ('XCS', 'XNW'):
        # PROBE layouts (finding what a shipped surface reads from which channel, in game): every channel that may be a
        # mask gets its own stripe direction - CS alpha vertical, NW blue horizontal, NW alpha diagonal - so one
        # screenshot shows which one cuts holes and which one glows. Never used by a material class.
        yy, xx = np.mgrid[0:h, 0:w]
        vert = ((xx * 8 // max(w, 1)) % 2 == 0) * 255.0
        horiz = ((yy * 8 // max(h, 1)) % 2 == 0) * 255.0
        diag = (((xx + yy) * 8 // max(w, 1)) % 2 == 0) * 255.0
        if layout == 'XCS':
            out[..., :3] = _colour(inputs.get('base_color'), src, size)
            out[..., 3] = vert
        else:
            out[..., 0:2] = 128.0
            out[..., 2] = horiz
            out[..., 3] = diag
    else:
        raise ValueError('unknown layout %r' % layout)
    return np.clip(np.round(out), 0, 255).astype(np.uint8)


def alpha_plane(inputs, src, size):
    """the cut-out mask 0..1: the alpha input when it is wired, else the base colour image's own alpha"""
    a = inputs.get('alpha')
    if a is not None and 'image' in a:
        return _scalar(a, src, size, 1.0)
    b = inputs.get('base_color')
    if b is not None and 'image' in b:
        return _scalar(dict(b, channel='A', invert=False, power=1.0, scale=1.0), src, size, 1.0)
    return _scalar(a, src, size, 1.0)


def has_cutout_alpha(inputs, src):
    """does the material's mask really have holes? -> (has an alpha source, fraction of texels under one half)"""
    a = inputs.get('alpha')
    rec = a if (a is not None and 'image' in a) else inputs.get('base_color')
    if rec is None or 'image' not in rec:
        return False, 0.0
    px, has_alpha = src.rgba(rec)
    if rec is not a and not has_alpha:
        return False, 0.0
    size = (px.shape[1], px.shape[0])
    m = alpha_plane(inputs, src, size)
    return True, float((m < 0.5).mean())


def emission_level(inputs, src):
    """how strongly the material really emits: strength x the brightest emission colour (0 = it does not)"""
    em, es = inputs.get('emission'), inputs.get('emission_strength') or {}
    if em is None:
        return 0.0
    v = es.get('value', 1.0) if isinstance(es, dict) else 1.0
    strength = float(v[0] if isinstance(v, (list, tuple)) else v)
    if 'value' in em:
        c = em['value']
        peak = max(float(x) for x in (c[:3] if isinstance(c, (list, tuple)) else [c]))
    elif 'image' in em:
        px, _ = src.rgba(em)
        peak = float(px[..., :3].max()) / 255.0
    else:
        return 0.0
    return strength * peak


def _value(rec):
    """the constant of a {'value': ...} input as one number, after its invert / power / scale"""
    v = rec['value']
    v = float(v[0] if isinstance(v, (list, tuple)) else v)
    return float(_transform(np.array([v], np.float32), rec)[0])


SEE_THROUGH_CLASSES = ('glass', 'holo', 'lava', 'water')    # classes whose look does not come from the alpha


def alpha_from_colour_attribute(rec):
    """was the alpha input baked from a Color Attribute (per-vertex values the bake cannot read)? Then it is unknown"""
    why = str(((rec.get('baked') or {}).get('why') or {}).get('alpha') or '')
    return 'color attribute' in why.lower()


def drop_invisible(materials):
    """A material whose alpha is a CONSTANT under one half (after the nodes are traced / baked) draws nothing in Blender:
    its faces are left out instead of stopping the build as a cut-out without holes (LEGO Utapau: glow pieces whose
    Color Attribute alpha is 0 - tint variants '#tint......00'). -> {material: reason}"""
    out = {}
    for name, rec in materials.items():
        if rec.get('skip') or rec.get('glow_of') or rec.get('class') in SEE_THROUGH_CLASSES:
            continue
        a = (rec.get('inputs') or {}).get('alpha')
        if alpha_from_colour_attribute(rec):
            # baked on a flat square, where a per-vertex colour cannot be read: the 0 is the bake's, not the surface's
            # (LEGO Utapau: 22 such materials, 1.1 M visible triangles). A tint variant's constant is real (no bake why)
            continue
        if isinstance(a, dict) and 'value' in a and 'image' not in a and _value(a) < 0.5:
            rec['skip'] = True
            rec['skip_why'] = 'its alpha is a constant %.2f, so it is invisible in Blender' % _value(a)
            out[name] = 'left out: ' + rec['skip_why']
    return out


def settle_glow(materials, src):
    """The glow layer of a glowing cut-out (material '<name>#glow', class holo, glow_of '<name>') is kept only when the
    base material really glows (emission x strength > 0.02 once traced / baked) AND really has holes. Otherwise the
    layer is left out (its faces are skipped) and the base is built as what it is: emissive (glows, no holes), cut-out
    (holes, no glow) or opaque. A class GUESSED as glowing cut-out from wired sockets alone made 47 glow layers on a
    ripped map whose emission was masked to black (Hunters Bastion, 2026-10-23). -> {material: reason}"""
    out = {}
    for name, rec in materials.items():
        base_name = rec.get('glow_of')
        if not base_name or rec.get('skip'):
            continue
        base = materials.get(base_name)
        if base is None:
            continue
        if base.get('skip'):                                 # the base is left out: so is its glow
            rec['skip'] = True
            rec['skip_why'] = 'the glow layer of "%s" is left out with it' % base_name
            continue
        inp = base.get('inputs', {})
        try:
            glows = emission_level(inp, src) > 0.02
        except PackError:
            glows = True
        a = inp.get('alpha')
        try:
            if alpha_from_colour_attribute(base):
                holes = False                                # unknown holes: built solid (reported)
            elif isinstance(a, dict) and 'image' in a:
                has, frac = has_cutout_alpha(inp, src)
                holes = has and frac > 0.0
            elif isinstance(a, dict) and 'value' in a:
                holes = _value(a) < 0.5
            else:
                has, frac = has_cutout_alpha(inp, src)
                holes = has and frac > 0.0
        except PackError:
            holes = True
        if glows and holes:
            continue
        rec['skip'] = True
        rec['skip_why'] = 'the glow layer of "%s" is left out: %s' % (base_name, 'it has no see-through texels' if glows
                                                                     else 'its emission is black or its strength is 0')
        base['glowing_cutout'] = False
        if glows:
            base['class'] = 'emissive'
        elif not holes:
            base['class'] = 'opaque'
        out[base_name] = rec['skip_why'] + ('; built as %s' % base['class'])
    return out


def effective_class(rec, src):
    """The class a material is built with -> (class, reason or None).
    A material left on Opaque with "detect automatically" on is promoted by what its own (traced or baked) inputs
    hold: a WIRED alpha with real holes -> cut-out; emission that is really there -> emissive. Anything the user chose
    stays. The promotion is reported (plan note + an info line in the Check tab), never silent."""
    cls = rec.get('class', 'opaque')
    if cls in ('cutout', 'cutout2s') and rec.get('class_guessed'):
        # a cut-out class that was only GUESSED (never confirmed by the user) and whose image has no hole: build it
        # opaque and say so, instead of stopping the build on a guess (seen on a user scene: two of 45 guesses)
        a = rec.get('inputs', {}).get('alpha')
        if isinstance(a, dict) and 'value' in a and _value(a) >= 0.5:
            # the alpha input traces / bakes to a constant (a mask switched off in the nodes): the base colour's own
            # alpha channel is NOT what Blender shows (Hunters Bastion: roughness lives there, 39 walls became holes)
            return 'opaque', 'its class was guessed as cut-out, but its alpha is a constant %.2f once the nodes are read' % _value(a)
        try:
            has, frac = has_cutout_alpha(rec.get('inputs', {}), src)
        except PackError:
            has, frac = True, 1.0
        if not has or frac <= 0.0:
            return 'opaque', 'its class was guessed as cut-out, but its image has no see-through texels'
    if cls == 'emissive' and rec.get('class_guessed'):
        # guessed as glowing, but nothing glows (black emission or strength 0 once the nodes are traced / baked): build it
        # opaque - it then takes a lightmap like any other surface (a user's city map: 35 of 57 "emissive" guesses)
        try:
            if emission_level(rec.get('inputs', {}), src) <= 0.02:
                return 'opaque', 'its class was guessed as emissive, but its emission is black or its strength is 0'
        except PackError:
            pass
    if cls != 'opaque' or not rec.get('class_auto', True):
        return cls, None
    inp = rec.get('inputs', {})
    try:
        a = inp.get('alpha')
        if isinstance(a, dict) and 'image' in a:
            has, frac = has_cutout_alpha(inp, src)
            if has and frac >= 0.005:
                two = rec.get('backface_culling') is False and bool(rec.get('double_sided'))
                return ('cutout2s' if two else 'cutout'), 'its alpha input has holes (%.1f %% of the texture is see-through)' % (100 * frac)
        lvl = emission_level(inp, src)
        if lvl > 0.02:
            return 'emissive', 'it emits light in Blender (emission x strength = %.2f)' % lvl
    except PackError:
        pass
    return cls, None


def has_relief(inputs, src):
    """does the material carry a normal map with real relief (not flat, not a constant)?"""
    n = inputs.get('normal')
    if not isinstance(n, dict) or 'image' not in n:
        return False
    try:
        px, _ = src.rgba(n)
    except PackError:
        return False
    return normal_convention(px[..., :3])[1] != 'flat'


def is_flat(layout, inputs):
    """a NAM that carries nothing (no normal map, constant metal) packs to a 4 x 4 texture"""
    if layout != 'NAM':
        return False
    n, m = inputs.get('normal'), inputs.get('metallic')
    return (n is None or 'value' in n) and (m is None or 'value' in m)


def unpack(layout, rgba):
    """the channels a packed texture holds, as floats 0..1 (normals as x, y in -1..1, green up)"""
    p = rgba.astype(np.float32) / 255.0
    if layout == 'CS':
        return {'base_color_srgb': p[..., :3], 'roughness': 1.0 - p[..., 3]}
    if layout in ('NAM', 'NW'):
        d = {'normal_xy': p[..., :2] * 2.0 - 1.0, 'const_b': int(rgba[..., 2].max())}
        d['metallic' if layout == 'NAM' else 'alpha'] = p[..., 3]
        return d
    if layout == 'E':
        return {'emission_srgb': p[..., :3]}
    if layout == 'HC':
        return {'holo_srgb': p[..., :3], 'flicker': p[..., 3]}
    raise ValueError(layout)


class Writer:
    """PNG output with content de-duplication: materials that pack to the same pixels share one texture"""

    def __init__(self, out_dir):
        self.dir = out_dir
        os.makedirs(out_dir, exist_ok=True)
        self.by_hash, self.names = {}, set()

    def save(self, rgba, name):
        rgba = np.ascontiguousarray(rgba)
        h = hashlib.md5(rgba.tobytes() + str(rgba.shape).encode()).hexdigest()
        if h in self.by_hash:
            return self.by_hash[h]
        n, k = name, 2
        while n.lower() in self.names:
            n = '%s_v%d' % (name, k); k += 1
        Image.fromarray(rgba, 'RGBA').save(os.path.join(self.dir, n + '.png'))
        self.by_hash[h] = n
        self.names.add(n.lower())
        return n
