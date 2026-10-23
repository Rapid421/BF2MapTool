"""Convert 2015 (swbf1) effect/emitter graph packages into destination-ready Battlefront II (swbf2) packages.

Input : pkg15/<asset>.json   (ebx graph-export --game swbf1)
Output: pkg_out/<n>.json     (build with: ebx graph-import <pkg> --as <dest> --bundle <b> --strict --allow-unsealed)

Every rule below is derived from shipped data, never tuned by eye (REPORT.md "Conversion rules" cites the evidence):
  * fields both SDKs have are copied verbatim;
  * fields only BFII has get the value DICE's cooker writes when the source does not set it (the mode over all 7,134
    shipped BFII emitters, inv/bf2_onlyfield_stats.json) or a documented mapping of the 2015 field that replaced it;
  * fields only 2015 has are dropped from an explicit list, each with an assertion that the 2015 value is the inert one
    or with its documented mapping -- anything else stops the conversion;
  * processors BFII does not have (UpdateOrientationData, UpdateLinearVelocityData) are spliced out of the chain, as
    DICE's own ports of the same emitters do; UpdateClipScaleData is re-generated at the chain end for the texture the
    emitter samples, with the Lookup DICE's cooker computed for that texture (it depends on the texture alone:
    124/124 textures map to one Lookup across 6,524 shipped processors).
"""
import json, os, sys, glob, collections, copy
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from canon import canonical

SCHEMA1 = json.load(open('inv/schema_bf1.json'))['types']
SCHEMA2 = json.load(open('inv/schema_bf2.json'))['types']
CLIP = json.load(open('inv/bf2_clipscale_by_texture.json'))       # BFII texture file guid -> Lookup (DICE-cooked)
NS = 'AddedContent/Hoth2015/FX/'                                  # destination namespace for every ported asset
_TS = os.path.join('tex_import', 'texture_steps.json')
IMPORTED_TEX = json.load(open(_TS))['guid_to_asset'] if os.path.exists(_TS) else {}
IMPORTED_CLIP = json.load(open(_TS)).get('clip_by_guid', {}) if os.path.exists(_TS) else {}

# 2015 texture layouts vs BFII (inv/pkg15_tex vs pkg2_tex): same file guid, same underlying texture guid except these,
# which DICE rebuilt (different texture guid / atlas grid) -- see REPORT.md.
TEX15 = {json.load(open(f))['FileGuid'].lower(): json.load(open(f)) for f in glob.glob('pkg15_tex/*.json')}
TEX2 = {json.load(open(f))['FileGuid'].lower(): json.load(open(f)) for f in glob.glob('pkg2_tex/*.json')}

issues, notes = [], collections.Counter()


def fail(msg):
    issues.append(msg)


def V(type_, **fields):
    return {'type': type_, 'fields': fields}


def qs(tname, fields):
    f = dict(fields)
    f['Cinematic'] = f['Ultra']            # 438/480 shipped QualityScalable* in DICE's ports of these assets: Cinematic == Ultra
    return {'type': tname, 'fields': dict(sorted(f.items()))}


# ------------------------------------------------------------------ BFII-only field values
def bf2_only_default(t, k, rec, ctx):
    if k == 'SchematicsEnable':
        return False
    table = {
        ('RandomEvaluatorData', 'Mirror'): False, ('RandomXYZEvaluatorData', 'Mirror'): False,
        ('PreRollData', 'SkipSimulatePostPreRoll'): False,
        ('EmitterData', 'EmitterGraphAssets'): [],
        ('UpdateVertexAnimData', 'AnimationTexture'): {'null': True},
        ('EmitterTemplateData', 'AlphaCullThreshold'): 0.005,
        ('EmitterTemplateData', 'EnablePyroShader'): False,
        ('EmitterTemplateData', 'Instanced'): False,
        ('EmitterTemplateData', 'LightMultiplierDynamic'): -1.0,
        ('EmitterTemplateData', 'SkipSimulationDistance'): -1.0,
        ('EmitterTemplateData', 'TweakInheritedEmitter'): {'null': True},
        ('EmitterTemplateData', 'UseRightTextureTile'): False,
        ('UpdateAgeData', 'DeathEffect'): {'null': True},
        ('UpdateAgeData', 'Throttle'): 1.0,
        ('UpdateAgeData', 'ThrottleEnvelope'): V('Vec4', w=1.0, x=0.0, y=0.0, z=0.0),
        ('UpdateAgeData', 'ThrottleFarDistance'): 0.0,
        ('UpdateTextureCoordsData', 'DirectTextureFrameLookup'): False,
        ('UpdateTextureCoordsData', 'DisableClipScaleOptimization'): False,
        ('UpdateTextureCoordsData', 'EnableFrameBlending'): True,
        ('UpdateTextureCoordsData', 'InputStartMax'): 0.0,
        ('UpdateTextureCoordsData', 'InputStartMin'): 0.0,
        ('UpdateCollisionData', 'CheckCharacter'): True, ('UpdateCollisionData', 'CheckGroup'): True,
        ('UpdateCollisionData', 'CheckPhantoms'): True, ('UpdateCollisionData', 'CheckRagdoll'): True,
        ('UpdateCollisionData', 'CheckSimpleShape'): True, ('UpdateCollisionData', 'CheckTerrain'): True,
        ('UpdateCollisionData', 'CheckWater'): True,
        ('UpdateCollisionData', 'CollisionType'): 'EmitterCollisionType_Bounce',
        ('UpdateCollisionData', 'DeathEffectOrientation'): 'EmitterCollisionEffectOrientation_BounceDirection',
        ('UpdateCollisionData', 'MaterialPair'): V('MaterialDecl', Packed=2048),
        ('UpdateCollisionData', 'RepelFactor'): 0.0, ('UpdateCollisionData', 'RepelHeight'): 0.0,
        ('UpdateCollisionData', 'SnapRange'): 0.0, ('UpdateCollisionData', 'SnapVelocity'): 'EmitterSnapVelocityType_Disabled',
        ('UpdateCollisionData', 'Throttle'): 0.0,
        ('UpdateCollisionData', 'ThrottleEnvelope'): V('Vec4', w=1.0, x=0.0, y=0.0, z=0.0),
        ('UpdateCollisionData', 'ThrottleFarDistance'): 0.0,
        ('EffectBlueprint', 'IsSimple'): True, ('EffectBlueprint', 'Objects'): [], ('EffectBlueprint', 'Schematics'): {'null': True},
        ('EffectEntityData', 'KeepAlive'): False, ('EffectEntityData', 'KillOnMaxCount'): True,
        ('EmitterEntityData', 'AutoStart'): True, ('EmitterEntityData', 'KeepAlive'): False,
        ('EmitterEntityData', 'LightProbeSampleMethod'): 'LightProbeSampleMethod_BoundingBoxCenter',
        ('EmitterEntityData', 'LightProbeSampleOffset'): V('Vec3', x=0.0, y=0.0, z=0.0),
        ('EmitterEntityData', 'LightProbeSampleOffsetMethod'): 'LightProbeSampleOffsetMethod_World',
        ('EmitterEntityData', 'PropertyIdLookupTable'): [],
        ('EmitterEntityData', 'SpawnProbabilityRandomType'): 'SpawnProbabilityRandomType_PerEmitter',
        ('EmitterEntityData', 'SpawnProbabilityRangeMin'): qs('QualityScalableFloat', dict(High=0.0, Low=0.0, Medium=0.0, Ultra=0.0)),
        ('SoundEffectEntityData', 'KeepAlive'): False, ('VisualEnvironmentEffectEntityData', 'KeepAlive'): False,
        ('LightEffectEntityData', 'IntensityMultiplier'): 1.0, ('LightEffectEntityData', 'KeepAlive'): False,
        ('LightEffectEntityData', 'RandomIntensityFreq'): 0.0, ('LightEffectEntityData', 'TubeWidth'): 1.0,
        ('SurfaceShaderInstanceDataStruct', 'ConditionalParameters'): [],
        ('SurfaceShaderInstanceDataStruct', 'SurfaceShaderName'): '',
    }
    if (t, k) in table:
        return copy.deepcopy(table[(t, k)])
    if t == 'EmitterTemplateData' and k == 'ObjectVariationNameHash':
        return ctx.get('variation_hash', 0)
    if t == 'EmitterEntityData' and k == 'SpawnOutsideViewRadius':
        return ctx['spawn_outside_view_radius'](rec)
    if t.startswith('QualityScalable') and k == 'Cinematic':
        return None   # handled by qs()
    raise KeyError('%s.%s' % (t, k))


# ------------------------------------------------------------------ 2015-only fields: drop, with the value checked
def check_drop(t, k, v, path):
    inert = {
        ('EmitterTemplateData', 'AnimationFrameColumnCount'): 1, ('EmitterTemplateData', 'AnimationFrameCount'): 1,
        ('EmitterTemplateData', 'BackLightPower'): 0.0, ('EmitterTemplateData', 'GnomonLightBackLight'): 0.0,
        ('EmitterTemplateData', 'VertexBackLight'): 0.0,
        ('EmitterTemplateData', 'MeshCullingFromEmitterCenter'): False, ('EmitterTemplateData', 'MeshCullingNearDistance'): 0.0,
        ('UpdateMeshEmitterSourceData', 'SendMeshNormalToGlobalNormal'): False,
        ('EffectEntityData', 'MaxInstanceCountTooCrowdedRadius'): 0, ('EffectEntityData', 'ResetInstanceWhenStarted'): True,
    }
    mapped = {('EmitterTemplateData', 'EmitterMaxSpawnDistance'), ('EmitterTemplateData', 'ObjectVariation'),
              ('EmitterTemplateData', 'Shader'), ('EmitterTemplateData', 'SkipSpawnOutsideView'),
              ('UpdateTransparencyData', 'CullThreshold'), ('EffectEntityData', 'KillWhenDistanceCulled'),
              ('EffectEntityData', 'TooCrowdedRadius')}
    per_component = {'CullDistance': 0.0, 'MaxActiveInstanceCount': 30, 'ResetInstanceWhenStarted': True, 'KillWhenDistanceCulled': False}
    if (t, k) in inert:
        if v != inert[(t, k)]:
            fail('%s: 2015-only %s.%s = %r is not the inert value %r' % (path, t, k, v, inert[(t, k)]))
        notes['dropped inert %s.%s' % (t, k)] += 1
        return
    if (t, k) in mapped:
        notes['dropped (mapped) %s.%s' % (t, k)] += 1
        return
    if t in ('EmitterEntityData', 'LightEffectEntityData', 'SoundEffectEntityData', 'VisualEnvironmentEffectEntityData') and k in per_component:
        want = per_component[k]
        got = v
        if isinstance(v, dict) and v.get('type', '').startswith('QualityScalable'):
            vals = set(v['fields'].values())
            got = vals.pop() if len(vals) == 1 else v
        if got != want:
            fail('%s: 2015-only %s.%s = %r is not the inert value %r' % (path, t, k, v, want))
        notes['dropped inert %s.%s' % (t, k)] += 1
        return
    if t == 'EffectReferenceObjectData' and k in ('CastEnvmapEnable', 'CastReflectionEnable', 'CastSunShadowEnable'):
        if v is not True:
            fail('%s: %s.%s = %r' % (path, t, k, v))
        return
    fail('%s: 2015-only field %s.%s has no rule' % (path, t, k))


# ------------------------------------------------------------------ generic value conversion
# 2015 enum values BFII's SDK does not have. EmittableAlignment_MotionStretch: BFII keeps 2015's MotionStretchScreen
# (screen-space, 'legacy' stretch) and adds ScreenMotionStretch / DirectionMotionStretch; the 2015 value without
# 'Screen' is the direction-aligned stretch. INFERRED from the names (no DICE port of a MotionStretch emitter exists
# to prove it) -- used only by the two camera-proximity snowfall emitters; see REPORT.md.
ENUM_MAP = {'EmittableAlignment_MotionStretch': 'EmittableAlignment_DirectionMotionStretch'}


def conv_value(v, path, ctx):
    if isinstance(v, str) and v in ENUM_MAP:
        notes['enum %s -> %s (inferred)' % (v, ENUM_MAP[v])] += 1
        return ENUM_MAP[v]
    if isinstance(v, list):
        return [conv_value(x, '%s[%d]' % (path, i), ctx) for i, x in enumerate(v)]
    if isinstance(v, dict):
        if 'ref' in v and len(v) == 1:
            return {'ref': ctx['remap'][v['ref']]}
        if 'null' in v:
            return {'null': True}
        if 'external' in v:
            return {'external': ctx['map_external'](v['external'], path)}
        if 'resource' in v:
            fail('%s: resource refs are not ported by fx_convert' % path)
            return v
        if 'type' in v and 'fields' in v:
            return conv_struct(v['type'], v['fields'], path, ctx)
    return v


def conv_struct(t, fields, path, ctx, is_container=False):
    s1 = (SCHEMA1.get(t) or {}).get('fields') or {}
    s2 = (SCHEMA2.get(t) or {}).get('fields')
    if s2 is None:
        fail('%s: BFII has no type %s' % (path, t))
        return {'type': t, 'fields': fields}
    out = {}
    for k, v in fields.items():
        if k in s2:
            out[k] = conv_value(v, path + '.' + k, ctx)
        else:
            check_drop(t, k, v, path)
    for k in s2:
        if k not in out:
            if t.startswith('QualityScalable') and k == 'Cinematic':
                out[k] = out['Ultra']
                continue
            try:
                out[k] = bf2_only_default(t, k, fields, ctx)
            except KeyError:
                fail('%s: no BFII default for %s.%s' % (path, t, k))
    return {'type': t, 'fields': dict(sorted(out.items()))}


# ------------------------------------------------------------------ emitters
def chain_indices(objs, tmpl_index):
    out, seen = [], set()
    p = objs[tmpl_index]['fields'].get('RootProcessor')
    while isinstance(p, dict) and 'ref' in p and p['ref'] not in seen:
        seen.add(p['ref']); out.append(p['ref'])
        p = objs[p['ref']]['fields'].get('NextProcessor')
    return out


REMOVE_PROCS = {'UpdateOrientationData', 'UpdateLinearVelocityData', 'UpdateClipScaleData'}


def convert_emitter(pkg, dest, extmap):
    objs = copy.deepcopy(pkg['Objects'])
    root = objs[0]
    tiers = ('TemplateDataLow', 'TemplateDataMedium', 'TemplateDataHigh', 'TemplateDataUltra')
    tmpls = []
    for t in tiers:
        r = root['fields'].get(t)
        if isinstance(r, dict) and 'ref' in r and r['ref'] not in tmpls:
            tmpls.append(r['ref'])
    removed = set()
    clip_needed = {}
    for ti in tmpls:
        chain = chain_indices(objs, ti)
        keep = [i for i in chain if objs[i]['type'] not in REMOVE_PROCS]
        removed.update(i for i in chain if objs[i]['type'] in REMOVE_PROCS)
        # relink the kept chain
        tf = objs[ti]['fields']
        tf['RootProcessor'] = {'ref': keep[0]} if keep else {'null': True}
        for a, b in zip(keep, keep[1:]):
            objs[a]['fields']['NextProcessor'] = {'ref': b}
        if keep:
            objs[keep[-1]]['fields']['NextProcessor'] = {'null': True}
        # clip scale: only for textured quad-like emitters without a custom shader (EmitterPipeline::addDefaultTextureProcessor)
        tc = [i for i in keep if objs[i]['type'] == 'UpdateTextureCoordsData']
        shader = (tf.get('Shader') or {}).get('fields', {}).get('Shader') or {'null': True}
        if tf.get('EmittableType') in ('EmittableType_Quad', 'EmittableType_Ribbon', 'EmittableType_Beam', 'EmittableType_Decal') \
                and tc and shader.get('null'):
            clip_needed[ti] = (keep[-1], tc[0])
        elif tf.get('EmittableType') in ('EmittableType_Quad', 'EmittableType_Ribbon', 'EmittableType_Beam', 'EmittableType_Decal') and not tc:
            fail('%s: quad emitter template %d without UpdateTextureCoordsData' % (dest, ti))
    # object table without removed processors, plus one appended UpdateClipScaleData per template that needs it
    order = [i for i in range(len(objs)) if i not in removed]
    remap = {old: new for new, old in enumerate(order)}
    new_objs = [objs[i] for i in order]
    for ti, (last, tci) in clip_needed.items():
        bt = objs[tci]['fields'].get('BaseTexture')
        g = (bt.get('external') or {}).get('file_guid', '').lower() if isinstance(bt, dict) else ''
        lookup = IMPORTED_CLIP.get(g) or CLIP.get(g)      # imported 2015 texels: lookup computed from them
        if lookup is None:
            frames = int(((TEX2.get(g) or TEX15.get(g) or {}).get('Objects') or [{}])[0].get('fields', {}).get('AnimationFrameCount') or 1)
            lookup = [32767] * (4 * frames)
            notes['clip scale: no DICE lookup for texture, full-quad (visually lossless) %s' % g] += 1
        clip = {'type': 'UpdateClipScaleData', 'exported': False, 'fields': {
            'EvaluatorInput': 'EfNormTime', 'EvaluatorInputParam': {'null': True}, 'Lookup': lookup,
            'NextProcessor': {'null': True}, 'Pre': {'null': True}, 'SchematicsEnable': False}}
        new_objs.append(clip)
        remap_clip = len(new_objs) - 1
        objs[last]['fields']['NextProcessor'] = {'ref': ('CLIP', remap_clip)}
    # rewrite refs: old index -> new index; the appended clip refs are already final
    def fix(v):
        if isinstance(v, list):
            return [fix(x) for x in v]
        if isinstance(v, dict):
            if 'ref' in v and len(v) == 1:
                r = v['ref']
                if isinstance(r, tuple):
                    return {'ref': r[1]}
                if r in removed:
                    raise ValueError('reference to a removed processor')
                return {'ref': remap[r]}
            return {k: fix(x) for k, x in v.items()}
        return v
    new_objs = [dict(o, fields=fix(o['fields'])) for o in new_objs]
    ident = {i: i for i in range(len(new_objs))}
    # per-template 2015-only field mappings
    out = []
    for i, o in enumerate(new_objs):
        t = o['type']
        f = o['fields']
        ctx = {'remap': ident, 'map_external': lambda e, p: extmap(e, p)}
        if t == 'EmitterTemplateData':
            if f.get('EmitterMaxSpawnDistance') not in (0, 0.0, f.get('MaxSpawnDistance')):
                fail('%s: EmitterMaxSpawnDistance %r differs from MaxSpawnDistance %r' % (dest, f.get('EmitterMaxSpawnDistance'), f.get('MaxSpawnDistance')))
            ov = f.get('ObjectVariation')
            if isinstance(ov, dict) and 'external' in ov:
                ctx['variation_hash'] = hash_quick_lower((ov['external'].get('asset') or '').split('/')[-1])
                fail('%s: ObjectVariation on an emitter (hash of the variation NAME needed) -- not handled' % dest)
            sh = (f.get('Shader') or {}).get('fields', {}).get('Shader')
            if isinstance(sh, dict) and not sh.get('null'):
                fail('%s: custom surface shader %s needs a CustomShaderData processor -- not handled' % (dest, sh))
        if t == 'UpdateTextureCoordsData':
            nt = f.get('NormalTexture')
            if isinstance(nt, dict) and 'external' in nt:
                a = (nt['external'].get('asset') or '')
                if a == 'Systems/Emitter/DefaultNormalTexture':
                    f['NormalTexture'] = {'null': True}          # DICE's ports: 0 of 78 keep the default normal texture
                    notes['NormalTexture DefaultNormalTexture -> null (DICE rule)'] += 1
        rec = {'type': t, 'exported': o.get('exported', False)}
        if o.get('guid'):
            rec['guid'] = o['guid']
        conv = conv_struct(t, f, '%s:objects[%d]' % (dest, i), ctx, is_container=True)
        rec['fields'] = conv['fields']
        out.append(rec)
    out[0]['fields']['Name'] = dest
    return mk_pkg(dest, 'ScalableEmitterDocument', out, pkg)


def hash_quick_lower(s):
    h = 5381
    for c in s.lower().encode():
        h = ((h * 33) ^ c) & 0xffffffff
    return h


def mk_pkg(dest, root_type, objects, src):
    return canonical({'Version': 1, 'Kind': 'ebx-graph', 'Game': 'swbf2', 'Asset': dest, 'FileGuid': None,
            'RootType': root_type, 'Sha256': None,
            'Source': {'game': src['Game'], 'asset': src['Asset'], 'file_guid': src['FileGuid'], 'sha256': src['Sha256'],
                       'converter': 'fx_convert.py'},
            'Objects': objects})


# ------------------------------------------------------------------ blueprints
def convert_blueprint(pkg, dest, extmap, emitter_skip_outside, max_active):
    objs = copy.deepcopy(pkg['Objects'])
    out = []
    ident = {i: i for i in range(len(objs))}
    for i, o in enumerate(objs):
        t = o['type']
        f = o['fields']
        ctx = {'remap': ident, 'map_external': lambda e, p: extmap(e, p)}

        def radius(rec):
            em = (rec.get('Emitter') or {}).get('external') or {}
            skip = emitter_skip_outside.get((em.get('file_guid') or '').lower())
            if skip is None:
                fail('%s: no SkipSpawnOutsideView known for emitter %s' % (dest, em.get('asset')))
                return 1.5
            # 2015 SkipSpawnOutsideView=false spawned regardless of the view; BFII: -1 = ignore frustum culling
            # (EmitterStateStreamImpl: "A value of -1 means ignore frustum culling"); true -> BFII's default 1.5
            return 1.5 if skip else -1.0
        ctx['spawn_outside_view_radius'] = radius
        if t == 'EffectEntityData':
            kill = f.get('KillWhenDistanceCulled')
            if kill and max_active:
                m = f['MaxActiveInstanceCount']['fields']
                for q in ('Low', 'Medium', 'High', 'Ultra'):
                    m[q] = max(m[q], max_active)
                notes['MaxActiveInstanceCount raised to placement count (2015 KillWhenDistanceCulled)'] += 1
        rec = {'type': t, 'exported': o.get('exported', False)}
        if o.get('guid'):
            rec['guid'] = o['guid']
        rec['fields'] = conv_struct(t, f, '%s:objects[%d]' % (dest, i), ctx, is_container=True)['fields']
        out.append(rec)
    out[0]['fields']['Name'] = dest
    return mk_pkg(dest, 'EffectBlueprint', out, pkg)


def main():
    plan = json.load(open(sys.argv[1]))        # {"emitters": [2015 path...], "blueprints": [...], "placements": {bp: n}}
    outdir = sys.argv[2]
    os.makedirs(outdir, exist_ok=True)
    src = {}
    for f in glob.glob('pkg15/*.json'):
        p = json.load(open(f))
        src[p['Asset'].lower()] = p
    em_dest = {e.lower(): NS + e for e in plan['emitters']}
    em_guid_dest = {src[e.lower()]['FileGuid'].lower(): NS + e for e in plan['emitters']}
    bf2_where = {}
    for line in open('inv/emdeps_where_bf2.jsonl'):
        x = json.loads(line)
        if x.get('path'):
            bf2_where[x['q'].lower()] = x
    for line in open('inv/sound_where_bf2.jsonl'):
        x = json.loads(line)
        if x.get('path'):
            bf2_where[x['q'].lower()] = x

    def extmap(e, path):
        g = (e.get('file_guid') or '').lower()
        if e.get('type') == 'ScalableEmitterDocument' or g in em_guid_dest:
            if g not in em_guid_dest:
                fail('%s: child/used emitter %s is not in the plan' % (path, e.get('asset')))
                return e
            return {'file_guid': None, 'class_guid': None, 'asset': em_guid_dest[g], 'type': 'ScalableEmitterDocument'}
        if g in IMPORTED_TEX:
            # 2015 atlas imported with its own pixels under its 2015 name (fx_textures.py)
            return {'file_guid': None, 'class_guid': None, 'asset': IMPORTED_TEX[g], 'type': e.get('type')}
        w = bf2_where.get(g)
        if w is None:
            fail('%s: dependency %s (%s) is not in BFII by guid' % (path, e.get('asset'), g))
            return e
        # resolve by file guid and by the 2015 instance guid (DICE kept instance guids in the assets it carried over;
        # the global effect parameter list is referenced per EffectParameter object, not by its root)
        return {'file_guid': g, 'class_guid': e.get('class_guid'), 'asset': w['path'], 'type': e.get('type')}

    skip_outside = {}
    written = []
    for e in plan['emitters']:
        p = src.get(e.lower())
        if p is None:
            fail('no 2015 package for emitter %s' % e); continue
        hi = [o for o in p['Objects'] if o['type'] == 'EmitterTemplateData']
        vals = {o['fields'].get('SkipSpawnOutsideView') for o in hi}
        if len(vals) != 1:
            notes['emitter tiers disagree on SkipSpawnOutsideView -> High tier used'] += 1
        hi_ref = p['Objects'][0]['fields'].get('TemplateDataHigh', {}).get('ref')
        skip_outside[p['FileGuid'].lower()] = p['Objects'][hi_ref]['fields'].get('SkipSpawnOutsideView') if hi_ref is not None else False
        dest = em_dest[e.lower()]
        out = convert_emitter(p, dest, extmap)
        fn = os.path.join(outdir, 'em__' + e.lower().replace('/', '__') + '.json')
        json.dump(out, open(fn, 'w'), indent=1)
        written.append(('emitter', dest, fn, p))
    for b in plan['blueprints']:
        p = src.get(b.lower())
        if p is None:
            fail('no 2015 package for blueprint %s' % b); continue
        dest = NS + b
        out = convert_blueprint(p, dest, extmap, skip_outside, plan['placements'].get(b, 0))
        fn = os.path.join(outdir, 'bp__' + b.lower().replace('/', '__') + '.json')
        json.dump(out, open(fn, 'w'), indent=1)
        written.append(('blueprint', dest, fn, p))
    # emitter import order: children before parents (EmitterData refs)
    deps = {}
    for kind, dest, fn, p in written:
        if kind != 'emitter':
            continue
        s = json.dumps(json.load(open(fn))['Objects'])
        deps[dest] = {d for d in em_dest.values() if d != dest and ('"asset": "%s"' % d) in s}
    order, done = [], set()
    def visit(d, stack=()):
        if d in done:
            return
        if d in stack:
            fail('emitter reference cycle at %s' % d); return
        for x in sorted(deps.get(d, ())):
            visit(x, stack + (d,))
        done.add(d); order.append(d)
    for d in sorted(deps):
        visit(d)
    files = {dest: fn for kind, dest, fn, p in written}
    manifest = {'emitters': [(d, files[d]) for d in order],
                'blueprints': [(dest, fn) for kind, dest, fn, p in written if kind == 'blueprint'],
                'issues': issues, 'notes': dict(notes)}
    json.dump(manifest, open(os.path.join(outdir, 'manifest.json'), 'w'), indent=1)
    print('emitters %d, blueprints %d, issues %d' % (len(order), len(manifest['blueprints']), len(issues)))
    for i in issues[:60]:
        print('  ISSUE', i)
    for k, v in sorted(notes.items()):
        print('  note %5d %s' % (v, k))


if __name__ == '__main__':
    main()
