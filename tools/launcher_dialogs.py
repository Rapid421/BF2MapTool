"""Launcher dialogs that stop a game launch, seen and answered with the mouse / keyboard.

The EA app writes one log line per error dialog it shows:
    Forwarding EA Desktop error dialog to splash: dialogId=[1], errorCode=[10608], title=[Your latest sync was interrupted], buttons=[2]
A Watcher reads the new part of that log. For a KNOWN dialog the answer is given; anything else is only photographed
and reported (never clicked blind).

  10608  "Your latest sync was interrupted" (the cloud save check before a launch failed): buttons LAUNCH GAME / CANCEL.
         Answer: LAUNCH GAME - but only when the game's local save files exist, and after copying them to a backup
         folder, so the launch cannot lose anything that is on this PC.

python launcher_dialogs.py find <screenshot.png>      -> where the primary (blue) button is in a saved screenshot
python launcher_dialogs.py now <out dir>              -> answer a dialog that is on screen right now
"""
import ctypes
import ctypes.wintypes as wt
import os
import re
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import screen

LINE = re.compile(r'error dialog to splash: dialogId=\[(\d+)\], errorCode=\[(\d+)\], title=\[(.*?)\], buttons=\[(\d+)\]')
KNOWN = {'10608': 'launch'}
SAVE_SUB = 'STAR WARS Battlefront II/settings'


def ea_log():
    return os.path.join(os.environ.get('LOCALAPPDATA', ''), 'Electronic Arts', 'EA Desktop', 'Logs', 'EADesktop.log')


def documents():
    buf = ctypes.create_unicode_buffer(520)
    ctypes.windll.shell32.SHGetFolderPathW(None, 5, None, 0, buf)
    return buf.value


class Watcher:
    def __init__(self):
        self.path = ea_log()
        self.pos = os.path.getsize(self.path) if os.path.exists(self.path) else 0

    def new_dialogs(self):
        if not os.path.exists(self.path):
            return []
        n = os.path.getsize(self.path)
        if n < self.pos:
            self.pos = 0                                   # the log was rotated
        if n == self.pos:
            return []
        with open(self.path, 'rb') as f:
            f.seek(self.pos)
            d = f.read().decode('utf-8', 'replace')
        self.pos = n
        return [{'code': m.group(2), 'title': m.group(3), 'buttons': int(m.group(4))} for m in LINE.finditer(d)]


def dialog_windows():
    """visible EA app windows that are dialog-sized (the main window is far larger)"""
    out = []
    for w in screen.windows():
        l, t, r, b = w['rect']
        if w['process'].lower() == 'eadesktop.exe' and not w['iconic'] and 300 <= r - l <= 1000 and 200 <= b - t <= 700:
            out.append(w)
    return out


def primary_button(img):
    """centre (x, y) of the largest block of the dialog's blue button colour in a PIL image, or None"""
    import numpy as np
    a = np.asarray(img.convert('RGB')).astype(np.int32)
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    m = (b > 200) & (r < 90) & (g > 60) & (g < 150) & (b - g > 80)
    rows = m.sum(1)
    if rows.max() < 60:
        return None
    ys = [i for i, v in enumerate(rows) if v >= 60]
    # the longest run of consecutive rows
    best, cur = [], []
    for y in ys:
        cur = cur + [y] if cur and y == cur[-1] + 1 else [y]
        if len(cur) > len(best):
            best = cur
    if len(best) < 12:
        return None
    cols = m[best[0]:best[-1] + 1].sum(0)
    xs = [i for i, v in enumerate(cols) if v >= len(best) * 0.6]
    if len(xs) < 40:
        return None
    return (xs[0] + xs[-1]) // 2, (best[0] + best[-1]) // 2, len(xs), len(best)


def backup_saves(out_dir):
    src = os.path.join(documents(), SAVE_SUB)
    files = [f for f in (os.listdir(src) if os.path.isdir(src) else []) if os.path.isfile(os.path.join(src, f))]
    if not files or not any(os.path.getsize(os.path.join(src, f)) > 1000 for f in files):
        return None, src
    dst = os.path.join(out_dir, 'save_backup_' + time.strftime('%Y%m%d_%H%M%S'))
    os.makedirs(dst, exist_ok=True)
    for f in files:
        shutil.copy2(os.path.join(src, f), os.path.join(dst, f))
    return dst, src


def dismiss(w):
    """close a dialog WITHOUT launching: Esc, then the second button (right of the blue one). -> True when it is gone"""
    from PIL import ImageGrab

    def gone():
        return not [x for x in dialog_windows() if x['hwnd'] == w['hwnd']]
    ctypes.windll.user32.SetForegroundWindow(w['hwnd'])
    time.sleep(0.4)
    screen.key('esc')
    time.sleep(2)
    if gone():
        return True
    l, t, r, b = w['rect']
    hit = primary_button(ImageGrab.grab(bbox=(l, t, r, b), all_screens=True))
    if hit:
        screen.click(l + hit[0] + hit[2] // 2 + 59, t + hit[1])
        time.sleep(2)
    return gone()


def answer(ev, out_dir, note):
    """ev = {'code', 'title', 'buttons'}; note(text) logs. -> True when the dialog was answered and is gone."""
    from PIL import ImageGrab
    os.makedirs(out_dir, exist_ok=True)
    wins = dialog_windows()
    stamp = time.strftime('%H%M%S')
    full = os.path.join(out_dir, 'launcher_dialog_%s.png' % stamp)
    ImageGrab.grab(all_screens=True).save(full)
    note('launcher dialog: code %s "%s" (%d buttons); screenshot %s; %d dialog window(s)' % (ev['code'], ev['title'], ev['buttons'], full, len(wins)))
    if KNOWN.get(ev['code']) != 'launch':
        note('launcher dialog: code %s is not one this test answers - left alone' % ev['code'])
        return False
    if not wins:
        note('launcher dialog: no dialog-sized EA window is on screen - nothing to click')
        return False
    bk, src = backup_saves(out_dir)
    if not bk:
        note('launcher dialog: no local save files in "%s" - NOT pressing Launch Game (it could replace the cloud save with an empty one)' % src)
        return False
    note('launcher dialog: local save files copied to %s' % bk)
    w = wins[0]
    l, t, r, b = w['rect']
    ctypes.windll.user32.SetForegroundWindow(w['hwnd'])
    time.sleep(0.5)
    img = ImageGrab.grab(bbox=(l, t, r, b), all_screens=True)
    hit = primary_button(img)
    if hit:
        x, y = l + hit[0], t + hit[1]
        note('launcher dialog: blue button %d x %d px at screen (%d, %d) - clicking' % (hit[2], hit[3], x, y))
        screen.click(x, y)
    else:
        note('launcher dialog: the blue button was not found in the window picture - pressing Enter instead')
        screen.key('enter')
    for i in range(10):
        time.sleep(1)
        if not [x for x in dialog_windows() if x['hwnd'] == w['hwnd']]:
            note('launcher dialog: answered, the dialog closed after %d s' % (i + 1))
            return True
    # mouse did not do it: keyboard (the primary button is the default one)
    ctypes.windll.user32.SetForegroundWindow(w['hwnd'])
    time.sleep(0.3)
    screen.key('enter')
    time.sleep(3)
    gone = not [x for x in dialog_windows() if x['hwnd'] == w['hwnd']]
    note('launcher dialog: after Enter the dialog is %s' % ('closed' if gone else 'STILL OPEN'))
    return gone


if __name__ == '__main__':
    if sys.argv[1] == 'find':
        from PIL import Image
        print(primary_button(Image.open(sys.argv[2])))
    elif sys.argv[1] == 'now':
        for w in dialog_windows():
            print('window', w)
        print(answer({'code': '10608', 'title': 'Your latest sync was interrupted', 'buttons': 2}, sys.argv[2], print))
