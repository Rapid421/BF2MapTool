"""Look at the desktop: list the visible top-level windows and save a screenshot.
python screen.py list                     -> one line per visible window (hwnd, pid, process, class, rect, title)
python screen.py shot <out.png> [hwnd]    -> the whole virtual screen, or one window's rectangle
python screen.py click <x> <y>            -> left click at screen pixel (x, y)
python screen.py key <name>               -> press a key: enter, esc, tab, space, left, right
Used by the game test to see launcher dialogs (for example the EA app's "sync was interrupted") that stop a launch."""
import ctypes
import ctypes.wintypes as wt
import sys
import time

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass


def proc_name(pid):
    h = k32.OpenProcess(0x1000, False, pid)
    if not h:
        return '?'
    buf = ctypes.create_unicode_buffer(520)
    n = wt.DWORD(520)
    ok = k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n))
    k32.CloseHandle(h)
    return buf.value.replace('\\', '/').split('/')[-1] if ok else '?'


def windows():
    out = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, _):
        if not u32.IsWindowVisible(h):
            return True
        r = wt.RECT()
        u32.GetWindowRect(h, ctypes.byref(r))
        if r.right - r.left < 40 or r.bottom - r.top < 40:
            return True
        t = ctypes.create_unicode_buffer(300)
        u32.GetWindowTextW(h, t, 300)
        c = ctypes.create_unicode_buffer(200)
        u32.GetClassNameW(h, c, 200)
        pid = wt.DWORD()
        u32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        out.append({'hwnd': int(h), 'pid': pid.value, 'process': proc_name(pid.value), 'class': c.value,
                    'rect': (r.left, r.top, r.right, r.bottom), 'title': t.value, 'iconic': bool(u32.IsIconic(h))})
        return True
    u32.EnumWindows(cb, 0)
    return out


def shot(path, hwnd=None):
    from PIL import ImageGrab
    if hwnd:
        r = wt.RECT()
        u32.GetWindowRect(hwnd, ctypes.byref(r))
        img = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom), all_screens=True)
    else:
        img = ImageGrab.grab(all_screens=True)
    img.save(path)
    return img.size


def click(x, y):
    u32.SetCursorPos(int(x), int(y))
    time.sleep(0.15)
    u32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.05)
    u32.mouse_event(0x0004, 0, 0, 0, 0)


KEYS = {'enter': 0x0D, 'esc': 0x1B, 'tab': 0x09, 'space': 0x20, 'left': 0x25, 'right': 0x27}


def key(name):
    vk = KEYS[name]
    u32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.05)
    u32.keybd_event(vk, 0, 2, 0)


if __name__ == '__main__':
    cmd = sys.argv[1]
    if cmd == 'list':
        for w in windows():
            print('%9d pid %6d %-28s %-28s %s%s "%s"' % (w['hwnd'], w['pid'], w['process'][:28], w['class'][:28], w['rect'],
                                                      ' (minimised)' if w['iconic'] else '', w['title'][:80]))
    elif cmd == 'shot':
        print(shot(sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else None))
    elif cmd == 'click':
        click(sys.argv[2], sys.argv[3])
        print('clicked', sys.argv[2], sys.argv[3])
    elif cmd == 'key':
        key(sys.argv[2])
        print('pressed', sys.argv[2])
