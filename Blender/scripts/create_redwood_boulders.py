"""Big, mostly buried slab boulders for the Redwood Forest (Mark 2026-11-16: "Bigger boulders as well, flatter ones cuz the
majority of it is buried").

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_redwood_boulders.py

What it writes (all at real size, Blender metres -> FBX -> vmdl import_scale 0.4 = the project's 40 u/m):

| what                 | files                                                                         |
|----------------------|-------------------------------------------------------------------------------|
| 3 textures + vmats   | materials/environment/redwoodForest/rocks/redwood_stone{,_mossedge,_moss}     |
| 5 slab boulders      | models/environment/redwoodForest/rocks/redwood_slab{1-5}_v1.fbx + _s0.._sN    |
| 5 stone bits         | models/environment/redwoodForest/rocks/redwood_rock_bit{1-5}_v1 (+ rock_bits) |
| 5 prefabs            | prefabs/environment/redwoodForest/rocks/redwood_slab{1-5}_v1.prefab           |

Shape: the visible cap of a much bigger buried rock. A displaced, domain-warped icosphere is squared off (super-ellipsoid
exponent on z, so the top reads tabular rather than as a dome), tilted a little (variant 2 a lot: a tipped slab with
one edge up), raised so only `exposed` metres show, then cut flat `skirt` metres under the ground line so it sits on a
slope without a gap. Ground line = origin (z = 0). Up-facing cleavage planes give the big flat ledges, side cleaves the
broken walls; collapse decimate + planar dissolve give facets of varied size.

Mining: the same MineableRock system as the Clover sectioned boulders (create_mineable_rocks.py). A slab is cut into 3-6
side-by-side Voronoi sections (seeds in the ground plane, so the cuts are near-vertical walls); every section reaches
the ground, so all are grounded and nothing collapses - the pickaxe takes the slab apart a section at a time. Bigger
sections than the clover boulders: more hits, more stone, bigger bits (the redwood bits below, in the slab's own stone).

Look: pixel_lit, PixelFlatShading, 256 px point-filtered textures at 2 m per tile = 128 texels / m (planar UVs per face,
a pure function of position + normal, so a section's outer faces keep the whole slab's UVs). Damp forest stone: four
low-contrast mid greys + the sparse connected crack net (the elm-bark recipe). A moss cap on the faces that look up
(face normal z + low-frequency noise of the face centre, so the cap edge wanders), with a stone / moss blotch band
between them. No speckle, no dither, no hue noise.

LODs: lod0-3 switching at 100 / 200 / 300 m (switch_threshold 400 / 800 / 1200). Renders of every slab (whole + sections)
go to RENDER_DIR (set it to a scratch folder; default is outside the repo's Assets).
"""
import bpy
import bmesh
import json
import math
import os
import random
import sys
import uuid

import numpy as np
from mathutils import Vector, Matrix, noise

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ASSETS = os.path.join(ROOT, "Assets")
MAT_DIR = "materials/environment/redwoodForest/rocks"
ROCK_DIR = "models/environment/redwoodForest/rocks"
PREFAB_DIR = "prefabs/environment/redwoodForest/rocks"
RENDER_DIR = os.environ.get("ROCK_RENDER_DIR", os.path.join(ROOT, "Blender", "renders", "redwood_boulders"))
VERSION = "v1"
UV_METERS = 2.0  # one 256 px tile per 2 m = 128 texels / m
TEX_SIZE = 256

LOD_DISSOLVE_DEG = (0.0, 9.0, 18.0, 32.0)
LOD_THRESHOLDS = (0.0, 400.0, 800.0, 1200.0)

MAT_STONE = "redwood_stone"
MAT_EDGE = "redwood_stone_mossedge"
MAT_MOSS = "redwood_stone_moss"
MATERIALS = [MAT_STONE, MAT_EDGE, MAT_MOSS]

# the resource_stone pickups the hit chips use (clover set, same item everywhere)
CHIP_MODELS = [f"models/resources/stone/stone_drop{i}_v1.vmdl" for i in range(1, 5)]

# Mined-section tuning: the sections are 2-3 m slabs of rock, not the clover boulder's ~1.5 m chunks.
TUNING = dict(HitsPerSection=6, StoneMin=4, StoneMax=7, CollapseStoneMin=2, CollapseStoneMax=4, BitsPerSection=8,
			  BitScale=1.4, DebrisLifetimeSeconds=4.0, DebrisScatterMeters=1.5)

# seed, (length, width) footprint at the ground line (m), exposed height (m), body thickness (m), tilt (deg),
# skirt below ground (m), sections, up-cleaves, side-cleaves, decimate target
SLABS = [
	(601, (6.0, 4.6), 1.3, 3.6, 6.0, 0.8, 5, 4, 3, 150),   # broad low table
	(602, (7.6, 4.0), 2.3, 2.6, 21.0, 1.0, 6, 3, 3, 150),  # tipped slab, one edge up
	(603, (4.4, 3.8), 1.8, 3.8, 9.0, 0.7, 4, 4, 3, 130),   # chunky, the tallest cap
	(604, (8.0, 3.2), 1.0, 2.6, 4.0, 0.6, 6, 4, 3, 150),   # long low ridge
	(605, (3.4, 2.8), 0.9, 2.4, 11.0, 0.5, 3, 3, 2, 100),  # small fill
]

BITS = [  # the bits a section bursts into (x BitScale in-engine)
	(701, (0.8, 0.6, 0.45)),
	(702, (0.65, 0.55, 0.4)),
	(703, (1.0, 0.7, 0.5)),
	(704, (0.55, 0.45, 0.32)),
	(705, (0.8, 0.75, 0.4)),
]


# ------------------------------------------------------------------------------------------------------------ textures

STONE_TONES = np.array([  # damp forest stone: neutral mid greys with the faintest warm-green cast, light to dark (sRGB)
	(92, 94, 89),
	(86, 88, 83),
	(80, 82, 77),
	(74, 76, 72),
], dtype=np.float32)
STONE_CRACK = np.array((63, 65, 61), dtype=np.float32)
MOSS_TONES = np.array([  # muted olive moss, one hue, four close values
	(76, 86, 54),
	(71, 81, 51),
	(66, 76, 48),
	(61, 71, 45),
], dtype=np.float32)


def value_noise(rng, cells):
	"""Tileable bilinear value noise on a cells x cells lattice, smoothstepped, 0..1."""
	lattice = rng.random((cells, cells)).astype(np.float32)
	coords = np.arange(TEX_SIZE, dtype=np.float32) * cells / TEX_SIZE
	i0 = np.floor(coords).astype(int) % cells
	i1 = (i0 + 1) % cells
	t = coords - np.floor(coords)
	t = t * t * (3 - 2 * t)
	a = lattice[np.ix_(i0, i0)]
	b = lattice[np.ix_(i0, i1)]
	c = lattice[np.ix_(i1, i0)]
	d = lattice[np.ix_(i1, i1)]
	ty = t[:, None]
	tx = t[None, :]
	return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty


def normalise(x):
	lo, hi = np.percentile(x, [3, 97])
	return np.clip((x - lo) / max(1e-6, hi - lo), 0, 0.999)


def crack_net(rng, walkers_n=4):
	"""A few random walks that branch: thin, connected, varied weight. 0..1 coverage mask."""
	mask = np.zeros((TEX_SIZE, TEX_SIZE), dtype=np.float32)
	walkers = []
	for _ in range(walkers_n):
		walkers.append([rng.random() * TEX_SIZE, rng.random() * TEX_SIZE, rng.random() * 2 * np.pi, 1 + int(rng.random() * 2), 70 + int(rng.random() * 60)])
	while walkers:
		x, y, ang, weight, steps = walkers.pop()
		for _ in range(steps):
			ang += (rng.random() - 0.5) * 0.32
			x = (x + np.cos(ang)) % TEX_SIZE
			y = (y + np.sin(ang)) % TEX_SIZE
			for ox in range(weight):
				for oy in range(weight):
					mask[int(y + oy) % TEX_SIZE, int(x + ox) % TEX_SIZE] = 1.0
			if rng.random() < 0.012 and len(walkers) < 8:
				walkers.append([x, y, ang + (0.9 if rng.random() < 0.5 else -0.9), 1, 18 + int(rng.random() * 26)])
	return mask


def tones(rng, palette, coarse=6, fine=14):
	blotch = normalise(value_noise(rng, coarse) * 0.65 + value_noise(rng, fine) * 0.35)
	return palette[(blotch * len(palette)).astype(int)]


def save_png(name, rgb):
	path = os.path.join(ASSETS, MAT_DIR.replace("/", os.sep), name + ".png")
	os.makedirs(os.path.dirname(path), exist_ok=True)
	img = bpy.data.images.new(name, TEX_SIZE, TEX_SIZE, alpha=False)
	rgba = np.ones((TEX_SIZE, TEX_SIZE, 4), dtype=np.float32)
	rgba[..., :3] = np.clip(rgb, 0, 255) / 255.0
	img.pixels.foreach_set(np.flipud(rgba).ravel())  # Blender rows run bottom-up
	img.filepath_raw = path
	img.file_format = 'PNG'
	img.save()
	return img


def write_vmat(name):
	text = f"""// Redwood forest stone (Blender/scripts/create_redwood_boulders.py): 256 px point-filtered, faceted, tiled x1 -
// the slab UVs are authored at 2 m per tile = 128 texels / m.
Layer0
{{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.900"
	PixelFlatShading "1.000"
	PixelTexCoordScale "[1.000 1.000]"
	TextureColor "{MAT_DIR}/{name}.png"
}}
"""
	path = os.path.join(ASSETS, MAT_DIR.replace("/", os.sep), name + ".vmat")
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


def make_textures():
	rng = np.random.default_rng(6611)
	stone = tones(rng, STONE_TONES)
	cracks = crack_net(rng)
	stone = stone * (1 - cracks[..., None]) + STONE_CRACK * cracks[..., None]

	moss = tones(rng, MOSS_TONES, coarse=8, fine=20)

	# the band between: moss blotches on stone, hard pixel edge (no dither), ~half cover
	cover = normalise(value_noise(rng, 4) * 0.7 + value_noise(rng, 9) * 0.3)  # a few big patches, not camo
	edge_mask = (cover > 0.5).astype(np.float32)[..., None]
	edge = stone * (1 - edge_mask) + moss * edge_mask

	images = {}
	for name, rgb in ((MAT_STONE, stone), (MAT_EDGE, edge), (MAT_MOSS, moss)):
		images[name] = save_png(name, rgb)
		write_vmat(name)
	return images


def make_materials(images):
	mats = {}
	for name in MATERIALS:
		mat = bpy.data.materials.new(name)
		mat.use_nodes = True
		bsdf = mat.node_tree.nodes.get("Principled BSDF")
		tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
		tex.image = images[name]
		tex.interpolation = 'Closest'
		mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
		bsdf.inputs["Roughness"].default_value = 0.9
		mats[name] = mat
	return mats


# ---------------------------------------------------------------------------------------------------------------- mesh

def bm_to_object(bm, name):
	me = bpy.data.meshes.new(name)
	bm.to_mesh(me)
	bm.free()
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	return obj


def obj_to_bm(obj):
	bm = bmesh.new()
	bm.from_mesh(obj.data)
	return bm


def apply_modifier(obj, mod):
	bpy.context.view_layer.objects.active = obj
	for o in bpy.context.selected_objects:
		o.select_set(False)
	obj.select_set(True)
	bpy.ops.object.modifier_apply(modifier=mod.name)


def decimate(obj, target_faces):
	faces = len(obj.data.polygons)
	if faces > target_faces:
		mod = obj.modifiers.new("dec", 'DECIMATE')
		mod.decimate_type = 'COLLAPSE'
		mod.ratio = max(0.02, target_faces / faces)
		apply_modifier(obj, mod)


def facet(obj, angle_deg=5.0):
	"""Tris to quads + planar dissolve: big flat facets of varied size."""
	bm = obj_to_bm(obj)
	bmesh.ops.join_triangles(bm, faces=bm.faces[:], angle_face_threshold=math.radians(25), angle_shape_threshold=math.radians(60))
	bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(angle_deg), verts=bm.verts[:], edges=bm.edges[:])
	bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-4)
	bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
	bm.to_mesh(obj.data)
	bm.free()


def bisect_keep_inner(bm, co, no):
	res = bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=co, plane_no=no, clear_outer=True, dist=1e-5)
	cut = [e for e in res["geom_cut"] if isinstance(e, bmesh.types.BMEdge)]
	if cut:
		bmesh.ops.holes_fill(bm, edges=cut, sides=0)


def slab(name, seed, foot, exposed, thick, tilt_deg, skirt, up_cleaves, side_cleaves, faces):
	"""The visible cap of a big buried rock: origin = ground line, `exposed` m above it, cut flat `skirt` m below."""
	rnd = random.Random(seed)
	off = Vector((rnd.uniform(-50, 50), rnd.uniform(-50, 50), rnd.uniform(-50, 50)))
	off2 = Vector((rnd.uniform(-50, 50), rnd.uniform(-50, 50), rnd.uniform(-50, 50)))
	# the body is wider than the footprint: the ground meets it a little above its equator, where it is still near-vertical
	body = Vector((foot[0] * 1.05, foot[1] * 1.05, thick))
	bm = bmesh.new()
	bmesh.ops.create_icosphere(bm, subdivisions=4, radius=1.0)
	for v in bm.verts:
		p = v.co.copy()
		w = noise.noise_vector(p * 0.45 + off) * 0.7  # stronger warp folds the wall into caves at the ground line
		q = (p + w * 0.45).normalized()
		r = 1.0 + 0.26 * noise.noise(q * 1.3 + off2) + 0.12 * noise.noise(q * 3.3 + off)
		p = q * r
		# square it off: |z|^0.65 pushes the top up into a table and steepens the walls (a super-ellipsoid, not a dome)
		p.z = math.copysign(abs(p.z) ** 0.65, p.z)
		# and the plan: a bit of the same on x / y so the outline is a rounded-off slab, not an ellipse
		p.x = math.copysign(abs(p.x) ** 0.85, p.x)
		p.y = math.copysign(abs(p.y) ** 0.85, p.y)
		v.co = Vector((p.x * body.x * 0.5, p.y * body.y * 0.5, p.z * body.z * 0.5))
	# lean: about an axis close to the slab's short side, so the long edge is the one that lifts
	axis = Vector((rnd.uniform(-0.3, 0.3), 1.0, 0.0)).normalized()
	rot = Matrix.Rotation(math.radians(tilt_deg) * (1 if rnd.random() < 0.5 else -1), 4, axis)
	bmesh.ops.transform(bm, matrix=rot, verts=bm.verts[:])
	# break planes before seating, measured from the body's own extents
	for k in range(up_cleaves):  # flat ledges on top
		a = rnd.uniform(0, math.tau)
		lean = rnd.uniform(0.25, 0.9)
		n = Vector((math.cos(a) * lean, math.sin(a) * lean, 1.0)).normalized()
		reach = max(v.co.dot(n) for v in bm.verts)
		bisect_keep_inner(bm, n * reach * rnd.uniform(0.74, 0.88), n)
	for k in range(side_cleaves):  # broken walls
		a = rnd.uniform(0, math.tau)
		n = Vector((math.cos(a), math.sin(a), rnd.uniform(0.05, 0.4))).normalized()  # never downward: no undercut at the ground
		reach = max(v.co.dot(n) for v in bm.verts)
		bisect_keep_inner(bm, n * reach * rnd.uniform(0.8, 0.9), n)
	# seat it: top at `exposed`, then everything deeper than the skirt goes
	top = max(v.co.z for v in bm.verts)
	for v in bm.verts:
		v.co.z += exposed - top
	# flare the foot: from a third of the way up, the rock widens into the ground, so the wall always slopes out where it
	# meets the terrain (a warp fold or a lean otherwise leaves an undercut that reads as a mouse hole)
	foot_top = exposed * 0.35
	cx = sum(v.co.x for v in bm.verts) / len(bm.verts)
	cy = sum(v.co.y for v in bm.verts) / len(bm.verts)
	for v in bm.verts:
		if v.co.z < foot_top:
			k = 1.0 + 0.32 * min(1.0, (foot_top - v.co.z) / (foot_top + skirt))
			v.co.x = cx + (v.co.x - cx) * k
			v.co.y = cy + (v.co.y - cy) * k
	bisect_keep_inner(bm, Vector((0, 0, -skirt)), Vector((0, 0, -1)))
	bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
	obj = bm_to_object(bm, name)
	decimate(obj, faces)
	facet(obj, 7.0)
	# recentre the footprint on the origin (x / y only - z = 0 stays the ground line)
	lo, hi = bounds(obj)
	obj.data.transform(Matrix.Translation(Vector((-(lo[0] + hi[0]) * 0.5, -(lo[1] + hi[1]) * 0.5, 0.0))))
	return obj


def hull(name, seed, size, points=12, faces=30):
	"""Convex hull of random points in an ellipsoid: an angular broken stone bit."""
	rnd = random.Random(seed)
	bm = bmesh.new()
	pts = []
	while len(pts) < points:
		p = Vector((rnd.uniform(-1, 1), rnd.uniform(-1, 1), rnd.uniform(-1, 1)))
		if p.length <= 1.0:
			pts.append(Vector((p.x * size[0] * 0.5, p.y * size[1] * 0.5, p.z * size[2] * 0.5)))
	for p in pts:
		bm.verts.new(p)
	bmesh.ops.convex_hull(bm, input=bm.verts[:])
	loose = [v for v in bm.verts if not v.link_faces]
	bmesh.ops.delete(bm, geom=loose, context='VERTS')
	obj = bm_to_object(bm, name)
	decimate(obj, faces)
	facet(obj, 3.0)
	return obj


# ------------------------------------------------------------------------------------------------------------ fracture

def convex_cell(planes, extent=200.0):
	"""Big cube clipped by half-spaces (co, no): keeps the side (p - co) . no <= 0."""
	bm = bmesh.new()
	bmesh.ops.create_cube(bm, size=extent * 2.0)
	for co, no in planes:
		bisect_keep_inner(bm, co, no)
	bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
	return bm


def intersect(source, cell_bm, name):
	piece = source.copy()
	piece.data = source.data.copy()
	piece.name = name
	piece.data.name = name
	bpy.context.scene.collection.objects.link(piece)
	cell = bm_to_object(cell_bm, name + "_cell")
	mod = piece.modifiers.new("cut", 'BOOLEAN')
	mod.operation = 'INTERSECT'
	mod.solver = 'EXACT'
	mod.object = cell
	apply_modifier(piece, mod)
	bpy.data.objects.remove(cell, do_unlink=True)
	bm = obj_to_bm(piece)
	bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-4)
	bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(0.5), verts=bm.verts[:], edges=bm.edges[:])
	bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
	bm.to_mesh(piece.data)
	bm.free()
	return piece if len(piece.data.polygons) >= 4 else None


def section_seeds(rnd, obj, count, exposed):
	"""Voronoi seeds spread over the footprint (ground plane), kept apart, with a little height jitter so the cut walls
	lean a few degrees instead of standing plumb."""
	lo, hi = bounds(obj)
	sx, sy = hi[0] - lo[0], hi[1] - lo[1]
	min_gap = math.sqrt(sx * sy / count) * 0.62
	seeds = []
	tries = 0
	while len(seeds) < count and tries < 5000:
		tries += 1
		u, v = rnd.uniform(-1, 1), rnd.uniform(-1, 1)
		if u * u + v * v > 1.0:
			continue
		p = Vector((u * sx * 0.38, v * sy * 0.38, exposed * rnd.uniform(0.2, 0.6)))
		if all((p - s).length > min_gap for s in seeds):
			seeds.append(p)
	return seeds


# ------------------------------------------------------------------------------------------------------------------ uv

def planar_uvs(obj):
	"""Every face flat on its own plane at UV_METERS per tile (position + normal only, so sections match their slab)."""
	me = obj.data
	if not me.uv_layers:
		me.uv_layers.new(name="UVMap")
	uv = me.uv_layers.active.data
	for f in me.polygons:
		n = f.normal
		ref = Vector((0, 0, 1)) if abs(n.z) < 0.9 else Vector((1, 0, 0))
		u = ref.cross(n).normalized()
		v = n.cross(u).normalized()
		for li in f.loop_indices:
			p = me.vertices[me.loops[li].vertex_index].co
			uv[li].uv = (p.dot(u) / UV_METERS, p.dot(v) / UV_METERS)


MOSS_NOISE = Vector((13.7, -41.2, 7.9))


def moss_slot(centre, normal):
	"""0 stone, 1 band, 2 moss: up-facing faces carry moss; low-frequency noise of the face centre wanders the edge.
	A pure function of the face, so sections and LODs agree with the whole slab."""
	if centre.z < 0.05:  # at or under the ground line: never moss (and the skirt is stone)
		return 0
	score = normal.z + 0.45 * noise.noise(centre * 0.38 + MOSS_NOISE) + 0.05 * min(centre.z, 2.0)
	if score > 0.92:
		return 2
	if score > 0.7:
		return 1
	return 0


def finish(obj, mats):
	bm = obj_to_bm(obj)
	bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method='BEAUTY', ngon_method='BEAUTY')
	bm.to_mesh(obj.data)
	bm.free()
	planar_uvs(obj)
	obj.data.materials.clear()
	for name in MATERIALS:
		obj.data.materials.append(mats[name])
	for p in obj.data.polygons:
		p.use_smooth = False
		p.material_index = moss_slot(p.center, p.normal)


def finish_plain(obj, mats):
	"""Bits: plain stone (they are the broken inside of the rock)."""
	finish(obj, mats)
	for p in obj.data.polygons:
		p.material_index = 0


def lods(obj):
	"""lod0 = the mesh; lod1-3 = planar dissolve at growing angles (vertices never move, so section seams stay put)."""
	base = obj.name
	obj.name = base + "_lod0"
	obj.data.name = base + "_lod0"
	out = [obj]
	for k in range(1, 4):
		c = obj.copy()
		c.data = obj.data.copy()
		c.name = f"{base}_lod{k}"
		c.data.name = c.name
		bpy.context.scene.collection.objects.link(c)
		bm = obj_to_bm(c)
		bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(LOD_DISSOLVE_DEG[k]), verts=bm.verts[:], edges=bm.edges[:])
		bm.to_mesh(c.data)
		bm.free()
		out.append(c)
	return out


def volume(obj):
	bm = obj_to_bm(obj)
	v = bm.calc_volume(signed=False)
	bm.free()
	return v


def tris(obj):
	return len(obj.data.polygons)


def bounds(obj):
	xs = [v.co.x for v in obj.data.vertices]
	ys = [v.co.y for v in obj.data.vertices]
	zs = [v.co.z for v in obj.data.vertices]
	return (min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs))


def footprint_at_ground(obj):
	"""Extent of the mesh where it crosses z = 0 (the outline the player sees at the ground line)."""
	pts = []
	me = obj.data
	for e in me.edges:
		a, b = me.vertices[e.vertices[0]].co, me.vertices[e.vertices[1]].co
		if (a.z <= 0.0) != (b.z <= 0.0):
			t = a.z / (a.z - b.z)
			pts.append(a.lerp(b, t))
	if not pts:
		return 0.0, 0.0
	return max(p.x for p in pts) - min(p.x for p in pts), max(p.y for p in pts) - min(p.y for p in pts)


# -------------------------------------------------------------------------------------------------------------- export

def export_fbx(objs, rel_path):
	path = os.path.join(ASSETS, rel_path.replace("/", os.sep))
	os.makedirs(os.path.dirname(path), exist_ok=True)
	for o in bpy.data.objects:
		o.select_set(False)
	for o in objs:
		o.select_set(True)
	bpy.context.view_layer.objects.active = objs[0]
	bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={'MESH'}, apply_unit_scale=True,
							 global_scale=1.0, axis_forward='-Z', axis_up='Y', mesh_smooth_type='FACE',
							 add_leaf_bones=False, bake_anim=False, path_mode='STRIP', embed_textures=False)


def render_mesh_block(fbx, mesh):
	return f"""					{{
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
"""


def physics_block(fbx, mesh, hull_shape):
	if hull_shape:
		return """			{
				_class = "PhysicsShapeList"
				children =
				[
					{
						_class = "PhysicsHullFromRender"
						parent_bone = ""
						surface_prop = "rock"
						collision_tags = "solid"
						faceMergeAngle = 10.0
						maxHullVertices = 32
						hull_mode = "SingleHull"
					},
				]
			},
"""
	return f"""			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "PhysicsMeshFile"
						name = "{mesh}_physics"
						parent_bone = ""
						surface_prop = "rock"
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


def write_vmdl(rel_vmdl, fbx, meshes, hull_shape, note):
	"""meshes = [lod0, lod1, ...] mesh names in `fbx` (one = no LOD groups). Each Blender material remaps to its vmat."""
	renders = "".join(render_mesh_block(fbx, m) for m in meshes)
	remaps = "".join(f"""						{{
							from = "{m}.vmat"
							to = "{MAT_DIR}/{m}.vmat"
						}},
""" for m in MATERIALS)
	lod = ""
	if len(meshes) > 1:
		groups = "".join(f"""					{{
						_class = "LODGroup"
						switch_threshold = {LOD_THRESHOLDS[k]:.1f}
						meshes = [ "{m}" ]
					}},
""" for k, m in enumerate(meshes))
		lod = f"""			{{
				_class = "LODGroupList"
				children =
				[
{groups}				]
			}},
"""
	text = f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
// {note} - Blender/scripts/create_redwood_boulders.py
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
						global_default_material = "{MAT_DIR}/{MAT_STONE}.vmat"
					}},
				]
			}},
{physics_block(fbx, meshes[0], hull_shape)}			{{
				_class = "RenderMeshList"
				children =
				[
{renders}				]
			}},
{lod}		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}}
}}
"""
	path = os.path.join(ASSETS, rel_vmdl.replace("/", os.sep))
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


# -------------------------------------------------------------------------------------------------------------- prefab

def guid(*parts):
	return str(uuid.uuid5(uuid.NAMESPACE_URL, "survivalgame2026/" + "/".join(parts)))


def renderer_json(key, model):
	return {
		"__type": "Sandbox.ModelRenderer", "__guid": guid(key, "renderer"), "__enabled": True, "Flags": 0,
		"BodyGroups": 18446744073709551615, "CreateAttachments": False, "LodOverride": None, "MaterialGroup": None,
		"MaterialOverride": None, "Materials": None, "Model": model,
		"RenderOptions": {"GameLayer": True, "OverlayLayer": False, "BloomLayer": False, "AfterUILayer": False},
		"RenderType": "On", "Tint": "1,1,1,1",
	}


def collider_json(key, model):
	return {
		"__type": "Sandbox.ModelCollider", "__guid": guid(key, "collider"), "__enabled": True, "Flags": 0,
		"ColliderFlags": 0, "Elasticity": None, "Friction": None, "IsTrigger": False, "Model": model,
		"RollingResistance": None, "Static": True, "Surface": None, "SurfaceVelocity": "0,0,0",
	}


def write_prefab(name, section_models, grounded, links, bit_models):
	"""Root [MineableRock], children section0..N [ModelRenderer + static ModelCollider], tagged rock_section."""
	t = TUNING
	rel = f"{PREFAB_DIR}/{name}.prefab"
	mine = {
		"__type": "Survival.MineableRock", "__guid": guid(rel, "mineable"), "__enabled": True, "Flags": 0,
		"HitsPerSection": t["HitsPerSection"], "RequireMineTool": True,
		"GroundedSections": grounded, "SectionLinks": links,
		"StoneResourceId": "resource_stone", "StoneMin": t["StoneMin"], "StoneMax": t["StoneMax"],
		"CollapseStoneMin": t["CollapseStoneMin"], "CollapseStoneMax": t["CollapseStoneMax"],
		"PieceModels": [], "BitModels": bit_models, "ChipModels": CHIP_MODELS,
		"BitsPerSection": t["BitsPerSection"], "BitScale": t["BitScale"], "DebrisLifetimeSeconds": t["DebrisLifetimeSeconds"],
		"DebrisScatterMeters": t["DebrisScatterMeters"], "LogMining": False,
	}
	children = []
	for k, model in enumerate(section_models):
		key = f"{rel}/section{k}"
		children.append({
			"__guid": guid(key), "__version": 2, "Flags": 0, "Name": f"section{k}", "Position": "0,0,0",
			"Rotation": "0,0,0,1", "Scale": "1,1,1", "Tags": "rock_section", "Enabled": True, "NetworkMode": 2,
			"NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": True, "OwnerTransfer": 1,
			"Components": [renderer_json(key, model), collider_json(key, model)], "Children": [],
		})
	root = {
		"__guid": guid(rel), "__version": 2, "Flags": 0, "Name": name, "Position": "0,0,0", "Rotation": "0,0,0,1",
		"Scale": "1,1,1", "Tags": "", "Enabled": True, "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0,
		"NetworkTransmit": True, "OwnerTransfer": 1, "Components": [mine], "Children": children,
		"__properties": {"NetworkInterpolation": True, "TimeScale": 1, "WantsSystemScene": True, "Metadata": {}},
		"__variables": [],
	}
	doc = {"RootObject": root, "ResourceVersion": 2, "ShowInMenu": False, "MenuPath": None, "MenuIcon": None,
		   "DontBreakAsTemplate": False, "__references": [], "__version": 2}
	path = os.path.join(ASSETS, rel.replace("/", os.sep))
	os.makedirs(os.path.dirname(path), exist_ok=True)
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		json.dump(doc, f, indent=2)


# -------------------------------------------------------------------------------------------------------------- render

def setup_render():
	scene = bpy.context.scene
	scene.render.engine = 'BLENDER_EEVEE'
	scene.render.resolution_x = 1000
	scene.render.resolution_y = 750
	scene.render.film_transparent = False
	world = bpy.data.worlds.new("sky")
	world.use_nodes = True
	world.node_tree.nodes["Background"].inputs[0].default_value = (0.55, 0.66, 0.8, 1)
	world.node_tree.nodes["Background"].inputs[1].default_value = 0.7
	scene.world = world
	sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN'))
	sun.data.energy = 3.2
	sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(35))
	scene.collection.objects.link(sun)
	cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
	cam.data.lens = 50
	scene.collection.objects.link(cam)
	scene.camera = cam
	ground = bpy.data.meshes.new("ground")
	bm = bmesh.new()
	bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=500)
	bm.to_mesh(ground)
	bm.free()
	g = bpy.data.objects.new("ground", ground)
	gm = bpy.data.materials.new("groundmat")
	gm.use_nodes = True
	gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.16, 0.11, 0.07, 1)  # needle duff
	g.data.materials.append(gm)
	scene.collection.objects.link(g)
	# a 1.8 m figure for scale
	pm = bpy.data.meshes.new("person")
	bm = bmesh.new()
	bmesh.ops.create_cone(bm, cap_ends=True, segments=8, radius1=0.25, radius2=0.2, depth=1.8)
	bmesh.ops.translate(bm, vec=Vector((0, 0, 0.9)), verts=bm.verts[:])
	bm.to_mesh(pm)
	bm.free()
	person = bpy.data.objects.new("person", pm)
	pmat = bpy.data.materials.new("personmat")
	pmat.use_nodes = True
	pmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.7, 0.2, 0.15, 1)
	person.data.materials.append(pmat)
	scene.collection.objects.link(person)
	return cam, g, person


def render(objs, path, cam, person, elev=24.0, yaw=35.0):
	scene = bpy.context.scene
	bpy.context.view_layer.update()
	shown = set(o.name for o in objs) | {"person"}
	for o in scene.objects:
		if o.type == 'MESH' and o.name != "ground":
			o.hide_render = o.name not in shown
	lo = Vector((1e9, 1e9, 1e9))
	hi = Vector((-1e9, -1e9, -1e9))
	for o in objs:
		for v in o.data.vertices:
			p = o.matrix_world @ v.co
			lo = Vector((min(lo.x, p.x), min(lo.y, p.y), min(lo.z, p.z)))
			hi = Vector((max(hi.x, p.x), max(hi.y, p.y), max(hi.z, p.z)))
	lo.z = max(lo.z, 0.0)
	person.location = (hi.x + 0.8, lo.y - 0.2, 0.0)
	hi.x += 1.1
	hi.z = max(hi.z, 1.8)
	centre = (lo + hi) * 0.5
	radius = max((hi - lo).length * 0.5, 0.05) + 0.6
	d = radius / math.tan(math.radians(18)) * 1.0
	dir = Vector((math.cos(math.radians(yaw)) * math.cos(math.radians(elev)), math.sin(math.radians(yaw)) * math.cos(math.radians(elev)), math.sin(math.radians(elev))))
	cam.location = centre + dir * d
	cam.rotation_euler = (-dir).to_track_quat('-Z', 'Y').to_euler()
	cam.data.clip_start = d * 0.01
	cam.data.clip_end = d * 10
	scene.render.filepath = path
	bpy.ops.render.render(write_still=True)


# --------------------------------------------------------------------------------------------------------------- build

def build_bits(mats, report):
	objs = []
	for i, (seed, size) in enumerate(BITS, 1):
		name = f"redwood_rock_bit{i}_{VERSION}"
		obj = hull(name, seed, size, points=11 + i, faces=30)
		finish_plain(obj, mats)
		objs.append(obj)
		lo, hi = bounds(obj)
		report.append(f"{name}: {tris(obj)} tris, {hi[0]-lo[0]:.2f} x {hi[1]-lo[1]:.2f} x {hi[2]-lo[2]:.2f} m")
	fbx = f"{ROCK_DIR}/redwood_rock_bits_{VERSION}.fbx"
	export_fbx(objs, fbx)
	for o in objs:
		write_vmdl(f"{ROCK_DIR}/{o.name}.vmdl", fbx, [o.name], True, "stone bit a mined redwood slab section bursts into")
	for o in objs:
		o.hide_render = True
	return [f"{ROCK_DIR}/{o.name}.vmdl" for o in objs]


def build_slab(index, spec, mats, bit_models, cam, person, report):
	seed, foot, exposed, thick, tilt, skirt, count, up_c, side_c, faces = spec
	name = f"redwood_slab{index}_{VERSION}"
	whole = slab(name + "_whole", seed, foot, exposed, thick, tilt, skirt, up_c, side_c, faces)
	fw, fl = footprint_at_ground(whole)
	lo, hi = bounds(whole)
	whole_volume = volume(whole)
	for attempt in range(8):  # the exact boolean now and then drops a cell: re-roll the seeds until the sections add up
		rnd = random.Random(seed + 17 + attempt * 101)
		seeds = section_seeds(rnd, whole, count, hi[2])
		sections = []
		for a, s in enumerate(seeds):
			clip = []
			for b, o in enumerate(seeds):
				if a != b:
					clip.append(((s + o) * 0.5, (o - s).normalized()))
			sections.append(intersect(whole, convex_cell(clip), f"{name}_s{a}"))
		keep = [s for s in sections if s is not None]
		total = sum(volume(s) for s in keep)
		if len(keep) == len(seeds) == count and abs(total - whole_volume) < whole_volume * 0.01:
			break
		print(f"[redwood_boulders] {name}: attempt {attempt} kept {len(keep)}/{len(seeds)} sections, volume {total:.2f} of {whole_volume:.2f} m^3 - retrying")
		for s in keep:
			bpy.data.objects.remove(s, do_unlink=True)
	else:
		raise RuntimeError(f"{name}: could not section cleanly")
	bpy.data.objects.remove(whole, do_unlink=True)
	for k, s in enumerate(keep):
		s.name = f"{name}_s{k}"
		s.data.name = s.name
	# every section reaches below the ground line (the skirt), so all are grounded; side-by-side cuts are not support
	grounded = [k for k, s in enumerate(keep) if bounds(s)[0][2] < 0.15]
	if len(grounded) != len(keep):
		print(f"[redwood_boulders] WARNING {name}: sections {set(range(len(keep))) - set(grounded)} do not reach the ground")
	allobjs = []
	lod0 = []
	for s in keep:
		levels = lods(s)
		for o in levels:
			finish(o, mats)
		allobjs += levels
		lod0.append(levels[0])
		for o in levels[1:]:
			o.hide_render = True
	fbx = f"{ROCK_DIR}/{name}.fbx"
	export_fbx(allobjs, fbx)
	for k in range(len(keep)):
		write_vmdl(f"{ROCK_DIR}/{name}_s{k}.vmdl", fbx, [f"{name}_s{k}_lod{j}" for j in range(4)], False, f"{name} section {k}")
	write_prefab(name, [f"{ROCK_DIR}/{name}_s{k}.vmdl" for k in range(len(keep))], grounded, [], bit_models)
	lods_tris = [sum(tris(o) for o in allobjs if o.name.endswith(f"_lod{j}")) for j in range(4)]
	report.append(f"{name}: {len(keep)} sections, ground footprint {fw:.1f} x {fl:.1f} m, exposed {hi[2]:.2f} m, "
				  f"skirt {-lo[2]:.2f} m, tris per lod {lods_tris}, grounded {grounded}")
	renders = []
	for tag, elev, yaw in (("", 24.0, 35.0), ("_low", 8.0, 200.0)):
		p = os.path.join(RENDER_DIR, f"{name}{tag}.png")
		render(lod0, p, cam, person, elev=elev, yaw=yaw)
		renders.append(p)
	# exploded sections
	c0 = Vector(((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, 0.0))
	for o in lod0:
		b0, b1 = bounds(o)
		c = Vector(((b0[0] + b1[0]) / 2, (b0[1] + b1[1]) / 2, 0.0))
		d = c - c0
		o.location = d.normalized() * 0.8 if d.length > 1e-4 else Vector()
	p = os.path.join(RENDER_DIR, f"{name}_sections.png")
	render(lod0, p, cam, person, elev=40.0, yaw=35.0)
	renders.append(p)
	# lod3 view, to check the far look keeps the moss cap
	lod3 = [o for o in allobjs if o.name.endswith("_lod3")]
	for o in lod0:
		o.location = (0, 0, 0)
		o.hide_render = True
	p = os.path.join(RENDER_DIR, f"{name}_lod3.png")
	render(lod3, p, cam, person, elev=24.0, yaw=35.0)
	renders.append(p)
	for o in lod3:
		o.hide_render = True
	return renders


def main():
	only = [int(a) for a in sys.argv[sys.argv.index("--") + 1:]] if "--" in sys.argv else []
	bpy.ops.wm.read_factory_settings(use_empty=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0
	os.makedirs(RENDER_DIR, exist_ok=True)
	images = make_textures()
	mats = make_materials(images)
	cam, ground, person = setup_render()
	report = []
	bit_models = build_bits(mats, report)
	renders = []
	for i, spec in enumerate(SLABS, 1):
		if only and i not in only:
			continue
		renders += build_slab(i, spec, mats, bit_models, cam, person, report)
	print("[redwood_boulders] ---")
	for line in report:
		print("[redwood_boulders]", line)
	for r in renders:
		print("[redwood_boulders] render", r)


main()
