"""Piece FBX writer / reader (binary FBX 7400, the exact layout FrostyCLI's `port import` has been fed by every port).

One mesh node ``mesh:lod0`` without a transform; geometry already in the game's axes (x right, y up, z = -Blender y),
metres, relative to the piece origin; normals per corner; two UV sets (the importer fills TexCoord1 only from a second
set); tangent and binormal per corner for both sets; optional vertex colours. No texture or file path is written.

The layout was measured on the pieces Blender's exporter wrote for the Mustafar port (``global_scale=0.01,
apply_unit_scale, use_tspace``): UpAxis Y, FrontAxis Z, UnitScaleFactor 1, model rotation 0.
"""
import os

import numpy as np

from . import fbxbin

SEP = b'\x00\x01'


def _p(name, t1, t2, flag, *vals):
    props = [('S', name), ('S', t1), ('S', t2), ('S', flag)]
    for v in vals:
        if isinstance(v, bool):
            props.append(('I', int(v)))
        elif isinstance(v, int):
            props.append(('I', v))
        elif isinstance(v, float):
            props.append(('D', v))
        else:
            props.append(('S', v))
    return ['P', props, []]


def _n(name, *props, kids=None):
    return [name, list(props), kids or []]


def tangents(pos, nrm, uv):
    """Per-corner tangent and binormal (T, 3, 3 each) of triangles pos / nrm (T, 3, 3) with uv (T, 3, 2).

    Triangle tangents (dP/du, dP/dv, unnormalised = area weighted) are summed over the corners that share position,
    normal and uv, then made orthogonal to the corner normal; binormal = sign x cross(normal, tangent) with the sign of
    the uv mapping's handedness. Triangles without a uv area get any perpendicular of the normal.
    """
    e1 = pos[:, 1] - pos[:, 0]
    e2 = pos[:, 2] - pos[:, 0]
    d1 = uv[:, 1] - uv[:, 0]
    d2 = uv[:, 2] - uv[:, 0]
    det = d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]
    ok = np.abs(det) > 1e-12
    inv = np.where(ok, 1.0 / np.where(ok, det, 1.0), 0.0)
    t = (e1 * d2[:, 1:2] - e2 * d1[:, 1:2]) * inv[:, None]
    b = (e2 * d1[:, 0:1] - e1 * d2[:, 0:1]) * inv[:, None]
    # normalise then weight by the triangle's area so a sliver does not steer its neighbours
    area = np.linalg.norm(np.cross(e1, e2), axis=1) / 2.0
    tl = np.linalg.norm(t, axis=1); bl = np.linalg.norm(b, axis=1)
    t = np.where((tl > 1e-20)[:, None], t / np.maximum(tl, 1e-20)[:, None], 0.0) * area[:, None]
    b = np.where((bl > 1e-20)[:, None], b / np.maximum(bl, 1e-20)[:, None], 0.0) * area[:, None]
    # corners are grouped only with triangles of the same uv handedness (mirrored halves that meet on a seam with equal
    # uvs keep their own tangents, as mikktspace does)
    hand = np.repeat(np.sign(det), 3)[:, None]
    key = np.concatenate([np.round(pos.reshape(-1, 3), 5), np.round(nrm.reshape(-1, 3), 4), np.round(uv.reshape(-1, 2), 5), hand], 1)
    _, inv_idx = np.unique(key, axis=0, return_inverse=True)
    inv_idx = inv_idx.reshape(-1)
    nk = int(inv_idx.max()) + 1 if len(inv_idx) else 0
    ts = np.zeros((nk, 3)); bs = np.zeros((nk, 3))
    np.add.at(ts, inv_idx, np.repeat(t, 3, axis=0))
    np.add.at(bs, inv_idx, np.repeat(b, 3, axis=0))
    tc = ts[inv_idx]; bc = bs[inv_idx]
    n = nrm.reshape(-1, 3)
    tc = tc - n * (tc * n).sum(1, keepdims=True)
    l = np.linalg.norm(tc, axis=1)
    bad = l < 1e-9
    if bad.any():
        # any perpendicular: cross with the axis the normal is least aligned with
        nb = n[bad]
        ax = np.eye(3)[np.argmin(np.abs(nb), axis=1)]
        tc[bad] = np.cross(nb, ax)
        l = np.linalg.norm(tc, axis=1)
    tc = tc / np.maximum(l, 1e-20)[:, None]
    cr = np.cross(n, tc)
    sign = np.where((cr * bc).sum(1) < 0.0, -1.0, 1.0)
    bn = cr * sign[:, None]
    return tc.reshape(-1, 3, 3), bn.reshape(-1, 3, 3)


def _indexed(a, decimals):
    """unique rows + index per row (IndexToDirect layer data)"""
    r = np.round(a, decimals) + 0.0            # + 0.0 turns -0.0 into 0.0 so equal rows hash alike
    u, first, inv = np.unique(r, axis=0, return_index=True, return_inverse=True)
    return a[first], inv.reshape(-1).astype(np.int32)


def _layer(kind, index, name, mapping, ref, data_name, data, idx_name=None, idx=None):
    kids = [_n('Version', ('I', 101)), _n('Name', ('S', name)), _n('MappingInformationType', ('S', mapping)),
            _n('ReferenceInformationType', ('S', ref)), _n(data_name, ('d', np.asarray(data, np.float64).reshape(-1)))]
    if idx is not None:
        kids.append(_n(idx_name, ('i', idx)))
    return _n(kind, ('I', index), kids=kids)


# Encodings of one and the same mesh. The game's importer refuses some piece files depending on their exact bytes
# ("mismatch in the amount of LODs", "at least one UV channel"): Mustafar 2 of 514 with zlib level 1 and none
# uncompressed; Rhen Var 5 of 724 uncompressed (all 270 - 293 kB) and none of those with zlib; one piece only with
# shared uvs. The cause is not known, so a refused piece is written again in the next encoding (import stage).
VARIANTS = [{'level': None, 'share_uv': False}, {'level': 1, 'share_uv': False}, {'level': 6, 'share_uv': False},
            {'level': 1, 'share_uv': True}, {'level': None, 'share_uv': True}]


def build(pos, nrm, uv0, uv1=None, colour=None, material='material', node='mesh:lod0', share_uv=False):
    """FBX tree of one piece. pos / nrm (T, 3, 3), uv0 / uv1 (T, 3, 2), colour (T, 3, 4) in 0..1 or None."""
    pos = np.asarray(pos, np.float64); nrm = np.asarray(nrm, np.float64); uv0 = np.asarray(uv0, np.float64)
    T = len(pos)
    if uv1 is None:
        uv1 = uv0
    uv1 = np.asarray(uv1, np.float64)
    verts, vidx = _indexed(pos.reshape(-1, 3), 5)
    pvi = vidx.reshape(T, 3).astype(np.int32).copy()
    pvi[:, 2] = -pvi[:, 2] - 1                     # the last corner of a polygon is stored as ~index
    nd, ni = _indexed(nrm.reshape(-1, 3), 6)
    kids = [_n('Properties70'), _n('GeometryVersion', ('I', 124)), _n('Vertices', ('d', verts.reshape(-1))),
            _n('PolygonVertexIndex', ('i', pvi.reshape(-1))),
            _layer('LayerElementNormal', 0, b'', b'ByPolygonVertex', b'IndexToDirect', 'Normals', nd, 'NormalsIndex', ni)]
    sets = [(b'UVSet0', uv0), (b'UVSet1', uv1)]
    for i, (nm, uv) in enumerate(sets):
        t, b = tangents(pos, nrm, uv)
        kids.append(_layer('LayerElementBinormal', i, nm, b'ByPolygonVertex', b'Direct', 'Binormals', b))
        kids.append(_layer('LayerElementTangent', i, nm, b'ByPolygonVertex', b'Direct', 'Tangents', t))
    if colour is not None:
        cd, ci = _indexed(np.asarray(colour, np.float64).reshape(-1, 4), 6)
        kids.append(_layer('LayerElementColor', 0, b'Col', b'ByPolygonVertex', b'IndexToDirect', 'Colors', cd, 'ColorIndex', ci))
    for i, (nm, uv) in enumerate(sets):
        # one UV value per corner, NOT shared between corners: the game's importer rejected a piece (Rhen Var,
        # 226 triangles, 40 distinct uvs on 678 corners) with "mismatch in the amount of LODs" when its uvs were
        # written shared, and takes the identical values unshared (tmp/probe/lod_bisect3.py). Why: not known.
        ud, ui = _indexed(uv.reshape(-1, 2), 7) if share_uv else (uv.reshape(-1, 2), np.arange(T * 3, dtype=np.int32))
        kids.append(_layer('LayerElementUV', i, nm, b'ByPolygonVertex', b'IndexToDirect', 'UV', ud, 'UVIndex', ui))
    kids.append(_n('LayerElementMaterial', ('I', 0), kids=[
        _n('Version', ('I', 101)), _n('Name', ('S', b'')), _n('MappingInformationType', ('S', b'AllSame')),
        _n('ReferenceInformationType', ('S', b'IndexToDirect')), _n('Materials', ('i', np.zeros(1, np.int32)))]))

    def le(kind, i):
        return _n('LayerElement', kids=[_n('Type', ('S', kind)), _n('TypedIndex', ('I', i))])
    l0 = [_n('Version', ('I', 100)), le(b'LayerElementNormal', 0), le(b'LayerElementBinormal', 0), le(b'LayerElementTangent', 0)]
    if colour is not None:
        l0.append(le(b'LayerElementColor', 0))
    l0 += [le(b'LayerElementUV', 0), le(b'LayerElementMaterial', 0)]
    kids.append(_n('Layer', ('I', 0), kids=l0))
    kids.append(_n('Layer', ('I', 1), kids=[_n('Version', ('I', 100)), le(b'LayerElementUV', 1), le(b'LayerElementBinormal', 1),
                                           le(b'LayerElementTangent', 1)]))
    GEO, MODEL, MAT, DOC = 1000001, 1000002, 1000003, 1000004
    nb = node.encode('ascii'); mb = material.encode('ascii', 'replace')
    geometry = _n('Geometry', ('L', GEO), ('S', nb + SEP + b'Geometry'), ('S', b'Mesh'), kids=kids)
    model = _n('Model', ('L', MODEL), ('S', nb + SEP + b'Model'), ('S', b'Mesh'), kids=[
        _n('Version', ('I', 232)),
        _n('Properties70', kids=[_p(b'DefaultAttributeIndex', b'int', b'Integer', b'', 0), _p(b'InheritType', b'enum', b'', b'', 1)]),
        _n('MultiLayer', ('I', 0)), _n('MultiTake', ('I', 0)), _n('Shading', ('C', True)), _n('Culling', ('S', b'CullingOff'))])
    mat = _n('Material', ('L', MAT), ('S', mb + SEP + b'Material'), ('S', b''), kids=[
        _n('Version', ('I', 102)), _n('ShadingModel', ('S', b'Phong')), _n('MultiLayer', ('I', 0)),
        _n('Properties70', kids=[_p(b'DiffuseColor', b'Color', b'', b'A', 1.0, 1.0, 1.0)])])
    gs = _n('GlobalSettings', kids=[_n('Version', ('I', 1000)), _n('Properties70', kids=[
        _p(b'UpAxis', b'int', b'Integer', b'', 1), _p(b'UpAxisSign', b'int', b'Integer', b'', 1),
        _p(b'FrontAxis', b'int', b'Integer', b'', 2), _p(b'FrontAxisSign', b'int', b'Integer', b'', 1),
        _p(b'CoordAxis', b'int', b'Integer', b'', 0), _p(b'CoordAxisSign', b'int', b'Integer', b'', 1),
        _p(b'OriginalUpAxis', b'int', b'Integer', b'', -1), _p(b'OriginalUpAxisSign', b'int', b'Integer', b'', 1),
        _p(b'UnitScaleFactor', b'double', b'Number', b'', 1.0), _p(b'OriginalUnitScaleFactor', b'double', b'Number', b'', 1.0)])])
    hdr = _n('FBXHeaderExtension', kids=[_n('FBXHeaderVersion', ('I', 1003)), _n('FBXVersion', ('I', 7400)),
                                        _n('EncryptionType', ('I', 0)), _n('Creator', ('S', b'bf2maptool'))])
    docs = _n('Documents', kids=[_n('Count', ('I', 1)), _n('Document', ('L', DOC), ('S', b'Scene'), ('S', b'Scene'), kids=[
        _n('Properties70', kids=[['P', [('S', b'SourceObject'), ('S', b'object'), ('S', b''), ('S', b'')], []],
                                 ['P', [('S', b'ActiveAnimStackName'), ('S', b'KString'), ('S', b''), ('S', b''), ('S', b'')], []]]),
        _n('RootNode', ('L', 0))])])

    def ot(name, tpl=None):
        k = [_n('Count', ('I', 1))]
        if tpl:
            k.append(_n('PropertyTemplate', ('S', tpl), kids=[_n('Properties70')]))
        return _n('ObjectType', ('S', name), kids=k)
    defs = _n('Definitions', kids=[_n('Version', ('I', 100)), _n('Count', ('I', 4)), ot(b'GlobalSettings'), ot(b'Geometry', b'FbxMesh'),
                                  ot(b'Model', b'FbxNode'), ot(b'Material', b'FbxSurfacePhong')])
    objs = _n('Objects', kids=[geometry, model, mat])
    conns = _n('Connections', kids=[_n('C', ('S', b'OO'), ('L', MODEL), ('L', 0)), _n('C', ('S', b'OO'), ('L', GEO), ('L', MODEL)),
                                   _n('C', ('S', b'OO'), ('L', MAT), ('L', MODEL))])
    takes = _n('Takes', kids=[_n('Current', ('S', b''))])
    return [hdr, _n('FileId', ('R', b'\x28\xb3\x2a\xeb\xb6\x24\xcc\xc2\xbf\xc8\xb0\x2a\xa9\x2b\xfc\xf1')),
            _n('CreationTime', ('S', b'1970-01-01 10:00:00:000')), _n('Creator', ('S', b'bf2maptool')), gs, docs,
            _n('References'), defs, objs, conns, takes]


def write(path, pos, nrm, uv0, uv1=None, colour=None, material='material', variant=0):
    v = VARIANTS[variant % len(VARIANTS)]
    fbxbin.save(path, 7400, build(pos, nrm, uv0, uv1, colour, material, share_uv=v['share_uv']), level=v['level'])


def rewrite(src, dst, variant):
    """the mesh of piece file `src` written to `dst` in another encoding (same positions, normals, uvs)"""
    g = read(src)
    _, nodes = fbxbin.load(src)
    mat = fbxbin.find(nodes, 'Objects', 'Material')
    name = bytes(mat[1][1][1]).split(b'\x00')[0].decode('ascii', 'replace') if mat else 'material'
    if os.path.exists(dst):
        os.remove(dst)                       # never write through a hard link to the piece file
    write(dst, g['pos'], g['nrm'], g['uv0'], g.get('uv1'), None, name, variant)


def read(path):
    """corner arrays of a piece FBX written by this module or by Blender's exporter:
    dict(pos, nrm, uv0, uv1 (T, 3, n), tan0, bin0 when present). Triangles only."""
    _, nodes = fbxbin.load(path)
    g = fbxbin.find(nodes, 'Objects', 'Geometry')
    get = lambda nm: next((k for k in g[2] if k[0] == nm), None)
    verts = get('Vertices')[1][0][1].reshape(-1, 3)
    pvi = get('PolygonVertexIndex')[1][0][1].copy()
    if not (pvi.reshape(-1, 3)[:, 2] < 0).all() or (pvi.reshape(-1, 3)[:, :2] < 0).any():
        raise ValueError('%s is not triangulated' % path)
    pvi[pvi < 0] = -pvi[pvi < 0] - 1
    T = len(pvi) // 3
    out = {'pos': verts[pvi].reshape(T, 3, 3)}

    def layer(kind, typed, data_name, idx_name, width):
        for k in g[2]:
            if k[0] == kind and k[1][0][1] == typed:
                data = next(c for c in k[2] if c[0] == data_name)[1][0][1].reshape(-1, width)
                ref = next(c for c in k[2] if c[0] == 'ReferenceInformationType')[1][0][1]
                if ref == b'IndexToDirect':
                    data = data[next(c for c in k[2] if c[0] == idx_name)[1][0][1]]
                return data.reshape(T, 3, width)
        return None
    out['nrm'] = layer('LayerElementNormal', 0, 'Normals', 'NormalsIndex', 3)
    out['uv0'] = layer('LayerElementUV', 0, 'UV', 'UVIndex', 2)
    out['uv1'] = layer('LayerElementUV', 1, 'UV', 'UVIndex', 2)
    out['tan0'] = layer('LayerElementTangent', 0, 'Tangents', None, 3)
    out['bin0'] = layer('LayerElementBinormal', 0, 'Binormals', None, 3)
    out['colour'] = layer('LayerElementColor', 0, 'Colors', 'ColorIndex', 4)
    return out
