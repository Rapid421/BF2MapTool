"""Print a door_probe.json so it can be read: python door_show.py <door_probe.json>"""
import json
import sys

d = json.load(open(sys.argv[1]))
print('door', d.get('door'), 'game pos', d.get('door_game_pos'), '| wires', d.get('wires'))
print('where:', d.get('where'))
for phase in ('before', 'after'):
    print('==', phase)
    for node, lines in (d.get(phase) or {}).items():
        keep = [l for l in lines[1:] if any(k in l for k in ('Enabled', 'NumPlayersInside', 'Value', 'Out ', 'Out\t', 'In', 'Team', 'Include', 'live', 'Duration', 'Condition'))]
        print(' ', lines[0][:70] if lines else node)
        for l in keep[:9]:
            print('      ', l[:150])
print('delivered', json.dumps(d.get('delivered'))[:400])
print('shots', [(s['name'], s['ok']) for s in d.get('shots', [])])
for l in (d.get('trace_tail') or [])[-14:]:
    print('  trace', l[:170])
