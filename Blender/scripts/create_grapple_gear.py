"""Grapple gear: the forearm gauntlet launcher, the hook head and one rope segment (basic_hook).

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_grapple_gear.py"

Writes, under Assets/models/tools/grapple_gear/:
  grapple_gear.fbx              three meshes: grapple_gauntlet, grapple_hook, grapple_rope_segment
  grapple_gauntlet.vmdl         bracer + launcher, worn on the LEFT forearm
  grapple_hook.vmdl             three-prong hook head on the end of the rope
  grapple_rope_segment.vmdl     1 m of rope; PlayerAnimation.Grapple.cs chains and stretches copies of it
  grapple_gear.png / .vmat      256 x 256 atlas (leather, dark housing metal, iron, brass, coiled rope), pixel_lit
  grapple_rope.png / .vmat      64 x 64 tiling three-strand twist, pixel_lit
Blender metres, exported 1:1; the vmdls' import_scale 0.4 gives 40 u/m.

Conventions (read by PlayerAnimation.Grapple.cs - no per-item angles):
  * gauntlet: engine +X = up the forearm toward the elbow, engine +Z = the outer side of the forearm (launcher side),
    origin ON THE FOREARM CENTRE LINE at the middle of the bracer. The rope leaves at MUZZLE (hand end, launcher side);
    the same numbers are GrappleMuzzle*Meters in the code.
  * hook: engine +X = the way it flies (point first), origin at the rope eye at its tail; HOOK_LENGTH tail to point.
  * rope segment: engine +X along the rope, origin at its start, ROPE_RADIUS thick, 1 m long, v repeats ROPE_TWISTS.
Modelled with those axes on Blender +X / +Z; the export turns the meshes -90 deg about Z like the shield does
(Blender +X lands on engine +Y otherwise).

Look: a stitched leather bracer that tapers to the wrist, two iron bands, and on the outer side a wedge housing with
a rope drum at the elbow end and a short barrel toward the hand. Faceted (flat shading), every ring jittered so no
two faces are the same size.
"""
import bpy, bmesh, math, os, struct, zlib
import numpy as np
from mathutils import Vector, Matrix

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
MODEL_REL = "models/tools/grapple_gear"
MODEL_DIR = os.path.join(ROOT, "Assets", *MODEL_REL.split("/"))
FBX = "grapple_gear"
SEED = 19

# Forearm radius the bracer wraps (m): wrist end -> elbow end. The pawn's forearm is about 0.26 m long.
ARM_R_WRIST = 0.047
ARM_R_ELBOW = 0.060
X_WRIST = -0.095
X_ELBOW = 0.085
MUZZLE = (-0.128, 0.0, 0.076)     # where the rope leaves (x toward the hand, z out from the arm centre line)
HOOK_LENGTH = 0.15
ROPE_RADIUS = 0.02
ROPE_TWISTS = 4.0                 # texture repeats per metre of rope

# ---------------------------------------------------------------- textures
TEX = 256
REGIONS = {                       # x0, y0, w, h in texels (image row 0 = top)
	"leather": (0, 0, 128, 128),
	"housing": (128, 0, 128, 128),
	"iron": (0, 128, 128, 128),
	"brass": (128, 128, 64, 64),
	"coil": (192, 128, 64, 64),
	"stitch": (128, 192, 128, 64),
}
LEATHER = (92, 60, 38)
HOUSING = (58, 62, 70)
IRON = (104, 106, 112)
BRASS = (168, 128, 62)
ROPE_TONES = [(176, 150, 104), (150, 124, 82), (104, 84, 56)]


def value_noise(rng, h, w, cells):
	"""Smooth 0..1 noise: bilinear upsample of a cells x cells random grid."""
	grid = rng.random((cells + 1, cells + 1))
	ys = np.linspace(0, cells, h, endpoint=False)
	xs = np.linspace(0, cells, w, endpoint=False)
	y0 = np.floor(ys).astype(int); x0 = np.floor(xs).astype(int)
	fy = (ys - y0)[:, None]; fx = (xs - x0)[None, :]
	a = grid[y0][:, x0]; b = grid[y0][:, x0 + 1]; c = grid[y0 + 1][:, x0]; d = grid[y0 + 1][:, x0 + 1]
	return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def fill(img, region, tone, noise, amount):
	x0, y0, w, h = REGIONS[region]
	img[y0:y0 + h, x0:x0 + w, :3] = np.array(tone, float)[None, None, :] + (noise[:h, :w, None] - 0.5) * amount


def make_atlas():
	rng = np.random.default_rng(SEED)
	img = np.zeros((TEX, TEX, 4), np.float64)
	img[..., 3] = 255

	# Leather: warm brown, a slow mottle and a few darker scuffs.
	n = value_noise(rng, 128, 128, 7)
	fill(img, "leather", LEATHER, n, 22.0)
	scuff = value_noise(rng, 128, 128, 22)
	x0, y0, w, h = REGIONS["leather"]
	img[y0:y0 + h, x0:x0 + w, :3] -= (scuff > 0.74)[:, :, None] * 12.0

	# Stitched leather edge: the same leather with a row of pale stitches (used on the bracer's two end rings).
	fill(img, "stitch", LEATHER, value_noise(rng, 64, 128, 6), 18.0)
	x0, y0, w, h = REGIONS["stitch"]
	img[y0:y0 + h, x0:x0 + w, :3] -= 10.0
	for col in range(4, w, 12):
		img[y0 + 28:y0 + 34, x0 + col:x0 + col + 6, :3] = (188, 170, 132)

	# Housing: dark blued steel with worn, slightly lighter patches.
	n = value_noise(rng, 128, 128, 9)
	fill(img, "housing", HOUSING, n, 20.0)
	x0, y0, w, h = REGIONS["housing"]
	img[y0:y0 + h, x0:x0 + w, :3] += (n > 0.7)[:, :, None] * 14.0

	# Iron bands: dull grey with dents and a little rust warmth.
	n = value_noise(rng, 128, 128, 10)
	fill(img, "iron", IRON, n, 26.0)
	x0, y0, w, h = REGIONS["iron"]
	img[y0:y0 + h, x0:x0 + w, :3] += (n > 0.8)[:, :, None] * np.array((16.0, 4.0, -10.0))

	# Brass: warm yellow metal, darker in the hollows.
	n = value_noise(rng, 64, 64, 6)
	fill(img, "brass", BRASS, n, 30.0)

	# Coiled rope on the drum: wraps as bands across the region (v runs across the wraps).
	x0, y0, w, h = REGIONS["coil"]
	n = value_noise(rng, 64, 64, 12)
	for row in range(h):
		f = (row % 8) / 8.0
		tone = ROPE_TONES[2] if f < 0.2 else ROPE_TONES[1] if f < 0.45 else ROPE_TONES[0]
		img[y0 + row, x0:x0 + w, :3] = np.array(tone, float)[None, :] + (n[row, :w, None] - 0.5) * 14.0

	return np.clip(img, 0, 255).astype(np.uint8)


def make_rope():
	"""64 x 64, tiles both ways: three strands across u, wound once round per tile of v."""
	rng = np.random.default_rng(SEED + 5)
	size = 64
	img = np.zeros((size, size, 4), np.float64)
	img[..., 3] = 255
	for y in range(size):
		for x in range(size):
			f = ((x + 0.5) / size * 3.0 + (y + 0.5) / size) % 1.0
			tone = ROPE_TONES[2] if f < 0.16 else ROPE_TONES[1] if f < 0.42 else ROPE_TONES[0]
			img[y, x, :3] = np.array(tone, float) + (rng.random() - 0.5) * 10.0
	return np.clip(img, 0, 255).astype(np.uint8)


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


# ---------------------------------------------------------------- mesh builder
class Builder:
	def __init__(self, seed):
		self.bm = bmesh.new()
		self.uv = self.bm.loops.layers.uv.new("UVMap")
		self.rng = np.random.default_rng(seed)

	def face(self, pts, uvs):
		verts = [self.bm.verts.new(Vector(p)) for p in pts]
		try:
			f = self.bm.faces.new(verts)
		except ValueError:
			return None
		for loop, uv in zip(f.loops, uvs):
			loop[self.uv].uv = uv
		return f

	def flat(self, pts, region):
		"""One face in one region: a random spot in it, spread a little so point sampling picks neighbouring texels."""
		x0, y0, w, h = REGIONS[region]
		px = x0 + 6 + self.rng.random() * (w - 12)
		py = y0 + 6 + self.rng.random() * (h - 12)
		uvs = [((px + (self.rng.random() - 0.5) * 8.0) / TEX, 1.0 - (py + (self.rng.random() - 0.5) * 8.0) / TEX) for _ in pts]
		return self.face(pts, uvs)

	def strip(self, pts, region, u0, u1):
		"""A quad mapped across a region: u along its width (u0..u1 of the region), v its full height."""
		x0, y0, w, h = REGIONS[region]
		ua = (x0 + 2 + u0 * (w - 4)) / TEX
		ub = (x0 + 2 + u1 * (w - 4)) / TEX
		va = 1.0 - (y0 + 2) / TEX
		vb = 1.0 - (y0 + h - 2) / TEX
		return self.face(pts, [(ua, va), (ub, va), (ub, vb), (ua, vb)])

	def box(self, corners, region):
		"""corners indexed x*4 + y*2 + z (any hexahedron)."""
		for q in ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)):
			self.flat([corners[i] for i in q], region)

	def prism(self, p0, p1, r0, r1, sides, region, up=(0.0, 0.0, 1.0), cap0=None, cap1=None, phase=0.0):
		"""A tapered n-sided prism from p0 to p1. Caps are optional regions."""
		p0 = Vector(p0); p1 = Vector(p1)
		axis = (p1 - p0).normalized()
		side = axis.cross(Vector(up))
		if side.length < 1e-4:
			side = axis.cross(Vector((0.0, 1.0, 0.0)))
		side.normalize()
		other = axis.cross(side).normalized()
		ring = lambda p, r: [tuple(p + (side * math.cos(phase + 2 * math.pi * i / sides) + other * math.sin(phase + 2 * math.pi * i / sides)) * r) for i in range(sides)]
		a = ring(p0, r0); b = ring(p1, r1)
		for i in range(sides):
			j = (i + 1) % sides
			self.flat([a[i], a[j], b[j], b[i]], region)
		if cap0:
			self.flat(a[::-1], cap0)
		if cap1:
			self.flat(b, cap1)
		return a, b

	def to_object(self, name):
		bmesh.ops.remove_doubles(self.bm, verts=self.bm.verts, dist=1e-6)
		bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
		me = bpy.data.meshes.new(name)
		self.bm.to_mesh(me)
		self.bm.free()
		me.update()
		obj = bpy.data.objects.new(name, me)
		bpy.context.scene.collection.objects.link(obj)
		return obj


def arm_radius(x):
	t = (x - X_WRIST) / (X_ELBOW - X_WRIST)
	return ARM_R_WRIST + (ARM_R_ELBOW - ARM_R_WRIST) * t


def ring_at(x, radius, angles, jitter, rng, skew=0.0):
	"""Ring of points around the forearm axis (X). Angle 0 = +Z (outer side), turning toward +Y."""
	pts = []
	for a in angles:
		r = radius * (1.0 + (rng.random() - 0.5) * jitter)
		pts.append((x + skew * math.cos(a), math.sin(a) * r, math.cos(a) * r))
	return pts


def build_gauntlet():
	b = Builder(SEED + 1)
	rng = b.rng
	# Uneven angles round the arm (9 sides): narrow panels under the launcher, wider ones underneath.
	angles = [math.radians(d) for d in (-28, 26, 72, 118, 158, 201, 243, 288, 322)]
	n = len(angles)
	xs = [X_WRIST, -0.052, -0.004, 0.047, X_ELBOW]
	skews = [-0.006, 0.0, 0.0, 0.0, 0.008]      # the cuff ends are cut on a slight slant
	wall = 0.007
	outer = [ring_at(x, arm_radius(x) + wall, angles, 0.07, rng, s) for x, s in zip(xs, skews)]
	inner = [ring_at(x, arm_radius(x) - 0.002, angles, 0.0, rng, s) for x, s in zip(xs, skews)]

	# Outer leather, inner lining (faces the arm), and a stitched lip at each end.
	for k in range(len(xs) - 1):
		for i in range(n):
			j = (i + 1) % n
			b.flat([outer[k][i], outer[k][j], outer[k + 1][j], outer[k + 1][i]], "leather")
			b.flat([inner[k][j], inner[k][i], inner[k + 1][i], inner[k + 1][j]], "leather")
	for k in (0, len(xs) - 1):
		for i in range(n):
			j = (i + 1) % n
			b.strip([outer[k][i], outer[k][j], inner[k][j], inner[k][i]], "stitch", i / n, (i + 1) / n)

	# Two iron bands, different widths, standing proud of the leather.
	for xc, width in ((-0.068, 0.020), (0.060, 0.028)):
		lift = 0.006
		a = ring_at(xc - width * 0.5, arm_radius(xc - width * 0.5) + wall + lift, angles, 0.03, rng)
		c = ring_at(xc + width * 0.5, arm_radius(xc + width * 0.5) + wall + lift, angles, 0.03, rng)
		a_in = ring_at(xc - width * 0.5, arm_radius(xc - width * 0.5) + wall - 0.002, angles, 0.0, rng)
		c_in = ring_at(xc + width * 0.5, arm_radius(xc + width * 0.5) + wall - 0.002, angles, 0.0, rng)
		for i in range(n):
			j = (i + 1) % n
			b.flat([a[i], a[j], c[j], c[i]], "iron")
			b.flat([a_in[i], a_in[j], a[j], a[i]], "iron")
			b.flat([c[i], c[j], c_in[j], c_in[i]], "iron")

	# Launcher housing on the outer side: a wedge, tall at the elbow end, low toward the hand, narrower in front.
	z0 = arm_radius(0.0) + wall - 0.004
	hx0, hx1 = -0.058, 0.078
	corners = [
		(hx0, -0.024, z0), (hx0, -0.021, 0.090),
		(hx0, 0.024, z0), (hx0, 0.021, 0.090),
		(hx1, -0.031, z0 + 0.004), (hx1, -0.028, 0.104),
		(hx1, 0.031, z0 + 0.004), (hx1, 0.028, 0.104),
	]
	b.box(corners, "housing")

	# Rope drum across the elbow end of the housing: coiled rope between two brass cheeks.
	dc = Vector((0.050, 0.0, 0.106))
	b.prism(dc + Vector((0, -0.030, 0)), dc + Vector((0, 0.030, 0)), 0.029, 0.029, 7, "coil", up=(1.0, 0.0, 0.0), phase=0.3)
	for sy in (-1.0, 1.0):
		b.prism(dc + Vector((0, sy * 0.030, 0)), dc + Vector((0, sy * 0.037, 0)), 0.035, 0.032, 7, "brass",
				up=(1.0, 0.0, 0.0), cap0="brass", cap1="brass", phase=0.3)

	# Barrel toward the hand: tapered, with a brass muzzle ring. The hook's shaft sits inside it.
	mz = Vector(MUZZLE)
	b.prism((-0.050, 0.0, 0.078), tuple(mz + Vector((0.012, 0, 0))), 0.022, 0.017, 6, "iron", phase=0.5)
	b.prism(tuple(mz + Vector((0.012, 0, 0))), tuple(mz), 0.021, 0.019, 6, "brass", cap1="housing", phase=0.5)

	# Winding lever on the thumb side and a low sight fin on top.
	b.box([(-0.012, 0.027, 0.070), (-0.012, 0.027, 0.084), (-0.012, 0.040, 0.072), (-0.012, 0.040, 0.082),
		   (0.034, 0.029, 0.072), (0.034, 0.029, 0.090), (0.034, 0.043, 0.076), (0.034, 0.043, 0.086)], "brass")
	b.box([(-0.040, -0.004, 0.088), (-0.040, -0.004, 0.099), (-0.040, 0.004, 0.088), (-0.040, 0.004, 0.099),
		   (0.000, -0.006, 0.092), (0.000, -0.006, 0.108), (0.000, 0.006, 0.092), (0.000, 0.006, 0.108)], "iron")
	return b.to_object("grapple_gauntlet")


def build_hook():
	"""Point along +X, rope eye at the origin. Three prongs swept back from the neck, each a different length."""
	b = Builder(SEED + 2)
	b.prism((0.0, 0, 0), (0.012, 0, 0), 0.013, 0.013, 6, "brass", cap0="brass", cap1="brass")    # eye collar
	b.prism((0.012, 0, 0), (0.108, 0, 0), 0.008, 0.010, 6, "iron")                               # shaft
	b.prism((0.108, 0, 0), (HOOK_LENGTH, 0, 0), 0.013, 0.0015, 6, "iron", cap0="iron")           # point
	for k, (reach, back) in enumerate(((0.056, 0.030), (0.050, 0.038), (0.060, 0.026))):
		a = math.radians(90 + 120 * k)
		out = Vector((0.0, math.cos(a), math.sin(a)))
		root = Vector((0.104, 0, 0)) + out * 0.006
		knee = Vector((0.082, 0, 0)) + out * (reach * 0.72)
		tip = Vector((back, 0, 0)) + out * reach
		b.prism(tuple(root), tuple(knee), 0.008, 0.0065, 4, "iron", up=tuple(out), phase=0.785)
		b.prism(tuple(knee), tuple(tip), 0.0065, 0.0012, 4, "iron", up=tuple(out), phase=0.785, cap0="iron")
	return b.to_object("grapple_hook")


def build_rope_segment():
	"""1 m of rope along +X from the origin: six sides, u once round, v ROPE_TWISTS times along."""
	bm = bmesh.new()
	uv = bm.loops.layers.uv.new("UVMap")
	sides = 6
	for i in range(sides):
		a0 = 2 * math.pi * i / sides
		a1 = 2 * math.pi * (i + 1) / sides
		p = lambda x, a: Vector((x, math.cos(a) * ROPE_RADIUS, math.sin(a) * ROPE_RADIUS))
		f = bm.faces.new([bm.verts.new(p(0.0, a0)), bm.verts.new(p(0.0, a1)), bm.verts.new(p(1.0, a1)), bm.verts.new(p(1.0, a0))])
		for loop, t in zip(f.loops, ((i / sides, 0.0), ((i + 1) / sides, 0.0), ((i + 1) / sides, ROPE_TWISTS), (i / sides, ROPE_TWISTS))):
			loop[uv].uv = t
	bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
	me = bpy.data.meshes.new("grapple_rope_segment")
	bm.to_mesh(me)
	bm.free()
	me.update()
	obj = bpy.data.objects.new("grapple_rope_segment", me)
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
								from = "{mat}.vmat"
								to = "{rel}/{mat}.vmat"
							}},
						]
						use_global_default = false
						global_default_material = "{rel}/{mat}.vmat"
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
						filename = "{rel}/{fbx}.fbx"
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
{{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "{rough}"
	TextureColor "{rel}/{name}.png"
}}
"""

# Blender +X lands on the engine's +Y; this quarter turn puts the long axis (+X here) on engine +X.
EXPORT_YAW = Matrix.Rotation(math.radians(-90.0), 4, 'Z')


def main():
	for o in list(bpy.data.objects):
		bpy.data.objects.remove(o, do_unlink=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0
	os.makedirs(MODEL_DIR, exist_ok=True)

	write_png(os.path.join(MODEL_DIR, "grapple_gear.png"), make_atlas())
	write_png(os.path.join(MODEL_DIR, "grapple_rope.png"), make_rope())
	for name, rough in (("grapple_gear", "0.700"), ("grapple_rope", "0.900")):
		with open(os.path.join(MODEL_DIR, name + ".vmat"), "w", newline="\n") as f:
			f.write(VMAT.format(rel=MODEL_REL, name=name, rough=rough))

	gear_mat = bpy.data.materials.new("grapple_gear")
	rope_mat = bpy.data.materials.new("grapple_rope")
	objects = [(build_gauntlet(), gear_mat), (build_hook(), gear_mat), (build_rope_segment(), rope_mat)]
	for obj, mat in objects:
		obj.data.materials.append(mat)
		obj.data.transform(EXPORT_YAW)
		obj.data.update()

	for o in bpy.data.objects:
		o.select_set(False)
	for obj, _ in objects:
		obj.select_set(True)
	bpy.context.view_layer.objects.active = objects[0][0]
	bpy.ops.export_scene.fbx(filepath=os.path.join(MODEL_DIR, FBX + ".fbx"), use_selection=True,
							 global_scale=1.0, apply_unit_scale=True, object_types={'MESH'},
							 mesh_smooth_type='OFF', path_mode='STRIP', embed_textures=False)
	for obj, mat in objects:
		with open(os.path.join(MODEL_DIR, obj.name + ".vmdl"), "w", newline="\n") as f:
			f.write(VMDL.format(name=obj.name, rel=MODEL_REL, fbx=FBX, mat=mat.name))
		d = obj.dimensions
		print(f"[grapple_gear] {obj.name}: verts={len(obj.data.vertices)} faces={len(obj.data.polygons)} dims={d.x:.3f} x {d.y:.3f} x {d.z:.3f} m")


main()
