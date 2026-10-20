"""Low-poly hover platform for the vehicle system (Mark 2026-10-08: "a flat floating platform that players can
build on, 3x2 in size … the front of the ship should have the driver's steering console, used while standing
so it's like one of those pallet carts but backwards").

    blender -b --python "Blender/scripts/create_hover_platform.py"

Writes Assets/models/vehicles/: hover_platform.fbx (two objects: `hover_platform` = render mesh, `hover_platform_phys`
= slab + pods for the single-hull collider), hover_platform.vmdl, platform_atlas.png (64 px palette atlas, 8 px
cells) + platform_atlas.vmat (pixel_lit, point-sampled). Blender meters, exported 1:1; the vmdl uses import_scale
0.4 (40 u/m). Origin = slab centre; deck top at z +0.2 m (+8 u), pod undersides at z -0.55 m (Vehicle
HoverHullBottomMeters). Deck = 3 x 2 build tiles = 6 x 4 m (DECK_L x DECK_W): change those two numbers and the
`vehicle_hover_platform` row in BuildModuleDimensions.SizesMeters together.

Layout (Blender frame, forward = +X, left = +Y; the export yaw puts forward on the engine's +X like the buggy):
slab 6 x 4 x 0.4 with a tread-plate top and hazard stripes along the long edges, four lift pods underneath at
(+-2.2, +-1.4), a console pedestal at the front centre (x 2.6) with a tilted screen and a handlebar the pawn
stands behind (seat anchor x 1.9 m), and a cell bay box at the front-right corner (x 2.4, y -1.55) that the prefab
tags `vehiclestorage`.
"""
import bpy, bmesh, math, os, struct, zlib
import numpy as np
from mathutils import Vector, Matrix

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
OUT_DIR = os.path.join(ROOT, "Assets", "models", "vehicles")
ASSET_REL = "models/vehicles"
BODY = "hover_platform"
PHYS = "hover_platform_phys"
ATLAS = "platform_atlas"

DECK_L = 6.0
DECK_W = 4.0
DECK_T = 0.4
POD_X = 2.2
POD_Y = 1.4
POD_R = 0.5
POD_H = 0.35
CONSOLE_X = 2.6
BAY_X = 2.4
BAY_Y = -1.55

# ---------------------------------------------------------------- palette atlas (8 px cells on a 64 px sheet)
CELL = 8
SHEET = 64
# name -> (column, row, rgb)
CELLS = {
	"steel":   (0, 0, (118, 122, 130)),
	"edge":    (1, 0, (74, 78, 86)),
	"pod":     (2, 0, (40, 42, 48)),
	"glow":    (3, 0, (80, 210, 240)),
	"console": (4, 0, (48, 52, 58)),
	"rail":    (5, 0, (160, 164, 170)),
	"cellbox": (6, 0, (226, 118, 40)),
	"vent":    (7, 0, (30, 32, 36)),
	"deck":    (0, 1, (128, 132, 140)),   # 2x2 cells: tread plate, planar-mapped
	"stripe":  (2, 1, (236, 190, 40)),    # 2x2 cells: yellow / black hazard, planar-mapped
	"screen":  (4, 1, (24, 60, 72)),      # 2x2 cells: console screen with cyan readout lines, planar-mapped
}
BIG = {"deck", "stripe", "screen"}


def make_atlas():
	img = np.zeros((SHEET, SHEET, 4), np.uint8)
	img[..., 3] = 255
	rng = np.random.default_rng(11)
	for name, (cx, cy, rgb) in CELLS.items():
		size = CELL * (2 if name in BIG else 1)
		x0, y0 = cx * CELL, cy * CELL
		base = np.array(rgb, float)
		block = np.repeat(np.repeat(base[None, None, :], size, 0), size, 1)
		block += rng.integers(-5, 6, (size, size, 1))
		if name == "deck":
			# tread plate: a diagonal lattice of raised bars
			for y in range(size):
				for x in range(size):
					if (x + y) % 8 in (0, 1) or (x - y) % 8 in (0, 1):
						block[y, x] = np.array(rgb) + 22
					elif (x + y) % 8 == 2 or (x - y) % 8 == 2:
						block[y, x] = np.array(rgb) - 26
		if name == "stripe":
			for y in range(size):
				for x in range(size):
					if ((x + y) // 4) % 2 == 1:
						block[y, x] = (28, 28, 30)
		if name == "screen":
			for y in range(size):
				for x in range(size):
					if y in (3, 7, 11) and 2 <= x <= 13 - (y // 4) * 3:
						block[y, x] = CELLS["glow"][2]
					if 2 <= x <= 4 and 2 <= y <= 13 and y % 2 == 0:
						block[y, x] = (110, 230, 250)
		img[y0:y0 + size, x0:x0 + size, :3] = np.clip(block, 0, 255).astype(np.uint8)
	return img


def write_png(path, rgba):
	h, w, _ = rgba.shape
	raw = b"".join(b"\x00" + rgba[y].tobytes() for y in range(h))

	def chunk(tag, data):
		c = struct.pack(">I", len(data)) + tag + data
		return c + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff)

	png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
	png += chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
	with open(path, "wb") as f:
		f.write(png)


def cell_uv(name, fx=0.5, fy=0.5):
	"""UV inside a cell (fx, fy in 0..1). Image row 0 is the top, so v is flipped."""
	cx, cy, _ = CELLS[name]
	size = CELL * (2 if name in BIG else 1)
	u = (cx * CELL + fx * size) / SHEET
	v = 1.0 - (cy * CELL + fy * size) / SHEET
	return (u, v)


# ---------------------------------------------------------------- mesh helpers (same shape as create_dune_buggy.py)
class Builder:
	def __init__(self):
		self.bm = bmesh.new()
		self.tag = self.bm.faces.layers.string.new("cell")

	def _add(self, tmp, name, planar=None):
		tmp.faces.ensure_lookup_table()
		vmap = {}
		for v in tmp.verts:
			vmap[v.index] = self.bm.verts.new(v.co)
		for f in tmp.faces:
			try:
				nf = self.bm.faces.new([vmap[v.index] for v in f.verts])
			except ValueError:
				continue
			nf[self.tag] = name.encode()
			if planar:
				nf[self.tag] = (name + "|" + planar).encode()

	def box(self, center, size, name="steel", rot=None, planar=None):
		tmp = bmesh.new()
		mat = Matrix.Translation(Vector(center))
		if rot is not None:
			mat = mat @ rot.to_4x4()
		mat = mat @ Matrix.Diagonal((size[0], size[1], size[2], 1.0))
		bmesh.ops.create_cube(tmp, size=1.0, matrix=mat)
		tmp.verts.index_update()
		self._add(tmp, name, planar)
		tmp.free()

	def cylinder(self, center, radius, depth, name="steel", segs=10, rot=None, radius2=None):
		tmp = bmesh.new()
		mat = Matrix.Translation(Vector(center))
		if rot is not None:
			mat = mat @ rot.to_4x4()
		bmesh.ops.create_cone(tmp, cap_ends=True, cap_tris=False, segments=segs, radius1=radius,
							  radius2=radius if radius2 is None else radius2, depth=depth, matrix=mat)
		tmp.verts.index_update()
		self._add(tmp, name)
		tmp.free()

	def tube(self, a, b, r=0.025, name="rail", segs=6):
		a, b = Vector(a), Vector(b)
		d = b - a
		if d.length < 1e-5:
			return
		rot = d.normalized().to_track_quat('Z', 'Y').to_matrix().to_4x4()
		mat = Matrix.Translation((a + b) * 0.5) @ rot
		tmp = bmesh.new()
		bmesh.ops.create_cone(tmp, cap_ends=True, cap_tris=False, segments=segs, radius1=r, radius2=r, depth=d.length, matrix=mat)
		tmp.verts.index_update()
		self._add(tmp, name)
		tmp.free()

	def to_object(self, name):
		bm = self.bm
		bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.0005)
		uv = bm.loops.layers.uv.new("UVMap")
		for f in bm.faces:
			raw = f[self.tag].decode()
			cell, _, planar = raw.partition("|")
			if planar:
				axes = {"xy": (0, 1), "xz": (0, 2), "yz": (1, 2)}[planar]
				pts = [(l.vert.co[axes[0]], l.vert.co[axes[1]]) for l in f.loops]
				xs = [p[0] for p in pts]
				ys = [p[1] for p in pts]
				w = max(xs) - min(xs) or 1.0
				h = max(ys) - min(ys) or 1.0
				# tile the big cell every metre so the tread / stripes keep their scale on the long faces
				for l, p in zip(f.loops, pts):
					fx = ((p[0] - min(xs)) % 1.0) if w > 1.0 else (p[0] - min(xs)) / w
					fy = ((p[1] - min(ys)) % 1.0) if h > 1.0 else (p[1] - min(ys)) / h
					l[uv].uv = cell_uv(cell, fx, fy)
			else:
				for l in f.loops:
					l[uv].uv = cell_uv(cell)
			f.smooth = False
		me = bpy.data.meshes.new(name)
		bm.to_mesh(me)
		bm.free()
		obj = bpy.data.objects.new(name, me)
		bpy.context.scene.collection.objects.link(obj)
		return obj


# ---------------------------------------------------------------- body
def build_body(mat):
	b = Builder()
	half_t = DECK_T * 0.5

	# Slab: dark edge band, tread-plate top in 1 m tiles (planar xy wraps every metre), plain underside.
	b.box((0, 0, 0), (DECK_L, DECK_W, DECK_T), "edge")
	b.box((0, 0, half_t + 0.01), (DECK_L - 0.1, DECK_W - 0.1, 0.02), "deck", planar="xy")
	# Hazard stripes along both long edges, on the deck top.
	for sy in (1, -1):
		b.box((0, sy * (DECK_W * 0.5 - 0.17), half_t + 0.025), (DECK_L - 0.2, 0.28, 0.01), "stripe", planar="xy")
	# Vent slots in the edge band (three per long side) so the sides are not one flat face.
	for sx in (-1.8, 0.0, 1.8):
		for sy in (1, -1):
			b.box((sx, sy * (DECK_W * 0.5 + 0.005), 0.0), (0.9, 0.02, 0.16), "vent")

	# Four lift pods: a wide skirt tapering down to the glow disc.
	for px in (POD_X, -POD_X):
		for py in (POD_Y, -POD_Y):
			b.cylinder((px, py, -half_t - POD_H * 0.5), POD_R, POD_H, "pod", segs=10, radius2=POD_R * 0.8)
			b.cylinder((px, py, -half_t - POD_H - 0.02), POD_R * 0.7, 0.04, "glow", segs=10)
	# A keel strake between the pods so the belly reads as a hull.
	b.box((0, 0, -half_t - 0.08), (DECK_L - 1.6, 0.5, 0.16), "edge")

	# Console: pedestal on the deck at the front centre, tilted screen, handlebar behind it.
	ped_h = 1.0
	b.box((CONSOLE_X, 0, half_t + ped_h * 0.5), (0.45, 0.7, ped_h), "console")
	tilt = Matrix.Rotation(math.radians(-25.0), 3, 'Y')   # top leans toward the driver (back = -X)
	top_c = Vector((CONSOLE_X - 0.02, 0, half_t + ped_h + 0.06))
	b.box(top_c, (0.5, 0.76, 0.1), "console", rot=tilt)
	screen_c = top_c + tilt @ Vector((0.0, 0.0, 0.056))
	b.box(screen_c, (0.32, 0.56, 0.012), "screen", rot=tilt, planar="xy")
	# Handlebar: a crossbar at the driver's side with two grips and the uprights into the pedestal.
	bar_z = half_t + 1.02
	bar_x = CONSOLE_X - 0.3
	b.tube((bar_x, -0.42, bar_z), (bar_x, 0.42, bar_z), r=0.028, name="rail")
	for sy in (0.42, -0.42):
		b.tube((bar_x, sy, bar_z), (CONSOLE_X - 0.22, sy, bar_z - 0.25), r=0.022, name="rail")
		b.cylinder((bar_x, sy * 0.78, bar_z), 0.04, 0.22, "pod", segs=6, rot=Matrix.Rotation(math.radians(90.0), 3, 'X'))
	# Console foot plate so the pedestal does not just sprout from the deck.
	b.box((CONSOLE_X, 0, half_t + 0.02), (0.7, 0.95, 0.04), "edge")

	# Cell bay at the front-right corner: a steel box with an orange lid and two cyan terminals showing.
	bay_h = 0.35
	b.box((BAY_X, BAY_Y, half_t + bay_h * 0.5), (0.5, 0.5, bay_h), "console")
	b.box((BAY_X, BAY_Y, half_t + bay_h + 0.02), (0.44, 0.44, 0.04), "cellbox")
	for sy in (-0.1, 0.1):
		b.box((BAY_X, BAY_Y + sy, half_t + bay_h + 0.06), (0.08, 0.08, 0.04), "glow")

	obj = b.to_object(BODY)
	obj.data.materials.append(mat)
	return obj


def build_phys():
	"""Convex hull source: the slab plus the pods, nothing above the deck (the prefab's own box colliders
	do the console and the bay, so the compound body stays a flat plate pieces can be built on)."""
	b = Builder()
	b.box((0, 0, 0), (DECK_L, DECK_W, DECK_T), "edge")
	for px in (POD_X, -POD_X):
		for py in (POD_Y, -POD_Y):
			b.box((px, py, -DECK_T * 0.5 - POD_H * 0.5), (POD_R * 1.6, POD_R * 1.6, POD_H), "pod")
	return b.to_object(PHYS)


# ---------------------------------------------------------------- vmdl / vmat
VMDL = """<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
{{
	rootNode =
	{{
		_class = "RootNode"
		children =
		[
			{{
				_class = "MaterialGroupList"
				children =
				[
					{{
						_class = "DefaultMaterialGroup"
						remaps =
						[
							{{
								from = "{atlas}.vmat"
								to = "{rel}/{atlas}.vmat"
							}},
						]
						use_global_default = false
						global_default_material = "{rel}/{atlas}.vmat"
					}},
				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
					{{
						_class = "RenderMeshFile"
						name = "{name}_lod0"
						filename = "{rel}/{name}.fbx"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 0.4
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{{
							exclude_by_default = true
							exception_list = [ "{name}" ]
						}}
					}},
				]
			}},
			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "PhysicsHullFile"
						filename = "{rel}/{name}.fbx"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 0.4
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						surface_prop = "metal"
						collision_tags = "solid"
						faceMergeAngle = 10.0
						maxHullVertices = 48
						hull_mode = "SingleHull"
						import_filter =
						{{
							exclude_by_default = true
							exception_list = [ "{phys}" ]
						}}
					}},
				]
			}},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}}
}}
"""

VMAT = """Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.800"
	TextureColor "models/vehicles/platform_atlas.png"
}
"""

# Blender +X lands on the engine's +Y; this quarter turn makes modelled forward = engine +X (the drive axis).
EXPORT_YAW = Matrix.Rotation(math.radians(-90.0), 4, 'Z')


def export(objs, name):
	for o in objs:
		o.data.transform(EXPORT_YAW)
		o.data.update()
	for o in bpy.data.objects:
		o.select_set(False)
	for o in objs:
		o.select_set(True)
	bpy.context.view_layer.objects.active = objs[0]
	bpy.ops.export_scene.fbx(filepath=os.path.join(OUT_DIR, name + ".fbx"), use_selection=True,
							 global_scale=1.0, apply_unit_scale=True, object_types={'MESH'},
							 mesh_smooth_type='OFF', path_mode='STRIP', embed_textures=False)
	with open(os.path.join(OUT_DIR, name + ".vmdl"), "w", newline="\n") as f:
		f.write(VMDL.format(name=name, phys=PHYS, rel=ASSET_REL, atlas=ATLAS))


def main():
	for o in list(bpy.data.objects):
		bpy.data.objects.remove(o, do_unlink=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0
	os.makedirs(OUT_DIR, exist_ok=True)

	png = os.path.join(OUT_DIR, ATLAS + ".png")
	write_png(png, make_atlas())
	with open(os.path.join(OUT_DIR, ATLAS + ".vmat"), "w", newline="\n") as f:
		f.write(VMAT)

	mat = bpy.data.materials.new(ATLAS)
	mat.use_nodes = True
	bsdf = mat.node_tree.nodes["Principled BSDF"]
	bsdf.inputs["Roughness"].default_value = 0.8
	tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
	tex.image = bpy.data.images.load(png, check_existing=True)
	tex.interpolation = 'Closest'
	mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])

	body = build_body(mat)
	phys = build_phys()
	export([body, phys], BODY)
	for o in (body, phys):
		tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
		print(f"{o.name}: {len(o.data.polygons)} faces, {tris} tris")
	# Bounds check in the exported (engine-facing) frame: the deck must read 6 m along X and 4 m along Y.
	xs = [v.co.x for v in body.data.vertices]
	ys = [v.co.y for v in body.data.vertices]
	zs = [v.co.z for v in body.data.vertices]
	print(f"exported bounds x {min(xs):.2f}..{max(xs):.2f} y {min(ys):.2f}..{max(ys):.2f} z {min(zs):.2f}..{max(zs):.2f}")
	blend_dir = os.path.join(ROOT, "Blender", "blenderprojects")
	os.makedirs(blend_dir, exist_ok=True)
	blend = os.path.join(blend_dir, "vehicle_hover_platform.blend")
	bpy.ops.wm.save_as_mainfile(filepath=blend)
	print("saved", blend)


if __name__ == "__main__":
	main()
