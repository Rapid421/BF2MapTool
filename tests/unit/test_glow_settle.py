"""texpack.settle_glow + effective_class on glowing cut-outs: the glow layer is kept only when the base really glows
AND really has holes; a guessed cut-out whose alpha is a constant is built opaque even when its base colour image
carries something else in its alpha channel (Hunters Bastion: roughness in C_Alpha, 39 walls became cut-outs).
   python test_glow_settle.py"""
import os
import sys
import tempfile

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime'))
from bf2maptool import texpack  # noqa: E402

d = tempfile.mkdtemp(prefix='glowsettle_')


def img(name, rgba):
    a = np.zeros((16, 16, 4), np.uint8)
    a[:] = rgba
    if name == 'holes.png':
        a[:8, :, 3] = 0
    Image.fromarray(a, 'RGBA').save(os.path.join(d, name))
    return {'image': {'name': name, 'file': name}, 'channel': 'RGB', 'invert': False, 'power': 1.0, 'scale': 1.0}


rough_in_alpha = img('colour_rough.png', (120, 120, 120, 60))     # C_RGB + roughness in A (all under one half)
glow = img('glow.png', (255, 200, 40, 255))
holes = dict(img('holes.png', (255, 255, 255, 255)), channel='A')
black = {'value': [0.0, 0.0, 0.0, 1.0]}
one = {'value': 1.0, 'invert': False, 'power': 1.0, 'scale': 1.0}


def base(em, es, alpha, cls='cutout', guessed=True):
    return {'class': cls, 'class_guessed': guessed, 'class_auto': False, 'glowing_cutout': True,
            'inputs': {'base_color': rough_in_alpha, 'emission': em, 'emission_strength': {'value': es}, 'alpha': alpha}}


mats = {
    'floor': base(black, 0.0, one),               # Hunters floor: emission masked to black, alpha switched off
    'grate': base(glow, 1.0, holes),              # a real lit grate
    'sign': base(glow, 1.0, one),                 # glows, no holes
    'fence': base(black, 1.0, holes),             # holes, no glow
}
for k in list(mats):
    mats[k + '#glow'] = {'class': 'holo', 'glow_of': k, 'inputs': {}}
src = texpack.Source(lambda p: os.path.join(d, p))
notes = texpack.settle_glow(mats, src)
ok = True


def expect(what, got, want):
    global ok
    good = got == want
    ok &= good
    print('%-4s %-34s got %-10r want %r' % ('ok' if good else 'FAIL', what, got, want))


expect('floor glow layer skipped', bool(mats['floor#glow'].get('skip')), True)
expect('floor built as', texpack.effective_class(mats['floor'], src)[0], 'opaque')
expect('grate glow layer kept', bool(mats['grate#glow'].get('skip')), False)
expect('grate built as', texpack.effective_class(mats['grate'], src)[0], 'cutout')
expect('sign glow layer skipped', bool(mats['sign#glow'].get('skip')), True)
expect('sign built as', texpack.effective_class(mats['sign'], src)[0], 'emissive')
expect('fence glow layer skipped', bool(mats['fence#glow'].get('skip')), True)
expect('fence built as', texpack.effective_class(mats['fence'], src)[0], 'cutout')
expect('notes for the three settled', sorted(notes), ['fence', 'floor', 'sign'])
# a guessed cut-out (no glow involved) whose alpha is a constant: opaque, the C_Alpha roughness is not a mask
g = {'class': 'cutout', 'class_guessed': True, 'inputs': {'base_color': rough_in_alpha, 'alpha': one}}
expect('guessed cut-out, constant alpha', texpack.effective_class(g, src)[0], 'opaque')
# a cut-out the USER picked keeps the base colour's alpha as its mask (the fallback stays for hand-set classes)
u = {'class': 'cutout', 'class_guessed': False, 'inputs': {'base_color': rough_in_alpha, 'alpha': one}}
expect('user cut-out, constant alpha', texpack.effective_class(u, src)[0], 'cutout')
# drop_invisible: a REAL constant alpha 0 (a tint variant '#tint......00') is left out; an alpha baked 0 from a Color
# Attribute (the bake cannot read per-vertex values) is not - on LEGO Utapau that was 1.1 M visible triangles
zero = {'value': 0.0, 'invert': False, 'power': 1.0, 'scale': 1.0}
vm = {'lamp#tintffffff00': {'class': 'cutout', 'inputs': {'alpha': zero}},
      'brick': {'class': 'opaque', 'inputs': {'alpha': dict(zero, baked=True)}, 'baked': {'why': {'alpha': 'Color Attribute'}}},
      'pane': {'class': 'glass', 'inputs': {'alpha': zero}},
      'lit': {'class': 'cutout', 'class_guessed': False, 'glowing_cutout': True, 'baked': {'why': {'alpha': 'Color Attribute'}},
              'inputs': {'base_color': rough_in_alpha, 'emission': black, 'emission_strength': {'value': 0.0}, 'alpha': dict(zero, baked=True)}},
      'lit#glow': {'class': 'holo', 'glow_of': 'lit', 'inputs': {}}}
dropped = texpack.drop_invisible(vm)
expect('invisible: only the real constant 0 is left out', sorted(dropped), ['lamp#tintffffff00'])
texpack.settle_glow(vm, src)
expect('Color-Attribute alpha glow base built solid', vm['lit']['class'], 'opaque')
expect('...and its glow layer is left out', bool(vm['lit#glow'].get('skip')), True)
print('PASS' if ok else 'FAIL')
sys.exit(0 if ok else 1)
