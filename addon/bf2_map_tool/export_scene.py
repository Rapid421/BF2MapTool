"""Scene -> map.json + geo/*.npz + tex/*.png: the ONLY place of the tool that touches Blender data.

Used by the add-on (in the open scene, or headless on a saved copy so the UI never blocks):
    blender -b <copy.blend> --python export_scene.py -- <out dir>

What is written (all positions in scene metres, Blender axes; the runtime converts to the game's):
  map.json        map settings, objects, materials (one record per Principled input), lights, gameplay markers, look
  geo/<obj>.npz   evaluated, triangulated, world-space triangles: pos / nrm (T, 3, 3), uv (T, 3, 2), mat (T), slots
  tex/<img>.png   every image a material input reads, saved once
"""
import json
import math
import os
import re
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

try:
    from . import matbake
except ImportError:                                  # run as a plain script by the build
    import sys as _sys
    _sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import matbake

try:
    from . import worldbake
    from . import reflbake
    from . import sunseal
except ImportError:
    import worldbake
    import reflbake
    import sunseal

try:
    from . import props
except ImportError:                                   # run as a plain script
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import props

MAP_VERSION = 1
SUFFIXES = {'normal': ('_n', '_nrm', '_normal', '_nor'), 'roughness': ('_r', '_rgh', '_rough', '_roughness'),
            'metallic': ('_m', '_metal', '_metallic', '_mtl'), 'emission': ('_e', '_em', '_emissive', '_emission', '_glow'),
            'ao': ('_ao', '_occlusion')}


def safe(n):
    return ''.join(ch if (ch.isalnum() or ch in '_-') else '_' for ch in n)


def ensure_props():
    if not hasattr(bpy.types.Scene, 'bf2'):
        props.register()


# ------------------------------------------------------------------ images / material inputs
class Images:
    def __init__(self, out_dir):
        self.dir = os.path.join(out_dir, 'tex')
        os.makedirs(self.dir, exist_ok=True)
        self.done = {}

    def export(self, img):
        """-> record of one image (saved as PNG once). A missing image gives 'missing': True."""
        if img.name in self.done:
            return dict(self.done[img.name])
        rec = {'name': img.name, 'colorspace': img.colorspace_settings.name, 'packed': img.packed_file is not None,
               'source_path': bpy.path.abspath(img.filepath) if img.filepath else ''}
        w, h = img.size[0], img.size[1]
        if w == 0 or h == 0 or not img.has_data and not self._load(img):
            rec.update(missing=True, size=[0, 0])
        else:
            w, h = img.size[0], img.size[1]
            fn = safe(os.path.splitext(img.name)[0]) + '.png'
            path = os.path.join(self.dir, fn)
            px = np.empty(w * h * 4, np.float32)
            img.pixels.foreach_get(px)
            px = px.reshape(h, w, 4)[::-1]
            if img.colorspace_settings.name not in ('sRGB', 'Non-Color', 'Linear Rec.709', 'Linear', 'Raw'):
                rec['warning'] = 'colour space %s is treated as sRGB' % img.colorspace_settings.name
            # pixels are scene-linear for sRGB images only when float; byte images give the stored values back
            if img.is_float and img.colorspace_settings.name == 'sRGB':
                c = np.clip(px[..., :3], 0, None)
                px = np.dstack([np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055), px[..., 3:]])
            a8 = (np.clip(px, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)
            write_png(path, a8)
            rec.update(missing=False, file='tex/' + fn, size=[w, h], channels=img.channels,
                       alpha_min=float(px[..., 3].min()), alpha_mean=float(px[..., 3].mean()))
        self.done[img.name] = rec
        return dict(rec)

    @staticmethod
    def _load(img):
        try:
            img.reload()
            _ = img.pixels[0]
            return img.size[0] > 0
        except Exception:
            return False


def write_png(path, rgba):
    """RGBA uint8 (H, W, 4), top row first -> PNG (zlib; no Blender image datablock is created)"""
    import struct
    import zlib
    h, w = rgba.shape[:2]
    raw = np.empty((h, w * 4 + 1), np.uint8)
    raw[:, 0] = 0
    raw[:, 1:] = rgba.reshape(h, w * 4)

    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)) +
                chunk(b'IDAT', zlib.compress(raw.tobytes(), 6)) + chunk(b'IEND', b''))


def _principled(mat):
    """(Principled BSDF node, stack of the group nodes it sits in) of a material, or (None, []).
    Game-ripped scenes build their materials from node groups: the BSDF is then inside a group (sometimes nested)."""
    if not mat or not mat.use_nodes or not mat.node_tree:
        return None, []

    def search(tree, stack, depth=0):
        out = next((n for n in tree.nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output), None) if depth == 0 else None
        start = None
        if out is not None and out.inputs['Surface'].is_linked:
            start = out.inputs['Surface'].links[0].from_node
        if depth > 0:
            go = next((n for n in tree.nodes if n.type == 'GROUP_OUTPUT' and getattr(n, 'is_active_output', True)), None)
            if go is not None:
                for i in go.inputs:
                    if i.is_linked and i.type == 'SHADER':
                        start = i.links[0].from_node
                        break
        seen = set()
        n = start
        while n is not None and n.name not in seen:
            seen.add(n.name)
            if n.type == 'BSDF_PRINCIPLED':
                return n, stack
            if n.type == 'GROUP' and n.node_tree is not None and depth < 6:
                r = search(n.node_tree, stack + [n], depth + 1)
                if r[0] is not None:
                    return r
            nxt = None
            for i in n.inputs:                      # walk through mix / add shaders to the first shader input
                if i.type == 'SHADER' and i.is_linked:
                    nxt = i.links[0].from_node
                    break
            n = nxt
        # not on the surface chain: any Principled in this tree, then in its groups
        pn = next((x for x in tree.nodes if x.type == 'BSDF_PRINCIPLED'), None)
        if pn is not None:
            return pn, stack
        if depth < 6:
            for g in tree.nodes:
                if g.type == 'GROUP' and g.node_tree is not None:
                    r = search(g.node_tree, stack + [g], depth + 1)
                    if r[0] is not None:
                        return r
        return None, []
    return search(mat.node_tree, [])


def _trace(sock, images, stack=()):
    """Follow one input socket back to an image or a constant, through node groups in both directions.
    Record: {'value': ...} or {'image': rec, 'channel', 'invert', 'power', 'scale'} (+ 'unsupported': node type when the
    chain has a node the packer cannot reproduce; + 'approximate': what was simplified, e.g. a colour mix)."""
    rec = {'channel': 'RGB', 'invert': False, 'power': 1.0, 'scale': 1.0}
    cur = sock
    stack = list(stack)
    for _ in range(40):
        if not cur.is_linked:
            v = getattr(cur, 'default_value', 0.0)
            try:
                v = [float(x) for x in v]
            except TypeError:
                v = float(v)
            out = {k: rec[k] for k in ('invert', 'power', 'scale')}
            if 'approximate' in rec:
                out['approximate'] = rec['approximate']
            out['value'] = v
            return out
        link = cur.links[0]
        n, fs = link.from_node, link.from_socket
        if n.type == 'TEX_IMAGE':
            if n.image is None:
                rec['image'] = {'name': '(empty image node)', 'missing': True, 'size': [0, 0]}
            else:
                rec['image'] = images.export(n.image)
            if fs.name == 'Alpha':
                rec['channel'] = 'A'
            t = matbake.uv_transform(n)
            if t:
                rec['uv_transform'] = t
            return rec
        if n.type == 'GROUP' and n.node_tree is not None:
            go = next((x for x in n.node_tree.nodes if x.type == 'GROUP_OUTPUT' and getattr(x, 'is_active_output', True)), None)
            inner = next((i for i in go.inputs if i.identifier == fs.identifier), None) if go is not None else None
            if inner is None:
                rec['unsupported'] = 'node group "%s"' % n.node_tree.name
                return rec
            stack.append(n)
            cur = inner
        elif n.type == 'GROUP_INPUT':
            if not stack:
                rec['unsupported'] = 'group input outside a group'
                return rec
            g = stack.pop()
            outer = next((i for i in g.inputs if i.identifier == fs.identifier), None)
            if outer is None:
                rec['unsupported'] = 'node group "%s"' % g.node_tree.name
                return rec
            cur = outer
        elif n.type in ('VALUE', 'RGB'):
            v = n.outputs[0].default_value
            try:
                v = [float(x) for x in v]
            except TypeError:
                v = float(v)
            out = {k: rec[k] for k in ('invert', 'power', 'scale')}
            if 'approximate' in rec:
                out['approximate'] = rec['approximate']
            out['value'] = v
            return out
        elif n.type in ('COMBINE_COLOR', 'COMBRGB', 'COMBINE_XYZ'):
            # channels re-assembled from other textures (e.g. a normal rebuilt from two channels): the image behind the
            # first wired channel is taken whole and the simplification is reported
            first = next((i for i in n.inputs if i.is_linked), None)
            if first is None:
                rec['unsupported'] = n.bl_label or n.type
                return rec
            rec['approximate'] = 'channels are combined by a "%s" node: the image behind its first channel is used whole' % (n.bl_label or n.type)
            rec['channel'] = 'RGB'
            cur = first
        elif n.type == 'NORMAL_MAP':
            rec['strength'] = float(n.inputs['Strength'].default_value)
            cur = n.inputs['Color']
        elif n.type in ('SEPARATE_COLOR', 'SEPRGB', 'SEPARATE_XYZ'):
            rec['channel'] = {'Red': 'R', 'Green': 'G', 'Blue': 'B', 'R': 'R', 'G': 'G', 'B': 'B', 'X': 'R', 'Y': 'G', 'Z': 'B'}.get(fs.name, 'R')
            cur = n.inputs[0]
        elif n.type == 'INVERT':
            rec['invert'] = not rec['invert']
            cur = n.inputs['Color']
        elif n.type == 'MATH' and n.operation in ('POWER', 'MULTIPLY') and sum(1 for i in n.inputs[:2] if i.is_linked) == 1:
            li = 0 if n.inputs[0].is_linked else 1
            c = float(n.inputs[1 - li].default_value)
            if n.operation == 'POWER' and li == 0:
                rec['power'] *= c
            elif n.operation == 'MULTIPLY':
                rec['scale'] *= c
            else:
                rec['unsupported'] = 'Math (%s)' % n.operation
                return rec
            cur = n.inputs[li]
        elif n.type == 'MATH' and n.operation == 'SUBTRACT' and not n.inputs[0].is_linked and abs(n.inputs[0].default_value - 1.0) < 1e-6:
            rec['invert'] = not rec['invert']
            cur = n.inputs[1]
        elif n.type in ('MIX', 'MIX_RGB'):
            # a colour mix (tint, dirt, detail): the tool packs ONE image per channel, so the first input that leads to an
            # image is taken and the simplification is reported
            cands = [i for i in n.inputs if i.is_linked and i.type in ('RGBA', 'VECTOR', 'VALUE') and i.name not in ('Factor', 'Fac')]
            pick = None
            for i in cands:
                t = _trace(i, images, stack)
                if 'image' in t:
                    pick = t
                    break
            if pick is None:
                rec['unsupported'] = n.bl_label or n.type
                return rec
            for k in ('invert',):
                pick[k] = pick[k] != rec[k]
            pick['power'] *= rec['power']; pick['scale'] *= rec['scale']
            if rec['channel'] != 'RGB':
                pick['channel'] = rec['channel']
            pick['approximate'] = 'a colour Mix node: only "%s" is used' % (pick['image'].get('name', '?'))
            return pick
        elif n.type in ('RGBTOBW', 'GAMMA', 'REROUTE', 'MAPPING', 'HUE_SAT', 'BRIGHTCONTRAST', 'CURVE_RGB', 'CLAMP'):
            if n.type == 'GAMMA':
                rec['power'] *= float(n.inputs['Gamma'].default_value)
            if n.type in ('HUE_SAT', 'BRIGHTCONTRAST', 'CURVE_RGB'):
                rec['approximate'] = 'a "%s" colour adjustment is not applied' % (n.bl_label or n.type)
            cur = n.inputs['Color'] if 'Color' in n.inputs else n.inputs[0]
        else:
            rec['unsupported'] = n.bl_label or n.type
            return rec
    rec['unsupported'] = 'node chain too long'
    return rec


NAME_HINTS = (('normal', ('_n', '_nrm', '_normal', '_nor', '_nm', '_ddn')), ('emission', ('_e', '_em', '_emissive', '_emission', '_glow')),
              ('roughness', ('_r', '_rgh', '_rough', '_roughness')), ('metallic', ('_m', '_metal', '_metallic', '_mtl')),
              ('base_color', ('_c', '_d', '_col', '_color', '_diff', '_diffuse', '_albedo', '_basecolor', '_base_color', '_bc')))


def _images_in(tree, depth=0, out=None):
    out = [] if out is None else out
    for n in tree.nodes:
        if n.type == 'TEX_IMAGE' and n.image is not None:
            out.append(n.image)
        elif n.type == 'GROUP' and n.node_tree is not None and depth < 6:
            _images_in(n.node_tree, depth + 1, out)
    return out


def _by_image_name(mat, have, images):
    """Last resort for materials whose node setup the tracer cannot follow (custom game shaders): the images the
    material uses, sorted by what their NAME says they are. Marked 'by_name' so the plan and the checks say so."""
    out = {}
    imgs = []
    for im in _images_in(mat.node_tree):
        if im.name not in [x.name for x in imgs]:
            imgs.append(im)
    import os as _os
    for im in imgs:
        stem = _os.path.splitext(im.name)[0].lower()
        stem = stem[:-4] if len(stem) > 4 and stem[-4] == '.' and stem[-3:].isdigit() else stem
        for key, sufs in NAME_HINTS:
            if key in have or key in out:
                continue
            if any(stem.endswith(sf) for sf in sufs):
                out[key] = {'image': images.export(im), 'channel': 'RGB', 'invert': False, 'power': 1.0, 'scale': 1.0, 'by_name': True}
                break
    if 'base_color' not in have and 'base_color' not in out and imgs:
        named = {r['image']['name'] for r in out.values()}
        rest = [im for im in imgs if im.name not in named and im.colorspace_settings.name == 'sRGB'] or [im for im in imgs if im.name not in named]
        if rest:
            big = max(rest, key=lambda im: im.size[0] * im.size[1])
            out['base_color'] = {'image': images.export(big), 'channel': 'RGB', 'invert': False, 'power': 1.0, 'scale': 1.0, 'by_name': True,
                                 'approximate': 'no image is wired to a base colour the tool can follow: the largest colour image is used'}
    return out


def _by_suffix(base_rec, images):
    """companion maps named by suffix next to the base colour file (wall.png -> wall_n.png, wall_rgh.png ...)"""
    out = {}
    src = (base_rec or {}).get('image', {}).get('source_path')
    if not src or not os.path.exists(src):
        return out
    folder, fn = os.path.split(src)
    stem, _ = os.path.splitext(fn)
    stem = re.sub(r'(_c|_col|_color|_basecolor|_albedo|_diffuse|_d)$', '', stem, flags=re.I)
    try:
        names = os.listdir(folder)
    except OSError:
        return out
    low = {n.lower(): n for n in names}
    for key, sufs in SUFFIXES.items():
        for s in sufs:
            for ext in ('.png', '.tga', '.jpg', '.jpeg', '.tif', '.tiff', '.bmp', '.dds'):
                cand = low.get((stem + s + ext).lower())
                if cand:
                    try:
                        img = bpy.data.images.load(os.path.join(folder, cand), check_existing=True)
                    except RuntimeError:
                        continue
                    if key != 'emission':
                        img.colorspace_settings.name = 'Non-Color'
                    out[key] = {'image': images.export(img), 'channel': 'RGB', 'invert': False, 'power': 1.0, 'scale': 1.0, 'by_suffix': True}
                    break
            if key in out:
                break
    return out


def material_record(mat, images, baker=None, bake_mode='auto'):
    b = mat.bf2
    rec = {'class': b.cls, 'class_guessed': bool(b.cls_guessed), 'emission_screen': b.emission_screen, 'emission_bake': b.emission_bake,
           'double_sided': bool(b.double_sided) or b.cls == 'cutout2s', 'normal_format': b.normal_format, 'class_auto': bool(b.cls_auto), 'template': b.template, 'skip': bool(b.skip), 'inputs': {},
           'blend_method': getattr(mat, 'blend_method', ''), 'backface_culling': bool(mat.use_backface_culling)}
    if b.cls == 'lava':
        rec['lava'] = {'color': list(b.lava_color), 'brightness': b.lava_brightness, 'crust': b.lava_crust, 'speed': b.lava_speed}
    if b.cls == 'holo':
        rec['holo'] = {'speed': [float(x) for x in b.holo_speed], 'tiling': [float(x) for x in b.holo_tiling],
                       'brightness': float(b.holo_brightness), 'image': b.holo_image}
        img = bpy.data.images.get(b.holo_image) if b.holo_image else None
        if img is not None:
            # the hologram shows ONE image as it is (the shader adds the scan lines, the flicker and the motion)
            r = {'image': images.export(img), 'channel': 'RGB', 'invert': False, 'power': 1.0, 'scale': 1.0}
            rec['inputs'] = {'emission': r, 'base_color': dict(r), 'emission_strength': {'value': 1.0}}
            return rec
    p, stack = _principled(mat)
    if p is None:
        rec['no_principled'] = True
        if baker is not None and mat.use_nodes and mat.node_tree and any(
                n.type == 'OUTPUT_MATERIAL' and n.is_active_output and n.inputs['Surface'].is_linked for n in mat.node_tree.nodes):
            res, _ = baker.bake_surface(mat)
            got = {k: v for k, v in res.get('records', {}).items() if v is not None}
            if 'base_color' in got:
                rec['inputs'].update(got)
                rec['baked'] = {'inputs': sorted(got), 'size': res.get('size'), 'why': {'surface': res.get('why')}, 'notes': res.get('notes', [])}
                return rec
        if mat.use_nodes and mat.node_tree:
            rec['inputs'].update(_by_image_name(mat, {}, images))
        if 'base_color' not in rec['inputs']:
            rec['inputs']['base_color'] = {'value': list(mat.diffuse_color)}
        return rec
    rec['in_group'] = [g.node_tree.name for g in stack]
    names = {'base_color': ('Base Color',), 'alpha': ('Alpha',), 'roughness': ('Roughness',), 'metallic': ('Metallic',),
             'normal': ('Normal',), 'emission': ('Emission Color', 'Emission'), 'emission_strength': ('Emission Strength',)}
    linked = {}
    for key, cands in names.items():
        sock = next((p.inputs[c] for c in cands if c in p.inputs), None)
        if sock is None:
            continue
        linked[key] = bool(sock.is_linked)
        if key == 'normal' and not sock.is_linked:
            continue
        rec['inputs'][key] = _trace(sock, images, stack)
    # inputs that are more than an image or a constant are RENDERED (matbake): exactly what Blender shows
    if baker is not None:
        want, why = [], {}
        for key in ('base_color', 'alpha', 'roughness', 'metallic', 'normal', 'emission'):
            sock = next((p.inputs[c] for c in names[key] if c in p.inputs), None)
            r = rec['inputs'].get(key)
            if sock is None or not sock.is_linked or not isinstance(r, dict):
                continue
            reason = r.get('unsupported') or r.get('approximate') or r.get('uv_transform')
            if reason or (bake_mode == 'always' and key != 'normal'):
                want.append(key); why[key] = reason or 'every input is baked (Bake materials: Always)'
        es = rec['inputs'].get('emission_strength')
        if isinstance(es, dict) and 'value' not in es and 'emission' not in want:
            want.append('emission'); why['emission'] = 'the emission strength is driven by nodes'
        if want:
            res, _ = baker.bake_inputs(mat, want, names, _principled, why)
            done = []
            for key, r in res.get('records', {}).items():
                if r is None:
                    rec['inputs'].pop(key, None)
                else:
                    rec['inputs'][key] = r
                done.append(key)
            rec['baked'] = {'inputs': sorted(done), 'size': res.get('size'), 'why': why, 'notes': res.get('notes', []),
                            'not_baked': sorted(k for k in want if k not in done)}
    # ambient occlusion has no Principled socket: the glTF convention (a group node with an Occlusion input) is read
    for n in mat.node_tree.nodes:
        if n.type == 'GROUP' and n.node_tree and 'gltf' in n.node_tree.name.lower() and 'Occlusion' in n.inputs and n.inputs['Occlusion'].is_linked:
            rec['inputs']['ao'] = _trace(n.inputs['Occlusion'], images)
    # inputs the tracer could not follow to an image: try the material's images by name
    have = {k for k, r in rec['inputs'].items() if isinstance(r, dict) and 'image' in r}
    # a socket with nothing wired to it holds the value the user set: never replaced by a guess (MatTest 2026-10-23: a
    # grey material with a normal map got the NORMAL MAP as its colour - "the largest colour image is used")
    set_by_hand = {k for k, l in linked.items() if not l and k in rec['inputs']}
    need_name = (linked.get('base_color') and 'base_color' not in have) or any(isinstance(r, dict) and r.get('unsupported') for r in rec['inputs'].values())
    if need_name:
        for key, r in _by_image_name(mat, have | set_by_hand, images).items():
            old = rec['inputs'].get(key)
            if key in set_by_hand:
                continue
            if old is None or 'value' in old or old.get('unsupported'):
                if old is not None and old.get('unsupported'):
                    r['replaces_unsupported'] = old['unsupported']
                rec['inputs'][key] = r
    for key, r in _by_suffix(rec['inputs'].get('base_color'), images).items():
        cur = rec['inputs'].get(key)
        if key in set_by_hand and key != 'normal':
            continue
        if cur is None or 'value' in cur and key != 'emission':
            rec['inputs'][key] = r
    return rec


# ------------------------------------------------------------------ geometry
def mesh_arrays(ob, dg, ev=None, mw=None):
    """world-space triangles of the evaluated object, or None when it has none. For an instance (collection instance,
    Geometry Nodes, particles) `ev` is the depsgraph's instance object and `mw` its world matrix"""
    ev = ev if ev is not None else ob.evaluated_get(dg)
    try:
        me = ev.to_mesh()
    except RuntimeError:
        return None
    try:
        me.calc_loop_triangles()
        n = len(me.loop_triangles)
        if n == 0:
            return None
        loops = np.empty(n * 3, np.int32); me.loop_triangles.foreach_get('loops', loops)
        verts = np.empty(n * 3, np.int32); me.loop_triangles.foreach_get('vertices', verts)
        mi = np.empty(n, np.int32); me.loop_triangles.foreach_get('material_index', mi)
        co = np.empty(len(me.vertices) * 3, np.float64); me.vertices.foreach_get('co', co)
        co = co.reshape(-1, 3)
        cn = np.empty(len(me.loops) * 3, np.float32); me.corner_normals.foreach_get('vector', cn)
        cn = cn.reshape(-1, 3).astype(np.float64)
        if me.uv_layers.active is not None:
            uv = np.empty(len(me.loops) * 2, np.float32); me.uv_layers.active.data.foreach_get('uv', uv)
            uv = uv.reshape(-1, 2).astype(np.float64)
            has_uv = True
        else:
            uv = np.full((len(me.loops), 2), 0.5); has_uv = False
        col = None
        ca = me.color_attributes.active_color if hasattr(me, 'color_attributes') else None
        if ca is not None and len(ca.data):
            c = np.empty(len(ca.data) * 4, np.float32); ca.data.foreach_get('color', c)
            c = c.reshape(-1, 4)
            col = c[loops] if ca.domain == 'CORNER' else c[verts]
        mw = np.array(mw if mw is not None else ob.matrix_world)
        m3 = mw[:3, :3]
        det = float(np.linalg.det(m3))
        pos = co[verts] @ m3.T + mw[:3, 3]
        nm = np.linalg.inv(m3).T if abs(det) > 1e-20 else np.eye(3)
        nrm = cn[loops] @ nm.T
        ln = np.linalg.norm(nrm, axis=1, keepdims=True)
        nrm = np.where(ln > 1e-12, nrm / np.maximum(ln, 1e-12), 0.0)
        pos = pos.reshape(n, 3, 3); nrm = nrm.reshape(n, 3, 3); uvt = uv[loops].reshape(n, 3, 2)
        auto_uv = False
        if not has_uv:
            # no UV map: a box projection, 1 uv unit = 1 metre - every triangle is laid on the world plane its face looks
            # along. Good enough for a tiling material; the Check tab says which objects got it (geo.auto_uv).
            fn = np.cross(pos[:, 1] - pos[:, 0], pos[:, 2] - pos[:, 0])
            ax = np.argmax(np.abs(fn), axis=1)
            ua = np.array([1, 0, 0])[ax]
            va = np.array([2, 2, 1])[ax]
            rows = np.arange(n)[:, None]
            uvt = np.stack([pos[rows, np.arange(3)[None, :], ua[:, None]], pos[rows, np.arange(3)[None, :], va[:, None]]], -1)
            has_uv, auto_uv = True, True
        if col is not None:
            col = col.reshape(n, 3, 4)
        if det < 0:                                   # mirrored object: keep the faces' outside outside
            pos = pos[:, ::-1]; nrm = nrm[:, ::-1]; uvt = uvt[:, ::-1]
            if col is not None:
                col = col[:, ::-1]
        slots = [s.material.name if s.material else '' for s in ev.material_slots]
        if not slots or not any(slots):                # Geometry Nodes instances carry their materials on the mesh
            slots = [m.name if m else '' for m in me.materials] or slots
        slots = slots or ['']
        mi = np.clip(mi, 0, len(slots) - 1)
        out = {'pos': pos, 'nrm': nrm.astype(np.float32), 'uv': uvt, 'mat': mi.astype(np.int16), 'slots': np.array(slots),
               'has_uv': np.array(has_uv), 'auto_uv': np.array(auto_uv), 'det': np.array(det), 'scale': np.linalg.norm(m3, axis=0)}
        if col is not None:
            out['col'] = col.astype(np.float32)
        return out
    finally:
        ev.to_mesh_clear()


# ---- vertex-colour tints: a material that multiplies by a Color Attribute, on an object whose colour attribute is ONE
# colour (LEGO rips: 4,381 of 5,526 objects of LSWTSS Utapau, 2026-10-23), is exact as a copy with that colour as a
# constant. The bake renders on a flat square where the attribute does not exist, so without this the tint was lost.
TINT_STEP = 32.0          # colours are rounded to 1/32 so nearly equal tints share one copy
TINT_SPREAD = 0.02        # the most an object's colours may differ and still count as one colour


def _vcol_nodes(tree, depth=0):
    """the Color Attribute nodes of a node tree, also inside its groups"""
    out = []
    for n in tree.nodes:
        if n.type == 'VERTEX_COLOR':
            out.append(n)
        elif n.type == 'GROUP' and n.node_tree is not None and depth < 6:
            out += _vcol_nodes(n.node_tree, depth + 1)
    return out


def reads_vcol(mat):
    return bool(mat and mat.use_nodes and mat.node_tree and _vcol_nodes(mat.node_tree))


def tinted_copy(mat, rgba, name):
    """a copy of `mat` whose Color Attribute nodes give the constant `rgba` (scene-linear); groups that hold such a node
    are copied too, so the original material and its other users are untouched"""
    m = mat.copy()
    m.name = name[:63]

    def fix(tree, depth=0):
        for n in list(tree.nodes):
            if n.type == 'GROUP' and n.node_tree is not None and depth < 6 and _vcol_nodes(n.node_tree):
                n.node_tree = n.node_tree.copy()
                fix(n.node_tree, depth + 1)
        for n in [x for x in tree.nodes if x.type == 'VERTEX_COLOR']:
            rgb = tree.nodes.new('ShaderNodeRGB'); rgb.outputs[0].default_value = (rgba[0], rgba[1], rgba[2], 1.0)
            val = tree.nodes.new('ShaderNodeValue'); val.outputs[0].default_value = rgba[3]
            rgb.location = val.location = n.location
            for out_name, src in (('Color', rgb.outputs[0]), ('Alpha', val.outputs[0])):
                for l in list(n.outputs[out_name].links):
                    tree.links.new(src, l.to_socket)
            tree.nodes.remove(n)
    fix(m.node_tree)
    return m


class Tints:
    """decides, per object, which faces use a tinted copy of their material; the copies are made when the materials are
    recorded (`make`)"""

    def __init__(self, on, per_material):
        self.on, self.per_material = on, per_material
        self.reads = {}                 # material name -> reads a Color Attribute
        self.by_mat = {}                # material name -> {quantised rgba: variant name}
        self.variants = {}              # variant name -> (material name, rgba)
        self.objects, self.varying, self.no_colour = 0, [], 0

    def _reads(self, name):
        if name not in self.reads:
            self.reads[name] = reads_vcol(bpy.data.materials.get(name))
        return self.reads[name]

    def apply(self, a, oname):
        """rewrite a mesh_arrays() result's slot names for its tinted faces (before it is saved)"""
        if not self.on:
            return
        slots = [str(s) for s in a['slots']]
        reading = [i for i, s in enumerate(slots) if s and self._reads(s)]
        if not reading:
            return
        if 'col' not in a:
            self.no_colour += 1
            return
        col = a['col']
        changed = False
        for i in reading:
            c = col[a['mat'] == i].reshape(-1, 4)
            if not len(c):
                continue
            if float((c.max(0) - c.min(0))[:3].max()) > TINT_SPREAD:
                self.varying.append(oname)
                continue
            q = tuple(round(float(x) * TINT_STEP) / TINT_STEP for x in c.mean(0))
            slots[i] = self._variant(slots[i], q)
            changed = True
        if changed:
            self.objects += 1
            a['slots'] = np.array(slots)

    def _variant(self, mname, q):
        have = self.by_mat.setdefault(mname, {})
        if q not in have:
            if len(have) >= self.per_material:
                near = min(have, key=lambda k: sum((x - y) ** 2 for x, y in zip(k, q)))
                return have[near]
            vname = '%s#tint%02x%02x%02x%02x' % ((mname,) + tuple(int(round(x * 255)) for x in q))
            have[q] = vname
            self.variants[vname] = (mname, q)
        return have[q]

    def make(self, vname):
        mname, q = self.variants[vname]
        return tinted_copy(bpy.data.materials[mname], q, vname)

    def summary(self):
        return {'on': self.on, 'objects_tinted': self.objects, 'variants': len(self.variants),
                'materials_with_variants': len(self.by_mat), 'objects_varying': len(set(self.varying)),
                'varying_examples': sorted(set(self.varying))[:20], 'objects_without_colour_attribute': self.no_colour}


# ---- glowing cut-outs (a lit grate, a sign with a see-through background): ONE material that has alpha holes AND
# emission. The game has no surface the tool can use for both yet (the shipped emissive alpha-test surface reads
# smoothness / metal from the usual channels but its holes and glow from elsewhere - MatTest probe, 2026-10-23), so it
# is built from two surfaces proven in game: the cut-out (holes, lit, solid) and, 2 mm in front of the same faces,
# the additive hologram surface with a still image (its texture is the glow x the alpha, so the holes stay clear).
GLOW_SUFFIX = '#glow'
GLOW_OFFSET = 0.002       # metres in front of the cut-out faces


def is_glowing_cutout(mat):
    """does the material have a WIRED alpha AND real emission (class Cut-out, glowing - or Opaque left on detect)?"""
    if mat is None:
        return False
    b = mat.bf2
    if b.cls == 'cutout_emissive':
        return True
    if b.cls != 'opaque' or not b.cls_auto:
        return False
    p = _principled(mat)[0]
    if p is None:
        return False
    def sock(*names):
        return next((p.inputs[n] for n in names if n in p.inputs), None)
    al, em, es = sock('Alpha'), sock('Emission Color', 'Emission'), sock('Emission Strength')
    if al is None or not al.is_linked or em is None:
        return False
    strength_on = es is None or es.is_linked or es.default_value > 0.0
    colour_on = em.is_linked or max(em.default_value[:3]) > 0.0
    return strength_on and colour_on


class Glow:
    """adds the glow layer's faces to an object's triangles (before they are saved) and records which materials glow"""

    def __init__(self):
        self.reads = {}
        self.materials = set()
        self.objects = 0

    def apply(self, a):
        slots = [str(s) for s in a['slots']]
        for s in slots:
            if s and s not in self.reads:        # a tinted copy glows when its material does
                self.reads[s] = is_glowing_cutout(bpy.data.materials.get(s.split('#tint')[0]))
        glow = [i for i, s in enumerate(slots) if s and self.reads[s]]
        if not glow:
            return
        sel = np.isin(a['mat'], glow)
        if not sel.any():
            return
        fn = np.cross(a['pos'][sel, 1] - a['pos'][sel, 0], a['pos'][sel, 2] - a['pos'][sel, 0])
        fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-12)
        add = {k: a[k][sel] for k in ('pos', 'nrm', 'uv', 'mat') + (('col',) if 'col' in a else ())}
        add['pos'] = add['pos'] + fn[:, None, :] * GLOW_OFFSET
        # the glow faces use new slots: '<material>#glow'
        new_slots = list(slots)
        remap = {}
        for i in glow:
            remap[i] = len(new_slots)
            new_slots.append(slots[i] + GLOW_SUFFIX)
            self.materials.add(slots[i])
        add['mat'] = np.array([remap[int(m)] for m in add['mat']], a['mat'].dtype)
        for k, v in add.items():
            a[k] = np.concatenate([a[k], v])
        a['slots'] = np.array(new_slots)
        self.objects += 1


DEFAULT_MATERIAL = 'BF2 default grey'


def default_slots(a, oname, defaulted):
    """faces without a material get a plain grey one - what Blender draws for them - instead of stopping the build
    (Tantive IV interior rip, 2026-10-23); the object is listed for the check geo.no_material (a warning)"""
    slots = [str(s) for s in a['slots']]
    used = set(int(x) for x in np.unique(a['mat']))
    if not any(not slots[i] for i in used if i < len(slots)):
        return
    if DEFAULT_MATERIAL not in bpy.data.materials:
        m = bpy.data.materials.new(DEFAULT_MATERIAL)
        m.use_nodes = True
        p = m.node_tree.nodes.get('Principled BSDF')
        if p is not None:
            p.inputs['Base Color'].default_value = (0.6, 0.6, 0.6, 1.0)
    a['slots'] = np.array([s or DEFAULT_MATERIAL for s in slots])
    defaulted.append(oname)


def _basis(ob):
    m = ob.matrix_world.to_3x3().normalized()
    return [list(m.col[0]), list(m.col[1]), list(m.col[2])]


def _box(ob):
    """world oriented box of an object: centre, half extents along its own axes, axes (columns)"""
    mw = ob.matrix_world
    bb = [Vector(c) for c in ob.bound_box] if ob.type == 'MESH' else [Vector((x, y, z)) for x in (-1, 1) for y in (-1, 1) for z in (-1, 1)]
    lo = Vector([min(c[i] for c in bb) for i in range(3)]); hi = Vector([max(c[i] for c in bb) for i in range(3)])
    sc = mw.to_scale()
    return {'centre': list(mw @ ((lo + hi) / 2)), 'half': [abs((hi[i] - lo[i]) / 2 * sc[i]) for i in range(3)], 'axes': _basis(ob)}


def _yaw(ob):
    f = ob.matrix_world.to_3x3() @ Vector((0.0, -1.0, 0.0))      # an empty's "front" is -Y, as Blender's cameras / characters face
    return math.atan2(f.x, f.y)


def find_collision_object(ob, scene_objects):
    b = ob.bf2
    if b.collision_object is not None:
        return b.collision_object
    for c in scene_objects:
        n = c.name.lower()
        if n == ob.name.lower() + '_col' or (n.startswith('ucx_' + ob.name.lower())):
            return c
    for c in ob.children:
        if c.name.lower().startswith('ucx_') or c.bf2.role == 'collision':
            return c
    return None


def undo_game_look(scene):
    """material slots that show the "Game look" copies point to their originals again (gamelook.py): the export always
    reads the user's own materials"""
    n = 0
    for o in scene.objects:
        for s in o.material_slots:
            m = s.material
            if m is not None and m.get('bf2_game_look_of') and m['bf2_game_look_of'] in bpy.data.materials:
                s.material = bpy.data.materials[m['bf2_game_look_of']]
                n += 1
    return n


def export(out_dir, scene=None, log=print):
    ensure_props()
    scene = scene or bpy.context.scene
    if undo_game_look(scene):
        log('game look: the export reads the original materials')
    out_dir = os.path.abspath(out_dir)
    os.makedirs(os.path.join(out_dir, 'geo'), exist_ok=True)
    dg = bpy.context.evaluated_depsgraph_get()
    vl = set(o.name for o in bpy.context.view_layer.objects)
    sp = scene.bf2
    images = Images(out_dir)
    objs = [o for o in scene.objects if o.name in vl]
    col_users = {}
    for o in objs:
        if o.type == 'MESH' and o.bf2.role in ('static', 'door') and o.bf2.collision == 'custom':
            c = find_collision_object(o, objs)
            if c is not None:
                col_users[c.name] = o.name
    M = {'version': MAP_VERSION,
         'map': {'name': sp.name, 'display_name': sp.display_name or sp.name, 'planet': sp.planet or sp.name, 'description': sp.description,
                 'era': sp.era, 'host': 'kamino_01',
                 'modes': [m for m in ('blast', 'hvv', 'arcade', 'supremacy') if getattr(sp, 'mode_' + m)],
                 'piece_tris': int(getattr(sp, 'piece_tris', 3000)), 'piece_far': float(getattr(sp, 'piece_far', 0.5)), 'piece_ext': float(getattr(sp, 'piece_ext', 48.0))},
         'blend': {'file': bpy.data.filepath.replace('\\', '/'), 'blender': bpy.app.version_string,
                   'unit_scale': scene.unit_settings.scale_length},
         'objects': [], 'materials': {}, 'lights': [], 'spawns': [], 'command_posts': [], 'kill_volumes': [], 'doors': [],
         'not_walkable': [], 'combat_area': None, 'reflection_probes': [], 'markers': [], 'skipped': []}
    used_mats = set()
    names_done = set()
    defaulted = []
    glow = Glow()
    tints = Tints(bool(getattr(sp, 'tint_variants', True)), int(getattr(sp, 'tint_max', 16)))

    def geo_file(name):
        base = safe(name); k = 2
        while base.lower() in names_done:
            base = '%s_%d' % (safe(name), k); k += 1
        names_done.add(base.lower())
        return 'geo/%s.npz' % base

    import time as _time
    _t0 = _tlast = _time.time()
    _all = sorted(objs, key=lambda x: x.name)
    for _k, o in enumerate(_all):
        if _time.time() - _tlast > 30.0:
            _tlast = _time.time()
            log('objects: %d of %d read (%.0f s)' % (_k, len(_all), _tlast - _t0))
        b = o.bf2
        role = b.role
        if o.name in col_users and role == 'static':
            role = 'collision'
        if role in ('skip', 'reference') or o.get('bf2_reference'):      # the soldier figure, whatever its role was set to
            M['skipped'].append({'name': o.name, 'why': 'reference' if o.get('bf2_reference') else role}); continue
        if o.type == 'LIGHT':
            d = o.data
            fwd = o.matrix_world.to_3x3() @ Vector((0.0, 0.0, -1.0))
            L = {'name': o.name, 'type': d.type.lower(), 'pos': list(o.matrix_world.translation), 'dir': list(fwd.normalized()),
                 'color': list(d.color), 'energy': float(d.energy), 'radius': float(getattr(d, 'shadow_soft_size', 0.0)),
                 'mode': d.bf2.mode, 'shadows': bool(d.bf2.shadows), 'bake_strength': float(d.bf2.bake_strength), 'hidden': bool(o.hide_render),
                 'cutoff': float(d.cutoff_distance) if getattr(d, 'use_custom_distance', False) else 0.0}
            if getattr(d, 'use_temperature', False):         # a colour temperature multiplies the lamp's colour
                tc = getattr(d, 'temperature_color', None)
                if tc is not None:
                    L['color'] = [float(a) * float(b) for a, b in zip(d.color, tc)]
            if d.type == 'SUN':
                L['angle'] = float(getattr(d, 'angle', 0.0))  # the sun's diameter in radians
            if d.type == 'SPOT':
                L.update(spot_size=float(d.spot_size), spot_blend=float(d.spot_blend))
            if d.type == 'AREA':
                L.update(shape=d.shape.lower(), size=float(d.size), size_y=float(d.size_y if d.shape in ('RECTANGLE', 'ELLIPSE') else d.size),
                         axes=_basis(o))
            M['lights'].append(L)
            continue
        if role in ('spawn', 'hero_spawn'):
            M['spawns'].append({'name': o.name, 'pos': list(o.matrix_world.translation), 'yaw': _yaw(o), 'team': b.team, 'hero': role == 'hero_spawn'})
            continue
        if role == 'command_post':
            M['command_posts'].append({'name': o.name, 'pos': list(o.matrix_world.translation), 'yaw': _yaw(o), 'owner': b.team,
                                       'letter': (b.cp_letter or 'A').upper(), 'radius': b.cp_radius, 'order': b.cp_order, 'vehicles': bool(b.cp_vehicles),
                                       'spawns': [c.name for c in o.children if c.bf2.role == 'spawn']})
            continue
        if role == 'kill_volume':
            M['kill_volumes'].append(dict(_box(o), name=o.name)); continue
        if role == 'not_walkable':
            M['not_walkable'].append(dict(_box(o), name=o.name)); continue
        if role in ('reflection_probe', 'sound', 'fx'):
            rec = {'name': o.name, 'kind': role, 'pos': list(o.matrix_world.translation), 'preset': b.preset}
            if role == 'reflection_probe':
                # the sphere the probe serves: the empty's drawn size times its scale (a mesh: half its largest side)
                size = (o.empty_display_size if o.type == 'EMPTY' else max(o.dimensions) / 2.0) * max(o.matrix_world.to_scale())
                rec['radius'] = float(max(size, 1.0))
            (M['reflection_probes'] if role == 'reflection_probe' else M['markers']).append(rec)
            continue
        if o.type != 'MESH':
            if o.type in ('CURVE', 'SURFACE', 'FONT', 'META') and role == 'static':
                pass                                   # evaluated to a mesh below
            else:
                continue
        if role == 'combat_area':
            a = mesh_arrays(o, dg)
            if a is not None:
                pts = a['pos'].reshape(-1, 3)[:, :2]
                M['combat_area'] = {'name': o.name, 'points': np.unique(np.round(pts, 3), axis=0).tolist()}
            continue
        if o.hide_render and role != 'collision':
            M['skipped'].append({'name': o.name, 'why': 'hidden in renders'}); continue
        a = mesh_arrays(o, dg)
        if a is None:
            M['skipped'].append({'name': o.name, 'why': 'no faces'}); continue
        gf = geo_file(o.name)
        if role != 'collision':
            default_slots(a, o.name, defaulted)
            tints.apply(a, o.name)
            glow.apply(a)
        np.savez_compressed(os.path.join(out_dir, gf), **a)
        rec = {'name': o.name, 'geo': gf, 'tris': int(len(a['pos'])), 'role': role, 'collision': b.collision,
               'scale': [float(x) for x in o.matrix_world.to_scale()], 'negative_scale': bool(float(a['det']) < 0),
               'has_uv': bool(a['has_uv']), 'auto_uv': bool(a['auto_uv']), 'modifiers': [m.type for m in o.modifiers if m.show_render]}
        if role == 'collision':
            rec['collision_for'] = col_users.get(o.name)
        else:
            if b.collision == 'custom':
                c = find_collision_object(o, objs)
                rec['collision_object'] = c.name if c else None
            for s in a['slots']:
                if str(s):
                    used_mats.add(str(s))
        if role == 'door':
            rec['door'] = {'axis': b.door_axis, 'distance': b.door_distance, 'axes': _basis(o), 'origin': list(o.matrix_world.translation)}
            M['doors'].append(o.name)
        M['objects'].append(rec)
    # what instancers draw - collection instances, Geometry Nodes instances, particle / hair instances: these are not in
    # scene.objects, only in the depsgraph (before 0.2.0 they were silently left out of the map)
    n_inst, n_inst_tris, inst_skip = 0, 0, {}
    _tlast = _time.time()
    for inst in dg.object_instances:
        if not inst.is_instance or inst.parent is None:
            continue
        par, ev = inst.parent.original, inst.object
        src = ev.original
        if par.name not in vl:
            continue
        if _time.time() - _tlast > 30.0:
            _tlast = _time.time()
            log('instances: %d read' % n_inst)
        prole = 'collision' if par.name in col_users else par.bf2.role
        srole = src.bf2.role if src is not par else prole
        why = None
        if prole in ('skip', 'reference') or srole in ('skip', 'reference') or src.get('bf2_reference') or par.get('bf2_reference'):
            why = 'instancer or instanced object is "Leave out"'
        elif par.hide_render or (src is not par and src.hide_render):
            why = 'hidden in renders'
        elif ev.type not in ('MESH', 'CURVE', 'SURFACE', 'FONT', 'META'):
            why = None if ev.type in ('EMPTY', 'LIGHT', 'CAMERA') else 'not geometry (%s)' % ev.type
            if why is None:
                continue
        elif srole not in ('static', 'collision'):
            why = 'role "%s" inside an instance is not supported (make the instance real: Object > Apply > Make Instances Real)' % srole
        if why:
            inst_skip[why] = inst_skip.get(why, 0) + 1
            continue
        a = mesh_arrays(src, dg, ev=ev, mw=inst.matrix_world.copy())
        if a is None:
            continue
        name = '%s|%s|%d' % (par.name, src.name, n_inst + 1)
        gf = geo_file(name)
        default_slots(a, name, defaulted)
        tints.apply(a, name)
        glow.apply(a)
        np.savez_compressed(os.path.join(out_dir, gf), **a)
        b = src.bf2 if src is not par else par.bf2
        rec = {'name': name, 'geo': gf, 'tris': int(len(a['pos'])), 'role': 'collision' if 'collision' in (prole, srole) else 'static',
               'collision': b.collision if b.collision != 'custom' else 'exact', 'scale': [float(x) for x in a['scale']],
               'negative_scale': bool(float(a['det']) < 0), 'has_uv': bool(a['has_uv']), 'auto_uv': bool(a['auto_uv']),
               'modifiers': [], 'instance_of': src.name, 'instancer': par.name}
        if rec['role'] == 'static':
            for s in a['slots']:
                if str(s):
                    used_mats.add(str(s))
        M['objects'].append(rec)
        n_inst += 1
        n_inst_tris += rec['tris']
    M['instances'] = {'objects': n_inst, 'triangles': n_inst_tris, 'skipped': inst_skip}
    if n_inst or inst_skip:
        log('instances: %d exported (%d triangles)%s' % (n_inst, n_inst_tris, ''.join('; %d skipped: %s' % (v, k) for k, v in inst_skip.items())))
    bake_mode = getattr(sp, 'bake_materials', 'auto')
    baker = None if bake_mode == 'never' else matbake.Baker(out_dir, write_png, safe, int(getattr(sp, 'bake_max_size', '2048')), log)
    _t0 = _tlast = _time.time()
    made = {}                                  # a tinted copy is made once, also when its glow layer needs it
    for _k, name in enumerate(sorted(used_mats)):
        if _time.time() - _tlast > 30.0:
            _tlast = _time.time()
            log('materials: %d of %d read (%.0f s)' % (_k, len(used_mats), _tlast - _t0))
        is_glow = name.endswith(GLOW_SUFFIX)
        base = name[:-len(GLOW_SUFFIX)] if is_glow else name
        mat = made.get(base) or (tints.make(base) if base in tints.variants else bpy.data.materials[base])
        made[base] = mat
        rec = material_record(mat, images, baker, bake_mode)
        if base in tints.variants:
            rec['tint_of'] = tints.variants[base][0]
            rec['tint'] = [round(x, 4) for x in tints.variants[base][1]]
        if is_glow:
            # the glow layer of a glowing cut-out: the additive hologram surface, still, showing emission x alpha
            rec.update({'class': 'holo', 'class_auto': False, 'glow_of': base,
                        'holo': {'speed': [0.0, 0.0], 'tiling': [1.0, 1.0], 'brightness': 1.0, 'image': ''}})
        elif base in glow.materials or (glow.reads.get(base) and mat.bf2.cls in ('cutout_emissive', 'opaque')):
            # the solid, lit part of a glowing cut-out
            two = rec.get('double_sided') and not rec.get('backface_culling')
            rec.update({'class': 'cutout2s' if two else 'cutout', 'class_auto': False, 'glowing_cutout': True})
        if rec.get('class') == 'cutout_emissive':          # the runtime builds it as the two surfaces above, never by this name
            rec['class'] = 'cutout'
        M['materials'][name] = rec
    if baker is not None:
        baker.close()
        M['bake'] = {'mode': bake_mode, 'bakes': baker.count, 'seconds': round(baker.seconds, 1), 'failed': baker.failed,
                     'materials': sorted(n for n, r in M['materials'].items() if r.get('baked'))}
        log('materials baked: %d (%d renders, %.1f s)%s' % (len(M['bake']['materials']), baker.count, baker.seconds,
                                                           '; FAILED: %s' % baker.failed if baker.failed else ''))
    # play area: the spawns / command posts when there are any, else the box of the map geometry
    pts = [s['pos'] for s in M['spawns']] + [c['pos'] for c in M['command_posts']]
    if pts:
        P = np.array(pts); c = (P.min(0) + P.max(0)) / 2
        r = float(np.linalg.norm(P - c, axis=1).max()) + 40.0
    else:
        los, his = [], []
        for rec in M['objects']:
            if rec['role'] == 'static':
                z = np.load(os.path.join(out_dir, rec['geo']))
                p = z['pos']
                # faces of a left-out material (a sky dome inside the one mesh of a ripped map) do not decide the play area
                keep = np.array([not (M['materials'].get(str(s)) or {}).get('skip') for s in z['slots']])
                if len(keep) and not keep.all():
                    p = p[keep[np.clip(z['mat'], 0, len(keep) - 1)]]
                if not len(p):
                    continue
                p = p.reshape(-1, 3); los.append(p.min(0)); his.append(p.max(0))
        # a terrain or backdrop many times larger than everything else must not decide the play area: when most
        # objects are small, only those under 500 m across are measured
        small = [i for i in range(len(los)) if float((his[i] - los[i]).max()) < 500.0]
        if los and len(small) >= max(3, len(los) // 2) and len(small) < len(los):
            M.setdefault('notes', []).append('play area measured from the %d object(s) under 500 m across; %d larger one(s) ignored' % (
                len(small), len(los) - len(small)))
            los, his = [los[i] for i in small], [his[i] for i in small]
        if los:
            lo, hi = np.min(los, 0), np.max(his, 0); c = (lo + hi) / 2
            r = float(np.linalg.norm((hi - lo)[:2]) / 2)
        else:
            c, r = np.zeros(3), 100.0
    M['play'] = {'centre': [float(x) for x in c], 'radius': sp.play_radius or r, 'keep_distance': float(sp.keep_distance), 'measured': not pts and 'geometry box' or 'spawns and command posts'}
    w = scene.world
    # the game has ONE sun: the strongest visible Sun lamp that shines downwards (a scene may hold more, as fill lights)
    suns = [l for l in M['lights'] if l['type'] == 'sun' and not l['hidden']]
    sun = max(suns, key=lambda l: (l['dir'][2] < 0.0, float(l.get('energy', 0.0)))) if suns else None
    world_mode = sp.sky == 'world'
    M['look'] = {'sky': sp.sky, 'sky_hdr': bpy.path.abspath(sp.sky_hdr).replace('\\', '/') if sp.sky_hdr else '', 'sky_strength': 1.0 if world_mode else sp.sky_strength,
                 # in Blender World mode the scene decides: a Sun lamp that is there shines, and there is fog only when asked for
                 'sun_on': (sun is not None) if world_mode else bool(sp.sun_on), 'sun': sun, 'exposure': sp.exposure,
                 'suns': [l['name'] for l in suns],
                 'fog': {'color': list(sp.fog_color), 'start': sp.fog_start, 'end': sp.fog_end,
                         'density': sp.fog_density if (sp.fog_on or not world_mode) else 0.0, 'on': bool(sp.fog_on or not world_mode)},
                 'world_color': list(w.color) if w else [0.05, 0.05, 0.05], 'ev_level': sp.ev_level,
                 # game sky: a shipped environment taken whole (runtime look.GAME_SKIES)
                 'game_sky': getattr(sp, 'game_sky', 'tatooine_sunny'), 'game_sky_asset': getattr(sp, 'game_sky_asset', ''),
                 'sun_from_scene': bool(getattr(sp, 'sun_from_scene', True)), 'exposure_mode': getattr(sp, 'exposure_mode', 'outdoor')}
    # what Blender itself does with the picture: the runtime needs it to hand the same light to the game
    vs = scene.view_settings
    M['look']['blender'] = {'view_transform': vs.view_transform, 'look': vs.look, 'exposure': float(vs.exposure), 'gamma': float(vs.gamma)}
    # does the World hold a picture / a sky model, or only a colour (the checks warn when a preset sky would replace a picture)
    wn = [n.type for n in w.node_tree.nodes] if (w is not None and getattr(w, 'use_nodes', True) and w.node_tree) else []
    M['look']['world_kind'] = 'none' if w is None else ('image' if any(t in ('TEX_ENVIRONMENT', 'TEX_SKY', 'TEX_IMAGE') for t in wn) else 'colour')
    if sp.sky == 'world':
        wb = worldbake.bake(scene, os.path.join(out_dir, 'world.hdr'), 2048, log) if w is not None else None
        M['look']['world_hdr'] = wb['file'] if wb else ''
        M['look']['world_name'] = w.name if w is not None else ''
        # what the scene looks like from inside, for the game's reflections (shiny and metal surfaces)
        try:
            pt, free = reflbake.pick_point(scene, [q['pos'] for q in M['spawns']] + [q['pos'] for q in M['command_posts']], M['play']['centre'])
            rc = reflbake.capture(scene, pt, os.path.join(out_dir, 'reflection.hdr'), 512, log)
            if rc:
                rc['clearance'] = round(free, 2)
                M['look']['reflection'] = rc
                log('reflection capture at (%.1f, %.1f, %.1f), %.1f m of free space around it' % (pt[0], pt[1], pt[2], free))
        except Exception as e:                      # reflections are an extra: the port goes on without them
            log('reflection capture skipped: %s' % e)
        # extra probes: each serves the rooms inside its sphere with what is seen from its own point
        for k, pr in enumerate(M['reflection_probes']):
            try:
                rc = reflbake.capture(scene, pr['pos'], os.path.join(out_dir, 'reflection_probe_%02d.hdr' % (k + 1)), 512, log)
                if rc:
                    pr.update(file=rc['file'], width=rc['width'], height=rc['height'])
                    log('reflection probe "%s" captured at (%.1f, %.1f, %.1f), radius %.1f m' % (pr['name'], pr['pos'][0], pr['pos'][1], pr['pos'][2], pr['radius']))
            except Exception as e:
                log('reflection probe "%s" skipped: %s' % (pr['name'], e))
    if sun is not None and M['look']['sun_on']:
        try:
            M['look']['sun_seal'] = sunseal.seal(scene, M, out_dir, sun['dir'], log)
        except Exception as e:                      # the port goes on; the sun may then shine through one-sided roofs
            log('sun seal skipped: %s' % e)
    # the loading-screen picture, from the menu camera (or the scene camera)
    cam = reflbake.menu_camera(scene) if getattr(sp, 'menu_picture', True) else None
    if cam is not None:
        try:
            t0 = _time.time()
            mp = reflbake.menu_picture(scene, cam, os.path.join(out_dir, 'menu_picture.png'), log)
            if mp:
                M['map']['menu_picture'] = mp
                log('loading picture rendered from camera "%s" (%.0f s)' % (cam.name, _time.time() - t0))
        except Exception as e:                      # an extra: the port goes on with the host's loading screen
            log('loading picture skipped: %s' % e)
    M['tints'] = tints.summary()
    M['glowing_cutouts'] = {'materials': sorted(glow.materials), 'objects': glow.objects}
    M['default_material_objects'] = defaulted
    if tints.variants or tints.varying:
        log('vertex-colour tints: %d object(s) tinted, %d tinted material copies of %d material(s); %d object(s) with varying colours keep '
            'the plain material' % (tints.objects, len(tints.variants), len(tints.by_mat), len(set(tints.varying))))
    M['options'] = {'auto_spawns': bool(sp.auto_spawns), 'bake_quality': sp.bake_quality, 'dynamic_light_budget': sp.dynamic_light_budget}
    with open(os.path.join(out_dir, 'map.json'), 'w', encoding='utf-8') as f:
        json.dump(M, f, indent=1)
    log('EXPORT DONE objects %d (triangles %d), materials %d, images %d, lights %d, spawns %d, command posts %d, kill volumes %d, doors %d -> %s'
        % (len(M['objects']), sum(o['tris'] for o in M['objects']), len(M['materials']), len(images.done), len(M['lights']),
           len(M['spawns']), len(M['command_posts']), len(M['kill_volumes']), len(M['doors']), out_dir))
    return M


if __name__ == '__main__':
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    if not argv:
        print('usage: blender -b <file.blend> --python export_scene.py -- <out dir>')
        sys.exit(2)
    export(argv[0], log=lambda m: print(m, flush=True))
