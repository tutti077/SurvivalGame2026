"""Low-poly dune buggy for the vehicle system — body + one wheel model (Mark's reference photo, 2026-10-03:
tube roll cage, two bucket seats, orange hood plate with stripes, coil-over shocks, fat knobby tyres on
tan rims, fuel canister top-left rear).

    blender -b --python "Blender/scripts/create_dune_buggy.py"

Writes Assets/models/vehicles/: dune_buggy.fbx/.vmdl (body, origin = chassis centre as the Vehicle
component expects), dune_buggy_wheel.fbx/.vmdl (one wheel, axle along local Y so the Vehicle code's
pitch spin / yaw steer rotates it), buggy_atlas.png (64 px palette atlas, 8 px cells) + buggy_atlas.vmat
(pixel_lit, point-sampled). Blender meters, exported 1:1; the vmdls use import_scale 0.4 (40 u/m).
Geometry matches the prefab: wheel mounts at x ±1.2 m, y ±0.85 m; seats at (0.2, ±0.4, 0.3); the
storage crate on the prefab sits against the rear plate at x -1.65 m.
"""
import bpy, bmesh, math, os, struct, zlib
import numpy as np
from mathutils import Vector, Matrix

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
OUT_DIR = os.path.join(ROOT, "Assets", "models", "vehicles")
ASSET_REL = "models/vehicles"
BODY = "dune_buggy"
WHEEL = "dune_buggy_wheel"
ATLAS = "buggy_atlas"

TUBE_R = 0.03
TUBE_SEGS = 6
WHEEL_R = 0.35
WHEEL_W = 0.26

# ---------------------------------------------------------------- palette atlas (8 px cells on a 64 px sheet)
CELL = 8
SHEET = 64
# name -> (column, row, rgb)
CELLS = {
	"steel":   (0, 0, (112, 114, 120)),
	"orange":  (1, 0, (232, 108, 38)),
	"rubber":  (2, 0, (30, 30, 32)),
	"knob":    (3, 0, (46, 46, 50)),
	"seat":    (4, 0, (36, 36, 40)),
	"floor":   (5, 0, (62, 62, 64)),
	"tan":     (6, 0, (196, 176, 136)),
	"hub":     (7, 0, (74, 68, 62)),
	"plate":   (0, 1, (124, 122, 114)),   # 2x2 cells: hood plate with orange stripes, planar-mapped
	"rim":     (2, 1, (196, 176, 136)),   # 2x2 cells: rim face with spoke gaps, planar-mapped
	"canister": (4, 1, (206, 190, 150)),
	"spring":  (5, 1, (226, 96, 32)),
	"strap":   (6, 1, (160, 36, 40)),
}
BIG = {"plate", "rim"}


def make_atlas():
	img = np.zeros((SHEET, SHEET, 4), np.uint8)
	img[..., 3] = 255
	rng = np.random.default_rng(3)
	for name, (cx, cy, rgb) in CELLS.items():
		size = CELL * (2 if name in BIG else 1)
		x0, y0 = cx * CELL, cy * CELL
		base = np.array(rgb, float)
		block = np.repeat(np.repeat(base[None, None, :], size, 0), size, 1)
		# a little per-texel variation so flat faces are not dead flat under point sampling
		block += rng.integers(-6, 7, (size, size, 1))
		if name == "plate":
			for y in range(size):
				for x in range(size):
					if (x + y) % 8 in (2, 3):
						block[y, x] = CELLS["orange"][2]
		if name == "rim":
			c = (size - 1) / 2
			for y in range(size):
				for x in range(size):
					dx, dy = x - c, y - c
					r = math.hypot(dx, dy)
					ang = math.atan2(dy, dx)
					spoke = abs(((ang / (2 * math.pi) * 5) % 1.0) - 0.5) < 0.22
					if r > 3.2 and not spoke:
						block[y, x] = CELLS["hub"][2]
					if r <= 1.6:
						block[y, x] = CELLS["hub"][2]
		img[y0:y0 + size, x0:x0 + size, :3] = np.clip(block, 0, 255).astype(np.uint8)
	return img


def write_png(path, rgba):
	h, w, _ = rgba.shape
	raw = b"".join(b"\x00" + rgba[y].tobytes() for y in range(h))

	def chunk(tag, data):
		c = struct.pack(">I", len(data)) + tag + data
		return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

	png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
		   + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
	with open(path, "wb") as f:
		f.write(png)


def cell_uv(name, fx=0.5, fy=0.5):
	"""UV inside a cell (fx, fy in 0..1). Image row 0 is the top, so v is flipped."""
	cx, cy, _ = CELLS[name]
	size = CELL * (2 if name in BIG else 1)
	u = (cx * CELL + fx * size) / SHEET
	v = 1.0 - (cy * CELL + fy * size) / SHEET
	return (u, v)


# ---------------------------------------------------------------- mesh helpers
class Builder:
	def __init__(self):
		self.bm = bmesh.new()
		self.tag = self.bm.faces.layers.string.new("cell")

	def _add(self, tmp, name, planar=None):
		"""Append a temporary bmesh into ours, tagging every face with a palette cell."""
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

	def tube(self, a, b, r=TUBE_R, name="steel", segs=TUBE_SEGS):
		a, b = Vector(a), Vector(b)
		d = b - a
		length = d.length
		if length < 1e-5:
			return
		rot = d.normalized().to_track_quat('Z', 'Y').to_matrix().to_4x4()
		mat = Matrix.Translation((a + b) * 0.5) @ rot
		tmp = bmesh.new()
		bmesh.ops.create_cone(tmp, cap_ends=True, cap_tris=False, segments=segs, radius1=r, radius2=r, depth=length, matrix=mat)
		tmp.verts.ensure_lookup_table()
		for v in tmp.verts:
			v.index = v.index
		tmp.verts.index_update()
		self._add(tmp, name)
		tmp.free()

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

	def ring(self, center, radius, tube_r, name="steel", rot=None, segs=10):
		"""Steering wheel: a thin torus built as a spun polygon."""
		tmp = bmesh.new()
		prof = [tmp.verts.new((radius + tube_r * math.cos(t), 0.0, tube_r * math.sin(t))) for t in np.linspace(0, 2 * math.pi, 5, endpoint=False)]
		edges = [tmp.edges.new((prof[i], prof[(i + 1) % 5])) for i in range(5)]
		bmesh.ops.spin(tmp, geom=edges, cent=(0, 0, 0), axis=(0, 0, 1), angle=2 * math.pi, steps=segs, use_merge=True)
		mat = Matrix.Translation(Vector(center))
		if rot is not None:
			mat = mat @ rot.to_4x4()
		bmesh.ops.transform(tmp, matrix=mat, verts=tmp.verts)
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
				# planar map the face's bounding square onto the (big) cell
				axes = {"xy": (0, 1), "xz": (0, 2), "yz": (1, 2)}[planar]
				pts = [(l.vert.co[axes[0]], l.vert.co[axes[1]]) for l in f.loops]
				xs = [p[0] for p in pts]
				ys = [p[1] for p in pts]
				w = max(xs) - min(xs) or 1.0
				h = max(ys) - min(ys) or 1.0
				for l, p in zip(f.loops, pts):
					l[uv].uv = cell_uv(cell, (p[0] - min(xs)) / w, (p[1] - min(ys)) / h)
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
	B = Builder()
	T = B.tube
	# floor rails + cross tubes (z -0.25)
	zf = -0.25
	for y in (-0.55, 0.55):
		T((-1.35, y, zf), (1.3, y, zf))
	for x in (-1.35, -0.45, 0.5, 1.3):
		T((x, -0.55, zf), (x, 0.55, zf))
	# floor pan
	B.box((-0.15, 0.0, zf - 0.02), (2.2, 1.08, 0.03), "floor")
	# sills (z 0.3) + uprights
	for y in (-0.6, 0.6):
		T((-1.0, y, 0.3), (1.0, y, 0.3))
		T((-1.0, y, zf), (-1.0, y, 0.3))
		T((1.0, y, zf), (1.0, y, 0.3))
	# main hoop behind the seats
	for y in (-0.6, 0.6):
		T((-0.45, y, zf), (-0.45, y, 1.1))
		T((-0.45, y, 1.1), (-1.4, y * 0.75, 0.2))            # rear diagonal brace
		T((-0.45, y, 0.65), (-1.4, y * 0.75, 0.2), r=0.022)   # lower brace
	T((-0.45, -0.6, 1.1), (-0.45, 0.6, 1.1))
	T((-0.45, -0.6, 0.65), (-0.45, 0.6, 0.65), r=0.022)
	# front hoop (windscreen frame) + roof tubes
	for y in (-0.55, 0.55):
		T((0.6, y, 0.3), (0.6, y, 0.95))
		T((0.6, y, 0.95), (-0.45, y * 1.09, 1.1))
		T((0.6, y, 0.95), (1.25, y * 0.8, 0.3), r=0.022)      # bonnet brace
	T((0.6, -0.55, 0.95), (0.6, 0.55, 0.95))
	T((0.6, -0.55, 0.62), (0.6, 0.55, 0.62), r=0.022)         # dash bar
	# nose
	for y in (-0.6, 0.6):
		T((1.0, y, 0.3), (1.72, y * 0.5, 0.12))
		T((1.3, y * 0.92, zf), (1.72, y * 0.5, 0.12))
	T((1.72, -0.3, 0.12), (1.72, 0.3, 0.12))
	# hood plate (orange stripes), pitched down toward the nose
	B.box((1.42, 0.0, 0.24), (0.55, 0.86, 0.03), "plate", rot=Matrix.Rotation(math.radians(14), 3, 'Y'), planar="xy")
	# rear cage + engine plate
	for y in (-0.45, 0.45):
		T((-1.4, y, 0.2), (-1.4, y, 0.62))
		T((-1.4, y, zf), (-1.4, y, 0.2))
	T((-1.4, -0.45, 0.62), (-1.4, 0.45, 0.62))
	T((-1.4, -0.45, 0.2), (-1.4, 0.45, 0.2))
	B.box((-1.33, 0.0, 0.41), (0.03, 0.86, 0.38), "plate", planar="yz")
	# fuel canister, top-left rear (strapped to the hoop)
	T((-0.9, 0.62, 0.9), (-0.35, 0.62, 0.9), r=0.11, name="canister", segs=8)
	B.box((-0.62, 0.62, 0.9), (0.06, 0.26, 0.26), "strap")
	T((-0.62, 0.62, 0.65), (-0.62, 0.62, 0.9), r=0.02)
	# seats: cushion + raked back + headrest, driver left (+y)
	for y in (-0.4, 0.4):
		B.box((0.2, y, 0.22), (0.5, 0.46, 0.12), "seat")
		back_rot = Matrix.Rotation(math.radians(-12), 3, 'Y')
		B.box((-0.05, y, 0.55), (0.1, 0.46, 0.62), "seat", rot=back_rot)
		B.box((-0.11, y, 0.95), (0.1, 0.26, 0.16), "seat", rot=back_rot)
		# side bolsters
		B.box((0.2, y - 0.2, 0.3), (0.5, 0.06, 0.08), "seat")
		B.box((0.2, y + 0.2, 0.3), (0.5, 0.06, 0.08), "seat")
	# steering wheel + column (driver +y)
	col_rot = Matrix.Rotation(math.radians(-50), 3, 'Y')
	T((0.95, 0.4, 0.45), (0.66, 0.4, 0.74), r=0.025)
	B.ring((0.64, 0.4, 0.76), 0.16, 0.022, "seat", rot=col_rot)
	B.box((0.64, 0.4, 0.76), (0.05, 0.3, 0.04), "seat", rot=col_rot)   # spoke bar
	# suspension: upper + lower A-arms and coil-over shocks toward each wheel mount
	for sx in (1.0, -1.0):
		for sy in (1.0, -1.0):
			mx, my = 1.2 * sx, 0.85 * sy
			T((mx - 0.25 * sx, 0.4 * sy, zf), (mx, my - 0.1 * sy, -0.4), r=0.02)      # lower arm
			T((mx + 0.25 * sx, 0.4 * sy, zf), (mx, my - 0.1 * sy, -0.4), r=0.02)
			T((mx, 0.5 * sy, 0.1), (mx, my - 0.15 * sy, -0.2), r=0.02)                # upper arm
			T((mx - 0.05 * sx, 0.5 * sy, 0.45), (mx, my - 0.12 * sy, -0.35), r=0.028)  # damper body
			T((mx - 0.03 * sx, 0.5 * sy, 0.25), (mx, my - 0.12 * sy, -0.15), r=0.045, name="spring", segs=8)  # coil
			T((mx, my - 0.1 * sy, -0.4), (mx, my, -0.4), r=0.03, name="hub")           # stub axle toward the wheel
	obj = B.to_object(BODY)
	obj.data.materials.append(mat)
	return obj


# ---------------------------------------------------------------- wheel (axle = local Y, origin = hub centre)
def build_wheel(mat):
	bm = bmesh.new()
	tag = bm.faces.layers.string.new("cell")
	# profile in (r, y): hub face, rim lip, sidewall, tread
	prof = [(0.04, 0.07), (0.19, 0.07), (0.21, 0.12), (0.31, 0.13), (0.35, 0.09), (0.35, -0.09), (0.31, -0.13), (0.21, -0.12), (0.19, -0.07), (0.04, -0.07)]
	verts = [bm.verts.new((r, y, 0.0)) for r, y in prof]
	edges = [bm.edges.new((verts[i], verts[i + 1])) for i in range(len(verts) - 1)]
	steps = 16
	bmesh.ops.spin(bm, geom=edges, cent=(0, 0, 0), axis=(0, 1, 0), angle=2 * math.pi, steps=steps, use_merge=True)
	bm.faces.ensure_lookup_table()
	# tag faces by where they sit on the profile
	knob_faces = []
	for f in bm.faces:
		c = f.calc_center_median()
		r = math.hypot(c.x, c.z)
		ay = abs(c.y)
		if r > 0.33 and ay < 0.1:
			f[tag] = b"rubber"
			ang = math.atan2(c.z, c.x)
			idx = int(round(ang / (2 * math.pi / steps))) % steps
			if idx % 2 == 0:
				knob_faces.append(f)
		elif r > 0.2:
			f[tag] = b"rubber"
		elif r > 0.1 and ay > 0.09:
			f[tag] = b"tan"
		else:
			f[tag] = b"rim|xz"
	# tread knobs: every other tread face pushed out, sides of the knob lighter
	res = bmesh.ops.extrude_face_region(bm, geom=knob_faces)
	new_verts = [g for g in res["geom"] if isinstance(g, bmesh.types.BMVert)]
	for v in new_verts:
		radial = Vector((v.co.x, 0.0, v.co.z)).normalized()
		v.co += radial * 0.03
	for g in res["geom"]:
		if isinstance(g, bmesh.types.BMFace):
			g[tag] = b"knob"
	for f in knob_faces:
		f[tag] = b"knob"
	bm.faces.ensure_lookup_table()
	uv = bm.loops.layers.uv.new("UVMap")
	for f in bm.faces:
		raw = f[tag].decode()
		cell, _, planar = raw.partition("|")
		if planar:
			pts = [(l.vert.co.x, l.vert.co.z) for l in f.loops]
			# map by position within the rim disc (radius 0.21) so the spoke texture lines up around the hub
			for l, p in zip(f.loops, pts):
				l[uv].uv = cell_uv(cell, 0.5 + p[0] / 0.42, 0.5 + p[1] / 0.42)
		else:
			for l in f.loops:
				l[uv].uv = cell_uv(cell)
		f.smooth = False
	me = bpy.data.meshes.new(WHEEL)
	bm.to_mesh(me)
	bm.free()
	obj = bpy.data.objects.new(WHEEL, me)
	bpy.context.scene.collection.objects.link(obj)
	obj.data.materials.append(mat)
	return obj


# ---------------------------------------------------------------- export
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

	PixelRoughness "0.850"
	TextureColor "models/vehicles/buggy_atlas.png"
}
"""


# Blender +X lands on the engine's +Y (see memory: artist-pack import). The body is modelled with forward = +X
# and left = +Y in Blender; this quarter turn makes forward = engine +X (the Vehicle's drive axis) and the
# wheel axle = engine Y, which is the axis Vehicle.UpdateWheelVisuals spins around.
EXPORT_YAW = Matrix.Rotation(math.radians(-90.0), 4, 'Z')


def export(obj, name):
	obj.data.transform(EXPORT_YAW)
	obj.data.update()
	for o in bpy.data.objects:
		o.select_set(False)
	obj.select_set(True)
	bpy.context.view_layer.objects.active = obj
	bpy.ops.export_scene.fbx(filepath=os.path.join(OUT_DIR, name + ".fbx"), use_selection=True,
							 global_scale=1.0, apply_unit_scale=True, object_types={'MESH'},
							 mesh_smooth_type='OFF', path_mode='STRIP', embed_textures=False)
	with open(os.path.join(OUT_DIR, name + ".vmdl"), "w", newline="\n") as f:
		f.write(VMDL.format(name=name, rel=ASSET_REL, atlas=ATLAS))


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
	bsdf.inputs["Roughness"].default_value = 0.85
	tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
	tex.image = bpy.data.images.load(png, check_existing=True)
	tex.interpolation = 'Closest'
	mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])

	body = build_body(mat)
	wheel = build_wheel(mat)
	wheel.location = (0.0, 2.0, 0.0)   # parked beside the body in the .blend; exported at its own origin
	export(body, BODY)
	wheel.location = (0.0, 0.0, 0.0)
	export(wheel, WHEEL)
	for o in (body, wheel):
		tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
		print(f"{o.name}: {len(o.data.polygons)} faces, {tris} tris")
	blend = os.path.join(ROOT, "Blender", "blenderprojects", "vehicle_dune_buggy.blend")
	bpy.ops.wm.save_as_mainfile(filepath=blend)
	print("saved", blend)


if __name__ == "__main__":
	main()
