"""Low-poly round wooden shield for the offhand slot (shield_wood).

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_shield_wood.py"

Writes Assets/models/weapons/shield_wood.fbx + .vmdl and Assets/materials/weapons/shield_wood.png + .vmat
(256 x 256, pixel_lit). Blender metres, exported 1:1; the vmdl's import_scale 0.4 gives 40 u/m.

Shield model convention (read by PlayerAnimation.Shield.cs - no per-item angles):
  * engine +Z = the outward face normal (boss side), engine +X = "up" the forearm toward the elbow,
  * the model origin sits ON THE FOREARM CENTRE LINE behind the grip (0.045 m behind the back plane),
    so the code can put the origin on the arm and the shield lands flat on it.
Modelled here with up = Blender +X; the export turns the mesh -90 deg about Z like the buggy does
(Blender +X lands on engine +Y otherwise).

Geometry: seven planks of varied width running up the forearm axis, a gentle dome, iron rim + eight
rivets, a domed iron boss, and behind: a wooden grip bar on two posts (hand side, -X) and a leather
forearm strap (+X). Faceted (flat shading), planar-mapped planks so the painted seams line up with
the geometry seams.
"""
import bpy, bmesh, math, os, struct, zlib
import numpy as np
from mathutils import Vector, Matrix

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
MODEL_DIR = os.path.join(ROOT, "Assets", "models", "weapons")
MAT_DIR = os.path.join(ROOT, "Assets", "materials", "weapons")
MODEL_REL = "models/weapons"
MAT_REL = "materials/weapons"
NAME = "shield_wood"

R = 0.34           # plank disc radius (m)
RIM = 0.025        # rim width beyond the disc
T = 0.022          # plank thickness
DOME = 0.035       # extra height of the dome at the centre
BOSS_R = 0.075
BOSS_H = 0.06
ARM_R = 0.045      # forearm radius: the origin sits this far behind the back plane
SEED = 7

# Plank boundaries across the disc (along Y, planks run along X): varied widths.
PLANK_EDGES = [-R, -0.235, -0.13, -0.045, 0.05, 0.145, 0.245, R]

# ---------------------------------------------------------------- texture (256 px, four regions)
TEX = 256
PLANK_PX = 160          # planks: [0,160) x [0,160)   (planar map of the face, u along the planks)
# right column (x 160..255): metal rows 0..95, leather rows 96..159, dark grip wood rows 160..255
WOOD_TONES = [(128, 92, 58), (116, 80, 50), (139, 102, 66), (108, 76, 48)]
METAL = (96, 98, 104)
LEATHER = (84, 54, 34)
GRIP = (82, 58, 36)


def value_noise(rng, h, w, cells):
	"""Smooth 0..1 noise: bilinear upsample of a cells x cells random grid."""
	grid = rng.random((cells + 1, cells + 1))
	ys = np.linspace(0, cells, h, endpoint=False)
	xs = np.linspace(0, cells, w, endpoint=False)
	y0 = np.floor(ys).astype(int); x0 = np.floor(xs).astype(int)
	fy = (ys - y0)[:, None]; fx = (xs - x0)[None, :]
	a = grid[y0][:, x0]; b = grid[y0][:, x0 + 1]; c = grid[y0 + 1][:, x0]; d = grid[y0 + 1][:, x0 + 1]
	return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def make_texture():
	rng = np.random.default_rng(SEED)
	img = np.zeros((TEX, TEX, 4), np.float64)
	img[..., 3] = 255

	# Planks: the face is planar-mapped u = X (along the plank), v = Y (across). Image row 0 is the
	# top, and cell_uv flips v, so row = (1 - (y/R+1)/2) * PLANK_PX.
	grain = value_noise(rng, PLANK_PX, PLANK_PX, 6)          # slow tone drift
	streak = value_noise(rng, PLANK_PX, PLANK_PX * 1, 28)     # finer along-plank streaks
	for row in range(PLANK_PX):
		y = (1.0 - (row + 0.5) / PLANK_PX) * 2.0 * R - R       # plank-space Y of this texel row
		plank = 0
		for i in range(len(PLANK_EDGES) - 1):
			if PLANK_EDGES[i] <= y < PLANK_EDGES[i + 1]:
				plank = i
				break
		tone = np.array(WOOD_TONES[plank % len(WOOD_TONES)], float)
		for col in range(PLANK_PX):
			g = (grain[row, col] - 0.5) * 18.0 + (streak[row, (col * 3) % PLANK_PX] - 0.5) * 10.0
			c = tone + g
			# a few darker grain lines running along the plank (constant row, broken by the streak)
			if (row * 7 + plank * 3) % 11 == 0 and streak[row, col] > 0.45:
				c -= 14.0
			img[row, col, :3] = c
	# seams: one dark texel row at every plank boundary
	for e in PLANK_EDGES[1:-1]:
		row = int(round((1.0 - (e + R) / (2.0 * R)) * PLANK_PX))
		row = min(max(row, 0), PLANK_PX - 1)
		img[row, :PLANK_PX, :3] -= 40.0
		if row + 1 < PLANK_PX:
			img[row + 1, :PLANK_PX, :3] -= 26.0

	# Metal: dull iron with dents and a little rust warmth.
	m = value_noise(rng, 96, 96, 8)
	base = np.array(METAL, float)
	for row in range(96):
		for col in range(96):
			c = base + (m[row, col] - 0.5) * 26.0
			if m[row, col] > 0.78:
				c += np.array((18.0, 6.0, -10.0))   # rust-warm spots
			img[row, PLANK_PX + col, :3] = c
	# Leather: warm brown with a soft mottle.
	l = value_noise(rng, 64, 96, 6)
	for row in range(64):
		for col in range(96):
			img[96 + row, PLANK_PX + col, :3] = np.array(LEATHER, float) + (l[row, col] - 0.5) * 20.0
	# Grip wood: darker, finer grain along u.
	g2 = value_noise(rng, 96, 96, 20)
	for row in range(96):
		for col in range(96):
			img[160 + row, PLANK_PX + col, :3] = np.array(GRIP, float) + (g2[row, col] - 0.5) * 16.0

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


def plank_uv(x, y):
	"""Planar map of a face point onto the plank region (u along X, v along Y; image row 0 = top)."""
	u = ((x / R) + 1.0) * 0.5 * (PLANK_PX / TEX)
	v = 1.0 - (1.0 - ((y / R) + 1.0) * 0.5) * (PLANK_PX / TEX)
	return (min(max(u, 0.0), PLANK_PX / TEX), min(max(v, 1.0 - PLANK_PX / TEX), 1.0))


def region_uv(rng, region):
	"""A random point inside the metal / leather / grip region (whole-face flat colour with variation)."""
	x0, y0, w, h = {
		"metal": (PLANK_PX, 0, 96, 96),
		"leather": (PLANK_PX, 96, 96, 64),
		"grip": (PLANK_PX, 160, 96, 96),
	}[region]
	px = x0 + 2 + rng.random() * (w - 4)
	py = y0 + 2 + rng.random() * (h - 4)
	return (px / TEX, 1.0 - py / TEX)


# ---------------------------------------------------------------- mesh builder
class Builder:
	def __init__(self):
		self.bm = bmesh.new()
		self.uv = self.bm.loops.layers.uv.new("UVMap")
		self.rng = np.random.default_rng(SEED + 1)

	def face(self, pts, uvs, flip=False):
		verts = [self.bm.verts.new(Vector(p)) for p in pts]
		if flip:
			verts = verts[::-1]
			uvs = uvs[::-1]
		try:
			f = self.bm.faces.new(verts)
		except ValueError:
			return None
		for loop, uv in zip(f.loops, uvs):
			loop[self.uv].uv = uv
		return f

	def flat_face(self, pts, region, flip=False):
		uv = region_uv(self.rng, region)
		# tiny spread so point sampling picks neighbouring texels, not one flat value
		uvs = [(uv[0] + (self.rng.random() - 0.5) * 0.02, uv[1] + (self.rng.random() - 0.5) * 0.02) for _ in pts]
		return self.face(pts, uvs, flip)

	def box(self, center, size, region, rot=None):
		cx, cy, cz = center
		sx, sy, sz = [s * 0.5 for s in size]
		corners = [Vector((x, y, z)) for x in (-sx, sx) for y in (-sy, sy) for z in (-sz, sz)]
		if rot is not None:
			corners = [rot @ c for c in corners]
		corners = [(c.x + cx, c.y + cy, c.z + cz) for c in corners]
		# index: x*4 + y*2 + z
		quads = [
			(0, 1, 3, 2), (4, 6, 7, 5),   # -x, +x
			(0, 4, 5, 1), (2, 3, 7, 6),   # -y, +y
			(0, 2, 6, 4), (1, 5, 7, 3),   # -z, +z
		]
		for q in quads:
			self.flat_face([corners[i] for i in q], region)

	def to_object(self, name):
		bmesh.ops.remove_doubles(self.bm, verts=self.bm.verts, dist=1e-5)
		bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
		me = bpy.data.meshes.new(name)
		self.bm.to_mesh(me)
		self.bm.free()
		me.update()
		obj = bpy.data.objects.new(name, me)
		bpy.context.scene.collection.objects.link(obj)
		return obj


def dome_z(x, y, thickness):
	r2 = (x * x + y * y) / (R * R)
	return thickness + DOME * max(0.0, 1.0 - r2)


def arc_points(y_from, y_to, side, steps_per_rad=8.0):
	"""Points on the plank circle from y_from to y_to on the +x or -x side, inclusive of both ends."""
	a0 = math.asin(max(-1.0, min(1.0, y_from / R)))
	a1 = math.asin(max(-1.0, min(1.0, y_to / R)))
	n = max(1, int(abs(a1 - a0) * steps_per_rad + 0.5))
	pts = []
	for i in range(n + 1):
		a = a0 + (a1 - a0) * i / n
		x = math.cos(a) * R * side
		pts.append((x, math.sin(a) * R))
	return pts


def build_planks(b):
	for i in range(len(PLANK_EDGES) - 1):
		y0, y1 = PLANK_EDGES[i], PLANK_EDGES[i + 1]
		# One thickness for every plank: a per-plank step left bright little walls between the planks
		# that read as pale stripes - the seams are painted into the texture instead.
		t = T
		# outline: +x arc from y0 up to y1, then -x arc from y1 back down to y0
		outline = arc_points(y0, y1, +1.0) + arc_points(y1, y0, -1.0)
		# drop near-duplicate points (top / bottom planks meet at x ~ 0)
		clean = []
		for p in outline:
			if not clean or (abs(p[0] - clean[-1][0]) > 1e-4 or abs(p[1] - clean[-1][1]) > 1e-4):
				clean.append(p)
		if abs(clean[0][0] - clean[-1][0]) < 1e-4 and abs(clean[0][1] - clean[-1][1]) < 1e-4:
			clean.pop()
		if len(clean) < 3:
			continue
		front = [(x, y, dome_z(x, y, t)) for x, y in clean]
		back = [(x, y, 0.0) for x, y in clean]
		uvs = [plank_uv(x, y) for x, y in clean]
		b.face(front, uvs)                 # face (normal +z after recalc)
		b.face(back, uvs, flip=True)       # back - the arcs sit under the rim, the plank joints are flush


def build_rim(b, segs=28):
	z_back = -0.004
	z_front_at = lambda x, y: dome_z(x, y, T) + 0.01
	for i in range(segs):
		a0 = 2 * math.pi * i / segs
		a1 = 2 * math.pi * (i + 1) / segs
		ri, ro = R - 0.004, R + RIM
		p = lambda a, r: (math.cos(a) * r, math.sin(a) * r)
		i0, i1, o0, o1 = p(a0, ri), p(a1, ri), p(a0, ro), p(a1, ro)
		zf0, zf1 = z_front_at(*i0), z_front_at(*i1)
		# front annulus
		b.flat_face([(*i0, zf0), (*o0, zf0 - 0.008), (*o1, zf1 - 0.008), (*i1, zf1)], "metal")
		# outer wall
		b.flat_face([(*o0, zf0 - 0.008), (*o0, z_back), (*o1, z_back), (*o1, zf1 - 0.008)], "metal")
		# back annulus
		b.flat_face([(*o0, z_back), (*i0, z_back), (*i1, z_back), (*o1, z_back)], "metal")
		# inner wall (visible where the planks sit lower than the rim)
		b.flat_face([(*i0, zf0), (*i1, zf1), (*i1, 0.0), (*i0, 0.0)], "metal")


def build_rivets(b, count=8):
	for i in range(count):
		a = 2 * math.pi * (i + 0.5) / count
		r = R - 0.03
		x, y = math.cos(a) * r, math.sin(a) * r
		z = dome_z(x, y, T)
		b.box((x, y, z + 0.006), (0.016, 0.016, 0.012), "metal")


def build_boss(b, segs=12, rings=3):
	zc = dome_z(0.0, 0.0, T) - 0.01
	# scale the hemisphere so it protrudes BOSS_H above the dome
	def pt(theta, phi):
		return (math.cos(theta) * math.sin(phi) * BOSS_R,
				math.sin(theta) * math.sin(phi) * BOSS_R,
				zc + math.cos(phi) * (BOSS_H + 0.01))
	cap = (0.0, 0.0, zc + BOSS_H + 0.01)
	for i in range(segs):
		t0 = 2 * math.pi * i / segs
		t1 = 2 * math.pi * (i + 1) / segs
		for k in range(rings):
			p0 = math.pi * 0.5 * k / rings
			p1 = math.pi * 0.5 * (k + 1) / rings
			if k == 0:
				b.flat_face([cap, pt(t0, p1), pt(t1, p1)], "metal")
			else:
				b.flat_face([pt(t0, p0), pt(t0, p1), pt(t1, p1), pt(t1, p0)], "metal")
	# flat collar where the boss meets the planks
	for i in range(segs):
		t0 = 2 * math.pi * i / segs
		t1 = 2 * math.pi * (i + 1) / segs
		b.flat_face([pt(t0, math.pi * 0.5), (math.cos(t0) * (BOSS_R + 0.012), math.sin(t0) * (BOSS_R + 0.012), zc + 0.004),
					 (math.cos(t1) * (BOSS_R + 0.012), math.sin(t1) * (BOSS_R + 0.012), zc + 0.004), pt(t1, math.pi * 0.5)], "metal")


def build_back(b):
	# Grip bar on the hand side (-X): across Y, standing off the back plane on two posts.
	gx = -0.12
	b.box((gx, 0.0, -0.030), (0.03, 0.17, 0.028), "grip")
	for sy in (-0.085, 0.085):
		b.box((gx, sy, -0.016), (0.03, 0.02, 0.032), "grip")
	# Forearm strap on the elbow side (+X): a U of leather hanging from the back plane around the arm.
	sx = 0.10
	w, th = 0.035, 0.006
	for sy in (-0.052, 0.052):
		b.box((sx, sy, -0.045), (w, th, 0.09), "leather")
	b.box((sx, 0.0, -0.090), (w, 0.104 + th, th), "leather")


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

	PixelRoughness "0.800"
	TextureColor "materials/weapons/shield_wood.png"
}
"""

# Blender +X lands on the engine's +Y; this quarter turn puts the shield's "up" (+X here) on engine +X.
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

	b = Builder()
	build_planks(b)
	build_rim(b)
	build_rivets(b)
	build_boss(b)
	build_back(b)
	obj = b.to_object(NAME)

	mat = bpy.data.materials.new(NAME)
	mat.use_nodes = True
	obj.data.materials.append(mat)

	# Origin on the forearm centre line (ARM_R behind the back plane), then the export yaw.
	obj.data.transform(Matrix.Translation(Vector((0.0, 0.0, ARM_R))) )
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
	print(f"[shield_wood] verts={len(obj.data.vertices)} faces={len(obj.data.polygons)} dims={dims.x:.3f} x {dims.y:.3f} x {dims.z:.3f} m")


main()
