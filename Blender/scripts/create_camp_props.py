"""
Scav camp dressing + camp furniture (Mark 2026-11-10, prompts 26 + 29):

    camp_hut_a / camp_hut_b   tiny round thatch huts the scavs hide in - wattle wall ring with a doorway, a conical
                              layered thatch roof with a tied top knot, door posts; inside faces so the interior reads.
    camp_fence_a / camp_fence_b   2 m of crappy fence: two leaning posts, two sagging rails, rope lashings; _b has a
                              snapped top rail and a post about to give up.
    campfire + campfire_embers   stone ring, three leaning logs, an ash / ember bed (the bed is what Campfire tints).
    beehive_box               the craftable hive: slatted box on legs, pitched lid, entrance slot and landing board.
    roasting_spit             two forked posts and a cranked pole across them, to stand over a campfire (Mark 2026-11-19:
                              the abandoned structures hold "a campfire thats not lit, a roasting spit"). Origin at the
                              centre of its box (build piece convention), bar along engine Y.

Authored in ENGINE metres (X forward, Y left, Z up) and rotated -90 deg about Z on export, because this FBX
convention lands Blender X on engine Y (measured on the chest, 2026-11-10). vmdls import at 0.4 (40 u/m).
Textures: 256 px over 2 m (128 texels / m): camp_thatch, camp_wattle, camp_wood (rope band in its bottom rows),
camp_embers; stones reuse rock_gunmetal. Four low-contrast tones per texture, no dither / speckle.

Writes models under models/environment/camp/ (huts, fences) and models/building/ (campfire, embers, beehive box),
materials under materials/environment/camp/, prefabs under prefabs/build/, and one render per model to
CAMP_RENDER_DIR (default Blender/renders/camp).

Run:  "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_camp_props.py
      (optional after `--`: `spit` builds only the roasting spit and leaves every other file alone)
"""
import bpy
import bmesh
import json
import math
import os
import random
import sys
import uuid
from mathutils import Vector, Matrix

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ASSETS = os.path.join(ROOT, "Assets")
MAT_DIR = "materials/environment/camp"
CAMP_MODEL_DIR = "models/environment/camp"
BUILD_MODEL_DIR = "models/building"
PREFAB_DIR = "prefabs/build"
RENDER_DIR = os.environ.get("CAMP_RENDER_DIR", os.path.join(ROOT, "Blender", "renders", "camp"))
TEX_PX = 256
TILE_M = 2.0
ROCK_VMAT = "materials/environment/cloverHills/rocks/rock_gunmetal.vmat"

EXPORT_ROT = Matrix.Rotation(math.radians(-90.0), 4, 'Z')   # engine frame -> this FBX convention


# ----------------------------------------------------------------------------------------------------------- textures

def save_png(name, px):
	n = len(px)
	img = bpy.data.images.new(name, n, n, alpha=False)
	flat = []
	for y in range(n):
		row = px[y]
		for x in range(n):
			c = row[x]
			flat.extend((c[0], c[1], c[2], 1.0))
	img.pixels = flat
	path = os.path.join(ASSETS, MAT_DIR.replace("/", os.sep), name + ".png")
	os.makedirs(os.path.dirname(path), exist_ok=True)
	img.filepath_raw = path
	img.file_format = 'PNG'
	img.save()
	return img


def blank(tone):
	return [[list(tone) for _ in range(TEX_PX)] for _ in range(TEX_PX)]


def shade(px, x, y, k):
	n = len(px)
	c = px[y % n][x % n]
	px[y % n][x % n] = [c[0] * k, c[1] * k, c[2] * k]


def put(px, x, y, tone):
	n = len(px)
	px[y % n][x % n] = list(tone)


def tex_thatch(rng):
	"""Straw in horizontal courses: vertical strands in four tones, a shadow under every course edge."""
	tones = [(0.64, 0.53, 0.30), (0.72, 0.61, 0.37), (0.56, 0.46, 0.26), (0.48, 0.39, 0.22)]
	px = blank(tones[0])
	n = TEX_PX
	course = 48
	for cy in range(0, n, course):
		offset = rng.randint(0, 6)
		x = -rng.randint(0, 3)
		while x < n + 3:
			w = rng.choice((2, 2, 3, 3, 4))
			t = tones[rng.choice((0, 0, 1, 1, 1, 2, 2, 3))]
			lean = rng.uniform(-0.08, 0.08)
			for yy in range(course + 6):
				y = cy + yy - 4 + offset
				xx = x + int(lean * yy)
				for k in range(w):
					put(px, xx + k, y, t)
			x += w
		# course edge: a 3 px shadow below the overlap line, a 1 px light rim above it
		for xx in range(n):
			y = cy + offset
			shade(px, xx, y - 1, 1.08)
			shade(px, xx, y, 0.72)
			shade(px, xx, y + 1, 0.80)
			shade(px, xx, y + 2, 0.90)
	# a few broken strands (short darker dashes)
	for i in range(40):
		x, y = rng.randint(0, n - 1), rng.randint(0, n - 1)
		for yy in range(rng.randint(4, 12)):
			shade(px, x, y + yy, 0.78)
	return px


def tex_wattle(rng):
	"""Horizontal rods woven round vertical stakes; rods alternate in front / behind each stake."""
	tones = [(0.46, 0.37, 0.26), (0.53, 0.44, 0.31), (0.39, 0.31, 0.22), (0.59, 0.50, 0.36)]
	px = blank(tones[2])
	n = TEX_PX
	rod = 7
	stake_pitch = 40
	stake_w = 4
	rows = n // rod
	for r in range(rows):
		y0 = r * rod
		t = tones[rng.choice((0, 0, 1, 1, 3))]
		wob = rng.uniform(-1.0, 1.0)
		for x in range(n):
			yy = int(wob * math.sin(x / n * 6.28 * 2.0))
			for k in range(rod):
				y = y0 + k + yy
				tone = t
				if k == 0:
					tone = (t[0] * 1.06, t[1] * 1.06, t[2] * 1.06)
				elif k == rod - 1:
					tone = (t[0] * 0.8, t[1] * 0.8, t[2] * 0.8)
				put(px, x, y, tone)
	for sx in range(0, n, stake_pitch):
		shift = rng.randint(-3, 3)
		for r in range(rows):
			y0 = r * rod
			in_front = (r + (sx // stake_pitch)) % 2 == 0
			if not in_front:
				continue
			for k in range(-2, rod + 2):
				for w in range(stake_w):
					tone = tones[2] if w not in (0, stake_w - 1) else (tones[2][0] * 0.8, tones[2][1] * 0.8, tones[2][2] * 0.8)
					put(px, sx + shift + w, y0 + k, tone)
	return px


def tex_wood(rng):
	"""Weathered grey-brown posts: long grain streaks, two knots; bottom 28 rows are the rope band."""
	tones = [(0.47, 0.42, 0.35), (0.54, 0.49, 0.42), (0.40, 0.35, 0.29), (0.60, 0.56, 0.48)]
	px = blank(tones[0])
	n = TEX_PX
	band = 28
	col_tone = []
	phase = rng.uniform(0, 6.28)
	for x in range(n):
		s = math.sin(x / n * 6.28 * 9.0 + phase) + 0.5 * math.sin(x / n * 6.28 * 23.0 + phase * 1.7)
		col_tone.append(3 if s > 1.0 else 1 if s > 0.2 else 0 if s > -0.9 else 2)
	for y in range(n - band):
		drift = int(2.0 * math.sin(y / n * 6.28 * 1.5 + phase))
		for x in range(n):
			put(px, x, y, tones[col_tone[(x + drift) % n]])
	for i in range(9):   # grain cracks
		x = rng.uniform(0, n)
		y = rng.randint(0, n - band - 1)
		for s in range(rng.randint(40, 160)):
			y += 1
			if y >= n - band:
				break
			x += rng.uniform(-0.3, 0.3)
			shade(px, int(x), y, 0.62)
	for i in range(2):   # knots
		cx, cy = rng.uniform(0, n), rng.uniform(10, n - band - 10)
		rx, ry = 3.0, 5.0
		for y in range(int(cy - ry) - 1, int(cy + ry) + 2):
			for x in range(int(cx - rx) - 1, int(cx + rx) + 2):
				d = ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2
				if 0.55 < d < 1.0:
					shade(px, x, y, 0.7)
				elif d <= 0.55:
					shade(px, x, y, 0.88)
	rope = [(0.70, 0.60, 0.42), (0.61, 0.51, 0.35), (0.76, 0.67, 0.48)]
	for y in range(n - band, n):
		for x in range(n):
			k = ((x + (y - (n - band)) * 2) // 6) % 3
			put(px, x, y, rope[k])
			if ((x + (y - (n - band)) * 2) % 6) == 0:
				shade(px, x, y, 0.72)
	return px


def tex_embers(rng):
	"""Light grey lumpy ash - the campfire tints this orange when lit, grey when out."""
	tones = [(0.78, 0.76, 0.72), (0.70, 0.68, 0.64), (0.86, 0.84, 0.80), (0.62, 0.60, 0.57)]
	px = blank(tones[0])
	n = TEX_PX
	for i in range(900):
		x, y = rng.randint(0, n - 1), rng.randint(0, n - 1)
		t = tones[rng.randint(0, 3)]
		r = rng.randint(3, 9)
		for yy in range(-r, r + 1):
			for xx in range(-r, r + 1):
				if xx * xx + yy * yy <= r * r:
					put(px, x + xx, y + yy, t)
	return px


# ----------------------------------------------------------------------------------------------------------- geometry

class Builder:
	"""One mesh per model; faces carry a material index and UVs in metres / TILE_M."""

	def __init__(self, name, materials):
		self.name = name
		self.bm = bmesh.new()
		self.uv = self.bm.loops.layers.uv.new("UVMap")
		self.materials = materials   # list of material names; face.material_index indexes it

	def mat(self, name):
		return self.materials.index(name)

	def quad(self, pts, mat, uvs):
		try:
			f = self.bm.faces.new([self.bm.verts.new(p) if not isinstance(p, bmesh.types.BMVert) else p for p in pts])
		except ValueError:
			return None
		f.material_index = mat
		f.smooth = False
		for loop, uv in zip(f.loops, uvs):
			loop[self.uv].uv = (uv[0] / TILE_M, uv[1] / TILE_M)
		return f

	def tube(self, path, radii, sides, mat, rng, cap_start=True, cap_end=True, jitter=0.1, u_scale=1.0):
		roll = rng.uniform(0, math.pi)
		rings = []
		dist = [0.0]
		for i in range(1, len(path)):
			dist.append(dist[-1] + (path[i] - path[i - 1]).length)
		for i, p in enumerate(path):
			prev = path[max(i - 1, 0)]
			nxt = path[min(i + 1, len(path) - 1)]
			t = (nxt - prev).normalized()
			up = Vector((0, 0, 1)) if abs(t.z) < 0.9 else Vector((1, 0, 0))
			n1 = t.cross(up).normalized()
			n2 = t.cross(n1).normalized()
			ring = []
			for j in range(sides):
				a = roll + 2 * math.pi * j / sides + rng.uniform(-0.2, 0.2) * (2 * math.pi / sides)
				r = radii[i] * rng.uniform(1 - jitter, 1 + jitter)
				ring.append(self.bm.verts.new(p + n1 * (math.cos(a) * r) + n2 * (math.sin(a) * r)))
			rings.append(ring)
			roll += rng.uniform(-0.1, 0.1)
		v0 = rng.uniform(0, 1.5)
		for i in range(len(rings) - 1):
			a, b = rings[i], rings[i + 1]
			ca = 2 * math.pi * radii[i] * u_scale
			cb = 2 * math.pi * radii[i + 1] * u_scale
			for j in range(sides):
				k = (j + 1) % sides
				self.quad((a[j], a[k], b[k], b[j]), mat,
						  ((ca * j / sides, v0 + dist[i]), (ca * (j + 1) / sides, v0 + dist[i]),
						   (cb * (j + 1) / sides, v0 + dist[i + 1]), (cb * j / sides, v0 + dist[i + 1])))
		if cap_start:
			f = self.bm.faces.new(tuple(reversed(rings[0])))
			f.material_index = mat
			f.smooth = False
			for loop in f.loops:
				loop[self.uv].uv = (rng.uniform(0, 0.2), rng.uniform(0, 0.2))
		if cap_end:
			f = self.bm.faces.new(tuple(rings[-1]))
			f.material_index = mat
			f.smooth = False
			for loop in f.loops:
				loop[self.uv].uv = (rng.uniform(0, 0.2), rng.uniform(0, 0.2))
		return rings

	def box(self, centre, size, mat, rng, yaw=0.0, pitch=0.0, roll=0.0, jitter=0.0):
		hx, hy, hz = size.x / 2, size.y / 2, size.z / 2
		rot = Matrix.Rotation(yaw, 4, 'Z') @ Matrix.Rotation(pitch, 4, 'Y') @ Matrix.Rotation(roll, 4, 'X')
		corners = []
		for sx in (-1, 1):
			for sy in (-1, 1):
				for sz in (-1, 1):
					c = Vector((sx * hx, sy * hy, sz * hz))
					if jitter > 0:
						c += Vector((rng.uniform(-jitter, jitter), rng.uniform(-jitter, jitter), rng.uniform(-jitter, jitter)))
					corners.append(self.bm.verts.new(centre + (rot @ c)))
		idx = lambda sx, sy, sz: ((sx + 1) // 2) * 4 + ((sy + 1) // 2) * 2 + ((sz + 1) // 2)
		u0 = rng.uniform(0, 1.0)
		faces = [
			((-1, -1, -1), (-1, 1, -1), (-1, 1, 1), (-1, -1, 1), size.y, size.z),   # -x
			((1, 1, -1), (1, -1, -1), (1, -1, 1), (1, 1, 1), size.y, size.z),       # +x
			((-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1), size.x, size.z),   # -y
			((1, 1, -1), (-1, 1, -1), (-1, 1, 1), (1, 1, 1), size.x, size.z),       # +y
			((-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1), size.x, size.y),       # +z
			((-1, 1, -1), (1, 1, -1), (1, -1, -1), (-1, -1, -1), size.x, size.y),   # -z
		]
		for a, b, c, d, w, h in faces:
			self.quad((corners[idx(*a)], corners[idx(*b)], corners[idx(*c)], corners[idx(*d)]), mat,
					  ((u0, 0), (u0 + w, 0), (u0 + w, h), (u0, h)))

	def blob(self, centre, size, mat, rng):
		"""A lumpy stone: an icosphere with every vertex pushed in or out, flattened underneath, planar-dissolved so the
		facets vary in size and nothing reads as a box or a ball."""
		tmp = bmesh.new()
		bmesh.ops.create_icosphere(tmp, subdivisions=1, radius=1.0)
		for v in tmp.verts:
			v.co = Vector((v.co.x * size.x * rng.uniform(0.78, 1.18), v.co.y * size.y * rng.uniform(0.78, 1.18), v.co.z * size.z * rng.uniform(0.8, 1.15)))
			if v.co.z < -size.z * 0.35:
				v.co.z = -size.z * 0.35
		bmesh.ops.dissolve_limit(tmp, angle_limit=math.radians(22.0), verts=tmp.verts, edges=tmp.edges)
		rot = Matrix.Rotation(rng.uniform(0, math.pi), 4, 'Z')
		verts = {}
		for v in tmp.verts:
			verts[v.index] = self.bm.verts.new(centre + (rot @ v.co))
		tmp.verts.ensure_lookup_table()
		for f in tmp.faces:
			try:
				nf = self.bm.faces.new([verts[v.index] for v in f.verts])
			except ValueError:
				continue
			nf.material_index = mat
			nf.smooth = False
			n = f.normal
			ax = Vector((0, 1, 0)) if abs(n.x) >= abs(n.y) and abs(n.x) >= abs(n.z) else Vector((1, 0, 0))
			ay = n.cross(ax).normalized()
			ax = ay.cross(n).normalized()
			for loop, v in zip(nf.loops, f.verts):
				loop[self.uv].uv = ((v.co.dot(ax) + 0.5) / TILE_M, (v.co.dot(ay) + 0.5) / TILE_M)
		tmp.free()

	def finish(self):
		bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
		mesh = bpy.data.meshes.new(self.name)
		self.bm.to_mesh(mesh)
		self.bm.free()
		for m in self.materials:
			mesh.materials.append(get_material(m))
		for p in mesh.polygons:
			p.use_smooth = False
		obj = bpy.data.objects.new(self.name, mesh)
		bpy.context.scene.collection.objects.link(obj)
		return obj


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
		bsdf.inputs["Roughness"].default_value = 0.95
	_materials[name] = mat
	return mat


# --------------------------------------------------------------------------------------------------------------- huts

def build_hut(name, seed, radius, wall_h, apex_h, overhang):
	rng = random.Random(seed)
	b = Builder(name, ["camp_wattle", "camp_thatch"])
	wattle, thatch = b.mat("camp_wattle"), b.mat("camp_thatch")
	segs = 14
	door = (0, 1)   # segment indices left open, centred on +X (engine forward)
	door_half = math.pi / segs   # half the angular width of the door opening (two segments)
	# wall ring, double sided
	ring_lo, ring_hi = [], []
	for i in range(segs + 1):
		a = -door_half + 2 * math.pi * i / segs
		r = radius * rng.uniform(0.97, 1.03)
		ring_lo.append(Vector((math.cos(a) * r, math.sin(a) * r, rng.uniform(-0.03, 0.0))))
		ring_hi.append(Vector((math.cos(a) * r, math.sin(a) * r, wall_h * rng.uniform(0.96, 1.03))))
	circ = 2 * math.pi * radius
	for i in range(segs):
		if i in door:
			continue
		u0, u1 = circ * i / segs, circ * (i + 1) / segs
		p0, p1, p2, p3 = ring_lo[i], ring_lo[i + 1], ring_hi[i + 1], ring_hi[i]
		b.quad((p0, p1, p2, p3), wattle, ((u0, 0), (u1, 0), (u1, wall_h), (u0, wall_h)))
		# inside face (a hair inward so it never z-fights)
		inset = 0.02
		q = [p * (1 - inset / radius) for p in (p0, p1, p2, p3)]
		b.quad((q[3], q[2], q[1], q[0]), wattle, ((u0, wall_h), (u1, wall_h), (u1, 0), (u0, 0)))
	# door posts + lintel
	for i in (door[0], door[1] + 1):
		a = -door_half + 2 * math.pi * i / segs
		c = Vector((math.cos(a) * radius, math.sin(a) * radius, wall_h / 2))
		b.box(c, Vector((0.11, 0.11, wall_h + 0.1)), wattle, rng, yaw=a, jitter=0.01)
	a0 = -door_half
	a1 = -door_half + 2 * math.pi * (door[1] + 1) / segs
	mid = (a0 + a1) / 2
	lintel_c = Vector((math.cos(mid) * radius, math.sin(mid) * radius, wall_h + 0.05))
	width = (Vector((math.cos(a0), math.sin(a0), 0)) - Vector((math.cos(a1), math.sin(a1), 0))).length * radius
	b.box(lintel_c, Vector((0.1, width + 0.2, 0.1)), wattle, rng, yaw=mid, jitter=0.01)
	# roof: two thatch layers (lower skirt + main cone), both double sided, rim sagging unevenly
	def cone(r_out, z_out, r_in, z_in, facets, drop):
		outer, inner = [], []
		for i in range(facets + 1):
			a = 2 * math.pi * i / facets + rng.uniform(-0.02, 0.02)
			ro = r_out * rng.uniform(0.97, 1.03)
			outer.append(Vector((math.cos(a) * ro, math.sin(a) * ro, z_out - rng.uniform(0, drop))))
			inner.append(Vector((math.cos(a) * r_in, math.sin(a) * r_in, z_in)))
		outer[-1] = outer[0]
		inner[-1] = inner[0]
		slope = math.sqrt((r_out - r_in) ** 2 + (z_in - z_out) ** 2)
		for i in range(facets):
			u0, u1 = 2 * math.pi * r_out * i / facets, 2 * math.pi * r_out * (i + 1) / facets
			if r_in > 0.01:
				b.quad((outer[i], outer[i + 1], inner[i + 1], inner[i]), thatch, ((u0, 0), (u1, 0), (u1, slope), (u0, slope)))
				b.quad((inner[i], inner[i + 1], outer[i + 1], outer[i]), thatch, ((u0, slope), (u1, slope), (u1, 0), (u0, 0)))
			else:
				apex = inner[i]
				f = b.bm.faces.new((b.bm.verts.new(outer[i]), b.bm.verts.new(outer[i + 1]), b.bm.verts.new(apex)))
				f.material_index = thatch
				f.smooth = False
				for loop, uv in zip(f.loops, ((u0, 0), (u1, 0), ((u0 + u1) / 2, slope))):
					loop[b.uv].uv = (uv[0] / TILE_M, uv[1] / TILE_M)
				f2 = b.bm.faces.new((b.bm.verts.new(apex), b.bm.verts.new(outer[i + 1]), b.bm.verts.new(outer[i])))
				f2.material_index = thatch
				f2.smooth = False
				for loop, uv in zip(f2.loops, (((u0 + u1) / 2, slope), (u1, 0), (u0, 0))):
					loop[b.uv].uv = (uv[0] / TILE_M, uv[1] / TILE_M)
	skirt_r = radius + overhang
	skirt_z = wall_h - 0.2
	cone(skirt_r, skirt_z, radius + overhang * 0.45, skirt_z + 0.55, 14, 0.12)
	cone(radius + overhang * 0.5, skirt_z + 0.45, 0.0, apex_h, 14, 0.08)
	# tied top knot
	knot = [Vector((0, 0, apex_h - 0.12)), Vector((0.02, 0.01, apex_h + 0.1)), Vector((0.04, 0.03, apex_h + 0.32))]
	b.tube(knot, [0.16, 0.09, 0.05], 6, thatch, rng, cap_start=False)
	return b.finish()


# ------------------------------------------------------------------------------------------------------------- fences

def build_fence(name, seed, broken):
	rng = random.Random(seed)
	b = Builder(name, ["camp_wood"])
	wood = b.mat("camp_wood")
	length = 2.0
	posts = []
	for i, x in enumerate((-length / 2 + 0.1, length / 2 - 0.1)):
		lean_y = math.radians(rng.uniform(-8, 8)) if not (broken and i == 1) else math.radians(16)
		lean_x = math.radians(rng.uniform(-5, 5))
		h = rng.uniform(1.2, 1.4)
		base = Vector((x, 0, -0.12))
		top = base + Vector((math.sin(lean_x) * h, math.sin(lean_y) * h, math.cos(lean_y) * h))
		mid = base.lerp(top, 0.5) + Vector((rng.uniform(-0.01, 0.01), rng.uniform(-0.01, 0.01), 0))
		b.tube([base, mid, top], [0.065, 0.06, 0.045], 6, wood, rng, jitter=0.12)
		posts.append((base, top))

	def post_point(i, z):
		base, top = posts[i]
		t = (z - base.z) / (top.z - base.z)
		return base.lerp(top, t)

	def rail(z, sag, break_at=None):
		p0 = post_point(0, z) + Vector((-0.14, 0.07, 0))
		p1 = post_point(1, z) + Vector((0.14, 0.07, 0))
		if break_at is None:
			m = p0.lerp(p1, 0.5) - Vector((0, 0, sag))
			b.tube([p0, p0.lerp(m, 0.5), m, m.lerp(p1, 0.5), p1], [0.045, 0.05, 0.048, 0.046, 0.04], 5, wood, rng, jitter=0.15)
		else:
			# snapped: the left half droops to the ground, the right stub stays on its post
			m = p0.lerp(p1, break_at)
			drop = Vector((m.x + 0.1, m.y + 0.2, 0.02))
			b.tube([p0, p0.lerp(m, 0.5) - Vector((0, 0, 0.05)), m.lerp(drop, 0.5), drop], [0.045, 0.05, 0.045, 0.035], 5, wood, rng, jitter=0.15)
			stub = p1.lerp(m, 0.35)
			b.tube([p1, stub], [0.04, 0.03], 5, wood, rng, jitter=0.2)
		# lashings: three rope rings round each post / rail junction
		for i in range(2):
			c = post_point(i, z) + Vector((0, 0.035, 0))
			for k in range(3):
				ring_c = c + Vector((0, 0, (k - 1) * 0.035))
				pts = [ring_c + Vector((math.cos(a) * 0.085, math.sin(a) * 0.085 * 0.8, (k - 1) * 0.004)) for a in [2 * math.pi * j / 6 for j in range(7)]]
				b.tube(pts, [0.012] * 7, 4, wood, rng, cap_start=False, cap_end=False, jitter=0.05)
				# rope UVs land in the band: push v into the bottom rows
				for f in b.bm.faces[-24:]:
					for loop in f.loops:
						u, v = loop[b.uv].uv
						loop[b.uv].uv = (u, 1.0 - 0.06 + (v % 0.05))
	rail(0.55, rng.uniform(0.03, 0.08))
	rail(1.05, rng.uniform(0.04, 0.10), break_at=0.55 if broken else None)
	return b.finish()


# ----------------------------------------------------------------------------------------------------------- campfire

def build_campfire(seed):
	rng = random.Random(seed)
	b = Builder("campfire", ["rock_gunmetal", "camp_wood"])
	rock, wood = b.mat("rock_gunmetal"), b.mat("camp_wood")
	# stone ring: lumpy low polyhedra
	for i in range(9):
		a = 2 * math.pi * i / 9 + rng.uniform(-0.15, 0.15)
		r = 0.52 + rng.uniform(-0.04, 0.04)
		c = Vector((math.cos(a) * r, math.sin(a) * r, 0.05))
		size = Vector((rng.uniform(0.11, 0.16), rng.uniform(0.09, 0.14), rng.uniform(0.08, 0.12)))
		b.blob(c, size, rock, rng)
	# three logs leaning together
	for i in range(3):
		a = 2 * math.pi * i / 3 + rng.uniform(-0.2, 0.2)
		foot = Vector((math.cos(a) * 0.38, math.sin(a) * 0.38, 0.06))
		head = Vector((math.cos(a + math.pi) * 0.08, math.sin(a + math.pi) * 0.08, 0.5 + rng.uniform(-0.05, 0.05)))
		mid = foot.lerp(head, 0.5)
		b.tube([foot, mid, head], [0.07, 0.065, 0.05], 6, wood, rng, jitter=0.12)
	# two spare logs on the ground beside the ring
	for i in range(2):
		a = rng.uniform(0, 2 * math.pi)
		c = Vector((math.cos(a) * 0.85, math.sin(a) * 0.85, 0.06))
		d = Vector((math.cos(a + 1.3), math.sin(a + 1.3), 0))
		b.tube([c - d * 0.3, c, c + d * 0.3], [0.055, 0.06, 0.05], 6, wood, rng, jitter=0.12)
	fire = b.finish()
	# ember bed: a lumpy disc inside the ring (tinted by Campfire)
	e = Builder("campfire_embers", ["camp_embers"])
	em = e.mat("camp_embers")
	centre = Vector((0, 0, 0.04))
	ring = []
	for i in range(10):
		a = 2 * math.pi * i / 10
		r = 0.38 * rng.uniform(0.85, 1.0)
		ring.append(e.bm.verts.new(Vector((math.cos(a) * r, math.sin(a) * r, 0.02 + rng.uniform(0, 0.03)))))
	cv = e.bm.verts.new(centre + Vector((0, 0, 0.06)))
	for i in range(10):
		f = e.bm.faces.new((ring[i], ring[(i + 1) % 10], cv))
		f.material_index = em
		f.smooth = False
		for loop, v in zip(f.loops, (ring[i], ring[(i + 1) % 10], cv)):
			loop[e.uv].uv = ((v.co.x + 0.5) / TILE_M, (v.co.y + 0.5) / TILE_M)
	embers = e.finish()
	return fire, embers


# ---------------------------------------------------------------------------------------------------------- beehive box

def build_beehive_box(seed):
	rng = random.Random(seed)
	b = Builder("beehive_box", ["camp_wood"])
	wood = b.mat("camp_wood")
	w, d = 0.5, 0.42
	leg_h = 0.28
	body_h = 0.5
	for sx in (-1, 1):
		for sy in (-1, 1):
			b.box(Vector((sx * (w / 2 - 0.04), sy * (d / 2 - 0.04), leg_h / 2)), Vector((0.05, 0.05, leg_h)), wood, rng, jitter=0.004)
	# slatted walls: five boards a side with uneven gaps
	slats = 5
	slat_h = body_h / slats
	for k in range(slats):
		z = leg_h + slat_h * (k + 0.5)
		gap = rng.uniform(0.006, 0.014)
		b.box(Vector((0, -d / 2, z)), Vector((w + rng.uniform(-0.01, 0.02), 0.03, slat_h - gap)), wood, rng, jitter=0.003)
		b.box(Vector((0, d / 2, z)), Vector((w + rng.uniform(-0.01, 0.02), 0.03, slat_h - gap)), wood, rng, jitter=0.003)
		b.box(Vector((-w / 2, 0, z)), Vector((0.03, d + rng.uniform(-0.01, 0.02), slat_h - gap)), wood, rng, jitter=0.003)
		b.box(Vector((w / 2, 0, z)), Vector((0.03, d + rng.uniform(-0.01, 0.02), slat_h - gap)), wood, rng, jitter=0.003)
	# floor + landing board + entrance strip (a darker bar across the front bottom)
	b.box(Vector((0, 0, leg_h + 0.015)), Vector((w, d, 0.03)), wood, rng)
	b.box(Vector((w / 2 + 0.07, 0, leg_h + 0.02)), Vector((0.16, 0.26, 0.02)), wood, rng, pitch=math.radians(8))
	b.box(Vector((w / 2 + 0.016, 0, leg_h + 0.06)), Vector((0.012, 0.22, 0.03)), wood, rng)
	# pitched lid with overhang
	top = leg_h + body_h
	for sy in (-1, 1):
		c = Vector((0, sy * d / 4, top + 0.06))
		b.box(c, Vector((w + 0.14, d / 2 + 0.08, 0.025)), wood, rng, roll=sy * math.radians(-22), jitter=0.003)
	b.box(Vector((0, 0, top + 0.125)), Vector((w + 0.16, 0.05, 0.04)), wood, rng, jitter=0.003)
	return b.finish()


# ------------------------------------------------------------------------------------------------------------- export

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


def vmat_path(name):
	return ROCK_VMAT if name == "rock_gunmetal" else f"{MAT_DIR}/{name}.vmat"


def write_vmdl(rel_vmdl, fbx, mesh, materials, physics, note):
	remaps = "".join(f"""							{{
								from = "{m}.vmat"
								to = "{vmat_path(m)}"
							}},
""" for m in materials)
	if physics == "mesh":
		phys = f"""			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "PhysicsMeshFile"
						name = "{mesh}_physics"
						parent_bone = ""
						surface_prop = "wood"
						collision_tags = "solid"
						recenter_on_parent_bone = false
						offset_origin = [ 0.0, 0.0, 0.0 ]
						offset_angles = [ 0.0, 0.0, 0.0 ]
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						filename = "{fbx}"
						import_scale = 0.4
						maxMeshVertices = 0
						qemError = 0.0
						import_filter =
						{{
							exclude_by_default = true
							exception_list = [ "{mesh}" ]
						}}
					}},
				]
			}},
"""
	elif physics == "hull":
		phys = """			{
				_class = "PhysicsShapeList"
				children =
				[
					{
						_class = "PhysicsHullFromRender"
						parent_bone = ""
						surface_prop = "wood"
						collision_tags = "solid"
						faceMergeAngle = 10.0
						maxHullVertices = 32
						hull_mode = "SingleHull"
					},
				]
			},
"""
	else:
		phys = ""
	text = f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
// {note} - Blender/scripts/create_camp_props.py
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
						global_default_material = "{vmat_path(materials[0])}"
					}},
				]
			}},
{phys}			{{
				_class = "RenderMeshList"
				children =
				[
					{{
						_class = "RenderMeshFile"
						name = "{mesh}"
						filename = "{fbx}"
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
	path = os.path.join(ASSETS, rel_vmdl.replace("/", os.sep))
	os.makedirs(os.path.dirname(path), exist_ok=True)
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


def write_vmat(name):
	text = f"""Layer0
{{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.950"
	PixelSpecular "0.250"
	PixelFlatShading "1.000"
	TextureColor "{MAT_DIR}/{name}.png"
}}
"""
	path = os.path.join(ASSETS, MAT_DIR.replace("/", os.sep), name + ".vmat")
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


def guid(*parts):
	return str(uuid.uuid5(uuid.NAMESPACE_URL, "survivalgame2026/" + "/".join(parts)))


def write_build_prefab(name, model, collider, tags="buildpiece"):
	"""A hammer / camp piece: root [collider, BuildPiece], child Visual [ModelRenderer]. collider = ("model", vmdl) or ("box", "sx,sy,sz", "cx,cy,cz")."""
	rel = f"{PREFAB_DIR}/{name}.prefab"
	if collider[0] == "model":
		col = {"__type": "Sandbox.ModelCollider", "__guid": guid(rel, "collider"), "__enabled": True, "Flags": 0,
			   "IsTrigger": False, "Model": collider[1], "Static": True, "Surface": None}
	else:
		col = {"__type": "Sandbox.BoxCollider", "__guid": guid(rel, "collider"), "__enabled": True, "Flags": 0,
			   "Center": collider[2], "ColliderFlags": 0, "Elasticity": None, "Friction": None, "IsTrigger": False,
			   "RollingResistance": None, "Scale": collider[1], "Static": True, "Surface": None, "SurfaceVelocity": "0,0,0"}
	piece = {"__type": "Survival.BuildPiece", "__guid": guid(rel, "piece"), "__enabled": True, "Flags": 0,
			 "IsBlueprint": False, "PieceId": name}
	renderer = {"__type": "Sandbox.ModelRenderer", "__guid": guid(rel, "renderer"), "__enabled": True, "Flags": 0,
				"BodyGroups": 18446744073709551615, "CreateAttachments": False, "LodOverride": None, "MaterialGroup": None,
				"MaterialOverride": None, "Materials": None, "Model": model,
				"RenderOptions": {"GameLayer": True, "OverlayLayer": False, "BloomLayer": False, "AfterUILayer": False},
				"RenderType": "On", "Tint": "1,1,1,1"}
	visual = {"__guid": guid(rel, "visual"), "__version": 2, "Flags": 0, "Name": "Visual", "Position": "0,0,0",
			  "Rotation": "0,0,0,1", "Scale": "1,1,1", "Tags": "", "Enabled": True, "NetworkMode": 1, "NetworkFlags": 0,
			  "NetworkOrphaned": 0, "NetworkTransmit": True, "OwnerTransfer": 1, "Components": [renderer], "Children": []}
	root = {"__guid": guid(rel), "__version": 2, "Flags": 0, "Name": name, "Position": "0,0,0", "Rotation": "0,0,0,1",
			"Scale": "1,1,1", "Tags": tags, "Enabled": True, "NetworkMode": 1, "NetworkFlags": 0, "NetworkOrphaned": 0,
			"NetworkTransmit": True, "OwnerTransfer": 1, "Components": [col, piece], "Children": [visual],
			"__properties": {"NetworkInterpolation": True, "TimeScale": 1, "WantsSystemScene": True, "Metadata": {},
							 "NavMesh": {"Enabled": False, "IncludeStaticBodies": True, "IncludeKeyframedBodies": True,
										 "EditorAutoUpdate": False, "AgentHeight": 64, "AgentRadius": 16, "AgentStepSize": 18,
										 "AgentMaxSlope": 40, "ExcludedBodies": "", "IncludedBodies": "", "DeferGeneration": False,
										 "CustomBounds": False}},
			"__variables": []}
	doc = {"RootObject": root, "ResourceVersion": 2, "ShowInMenu": False, "MenuPath": None, "MenuIcon": None,
		   "DontBreakAsTemplate": False, "__references": [], "__version": 2}
	path = os.path.join(ASSETS, rel.replace("/", os.sep))
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		json.dump(doc, f, indent=2)


# ------------------------------------------------------------------------------------------------------ roasting spit

SPIT_SIZE_M = (2.0, 0.3, 1.2)   # BuildModuleDimensions.SizesMeters["roasting_spit"]: bar length, depth, height


def build_roasting_spit(seed):
	"""Engine frame, centred on its box: posts at y = +-0.8 from z -0.6 (the ground) to a fork at 0.45, the pole in the forks."""
	rng = random.Random(seed)
	b = Builder("roasting_spit", ["camp_wood"])
	wood = b.mat("camp_wood")
	for sy in (-1, 1):
		lean = rng.uniform(-0.03, 0.03)
		foot = Vector((lean, sy * 0.8, -0.6))
		knee = Vector((lean * 0.3, sy * 0.8, -0.05))
		crotch = Vector((0.0, sy * 0.8, 0.36))
		b.tube([foot, knee, crotch], [0.05, 0.045, 0.042], 6, wood, rng, jitter=0.12)
		# the fork: two short prongs the pole lies between
		for sx in (-1, 1):
			tip = crotch + Vector((sx * 0.085, rng.uniform(-0.01, 0.01), 0.22))
			b.tube([crotch - Vector((0, 0, 0.02)), crotch.lerp(tip, 0.5) + Vector((sx * 0.012, 0, 0)), tip], [0.036, 0.03, 0.022], 5, wood, rng, cap_start=False, jitter=0.12)
	# the pole, sagging a little, its far end bent down into a crank handle
	z = 0.44
	pole = [Vector((0, -0.98, z + 0.01)), Vector((0, -0.4, z - 0.012)), Vector((0, 0.2, z - 0.02)), Vector((0, 0.86, z)),
			Vector((0, 0.95, z - 0.02)), Vector((0.02, 0.97, z - 0.16)), Vector((0.02, 1.0, z - 0.2))]
	b.tube(pole, [0.024, 0.028, 0.028, 0.026, 0.024, 0.022, 0.02], 5, wood, rng, jitter=0.1)
	return b.finish()


# ------------------------------------------------------------------------------------------------------------- render

def render(obj, tag, dist, elev, extra=()):
	scene = bpy.context.scene
	scene.render.engine = 'BLENDER_EEVEE'
	scene.render.resolution_x = 900
	scene.render.resolution_y = 600
	if scene.world is None:
		world = bpy.data.worlds.new("w")
		world.use_nodes = True
		world.node_tree.nodes["Background"].inputs[0].default_value = (0.33, 0.37, 0.31, 1.0)
		scene.world = world
	if bpy.data.objects.get("ground") is None:
		bpy.ops.mesh.primitive_plane_add(size=30.0, location=(0.0, 0.0, -0.002))
		ground = bpy.context.active_object
		ground.name = "ground"
		gm = bpy.data.materials.new("ground")
		gm.use_nodes = True
		gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.27, 0.36, 0.2, 1.0)
		ground.data.materials.append(gm)
		sun = bpy.data.lights.new("sun", 'SUN')
		sun.energy = 4.0
		sun.angle = math.radians(4.0)
		so = bpy.data.objects.new("sun", sun)
		scene.collection.objects.link(so)
		so.rotation_euler = (math.radians(50.0), 0.0, math.radians(-35.0))
		cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
		scene.collection.objects.link(cam)
		scene.camera = cam
	cam = scene.camera
	for o in bpy.data.objects:
		if o.type == 'MESH' and o.name != "ground":
			o.hide_render = o is not obj and o not in extra
	xs = [(obj.matrix_world @ v.co) for v in obj.data.vertices]
	centre = sum(xs, Vector()) / len(xs)
	centre.z = (min(v.z for v in xs) + max(v.z for v in xs)) * 0.5
	a = math.radians(-35.0)
	cam.location = centre + Vector((math.cos(a) * dist, math.sin(a) * dist, elev))
	cam.rotation_euler = (centre - cam.location).to_track_quat('-Z', 'Y').to_euler()
	os.makedirs(RENDER_DIR, exist_ok=True)
	scene.render.filepath = os.path.join(RENDER_DIR, f"{tag}.png")
	bpy.ops.render.render(write_still=True)


def stats(obj):
	tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
	xs = [v.co.x for v in obj.data.vertices]
	ys = [v.co.y for v in obj.data.vertices]
	zs = [v.co.z for v in obj.data.vertices]
	return f"{tris} tris, {max(xs) - min(xs):.2f} x {max(ys) - min(ys):.2f} x {max(zs) - min(zs):.2f} m, z {min(zs):.2f}..{max(zs):.2f}"


def main_spit():
	"""Only the roasting spit: reuses the camp_wood texture already on disk, touches no other file."""
	bpy.ops.wm.read_factory_settings(use_empty=True)
	img = bpy.data.images.load(os.path.join(ASSETS, MAT_DIR.replace("/", os.sep), "camp_wood.png"))
	img.name = "camp_wood"
	spit = build_roasting_spit(501)
	fbx = f"{BUILD_MODEL_DIR}/{spit.name}.fbx"
	export_fbx([spit], fbx)
	write_vmdl(f"{BUILD_MODEL_DIR}/{spit.name}.vmdl", fbx, spit.name, [m.name for m in spit.data.materials], None, "roasting spit over a campfire")
	half = [v * 20.0 for v in SPIT_SIZE_M]   # 40 u/m, half extents; the prefab box is in the mesh frame (bar along Y)
	write_build_prefab("roasting_spit", f"{BUILD_MODEL_DIR}/roasting_spit.vmdl", ("box", f"{half[1] * 2:g},{half[0] * 2:g},{half[2] * 2:g}", "0,0,0"))
	print(f"[camp] {spit.name}: {stats(spit)}")
	bpy.ops.mesh.primitive_plane_add(size=30.0, location=(0.0, 0.0, -0.602))
	bpy.context.active_object.name = "ground"
	gm = bpy.data.materials.new("ground")
	gm.use_nodes = True
	gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.27, 0.36, 0.2, 1.0)
	bpy.context.active_object.data.materials.append(gm)
	sun = bpy.data.lights.new("sun", 'SUN')
	sun.energy = 4.0
	so = bpy.data.objects.new("sun", sun)
	bpy.context.scene.collection.objects.link(so)
	so.rotation_euler = (math.radians(50.0), 0.0, math.radians(-35.0))
	cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
	bpy.context.scene.collection.objects.link(cam)
	bpy.context.scene.camera = cam
	render(spit, "roasting_spit", 3.2, 1.2)


def main():
	if "--" in sys.argv and "spit" in sys.argv[sys.argv.index("--") + 1:]:
		main_spit()
		return
	bpy.ops.wm.read_factory_settings(use_empty=True)
	rng = random.Random(2611)
	for name, fn in (("camp_thatch", tex_thatch), ("camp_wattle", tex_wattle), ("camp_wood", tex_wood), ("camp_embers", tex_embers)):
		save_png(name, fn(rng))
		write_vmat(name)

	hut_a = build_hut("camp_hut_a", 101, radius=1.7, wall_h=1.7, apex_h=3.1, overhang=0.45)
	hut_b = build_hut("camp_hut_b", 102, radius=1.5, wall_h=1.6, apex_h=2.8, overhang=0.4)
	fence_a = build_fence("camp_fence_a", 201, broken=False)
	fence_b = build_fence("camp_fence_b", 202, broken=True)
	fire, embers = build_campfire(301)
	hive = build_beehive_box(401)
	spit = build_roasting_spit(501)

	for obj, rel_dir, physics, note in (
			(hut_a, CAMP_MODEL_DIR, "mesh", "scav camp thatch hut A"),
			(hut_b, CAMP_MODEL_DIR, "mesh", "scav camp thatch hut B"),
			(fence_a, CAMP_MODEL_DIR, None, "scav camp fence, whole"),
			(fence_b, CAMP_MODEL_DIR, None, "scav camp fence, snapped rail"),
			(fire, BUILD_MODEL_DIR, None, "campfire: stone ring + logs"),
			(embers, BUILD_MODEL_DIR, None, "campfire ember bed (tinted by Campfire)"),
			(hive, BUILD_MODEL_DIR, "hull", "craftable beehive box"),
			(spit, BUILD_MODEL_DIR, None, "roasting spit over a campfire")):
		fbx = f"{rel_dir}/{obj.name}.fbx"
		export_fbx([obj], fbx)
		write_vmdl(f"{rel_dir}/{obj.name}.vmdl", fbx, obj.name, [m.name for m in obj.data.materials], physics, note)
		print(f"[camp] {obj.name}: {stats(obj)}")

	write_build_prefab("camp_hut_a", f"{CAMP_MODEL_DIR}/camp_hut_a.vmdl", ("model", f"{CAMP_MODEL_DIR}/camp_hut_a.vmdl"))
	write_build_prefab("camp_hut_b", f"{CAMP_MODEL_DIR}/camp_hut_b.vmdl", ("model", f"{CAMP_MODEL_DIR}/camp_hut_b.vmdl"))
	write_build_prefab("camp_fence_a", f"{CAMP_MODEL_DIR}/camp_fence_a.vmdl", ("box", "88,10,54", "0,0,26"))
	write_build_prefab("camp_fence_b", f"{CAMP_MODEL_DIR}/camp_fence_b.vmdl", ("box", "88,10,54", "0,0,26"))
	write_build_prefab("beehive_box", f"{BUILD_MODEL_DIR}/beehive_box.vmdl", ("box", "26,20,40", "0,0,20"))
	write_build_prefab("roasting_spit", f"{BUILD_MODEL_DIR}/roasting_spit.vmdl", ("box", "12,80,48", "0,0,0"))

	render(hut_a, "camp_hut_a", 7.5, 3.2)
	render(hut_b, "camp_hut_b", 7.0, 3.0)
	render(fence_a, "camp_fence_a", 3.4, 1.3)
	render(fence_b, "camp_fence_b", 3.4, 1.3)
	render(fire, "campfire", 2.4, 1.3, extra=(embers,))
	render(hive, "beehive_box", 2.0, 0.9)
	render(spit, "roasting_spit", 3.2, 1.2)
	print(f"[camp] renders in {RENDER_DIR}")


main()
