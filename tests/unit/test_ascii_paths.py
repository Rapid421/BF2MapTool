"""config.safe gives ASCII names ('Béton' -> 'Beton'); config.ascii_alias reaches a folder with accents in its path by
an ASCII-only path (a directory junction) whose files are the same files.   python test_ascii_paths.py [scratch dir]"""
import os
import shutil
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'runtime'))
from bf2maptool import config  # noqa: E402

ok = True


def expect(what, got, want):
    global ok
    good = got == want
    ok &= good
    print('%-4s %-48s got %-40r want %r' % ('ok' if good else 'FAIL', what, got, want))


expect('safe: accents dropped', config.safe('Béton'), 'Beton')
expect('safe: symbols -> _', config.safe('Métal (#2)'), 'Metal___2_')
cjk_a, cjk_b = config.safe('金属'), config.safe('木材')
expect('safe: no-ASCII names stay apart', cjk_a != cjk_b and cjk_a.isascii() and cjk_a.startswith('___'), True)
expect('safe: ASCII kept', config.safe('Packed_mat-1'), 'Packed_mat-1')

root = sys.argv[1] if len(sys.argv) > 1 else tempfile.gettempdir()
d = os.path.join(root, 'aliasé wörk dir')
os.makedirs(os.path.join(d, 'pkg'), exist_ok=True)
with open(os.path.join(d, 'pkg', 'mesh.fbx'), 'w') as f:
    f.write('x')
a = config.ascii_alias(d)
expect('alias is ASCII', a.isascii(), True)
expect('alias reaches the same file', os.path.isfile(os.path.join(a, 'pkg', 'mesh.fbx')) and
       os.path.samefile(os.path.join(a, 'pkg', 'mesh.fbx'), os.path.join(d, 'pkg', 'mesh.fbx')), True)
expect('alias made once (same again)', config.ascii_alias(d), a)
os.rmdir(a)                                               # the junction only
expect('target kept when the link goes', os.path.isfile(os.path.join(d, 'pkg', 'mesh.fbx')), True)
shutil.rmtree(d)
print('PASS' if ok else 'FAIL')
sys.exit(0 if ok else 1)
