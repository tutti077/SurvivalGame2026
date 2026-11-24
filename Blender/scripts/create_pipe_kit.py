"""
The pipe systems kit - the first cyber layer over the wilderness (Mark 2026-11-19: "scarce metal pipe systems, that look
like they carry stuff from point A to point B ... 3-5 meters off the ground and about as thick as the player is tall ...
one end pinched and destroyed (crushed soda can look) ... the other goes deep underground into a cement square").

    pipe_straight_8m / pipe_straight_4m   1.8 m pipe run: flange collar at the start joint, an orange marking band,
                                          a cable conduit along the side, a riveted patch plate (lod0)
    pipe_bend_45        lobster-back elbow, bends 45 deg DOWN (toward local -Z); rolled 180 deg it bends up
    pipe_support        saddle + two splayed legs + braces; legs run 9 m below the pipe axis so they sink into any ground
    pipe_footing        the concrete pad a support stands in (origin at ground level)
    pipe_ground_entry   the cracked concrete collar where the pipe meets the ground (origin on the pipe axis)
    pipe_crushed_end    5 m of pipe flattened, twisted and pinched shut (no collar: the next piece's collar covers the joint)
    pipe_cement_cube    1 m stub + sleeve into a plain 5 m cement cube

Piece convention (what Code/World/Pipes/PipeRoutePlanner.cs chains): the START JOINT is the origin, the pipe runs along
local +X, Z up. Every piece but the crushed end carries the collar of its start joint; a joint may kink up to ~10 deg
and the collar hides the mitre gap.

Authored in ENGINE metres and rotated -90 deg about Z on export (this FBX convention lands Blender X on engine Y - see
create_camp_props.py). vmdls import at 0.4 (40 u/m). LODs switch at 100 / 200 / 300 m (400 / 800 / 1200); the physics
mesh is lod1. Textures: 256 px over 2 m - pipe_metal, pipe_band, pipe_steel, pipe_concrete; a few low-contrast tones
each, no dither / speckle.

Writes Assets/{models,materials,prefabs}/environment/cyber/pipes/ and one render per model plus an assembled preview
to PIPE_RENDER_DIR (default Blender/renders/pipes).

Run:  "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_pipe_kit.py
"""
import bpy
import bmesh
import json
import math
import os
import random
import uuid
from mathutils import Vector, Matrix

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ASSETS = os.path.join(ROOT, "Assets")
MAT_DIR = "materials/environment/cyber/pipes"
MODEL_DIR = "models/environment/cyber/pipes"
PREFAB_DIR = "prefabs/environment/cyber/pipes"
RENDER_DIR = os.environ.get("PIPE_RENDER_DIR", os.path.join(ROOT, "Blender", "renders", "pipes"))
VERSION = "v1"
TEX_PX = 256
TILE_M = 2.0
LOD_THRESHOLDS = (0.0, 400.0, 800.0, 1200.0)
LOD_SIDES = (12, 8, 6, 6)

R = 0.9            # pipe radius: 1.8 m across, as thick as the player is tall
COLLAR_R = 1.07
COLLAR_HALF = 0.16
BEND_R = 2.4       # elbow centreline radius
BEND_DEG = 45.0
U_TILES = 3.0      # texture repeats round the pipe (5.65 m of circumference)

EXPORT_ROT = Matrix.Rotation(math.radians(-90.0), 4, 'Z')

X = Vector((1, 0, 0))
Y = Vector((0, 1, 0))
Z = Vector((0, 0, 1))


# ----------------------------------------------------------------------------------------------------------- textures

def periodic_noise(seed, cells):
	"""Tileable value noise sampler over the unit square."""
	rng = random.Random(seed)
	grid = [[rng.random() for _ in range(cells)] for _ in range(cells)]

	def at(u, v):
		x = u * cells
		y = v * cells
		x0 = int(math.floor(x))
		y0 = int(math.floor(y))
		fx = x - x0
		fy = y - y0
		fx = fx * fx * (3 - 2 * fx)
		fy = fy * fy * (3 - 2 * fy)
		g = lambda i, j: grid[j % cells][i % cells]
		return (g(x0, y0) * (1 - fx) + g(x0 + 1, y0) * fx) * (1 - fy) + (g(x0, y0 + 1) * (1 - fx) + g(x0 + 1, y0 + 1) * fx) * fy
	return at


def blank(tone):
	return [[list(tone) for _ in range(TEX_PX)] for _ in range(TEX_PX)]


def put(px, x, y, tone):
	px[y % TEX_PX][x % TEX_PX] = list(tone)


def save_png(name, px):
	img = bpy.data.images.new(name, TEX_PX, TEX_PX, alpha=False)
	flat = []
	for y in range(TEX_PX):
		for x in range(TEX_PX):
			c = px[y][x]
			flat.extend((c[0], c[1], c[2], 1.0))
	img.pixels = flat
	path = os.path.join(ASSETS, MAT_DIR.replace("/", os.sep), name + ".png")
	os.makedirs(os.path.dirname(path), exist_ok=True)
	img.filepath_raw = path
	img.file_format = 'PNG'
	img.save()
	return img


def blotch_fill(px, tones, seed, cells=5, cuts=(0.38, 0.62)):
	"""Large soft-edged patches of 3 tones from low-frequency noise (hard tone steps, no gradients)."""
	n = periodic_noise(seed, cells)
	n2 = periodic_noise(seed + 1, cells * 2)
	for y in range(TEX_PX):
		for x in range(TEX_PX):
			v = n(x / TEX_PX, y / TEX_PX) * 0.7 + n2(x / TEX_PX, y / TEX_PX) * 0.3
			put(px, x, y, tones[0] if v < cuts[0] else tones[1] if v < cuts[1] else tones[2])


def tex_metal(rng):
	"""Painted gunmetal plate: three close blue-grey tones in big patches, plate seams every metre along the pipe (v) with
	staggered butt seams, and a few rust runs trailing round the pipe (u) from the seams."""
	tones = ((0.305, 0.345, 0.365), (0.325, 0.365, 0.385), (0.35, 0.388, 0.405))
	seam = (0.2, 0.225, 0.24)
	rust = (0.4, 0.29, 0.21)
	px = blank(tones[1])
	blotch_fill(px, tones, rng.randrange(1 << 30), cells=3, cuts=(0.4, 0.6))
	for row in (0, 128):
		for x in range(TEX_PX):
			put(px, x, row, seam)
			put(px, x, row + 1, tones[2])
	for row, xs in ((0, (40, 168)), (128, (104, 232))):
		for x0 in xs:
			for y in range(row, row + 128):
				put(px, x0, y, seam)
	for _ in range(7):
		y0 = rng.choice((2, 130)) + rng.randrange(0, 90)
		x0 = rng.randrange(TEX_PX)
		length = rng.randrange(26, 70)
		width = rng.choice((2, 3, 3, 4))
		for i in range(length):
			w = max(1, width - (i * width) // length)
			for k in range(w):
				put(px, x0 + i, y0 + k, rust)
	return px


def tex_band(rng):
	"""Faded orange marking paint, chipped back to dark metal in a few big flakes."""
	tones = ((0.66, 0.37, 0.14), (0.74, 0.44, 0.17), (0.8, 0.5, 0.2))
	chip = (0.26, 0.27, 0.27)
	px = blank(tones[1])
	blotch_fill(px, tones, rng.randrange(1 << 30), cells=4)
	n = periodic_noise(rng.randrange(1 << 30), 7)
	for y in range(TEX_PX):
		for x in range(TEX_PX):
			if n(x / TEX_PX, y / TEX_PX) > 0.74:
				put(px, x, y, chip)
	return px


def tex_steel(rng):
	"""Bare structural steel gone dark, with rust-brown patches (collars, supports, the crushed end)."""
	tones = ((0.205, 0.2, 0.195), (0.228, 0.22, 0.212), (0.3, 0.225, 0.172))
	px = blank(tones[0])
	blotch_fill(px, tones, rng.randrange(1 << 30), cells=4, cuts=(0.45, 0.76))
	for row in (63, 191):
		for x in range(TEX_PX):
			put(px, x, row, (0.15, 0.148, 0.145))
	return px


def tex_concrete(rng):
	"""Board-formed cement: three warm greys in big patches, a form line every 0.25 m, two thin connected cracks."""
	tones = ((0.5, 0.495, 0.475), (0.525, 0.52, 0.497), (0.548, 0.541, 0.518))
	line = (0.41, 0.405, 0.39)
	px = blank(tones[1])
	blotch_fill(px, tones, rng.randrange(1 << 30), cells=3)
	for row in range(0, TEX_PX, 32):
		for x in range(TEX_PX):
			if (x // 48 + row // 32) % 5 != 0:   # the line breaks here and there
				put(px, x, row, line)
	for _ in range(2):
		x = rng.randrange(TEX_PX)
		y = rng.randrange(TEX_PX)
		dx = rng.choice((-1, 1))
		for _ in range(rng.randrange(120, 200)):
			put(px, x, y, (0.33, 0.325, 0.315))
			step = rng.random()
			if step < 0.55:
				y += 1
			elif step < 0.85:
				x += dx
			else:
				x += dx
				y += 1
	return px


TEXTURES = (("pipe_metal", tex_metal, 0.55, 0.45), ("pipe_band", tex_band, 0.8, 0.3),
			("pipe_steel", tex_steel, 0.75, 0.35), ("pipe_concrete", tex_concrete, 0.97, 0.1))


def write_vmat(name, roughness, specular):
	text = f"""// Pipe systems kit (Blender/scripts/create_pipe_kit.py): 256 px point-filtered, faceted, UVs authored at 2 m per tile.
Layer0
{{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "{roughness:.3f}"
	PixelSpecular "{specular:.3f}"
	PixelFlatShading "1.000"
	TextureColor "{MAT_DIR}/{name}.png"
}}
"""
	path = os.path.join(ASSETS, MAT_DIR.replace("/", os.sep), name + ".vmat")
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


_materials = {}


def get_material(name):
	if name in _materials:
		return _materials[name]
	mat = bpy.data.materials.new(name)
	mat.use_nodes = True
	img = bpy.data.images.get(name)
	if img is not None:
		bsdf = mat.node_tree.nodes.get("Principled BSDF")
		tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
		tex.image = img
		tex.interpolation = 'Closest'
		mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
		bsdf.inputs["Roughness"].default_value = 0.85
	_materials[name] = mat
	return mat


# ------------------------------------------------------------------------------------------------------------ builder

MATERIALS = ["pipe_metal", "pipe_band", "pipe_steel", "pipe_concrete"]
METAL, BAND, STEEL, CONCRETE = 0, 1, 2, 3


class Builder:
	"""One mesh; faces carry a material index and UVs already in tiles."""

	def __init__(self, name):
		self.name = name
		self.bm = bmesh.new()
		self.uv = self.bm.loops.layers.uv.new("UVMap")

	def v(self, p):
		return self.bm.verts.new(p)

	def face(self, verts, mat, uvs=None):
		try:
			f = self.bm.faces.new(verts)
		except ValueError:
			return None
		f.material_index = mat
		f.smooth = False
		if uvs is None:
			# Planar along the face's own axes, in metres
			n = f.normal if f.normal.length > 0 else Z
			f.normal_update()
			n = f.normal
			ax = Y if abs(n.x) >= abs(n.y) and abs(n.x) >= abs(n.z) else X
			ay = n.cross(ax).normalized()
			ax = ay.cross(n).normalized()
			uvs = [(vv.co.dot(ax) / TILE_M, vv.co.dot(ay) / TILE_M) for vv in f.verts]
		for loop, uv in zip(f.loops, uvs):
			loop[self.uv].uv = uv
		return f

	def ring(self, centre, n1, n2, radius, sides, wide=1.0, thin=1.0, bump=None, phase=0.0):
		"""Vertices round `centre` in the (n1, n2) plane; the first sits half a facet past n1 so 12 / 8 sides put a flat on top."""
		out = []
		for j in range(sides):
			a = (math.pi / sides) + (2 * math.pi * (j + phase) / sides)
			k = bump(j, a) if bump else 1.0
			out.append(self.v(centre + n1 * (math.cos(a) * radius * wide * k) + n2 * (math.sin(a) * radius * thin * k)))
		return out

	def loft(self, a, b, mat, va, vb, u_tiles=U_TILES):
		n = len(a)
		for j in range(n):
			k = (j + 1) % n
			self.face((a[j], a[k], b[k], b[j]), mat,
					  ((u_tiles * j / n, va), (u_tiles * (j + 1) / n, va), (u_tiles * (j + 1) / n, vb), (u_tiles * j / n, vb)))

	def loft_tris(self, a, b, mat, va, vb, half, u_tiles=U_TILES):
		"""Between two rings where `b` sits half a facet round from `a` (`half` = +1) or back (-1): triangles, the
		diamond buckles of a crushed can."""
		n = len(a)
		for j in range(n):
			k = (j + 1) % n
			u0, u1 = u_tiles * j / n, u_tiles * (j + 1) / n
			um = u_tiles * (j + 0.5) / n
			if half > 0:
				self.face((a[j], a[k], b[j]), mat, ((u0, va), (u1, va), (um, vb)))
				self.face((b[j], a[k], b[k]), mat, ((um, vb), (u1, va), (um + u_tiles / n, vb)))
			else:
				self.face((a[j], b[k], b[j]), mat, ((um, va), (u1, vb), (u0, vb)))
				self.face((a[j], a[k], b[k]), mat, ((um, va), (um + u_tiles / n, va), (u1, vb)))

	def cap(self, ring, mat, flip=False):
		self.face(tuple(reversed(ring)) if flip else tuple(ring), mat)

	def collar(self, centre, t, n1, n2, sides, r_in=R - 0.02, r_out=COLLAR_R, half=COLLAR_HALF, mat=STEEL):
		"""A flange: two annulus faces and the rim between them."""
		a_in = self.ring(centre - t * half, n1, n2, r_in, sides)
		a_out = self.ring(centre - t * half, n1, n2, r_out, sides)
		b_out = self.ring(centre + t * half, n1, n2, r_out, sides)
		b_in = self.ring(centre + t * half, n1, n2, r_in, sides)
		self.loft(a_in, a_out, mat, 0.0, 0.1)
		self.loft(a_out, b_out, mat, 0.1, 0.1 + (2 * half) / TILE_M)
		self.loft(b_out, b_in, mat, 0.3, 0.4)

	def beam(self, p0, p1, w, h, mat, side=None):
		"""A box from p0 to p1, w across (`side`) and h the other way."""
		t = (p1 - p0).normalized()
		s = side if side is not None else (t.cross(Z) if abs(t.z) < 0.95 else Y)
		s = (s - t * s.dot(t)).normalized()
		u = t.cross(s).normalized()
		length = (p1 - p0).length
		c = lambda p, a, b: self.v(p + s * (a * w / 2) + u * (b * h / 2))
		q0 = [c(p0, -1, -1), c(p0, 1, -1), c(p0, 1, 1), c(p0, -1, 1)]
		q1 = [c(p1, -1, -1), c(p1, 1, -1), c(p1, 1, 1), c(p1, -1, 1)]
		dims = (w, h, w, h)
		for j in range(4):
			k = (j + 1) % 4
			self.face((q0[j], q0[k], q1[k], q1[j]), mat,
					  ((0, 0), (dims[j] / TILE_M, 0), (dims[j] / TILE_M, length / TILE_M), (0, length / TILE_M)))
		self.face(tuple(reversed(q0)), mat)
		self.face(tuple(q1), mat)

	def box(self, lo, hi, mat, rng=None, jitter=0.0):
		pts = {}
		for sx in (0, 1):
			for sy in (0, 1):
				for sz in (0, 1):
					p = Vector((hi.x if sx else lo.x, hi.y if sy else lo.y, hi.z if sz else lo.z))
					if rng and jitter > 0:
						p += Vector((rng.uniform(-jitter, jitter), rng.uniform(-jitter, jitter), rng.uniform(-jitter, jitter)))
					pts[(sx, sy, sz)] = self.v(p)
		for quad in (((0, 0, 0), (0, 1, 0), (0, 1, 1), (0, 0, 1)), ((1, 1, 0), (1, 0, 0), (1, 0, 1), (1, 1, 1)),
					 ((0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1)), ((1, 1, 0), (0, 1, 0), (0, 1, 1), (1, 1, 1)),
					 ((0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1)), ((0, 1, 0), (1, 1, 0), (1, 0, 0), (0, 0, 0))):
			self.face([pts[q] for q in quad], mat)

	def finish(self):
		bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
		mesh = bpy.data.meshes.new(self.name)
		self.bm.to_mesh(mesh)
		self.bm.free()
		for m in MATERIALS:
			mesh.materials.append(get_material(m))
		for p in mesh.polygons:
			p.use_smooth = False
		obj = bpy.data.objects.new(self.name, mesh)
		bpy.context.scene.collection.objects.link(obj)
		return obj


# ------------------------------------------------------------------------------------------------------------- pieces

def run_of_pipe(b, x0, x1, sides, mat=METAL, radius=R):
	a = b.ring(X * x0, Y, Z, radius, sides)
	c = b.ring(X * x1, Y, Z, radius, sides)
	b.loft(a, c, mat, x0 / TILE_M, x1 / TILE_M)
	return a, c


def build_straight(name, length, lod, seed):
	rng = random.Random(seed)
	sides = LOD_SIDES[lod]
	b = Builder(name)
	a, c = run_of_pipe(b, 0.0, length, sides)
	b.cap(a, METAL, flip=True)
	b.cap(c, METAL)
	if lod < 3:
		b.collar(Vector((0, 0, 0)), X, Y, Z, sides)
		# The orange marking band, off-centre so a run of pieces does not read as a ruler
		bx = length * 0.58
		bw = 0.3
		ba = b.ring(X * (bx - bw), Y, Z, R + 0.035, sides)
		bb = b.ring(X * (bx + bw), Y, Z, R + 0.035, sides)
		b.loft(ba, bb, BAND, 0.0, (2 * bw) / TILE_M)
		b.loft(b.ring(X * (bx - bw), Y, Z, R - 0.01, sides), ba, BAND, 0.0, 0.02)
		b.loft(bb, b.ring(X * (bx + bw), Y, Z, R - 0.01, sides), BAND, 0.0, 0.02)
	if lod < 2:
		# Cable conduit along the upper side, with two junction boxes
		ang = math.radians(52.0)
		out = Y * math.cos(ang) + Z * math.sin(ang)
		p = out * (R + 0.07)
		b.beam(p + X * 0.3, p + X * (length - 0.3), 0.12, 0.12, STEEL, side=out.cross(X))
		for jx in (length * 0.27, length * 0.8):
			b.beam(p + out * 0.04 + X * (jx - 0.22), p + out * 0.04 + X * (jx + 0.22), 0.3, 0.2, STEEL, side=out.cross(X))
	if lod == 0:
		# A riveted patch plate over two facets on the far side
		j = sides // 2 + 1
		x0 = length * 0.18
		x1 = x0 + 1.3
		pts = []
		for jj in (j, j + 1, j + 2):
			a_ = (math.pi / sides) + (2 * math.pi * jj / sides)
			pts.append(Y * (math.cos(a_) * (R + 0.04)) + Z * (math.sin(a_) * (R + 0.04)))
		for k in range(2):
			b.face((b.v(pts[k] + X * x0), b.v(pts[k + 1] + X * x0), b.v(pts[k + 1] + X * x1), b.v(pts[k] + X * x1)), STEEL)
		# Flange bolts
		for k in range(8):
			a_ = 2 * math.pi * (k + 0.5) / 8
			o = (Y * math.cos(a_) + Z * math.sin(a_)) * (R + 0.09)
			b.beam(o - X * (COLLAR_HALF + 0.07), o + X * (COLLAR_HALF + 0.07), 0.07, 0.07, STEEL, side=o.normalized().cross(X))
	_ = rng
	return b.finish()


def bend_frame(a):
	"""Centre, tangent and the in-plane normal of the elbow's centreline at angle `a` (radians): bends toward -Z."""
	centre = Vector((BEND_R * math.sin(a), 0.0, -BEND_R * (1 - math.cos(a))))
	t = Vector((math.cos(a), 0.0, -math.sin(a)))
	n2 = Vector((math.sin(a), 0.0, math.cos(a)))
	return centre, t, n2


def build_bend(name, lod):
	sides = LOD_SIDES[lod]
	gores = (3, 3, 2, 1)[lod]
	b = Builder(name)
	total = math.radians(BEND_DEG)
	rings = []
	for k in range(gores + 1):
		centre, t, n2 = bend_frame(total * k / gores)
		rings.append(b.ring(centre, Y, n2, R, sides))
	for k in range(gores):
		v0 = BEND_R * total * k / gores / TILE_M
		v1 = BEND_R * total * (k + 1) / gores / TILE_M
		b.loft(rings[k], rings[k + 1], METAL, v0, v1)
	b.cap(rings[0], METAL, flip=True)
	b.cap(rings[-1], METAL)
	if lod < 3:
		b.collar(Vector((0, 0, 0)), X, Y, Z, sides)
	if lod < 2:
		# Weld bands on the gore seams
		for k in range(1, gores):
			centre, t, n2 = bend_frame(total * k / gores)
			wa = b.ring(centre - t * 0.07, Y, n2, R + 0.03, sides)
			wb = b.ring(centre + t * 0.07, Y, n2, R + 0.03, sides)
			b.loft(wa, wb, STEEL, 0.0, 0.07)
	return b.finish()


def bend_end():
	"""Where the elbow ends (local) and how far it pitched (degrees, downward)."""
	centre, _, _ = bend_frame(math.radians(BEND_DEG))
	return centre, BEND_DEG


def build_support(name, lod):
	b = Builder(name)
	# Saddle: a cradle arc under the pipe
	half_w = 0.3
	arc = (5, 4, 3, 2)[lod]
	lo, hi = math.radians(205.0), math.radians(335.0)
	for k in range(arc):
		a0 = lo + (hi - lo) * k / arc
		a1 = lo + (hi - lo) * (k + 1) / arc
		d0 = Y * math.cos(a0) + Z * math.sin(a0)
		d1 = Y * math.cos(a1) + Z * math.sin(a1)
		ri, ro = R + 0.01, R + 0.16
		inner = [d0 * ri - X * half_w, d1 * ri - X * half_w, d1 * ri + X * half_w, d0 * ri + X * half_w]
		outer = [d0 * ro - X * half_w, d1 * ro - X * half_w, d1 * ro + X * half_w, d0 * ro + X * half_w]
		vi = [b.v(p) for p in inner]
		vo = [b.v(p) for p in outer]
		b.face(vi, STEEL)
		b.face(tuple(reversed(vo)), STEEL)
		b.face((vi[0], vi[1], vo[1], vo[0]), STEEL)
		b.face((vi[3], vi[2], vo[2], vo[3]), STEEL)
		if k == 0:
			b.face((vi[0], vi[3], vo[3], vo[0]), STEEL)
		if k == arc - 1:
			b.face((vi[1], vi[2], vo[2], vo[1]), STEEL)
	# Legs: splay outward on the way down, 9 m long so the feet are always under ground
	top_z = -0.72
	foot_z = -9.0
	for s in (-1, 1):
		top = Vector((0.0, s * 0.82, top_z))
		foot = Vector((0.0, s * 1.3, foot_z))
		b.beam(top, foot, 0.32, 0.32, STEEL, side=X)
	# Cap beam under the saddle
	b.beam(Vector((0, -1.05, -1.08)), Vector((0, 1.05, -1.08)), 0.3, 0.22, STEEL, side=X)
	if lod < 3:
		# Lower tie
		b.beam(Vector((0, -1.02, -3.4)), Vector((0, 1.02, -3.4)), 0.16, 0.16, STEEL, side=X)
	if lod < 2:
		# X brace between the cap beam and the tie
		b.beam(Vector((0.14, -0.9, -1.25)), Vector((0.14, 0.98, -3.3)), 0.1, 0.1, STEEL, side=X)
		b.beam(Vector((-0.14, 0.9, -1.25)), Vector((-0.14, -0.98, -3.3)), 0.1, 0.1, STEEL, side=X)
	return b.finish()


def build_footing(name, lod, seed):
	rng = random.Random(seed)
	b = Builder(name)
	b.box(Vector((-0.75, -1.85, -0.9)), Vector((0.75, 1.85, 0.28)), CONCRETE, rng, 0.05 if lod < 2 else 0.0)
	if lod < 2:
		# Two plinths the legs stand in
		for s in (-1, 1):
			b.box(Vector((-0.42, s * 1.15 - 0.42, 0.24)), Vector((0.42, s * 1.15 + 0.42, 0.52)), CONCRETE, rng, 0.03)
	return b.finish()


def build_ground_entry(name, lod, seed):
	"""A thick concrete collar round the pipe axis (X): chamfered square outside, round hole inside, edges knocked about."""
	rng = random.Random(seed)
	b = Builder(name)
	half = 0.75
	o = 1.75   # half size of the square
	ch = 0.45  # corner chamfer
	outline = [(o, -o + ch), (o, o - ch), (o - ch, o), (-o + ch, o), (-o, o - ch), (-o, -o + ch), (-o + ch, -o), (o - ch, -o)]
	hole_r = R + 0.06
	jit = 0.07 if lod < 2 else 0.0

	def outer(x):
		return [b.v(Vector((x + rng.uniform(-jit, jit), y + rng.uniform(-jit, jit), z + rng.uniform(-jit, jit)))) for y, z in outline]

	def inner(x):
		out = []
		for k in range(8):
			a = math.radians(-22.5 + 45.0 * k)   # vertex k sits between outline points so the annulus quads stay planar-ish
			out.append(b.v(Vector((x, math.cos(a) * hole_r, math.sin(a) * hole_r))))
		return out
	a_out, b_out = outer(-half), outer(half)
	a_in, b_in = inner(-half), inner(half)
	b.loft(a_out, b_out, CONCRETE, 0.0, (2 * half) / TILE_M, u_tiles=6.0)
	for k in range(8):
		n = (k + 1) % 8
		b.face((a_in[k], a_in[n], a_out[n], a_out[k]), CONCRETE)
		b.face((b_out[k], b_out[n], b_in[n], b_in[k]), CONCRETE)
	b.loft(b_in, a_in, CONCRETE, 0.0, (2 * half) / TILE_M)
	return b.finish()


def build_crushed(name, lod, seed):
	"""The pipe crushed like a soda can and pinched shut: a metre of sound pipe, then rings of diamond buckles (each ring
	half a facet round from the last, every other one pulled in) that flatten, sag and twist into a folded-over seam -
	nothing to look into."""
	rng = random.Random(seed)
	sides = LOD_SIDES[lod]
	b = Builder(name)
	start = 1.0
	count = (9, 9, 5, 5)[lod]
	rows = []   # x, ring
	rows.append((0.0, b.ring(Vector((0, 0, 0)), Y, Z, R, sides)))
	rows.append((start, b.ring(Vector((start, 0, 0)), Y, Z, R, sides)))
	for k in range(1, count + 1):
		t = k / count
		x = start + (3.5 * t)
		thin = 1.0 + (0.07 - 1.0) * (t ** 0.85)
		wide = 1.0 + 0.3 * math.sin(t * math.pi / 2)
		pulled = 1.0 - (0.17 * (1.0 - 0.4 * t) if k % 2 == 1 else 0.0)
		phi = math.radians(24.0) * t
		e1 = Y * math.cos(phi) + Z * math.sin(phi)
		e2 = Z * math.cos(phi) - Y * math.sin(phi)
		ring = b.ring(Vector((x, 0, -0.3 * t * t)), e1, e2, R * pulled, sides, wide=wide, thin=thin, phase=0.5 if k % 2 == 1 else 0.0)
		for vtx in ring:
			vtx.co += Vector((rng.uniform(-0.04, 0.04), rng.uniform(-0.03, 0.03), rng.uniform(-0.03, 0.03)))
		rows.append((x, ring))
	# The seam: folded down and over, edge torn
	phi = math.radians(30.0)
	e1 = Y * math.cos(phi) + Z * math.sin(phi)
	e2 = (Z * math.cos(phi) - Y * math.sin(phi)) * 0.6 + X * 0.8   # the flat tips forward as it folds
	tip = b.ring(Vector((4.85, 0, -0.62)), e1, e2.normalized(), R, sides, wide=1.22, thin=0.035, phase=0.5 if (count + 1) % 2 == 1 else 0.0)
	for vtx in tip:
		vtx.co += Vector((rng.uniform(-0.05, 0.12), 0.0, rng.uniform(-0.08, 0.05)))
	rows.append((4.85, tip))
	paint = 2 if lod < 2 else 1   # past here the paint is gone where the metal folded
	b.loft(rows[0][1], rows[1][1], METAL, 0.0, start / TILE_M)
	for i in range(1, len(rows) - 1):
		half = 1 if i % 2 == 1 else -1
		b.loft_tris(rows[i][1], rows[i + 1][1], METAL if i <= paint else STEEL, rows[i][0] / TILE_M, rows[i + 1][0] / TILE_M, half)
	b.cap(rows[0][1], METAL, flip=True)
	b.cap(rows[-1][1], STEEL)
	return b.finish()


def build_cube(name, lod, seed):
	rng = random.Random(seed)
	sides = LOD_SIDES[lod]
	b = Builder(name)
	stub = 1.0
	a, c = run_of_pipe(b, 0.0, stub + 0.1, sides)
	b.cap(a, METAL, flip=True)
	if lod < 3:
		b.collar(Vector((0, 0, 0)), X, Y, Z, sides)
		# Cast sleeve where the pipe enters the wall
		b.collar(X * (stub - 0.12), X, Y, Z, 8, r_in=R - 0.02, r_out=R + 0.42, half=0.14, mat=CONCRETE)
	b.box(Vector((stub, -2.5, -2.5)), Vector((stub + 5.0, 2.5, 2.5)), CONCRETE, rng, 0.03 if lod < 2 else 0.0)
	return b.finish()


# -------------------------------------------------------------------------------------------------------------- export

def export_fbx(objs, rel_path):
	path = os.path.join(ASSETS, rel_path.replace("/", os.sep))
	os.makedirs(os.path.dirname(path), exist_ok=True)
	for o in bpy.data.objects:
		o.select_set(False)
	for o in objs:
		o.data.transform(EXPORT_ROT)
		o.select_set(True)
	bpy.context.view_layer.objects.active = objs[0]
	bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={'MESH'}, apply_unit_scale=True,
							 global_scale=1.0, axis_forward='-Z', axis_up='Y', mesh_smooth_type='FACE',
							 add_leaf_bones=False, bake_anim=False, path_mode='STRIP', embed_textures=False)
	for o in objs:
		o.data.transform(EXPORT_ROT.inverted())


def mesh_file_block(cls, fbx, mesh, extra=""):
	return f"""					{{
						_class = "{cls}"
						name = "{mesh}{'_physics' if cls == 'PhysicsMeshFile' else ''}"
{extra}						filename = "{fbx}"
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
							exception_list = [ "{mesh}" ]
						}}
					}},
"""


def write_vmdl(rel_vmdl, fbx, meshes, surface, note):
	"""meshes = [lod0 .. lod3] mesh names in `fbx`; the physics mesh is lod1."""
	remaps = "".join(f"""						{{
							from = "{m}.vmat"
							to = "{MAT_DIR}/{m}.vmat"
						}},
""" for m in MATERIALS)
	renders = "".join(mesh_file_block("RenderMeshFile", fbx, m) for m in meshes)
	physics = mesh_file_block("PhysicsMeshFile", fbx, meshes[1], extra=f"""						surface_prop = "{surface}"
						collision_tags = "solid"
						recenter_on_parent_bone = false
						offset_origin = [ 0.0, 0.0, 0.0 ]
						offset_angles = [ 0.0, 0.0, 0.0 ]
						maxMeshVertices = 0
						qemError = 0.0
""")
	groups = "".join(f"""					{{
						_class = "LODGroup"
						switch_threshold = {LOD_THRESHOLDS[k]:.1f}
						meshes = [ "{m}" ]
					}},
""" for k, m in enumerate(meshes))
	text = f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
// {note} - Blender/scripts/create_pipe_kit.py
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
{remaps}						]
						use_global_default = false
						global_default_material = "{MAT_DIR}/pipe_metal.vmat"
					}},
				]
			}},
			{{
				_class = "PhysicsShapeList"
				children =
				[
{physics}				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
{renders}				]
			}},
			{{
				_class = "LODGroupList"
				children =
				[
{groups}				]
			}},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}}
}}
"""
	path = os.path.join(ASSETS, rel_vmdl.replace("/", os.sep))
	os.makedirs(os.path.dirname(path), exist_ok=True)
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


def guid(*parts):
	return str(uuid.uuid5(uuid.NAMESPACE_URL, "survivalgame2026/" + "/".join(parts)))


def write_prefab(name, model):
	"""A static world piece: root [ModelRenderer, ModelCollider (static)], tag `pipe`."""
	rel = f"{PREFAB_DIR}/{name}.prefab"
	renderer = {"__type": "Sandbox.ModelRenderer", "__guid": guid(rel, "renderer"), "__enabled": True, "Flags": 0,
				"BodyGroups": 18446744073709551615, "CreateAttachments": False, "LodOverride": None, "MaterialGroup": None,
				"MaterialOverride": None, "Materials": None, "Model": model,
				"RenderOptions": {"GameLayer": True, "OverlayLayer": False, "BloomLayer": False, "AfterUILayer": False},
				"RenderType": "On", "Tint": "1,1,1,1"}
	collider = {"__type": "Sandbox.ModelCollider", "__guid": guid(rel, "collider"), "__enabled": True, "Flags": 0,
				"IsTrigger": False, "Model": model, "Static": True, "Surface": None}
	root = {"__guid": guid(rel), "__version": 2, "Flags": 0, "Name": name, "Position": "0,0,0", "Rotation": "0,0,0,1",
			"Scale": "1,1,1", "Tags": "pipe", "Enabled": True, "NetworkMode": 1, "NetworkFlags": 0, "NetworkOrphaned": 0,
			"NetworkTransmit": True, "OwnerTransfer": 1, "Components": [renderer, collider], "Children": [],
			"__properties": {"NetworkInterpolation": True, "TimeScale": 1, "WantsSystemScene": True, "Metadata": {},
							 "NavMesh": {"Enabled": False, "IncludeStaticBodies": True, "IncludeKeyframedBodies": True,
										 "EditorAutoUpdate": False, "AgentHeight": 64, "AgentRadius": 16, "AgentStepSize": 18,
										 "AgentMaxSlope": 40, "ExcludedBodies": "", "IncludedBodies": "", "DeferGeneration": False,
										 "CustomBounds": False}},
			"__variables": []}
	doc = {"RootObject": root, "ResourceVersion": 2, "ShowInMenu": False, "MenuPath": None, "MenuIcon": None,
		   "DontBreakAsTemplate": False, "__references": [], "__version": 2}
	path = os.path.join(ASSETS, rel.replace("/", os.sep))
	os.makedirs(os.path.dirname(path), exist_ok=True)
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		json.dump(doc, f, indent=2)


# ------------------------------------------------------------------------------------------------------------- render

def setup_render():
	scene = bpy.context.scene
	scene.render.engine = 'BLENDER_EEVEE'
	scene.render.resolution_x = 1000
	scene.render.resolution_y = 640
	world = bpy.data.worlds.new("w")
	world.use_nodes = True
	world.node_tree.nodes["Background"].inputs[0].default_value = (0.42, 0.5, 0.56, 1.0)
	scene.world = world
	sun = bpy.data.lights.new("sun", 'SUN')
	sun.energy = 4.0
	sun.angle = math.radians(4.0)
	so = bpy.data.objects.new("sun", sun)
	scene.collection.objects.link(so)
	so.rotation_euler = (math.radians(52.0), 0.0, math.radians(-40.0))
	cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
	scene.collection.objects.link(cam)
	scene.camera = cam


def render(objs, tag, yaw_deg=-52.0, elev_deg=18.0, margin=1.25, lens=50.0):
	"""Frame `objs` (everything else hidden) from a three-quarter view."""
	scene = bpy.context.scene
	cam = scene.camera
	cam.data.lens = lens
	cam.data.clip_end = 2000.0
	for o in bpy.data.objects:
		if o.type == 'MESH':
			o.hide_render = o not in objs
	pts = [o.matrix_world @ v.co for o in objs for v in o.data.vertices]
	lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
	hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
	centre = (lo + hi) * 0.5
	radius = max((p - centre).length for p in pts)
	fov = 2 * math.atan(min(36.0, 36.0 * 640 / 1000) / (2 * lens))
	dist = radius * margin / math.sin(fov / 2)
	a = math.radians(yaw_deg)
	e = math.radians(elev_deg)
	cam.location = centre + Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e))) * dist
	cam.rotation_euler = (centre - cam.location).to_track_quat('-Z', 'Y').to_euler()
	os.makedirs(RENDER_DIR, exist_ok=True)
	scene.render.filepath = os.path.join(RENDER_DIR, f"{tag}.png")
	bpy.ops.render.render(write_still=True)


def instance(src, matrix, bucket):
	o = bpy.data.objects.new(src.name + "_i", src.data)
	bpy.context.scene.collection.objects.link(o)
	o.matrix_world = matrix
	bucket.append(o)
	return o


def assemble(models):
	"""A short system the way the planner chains it: crushed end, 45 deg rise, level run on supports, 45 deg dive, cube.
	Also the check that the piece conventions fit together."""
	out = []
	pos = Vector((0.0, 0.0, -2.4))
	pitch = math.radians(45.0)   # positive = climbing
	rot = lambda p, roll=0.0: Matrix.Rotation(-p, 4, 'Y') @ Matrix.Rotation(roll, 4, 'X')
	place = lambda src, p, r: instance(src, Matrix.Translation(p) @ r, out)
	fwd = lambda p: Vector((math.cos(p), 0.0, math.sin(p)))
	end_local, end_pitch = bend_end()

	place(models["pipe_crushed_end"], pos, Matrix.Rotation(math.pi, 4, 'Z') @ rot(pitch))   # points back down the line
	place(models["pipe_straight_8m"], pos, rot(pitch))
	entry_a = pos + fwd(pitch) * (2.4 / math.sin(pitch))
	place(models["pipe_ground_entry"], entry_a, rot(pitch))
	pos = pos + fwd(pitch) * 8.0
	place(models["pipe_bend_45"], pos, rot(pitch))
	pos = pos + (rot(pitch) @ end_local)
	pitch -= math.radians(end_pitch)
	for k in range(4):
		place(models["pipe_straight_8m"], pos, rot(pitch))
		if k % 2 == 0:
			place(models["pipe_support"], pos + fwd(pitch) * 4.0, Matrix.Identity(4))
			place(models["pipe_footing"], Vector((pos.x + 4.0, 0.0, 0.0)), Matrix.Identity(4))
		pos = pos + fwd(pitch) * 8.0
	place(models["pipe_bend_45"], pos, rot(pitch))
	pos = pos + (rot(pitch) @ end_local)
	pitch -= math.radians(end_pitch)
	for k in range(4):
		place(models["pipe_straight_8m"], pos, rot(pitch))
		if k == 0:
			place(models["pipe_ground_entry"], pos + fwd(pitch) * (pos.z / math.sin(-pitch)), rot(pitch))
		pos = pos + fwd(pitch) * 8.0
	place(models["pipe_bend_45"], pos, rot(pitch, math.pi))   # rolled over: bends back up to level
	pos = pos + (rot(pitch, math.pi) @ end_local)
	pitch += math.radians(end_pitch)
	place(models["pipe_cement_cube"], pos, rot(pitch))
	return out


# ---------------------------------------------------------------------------------------------------------------- main

PIECES = [  # name, builder(lod) -> object, surface, note, (yaw, elev) of its render
	("pipe_straight_8m", lambda n, lod: build_straight(n, 8.0, lod, 11), "metal", "pipe run, 8 m", (-58.0, 16.0)),
	("pipe_straight_4m", lambda n, lod: build_straight(n, 4.0, lod, 12), "metal", "pipe run, 4 m", (-58.0, 16.0)),
	("pipe_bend_45", lambda n, lod: build_bend(n, lod), "metal", "45 degree elbow (bends toward -Z)", (-75.0, 12.0)),
	("pipe_support", lambda n, lod: build_support(n, lod), "metal", "pipe support trestle (origin on the pipe axis)", (-25.0, 10.0)),
	("pipe_footing", lambda n, lod: build_footing(n, lod, 21), "concrete", "support footing (origin at ground level)", (-35.0, 24.0)),
	("pipe_ground_entry", lambda n, lod: build_ground_entry(n, lod, 31), "concrete", "ground entry collar (origin on the pipe axis)", (-40.0, 18.0)),
	("pipe_crushed_end", lambda n, lod: build_crushed(n, lod, 41), "metal", "crushed, pinched-off pipe end", (-62.0, 22.0)),
	("pipe_cement_cube", lambda n, lod: build_cube(n, lod, 51), "concrete", "cement cube the deep end leads into", (-128.0, 16.0)),
]


def main():
	bpy.ops.wm.read_factory_settings(use_empty=True)
	rng = random.Random(1911)
	for name, fn, rough, spec in TEXTURES:
		save_png(name, fn(rng))
		write_vmat(name, rough, spec)
	setup_render()

	lod0 = {}
	for name, build, surface, note, view in PIECES:
		full = f"{name}_{VERSION}"
		objs = []
		for lod in range(4):
			o = build(f"{full}_lod{lod}", lod)
			o.data.name = o.name
			objs.append(o)
		fbx = f"{MODEL_DIR}/{full}.fbx"
		export_fbx(objs, fbx)
		write_vmdl(f"{MODEL_DIR}/{full}.vmdl", fbx, [o.name for o in objs], surface, note)
		write_prefab(full, f"{MODEL_DIR}/{full}.vmdl")
		lod0[name] = objs[0]
		tris = [sum(len(p.vertices) - 2 for p in o.data.polygons) for o in objs]
		xs = [v.co for v in objs[0].data.vertices]
		size = [max(getattr(c, ax) for c in xs) - min(getattr(c, ax) for c in xs) for ax in "xyz"]
		print(f"[pipes] {full}: tris {tris}, {size[0]:.2f} x {size[1]:.2f} x {size[2]:.2f} m")
		render([objs[0]], full, yaw_deg=view[0], elev_deg=view[1])

	system = assemble(lod0)
	render(system, "pipe_system_preview", yaw_deg=-78.0, elev_deg=8.0, margin=1.05)
	end_local, _ = bend_end()
	print(f"[pipes] bend end local {end_local.x:.4f}, {end_local.y:.4f}, {end_local.z:.4f}")
	print(f"[pipes] renders in {RENDER_DIR}")


main()
