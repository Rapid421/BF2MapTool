"""Host shell Kamino_01 (Clone Wars): the measured facts of this host, from the Mygeeto, Polis Massa and Mustafar ports.

Nothing here names a user's map. GUIDs are those of Kamino's own objects, which `level create` keeps in the clone.
"""
LEVEL = 'Levels/MP/Kamino_01/Kamino_01'
DIR = 'Levels/MP/Kamino_01'
ERA = 'clone_wars'
MODES = 'Blast,HeroesVersusVillains'

# Where a map sits: its play centre over the centre of the host's Skirmish play space (x -86, z 47), floors near
# y 408 - clear of the host's old city volume and its leftover light probes (pieces placed at the host's own height
# sample those probes and go uniformly dark), and under the host's weapon ceiling.
PLAY_X, PLAY_Z, FLOOR_Y = -86.0, 47.0, 408.0
WORLD_SIZE_XZ = 8192.0          # the root's own 4096 / 1024 box is too small for a map placed high
WORLD_SIZE_Y = 8192.0
MAX_VEHICLE_HEIGHT = 1000.0     # above it shots pass through soldiers and static collision
MAX_EXTENT = 3500.0             # metres from the play centre the world box leaves room for


# ------------------------------------------------------------------ material templates
# One single-material shipped mesh per material class (a multi-material template crashes the client once drawn from
# afar). `slots` = (shader parameter, packed layout, constant blue, shipped texture whose format the import copies).
# `home_db` = the level whose DX11 shader database carries EVERY vertex-declaration permutation of the surface: 'host'
# when this host compiles it itself, else the level the template mesh ships in - its surface is merged from THERE into
# the map's own database (a surface taken from another database had programs for 2 of 4 declarations and drew nothing).
_NAM_BOX = 'Objects/Props/ObjectSets/_RebelAlliance/Box_M_02/T_Box_M_02_NAM'
_KAM_TEX = 'Objects/Architecture/Kamino/TexturesGeneric/T_KAM_InteriorDetails_01'
# The *_TextureArray_* presets blend a detail layer (normal + colour) from a shared texture array into the surface; the
# packed NAM / NW blue channel picks the slice. The template meshes set it up for THEIR use: the Tatooine carpet with
# GlobalDetailStrength 2.0 (seen by the user 2026-10-23: every cut-out wore a cloth weave). A ported material is
# exactly what Blender shows, so the detail layer is switched off on every template that has one.
NO_DETAIL = {'GlobalDetailStrength': '0,0,0,0'}
TEMPLATES = {
    # colour and normal uv unclamped; TexCoord1 + RadiosityTexCoord (lightmapped); seen in game on this host
    'opaque': {
        'mesh': 'objects/architecture/sullust/factory/factory_sullust_turbolaser_platform_01/factory_sullust_turbolaser_platform_01_mesh',
        'shader': 'Shaders/Presets/SS_PropsMetallicPreset_1UV_TextureArray_01', 'guid': 'f8f1ed9b-4ceb-453e-bb4f-7110ef5ba8f3',
        'slots': [('_CS', 'CS', 0, 'Objects/Architecture/_GalacticEmpire/BunkerSystem_01/T_BS_01_Entrance_B_CS'),
                  ('_NAM_texcoord0', 'NAM', 25, _NAM_BOX)],
        'deps': [], 'home_db': 'host', 'lightmapped': True, 'emissive_param': None, 'params': dict(NO_DETAIL)},
    # CANDIDATE for 'opaque' (2026-10-12): the 2UV array preset. In the host's shader database its permutation set
    # reaches only wrap / wrap anisotropic samplers, while the 1UV preset above also reaches a wrap-U / CLAMP-V one -
    # and in game the 1UV preset's normal map shows no relief where v leaves 0..1. Same vertex layout as the 1UV
    # template mesh (TexCoord0, TexCoord1, RadiosityTexCoord). Used by the Mygeeto / Polis Massa ports on this host
    # with flat normal maps. Reached only through a material's 'template' override until seen in game.
    'opaque2': {
        'mesh': 'objects/props/objectsets/_rebelalliance/box_m_02/box_m_02_mesh',
        'shader': 'Shaders/Presets/SS_PropsMetallicPreset_2UV_TextureArray_01', 'guid': '943997d0-0838-48ac-acfe-09d7bf578581',
        'slots': [('_CS', 'CS', 0, 'Objects/Architecture/_GalacticEmpire/BunkerSystem_01/T_BS_01_Entrance_B_CS'),
                  ('_NAM_texcoord0', 'NAM', 51, _NAM_BOX)],
        'deps': [], 'home_db': 'host', 'lightmapped': True, 'emissive_param': None, 'params': dict(NO_DETAIL)},
    # the host's own light-strip surface; seen in game on this host
    'emissive': {
        'mesh': 'objects/architecture/kamino/corridorslarge/o_kam_emissivestrip_s512_01_mesh',
        'shader': 'Objects/Architecture/Kamino/Shaders/SS_InteriorDetails_01', 'guid': 'b44c9098-52ab-4d3f-b672-317463722a9a',
        'slots': [('_CS', 'CS', 0, _KAM_TEX + '_CS'), ('_NAM_texcoord0', 'NAM', 19, _KAM_TEX + '_NAM'), ('_E', 'E', 0, _KAM_TEX + '_E')],
        'deps': [], 'home_db': 'host', 'lightmapped': False, 'emissive_param': '_EmissiveMultiplier'},
    # alpha test on the NW texture's alpha; the surface is not in this host's database
    'cutout': {
        'mesh': 'objects/props/objectsets/tatooine/carpet_01/tat_carpetlarge_01_mesh',
        'shader': 'Shaders/Presets/SS_PropsNonMetallicPreset_AlphaTest_TextureArray_01', 'guid': 'f7c6feee-e63b-455a-8189-2747671b4e7e',
        'slots': [('CS', 'CS', 0, 'Objects/Props/ObjectSets/Tatooine/moseisley_carpets/T_MosEisley_Carpets_01_CS'),
                  ('NW', 'NW', 127, 'Objects/Props/ObjectSets/Takodana/carpet_01/T_Carpet_01_NAM')],
        'deps': ['Objects/Props/ObjectSets/Tatooine/moseisley_carpets/T_MosEisley_Carpets_01_CS',
                 'Objects/Props/ObjectSets/Takodana/carpet_01/T_Carpet_01_NAM'],
        'home_db': 'Levels/MP/Tatooine_01/Tatooine_01', 'lightmapped': False, 'emissive_param': None, 'params': dict(NO_DETAIL)},
    # the host's own window glass: a single-material shipped mesh on the host level's glass surface. It takes no texture
    # and no parameter from the material (the shipped mesh sets none either): every glass pane looks like this host's
    # windows. See-through, solid, not part of the light bake.
    'glass': {
        'mesh': 'objects/architecture/kamino/cloningfacility/o_kam_wallchamber_windowglass_01_mesh',
        'shader': 'Levels/MP/Kamino_01/Shaders/SS_KaminoGlass_01', 'guid': 'f068bf40-4621-4630-bcdd-bf718876173c',
        'slots': [], 'deps': [], 'home_db': 'host', 'lightmapped': False, 'emissive_param': None},
    # Animated hologram / neon / scrolling screen: the Vardos ticker (single material) on FX/Shaders/Ambient/Holo/
    # SS_Holo_Ticker_01, additive, unlit. The surface ships in the SP root level's database; merged from there and made
    # TEXTURE-COLOURED in the map's own database (vendor/rvm/holo_texcolor.py, `patch`): screen colour = HC rgb x K.
    # The shader adds its own scan lines and noise flicker; UV_Speed scrolls the texture (u, v per second).
    # Seen in game on the Polis Massa (PM16/PM18) and Mygeeto (M27) ports with this exact recipe.
    'holo': {
        'mesh': 'objects/architecture/vardos/vfx/vardos_ticker_circle_01_mesh',
        'shader': 'FX/Shaders/Ambient/Holo/SS_Holo_Ticker_01', 'guid': '80f49958-47fc-46b3-971c-e5d1e99953a2',
        'slots': [('Texture', 'HC', 0, 'FX/Cinematics/Textures/Ticker/T_Portrait_Ticker_03')],
        'deps': ['FX/Cinematics/Textures/Ticker/T_Portrait_Ticker_03', 'Shaders/Presets/Walrus/Textures/Scanlines/T_Scanlines_02_C',
                 'FX/Textures/Util/T_TilingNoises_02_D'],
        'home_db': 'Levels/SP/RootLevel/RootLevel', 'lightmapped': False, 'emissive_param': None, 'patch': 'holo_texcolor',
        'params': {'UV_Coord': '1,1,0,0', 'YGradient_Mult': '0,0,0,0'}, 'speed_param': 'UV_Speed'},
}
# Emissive cut-out (PM-3, 2026-10-23): Jabba's Palace lit hallway window, ONE material on the metallic emissive alpha-test
# preset; it ships only in JabbasPalace_01 (bundle ls), whose database carries the surface (offline merge with the
# Tatooine cut-out: VERIFY PASS). Its textures are T_TRL_Hallway_01_Window_01_CS / _NW: CS alpha holds the slits
# (the probable alpha-test mask), NW = normal + sparse blue + grunge alpha; the glow = EmissiveColor x
# EmissiveIntensity. Which channel cuts and which glows is not known yet: the PROBE template packs a different stripe
# direction into each candidate channel (texpack XCS / XNW) so one game shot tells. Reached only through a
# material's 'template' override until that shot is read.
_JABBA_WIN = 'Objects/Architecture/Tatooine/JabbasPalace/_ThroneRoom/T_TRL_Hallway_01_Window_01'
TEMPLATES['cutout_emissive_probe'] = {
    'mesh': 'objects/architecture/tatooine/jabbaspalace/_throneroom/trl_hallway_01_window_01_lit_mesh',
    'shader': 'Shaders/Presets/SS_PropsMetallicPreset_01_EmissiveAlphaTest', 'guid': 'cd810e5c-5e69-4130-a402-6cad5a8bc8d4',
    'slots': [('BaseColor', 'XCS', 0, _JABBA_WIN + '_CS'), ('Normal', 'XNW', 0, _JABBA_WIN + '_NW')],
    'deps': [_JABBA_WIN + '_CS', _JABBA_WIN + '_NW'],
    'home_db': 'S2_2/Levels/JabbasPalace_01/JabbasPalace_01', 'lightmapped': False, 'emissive_param': None,
    'params': {'EmissiveColor': '1,1,1,1', 'EmissiveIntensity': '5000,0,0,0'}}
# material class -> template. A double-sided cut-out is the cut-out template plus the reversed faces the split adds.
# 'lava' draws on the emissive template until the lava preset is built into the map's own shader database.
# An OPAQUE material whose normal map has relief is built on this template instead of 'opaque':
# seen in game 2026-10-12 (ToolArena review t4 .. t7, one raised dome per texture cell): the 1UV array preset shows
# the relief only where 0 <= v <= 1 (its shader database entry reaches a wrap-U / clamp-V sampler), the 2UV preset
# only inside the unit square, while this template (and the cut-out one) tiles it in every cell. Price: its template
# mesh has no RadiosityTexCoord, so such pieces cannot take a lightmap (they are lit by probes once there is a bake).
# Materials with a flat normal map stay on 'opaque' (nothing to tile).
OPAQUE_RELIEF_TEMPLATE = 'emissive'
# The opaque (1UV) surface repeats the normal map sideways but not upwards (wrap U, clamp V). With this on, the split
# cuts opaque normal-mapped geometry along the whole-number v lines and moves every part into v 0..1, so such
# materials can STAY on the lightmap-capable opaque surface; OPAQUE_RELIEF_TEMPLATE is then only the fallback for a
# material the split did not band.
OPAQUE_V_CLAMPED = True
CLASS_TEMPLATE = {'opaque': 'opaque', 'emissive': 'emissive', 'cutout': 'cutout', 'cutout2s': 'cutout', 'lava': 'emissive', 'glass': 'glass', 'holo': 'holo'}
COMMON_DEPS = ['LodGroups/NoCullingLodGroup', 'LodGroups/LodGroupGenericMedium', 'LodGroups/LodGroupGenericLarge',
               'LodGroups/LodGroupGenericSmall', 'Objects/Props/_CommonTextures/A_Generic_01']


# blueprint / physics / static-group donors of a piece (shipped assets every piece is cloned from)
BLUEPRINT_NO_PHYSICS = 'Objects/Nature/Volcanic/Sullustan/CraterLavaRiver_01/CraterLavaRiver_01'
BLUEPRINT_PHYSICS = 'Objects/Props/ObjectSets/Bespin/CryoChamber_Pipe_01/CryoChamber_MetalPipe_S64_01'
PHYSICS_TEMPLATE = 'Objects/Props/ObjectSets/_RebelAlliance/MC80/MC80_Hangar_PlatformL_01_Physics_Win32'
GROUP_TEMPLATE = 'Levels/MP/Kamino_01/Content'                  # its static group 0 is the donor of the map's group
PHYSICS_RES_TYPE = 0x91043F65
NO_CULLING_LOD = 'LodGroups/NoCullingLodGroup'
# PROVISIONAL (to be replaced by the measured rule of the lighting step): game emissive multiplier per unit of
# Blender emission strength. The Mustafar port ended at 280 by eye at EV 3.8; this host ships 15,000 on its strips.
EMISSIVE_PER_UNIT = 280.0
# Strength of a local lamp IN THE BAKE against the same lamp real-time. Measured 2026-10-12 on the light calibration
# scene (work/LightCal/review/lc2): with the same light record a baked 1000 W lamp lit its bay like a real-time lamp of
# 80 W (floor, which also carries the real-time highlight) to 147 W (back wall, where the baked bay also has bounce),
# i.e. the bake came out 7 to 12 times too dim. One factor for every lamp; a lamp's own "Brightness in the bake"
# multiplies it. INFERRED from two matches, not a clean number; where the factor comes from is not known.
BAKE_LAMP_SCALE = {'PbrSphereLightEntityData': 30.0, 'PbrSpotLightEntityData': 151.0, 'PbrRectangularLightEntityData': 39.0}
# per kind, LightMatch lm3 (direct light in near-black rooms): with 49 a baked point lamp showed at 1.4 - 1.9 of the same
# lamp real-time, a spot at 0.24, an area lamp at 1.26


def template_for(cls, override=None):
    """(template name, template) of a material class; `override` = a template named by the material itself"""
    t = override if override in TEMPLATES else CLASS_TEMPLATE.get(cls)
    return (t, TEMPLATES[t]) if t else (None, None)


# ------------------------------------------------------------------ shell (level create + clean-up)
# What `level create --skirmish-from` leaves of the host that must go, measured on three ports' deep dumps:
DROPPED_LAYERS = ('Shared_Art', 'Shared_Art_Schematic', 'LivingWorld', 'LivingWorld_Schematic', 'Default_Lighting', 'Pathfinding',
                  'Stormy_VFX')                                   # ocean, living world, underwater lighting, the host's navmesh, rain
ROOT_UNBUNDLE = ('Shared_Art', 'Shared_Art_Schematic', 'LivingWorld', 'LivingWorld_Schematic', 'Default_Lighting', 'Pathfinding')
WATER_ASSETS = ('Levels/MP/Kamino_01/Shaders/SS_Oceanwater_Kamino', 'FX/Meshes/Ambient/Waterfall/Kamino_EdgeWater',
                'FX/Meshes/Ambient/Waterfall/Yavin_Temple_watermesh_02')
CLEAR_LAYERS = ('GameModes_Shared', 'Default', 'Shared_Lighting', 'GlobalVFX', 'Stormy_VE', 'Stormy_Lights')   # host colliders, lights, VFX
CLEAR_KEEP_TYPES = ['DistantShadowCacheVolumeEntityData']
GROUP_CLEAR_LAYER = 'Stormy'                                    # its static group (host set dressing)
DOOR_LAYERS = ('TeamDeathmatch_Global', 'HeroArena_Logic')
DOOR_BLUEPRINTS = {'ea778940-64cc-4640-a37f-bbecde6ce02a',     # PF_AutomaticDoubleDoor_Medium_Kamino_01
                   'f72d39c6-3416-4936-8fea-7267fd26a102'}     # PF_AutomaticDoubleDoor_Large_Kamino_01
VE_VOLUME_LAYER = 'Lighting'
VE_VOLUME_BLUEPRINTS = {'19cca0f7-8d84-45fa-8dbe-c78f5af131eb',  # VE_PV_Kamino_Interior_01
                        'e8776b14-d1bf-11e6-b054-fee1d5f86314'}  # VE_DisableOutdoorLight_01
# The host's Setup waits for two streamed-in sub-worlds; `level create` drops one (Content), so its SyncedBool never
# sets and the level loads forever. The kept sibling sets it too.
LOAD_GATE = {'source': '0b83a2b4-6e6f-4225-9d07-85ada61e68df', 'source_field': '0x263cb49e', 'target_layer': 'Setup',
             'target': '2c37014e-9859-4bb5-8adc-7c7cbc95fe76', 'target_field': '0xa4253151'}
SPAWN_LAYERS = ('TeamDeathmatch_Global', 'HeroArena_Logic', 'HeroArena_Shapes', 'TeamDeathmatch_Skirmish_Logic',
                'TeamDeathmatch_Skirmish_DefendAreas')
BAKE_NAME = 'Kamino'                                             # name filter of the host's Enlighten data the bake replaces
# A far limit for geometry (metres from the play centre; 0 = none). NOT in force: it was added on the belief that far
# geometry crashes the level, which is not established. What is measured (2026-10-12, a user's city map):
#   - 1,162 pieces, nothing beyond 338 m, no bake: loads and plays;
#   - 1,753 pieces (591 more: a 2.2 km backdrop shell; 877 pieces without collision): access violation at
#     game+0x15AC36B about 3 s into loading, with and without the bake, registers identical every time;
#   - the same project with the 503 placements beyond 400 m taken out (their group members left, empty): same crash.
# So it is not the bake and not the far PLACEMENTS. Still open: the piece count, the 550 m meshes, the pieces without
# collision. A small arena inside a 2.2 km shell (tests/scenes/fartest.blend) was built to tell, but never loaded.
FAR_LIMIT = 0.0
LIGHT_LAYER = 'Shared_Lighting'
# a shipped reflection cube (128 x 128 RGBA16F, 6 mips) the level's own cube is made after; the volume goes in LIGHT_LAYER
REFLECTION_TEXTURE = 'Levels/MP/Kamino_01/ReflectionVolumeTexture/Shared_Lighting/5083e15d-9ea7-43b3-a2bb-1dc12f654f0b-Tex'
VE_REF_LAYERS = ('', 'HighEnd', 'Stormy')                       # '' = the level root
MODE_PARTITIONS = ('HeroArena', 'HvsV', 'TeamDeathmatch')       # layers of the host's own modes: not shared with a second level
KILL_LAYER = 'GameModes_Shared'                                 # three OBBData the host's one kill logic reads
KILL_OBBS = ('6ac9ca7e-db58-4c81-b369-8bff117b6d11', '96f7f0ba-bbf3-4beb-8956-981ddb1de624', '5bc997e9-b490-4397-a0e7-843df02f0ef8')


def create_args(job):
    a = ['level', 'create', job.level, '--modes', MODES, '--skirmish-from', LEVEL, '--menu-name', job.display,
         '--planet-name', job.planet, '--kyber', job.display]
    if job.description:
        a += ['--menu-description', job.description]
    return a


def layer_objects(dump):
    """placed objects of a deep layer dump: guid, blueprint file guid / path, type, position"""
    out = []

    def walk(o):
        if isinstance(o, dict):
            ig = o.get('__InstanceGuid')
            if isinstance(ig, dict) and 'BlueprintTransform' in o:
                bp = o.get('Blueprint') or {}
                tr = (o.get('BlueprintTransform') or {}).get('trans') or {}
                out.append({'guid': ig.get('ExportedGuid'), 'exported': ig.get('IsExported'), 'bp_file': bp.get('__fileGuid'),
                            'bp': bp.get('__path'), 'type': o.get('__type'), 'pos': [tr.get(k, 0) for k in 'xyz']})
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(dump)
    return out


def cleanup_steps(job, load_dump):
    """pipeline steps of the host clean-up; `load_dump(layer)` returns the deep dump of one layer of the new level"""
    L, B = job.level_dir, job.bundle
    s = []
    for part in ROOT_UNBUNDLE:
        s.append({'op': 'bundle-remove', 'ebx': DIR + '/' + part, 'bundle': B, 'on-error': 'continue'})
    s.append({'op': 'bundle-remove', 'ebx': DIR + '/Stormy_VFX', 'bundle': 'win32/' + L + '/Stormy', 'on-error': 'continue'})
    for a in WATER_ASSETS:
        for b in (B, 'win32/' + L + '/Stormy'):
            s.append({'op': 'bundle-remove', 'ebx': a, 'bundle': b, 'on-error': 'continue'})
    for lay in CLEAR_LAYERS:
        s.append({'op': 'level-clear', 'ebx': L + '/' + lay, 'remove': True, 'keep_types': CLEAR_KEEP_TYPES, 'on-error': 'continue'})
    s.append({'op': 'level-clear', 'ebx': L + '/' + GROUP_CLEAR_LAYER, 'delete_groups': True, 'only_types': ['StaticModelGroupEntityData'],
              'on-error': 'continue'})
    n = {'doors': 0, 've_volumes': 0}
    for lay in DOOR_LAYERS:
        for o in layer_objects(load_dump(lay)):
            if o['bp_file'] in DOOR_BLUEPRINTS and o['exported']:
                s.append({'op': 'level-remove', 'ebx': L + '/' + lay, 'instance_guid': o['guid'], 'on-error': 'continue'}); n['doors'] += 1
    for o in layer_objects(load_dump(VE_VOLUME_LAYER)):
        if o['bp_file'] in VE_VOLUME_BLUEPRINTS and o['exported']:
            s.append({'op': 'level-remove', 'ebx': L + '/' + VE_VOLUME_LAYER, 'instance_guid': o['guid'], 'on-error': 'continue'}); n['ve_volumes'] += 1
    g = LOAD_GATE
    s.append({'op': 'cli', 'args': ['connection', 'add', job.level, '--source', g['source'], '--source-field', g['source_field'],
                                    '--target-ebx', L + '/' + g['target_layer'], '--target', g['target'], '--target-field', g['target_field'],
                                    '--kind', 'event', '--realm', 'Server']})
    s.append({'op': 'ebx-set', 'ebx': job.level, 'prop': 'WorldSizeXZ', 'value': WORLD_SIZE_XZ})
    s.append({'op': 'ebx-set', 'ebx': job.level, 'prop': 'WorldSizeY', 'value': WORLD_SIZE_Y})
    return s, n


def prune_steps(job, partitions, registries):
    """in a NEW process on the saved project: removed instances stay in memory until written"""
    s = []
    for r in registries:
        for h in DROPPED_LAYERS:
            s.append({'op': 'cli', 'args': ['port', 'prune-refs', r, '--prefix', DIR + '/' + h], 'on-error': 'continue'})
        s.append({'op': 'cli', 'args': ['port', 'prune-refs', r, '--dangling'], 'on-error': 'continue'})
    for part in partitions:
        a = ['level', 'prune', part, '--dangling']
        for h in DROPPED_LAYERS:
            a += ['--refs-into', DIR + '/' + h]
        s.append({'op': 'cli', 'args': a, 'on-error': 'continue'})
    return s


def leftover_host_refs(dump):
    """references of a dump into the dropped host layers (must be 0 after the clean-up)"""
    bad = tuple((DIR + '/' + h).lower() for h in DROPPED_LAYERS)
    n = 0

    def walk(o):
        nonlocal n
        if isinstance(o, dict):
            p = o.get('__path')
            if isinstance(p, str) and p.lower() in bad:
                n += 1
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(dump)
    return n


# ------------------------------------------------------------------ doors
# The host's automatic double door (trigger box -> controller -> two keyframed leaves that slide along the prefab's
# local x). A map door is ONE panel put into both leaf slots of a clone of that prefab, in a frame turned so local x is
# the direction the door should slide. Indices are those of this prefab (dumped on two ports).
_DOOR_KAM = 'Objects/Props/ObjectSets/Kamino/Doors/O_KAM_DoorDoubleLeft_L_02'
DOOR = {
    'prefab': 'Gameplay/Prefabs/gamemodes/shared/PF_AutomaticDoubleDoor_Large_Kamino_01',
    'panel_blueprint': _DOOR_KAM,
    'panel_physics': _DOOR_KAM + '_Physics_Win32',
    'leaf_slots': ('Objects[3].Blueprint', 'Objects[4].Blueprint'),
    'open_fields': ('Interface.Fields[4].Value', 'Interface.Fields[5].Value'),       # 'Float32 <metres>'
    'light_fields': ('Interface.Fields[7].Value', 'Interface.Fields[8].Value'),      # door light colours
    'trigger_half': 'LinkConnections[2].Target.HalfExtents',
    'trigger_trans': ('LinkConnections[2].Target.Transform.trans', 'Objects[2].Transform.trans'),
    'trigger_reach': 4.84,                                                           # metres either side, as shipped
    # the door sound's audio lives in the host's superbundle and cannot be copied: the sound entity and its three
    # connections come out (a clone that kept them crashed on load); highest index first
    'sound_refs': ('Objects[5].DefaultSound', 'Objects[5].Sound'),
    'sound_remove': (('EventConnections', 3), ('EventConnections', 2), ('PropertyConnections', 7), ('Objects', 5)),
    'logic': ('Gameplay/GameModes/Shared/Doors/PF_AutomaticDoor_Core', 'Gameplay/GameModes/Shared/Doors/PF_AutomaticDoor_Controller',
              'Gameplay/Prefabs/GameModes/Shared/PF_Door_Lights'),
    # network-registry entries the host's own registries list for the door logic: (asset index in 'logic', class guid)
    'registry': ((0, 'd31e6982-b520-6784-1cf1-c3c18e7119c0'), (0, 'b683a611-b6f1-4f15-9d87-fdad5a115488'),
                 (0, '8ed79c3c-4b49-4895-9cc9-11b83f13ae2f'), (0, '5d2e3778-36c8-4cbb-8e1e-501bdea0e4b1'),
                 (0, '67ca62fc-f3ab-4031-9242-6dcbece05cc3'), (1, 'e408aa46-46e8-167e-31be-d4b65a587796'),
                 (1, 'f29bf912-de83-4c5d-9628-76961690f237'), (1, '22de1928-ad95-4b37-8f45-7c993a086919'),
                 (2, 'b556f27e-cc10-4566-86f0-e87e9845c2c5')),
    'open_fraction': 0.94,                                                           # a lip of the panel stays in the frame
}


def offset_for(play_centre):
    """scene play centre (x, y, z) -> the (x, y, z) added after the axis change (game = (x, z, -y) + offset)"""
    cx, cy, cz = play_centre
    return (round(PLAY_X - cx, 2), round(FLOOR_Y - cz, 2), round(PLAY_Z + cy, 2))
