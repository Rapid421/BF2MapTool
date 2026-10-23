"""BF2 Map Menu Maker - load several map mods together and every map shows in the menu.

Each map mod made with the BF2 Map Tool carries its own copy of the game's shared menu lists (the level list, the
game-mode lists, the Instant Action screens). Loaded together, the last one wins and only its map shows. This app
merges just those lists from the mods you add (FrostyCLI `mod merge --menu-only`) into one small mod. Put that mod
LAST - below every map mod - in Frosty Mod Manager, or last in the Kyber mod list. Make it again when you add or
remove a map.

    BF2 Map Menu Maker.exe                                   the window
    BF2 Map Menu Maker.exe --make out.fbmod a.fbmod b.fbmod [--game <folder>]    no window (tests, scripts)

It needs FrostyCLI and the Frosty plugins next to it (the BF2 Map Tool bundle layout: FrostyCLI/, FrostyPlugins/).
"""
import json
import os
import subprocess
import sys
import tempfile
import threading
import time

APP = 'BF2 Map Menu Maker'
NOWIN = 0x08000000
GAME_EXE = 'starwarsbattlefrontii.exe'


def base_dir():
    return os.path.dirname(os.path.abspath(sys.executable if getattr(sys, 'frozen', False) else __file__))


def find_frosty():
    """(FrostyCLI.exe, plugins folder) next to the app, one or two folders up, or in a BF2 Map Tool bundle folder"""
    b = base_dir()
    roots = [b, os.path.dirname(b), os.path.dirname(os.path.dirname(b))]
    for r in list(roots):
        try:
            roots += [os.path.join(r, d) for d in os.listdir(r) if d.lower().startswith(('bf2maptool', 'bf2_map_tool'))]
        except OSError:
            pass
    for r in roots:
        cli = next((os.path.join(r, p) for p in ('FrostyCLI/FrostyCLI.exe', 'FrostyCLI.exe') if os.path.isfile(os.path.join(r, p))), None)
        plg = next((os.path.join(r, p) for p in ('FrostyPlugins', 'Plugins') if os.path.isfile(os.path.join(r, p, 'MeshSetPlugin.dll'))), None)
        if cli and plg:
            return cli, plg
    return None, None


def find_game():
    """the game folder from the EA app / Origin / Steam registry entries, then the usual folders"""
    cands = []
    try:
        import winreg
        for hive, key, name in ((winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\EA Games\STAR WARS Battlefront II', 'Install Dir'),
                                (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\Origin Games\1053088', 'Install Dir'),
                                (winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam', 'SteamPath')):
            for view in (winreg.KEY_WOW64_32KEY, winreg.KEY_WOW64_64KEY):
                try:
                    with winreg.OpenKey(hive, key, 0, winreg.KEY_READ | view) as k:
                        v = winreg.QueryValueEx(k, name)[0]
                        if name == 'SteamPath':
                            v = os.path.join(v, 'steamapps', 'common', 'STAR WARS Battlefront II')
                        cands.append(v)
                except OSError:
                    pass
    except ImportError:
        pass
    for d in 'CDEFGHIJ':
        for sub in ('Program Files/EA Games/STAR WARS Battlefront II', 'Program Files (x86)/Origin Games/STAR WARS Battlefront II',
                    'STAR WARS Battlefront II', 'Games/STAR WARS Battlefront II', 'SteamLibrary/steamapps/common/STAR WARS Battlefront II',
                    'EA Games/STAR WARS Battlefront II'):
            cands.append('%s:/%s' % (d, sub))
    return next((os.path.normpath(c) for c in cands if c and os.path.isfile(os.path.join(c, GAME_EXE))), None)


def settings_path():
    d = os.path.join(os.environ.get('APPDATA') or base_dir(), 'BF2MapMenuMaker')
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, 'settings.json')


def load_settings():
    try:
        return json.load(open(settings_path(), encoding='utf-8'))
    except (OSError, ValueError):
        return {}


def save_settings(s):
    try:
        json.dump(s, open(settings_path(), 'w', encoding='utf-8'), indent=1)
    except OSError:
        pass


def make_menu(mods, out, game, log=print, timeout=1800):
    """merge the menu lists of `mods` into `out` -> (ok, message)"""
    cli, plugins = find_frosty()
    if not cli:
        return False, 'FrostyCLI was not found. Keep this app in the BF2 Map Tool folder, next to the FrostyCLI and FrostyPlugins folders.'
    if not game or not os.path.isfile(os.path.join(game, GAME_EXE)):
        return False, 'The game folder is not set (it must hold %s).' % GAME_EXE
    mods = [os.path.abspath(m) for m in mods]
    bad = [m for m in mods if not (os.path.isfile(m) and m.lower().endswith('.fbmod'))]
    if bad:
        return False, 'Not a mod file: %s' % ', '.join(os.path.basename(m) for m in bad)
    if len(mods) < 2:
        return False, 'Add at least two map mods - one map on its own already shows in the menu.'
    out = os.path.abspath(out)
    if out.lower() in [m.lower() for m in mods]:
        return False, 'The menu mod must not overwrite one of the map mods.'
    os.makedirs(os.path.dirname(out), exist_ok=True)
    maps = [os.path.splitext(os.path.basename(m))[0] for m in mods]
    work = tempfile.mkdtemp(prefix='bf2menu_')
    steps = [{'op': 'project-new'},
             {'op': 'cli', 'args': ['mod', 'merge'] + mods + ['--menu-only']},
             {'op': 'mod-build', 'out': out, 'mod-name': ('Maps menu: ' + ', '.join(maps))[:60], 'mod-author': 'BF2 Map Menu Maker',
              'mod-version': '1.0', 'mod-description': 'Lists every map of: %s. Load it LAST, below these map mods.' % ', '.join(maps)}]
    sp = os.path.join(work, 'steps.json')
    json.dump({'game': 'swbf2', 'game-path': game, 'handler-plugins': plugins, 'steps': steps}, open(sp, 'w', encoding='utf-8'), indent=1)
    if os.path.exists(out):
        os.remove(out)
    log('Merging the menus of %d map mods ...' % len(mods))
    env = dict(os.environ, TMP=work, TEMP=work)
    t = time.time()
    p = subprocess.Popen([cli, 'run', sp], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env, cwd=os.path.dirname(cli),
                         creationflags=NOWIN, text=True, encoding='utf-8', errors='replace')
    lines = []
    for line in p.stdout:
        lines.append(line.rstrip())
        if any(k in line for k in ('mod merge', 'change(s)', 'CONFLICT', 'REFUSED', 'ERR', 'OK      mod-build')):
            log('  ' + line.strip()[:160])
        if time.time() - t > timeout:
            p.kill()
            return False, 'FrostyCLI took longer than %d s and was stopped.' % timeout
    p.wait()
    with open(out + '.log', 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
    if p.returncode != 0 or not os.path.isfile(out):
        return False, 'FrostyCLI could not make the menu mod (exit %s). Its log: %s' % (p.returncode, out + '.log')
    return True, 'Made %s (%d KB, %.0f s) for: %s.\nIn Frosty Mod Manager (or the Kyber mod list) put it LAST, below these map mods.' % (
        out, os.path.getsize(out) // 1024, time.time() - t, ', '.join(maps))


def gui():
    import tkinter as tk
    from tkinter import filedialog, messagebox, ttk
    s = load_settings()
    root = tk.Tk()
    root.title(APP)
    root.geometry('720x560')
    root.minsize(560, 440)
    pad = {'padx': 8, 'pady': 4}
    tk.Label(root, text='1  Add the map mods (.fbmod) you want to play together', anchor='w', font=('Segoe UI', 10, 'bold')).pack(fill='x', **pad)
    fr = tk.Frame(root); fr.pack(fill='both', expand=True, padx=8)
    lb = tk.Listbox(fr, selectmode='extended', height=10)
    lb.pack(side='left', fill='both', expand=True)
    sb = tk.Scrollbar(fr, command=lb.yview); sb.pack(side='left', fill='y'); lb.config(yscrollcommand=sb.set)
    bt = tk.Frame(fr); bt.pack(side='left', fill='y', padx=6)

    def add():
        fs = filedialog.askopenfilenames(title='Map mods', filetypes=[('Frosty mods', '*.fbmod')], initialdir=s.get('last_dir') or base_dir())
        for f in fs:
            if f not in lb.get(0, 'end'):
                lb.insert('end', f)
        if fs:
            s['last_dir'] = os.path.dirname(fs[0])
            if not out_v.get():
                out_v.set(os.path.join(os.path.dirname(fs[0]), 'Maps_Menu.fbmod'))

    def remove():
        for i in reversed(lb.curselection()):
            lb.delete(i)
    for t, c in (('Add map mods...', add), ('Remove', remove), ('Clear', lambda: lb.delete(0, 'end'))):
        tk.Button(bt, text=t, command=c, width=16).pack(pady=2)
    tk.Label(root, text='2  The game folder', anchor='w', font=('Segoe UI', 10, 'bold')).pack(fill='x', **pad)
    gf = tk.Frame(root); gf.pack(fill='x', padx=8)
    game_v = tk.StringVar(value=s.get('game') or find_game() or '')
    tk.Entry(gf, textvariable=game_v).pack(side='left', fill='x', expand=True)
    tk.Button(gf, text='Browse...', command=lambda: game_v.set(filedialog.askdirectory(title='STAR WARS Battlefront II folder') or game_v.get())).pack(side='left', padx=4)
    tk.Label(root, text='3  Save the menu mod as', anchor='w', font=('Segoe UI', 10, 'bold')).pack(fill='x', **pad)
    of = tk.Frame(root); of.pack(fill='x', padx=8)
    out_v = tk.StringVar(value='')
    tk.Entry(of, textvariable=out_v).pack(side='left', fill='x', expand=True)
    tk.Button(of, text='Save as...', command=lambda: out_v.set(filedialog.asksaveasfilename(
        defaultextension='.fbmod', filetypes=[('Frosty mod', '*.fbmod')], initialfile='Maps_Menu.fbmod') or out_v.get())).pack(side='left', padx=4)
    go = tk.Button(root, text='Make menu mod', font=('Segoe UI', 11, 'bold'), height=2)
    go.pack(fill='x', **pad)
    bar = ttk.Progressbar(root, mode='indeterminate'); bar.pack(fill='x', padx=8)
    txt = tk.Text(root, height=8, wrap='word'); txt.pack(fill='both', expand=True, **pad)
    txt.insert('end', 'Load order in Frosty Mod Manager:  your map mods  ->  the menu mod LAST.\n'
                      'Make the menu mod again whenever you add or remove a map.\n')

    def log(m):
        root.after(0, lambda: (txt.insert('end', m + '\n'), txt.see('end')))

    def run():
        mods, out, game = list(lb.get(0, 'end')), out_v.get().strip(), game_v.get().strip()
        if not out:
            messagebox.showwarning(APP, 'Choose where to save the menu mod (step 3).'); return
        s['game'] = game; save_settings(s)
        go.config(state='disabled'); bar.start(12)

        def work():
            ok, msg = make_menu(mods, out, game, log)
            def done():
                bar.stop(); go.config(state='normal'); log(msg)
                (messagebox.showinfo if ok else messagebox.showerror)(APP, msg)
            root.after(0, done)
        threading.Thread(target=work, daemon=True).start()
    go.config(command=run)
    cli, _ = find_frosty()
    if not cli:
        log('FrostyCLI was not found next to this app: keep it in the BF2 Map Tool folder.')
    if not game_v.get():
        log('The game folder was not found by itself: press Browse and pick it.')
    root.mainloop()


def main(argv):
    if argv and argv[0] == '--make':
        args = argv[1:]
        game = None
        if '--game' in args:
            i = args.index('--game'); game = args[i + 1]; del args[i:i + 2]
        out, mods = args[0], args[1:]
        ok, msg = make_menu(mods, out, game or load_settings().get('game') or find_game())
        print(('OK ' if ok else 'FAIL ') + msg, flush=True)
        return 0 if ok else 1
    gui()
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
