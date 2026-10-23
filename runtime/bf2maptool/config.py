"""map.json (the scene export) + settings.json (machine paths) -> one Job object every stage works from.

Nothing map-specific or machine-specific lives in code: names, asset paths and GUIDs are derived here from
``map.name``; tool paths come from settings.json.
"""
import hashlib
import json
import os
import re
import subprocess
import tempfile
import unicodedata
import uuid

MAP_VERSION = 1
MODES = ('blast', 'hvv', 'arcade', 'supremacy')
CLASSES = ('opaque', 'emissive', 'cutout', 'cutout2s', 'glass', 'lava', 'holo')
COLLISION = ('exact', 'custom', 'hull', 'none')
SETTINGS_KEYS = {
    'game_path': 'the game folder (holds starwarsbattlefrontii.exe)',
    'frostycli': 'FrostyCLI.exe',
    'plugins_path': 'the Frosty Editor Plugins folder',
    'havok_tool': 'HavokCollisionTool.dll',
    'havok_template': 'collision template .res (its .resmeta sits next to it)',
    'fluxbake': 'the light baker executable',
    'work_dir': 'a folder with room for builds (several GB)',
}
OPTIONAL_SETTINGS = {'blender': '', 'python': '', 'author': 'bf2maptool', 'leakmask': '', 'dotnet': 'dotnet',
                     'min_free_memory_gb': 6.0}


class ConfigError(Exception):
    pass


def _read_json(path):
    with open(path, 'r', encoding='utf-8') as f:
        return json.load(f)


def load_settings(path):
    s = _read_json(path)
    missing = [k for k in SETTINGS_KEYS if not s.get(k)]
    if missing:
        raise ConfigError('settings.json is missing: ' + ', '.join('%s (%s)' % (k, SETTINGS_KEYS[k]) for k in missing))
    for k, v in OPTIONAL_SETTINGS.items():
        s.setdefault(k, v)
    for k in list(SETTINGS_KEYS) + ['blender', 'leakmask']:
        if s.get(k):
            s[k] = s[k].replace('\\', '/')
    return s


def safe(name):
    """an asset / file name: ASCII letters, digits, '_' and '-'. Accents are dropped ('Béton' -> 'Beton'); a letter with
    no ASCII form ('金属') becomes '_' and the name gets a short hash of the original, so two such names stay apart"""
    out, lost = [], False
    for ch in name:
        a = unicodedata.normalize('NFKD', ch).encode('ascii', 'ignore').decode('ascii')
        if not a and not ch.isascii():
            lost = True
            a = '_'
        out.append(''.join(c if (c.isalnum() or c in '_-') else '_' for c in a))
    s = ''.join(out)
    return s + '_%s' % hashlib.sha1(name.encode('utf-8')).hexdigest()[:6] if lost else s


def ascii_alias(folder):
    """an ASCII-only path that leads to `folder` (a directory junction, made once): <drive>/_bf2maptool_links/<hash>,
    else the temp folder when that is ASCII. -> the alias, or `folder` itself when no alias can be made"""
    folder = os.path.abspath(folder)
    key = hashlib.sha1(folder.encode('utf-8')).hexdigest()[:12]
    for base in (os.path.join(os.path.splitdrive(folder)[0] + os.sep, '_bf2maptool_links'),
                 os.path.join(tempfile.gettempdir(), 'bf2maptool_links')):
        if not base.isascii():
            continue
        link = os.path.join(base, key)
        try:
            if os.path.isdir(link) and os.path.samefile(link, folder):
                return link.replace('\\', '/')
            os.makedirs(base, exist_ok=True)
            if os.path.lexists(link):
                os.rmdir(link)                                # a stale junction (rmdir removes the link, not its target)
            r = subprocess.run(['cmd', '/c', 'mklink', '/J', link, folder], capture_output=True, text=True, timeout=30,
                               creationflags=0x08000000)
            if r.returncode == 0 and os.path.samefile(link, folder):
                return link.replace('\\', '/')
        except (OSError, subprocess.SubprocessError):
            continue
    return folder.replace('\\', '/')


def file_hash(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def data_hash(obj):
    return hashlib.sha256(json.dumps(obj, sort_keys=True, default=str).encode('utf-8')).hexdigest()


# what a material input becomes when its image is missing: a colour map -> mid grey (Blender would draw magenta, which
# no one wants in game), anything else -> the input is left out (no normal relief, default smoothness / metal / alpha)
MISSING_STANDIN = {'base_color': {'value': [0.214, 0.214, 0.214]}}      # scene-linear 0.214 = sRGB 128


def drop_missing_images(m, export_dir):
    """take the inputs whose image is missing (not packed, not on disk) out of every material, so ONE lost texture of a
    ripped map does not stop the build; -> {material: [input, ...]} for the check `tex.missing` (a warning)"""
    out = {}
    for name, rec in (m.get('materials') or {}).items():
        inp = rec.get('inputs') or {}
        for key in list(inp):
            r = inp[key]
            img = r.get('image') if isinstance(r, dict) else None
            if img is None:
                continue
            lost = img.get('missing') or not img.get('file') or not os.path.exists(os.path.join(export_dir, img['file']))
            if lost:
                out.setdefault(name, []).append('%s (%s)' % (key, img.get('name', '?')))
                if key in MISSING_STANDIN:
                    inp[key] = dict(MISSING_STANDIN[key])
                else:
                    del inp[key]
    return out


class Job:
    """One map build: the scene export, the machine settings, every derived name and folder."""

    def __init__(self, map_path, settings):
        self.map_path = os.path.abspath(map_path).replace('\\', '/')
        self.export_dir = os.path.dirname(self.map_path)
        self.settings = settings
        self.map = m = _read_json(map_path)
        if m.get('version') != MAP_VERSION:
            raise ConfigError('map.json version %r is not supported (this runtime reads version %d)' % (m.get('version'), MAP_VERSION))
        self.missing_images = drop_missing_images(m, self.export_dir)
        from . import texpack
        self.glow_settled = texpack.drop_invisible(m.get('materials') or {})
        self.glow_settled.update(texpack.settle_glow(m.get('materials') or {}, texpack.Source(self.src)))
        info = m.get('map') or {}
        name = info.get('name', '')
        if not re.fullmatch(r'[A-Za-z][A-Za-z0-9]{1,23}', name):
            raise ConfigError('The level name %r must be 2-24 letters or digits and start with a letter.' % name)
        self.name = name
        self.display = info.get('display_name') or name
        self.planet = info.get('planet') or name
        self.description = info.get('description') or ''
        self.host = info.get('host') or 'kamino_01'
        # a map file without the key gets the three built modes; an EMPTY list stays empty (check `map.no_mode`)
        self.modes = [x for x in MODES if x in (['blast', 'hvv', 'arcade'] if info.get('modes') is None else info['modes'])]
        bad = [x for x in (info.get('modes') or []) if x not in MODES]
        if bad:
            raise ConfigError('Unknown game mode(s) in map.json: %s' % ', '.join(bad))
        # asset names
        self.level_dir = 'AddedContent/%s/Levels/%s_01' % (name, name)
        self.level = '%s/%s_01' % (self.level_dir, name)
        self.bundle = 'win32/' + self.level
        self.prefix = 'Objects/Ported/' + name
        self.short = name
        self.mvdb = self.level + '/MeshVariationDb_Win32'
        self.default_layer = self.level_dir + '/Default'
        self.registry = '%s/%s_01_networkregistry_Win32' % (self.level_dir, name)
        self.level2_dir = 'AddedContent/%s/Levels/%s_02' % (name, name)
        self.level2 = '%s/%s_02' % (self.level2_dir, name)
        self.bundle2 = 'win32/' + self.level2
        self.piece_prefix = (re.sub(r'[^A-Za-z]', '', name)[:2] or 'PC').upper()
        self.tex_prefix = 'T_%s_' % self.piece_prefix
        self._scheme = name.lower() + '.bf2maptool://'
        # folders
        self.work = os.path.join(settings['work_dir'], name).replace('\\', '/')
        self.dirs = {k: '%s/%s' % (self.work, k) for k in
                     ('pieces', 'tex', 'col', 'pkg', 'layers', 'projects', 'logs', 'dumps', 'nav', 'mods', 'tmp', 'steps', 'bake', 'db')}
        self.state_path = self.work + '/build.json'

    def makedirs(self):
        for d in self.dirs.values():
            os.makedirs(d, exist_ok=True)

    def sg(self, s):
        """deterministic GUID in this map's own namespace"""
        return str(uuid.uuid5(uuid.NAMESPACE_URL, self._scheme + s))

    def path(self, kind, *parts):
        return '/'.join((self.dirs[kind],) + parts)

    def native(self, p):
        """the same file by a path with only ASCII characters, for the tools that cannot open others (the FBX reader in
        FrostyCLI: a work folder 'awk wörk/work dir é' failed every piece with 'a mismatch in the amount of LODs').
        The work folder is reached through a directory junction (no admin rights needed) on the same drive."""
        if p.isascii() or not p.startswith(self.work):
            return p
        return ascii_alias(self.work) + p[len(self.work):]

    def src(self, rel):
        """a file named in map.json, relative to the export folder"""
        return rel.replace('\\', '/') if os.path.isabs(rel) else '%s/%s' % (self.export_dir, rel.replace('\\', '/'))

    def project(self, stage):
        return '%s/%s.fbproject' % (self.dirs['projects'], stage)

    def wants(self, stage):
        """is an optional stage part of this build? 'bake': when the build was started as a bake;
        'supremacy': when that mode is ticked"""
        if stage == 'supremacy':
            return 'supremacy' in self.modes
        return bool((self.map.get('options') or {}).get(stage))

    def play(self):
        """(centre, radius) of the play area in scene metres; pieces far from it may be coarser"""
        p = self.map.get('play') or {}
        return tuple(float(v) for v in p.get('centre', (0.0, 0.0, 0.0))), float(p.get('radius', 150.0))

    # blender (x, y, z) metres -> game (x, z, -y) + offset
    def offset(self):
        o = (self.map.get('placement') or {}).get('offset')
        if o is None:
            from . import hosts
            o = hosts.get(self.host).offset_for(self.play()[0])
        return tuple(float(v) for v in o)

    def to_game(self, p):
        o = self.offset()
        return (p[0] + o[0], p[2] + o[1], -p[1] + o[2])

    def from_game(self, p):
        o = self.offset()
        return (p[0] - o[0], -(p[2] - o[2]), p[1] - o[1])
