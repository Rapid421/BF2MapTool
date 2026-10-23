"""Bake what a material's node graph does into plain textures, so that ANY Blender material packs correctly.

The exporter follows a Principled BSDF input back to an image or a constant. Where that is not the whole truth - a mix
of two images, a colour ramp, a Bump node, a Mapping node that tiles or turns the image, a procedural texture, a
shader that is not a Principled BSDF at all - the input is RENDERED instead: the material is put on a flat square
whose UVs run 0..1 and Cycles bakes the value that reaches the socket (through an Emission shader, so no lighting
is involved). The result is exactly what Blender shows for every material that is a function of its UVs.

  base colour, roughness, metallic, alpha   the socket's source -> Emission -> bake EMIT
  emission                                  bake EMIT of the material itself = colour x strength (float); the brightest
                                            value becomes the strength, the picture is stored normalised
  normal                                    bake NORMAL, tangent space, +X +Y +Z = OpenGL convention (so the packer
                                            needs no guess for a baked normal map)
  a shader without a Principled BSDF        bake DIFFUSE colour / ROUGHNESS / EMIT / NORMAL of the whole shader

NOT exact, and said so in the record ('approximate'): materials that read object or world coordinates, vertex
colours, attributes, the view angle, or a second UV map - on the flat square those are not what they are on the map.
Only ever run on a COPY of the scene (the build exports from a saved copy in a background Blender).
"""
import math
import os

import bpy
import numpy as np

SPACE_NODES = {'NEW_GEOMETRY': 'Geometry', 'OBJECT_INFO': 'Object Info', 'ATTRIBUTE': 'Attribute', 'VERTEX_COLOR': 'Color Attribute',
               'AMBIENT_OCCLUSION': 'Ambient Occlusion', 'BEVEL': 'Bevel', 'LAYER_WEIGHT': 'Layer Weight', 'FRESNEL': 'Fresnel',
               'CAMERA': 'Camera Data', 'LIGHT_PATH': 'Light Path', 'PARTICLE_INFO': 'Particle Info', 'HAIR_INFO': 'Curves Info',
               'WIREFRAME': 'Wireframe', 'TANGENT': 'Tangent'}
PROCEDURAL = ('TEX_NOISE', 'TEX_VORONOI', 'TEX_WAVE', 'TEX_MAGIC', 'TEX_CHECKER', 'TEX_BRICK', 'TEX_GRADIENT', 'TEX_MUSGRAVE', 'TEX_WHITE_NOISE',
              'TEX_GABOR')


def _walk(tree, depth=0):
    for n in tree.nodes:
        yield n
        if n.type == 'GROUP' and n.node_tree is not None and depth < 6:
            yield from _walk(n.node_tree, depth + 1)


def survey(mat):
    """what the material's graph contains that decides size and exactness -> dict"""
    out = {'max_image': 0, 'image_wh': (0, 0), 'tiling': 1.0, 'space': [], 'uv_maps': set(), 'procedural': False}
    if not mat.use_nodes or not mat.node_tree:
        return out
    for n in _walk(mat.node_tree):
        if n.type == 'TEX_IMAGE' and n.image is not None:
            out['max_image'] = max(out['max_image'], n.image.size[0], n.image.size[1])
            if n.image.size[0] * n.image.size[1] > out['image_wh'][0] * out['image_wh'][1]:
                out['image_wh'] = (n.image.size[0], n.image.size[1])
            if n.projection != 'FLAT':
                out['space'].append('Image Texture (%s projection)' % n.projection.title())
        elif n.type == 'MAPPING':
            try:
                s = n.inputs['Scale'].default_value
                out['tiling'] = max(out['tiling'], abs(s[0]), abs(s[1]))
            except Exception:
                pass
        elif n.type == 'TEX_COORD':
            used = [o.name for o in n.outputs if o.is_linked and o.name != 'UV']
            if used:
                out['space'].append('Texture Coordinate (%s)' % ', '.join(used))
        elif n.type == 'UVMAP':
            out['uv_maps'].add(n.uv_map)
        elif n.type in SPACE_NODES:
            if any(o.is_linked for o in n.outputs):
                out['space'].append(SPACE_NODES[n.type])
        elif n.type in PROCEDURAL:
            out['procedural'] = True
            v = n.inputs.get('Vector')
            if v is not None and not v.is_linked:
                out['space'].append('%s without a UV input (object coordinates)' % (n.bl_label or n.type))
    return out


def uv_transform(image_node):
    """a description of what turns / tiles / replaces the UVs of an Image Texture node, or '' for plain UVs"""
    if image_node.projection != 'FLAT':
        return '%s projection' % image_node.projection.title()
    v = image_node.inputs.get('Vector')
    cur, n_seen = v, 0
    while cur is not None and cur.is_linked and n_seen < 12:
        n_seen += 1
        link = cur.links[0]
        n = link.from_node
        if n.type == 'MAPPING':
            def val(name, ident):
                s = n.inputs.get(name)
                if s is None:
                    return False
                if s.is_linked:
                    return True
                return any(abs(a - ident) > 1e-5 for a in s.default_value)
            if val('Scale', 1.0) or val('Rotation', 0.0) or val('Location', 0.0):
                return 'a Mapping node moves, turns or tiles the image'
            cur = n.inputs.get('Vector')
        elif n.type == 'TEX_COORD':
            return '' if link.from_socket.name == 'UV' else 'Texture Coordinate (%s)' % link.from_socket.name
        elif n.type == 'UVMAP':
            return ''
        elif n.type == 'REROUTE':
            cur = n.inputs[0]
        elif n.type in ('GROUP', 'GROUP_INPUT'):
            return 'the image coordinates come through a node group'
        else:
            return 'the image coordinates come through a "%s" node' % (n.bl_label or n.type)
    return ''


def _pow2(v):
    return 1 << max(4, int(math.ceil(math.log2(max(v, 16)))))


class Baker:
    def __init__(self, out_dir, write_png, safe, max_size=2048, log=print):
        self.write_png, self.safe = write_png, safe
        self.dir = os.path.join(out_dir, 'tex')
        os.makedirs(self.dir, exist_ok=True)
        self.max_size = int(max_size)
        self.log = log
        self.scene = None
        self.count = 0
        self.seconds = 0.0
        self.failed = []

    # ---- the flat square in its own scene
    def _setup(self):
        if self.scene is not None:
            return
        sc = bpy.data.scenes.new('bf2_bake')
        sc.render.engine = 'CYCLES'
        try:
            sc.cycles.device = 'CPU'
            sc.cycles.samples = 1
            sc.cycles.use_adaptive_sampling = False
            sc.cycles.use_denoising = False
            sc.cycles.filter_width = 0.01
        except Exception:
            pass
        me = bpy.data.meshes.new('bf2_bake_square')
        me.from_pydata([(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)], [], [(0, 1, 2, 3)])
        uv = me.uv_layers.new(name='UVMap')
        for i, c in enumerate(((0, 0), (1, 0), (1, 1), (0, 1))):
            uv.data[i].uv = c
        ob = bpy.data.objects.new('bf2_bake_square', me)
        sc.collection.objects.link(ob)
        self.scene, self.ob, self.me = sc, ob, me

    def close(self):
        if self.scene is None:
            return
        try:
            bpy.data.objects.remove(self.ob); bpy.data.meshes.remove(self.me); bpy.data.scenes.remove(self.scene)
        except Exception:
            pass
        self.scene = None

    def size_for(self, info):
        """(width, height): the shape of the material's largest image (so a baked input fits the others), times the
        tiling, powers of two, not over the limit"""
        w, h = info['image_wh']
        if not w or not h:
            w = h = 1024 if info['procedural'] else 256
        k = min(max(info['tiling'], 1.0), 4.0)
        w, h = _pow2(w * k), _pow2(h * k)
        while max(w, h) > self.max_size:
            w, h = max(w // 2, 16), max(h // 2, 16)
        return (w, h)

    # ---- one bake
    def _bake(self, mat, bake_type, size, pass_filter=None):
        """-> float32 (H, W, 4), top row first, raw (scene-linear / data) values"""
        import time
        t = time.time()
        w, h = size
        img = bpy.data.images.new('bf2_bake_target', w, h, alpha=True, float_buffer=True)
        img.colorspace_settings.name = 'Non-Color'
        nt = mat.node_tree
        node = nt.nodes.new('ShaderNodeTexImage')
        node.image = img
        for n in nt.nodes:
            n.select = False
        node.select = True
        nt.nodes.active = node
        self.me.materials.clear()
        self.me.materials.append(mat)
        names = {n.uv_map for n in _walk(nt) if n.type == 'UVMAP' and n.uv_map}
        for nm in names:
            if nm not in self.me.uv_layers:
                l = self.me.uv_layers.new(name=nm)
                for i, c in enumerate(((0, 0), (1, 0), (1, 1), (0, 1))):
                    l.data[i].uv = c
        self.me.uv_layers.active = self.me.uv_layers[0]
        vl = self.scene.view_layers[0]
        for o in self.scene.objects:
            o.select_set(False, view_layer=vl)
        self.ob.select_set(True, view_layer=vl)
        vl.objects.active = self.ob
        kw = dict(type=bake_type, margin=0, use_clear=True, target='IMAGE_TEXTURES', save_mode='INTERNAL')
        if pass_filter is not None:
            kw['pass_filter'] = pass_filter
        if bake_type == 'NORMAL':
            kw.update(normal_space='TANGENT', normal_r='POS_X', normal_g='POS_Y', normal_b='POS_Z')
        try:
            with bpy.context.temp_override(scene=self.scene, view_layer=vl, active_object=self.ob, object=self.ob, selected_objects=[self.ob]):
                bpy.ops.object.bake(**kw)
            px = np.empty(w * h * 4, np.float32)
            img.pixels.foreach_get(px)
            px = px.reshape(h, w, 4)[::-1].copy()
        finally:
            nt.nodes.remove(node)
            bpy.data.images.remove(img)
        self.count += 1
        self.seconds += time.time() - t
        return px

    # ---- a private copy of the material whose trees may be rewired
    @staticmethod
    def _private(mat, find_principled):
        m2 = mat.copy()
        made = []
        for d in range(8):
            p, stack = find_principled(m2)
            if p is None or d >= len(stack):
                break
            t = stack[d].node_tree.copy()
            stack[d].node_tree = t
            made.append(t)
        return m2, made

    @staticmethod
    def _surface(tree, top):
        """the socket of this tree that carries the shader out of it"""
        if top:
            out = next((n for n in tree.nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output), None)
            return out.inputs['Surface'] if out is not None else None
        go = next((n for n in tree.nodes if n.type == 'GROUP_OUTPUT' and getattr(n, 'is_active_output', True)), None)
        if go is None:
            return None
        return next((i for i in go.inputs if i.type == 'SHADER'), None)

    def _route(self, p, stack, src):
        """wire `src` -> Emission -> straight out of every group to the material output. -> False when it cannot be done"""
        tree = p.id_data
        em = tree.nodes.new('ShaderNodeEmission')
        em.inputs['Strength'].default_value = 1.0
        tree.links.new(src, em.inputs['Color'])
        cur_out = em.outputs[0]
        cur_tree = tree
        for level in range(len(stack), -1, -1):
            top = level == 0
            dst = self._surface(cur_tree, top)
            if dst is None:
                return False
            cur_tree.links.new(cur_out, dst)
            if top:
                return True
            g = stack[level - 1]
            nxt = next((o for o in g.outputs if o.identifier == dst.identifier), None)
            if nxt is None:
                return False
            cur_out, cur_tree = nxt, g.id_data
        return False

    def _save(self, name, a8):
        fn = self.safe(name) + '.png'
        self.write_png(os.path.join(self.dir, fn), a8)
        return fn

    @staticmethod
    def _srgb8(lin):
        c = np.clip(lin, 0.0, 1.0)
        return (np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055) * 255.0 + 0.5).astype(np.uint8)

    @staticmethod
    def _lin8(v):
        return (np.clip(v, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)

    def _record(self, mat, key, a8, colorspace, extra=None):
        h, w = a8.shape[:2]
        fn = self._save('bake_%s_%s' % (mat.name, key), a8)
        rec = {'image': {'name': 'bake:%s:%s' % (mat.name, key), 'colorspace': colorspace, 'packed': True, 'source_path': '', 'missing': False,
                         'file': 'tex/' + fn, 'size': [w, h], 'channels': 4, 'alpha_min': float(a8[..., 3].min()) / 255.0,
                         'alpha_mean': float(a8[..., 3].mean()) / 255.0},
               'channel': 'RGB', 'invert': False, 'power': 1.0, 'scale': 1.0, 'baked': True}
        rec.update(extra or {})
        return rec

    @staticmethod
    def _flat(px, tol=1.5 / 255.0):
        """a baked plane that is one value everywhere -> that value, else None"""
        lo, hi = px.reshape(-1, px.shape[-1]).min(0), px.reshape(-1, px.shape[-1]).max(0)
        return [float(x) for x in (lo + hi) / 2] if float((hi - lo).max()) <= tol else None

    # ---- a material with a Principled BSDF: the listed inputs
    def bake_inputs(self, mat, keys, sockets, find_principled, why):
        """keys: the inputs to bake; sockets: key -> socket name candidates on the Principled BSDF.
        -> {key: record}, notes. An input that cannot be baked is left out (the exporter keeps its own record)."""
        self._setup()
        info = survey(mat)
        size = self.size_for(info)
        out, notes = {}, []
        m2, made = self._private(mat, find_principled)
        try:
            p, stack = find_principled(m2)
            if p is None:
                return {}, ['the Principled BSDF was lost in the copy']
            if 'normal' in keys:
                px = self._bake(m2, 'NORMAL', size)
                flat = self._flat(px[..., :3])
                if flat is not None and abs(flat[0] - 0.5) < 0.01 and abs(flat[1] - 0.5) < 0.01:
                    out['normal'] = None                                   # the graph gives no relief at all
                else:
                    a8 = np.dstack([self._lin8(px[..., :3]), np.full(px.shape[:2], 255, np.uint8)])
                    out['normal'] = self._record(mat, 'normal', a8, 'Non-Color', {'normal_format': 'opengl'})
            if 'emission' in keys:
                px = self._bake(m2, 'EMIT', size)[..., :3]
                peak = float(px.max())
                if peak <= 1e-6:
                    out['emission'] = {'value': [0.0, 0.0, 0.0, 1.0], 'invert': False, 'power': 1.0, 'scale': 1.0, 'baked': True}
                    out['emission_strength'] = {'value': 0.0, 'invert': False, 'power': 1.0, 'scale': 1.0, 'baked': True}
                else:
                    a8 = np.dstack([self._srgb8(px / peak), np.full(px.shape[:2], 255, np.uint8)])
                    out['emission'] = self._record(mat, 'emission', a8, 'sRGB', {'peak': peak})
                    out['emission_strength'] = {'value': peak, 'invert': False, 'power': 1.0, 'scale': 1.0, 'baked': True}
            for key in keys:
                if key in ('normal', 'emission', 'emission_strength'):
                    continue
                sock = next((p.inputs[c] for c in sockets[key] if c in p.inputs), None)
                if sock is None or not sock.is_linked:
                    continue
                if not self._route(p, stack, sock.links[0].from_socket):
                    notes.append('%s: the shader could not be routed out of its node groups' % key)
                    continue
                px = self._bake(m2, 'EMIT', size)
                if key == 'base_color':
                    flat = self._flat(px[..., :3])
                    if flat is not None:
                        out[key] = {'value': flat + [1.0], 'invert': False, 'power': 1.0, 'scale': 1.0, 'baked': True}
                    else:
                        a8 = np.dstack([self._srgb8(px[..., :3]), np.full(px.shape[:2], 255, np.uint8)])
                        out[key] = self._record(mat, key, a8, 'sRGB')
                else:
                    g = px[..., 0]
                    flat = self._flat(g[..., None])
                    if flat is not None:
                        out[key] = {'value': flat[0], 'invert': False, 'power': 1.0, 'scale': 1.0, 'baked': True}
                    else:
                        g8 = self._lin8(g)
                        out[key] = self._record(mat, key, np.dstack([g8, g8, g8, np.full(g.shape, 255, np.uint8)]), 'Non-Color', {'channel': 'R'})
        except Exception as e:                                           # a bake that fails must not stop the export
            self.failed.append((mat.name, str(e)[:200]))
            notes.append('bake failed: %s' % str(e)[:200])
        finally:
            try:
                self.me.materials.clear()
                bpy.data.materials.remove(m2)
                for t in made:
                    bpy.data.node_groups.remove(t)
            except Exception:
                pass
        if info['space']:
            notes.append('uses %s: baked on a flat 0..1 square, so that part will not sit on the map as it does in Blender' % ', '.join(sorted(set(info['space']))[:4]))
        if len(info['uv_maps']) > 1:
            notes.append('uses %d UV maps: baked as if they were the same' % len(info['uv_maps']))
        if info['tiling'] > 4.0:
            notes.append('the image repeats %.0f times inside the material: baked into one %d px texture, fine detail is lost' % (info['tiling'], max(size)))
        return {'records': out, 'size': list(size), 'why': why, 'notes': notes}, notes

    # ---- a shader that is not a Principled BSDF: bake what Cycles makes of the whole surface
    def bake_surface(self, mat):
        self._setup()
        info = survey(mat)
        size = self.size_for(info)
        out, notes = {}, []
        m2 = mat.copy()
        try:
            px = self._bake(m2, 'DIFFUSE', size, {'COLOR'})
            flat = self._flat(px[..., :3])
            if flat is not None:
                out['base_color'] = {'value': flat + [1.0], 'invert': False, 'power': 1.0, 'scale': 1.0, 'baked': True}
            else:
                out['base_color'] = self._record(mat, 'base_color', np.dstack([self._srgb8(px[..., :3]), np.full(px.shape[:2], 255, np.uint8)]), 'sRGB')
            g = self._bake(m2, 'ROUGHNESS', size)[..., 0]
            flat = self._flat(g[..., None])
            if flat is not None:
                out['roughness'] = {'value': flat[0], 'invert': False, 'power': 1.0, 'scale': 1.0, 'baked': True}
            else:
                g8 = self._lin8(g)
                out['roughness'] = self._record(mat, 'roughness', np.dstack([g8, g8, g8, np.full(g.shape, 255, np.uint8)]), 'Non-Color', {'channel': 'R'})
            px = self._bake(m2, 'EMIT', size)[..., :3]
            peak = float(px.max())
            if peak > 1e-6:
                out['emission'] = self._record(mat, 'emission', np.dstack([self._srgb8(px / peak), np.full(px.shape[:2], 255, np.uint8)]), 'sRGB', {'peak': peak})
                out['emission_strength'] = {'value': peak, 'invert': False, 'power': 1.0, 'scale': 1.0, 'baked': True}
            px = self._bake(m2, 'NORMAL', size)
            flat = self._flat(px[..., :3])
            if not (flat is not None and abs(flat[0] - 0.5) < 0.01 and abs(flat[1] - 0.5) < 0.01):
                out['normal'] = self._record(mat, 'normal', np.dstack([self._lin8(px[..., :3]), np.full(px.shape[:2], 255, np.uint8)]), 'Non-Color',
                                             {'normal_format': 'opengl'})
        except Exception as e:
            self.failed.append((mat.name, str(e)[:200]))
            notes.append('bake failed: %s' % str(e)[:200])
        finally:
            try:
                self.me.materials.clear()
                bpy.data.materials.remove(m2)
            except Exception:
                pass
        if info['space']:
            notes.append('uses %s: baked on a flat 0..1 square, so that part will not sit on the map as it does in Blender' % ', '.join(sorted(set(info['space']))[:4]))
        return {'records': out, 'size': list(size), 'why': 'the surface is not a Principled BSDF', 'notes': notes}, notes
