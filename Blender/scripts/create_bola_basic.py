"""Low-poly bola for the main hand (bola_basic): three stones on sinew cords knotted together.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_bola_basic.py"

Writes Assets/models/weapons/bola_basic.fbx + .vmdl and Assets/materials/weapons/bola_basic.png + .vmat
(256 x 256, pixel_lit). Blender metres, exported 1:1; the vmdl's import_scale 0.4 gives 40 u/m.

Convention: the model ORIGIN is the knot (where the hand holds it); the three cords hang straight down
(-Z) 0.5 m to the stones, splayed a little so the stones never overlap. PlayerAnimation.Bola.cs holds the
knot on the hand bone and rotates the whole model: hanging limp at rest, swung out flat and spun while
charging. BolaProjectile spins the same model round its flight axis.
"""
import bpy, bmesh, math, os, struct, zlib, random
from mathutils import Vector, Matrix

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
MODEL_DIR = os.path.join(ROOT, "Assets", "models", "weapons")
MAT_DIR = os.path.join(ROOT, "Assets", "materials", "weapons")
MODEL_REL = "models/weapons"
MAT_REL = "materials/weapons"
NAME = "bola_basic"

CORD_LEN = 0.5
CORD_R = 0.006
STONE_R = 0.045
SPLAY = 0.06
SEED = 5
TEX = 256
STONE_ROWS = (0, 160)
CORD_ROWS = (160, 256)


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


def make_texture():
	rng = random.Random(SEED)
	px = [[(0, 0, 0, 255)] * TEX for _ in range(TEX)]
	stone = [(118, 112, 104), (98, 94, 88), (134, 128, 118), (84, 80, 76)]
	for y in range(*STONE_ROWS):
		for x in range(TEX):
			v = (math.sin(x * 0.21 + y * 0.13) + math.sin(x * 0.07 - y * 0.19)) * 0.25 + 0.5
			c = stone[int(v * 3.99)]
			px[y][x] = (c[0], c[1], c[2], 255)
	cord = [(168, 134, 88), (146, 114, 72), (184, 150, 100)]
	for y in range(*CORD_ROWS):
		for x in range(TEX):
			c = cord[int((x // 3 + y // 5) % 3)]
			px[y][x] = (c[0], c[1], c[2], 255)
	return px


def uv_rows(rows, u, v):
	lo, hi = rows
	return (u % 1.0, 1.0 - (lo + (hi - lo) * (v % 1.0)) / TEX)


def build():
	bm = bmesh.new()
	uv_layer = bm.loops.layers.uv.new("UVMap")
	rng = random.Random(SEED + 1)

	def tube(p0, p1, r, rows, segs=6):
		axis = (p1 - p0).normalized()
		side = axis.cross(Vector((0, 0, 1)))
		if side.length < 1e-4:
			side = axis.cross(Vector((1, 0, 0)))
		side.normalize()
		up = axis.cross(side).normalized()
		ra = []; rb = []
		for i in range(segs):
			a = i / segs * math.tau
			off = (side * math.cos(a) + up * math.sin(a)) * r
			ra.append(bm.verts.new(p0 + off)); rb.append(bm.verts.new(p1 + off))
		for i in range(segs):
			f = bm.faces.new((ra[i], ra[(i + 1) % segs], rb[(i + 1) % segs], rb[i]))
			f.smooth = False
			for loop, (u, v) in zip(f.loops, [(0, i / segs), (0, (i + 1) / segs), (1, (i + 1) / segs), (1, i / segs)]):
				loop[uv_layer].uv = uv_rows(rows, u, v)

	def stone(center, r):
		# an icosphere squashed and jittered: no primitive-readable silhouette
		verts = []
		t = (1 + 5 ** 0.5) / 2
		base = [(-1, t, 0), (1, t, 0), (-1, -t, 0), (1, -t, 0), (0, -1, t), (0, 1, t), (0, -1, -t), (0, 1, -t), (t, 0, -1), (t, 0, 1), (-t, 0, -1), (-t, 0, 1)]
		faces = [(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8), (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)]
		sx, sy, sz = rng.uniform(0.85, 1.15), rng.uniform(0.8, 1.1), rng.uniform(0.75, 1.0)
		for b in base:
			v = Vector(b).normalized() * r * rng.uniform(0.9, 1.1)
			verts.append(bm.verts.new(center + Vector((v.x * sx, v.y * sy, v.z * sz))))
		for k, (a, b, c) in enumerate(faces):
			f = bm.faces.new((verts[a], verts[b], verts[c]))
			f.smooth = False
			u = (k % 5) * 0.2
			for loop, (uu, vv) in zip(f.loops, [(u, 0.2), (u + 0.15, 0.8), (u + 0.2, 0.3)]):
				loop[uv_layer].uv = uv_rows(STONE_ROWS, uu, vv)

	knot = Vector((0, 0, 0))
	stone(knot, 0.016)
	for i in range(3):
		a = i / 3 * math.tau
		foot = Vector((math.cos(a) * SPLAY, math.sin(a) * SPLAY, -CORD_LEN))
		tube(knot, foot, CORD_R, CORD_ROWS)
		stone(foot - Vector((0, 0, STONE_R * 0.8)), STONE_R)

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

	PixelRoughness "0.900"
	TextureColor "materials/weapons/bola_basic.png"
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
	print(f"[bola_basic] verts={len(obj.data.vertices)} faces={len(obj.data.polygons)} dims={dims.x:.3f} x {dims.y:.3f} x {dims.z:.3f} m")


main()
