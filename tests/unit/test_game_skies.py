"""the add-on's Game sky list (props.GAME_SKIES, read without Blender) is the runtime's look.GAME_SKIES: same ids, same
names, in the same order; every asset is a shipped VisualEnvironmentBlueprint path; the Sky list keeps 'world' and adds
'game' after it (Blender stores enum choices by position).   python test_game_skies.py"""
import ast
import os
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
sys.path.insert(0, os.path.join(ROOT, 'runtime'))
from bf2maptool.stages import look  # noqa: E402

tree = ast.parse(open(os.path.join(ROOT, 'addon', 'bf2_map_tool', 'props.py'), encoding='utf-8').read())
vals = {}
for node in tree.body:
    if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name) and node.targets[0].id in ('GAME_SKIES', 'SKY_PRESETS'):
        vals[node.targets[0].id] = ast.literal_eval(node.value)
addon = [(k, n) for k, n, _ in vals['GAME_SKIES']]
runtime = [(k, v[0]) for k, v in look.GAME_SKIES.items()]
ok = True


def expect(what, good):
    global ok
    ok &= bool(good)
    print('%-4s %s' % ('ok' if good else 'FAIL', what))


expect('add-on and runtime lists are the same (%d skies)' % len(runtime), addon == runtime)
expect('every asset is a Levels/... VE_ path', all(v[1].startswith('Levels/') and '/VE_' in v[1] for v in look.GAME_SKIES.values()))
ids = [k for k, _, _ in vals['SKY_PRESETS']]
expect('Sky list: world then game at the end', ids[-2:] == ['world', 'game'])
expect('default game sky resolves', look.game_sky_asset({}) == look.GAME_SKIES['tatooine_sunny'][1])
expect('a typed asset path wins', look.game_sky_asset({'game_sky': 'hoth_sunny', 'game_sky_asset': ' Levels/X/VE_Y/ '}) == 'Levels/X/VE_Y')
print('PASS' if ok else 'FAIL')
sys.exit(0 if ok else 1)
