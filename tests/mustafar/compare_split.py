"""THROW-AWAY regression check: the runtime's split of the Mustafar export against the port's own split
(pieces/pieces.json written by Blender) and one collision OBJ of the port.
  python compare_split.py <tool pieces dir> <port dir>"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime')))
from bf2maptool import fbx

A = json.load(open(sys.argv[1] + '/pieces.json'))['pieces']
PORT = sys.argv[2]
B = json.load(open(PORT + '/pieces/pieces.json'))['pieces']
print('pieces %d / %d, triangles %d / %d' % (len(A), len(B), sum(p['tris'] for p in A), sum(p['tris'] for p in B)))
same_order = [a['material'] == b['material'] and a['tris'] == b['tris'] for a, b in zip(A, B)]
print('same material + triangle count at the same index: %d of %d' % (sum(same_order), len(B)))
dc = [float(np.abs(np.array(a['centre']) - np.array(b['centre'])).max()) for a, b in zip(A, B)]
ds = [float(np.abs(np.array(a['size']) - np.array(b['size'])).max()) for a, b in zip(A, B)]
da = [abs(a['area_m2'] - b['area_m2']) for a, b in zip(A, B)]
print('centre max diff %.4f m, size max diff %.4f m, area max diff %.3f m2 (total %.1f / %.1f)' % (
    max(dc), max(ds), max(da), sum(a['area_m2'] for a in A), sum(b['area_m2'] for b in B)))
print('objects per piece equal: %d of %d' % (sum(a['objects'] == b['objects'] for a, b in zip(A, B)), len(B)))
print('uv range max diff: %.3f; pieces with |uv| > 4: %d / %d' % (
    max(float(np.abs(np.array(a['uv_range']) - np.array(b['uv_range'])).max()) for a, b in zip(A, B)),
    sum(max(abs(x) for x in a['uv_range']) > 4 for a in A), sum(max(abs(x) for x in b['uv_range']) > 4 for b in B)))
# geometry of every piece: the same triangle set (positions) as Blender's FBX?
worst = 0.0; nbad = 0; nrm_worst = 0.0; uv_frac_worst = 0.0
for a, b in zip(A, B):
    fa = fbx.read(sys.argv[1] + '/' + a['file']); fb = fbx.read(PORT + '/pieces/' + b['file'])
    if len(fa['pos']) != len(fb['pos']):
        nbad += 1; continue
    ka = np.sort(np.round(np.sort(fa['pos'].reshape(-1, 9), axis=1), 3), axis=0)
    kb = np.sort(np.round(np.sort(fb['pos'].reshape(-1, 9), axis=1), 3), axis=0)
    d = float(np.abs(ka - kb).max())
    worst = max(worst, d)
    if d > 2e-3:
        nbad += 1
print('triangle sets (sorted coordinates) differ by more than 2 mm in %d pieces; worst coordinate diff %.5f m' % (nbad, worst))
# normals / uvs corner by corner on pieces whose triangle order matches
n = 0; dn = []; du = []
for a, b in zip(A, B):
    fa = fbx.read(sys.argv[1] + '/' + a['file']); fb = fbx.read(PORT + '/pieces/' + b['file'])
    if fa['pos'].shape == fb['pos'].shape and np.abs(fa['pos'] - fb['pos']).max() < 1e-3:
        n += 1
        dn.append(float(np.abs(fa['nrm'] - fb['nrm']).max()))
        f = fa['uv0'] - fb['uv0']
        du.append(float(np.abs(f - np.round(f)).max()))      # whole-number shifts may differ
print('pieces with identical triangle AND corner order: %d; there: normals max diff %.5f, uv (modulo whole numbers) max diff %.6f' % (
    n, max(dn) if dn else -1, max(du) if du else -1))
# collision frame: the port's raw OBJ of one piece vs the tool's piece npz
name_b = B[204]['name']; name_a = A[204]['name']
V = np.array([[float(x) for x in l.split()[1:4]] for l in open('%s/col/raw/%s.obj' % (PORT, name_b)) if l.startswith('v ')])
z = np.load('%s/%s.npz' % (sys.argv[1], name_a))
pa = np.unique(np.round(z['pos'].reshape(-1, 3), 3), axis=0); pb = np.unique(np.round(V, 3), axis=0)
print('collision frame, piece %s: port OBJ %d unique vertices, tool %d; bounds port %s..%s tool %s..%s; same set: %s' % (
    name_b, len(pb), len(pa), pb.min(0).tolist(), pb.max(0).tolist(), pa.min(0).tolist(), pa.max(0).tolist(),
    pa.shape == pb.shape and bool(np.abs(pa - pb).max() < 2e-3)))
