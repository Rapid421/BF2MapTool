"""BF2 Map Tool: turn a Blender scene into a new Battlefront II (2017) level and a .fbmod.

Sidebar (N) > "BF2 Map". The machine paths are in the add-on preferences (Check setup shows what works).
"""
bl_info = {
    'name': 'BF2 Map Tool',
    'author': 'bf2maptool',
    'version': (0, 2, 0),
    'blender': (4, 2, 0),
    'location': 'View3D > Sidebar > BF2 Map',
    'description': 'Port a Blender scene to Star Wars Battlefront II (2017) as a new level',
    'category': 'Import-Export',
}

import bpy
from bpy.props import PointerProperty

from . import build, ops, overlay, prefs, props, ui

_CLASSES = prefs.CLASSES + build.CLASSES + ops.CLASSES + overlay.CLASSES + ui.CLASSES


def register():
    props.register()
    for c in _CLASSES:
        bpy.utils.register_class(c)
    bpy.types.WindowManager.bf2_build = PointerProperty(type=build.BF2BuildState)
    ui.register_props()
    overlay.register_props()
    bpy.types.VIEW3D_MT_add.append(ops.add_menu)
    bpy.types.TOPBAR_MT_file_import.append(ops.import_menu)
    ops.register_keymap()


def unregister():
    ops.unregister_keymap()
    bpy.types.TOPBAR_MT_file_import.remove(ops.import_menu)
    bpy.types.VIEW3D_MT_add.remove(ops.add_menu)
    overlay.unregister_props()
    ui.unregister_props()
    del bpy.types.WindowManager.bf2_build
    for c in reversed(_CLASSES):
        bpy.utils.unregister_class(c)
    props.unregister()
