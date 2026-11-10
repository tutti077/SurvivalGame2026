"""Low-poly flint spear for the main hand (spear_flint, weapon class "spear").

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_spear_flint.py"

Writes Assets/models/weapons/spear_flint.fbx + .vmdl and Assets/materials/weapons/spear_flint.png + .vmat
(256 x 256, pixel_lit). Blender metres, exported 1:1; the vmdl's import_scale 0.4 gives 40 u/m.

Weapon convention (HeldModelFit, import_artist_pack.py): the model origin sits at the BUTT of the shaft and
the shaft runs along the long axis, so the fit holds it by the butt end and points the flint head away.
Modelled along Blender +X; the export quarter turn puts that on engine +X like the shield and buggy.

Geometry: a 1.9 m octagonal ash shaft that tapers from 3.6 cm at the grip to 2.8 cm under the head, a
knapped flint point 26 cm long (a flattened leaf of two faceted faces with a visible mid-ridge), and
three turns of sinew lashing where the point is seated. Faceted shading throughout.
"""
import bpy, bmesh, math, os, struct, zlib, random
from mathutils import Vector, Matrix

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
MODEL_DIR = os.path.join(ROOT, "Assets", "models", "weapons")
MAT_DIR = os.path.join(ROOT, "Assets", "materials", "weapons")
MODEL_REL = "models/weapons"
MAT_REL = "materials/weapons"
NAME = "spear_flint"

SHAFT_LEN = 1.9
R_BUTT = 0.018
R_TOP = 0.014
HEAD_LEN = 0.26
HEAD_W = 0.07
HEAD_T = 0.018
LASH_LEN = 0.09
SEED = 11

TEX = 256
# rows 0..159 wood (u along the shaft), 160..223 flint, 224..255 cord
WOOD_ROWS = (0, 160)
FLINT_ROWS = (160, 224)
CORD_ROWS = (224, 256)


def value_noise(rng, h, w, cells):
	grid = [[rng.random() for _ in range(cells + 1)] for _ in range(cells + 1)]
	out = []
	for y in range(h):
		fy = y / h * cells
		iy = int(fy); ty = fy - iy
		row = []
		for x in range(w):
			fx = x / w * cells
			ix = int(fx); tx = fx - ix
			a = grid[iy][ix]; b = grid[iy][min(ix + 1, cells)]
			c = grid[min(iy + 1, cells)][ix]; d = grid[min(iy + 1, cells)][min(ix + 1, cells)]
			row.append((a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty)
		out.append(row)
	return out


def make_texture():
	rng = random.Random(SEED)
	px = [[(0, 0, 0, 255)] * TEX for _ in range(TEX)]
	wood = [(132, 98, 60), (118, 86, 52), (146, 110, 68), (108, 78, 46)]
	grain = value_noise(rng, TEX, TEX, 6)
	for y in range(*WOOD_ROWS):
		for x in range(TEX):
			# long grain along x, broken by slow noise
			band = int((math.sin(y * 0.55 + grain[y][x] * 6.0) * 0.5 + 0.5) * 3.99)
			c = wood[band]
			px[y][x] = (c[0], c[1], c[2], 255)
	flint = [(96, 98, 102), (78, 80, 86), (112, 114, 118), (64, 66, 72)]
	fn = value_noise(rng, TEX, TEX, 10)
	for y in range(*FLINT_ROWS):
		for x in range(TEX):
			band = int(fn[y][x] * 3.99)
			c = flint[band]
			# knapping scallops: faint arcs
			if int((x * 0.35 + y * 0.9) % 9) == 0:
				c = flint[3]
			px[y][x] = (c[0], c[1], c[2], 255)
	cord = [(176, 142, 92), (156, 124, 78), (190, 156, 104)]
	for y in range(*CORD_ROWS):
		for x in range(TEX):
			c = cord[int((x // 4 + y // 3) % 3)]
			px[y][x] = (c[0], c[1], c[2], 255)
	return px


def write_png(path, rgba):
	h = len(rgba); w = len(rgba[0])
	raw = b"".join(b"\x00" + bytes(v for p in row for v in p) for row in rgba)
	def chunk(tag, data):
		c = tag + data
		return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xffffffff)
	png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
	png += chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
	with open(path, "wb") as f:
		f.write(png)


def uv_rows(rows, u, v):
	lo, hi = rows
	return (u % 1.0, 1.0 - (lo + (hi - lo) * (v % 1.0)) / TEX)


def build():
	bm = bmesh.new()
	uv_layer = bm.loops.layers.uv.new("UVMap")
	rng = random.Random(SEED + 1)

	def ring(x, r, segs=8, wobble=0.0):
		pts = []
		for i in range(segs):
			a = i / segs * math.tau
			rr = r * (1.0 + rng.uniform(-wobble, wobble))
			pts.append(bm.verts.new(Vector((x, math.cos(a) * rr, math.sin(a) * rr))))
		return pts

	def skin(ra, rb, rows, u0, u1):
		n = len(ra)
		for i in range(n):
			f = bm.faces.new((ra[i], ra[(i + 1) % n], rb[(i + 1) % n], rb[i]))
			f.smooth = False
			vs = [i / n, (i + 1) / n, (i + 1) / n, i / n]
			us = [u0, u0, u1, u1]
			for loop, u, v in zip(f.loops, us, vs):
				loop[uv_layer].uv = uv_rows(rows, u, v)

	def cap(ringv, rows, u):
		f = bm.faces.new(list(reversed(ringv)))
		f.smooth = False
		for loop in f.loops:
			loop[uv_layer].uv = uv_rows(rows, u, 0.5)

	# Shaft: butt at x = 0, head seat at x = SHAFT_LEN
	xs = [0.0, 0.35, 0.8, 1.25, SHAFT_LEN - LASH_LEN, SHAFT_LEN]
	rings = []
	for k, x in enumerate(xs):
		t = x / SHAFT_LEN
		r = R_BUTT * (1 - t) + R_TOP * t
		rings.append(ring(x, r, 8, 0.06 if 0 < k < len(xs) - 1 else 0.0))
	cap(rings[0], WOOD_ROWS, 0.0)
	for a, b, xa, xb in zip(rings[:-1], rings[1:], xs[:-1], xs[1:]):
		skin(a, b, WOOD_ROWS, xa / SHAFT_LEN * 4.0, xb / SHAFT_LEN * 4.0)

	# Lashing: three fatter rings over the seat
	seat0 = SHAFT_LEN - LASH_LEN
	lash_prev = None
	for k in range(4):
		x = seat0 + LASH_LEN * k / 3.0
		rr = R_TOP * (1.55 if k in (1, 2) else 1.25)
		lr = ring(x, rr, 8, 0.05)
		if lash_prev is not None:
			skin(lash_prev, lr, CORD_ROWS, k * 0.5, (k + 1) * 0.5)
		lash_prev = lr
	# close the lashing against the shaft at both ends (annulus faces)
	skin(rings[-2], lash_prev if False else rings[-2], CORD_ROWS, 0, 0) if False else None

	# Flint head: a flattened leaf with a mid-ridge, both faces faceted
	x0 = SHAFT_LEN - 0.02
	tip = Vector((SHAFT_LEN + HEAD_LEN, 0.0, 0.0))
	profile = [(0.0, 0.016), (0.25, 0.034), (0.5, 0.035), (0.75, 0.024), (0.92, 0.010)]
	left = []; right = []; ridge_top = []; ridge_bot = []
	for t, hw in profile:
		x = x0 + HEAD_LEN * t
		thick = HEAD_T * (1.0 - t * 0.8) * 0.5
		left.append(bm.verts.new(Vector((x, hw, 0.0))))
		right.append(bm.verts.new(Vector((x, -hw, 0.0))))
		ridge_top.append(bm.verts.new(Vector((x, rng.uniform(-0.004, 0.004), thick))))
		ridge_bot.append(bm.verts.new(Vector((x, rng.uniform(-0.004, 0.004), -thick))))
	tipv = bm.verts.new(tip)

	def tri(a, b, c, u):
		f = bm.faces.new((a, b, c))
		f.smooth = False
		for loop, (uu, vv) in zip(f.loops, [(u, 0.1), (u + 0.1, 0.9), (u + 0.2, 0.5)]):
			loop[uv_layer].uv = uv_rows(FLINT_ROWS, uu, vv)

	n = len(profile)
	for i in range(n - 1):
		u = i * 0.17
		# top faces
		tri(left[i], left[i + 1], ridge_top[i + 1], u); tri(left[i], ridge_top[i + 1], ridge_top[i], u + 0.05)
		tri(ridge_top[i], ridge_top[i + 1], right[i + 1], u + 0.1); tri(ridge_top[i], right[i + 1], right[i], u + 0.15)
		# bottom faces (reversed winding)
		tri(left[i + 1], left[i], ridge_bot[i + 1], u); tri(ridge_bot[i + 1], left[i], ridge_bot[i], u + 0.05)
		tri(ridge_bot[i + 1], ridge_bot[i], right[i + 1], u + 0.1); tri(right[i + 1], ridge_bot[i], right[i], u + 0.15)
	# tip fans
	tri(left[-1], tipv, ridge_top[-1], 0.8); tri(ridge_top[-1], tipv, right[-1], 0.85)
	tri(tipv, left[-1], ridge_bot[-1], 0.8); tri(tipv, ridge_bot[-1], right[-1], 0.85)
	# base: close the leaf against the seat
	tri(left[0], ridge_top[0], right[0], 0.0); tri(right[0], ridge_bot[0], left[0], 0.0)

	bm.verts.ensure_lookup_table()
	bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
	me = bpy.data.meshes.new(NAME)
	bm.to_mesh(me)
	bm.free()
	for p in me.polygons:
		p.use_smooth = False
	obj = bpy.data.objects.new(NAME, me)
	bpy.context.scene.collection.objects.link(obj)
	return obj


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
								from = "{name}.vmat"
								to = "{matrel}/{name}.vmat"
							}},
						]
						use_global_default = false
						global_default_material = "{matrel}/{name}.vmat"
					}},
				]
			}},
			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "PhysicsHullFromRender"
						parent_bone = ""
						surface_prop = "wood"
						collision_tags = "solid"
						faceMergeAngle = 20.0
						maxHullVertices = 32
						hull_mode = "SingleHull"
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
	TextureColor "materials/weapons/spear_flint.png"
}
"""

EXPORT_YAW = Matrix.Rotation(math.radians(-90.0), 4, 'Z')


def main():
	for o in list(bpy.data.objects):
		bpy.data.objects.remove(o, do_unlink=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0
	os.makedirs(MODEL_DIR, exist_ok=True)
	os.makedirs(MAT_DIR, exist_ok=True)

	write_png(os.path.join(MAT_DIR, NAME + ".png"), make_texture())
	with open(os.path.join(MAT_DIR, NAME + ".vmat"), "w", newline="\n") as f:
		f.write(VMAT)

	obj = build()
	mat = bpy.data.materials.new(NAME)
	mat.use_nodes = True
	obj.data.materials.append(mat)
	obj.data.transform(EXPORT_YAW)
	obj.data.update()

	for o in bpy.data.objects:
		o.select_set(False)
	obj.select_set(True)
	bpy.context.view_layer.objects.active = obj
	bpy.ops.export_scene.fbx(filepath=os.path.join(MODEL_DIR, NAME + ".fbx"), use_selection=True,
							 global_scale=1.0, apply_unit_scale=True, object_types={'MESH'},
							 mesh_smooth_type='OFF', path_mode='STRIP', embed_textures=False)
	with open(os.path.join(MODEL_DIR, NAME + ".vmdl"), "w", newline="\n") as f:
		f.write(VMDL.format(name=NAME, rel=MODEL_REL, matrel=MAT_REL))
	dims = obj.dimensions
	print(f"[spear_flint] verts={len(obj.data.vertices)} faces={len(obj.data.polygons)} dims={dims.x:.3f} x {dims.y:.3f} x {dims.z:.3f} m")


main()
