"""Guess what the objects of an imported / ripped scene are, so a scene that was never prepared for the tool can go
through it: sky domes and backdrops, exact duplicates, invisible helper geometry. Nothing is deleted - objects only
get the role "Leave out", and every guess comes back as (object, role, reason) so it can be listed and undone.
"""
import hashlib
import struct

import bpy

SKY_WORDS = ('sky', 'dome', 'cloud', 'stars', 'backdrop', 'horizon', 'skybox')
HELPER_WORDS = ('collision', 'inv_col', '_col_', 'shadowvolume', 'shadow_volume', 'ucx_', 'ubx_', 'occluder', 'portal', 'blocker', 'navmesh')
SKY_SIZE = 1500.0            # metres: nothing a soldier walks on is one object this large and this light
SKY_MAX_TRIS = 5000


def _tris(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


def _words(text, words):
    t = text.lower()
    return next((w for w in words if w in t), None)


def _mesh_signature(o):
    """Exact mesh content and material signature; equal size and origin are not enough."""
    h = hashlib.sha256()
    mesh = o.data
    h.update(struct.pack('<II', len(mesh.vertices), len(mesh.polygons)))
    for v in mesh.vertices:
        h.update(struct.pack('<3f', *v.co))
    for p in mesh.polygons:
        h.update(struct.pack('<II', len(p.vertices), p.material_index))
        h.update(struct.pack('<%dI' % len(p.vertices), *p.vertices))
    for slot in o.material_slots:
        name = slot.material.name if slot.material else ''
        h.update(name.encode('utf-8') + b'\0')
    for layer in mesh.uv_layers:
        h.update(layer.name.encode('utf-8') + b'\0')
        for uv in layer.data:
            h.update(struct.pack('<2f', *uv.uv))
    return h.digest()


def guess_roles(objects):
    """-> list of (object, role, reason) for the objects whose role should change. Only meshes still on the default
    role ("Map geometry") are looked at; anything the user already tagged stays."""
    out = []
    meshes = [o for o in objects if o.type == 'MESH' and o.bf2.role == 'static']
    seen = {}
    for o in sorted(meshes, key=lambda x: x.name):
        size = max(o.dimensions) if len(o.data.polygons) else 0.0
        mats = [s.material.name for s in o.material_slots if s.material]
        if not len(o.data.polygons):
            out.append((o, 'skip', 'it has no faces')); continue
        w = _words(o.name, SKY_WORDS) or next((x for x in (_words(m, SKY_WORDS) for m in mats) if x), None)
        if size >= SKY_SIZE and _tris(o) <= SKY_MAX_TRIS:
            out.append((o, 'skip', 'a %.0f m shell of %d triangles: a sky dome or backdrop (the level gets its own sky)' % (size, _tris(o)))); continue
        if w and size >= 400.0:
            out.append((o, 'skip', 'named "%s" and %.0f m across: sky / backdrop' % (w, size))); continue
        h = _words(o.name, HELPER_WORDS)
        if h is None and mats and all(_words(m, HELPER_WORDS) for m in mats):
            h = _words(mats[0], HELPER_WORDS)
        if h:
            out.append((o, 'skip', 'named "%s": an invisible helper (collision / shadow / portal geometry of the source game)' % h)); continue
        # Exact duplicates must have matching geometry and materials. Imported
        # objects commonly share an origin, size and vertex count while their
        # vertices occupy different parts of the map.
        mw = o.matrix_world
        key = (tuple(float(v) for row in mw for v in row), _mesh_signature(o))
        if key in seen:
            out.append((o, 'skip', 'an exact duplicate of "%s" (same mesh size, same place)' % seen[key]))
        else:
            seen[key] = o.name
    return out


def apply(guesses):
    for o, role, _ in guesses:
        o.bf2.role = role
    return len(guesses)
