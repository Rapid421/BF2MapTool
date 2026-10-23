"""What the export must make of MatTest's materials (no game needed):  python check_mattest_inputs.py <map.json>
- a constant colour stays that constant even when the material has another image (normal map) - bug of 2026-10-23
- roughness / metallic constants are carried exactly; the normal map is the normal input
- emission colour x strength reach the record"""
import json
import sys

m = json.load(open(sys.argv[1], encoding='utf-8'))['materials']
fails = []


def val(name, key):
    r = (m.get(name) or {}).get('inputs', {}).get(key) or {}
    return r.get('value'), (r.get('image') or {}).get('name')


for name in ('dome_opengl', 'dome_directx'):
    v, im = val(name, 'base_color')
    if im is not None or not v or abs(v[0] - 0.5) > 1e-3:
        fails.append('%s base colour must stay the constant 0.5, got value %s image %s' % (name, v, im))
    if val(name, 'normal')[1] is None:
        fails.append('%s has no normal map input' % name)
for name, rough in (('rough_1.0', 1.0), ('rough_0.6', 0.6), ('rough_0.3', 0.3), ('rough_0.0', 0.0)):
    v, _ = val(name, 'roughness')
    if v is None or abs(float(v if not isinstance(v, list) else v[0]) - rough) > 1e-3:
        fails.append('%s roughness %s, expected %s' % (name, v, rough))
for name, metal in (('gold_r0.3', 1.0), ('grey_nonmetal_r0.3', 0.0)):
    v, _ = val(name, 'metallic')
    if v is None or abs(float(v if not isinstance(v, list) else v[0]) - metal) > 1e-3:
        fails.append('%s metallic %s, expected %s' % (name, v, metal))
for name, s in (('red_e1', 1.0), ('red_e5', 5.0), ('white_e20', 20.0)):
    v, _ = val(name, 'emission_strength')
    if v is None or abs(float(v if not isinstance(v, list) else v[0]) - s) > 1e-3:
        fails.append('%s emission strength %s, expected %s' % (name, v, s))
print('PASS' if not fails else 'FAIL', fails)
sys.exit(1 if fails else 0)
