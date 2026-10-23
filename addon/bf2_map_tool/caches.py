"""Clean build caches: free the disk space one map's build keeps between builds, without touching what the user made
(mods/ = the built .fbmod files, export/ = the exported scene). The next build redoes the stages whose files are gone
(the runner re-runs a stage when one of its outputs is missing), so it takes about as long as a first build.

Pure Python (no Blender) so the tests run it on a copy of a work folder: python caches.py <work>/<Map> [--dry]
"""
import ctypes
import os
import shutil
import sys

# the folders a build writes and reads again only to skip work (biggest first, as measured on LEGO Utapau:
# projects 7 GB, pieces 2.6 GB, bake 2.3 GB, pkg 0.6 GB, nav 0.6 GB, col 0.55 GB)
CACHE_DIRS = ('projects', 'pieces', 'bake', 'pkg', 'nav', 'col', 'tmp')
KEEP = ('mods', 'export')


class Busy(Exception):
    pass


def _pid_alive(pid):
    if pid <= 0:
        return False
    if os.name == 'nt':
        h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)        # PROCESS_QUERY_LIMITED_INFORMATION
        if not h:
            return False
        code = ctypes.c_ulong()
        ok = ctypes.windll.kernel32.GetExitCodeProcess(h, ctypes.byref(code))
        ctypes.windll.kernel32.CloseHandle(h)
        return bool(ok) and code.value == 259                              # STILL_ACTIVE
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def building(work):
    """is a build of this map running now (its lock file names a live process)?"""
    p = os.path.join(work, 'build.lock')
    try:
        return _pid_alive(int(open(p).read().strip() or 0))
    except (OSError, ValueError):
        return False


def folder_bytes(path):
    n = 0
    for root, _, files in os.walk(path):
        for f in files:
            try:
                n += os.lstat(os.path.join(root, f)).st_size
            except OSError:
                pass
    return n


def measure(work):
    """{cache folder: bytes} of the folders that exist"""
    return {d: folder_bytes(os.path.join(work, d)) for d in CACHE_DIRS if os.path.isdir(os.path.join(work, d))}


def clean(work, dry=False):
    """delete the cache folders of one map's work folder -> {'freed': bytes, 'folders': {name: bytes}, 'failed': [...]}"""
    work = os.path.abspath(work)
    if not os.path.isfile(os.path.join(work, 'build.json')) and not os.path.isdir(os.path.join(work, 'mods')):
        raise ValueError('%s is not the work folder of a map (no build.json)' % work)
    if building(work):
        raise Busy('A build of this map is running. Cancel it or wait for it to finish.')
    sizes = measure(work)
    out = {'freed': 0, 'folders': sizes, 'failed': []}
    for d, n in sizes.items():
        p = os.path.join(work, d)
        if dry:
            out['freed'] += n
            continue
        shutil.rmtree(p, ignore_errors=True)
        left = folder_bytes(p) if os.path.exists(p) else 0
        out['freed'] += n - left
        if left:
            out['failed'].append('%s (%.0f MB still there - a file in use?)' % (d, left / 1e6))
    return out


def human(n):
    return '%.1f GB' % (n / 1e9) if n >= 1e9 else '%.0f MB' % (n / 1e6)


if __name__ == '__main__':
    r = clean(sys.argv[1], dry='--dry' in sys.argv)
    print('freed %s: %s%s' % (human(r['freed']), ', '.join('%s %s' % (k, human(v)) for k, v in r['folders'].items()),
                              ('; FAILED: ' + '; '.join(r['failed'])) if r['failed'] else ''))
