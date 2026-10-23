"""P0-7 test scene: the inputs real users have - names with accents / CJK / emoji-free symbols and spaces, a 63-character
name, a PACKED image, an image by RELATIVE path, an image file with a non-ASCII name, a MISSING image file, a material
with an image node but no file. Saved to <dir>/src/Awk scène.blend (space + accent in the path too).
    blender -b --factory-startup --python make_awkward.py -- <dir> <addon dir>
"""
import os
import sys

import bpy
import numpy as np

argv = sys.argv[sys.argv.index('--') + 1:]
D, ADDON = argv[0], argv[1]
sys.path.insert(0, ADDON)
import props  # noqa: E402

bpy.ops.wm.read_factory_settings(use_empty=True)
props.register()
src = os.path.join(D, 'src')
os.makedirs(os.path.join(src, 'tex'), exist_ok=True)
sc = bpy.context.scene
sc.bf2.name = 'AwkTest'


def png(path, rgb):
    img = bpy.data.images.new(os.path.basename(path), 64, 64, alpha=True)
    px = np.ones((64, 64, 4), np.float32); px[..., :3] = rgb; px[::8, :, :3] *= 0.7
    img.pixels.foreach_set(px.ravel())
    img.filepath_raw = path; img.file_format = 'PNG'; img.save()
    return img


def textured(mname, img):
    m = bpy.data.materials.new(mname); m.use_nodes = True
    t = m.node_tree.nodes.new('ShaderNodeTexImage'); t.image = img
    m.node_tree.links.new(t.outputs['Color'], m.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    return m


def box(name, loc, m, size=2.0):
    bpy.ops.mesh.primitive_cube_add(size=size, location=loc)
    o = bpy.context.active_object; o.name = name
    bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.uv.smart_project(); bpy.ops.object.mode_set(mode='OBJECT')
    o.data.materials.append(m)
    return o


bpy.ops.mesh.primitive_plane_add(size=60)
fl = bpy.context.active_object; fl.name = 'Sol en béton - floor'
fl.data.materials.append(textured('Béton', png(os.path.join(src, 'tex', 'béton_couleur.png'), (0.5, 0.5, 0.45))))
# relative path: the image lives next to the .blend as //tex/rel.png
rel = png(os.path.join(src, 'tex', 'rel.png'), (0.2, 0.6, 0.2))
box('Mur_墙_wall', (4, 0, 1), textured('Métal ✓', rel))
# packed image (its file is deleted after packing: only the .blend has it)
pk = png(os.path.join(src, 'tex', 'packed_tmp.png'), (0.7, 0.2, 0.2)); pk.pack()
box('Caisse emballée (packed)', (-4, 0, 1), textured('Packed matériau', pk))
# missing file
ms = bpy.data.images.load(os.path.join(src, 'tex', 'missing.png'), check_existing=False) if False else bpy.data.images.new('missing.png', 4, 4)
ms.source = 'FILE'; ms.filepath = os.path.join(src, 'tex', 'does_not_exist.png')
box('Missing texture box', (0, 5, 1), textured('Missing tex', ms))
# 63-character names (Blender's limit)
box('L' * 63, (0, -5, 1), textured('M' * 63, rel))
for t, x in (('team1', -20), ('team2', 20)):
    s = bpy.data.objects.new('Spawn équipe ' + t, None); s.location = (x, 0, 0.1); s.bf2.role = 'spawn'; s.bf2.team = t
    sc.collection.objects.link(s)
sun = bpy.data.lights.new('Soleil', 'SUN'); so = bpy.data.objects.new('Soleil', sun); so.rotation_euler = (0.6, 0.2, 0.4); sc.collection.objects.link(so)
sc.world = bpy.data.worlds.new('Monde')
blend = os.path.join(src, 'Awk scène.blend')
bpy.ops.wm.save_as_mainfile(filepath=blend, relative_remap=True)
bpy.ops.file.make_paths_relative()
bpy.ops.wm.save_mainfile()
os.remove(os.path.join(src, 'tex', 'packed_tmp.png'))
print('MADE', blend, [(i.name, i.filepath, bool(i.packed_file)) for i in bpy.data.images])
