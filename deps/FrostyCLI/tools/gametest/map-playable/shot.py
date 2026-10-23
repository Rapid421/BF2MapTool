"""Game-window screenshots only, and only while the game is alive (checked before and after each capture).

shot(path) -> True when a frame of the game window was saved. Uses the enlighten session's shot_window.ps1
(PrintWindow on the game's own top-level window: never the desktop or another window). A capture taken while the game
died is deleted.
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import harness  # noqa: E402

SHOT_PS1 = r'F:\Frosty\projects\enlighten-authoring\scripts\shot_window.ps1'
SHOTS = os.path.join(os.path.dirname(HERE), 'shots')


def shot(name, width=1600):
    path = name if os.path.isabs(name) else os.path.join(SHOTS, name if name.endswith('.png') else name + '.png')
    if not harness.game_running():
        print('shot skipped (no game):', os.path.basename(path))
        return False
    r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', SHOT_PS1, '-Out', path,
                        '-Width', str(width)], capture_output=True, text=True)
    ok = os.path.exists(path) and 'SKIPPED' not in r.stdout
    if ok and not harness.game_running():
        os.remove(path)
        print('shot discarded (the game went during the capture):', os.path.basename(path))
        return False
    print(('shot ' + os.path.basename(path)) if ok else ('shot failed: ' + (r.stdout.strip() or r.stderr.strip())[:200]))
    return ok


if __name__ == '__main__':
    shot(sys.argv[1])
