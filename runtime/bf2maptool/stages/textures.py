"""Stage `textures`: every material of the split -> its class's packed textures (tex/*.png) and tex/plan.json.

Nothing is guessed silently: every decision (resolution, normal convention with its measured correlation, flat
textures, the emission strength the mesh will get) is written into the plan, which the add-on shows per material.
"""
import json
import os

import numpy as np

from .. import config, hosts, texpack
from ..runner import StageError


def _materials(job):
    with open(job.path('pieces', 'pieces.json'), encoding='utf-8') as f:
        mats = {p['material'] for p in json.load(f)['pieces']}
    for o in job.map['objects']:                       # doors are not in the split: their materials are packed too
        if o.get('role') == 'door':
            z = np.load(job.src(o['geo']), allow_pickle=False)
            mats |= {str(z['slots'][i]) for i in np.unique(z['mat'])}
    return sorted(m for m in mats if m)


def inputs(job):
    host = hosts.get(job.host)
    files = {}
    for name, rec in sorted(job.map['materials'].items()):
        for key, r in sorted(rec.get('inputs', {}).items()):
            f = (r.get('image') or {}).get('file') if isinstance(r, dict) else None
            if f and os.path.exists(job.src(f)):
                files[job.src(f)] = config.file_hash(job.src(f))
    return {'materials': job.map['materials'], 'files': files, 'templates': host.TEMPLATES, 'classes': host.CLASS_TEMPLATE,
            'prefix': job.tex_prefix, 'doors': [o['name'] for o in job.map['objects'] if o.get('role') == 'door'], 'flat': texpack.FLAT_SIZE,
            'multiscatter': [texpack.MULTISCATTER_COMP, texpack.E_AVG.tolist()], 'v': 15}


def outputs(job):
    return [job.path('tex', 'plan.json')]


def holo_level(rec):
    """how bright a hologram material is: its Blender emission strength (when a constant) x its own Brightness"""
    es = (rec.get('inputs') or {}).get('emission_strength') or {}
    v = es.get('value', 1.0) if isinstance(es, dict) else 1.0
    v = float(v[0] if isinstance(v, list) else v)
    return max(0.0, v) * float((rec.get('holo') or {}).get('brightness', 1.0))


def holo_ref(job):
    """the level of the brightest hologram of the map: its texels are written at full scale, every other hologram's in
    proportion, and the shader database is patched with this level (stages/shaderdb.py) - one constant for the map"""
    lv = [holo_level(r) for r in job.map['materials'].values() if r.get('class') == 'holo' and not r.get('skip')]
    lv = [x for x in lv if x > 0.0]
    return max(lv) if lv else 1.0


def pack_material(job, host, name, rec, src, writer, banded=()):
    """-> plan record of one material; raises texpack.PackError"""
    cls, auto = texpack.effective_class(rec, src)
    tname, T = host.template_for(cls, rec.get('template'))
    relief_tpl = getattr(host, 'OPAQUE_RELIEF_TEMPLATE', None)
    moved = False
    if cls == 'opaque' and not rec.get('template') and relief_tpl and name not in banded and texpack.has_relief(rec.get('inputs', {}), src):
        tname, T, moved = relief_tpl, host.TEMPLATES[relief_tpl], True
    if T is None:
        raise texpack.PackError('class.unsupported', 'material class "%s" is not available on this host shell yet' % cls)
    inp = dict(rec.get('inputs', {}))
    # a normal map the exporter baked is OpenGL by construction; otherwise the user's choice, else detection
    inp['_normal_format'] = (inp.get('normal') or {}).get('normal_format') or rec.get('normal_format', 'auto')
    if cls == 'holo':
        inp['_holo_scale'] = holo_level(rec) / holo_ref(job)
    size, sizes = texpack.base_size(inp, src)
    base_img = (inp.get('base_color') or {}).get('image') or {}
    stem = config.safe(os.path.splitext(base_img.get('name', '') or name)[0])
    notes, tex, slots = [{'resized': x} for x in texpack.resize_notes(size, sizes)], {}, []
    if auto:
        notes.append({'class_auto': '%s: %s' % (cls, auto)})
    if moved:
        notes.append({'template': 'built on the "%s" surface: the plain opaque surface of this host does not repeat a normal map' % tname})
    for param, layout, b, tpl in T['slots']:
        if texpack.is_flat(layout, inp):
            mv = (inp.get('metallic') or {}).get('value', 0.0)
            metal = int(round(255 * float(mv[0] if isinstance(mv, list) else mv)))
            px = texpack.pack(layout, inp, src, (texpack.FLAT_SIZE, texpack.FLAT_SIZE), b)
            tn = writer.save(px, '%sFlat_%s_b%d%s' % (job.tex_prefix, layout, b, '_m%d' % metal if metal else ''))
        else:
            px = texpack.pack(layout, inp, src, size, b, notes)
            tn = writer.save(px, '%s%s_%s%s' % (job.tex_prefix, stem, layout, '_b%d' % b if layout in ('NAM', 'NW') else ''))
        tex[param] = tn
        slots.append({'param': param, 'layout': layout, 'const_b': b, 'template': tpl, 'texture': tn})
    out = {'class': cls, 'template': tname, 'tex': tex, 'slots': slots, 'size': list(size), 'input_sizes': {k: list(v) for k, v in sizes.items()},
           'notes': notes, 'double_sided': bool(rec.get('double_sided'))}
    if cls in ('cutout', 'cutout2s'):
        has, frac = texpack.has_cutout_alpha(inp, src)
        out['cutout_hole_fraction'] = round(frac, 4)
        if not has or frac <= 0.0:
            raise texpack.PackError('tex.cutout_no_alpha', 'the material is a cut-out but its image has no see-through texels '
                                    '(no alpha channel, or alpha never under one half)')
    if T.get('emissive_param'):
        es = inp.get('emission_strength') or {}
        v = es.get('value', 1.0)
        strength = float(v[0] if isinstance(v, list) else v)
        em = inp.get('emission')
        if cls == 'opaque':
            # an opaque material on an emissive-capable surface: a black emission texture and no glow
            strength = 0.0
            tn = writer.save(np.dstack([np.zeros((texpack.FLAT_SIZE, texpack.FLAT_SIZE, 3), np.uint8), np.full((texpack.FLAT_SIZE, texpack.FLAT_SIZE), 255, np.uint8)]), '%sFlat_E_black' % job.tex_prefix)
            for s in out['slots']:
                if s['layout'] == 'E':
                    s['texture'] = tn; tex[s['param']] = tn
        elif em is None or ('value' in em and max(em['value'][:3]) <= 0.0):
            notes.append({'emission': 'no emission input: the base colour is used as the emission colour'})
            px = texpack.pack('CS', inp, src, size)
            px[..., 3] = 255
            tn = writer.save(px, '%s%s_E' % (job.tex_prefix, stem))
            for s in out['slots']:
                if s['layout'] == 'E':
                    s['texture'] = tn; tex[s['param']] = tn
        if cls != 'opaque' and strength > 0.0:
            # The emissive surface SHOWS emission texture x colour texture (measured, LightMatch lm3: the same panel on a
            # black base is black, on a 0.8 base it shows at 0.8). So where a texel glows, its colour texture takes the
            # glow's hue and the emission texture the glow's level; where it does not glow the base colour stays.
            def lin(px):
                c = px[..., :3].astype(np.float32) / 255.0
                return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
            cs_px = texpack.pack('CS', inp, src, size)
            e_src = inp if not (em is None or ('value' in em and max(em['value'][:3]) <= 0.0)) else dict(inp, emission=inp.get('base_color'))
            e_lin = lin(texpack.pack('E', e_src, src, size))
            level = e_lin.max(-1, keepdims=True)
            glow = level > 0.002
            hue = np.where(glow, e_lin / np.maximum(level, 1e-6), lin(cs_px))
            cs_new = cs_px.copy()
            cs_new[..., :3] = np.clip(np.round(texpack.srgb_encode(hue) * 255.0), 0, 255).astype(np.uint8)
            e_new = np.zeros_like(cs_px)
            e_new[..., :3] = np.clip(np.round(texpack.srgb_encode(np.repeat(level, 3, -1)) * 255.0), 0, 255).astype(np.uint8)
            e_new[..., 3] = 255
            tn_cs = writer.save(cs_new, '%s%s_CSglow' % (job.tex_prefix, stem))
            tn_e = writer.save(e_new, '%s%s_Eglow' % (job.tex_prefix, stem))
            for s in out['slots']:
                if s['layout'] == 'CS':
                    s['texture'] = tn_cs; tex[s['param']] = tn_cs
                elif s['layout'] == 'E':
                    s['texture'] = tn_e; tex[s['param']] = tn_e
            out['emission_peak'] = float(level.max())
            notes.append({'emission': 'the glow colour is in the colour texture, its level in the emission texture (the surface shows their product)'})
        out['emission_strength'] = strength
        out['emission_screen'] = float(rec.get('emission_screen', 1.0))
        out['emission_bake'] = float(rec.get('emission_bake', 1.0))
    if 'ao' in inp:
        notes.append({'ao': 'not packed: this template has no occlusion channel; the light bake supplies occlusion'})
    for key, r in inp.items():
        if isinstance(r, dict) and r.get('unsupported'):
            notes.append({'unsupported_node': '%s input goes through a "%s" node the packer cannot reproduce; the image behind it is used as it is' % (key, r['unsupported'])})
    if cls == 'lava':
        notes.append({'lava': 'drawn on the emissive template (the lava preset is not built into this map yet)'})
    if cls == 'holo':
        h = rec.get('holo') or {}
        sp = h.get('speed') or [0.0, 0.0]
        out['holo'] = {'speed': [float(sp[0]), float(sp[1])], 'tiling': [float(x) for x in (h.get('tiling') or [1.0, 1.0])], 'level': holo_level(rec), 'scale': inp['_holo_scale']}
        notes.append({'holo': 'animated hologram: scrolls %.3g / %.3g per second (u / v), the shader adds scan lines and flicker; '
                      'level %.3g of the map\'s brightest hologram' % (sp[0], sp[1], inp['_holo_scale'])})
    return out


def run(job, log):
    host = hosts.get(job.host)
    src = texpack.Source(job.src)
    writer = texpack.Writer(job.dirs['tex'])
    plan, errors = {}, []
    with open(job.path('pieces', 'pieces.json'), encoding='utf-8') as f:
        banded = set(json.load(f).get('v_banded') or [])        # materials the split kept inside one vertical repeat
    for name in _materials(job):
        rec = job.map['materials'].get(name)
        if rec is None:
            errors.append(('geo.no_material', name, 'faces without a material'))
            continue
        try:
            plan[name] = pack_material(job, host, name, rec, src, writer, banded)
        except texpack.PackError as e:
            errors.append((e.code, name, str(e)))
    if errors:
        raise StageError('%d material(s) cannot be packed:\n' % len(errors) + '\n'.join('  [%s] %s: %s' % e for e in errors))
    keep = {t + '.png' for t in writer.names} | {'plan.json'}
    kept_lower = {k.lower() for k in keep}
    for f in os.listdir(job.dirs['tex']):
        if f.lower() not in kept_lower and f.endswith('.png'):
            os.remove(os.path.join(job.dirs['tex'], f))
    with open(job.path('tex', 'plan.json'), 'w', encoding='utf-8') as f:
        json.dump(plan, f, indent=1)
    from collections import Counter
    c = Counter(r['class'] for r in plan.values())
    conv = Counter(n['normal_convention'] for r in plan.values() for n in r['notes'] if 'normal_convention' in n)
    summary = {'materials': len(plan), 'classes': dict(c), 'textures': len(writer.names), 'normal_maps': dict(conv)}
    log('textures: %d materials %s -> %d packed textures; normal maps %s' % (len(plan), dict(c), len(writer.names), dict(conv)))
    return summary
