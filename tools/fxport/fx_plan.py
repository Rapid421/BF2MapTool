"""Select the effects to port and close the emitter set over child emitters (EmitterData), exporting any missing
2015 packages on the way. usage: fx_plan.py <cli.exe> <plan_out.json> <placements_out.json>"""
import json, collections, glob, os, re, subprocess, sys
cli, plan_out, pl_out = sys.argv[1], sys.argv[2], sys.argv[3]
inv = json.load(open('inv/fx_inventory_raw.json'))
GAMEPLAY_CHAIN = ('Gameplay/', 'gameplay/', 'FX/Prefabs/PF_ATAT', 'Objects/Props/_BattleBeyond', 'objects/props/_battlebeyond')
EXCLUDE_BP = set(json.load(open('inv/exclude_bp.json'))) if os.path.exists('inv/exclude_bp.json') else set()
sel = []
for r in inv['effects']:
    if r['scope'] != 'WA' or r['layer'] == 'layer4_streamedlevel_battlebeyond':
        continue
    if any(c.startswith(GAMEPLAY_CHAIN) for c in r['chain']):
        continue
    if r['excluded'] or r['excludedChain'] or r['blueprint'] in EXCLUDE_BP:
        continue
    # layer3_streamedlevel_fx is all ambient: its two AutoStart=false references (the camera-proximity snow) are
    # started by the VFX schematic, which fx_layer.py ports with them. Elsewhere only AutoStart placements qualify.
    if not r['autoStart'] and r['layer'] != 'layer3_streamedlevel_fx':
        continue
    sel.append(r)
bps = collections.Counter(r['blueprint'] for r in sel)

def load_src():
    return {json.load(open(f))['Asset'].lower(): json.load(open(f)) for f in glob.glob('pkg15/*.json')}

def refs(pkg):
    return re.findall(r'"asset": "([^"]+)", "type": "ScalableEmitterDocument"', json.dumps(pkg['Objects']))

for rnd in range(6):
    src = load_src()
    ems, missing, todo, seen = [], set(), [], set()
    for b in bps:
        todo += refs(src[b.lower()])
    while todo:
        e = todo.pop()
        if e.lower() in seen:
            continue
        seen.add(e.lower())
        if e.lower() not in src:
            missing.add(e); continue
        ems.append(e)
        todo += refs(src[e.lower()])
    if not missing:
        break
    open('inv/missing_em.txt', 'w').write('\n'.join(sorted(missing)) + '\n')
    subprocess.run([cli, '--game', 'swbf1', '--game-path', r'E:\STAR WARS Battlefront', 'ebx', 'graph-export',
                    '--list', 'inv/missing_em.txt', '--out-dir', 'pkg15'], capture_output=True)
    print('round %d: exported %d missing child emitters' % (rnd, len(missing)))
print(len(sel), 'placements', len(bps), 'blueprints', len(ems), 'emitters')
json.dump({'emitters': sorted(ems), 'blueprints': sorted(bps), 'placements': dict(bps)}, open(plan_out, 'w'), indent=1)
json.dump(sel, open(pl_out, 'w'), indent=0)
