"""Read the ported effects back out of a SAVED project (fresh process) and prove them field-exact.

usage: fx_verify.py <FrostyCLI.exe> <project.fbproject> <report.json>

Two independent checks per asset:
  A. read-back == converted package, field for field (the import is lossless);
  B. read-back vs the 2015 source, walked in parallel through the object graph: every 2015 leaf field is either equal
     in BFII (float32-exact), or dropped by a named conversion rule (fx_convert.py); processor chains are followed with
     the spliced processors skipped (UpdateOrientationData / UpdateLinearVelocityData / the old UpdateClipScaleData)
     and the regenerated UpdateClipScaleData accounted separately. Anything else is a "differ" with its path.
  C. imported 2015 atlases (tex_import/texture_steps.json): the AtlasTextureAsset read back equals its package and the
     2015 EBX (frames, columns, name, resource name); the res read back (framed: 16-byte meta + header + mip sizes) and
     the texel chunk read back are byte-identical to what was imported, and the header's first 32 bytes and the chunk
     are byte-identical to the 2015 files.
"""
import json, os, subprocess, sys, struct, collections, glob

HERE = os.path.dirname(os.path.abspath(__file__))
FX = os.path.dirname(HERE)
sys.path.insert(0, HERE)
SCHEMA2 = json.load(open(os.path.join(FX, 'inv', 'schema_bf2.json')))['types']
SPLICED = {'UpdateOrientationData', 'UpdateLinearVelocityData', 'UpdateClipScaleData'}


def f32(x):
    return struct.unpack('<f', struct.pack('<f', float(x)))[0]


def same_scalar(a, b):
    if isinstance(a, bool) or isinstance(b, bool):
        return a is b or a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return f32(a) == f32(b) if isinstance(a, float) or isinstance(b, float) else a == b
    return a == b


class Walk:
    def __init__(self, p15, p2, destmap, all_fields=False):
        self.a, self.b = p15['Objects'], p2['Objects']
        self.all_fields = all_fields      # package vs read-back (same game): compare every field, drop nothing
        self.destmap = destmap
        self.match = 0
        self.differ = []
        self.dropped = collections.Counter()
        self.added = collections.Counter()
        self.seen = set()

    def skip_chain(self, ref, objs):
        while isinstance(ref, dict) and 'ref' in ref and objs[ref['ref']]['type'] in SPLICED:
            ref = objs[ref['ref']]['fields'].get('NextProcessor')
        return ref

    def value(self, va, vb, path, field=None):
        if field in ('NextProcessor', 'RootProcessor'):
            va = self.skip_chain(va, self.a)
            vb = self.skip_chain(vb, self.b)
        if isinstance(va, dict) and 'ref' in va and len(va) == 1:
            if not (isinstance(vb, dict) and 'ref' in vb):
                self.differ.append((path, 'ref vs %s' % str(vb)[:40])); return
            if self.all_fields and va['ref'] != vb['ref']:
                self.differ.append((path, 'ref objects[%d] vs objects[%d]' % (va['ref'], vb['ref']))); return
            self.obj(va['ref'], vb['ref'], path)
            return
        if isinstance(va, dict) and 'null' in va:
            if isinstance(vb, dict) and 'null' in vb:
                self.match += 1
            elif field == 'NormalTexture' and isinstance(vb, dict):
                self.differ.append((path, 'null vs set'))
            else:
                self.differ.append((path, 'null vs %s' % str(vb)[:40]))
            return
        if isinstance(va, dict) and 'external' in va:
            ea = va['external']
            eb = (vb or {}).get('external') if isinstance(vb, dict) else None
            if eb is None:
                if field == 'NormalTexture' and (ea.get('asset') or '') == 'Systems/Emitter/DefaultNormalTexture':
                    self.dropped['NormalTexture=DefaultNormalTexture -> null (DICE rule)'] += 1
                    return
                self.differ.append((path, 'external vs %s' % str(vb)[:40])); return
            want = self.destmap.get((ea.get('file_guid') or '').lower())
            if want:
                ok = (eb.get('asset') or '').lower() == want.lower()
            elif not ea.get('file_guid'):
                ok = (eb.get('asset') or '').lower() == (ea.get('asset') or '').lower()     # package named it by path
            else:
                ok = (eb.get('file_guid') or '').lower() == (ea.get('file_guid') or '').lower()
            if ok:
                self.match += 1
            else:
                self.differ.append((path, 'external %s vs %s' % (ea.get('asset'), eb.get('asset'))))
            return
        if isinstance(va, dict) and 'resource' in va and len(va) == 1:
            rb = (vb or {}).get('resource') if isinstance(vb, dict) else None
            if rb is not None and (rb.get('name') or '').lower() == (va['resource'].get('name') or '').lower():
                self.match += 1       # resources are identified by name; the RID is assigned by the destination
            else:
                self.differ.append((path, 'resource %s vs %s' % (va['resource'].get('name'), str(vb)[:60])))
            return
        if isinstance(va, dict) and 'type' in va and 'fields' in va:
            if not (isinstance(vb, dict) and vb.get('type') == va['type']):
                self.differ.append((path, 'struct %s vs %s' % (va['type'], str(vb)[:40]))); return
            self.fields(va['type'], va['fields'], vb['fields'], path)
            return
        if isinstance(va, list):
            if not isinstance(vb, list) or len(va) != len(vb):
                self.differ.append((path, 'list len %d vs %s' % (len(va), len(vb) if isinstance(vb, list) else vb))); return
            for i, (x, y) in enumerate(zip(va, vb)):
                self.value(x, y, '%s[%d]' % (path, i))
            return
        if same_scalar(va, vb):
            self.match += 1
        else:
            self.differ.append((path, '%r vs %r' % (va, vb)))

    def fields(self, t, fa, fb, path):
        s2 = (SCHEMA2.get(t) or {}).get('fields') or {}
        for k, v in fa.items():
            if k == 'Name' and path.endswith('objects[0]'):
                continue
            # a 2015 field is dropped only when BFII's schema for the type is known and lacks it; for a type the
            # schema file does not list, every field is compared (a field BFII lacks then shows as a difference)
            if not self.all_fields and s2 and k not in s2:
                self.dropped['%s.%s' % (t, k)] += 1
                continue
            if k == 'Flags':
                self.match += 1       # identity bits re-derived for the new instance guid; flag byte checked below
                if isinstance(v, int) and isinstance(fb.get(k), int) and (v >> 24) != (fb[k] >> 24):
                    self.differ.append((path + '.Flags', 'flag byte %x vs %x' % (v >> 24, fb[k] >> 24)))
                continue
            self.value(v, fb.get(k), path + '.' + k, k)
        for k in fb:
            if k not in fa:
                self.added['%s.%s' % (t, k)] += 1

    def obj(self, ia, ib, path):
        if (ia, ib) in self.seen:
            return
        self.seen.add((ia, ib))
        oa, ob = self.a[ia], self.b[ib]
        if oa['type'] != ob['type']:
            self.differ.append((path, 'type %s vs %s' % (oa['type'], ob['type']))); return
        self.fields(oa['type'], oa['fields'], ob['fields'], '%s->objects[%d]' % (path, ib))


def main():
    cli, proj, report = sys.argv[1], sys.argv[2], sys.argv[3]
    man = json.load(open(os.path.join(FX, 'pkg_core', 'manifest.json')))
    assets = [d for d, _ in man['emitters']] + [d for d, _ in man['blueprints']]
    layer = ('AddedContent/Hoth2015/Levels/Hoth2015_01/FX_Hoth2015', os.path.join('pkg_core', 'layer_fx_hoth2015.json'))
    excl = ('AddedContent/Hoth2015/Levels/Hoth2015_01/EmitterExclusionVolumes_Hoth2015', os.path.join('pkg_core', 'exclusion_hoth2015.json'))
    out = os.path.join(FX, 'pkg_readback')
    os.makedirs(out, exist_ok=True)
    lst = os.path.join(out, '_list.txt')
    ts_path = os.path.join(FX, 'tex_import', 'texture_steps.json')
    tsteps = json.load(open(ts_path))['steps'] if os.path.exists(ts_path) else []
    atlases = [(s['as'], s['file']) for s in tsteps if s['op'] == 'ebx-graph-import']
    open(lst, 'w').write(chr(10).join(assets + [layer[0], excl[0]] + [a for a, _ in atlases]) + chr(10))
    r = subprocess.run([cli, '--game', 'swbf2', '--game-path', r'F:\STAR WARS Battlefront II', '--project', proj,
                        'ebx', 'graph-export', '--list', lst, '--out-dir', out], capture_output=True, text=True)
    src15 = {json.load(open(f))['Asset'].lower(): json.load(open(f)) for f in glob.glob(os.path.join(FX, 'pkg15', '*.json'))}
    destmap = {}
    for d, fn in man['emitters']:
        p = json.load(open(os.path.join(FX, fn)))
        destmap[src15[p['Source']['asset'].lower()]['FileGuid'].lower()] = d
    ts = os.path.join(FX, 'tex_import', 'texture_steps.json')
    if os.path.exists(ts):
        destmap.update({g.lower(): a for g, a in json.load(open(ts))['guid_to_asset'].items()})   # imported 2015 atlases
    rows, tot = [], collections.Counter()
    for d, fn in man['emitters'] + man['blueprints'] + [layer, excl]:
        rb = os.path.join(out, d.lower().replace('/', '_') + '.json')
        if not os.path.exists(rb):
            rows.append({'asset': d, 'readback': 'MISSING'}); tot['missing'] += 1; continue
        got = json.load(open(rb))
        want = json.load(open(os.path.join(FX, fn)))
        # A: package vs read-back
        diffA, cmpA = [], 0
        if len(got['Objects']) != len(want['Objects']):
            diffA.append('object count %d vs %d' % (len(want['Objects']), len(got['Objects'])))
        else:
            # every object once, index for index (same object table order is part of the contract); refs must point
            # at the same index, every field of every object is compared (root Name = the destination asset name)
            w = Walk({'Objects': want['Objects']}, {'Objects': got['Objects']}, {}, all_fields=True)
            for i in range(len(want['Objects'])):
                w.obj(i, i, '')
            diffA += ['%s: %s' % p for p in w.differ]
            cmpA += w.match + len(w.differ)
        row = {'asset': d, 'objects': len(got['Objects']), 'package_vs_readback_differ': len(diffA), 'package_diffs': diffA[:10],
               'package_fields_compared': cmpA}
        # B: 2015 source vs read-back
        src = want.get('Source') or {}
        if src.get('asset') and src['asset'].lower() in src15:
            w = Walk(src15[src['asset'].lower()], got, destmap)
            w.obj(0, 0, 'root')
            row.update({'fields_2015_equal': w.match, 'fields_2015_differ': len(w.differ), 'differ': w.differ[:20],
                        'dropped_by_rule': dict(w.dropped), 'bfii_fields_added': sum(w.added.values())})
            tot['equal'] += w.match; tot['differ'] += len(w.differ); tot['dropped'] += sum(w.dropped.values())
        tot['package_differ'] += len(diffA)
        tot['package_fields_compared'] += cmpA
        rows.append(row)
    # C: imported 2015 atlases
    if atlases:
        steps = [{'op': 'project-load', 'file': os.path.abspath(proj)}]
        blobs = []
        for s in tsteps:
            if s['op'] == 'res-import':
                o = os.path.join(out, 'rb_' + os.path.basename(s['file']))
                steps.append({'op': 'res-export', 'res': s['res'], 'out': o, 'framed': True}); blobs.append((s['file'], o, s['res']))
            elif s['op'] == 'chunk-import':
                o = os.path.join(out, 'rb_' + os.path.basename(s['file']))
                steps.append({'op': 'chunk-export', 'chunk_id': s['chunk_id'], 'out': o}); blobs.append((s['file'], o, s['chunk_id']))
        for _, o, _ in blobs:
            if os.path.exists(o):
                os.remove(o)
        pj = os.path.join(out, '_atlas_readback.json')
        json.dump({'game': 'swbf2', 'game-path': r'F:\STAR WARS Battlefront II', 'steps': steps}, open(pj, 'w'), indent=1)
        subprocess.run([cli, 'run', pj], capture_output=True, text=True)
        pkg15 = {json.load(open(f))['Asset'].lower(): json.load(open(f)) for f in glob.glob(os.path.join(FX, 'pkg15_tex', '*.json'))}
        for name, pf in atlases:
            rb = os.path.join(out, name.lower().replace('/', '_') + '.json')
            if not os.path.exists(rb):
                rows.append({'asset': name, 'readback': 'MISSING'}); tot['missing'] += 1; continue
            got = json.load(open(rb)); want = json.load(open(pf))
            w = Walk({'Objects': want['Objects']}, {'Objects': got['Objects']}, {}, all_fields=True)
            for i in range(min(len(want['Objects']), len(got['Objects']))):
                w.obj(i, i, '')
            src = pkg15[name.lower()]
            w15 = Walk(src, got, {})
            w15.obj(0, 0, 'root')
            tot['package_differ'] += len(w.differ) + (len(got['Objects']) != len(want['Objects']))
            tot['package_fields_compared'] += w.match + len(w.differ)
            tot['equal'] += w15.match; tot['differ'] += len(w15.differ); tot['dropped'] += sum(w15.dropped.values())
            rows.append({'asset': name, 'objects': len(got['Objects']), 'package_vs_readback_differ': len(w.differ),
                         'package_diffs': ['%s: %s' % p for p in w.differ][:10], 'fields_2015_equal': w15.match,
                         'fields_2015_differ': len(w15.differ), 'differ': w15.differ[:20], 'dropped_by_rule': dict(w15.dropped),
                         'bfii_fields_added': sum(w15.added.values())})
        src15_res = {}
        for fg, a in json.load(open(ts_path))['guid_to_asset'].items():
            src15_res[a.lower()] = open(os.path.join(FX, 'texres15', fg + '.res'), 'rb').read()
        for imp, o, key in blobs:
            ok = os.path.exists(o) and open(o, 'rb').read() == open(imp, 'rb').read()
            row = {'asset': key, 'kind': 'res' if imp.endswith('.framedres') else 'chunk', 'bytes_identical_to_import': ok,
                   'bytes': os.path.getsize(o) if os.path.exists(o) else None}
            if imp.endswith('.framedres') and os.path.exists(o):
                hdr15 = [v for k, v in src15_res.items() if k == key.lower()]      # res name = asset name, lowercased
                row['header_32_identical_to_2015'] = bool(hdr15) and open(o, 'rb').read()[16:48] == hdr15[0]
                ok = ok and row['header_32_identical_to_2015']
            tot['blob_ok' if ok else 'blob_differ'] += 1
            rows.append(row)
    res = {'project': proj, 'totals': dict(tot),
           'fidelity_pct': 100.0 * tot['equal'] / max(1, tot['equal'] + tot['differ']), 'assets': rows}
    json.dump(res, open(report, 'w'), indent=1)
    print(json.dumps(res['totals']), 'fidelity %.3f%%' % res['fidelity_pct'])
    for row in rows:
        if (row.get('readback') == 'MISSING' or row.get('package_vs_readback_differ') or row.get('fields_2015_differ')
                or row.get('bytes_identical_to_import') is False or row.get('header_32_identical_to_2015') is False):
            print('  ', row['asset'], row.get('readback', ''), row.get('package_diffs', [])[:3], row.get('differ', [])[:5])


if __name__ == '__main__':
    main()
