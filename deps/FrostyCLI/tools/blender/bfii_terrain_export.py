# BFII terrain export -- Blender helper for `frostycli terrain create`.
#
# Exports a terrain object (any mesh: a displaced grid, a sculpt, an imported DEM) as the inputs `terrain create` takes:
#
#   <out>/heights.r32 (+ heights.r32.json)  float32 metres, NaN where the mesh has no ground (holes)
#   <out>/layers.json + mask_<name>.png     one 8-bit weight map per terrain layer
#   <out>/colormap.png                      the terrain colour (colour attribute or image texture), if there is one
#   <out>/textures/<layer>_*.png            the layer materials' image textures (colour, normal), copied for texture import
#
# Layers come from the object's vertex groups named  bfii:<hostLayer>[:<name>[:<material>]]  (splat weights, painted with
# Weight Paint), or, when there are none, from its material slots: a slot whose material has a custom property
# `bfii_layer` = <hostLayer> (optionally `bfii_material` = <0..15>) paints its faces with weight 1.
# <hostLayer> is the index into the host level's TerrainData.TerrainLayers (`frostycli terrain info` lists them).
#
# Axes: Frostbite is right-handed Y-up, so Blender (x, y, z) is Frostbite (x, z, -y). The exported rasters run min
# Frostbite Z first, which is max Blender Y first -- the top row of a top-down view. Blender units are metres.
#
# Use it from the UI (File > Export > BFII Terrain) or headless:
#   blender -b scene.blend --python bfii_terrain_export.py -- --object Terrain --out D:/terrain --resolution 2049
# then:
#   frostycli terrain create --heights D:/terrain/heights.r32 --layers D:/terrain/layers.json --host <level terrain> ...

bl_info = {
    "name": "BFII Terrain Export",
    "author": "frostycli",
    "version": (1, 1, 0),
    "blender": (3, 3, 0),
    "location": "File > Export > BFII Terrain; File > Import > BFII Skirmish Template",
    "description": "Heightmap, layer weight maps, colour map and layer textures for frostycli terrain create; "
                   "Skirmish map templates (ground, spawns, play space) from frostycli level skirmish-template",
    "category": "Import-Export",
}

import json
import math
import os
import shutil
import struct
import sys
import zlib

import bpy
import mathutils
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

try:
    import numpy as np
except ImportError:          # Blender ships numpy; keep a pure-python fallback anyway
    np = None


# ---------------------------------------------------------------------------------------------- png writer

def write_png(path, width, height, channels, data):
    """8-bit PNG; `data` is a bytes-like of width*height*channels, row 0 first."""
    color_type = {1: 0, 3: 2, 4: 6}[channels]
    stride = width * channels
    raw = bytearray()
    mv = memoryview(bytes(data))
    for y in range(height):
        raw.append(0)
        raw.extend(mv[y * stride:(y + 1) * stride])

    def chunk(tag, payload):
        c = struct.pack(">I", len(payload)) + tag + payload
        return c + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, color_type, 0, 0, 0)))
        f.write(chunk(b"IDAT", zlib.compress(bytes(raw), 6)))
        f.write(chunk(b"IEND", b""))


# ---------------------------------------------------------------------------------------------- sampling

class TerrainSampler:
    """World-space triangle BVH of the evaluated object; vertical rays give height, triangle and barycentrics."""

    def __init__(self, obj, depsgraph):
        self.obj = obj
        self.ev = obj.evaluated_get(depsgraph)
        self.mesh = self.ev.to_mesh()
        self.mesh.calc_loop_triangles()
        mw = obj.matrix_world
        self.verts = [mw @ v.co for v in self.mesh.vertices]
        self.tris = [tuple(t.vertices) for t in self.mesh.loop_triangles]
        self.tri_loops = [tuple(t.loops) for t in self.mesh.loop_triangles]
        self.tri_poly = [t.polygon_index for t in self.mesh.loop_triangles]
        self.bvh = BVHTree.FromPolygons(self.verts, self.tris, all_triangles=True)
        xs = [v.x for v in self.verts]; ys = [v.y for v in self.verts]; zs = [v.z for v in self.verts]
        self.min = mathutils.Vector((min(xs), min(ys), min(zs)))
        self.max = mathutils.Vector((max(xs), max(ys), max(zs)))

    def free(self):
        self.ev.to_mesh_clear()

    def cast(self, bx, by):
        """(height, tri index, weights over the tri's 3 corners) at Blender (bx, by), or None."""
        top = self.max.z + 10.0
        hit = self.bvh.ray_cast(mathutils.Vector((bx, by, top)), mathutils.Vector((0.0, 0.0, -1.0)), top - self.min.z + 20.0)
        if hit[0] is None:
            return None
        loc, _n, tri, _d = hit
        corners = [self.verts[i] for i in self.tris[tri]]
        w = poly_3d_calc(corners, loc)
        return loc.z, tri, w


def collect_layers(obj):
    """[(hostLayer, name, material, kind, key)] from vertex groups bfii:<n>[:name[:material]], else material slots."""
    layers = []
    for vg in obj.vertex_groups:
        if not vg.name.lower().startswith("bfii:"):
            continue
        parts = vg.name.split(":")
        try:
            host = int(parts[1])
        except (IndexError, ValueError):
            raise RuntimeError("vertex group '%s': want bfii:<hostLayer>[:<name>[:<material>]]" % vg.name)
        name = parts[2] if len(parts) > 2 and parts[2] else "layer%d" % host
        material = int(parts[3]) if len(parts) > 3 and parts[3] else 0
        layers.append((host, name, material, "group", vg.index))
    if layers:
        return layers
    for slot_index, slot in enumerate(obj.material_slots):
        mat = slot.material
        if mat is None or "bfii_layer" not in mat:
            continue
        layers.append((int(mat["bfii_layer"]), mat.name, int(mat.get("bfii_material", 0)), "material", slot_index))
    return layers


def vertex_group_weights(mesh, group_index):
    w = [0.0] * len(mesh.vertices)
    for v in mesh.vertices:
        for g in v.groups:
            if g.group == group_index:
                w[v.index] = g.weight
                break
    return w


def find_color_source(obj, mesh):
    """('attribute', name) for a colour attribute named bfii_color (or the active one), ('image', image) for the
    first material's base-colour image texture, else None."""
    attrs = getattr(mesh, "color_attributes", None)
    if attrs is not None and len(attrs) > 0:
        a = attrs.get("bfii_color") or attrs.active_color
        if a is not None:
            return ("attribute", a.name)
    for slot in obj.material_slots:
        img = base_color_image(slot.material)
        if img is not None:
            return ("image", img)
    return None


def base_color_image(mat):
    if mat is None or not mat.use_nodes:
        return None
    for node in mat.node_tree.nodes:
        if node.type == "BSDF_PRINCIPLED":
            inp = node.inputs.get("Base Color")
            if inp is not None and inp.is_linked:
                src = inp.links[0].from_node
                if src.type == "TEX_IMAGE" and src.image is not None:
                    return src.image
    return None


def material_images(mat):
    """{'color': image, 'normal': image} of a Principled material, when present."""
    out = {}
    img = base_color_image(mat)
    if img is not None:
        out["color"] = img
    if mat is not None and mat.use_nodes:
        for node in mat.node_tree.nodes:
            if node.type == "BSDF_PRINCIPLED":
                inp = node.inputs.get("Normal")
                if inp is not None and inp.is_linked:
                    nm = inp.links[0].from_node
                    if nm.type == "NORMAL_MAP":
                        ci = nm.inputs.get("Color")
                        if ci is not None and ci.is_linked and ci.links[0].from_node.type == "TEX_IMAGE":
                            out["normal"] = ci.links[0].from_node.image
    return out


class ImageSampler:
    def __init__(self, image):
        self.w, self.h = image.size
        self.ch = image.channels
        # image.pixels are the stored values: display (sRGB) for 8-bit sRGB images, linear for float images
        self.srgb = (not image.is_float) and image.colorspace_settings.name.lower().startswith("srgb")
        if np is not None:
            self.px = np.array(image.pixels[:], dtype=np.float32)
        else:
            self.px = list(image.pixels[:])

    def rgb(self, u, v):
        x = min(self.w - 1, max(0, int((u % 1.0) * self.w)))
        y = min(self.h - 1, max(0, int((v % 1.0) * self.h)))
        i = (y * self.w + x) * self.ch
        return self.px[i], self.px[i + 1], self.px[i + 2]


# ---------------------------------------------------------------------------------------------- export

def export_terrain(obj, out_dir, resolution=1025, rect=None, colormap_resolution=1024, copy_textures=True, log=print):
    """Writes the files described at the top. `rect` = (minX, minY, maxX, maxY) in Blender world units (default: the
    object's footprint). Returns the sidecar dict."""
    if obj is None or obj.type != "MESH":
        raise RuntimeError("select a mesh object (the terrain)")
    os.makedirs(out_dir, exist_ok=True)
    depsgraph = bpy.context.evaluated_depsgraph_get()
    s = TerrainSampler(obj, depsgraph)
    try:
        if rect is None:
            rect = (s.min.x, s.min.y, s.max.x, s.max.y)
        bx0, by0, bx1, by1 = rect
        n = int(resolution)
        if n < 2:
            raise RuntimeError("resolution must be at least 2")
        # Frostbite: x = bx, z = -by. Row j runs min Frostbite Z first = max Blender Y first.
        fz_min, fz_max = -by1, -by0
        layers = collect_layers(obj)
        mesh = s.mesh
        group_w = {}
        for host, name, mat, kind, key in layers:
            if kind == "group":
                group_w[key] = vertex_group_weights(mesh, key)
        color_src = find_color_source(obj, mesh)
        color_img = ImageSampler(color_src[1]) if color_src and color_src[0] == "image" else None
        color_attr = mesh.color_attributes[color_src[1]] if color_src and color_src[0] == "attribute" else None
        uv_layer = mesh.uv_layers.active if color_img is not None else None
        cm = int(colormap_resolution) if color_src else 0

        heights = bytearray(n * n * 4)
        masks = {host: bytearray(n * n) for host, *_ in layers}
        colors = bytearray(cm * cm * 3) if cm else None
        holes = 0
        lo, hi = float("inf"), float("-inf")
        nan = struct.pack("<f", float("nan"))
        # rays exactly on the mesh's outline miss by rounding; pull the outermost ones in by a hair
        ex, ey = (bx1 - bx0) * 1e-6, (by1 - by0) * 1e-6
        for j in range(n):
            by = by1 - (by1 - by0) * j / (n - 1)          # row 0 = max Blender Y = min Frostbite Z
            by = min(max(by, by0 + ey), by1 - ey)
            for i in range(n):
                bx = bx0 + (bx1 - bx0) * i / (n - 1)
                bx = min(max(bx, bx0 + ex), bx1 - ex)
                hit = s.cast(bx, by)
                o = j * n + i
                if hit is None:
                    heights[o * 4:o * 4 + 4] = nan
                    holes += 1
                    continue
                h, tri, w = hit
                heights[o * 4:o * 4 + 4] = struct.pack("<f", h)
                lo = min(lo, h); hi = max(hi, h)
                if layers:
                    corners = s.tris[tri]
                    poly = mesh.polygons[s.tri_poly[tri]]
                    for host, name, mat, kind, key in layers:
                        if kind == "group":
                            gw = group_w[key]
                            v = gw[corners[0]] * w[0] + gw[corners[1]] * w[1] + gw[corners[2]] * w[2]
                        else:
                            v = 1.0 if poly.material_index == key else 0.0
                        masks[host][o] = max(0, min(255, int(v * 255 + 0.5)))
            if j % 64 == 0:
                log("  heights row %d / %d" % (j, n))

        if cm:
            for j in range(cm):
                by = by1 - (by1 - by0) * (j + 0.5) / cm
                for i in range(cm):
                    bx = bx0 + (bx1 - bx0) * (i + 0.5) / cm
                    hit = s.cast(bx, by)
                    rgb, is_srgb = (0.5, 0.5, 0.5), True
                    if hit is not None:
                        _h, tri, w = hit
                        loops = s.tri_loops[tri]
                        if color_attr is not None:
                            # colour attributes read back linear; the map is stored as display (sRGB) colour
                            cols = [color_attr.data[l].color if color_attr.domain == "CORNER" else color_attr.data[s.tris[tri][k]].color
                                    for k, l in enumerate(loops)]
                            rgb, is_srgb = tuple(sum(cols[k][c] * w[k] for k in range(3)) for c in range(3)), False
                        elif color_img is not None and uv_layer is not None:
                            uvs = [uv_layer.data[l].uv for l in loops]
                            u = sum(uvs[k].x * w[k] for k in range(3)); v = sum(uvs[k].y * w[k] for k in range(3))
                            rgb, is_srgb = color_img.rgb(u, v), color_img.srgb
                    o = (j * cm + i) * 3
                    for c in range(3):
                        val = max(0.0, min(1.0, rgb[c]))
                        if not is_srgb:
                            val = val * 12.92 if val <= 0.0031308 else 1.055 * val ** (1 / 2.4) - 0.055
                        colors[o + c] = int(val * 255 + 0.5)

        with open(os.path.join(out_dir, "heights.r32"), "wb") as f:
            f.write(heights)
        side = {
            "format": "f32le", "units": "metres", "width": n, "height": n,
            "worldMinX": bx0, "worldMaxX": bx1, "worldMinZ": fz_min, "worldMaxZ": fz_max,
            "rowOrder": "min-z-first", "minMetres": lo, "maxMetres": hi, "holeSamples": holes,
            "source": "blender:" + obj.name,
        }
        with open(os.path.join(out_dir, "heights.r32.json"), "w") as f:
            json.dump(side, f, indent=1)

        layer_entries = []
        for host, name, mat, kind, key in sorted(layers, key=lambda t: t[0]):
            fname = "mask_%d_%s.png" % (host, "".join(ch if ch.isalnum() else "_" for ch in name))
            write_png(os.path.join(out_dir, fname), n, n, 1, masks[host])
            entry = {"name": name, "hostLayer": host, "mask": fname, "material": mat}
            if copy_textures:
                mat_obj = obj.material_slots[key].material if kind == "material" else None
                imgs = material_images(mat_obj) if mat_obj is not None else {}
                for role, img in imgs.items():
                    src = bpy.path.abspath(img.filepath) if img.filepath else ""
                    tex_dir = os.path.join(out_dir, "textures")
                    os.makedirs(tex_dir, exist_ok=True)
                    dst = os.path.join(tex_dir, "%s_%s%s" % (fname[:-4], role, os.path.splitext(src)[1] or ".png"))
                    if src and os.path.exists(src):
                        shutil.copyfile(src, dst)
                    else:
                        img.save_render(dst)
                    entry[role] = os.path.relpath(dst, out_dir).replace("\\", "/")
            layer_entries.append(entry)
        if layer_entries:
            # Host layer 0 is the base on every shipped terrain: it takes whatever the painted layers leave, so
            # unpainted ground is never empty. A painted layer-0 group becomes that remainder; otherwise one is added.
            if layer_entries[0]["hostLayer"] == 0:
                layer_entries[0].pop("mask", None)
            else:
                layer_entries.insert(0, {"name": "base", "hostLayer": 0, "material": 0})
        if cm:
            write_png(os.path.join(out_dir, "colormap.png"), cm, cm, 3, colors)
        layers_json = {"weights": "splat", "layers": layer_entries or [{"name": "base", "hostLayer": 0}]}
        if cm:
            layers_json["colormap"] = "colormap.png"
        with open(os.path.join(out_dir, "layers.json"), "w") as f:
            json.dump(layers_json, f, indent=1)
        log("BFII terrain: %dx%d heights %.2f..%.2f m, %d hole sample(s), %d layer(s)%s -> %s"
            % (n, n, lo, hi, holes, len(layer_entries), ", colour map" if cm else "", out_dir))
        return side
    finally:
        s.free()


# ---------------------------------------------------------------------------------------------- import: Skirmish template

def read_heightmap(path):
    """(rows, cols, heights as list of rows min-Z first, sidecar) from a .r16 (terrain export-heights) or .r32 + sidecar."""
    side = json.load(open(path + ".json"))
    raw = open(path, "rb").read()
    if path.lower().endswith(".r16") or side.get("format") == "u16le":
        n = side.get("size") or int(round(math.sqrt(len(raw) // 2)))
        lo, hi = side.get("minMetres", 0.0), side.get("maxMetres", 1.0)
        vals = struct.unpack("<%dH" % (n * n), raw[:n * n * 2])
        scale = (hi - lo) / 65535.0
        heights = [lo + v * scale for v in vals]
    else:
        n = side.get("width") or int(round(math.sqrt(len(raw) // 4)))
        heights = list(struct.unpack("<%df" % (n * n), raw[:n * n * 4]))
    rows = [heights[r * n:(r + 1) * n] for r in range(n)]
    if side.get("rowOrder", "min-z-first") != "min-z-first":
        rows.reverse()
    return n, rows, side


def import_skirmish_template(folder, max_side=513, log=print):
    """`frostycli level skirmish-template <map> --out <folder>`: the map's ground round its Skirmish play space as a mesh,
    every spawn as an empty (team in its name and a custom property), the play space as an outline -- sculpt and paint
    the terrain keeping ground under the spawns, then File > Export > BFII Terrain and `level create --skirmish-from`."""
    t = json.load(open(os.path.join(folder, "template.json")))
    coll = bpy.data.collections.new("BFII Skirmish " + t["map"].split("/")[-1])
    bpy.context.scene.collection.children.link(coll)
    hm = os.path.join(folder, "heights.r16")
    if not os.path.exists(hm):
        hm = os.path.join(folder, "heights.r32")
    if os.path.exists(hm):
        n, rows, side = read_heightmap(hm)
        step = max(1, int(math.ceil((n - 1) / (max_side - 1))))
        idx = list(range(0, n, step))
        if idx[-1] != n - 1:
            idx.append(n - 1)
        x0, x1, z0, z1 = side["worldMinX"], side["worldMaxX"], side["worldMinZ"], side["worldMaxZ"]
        verts = []
        for r in idx:
            z = z0 + (z1 - z0) * r / (n - 1)
            for c in idx:
                x = x0 + (x1 - x0) * c / (n - 1)
                verts.append((x, -z, rows[r][c]))          # Frostbite (x, y, z) -> Blender (x, -z, y)
        m = len(idx)
        faces = [(r * m + c, r * m + c + 1, (r + 1) * m + c + 1, (r + 1) * m + c) for r in range(m - 1) for c in range(m - 1)]
        mesh = bpy.data.meshes.new("Terrain")
        mesh.from_pydata(verts, [], faces)
        mesh.update()
        terr = bpy.data.objects.new("Terrain", mesh)
        terr["bfii_map"] = t["map"]
        terr["bfii_spawn_y"] = t["spawnY"]
        coll.objects.link(terr)
        log("terrain: %d x %d vertices over %.0f x %.0f m" % (m, m, x1 - x0, z1 - z0))
    for i, sp in enumerate(t.get("spawns", [])):
        team = (sp["team"] or "neutral").replace("Team", "").lower() or "neutral"
        e = bpy.data.objects.new("spawn_%s_%03d" % (team if not team.isdigit() else "team" + team, i), None)
        e.empty_display_type = "CONE"
        e.empty_display_size = 1.5
        e.location = (sp["x"], -sp["z"], sp["y"])
        e.rotation_euler = (math.pi, 0, 0)               # cone points down at the ground
        e["bfii_team"] = sp["team"]
        coll.objects.link(e)
    p = t["play"]                                         # x0, x1, z0, z1
    y = t["spawnY"]
    pm = bpy.data.meshes.new("PlaySpace")
    pm.from_pydata([(p[0], -p[2], y), (p[1], -p[2], y), (p[1], -p[3], y), (p[0], -p[3], y)], [(0, 1), (1, 2), (2, 3), (3, 0)], [])
    coll.objects.link(bpy.data.objects.new("PlaySpace", pm))
    log("%d spawns, play space x %.0f..%.0f z %.0f..%.0f, spawns stand at %.1f m; next: %s"
        % (len(t.get("spawns", [])), p[0], p[1], p[2], p[3], y, t.get("next", "")))
    return coll


class IMPORT_OT_bfii_skirmish_template(bpy.types.Operator):
    bl_idname = "import_scene.bfii_skirmish_template"
    bl_label = "Import BFII Skirmish Template"
    bl_options = {"REGISTER", "UNDO"}

    directory: bpy.props.StringProperty(subtype="DIR_PATH")
    max_side: bpy.props.IntProperty(name="Mesh vertices per side", default=513, min=17, max=4097)

    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        if not os.path.exists(os.path.join(self.directory, "template.json")):
            self.report({"ERROR"}, "no template.json here -- make one with: frostycli level skirmish-template <map> --out <folder>")
            return {"CANCELLED"}
        import_skirmish_template(self.directory, self.max_side, log=lambda m: self.report({"INFO"}, m))
        return {"FINISHED"}


def import_menu_func(self, context):
    self.layout.operator(IMPORT_OT_bfii_skirmish_template.bl_idname, text="BFII Skirmish Template (frostycli)")


# ---------------------------------------------------------------------------------------------- UI

class EXPORT_OT_bfii_terrain(bpy.types.Operator):
    bl_idname = "export_scene.bfii_terrain"
    bl_label = "Export BFII Terrain"
    bl_options = {"REGISTER"}

    directory: bpy.props.StringProperty(subtype="DIR_PATH")
    resolution: bpy.props.IntProperty(name="Heightmap samples", default=1025, min=2, max=16385,
                                      description="Samples per side (2^n + 1 matches the terrain grid)")
    colormap_resolution: bpy.props.IntProperty(name="Colour map size", default=1024, min=16, max=8192)
    copy_textures: bpy.props.BoolProperty(name="Copy layer textures", default=True)

    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        try:
            export_terrain(context.active_object, self.directory, self.resolution, None, self.colormap_resolution,
                           self.copy_textures, log=lambda m: self.report({"INFO"}, m))
        except RuntimeError as e:
            self.report({"ERROR"}, str(e))
            return {"CANCELLED"}
        return {"FINISHED"}


def menu_func(self, context):
    self.layout.operator(EXPORT_OT_bfii_terrain.bl_idname, text="BFII Terrain (frostycli)")


def register():
    bpy.utils.register_class(EXPORT_OT_bfii_terrain)
    bpy.utils.register_class(IMPORT_OT_bfii_skirmish_template)
    bpy.types.TOPBAR_MT_file_export.append(menu_func)
    bpy.types.TOPBAR_MT_file_import.append(import_menu_func)


def unregister():
    bpy.types.TOPBAR_MT_file_import.remove(import_menu_func)
    bpy.types.TOPBAR_MT_file_export.remove(menu_func)
    bpy.utils.unregister_class(IMPORT_OT_bfii_skirmish_template)
    bpy.utils.unregister_class(EXPORT_OT_bfii_terrain)


def _cli(argv):
    import argparse
    p = argparse.ArgumentParser(prog="bfii_terrain_export")
    p.add_argument("--object", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--resolution", type=int, default=1025)
    p.add_argument("--colormap-resolution", type=int, default=1024)
    p.add_argument("--rect", help="minX,minY,maxX,maxY in Blender units (default: the object's footprint)")
    p.add_argument("--no-textures", action="store_true")
    a = p.parse_args(argv)
    obj = bpy.data.objects.get(a.object)
    if obj is None:
        raise SystemExit("no object named " + a.object)
    rect = tuple(float(v) for v in a.rect.split(",")) if a.rect else None
    export_terrain(obj, a.out, a.resolution, rect, a.colormap_resolution, not a.no_textures)


if __name__ == "__main__":
    if "--" in sys.argv:
        _cli(sys.argv[sys.argv.index("--") + 1:])
    else:
        register()
