"""Dump imported game meshes of a build back to files and print their vertex data per triangle.
python geo_dump.py <map.json> <settings.json> <piece name> [more pieces]   -> <work>/dumps/geo/<piece>_Mesh.meshgeo + a report"""
import glob
import os
import sys

import numpy as np

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'runtime')))
from bf2maptool import chain, cli, config, meshgeo as mg, runner

job = config.Job(sys.argv[1], config.load_settings(sys.argv[2]))
names = sys.argv[3:]
out = job.path('dumps', 'geo')
os.makedirs(out, exist_ok=True)
lst = job.path('dumps', 'geo_list.txt')
open(lst, 'w').write('\n'.join('%s/%s_Mesh' % (job.prefix, n) for n in names) + '\n')
proj = job.project('import')
c = cli.Cli(job, print)
r = c.direct('geo_dump', ['mesh', 'geo-dump', '--list', lst, '--outdir', out], project=proj, check=False)
np.set_printoptions(precision=3, suppress=True, linewidth=220)
for f in sorted(glob.glob(out + '/*.meshgeo')):
    if not any(n.lower() in os.path.basename(f).lower() for n in names):
        continue
    s = mg.load(f)['lods'][0]['sections'][0]
    idx = s['indices'].reshape(-1, 3).astype(np.int64)
    d = {k: mg.decode(s, k) for k in ('Pos', 'Normal', 'Tangent', 'TexCoord0', 'BinormalSign')}
    print(os.path.basename(f), 'verts', s['vcount'], 'tris', len(idx), 'decl', mg.decl(s))
    bad = 0
    for t in range(len(idx)):
        p = d['Pos'][idx[t]][:, :3]; n = d['Normal'][idx[t]][:, :3]; tg = d['Tangent'][idx[t]][:, :3]
        uv = d['TexCoord0'][idx[t]][:, :2]; bs = d['BinormalSign'][idx[t]]
        e1, e2 = p[1] - p[0], p[2] - p[0]
        d1, d2 = uv[1] - uv[0], uv[2] - uv[0]
        det = d1[0] * d2[1] - d2[0] * d1[1]
        if abs(det) < 1e-9:
            continue
        gt = (e1 * d2[1] - e2 * d1[1]) / det
        gb = (e2 * d1[0] - e1 * d2[0]) / det
        gt /= max(np.linalg.norm(gt), 1e-12); gb /= max(np.linalg.norm(gb), 1e-12)
        tdot = (tg @ gt)
        # the binormal the game rebuilds: sign x cross(normal, tangent)
        bsv = np.asarray(bs, np.float64).reshape(3, -1)
        sgn = bsv[:, -1] if bsv.shape[1] > 1 else bsv[:, 0]
        rb = np.cross(n, tg) * sgn[:, None]
        bdot = rb @ gb
        odd = (tdot.min() < 0.9) or (np.abs(bdot).min() < 0.9) or (np.sign(bdot).min() != np.sign(bdot).max())
        bad += bool(odd)
        if t < 6 or odd and bad <= 6:
            print(' tri', t, 'idx', idx[t], 'T.dPdu', tdot.round(2), 'rebuiltB.dPdv', bdot.round(2), 'binsign raw', bsv.round(2).tolist())
            print('    uv', uv.round(3).tolist(), 'tangent', tg.round(2).tolist())
    print('  triangles whose stored frame disagrees with their own uv mapping:', bad, 'of', len(idx))
