"""Every property the tool stores in a .blend: scene (map settings, look), object (role, collision, gameplay),
material (class, emission), light (mode). Registered by the add-on and by the headless export alike, so a scene
exports the same with or without the add-on's panels loaded.
"""
import bpy
from bpy.props import (BoolProperty, EnumProperty, FloatProperty, FloatVectorProperty, IntProperty, PointerProperty,
                       StringProperty)

MATERIAL_CLASSES = [
    ('opaque', 'Opaque', 'Solid surface: colour, normal, smoothness, metal'),
    ('emissive', 'Emissive', 'Glows and lights the bake'),
    ('cutout', 'Cut-out', 'Holes where the alpha is under one half (grates, fences)'),
    ('cutout2s', 'Cut-out, double-sided', 'Cut-out that is visible from both sides (foliage, cloth)'),
    ('glass', 'Glass', 'See-through glass'),
    ('lava', 'Lava', 'Flowing lava (the lava preset)'),
    # last in the list so scenes saved before it existed keep their choice
    ('holo', 'Hologram (animated)', 'Glowing, see-through, scrolling surface with scan lines and flicker: holograms, neon signs, '
     'animated screens. Not solid, not in the light bake'),
    ('cutout_emissive', 'Cut-out, glowing', 'Holes where the alpha is under one half, and the rest glows with the Emission: lit grates, '
     'signs with a see-through background. Built as a cut-out plus a glowing layer just in front of it'),
]
ROLES = [
    ('static', 'Map geometry', 'Part of the level'),
    ('skip', 'Leave out', 'Not exported'),
    ('collision', 'Collision mesh', 'Custom collision for another object; not drawn'),
    ('spawn', 'Spawn', 'A soldier spawn point'),
    ('hero_spawn', 'Hero spawn', 'A hero start (Heroes vs Villains)'),
    ('command_post', 'Command post', 'A capture point (Supremacy)'),
    ('combat_area', 'Combat area', 'Outline of the playable area (a flat mesh)'),
    ('kill_volume', 'Kill volume', 'A box that kills whoever enters'),
    ('door', 'Door', 'A mesh that slides open when someone is near'),
    ('not_walkable', 'Not walkable', 'A box bots and spawns stay out of'),
    ('reflection_probe', 'Reflection probe', 'An extra reflection capture point'),
    ('sound', 'Ambient sound', 'A looping sound from the preset list'),
    ('fx', 'Effect', 'An effect from the preset list'),
    ('reference', 'Scale reference', 'The 1.8 m figure; not exported'),
]
COLLISION = [
    ('exact', 'Exact', 'Every render triangle is solid (walk on it, shots stop)'),
    ('custom', 'Custom mesh', 'Another object gives the solid shape'),
    ('hull', 'Hull', 'One convex shape around the object (moving parts)'),
    ('none', 'None', 'Not solid (foliage, lava, decals)'),
]
TEAMS = [('team1', 'Team 1', ''), ('team2', 'Team 2', ''), ('neutral', 'Neutral', '')]
LIGHT_MODES = [
    ('baked', 'Baked', 'In the lightmaps only: no runtime cost'),
    ('dynamic', 'Dynamic', 'Real-time light; not in the lightmaps'),
    ('both', 'Both', 'Real-time direct light, bounce light baked'),
]
ERAS = [('clone_wars', 'Clone Wars', ''), ('civil_war', 'Galactic Civil War', ''), ('sequel', 'First Order / Resistance', '')]
DOOR_AXES = [('up', 'Up', ''), ('down', 'Down', ''), ('left', 'Left', ''), ('right', 'Right', '')]
SKY_PRESETS = [('hdr', 'HDR panorama file', ''), ('night', 'Night', ''), ('overcast', 'Overcast', ''), ('clear', 'Clear day', ''),
               ('sunset', 'Sunset', ''), ('space', 'Space', ''), ('volcanic', 'Volcanic', ''),
               # last in the list so scenes saved before it existed keep their choice
               ('world', 'Blender World (match the scene)', 'The scene\'s World is the sky; sun, lamps, emission and exposure are '
                'converted from their Blender values so the level is lit as the scene is'),
               ('game', 'Game sky (from a shipped level)', 'The sky, sun, fog, exposure and colour grading of one of the game\'s own '
                'levels, exactly as DICE made them; the light bake uses that sky and sun')]
# = runtime stages/look.py GAME_SKIES (tests/unit/test_game_skies.py keeps the two the same)
GAME_SKIES = [('tatooine_sunny', 'Tatooine - sunny', ''), ('tatooine_sunset', 'Tatooine - sunset', ''), ('tatooine_night', 'Tatooine - night', ''),
              ('mos_eisley', 'Mos Eisley - sunny', ''), ('endor_sunny', 'Endor - sunny', ''), ('endor_foggy', 'Endor - foggy', ''),
              ('endor_night', 'Endor - night', ''), ('hoth_sunny', 'Hoth - sunny', ''), ('hoth_sunset', 'Hoth - sunset', ''),
              ('jakku_sunny', 'Jakku - sunny', ''), ('jakku_sunset', 'Jakku - sunset', ''), ('kamino_stormy', 'Kamino - stormy', ''),
              ('kashyyyk_overcast', 'Kashyyyk - overcast', ''), ('kashyyyk_night', 'Kashyyyk - night', ''),
              ('naboo_cloudy', 'Naboo - cloudy', ''), ('naboo_dusk', 'Naboo - dusk', ''), ('naboo_sunset', 'Naboo - sunset', ''),
              ('starkiller_cloudy', 'Starkiller - cloudy', ''), ('takodana_cloudy', 'Takodana - cloudy', ''),
              ('takodana_dawn', 'Takodana - misty dawn', ''), ('yavin_sunny', 'Yavin 4 - sunny', ''), ('yavin_sunset', 'Yavin 4 - sunset', ''),
              ('sullust_cloudy', 'Sullust - cloudy', '')]
EXPOSURE_MODES = [('outdoor', 'Outdoor', 'The game sky\'s own automatic exposure (EV 12.5 - 15 for daylight)'),
                  ('interior', 'Interior', 'The exposure DICE uses inside (automatic EV 8 - 10.5): for enclosed maps lit by their lamps '
                   'and glowing panels, where the outdoor exposure leaves everything dark')]
# how bright a place this is in the game's own terms. The picture is the same at every level (all light scales with it);
# it only decides how the game's own effects - blaster bolts, explosions, HUD markers - sit against the level.
EV_LEVELS = [('auto', 'Automatic', 'Daylight when the scene has a sun, else from the brightness of the sky'),
             ('day', 'Daylight', ''), ('overcast', 'Overcast', ''), ('dusk', 'Dusk', ''), ('interior', 'Interior', ''), ('night', 'Night', '')]
# names and numbers = the runtime's stages/bake.py QUALITY table
BAKE_QUALITY = [('draft', 'Draft (64 samples, 3 texels / m)', ''), ('normal', 'Normal (256 samples, 6 texels / m)', ''),
                ('high', 'High (512 samples, 9 texels / m)', '')]


class BF2SceneProps(bpy.types.PropertyGroup):
    name: StringProperty(name='Level name', description='Letters and digits, starts with a letter; names every asset of the map', default='MyMap')
    display_name: StringProperty(name='Menu name', default='')
    planet: StringProperty(name='Planet', default='')
    description: StringProperty(name='Description', default='')
    menu_picture: BoolProperty(name='Loading picture from the camera', default=True,
                               description='Render the loading-screen picture from a camera named "Menu camera" (else the scene '
                                           'camera). No camera = the host level\'s loading screen')
    era: EnumProperty(name='Era', items=ERAS, default='clone_wars')
    mode_blast: BoolProperty(name='Blast', default=True)
    mode_hvv: BoolProperty(name='Heroes vs Villains', default=True)
    mode_arcade: BoolProperty(name='Arcade', default=True)
    mode_supremacy: BoolProperty(name='Supremacy', default=False)
    play_radius: FloatProperty(name='Play radius', description='0 = measured from the spawns and command posts', default=0.0, min=0.0, unit='LENGTH')
    keep_distance: FloatProperty(name='Keep geometry within', description='Geometry farther than this outside the play area is left out '
                                 '(huge terrains, backdrops). 0 = keep everything', default=250.0, min=0.0, unit='LENGTH')
    auto_spawns: BoolProperty(name='Auto spawns', description='Generate the spawns that are not placed by hand', default=True)
    bake_materials: EnumProperty(name='Bake materials', description='Render what a material\'s nodes do into plain textures before packing',
                                 items=[('auto', 'When needed', 'Only the inputs that are more than an image or a value: mixes, ramps, bump, tiling, procedurals'),
                                        ('always', 'Always', 'Every wired input of every material'), ('never', 'Never', 'Use the image behind the nodes and warn')],
                                 default='auto')
    bake_max_size: EnumProperty(name='Largest baked texture', items=[('1024', '1024', ''), ('2048', '2048', ''), ('4096', '4096', '')], default='2048')
    # look
    sky: EnumProperty(name='Sky', items=SKY_PRESETS, default='world')
    ev_level: EnumProperty(name='Light level', items=EV_LEVELS, default='auto')
    game_sky: EnumProperty(name='Game sky', items=GAME_SKIES, default='tatooine_sunny')
    game_sky_asset: StringProperty(name='Other game sky', default='',
                                   description='Any VisualEnvironmentBlueprint the game ships (its asset path); empty = the one picked above')
    sun_from_scene: BoolProperty(name='Sun direction from the scene', default=True,
                                 description='Game sky: point the game\'s sun the way the scene\'s sun lamp shines (when there is one)')
    exposure_mode: EnumProperty(name='Exposure', items=EXPOSURE_MODES, default='outdoor')
    bake_on_build: BoolProperty(name='Light bake on Build', default=True,
                                description='Build bakes the lighting too (bounce light, soft shadows, light probes). Off = a quicker '
                                            'build that looks flat in game')
    sky_hdr: StringProperty(name='HDR panorama', subtype='FILE_PATH', default='')
    sky_strength: FloatProperty(name='Sky brightness', default=1.0, min=0.0)
    sun_on: BoolProperty(name='Sun', default=True)
    exposure: FloatProperty(name='Exposure (EV)', default=0.0, soft_min=-6.0, soft_max=6.0)
    fog_on: BoolProperty(name='Game fog', default=False, description='Blender World mode: add the game\'s distance fog. Off = no fog, '
                         'as in the Blender render')
    fog_color: FloatVectorProperty(name='Fog colour', subtype='COLOR', size=3, default=(0.5, 0.55, 0.6), min=0.0, max=1.0)
    fog_start: FloatProperty(name='Fog start', default=40.0, min=0.0, unit='LENGTH')
    fog_end: FloatProperty(name='Fog end', default=800.0, min=1.0, unit='LENGTH')
    fog_density: FloatProperty(name='Fog amount', default=0.3, min=0.0, max=1.0)
    bake_quality: EnumProperty(name='Bake quality', items=BAKE_QUALITY, default='normal')
    piece_tris: IntProperty(name='Triangles per piece', description='Most triangles one game mesh holds. Raise it (up to 20,000) for very '
                            'dense maps that would otherwise make more pieces than a level can hold (about 1,200)', default=3000, min=1000, max=20000)
    piece_ext: FloatProperty(name='Piece size near the play area', description='Largest piece in metres near the play area (up to 63 m; '
                             'the game\'s 16-bit positions are exactly as precise up to 64 m as at 48 m)', default=48.0, min=24.0, max=63.0)
    piece_far: FloatProperty(name='Far piece growth', description='How fast pieces may grow with the distance from the play area '
                             '(0.5 = half the distance, 2 = twice the distance: fewer pieces for big backdrops)', default=0.5, min=0.5, max=2.0)
    dynamic_light_budget: IntProperty(name='Dynamic light budget', default=64, min=0)
    tint_variants: BoolProperty(name='Vertex-colour tints', default=True,
                                description='A material that multiplies by a Color Attribute: on every object whose colour attribute is ONE '
                                            'colour (a per-object tint, as in LEGO rips) the faces get a copy of the material with that colour '
                                            'baked in. Off = the colour attribute is ignored (warned)')
    tint_max: IntProperty(name='Tints per material', default=16, min=1, max=64,
                          description='Most tinted copies of one material; further tints use the nearest of those')


class BF2ObjectProps(bpy.types.PropertyGroup):
    role: EnumProperty(name='Role', items=ROLES, default='static')
    collision: EnumProperty(name='Collision', items=COLLISION, default='exact')
    collision_object: PointerProperty(name='Collision mesh', type=bpy.types.Object)
    team: EnumProperty(name='Team', items=TEAMS, default='neutral')
    cp_letter: StringProperty(name='Letter', default='A', maxlen=1)
    cp_radius: FloatProperty(name='Capture radius', default=9.0, min=1.0, unit='LENGTH')
    cp_order: IntProperty(name='Order', default=0, min=0)
    cp_vehicles: BoolProperty(name='Vehicle spawns', default=False)
    door_axis: EnumProperty(name='Slides', items=DOOR_AXES, default='up')
    door_distance: FloatProperty(name='Distance', description='0 = 94 % of the door size along the slide direction', default=0.0, min=0.0, unit='LENGTH')
    preset: StringProperty(name='Preset', default='')


class BF2MaterialProps(bpy.types.PropertyGroup):
    cls: EnumProperty(name='Class', items=MATERIAL_CLASSES, default='opaque')
    cls_guessed: BoolProperty(name='Guessed', default=False)
    cls_auto: BoolProperty(name='Detect from the material', description='While the class is Opaque: build the material as emissive when it '
                           'really emits light, as a cut-out when its alpha really has holes', default=True)
    emission_screen: FloatProperty(name='Brightness on screen', description='Multiplies the emission the player sees', default=1.0, min=0.0)
    emission_bake: FloatProperty(name='Brightness in the bake', description='Multiplies the light this material casts in the bake', default=1.0, min=0.0)
    normal_format: EnumProperty(name='Normal map format', description='Which way the green channel of the normal map points',
                                items=[('auto', 'Automatic', 'Worked out from the image itself'), ('opengl', 'OpenGL (green up)', 'Blender, Substance "OpenGL"'),
                                       ('directx', 'DirectX (green down)', 'Unreal, most game rips')], default='auto')
    template: StringProperty(name='Surface template', description='Advanced / testing: the name of a host surface template to build this '
                             'material on instead of its class\'s own (empty = normal)', default='')
    double_sided: BoolProperty(name='Double-sided', description='Draw both faces (adds the reversed faces)', default=False)
    skip: BoolProperty(name='Leave out', description='Faces with this material are not exported', default=False)
    lava_color: FloatVectorProperty(name='Lava colour', subtype='COLOR', size=3, default=(1.0, 0.9, 0.8), min=0.0, max=1.0)
    lava_brightness: FloatProperty(name='Lava brightness', default=1.0, min=0.0)
    lava_crust: FloatProperty(name='Crust', default=0.095, min=0.0, max=1.0)
    lava_speed: FloatProperty(name='Speed', default=1.0, min=0.0)
    holo_speed: FloatVectorProperty(name='Scroll speed', description='How fast the texture moves, in texture widths per second (u, v)',
                                    size=2, default=(0.0, 0.0), soft_min=-5.0, soft_max=5.0)
    holo_tiling: FloatVectorProperty(name='Tiling', description='How often the image repeats over the uv square (u, v)', size=2, default=(1.0, 1.0))
    holo_brightness: FloatProperty(name='Hologram brightness', description='Multiplies the glow (with the Emission Strength)', default=1.0, min=0.0)
    holo_image: StringProperty(name='Hologram image', description='The image shown; empty = the emission (or base colour) of the material, '
                               'baked when the nodes need it', default='')


class BF2LightProps(bpy.types.PropertyGroup):
    mode: EnumProperty(name='Mode', items=LIGHT_MODES, default='both')
    shadows: BoolProperty(name='Shadows', description='Dynamic lights only', default=False)
    bake_strength: FloatProperty(name='Brightness in the bake', description='Multiplies what this lamp gives the light bake '
                                 '(direct light of a Baked lamp, bounce of a Both lamp); the real-time light is not changed',
                                 default=1.0, min=0.0)


CLASSES = (BF2SceneProps, BF2ObjectProps, BF2MaterialProps, BF2LightProps)


def register():
    for c in CLASSES:
        if not getattr(c, 'is_registered', False):
            bpy.utils.register_class(c)
    bpy.types.Scene.bf2 = PointerProperty(type=BF2SceneProps)
    bpy.types.Object.bf2 = PointerProperty(type=BF2ObjectProps)
    bpy.types.Material.bf2 = PointerProperty(type=BF2MaterialProps)
    bpy.types.Light.bf2 = PointerProperty(type=BF2LightProps)


def unregister():
    for t in (bpy.types.Scene, bpy.types.Object, bpy.types.Material, bpy.types.Light):
        if hasattr(t, 'bf2'):
            del t.bf2
    for c in reversed(CLASSES):
        if getattr(c, 'is_registered', False):
            bpy.utils.unregister_class(c)
