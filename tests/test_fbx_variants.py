"""Every encoding variant of a piece file must hold the same mesh: python test_fbx_variants.py <piece.fbx> [...]"""
import os
import sys
import tempfile

import numpy as np

sys.path.insert(0, os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'runtime')))
from bf2maptool import fbx

ok = True
for src in sys.argv[1:]:
    a = fbx.read(src)
    d = tempfile.mkdtemp(prefix='fbxvar_')
    for k in range(len(fbx.VARIANTS)):
        dst = os.path.join(d, 'v%d.fbx' % k)
        fbx.rewrite(src, dst, k)
        b = fbx.read(dst)
        same = all(np.array_equal(a[x], b[x]) for x in ('pos', 'uv0', 'uv1')) and np.allclose(a['nrm'], b['nrm'], atol=1e-12) and \
            np.allclose(a['tan0'], b['tan0'], atol=1e-4)        # tangents are recomputed from the same data (seen: 5e-6 apart; the game stores halves)
        ok = ok and same
        print('%s variant %d %s: %7d bytes, mesh %s' % (os.path.basename(src), k, fbx.VARIANTS[k], os.path.getsize(dst), 'identical' if same else 'DIFFERENT'))
sys.exit(0 if ok else 1)
