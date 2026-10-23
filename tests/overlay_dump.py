"""Blender side of tests/test_overlay.py: write what the collision overlay would draw for the open scene.
    blender -b <scene.blend> --factory-startup --python overlay_dump.py -- <addon dir> <out.npz>
"""
import sys

import numpy as np

argv = sys.argv[sys.argv.index('--') + 1:]
sys.path.insert(0, argv[0])
import props
props.register()
import overlay

parts = overlay.solid_triangles()
np.savez(argv[1], kinds=np.array([k for k, _, _ in parts]), names=np.array([n for _, n, _ in parts]),
         counts=np.array([len(t) for _, _, t in parts]), tris=np.concatenate([t for _, _, t in parts]) if parts else np.zeros((0, 3, 3)),
         kill=overlay.kill_boxes())
print('OVERLAY DUMP', len(parts), 'objects', sum(len(t) for _, _, t in parts), 'triangles')
