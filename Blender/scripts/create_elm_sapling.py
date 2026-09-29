"""Elm saplings — environment_elmsapling1..5_v<VERSION>, 1.8-2.25 m tall young elms.

Run headless:
    blender -b --python "Blender/scripts/create_elm_sapling.py" -- render

Reuses the big elms' pipeline pieces from create_elm_tree.py (leaf texture, pixel-block PNG
writer, materials, two-object vmdl) but has its own VERSION, so either set can be regenerated
without touching the other. Real scale in meters (vmdl import_scale 0.4 = 40 u/m).

Per sapling (young elm, "whip" stage):
    trunk      the same ~3.5 cm as every branch, with a short flare only in the bottom ~25 cm
    branches   6-9 thin side shoots from ~25 % of the height up (leaves cover eye height: they break sight lines), some with a sub-shoot
    leaves     small clusters on every shoot tip and the leader; folded cards 0.55 m (leaves ~9 cm,
               a size down from the big elms' 1.8 m cards), 3-5 cards fanned out on each stick end only
    wood       ONE continuous mesh (tubes + joint balls, voxel remeshed at 4 mm, decimated)
    bark       its own texture: young elm bark is smooth grey with small dark vertical lenticel
               dashes (no ridges yet); 1.6 cm per texel so the dashes read on a thin stem
    chopping   plain ChopableTree (no stump / log): 12 HP (3 stone-axe hits), 2-4 wood
"""
import math
import os
import random
import sys

import bpy
import bmesh
import numpy as np
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import create_elm_tree as elm   # guarded main(): importing builds nothing
import elm_felling as fell

VERSION = 12
TAG = f"_v{VERSION}"
BARK = f"elm_sapling_bark{TAG}"
LEAVES = f"elm_sapling_leaves{TAG}"
MODEL_DIR = elm.MODEL_DIR
ASSET_DIR = elm.ASSET_DIR
PREFAB_DIR = elm.PREFAB_DIR
PREVIEW_DIR = os.path.join(elm.ROOT, "Blender", "blenderprojects", "elm_sapling_preview")
BLEND_OUT = os.path.join(elm.ROOT, "Blender", "blenderprojects", f"environment_elmsapling{TAG}.blend")
ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

BARK_TILE = 1.0      # m per 64 px tile = ~1.6 cm per texel on a thin stem
VOXEL = 0.004
WOOD_TRIS = 2200
CARD_LEN = 0.55
STICK_R = 0.02       # every branch and the trunk above its base: one thickness (before height scaling)
BASE_R = 0.032       # slightly thicker at the very bottom of the trunk
HP, WOOD = 12, (2, 4)
# LOD chain, same format as create_elm_tree.TREE_LODS: (switch_threshold, wood tris, cards kept, card scale)
SAPLING_LODS = [
	(0.0, None, 1.0, 1.0),
	(100.0, 500, 1.0, 1.0),    # Mark's LOD distances: 100 / 200 m (a third level would be 300)
	(200.0, 150, 0.6, 1.2),    # gentle step: keep 60 % of the cards (nested), grow them a little
]
SAPLING_PHYSICS_LOD = 1
UP = Vector((0, 0, 1))

# name, seed, total height incl. leaves (m), base radius (m), side shoots, lean
VARIANTS = [
	(f"environment_elmsapling1{TAG}", 3, 2.25, 0.038, 8, 0.06),
	(f"environment_elmsapling2{TAG}", 8, 1.80, 0.030, 6, 0.03),
	(f"environment_elmsapling3{TAG}", 14, 2.10, 0.034, 9, 0.10),
	(f"environment_elmsapling4{TAG}", 21, 1.95, 0.032, 7, 0.02),
	(f"environment_elmsapling5{TAG}", 33, 2.20, 0.036, 7, 0.13),   # the leaning one
]

SAPLING_BARK = [(96, 96, 92), (118, 118, 112), (134, 134, 127), (148, 147, 140), (160, 159, 151)]
DASH = [(36, 34, 32), (58, 55, 51)]


def make_sapling_bark():
	"""Smooth young-elm bark: pale grey in faint vertical tonal streaks, with scattered tiny dark
	vertical dashes (lenticels, 1 px wide, 2-4 px tall), each with a lighter pixel above it."""
	rng = np.random.default_rng(17)
	T = elm.TEX
	x = np.arange(T)
	streak = np.zeros((T, T))
	for k in range(3):                                       # tile-safe vertical streaks
		streak += np.sin(2 * math.pi * ((k + 2) * x / T + rng.random()))[None, :] / (k + 1)
	idx = np.clip(np.round(2 + streak * 0.9 + rng.normal(0, 0.35, (T, T))), 0, 4).astype(int)
	rgb = np.array(SAPLING_BARK, float)[idx]
	for _ in range(46):
		cx, cy, h = rng.integers(0, T), rng.integers(0, T), rng.integers(2, 5)
		for dy in range(h):
			rgb[(cy + dy) % T, cx] = DASH[0] if 0 < dy < h - 1 or h == 2 else DASH[1]
		rgb[(cy - 1) % T, cx] = SAPLING_BARK[4]
	return np.dstack([rgb, np.full((T, T), 255)]).astype(np.uint8)


# ---------------------------------------------------------------------------- skeleton

def build_skeleton(rng, height, r0, shoots, lean):
	"""Returns (nodes as (pos, r, parent), tips) — tips are leaf-cluster centres."""
	nodes = []

	def add(p, r, parent):
		nodes.append((p, r, parent))
		return len(nodes) - 1

	lean_dir = Vector((math.cos(rng.uniform(0, 6.3)), math.sin(rng.uniform(0, 6.3)), 0)) * lean
	trunk = [add(Vector((0, 0, -0.05)), BASE_R, -1)]
	d = (UP + lean_dir).normalized()
	# a short flare right at the ground (one node 12 cm up), then the uniform stick thickness
	trunk.append(add(Vector((0, 0, 0.12)), (BASE_R + STICK_R) / 2, trunk[-1]))
	steps = 12
	for k in range(1, steps + 1):
		f = k / steps
		d = (d + elm.rand_unit(rng) * 0.05 + lean_dir * 0.1).normalized()
		p = nodes[trunk[-1]][0] + d * ((height - 0.12) / steps)
		trunk.append(add(p, STICK_R, trunk[-1]))   # uniform above the ground flare
	tips = [(nodes[trunk[-1]][0], d)]
	az = rng.uniform(0, 6.3)

	def shoot(j, direction, length, r_start, segs):
		path = [j]
		dd = direction
		for k in range(1, segs + 1):
			dd = (dd + UP * 0.07 + elm.rand_unit(rng) * 0.08).normalized()   # curls gently upward
			p = nodes[path[-1]][0] + dd * (length / segs)
			path.append(add(p, STICK_R, path[-1]))   # uniform thickness
		return path, dd

	for s in range(shoots):
		t = 0.25 + 0.65 * s / max(1, shoots - 1) + rng.uniform(-0.03, 0.03)   # from low down: eye-height cover
		j = trunk[min(len(trunk) - 2, max(2, int(round(t * steps))))]
		az += math.radians(137.5) + rng.uniform(-0.4, 0.4)
		up_deg = rng.uniform(22, 42) + 12 * t          # spread outward, not hugging the stem
		dirn = elm.tilt(UP, math.radians(90 - up_deg), az) if False else \
			Vector((math.cos(az) * math.cos(math.radians(up_deg)), math.sin(az) * math.cos(math.radians(up_deg)),
					math.sin(math.radians(up_deg))))
		length = (0.55 - 0.3 * t) * height * rng.uniform(0.8, 1.15) * 1.1
		rs = STICK_R
		path, dd = shoot(j, dirn, length, rs, 4)
		tips.append((nodes[path[-1]][0], dd))
		if rng.random() < 0.9:                                  # a sub-shoot off the middle
			m = path[2]
			sub = (dd + Vector((-dd.y, dd.x, 0)) * rng.choice((-0.8, 0.8))).normalized()
			sp, sd = shoot(m, sub, length * 0.45, STICK_R, 3)
			tips.append((nodes[sp[-1]][0], sd))
	return nodes, tips


# ---------------------------------------------------------------------------- meshes

def build_wood(name, nodes):
	bm = bmesh.new()
	SIDES = 8
	for p, r, parent in nodes:
		ball = bmesh.ops.create_icosphere(bm, subdivisions=1, radius=r)
		bmesh.ops.translate(bm, verts=ball["verts"], vec=p)
		if parent < 0:
			continue
		q, rq, _ = nodes[parent]
		d = (p - q).normalized()
		a1 = d.orthogonal().normalized()
		a2 = d.cross(a1)
		rings = [[bm.verts.new(c + (a1 * math.cos(k * 2 * math.pi / SIDES) + a2 * math.sin(k * 2 * math.pi / SIDES)) * rr)
				  for k in range(SIDES)] for c, rr in ((q, rq), (p, r))]
		for k in range(SIDES):
			k2 = (k + 1) % SIDES
			bm.faces.new((rings[0][k], rings[0][k2], rings[1][k2], rings[1][k]))
		bm.faces.new(list(reversed(rings[0])))
		bm.faces.new(rings[1])
	me = bpy.data.meshes.new(name)
	bm.to_mesh(me)
	bm.free()
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	elm.bake(obj, ('REMESH', {"mode": 'VOXEL', "voxel_size": VOXEL, "adaptivity": 0.0}),
			 ('SMOOTH', {"factor": 0.5, "iterations": 4}))
	# keep the main piece only (a hair-thin tip can pinch off in the voxel pass)
	bm = bmesh.new()
	bm.from_mesh(obj.data)
	seen, parts = set(), []
	for v in bm.verts:
		if v in seen:
			continue
		part, stack = [], [v]
		seen.add(v)
		while stack:
			c = stack.pop()
			part.append(c)
			for e in c.link_edges:
				o = e.other_vert(c)
				if o not in seen:
					seen.add(o)
					stack.append(o)
		parts.append(part)
	parts.sort(key=len)
	for part in parts[:-1]:
		bmesh.ops.delete(bm, geom=part, context='VERTS')
	bm.to_mesh(obj.data)
	bm.free()
	tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
	elm.bake(obj, ('DECIMATE', {"ratio": min(1.0, WOOD_TRIS / tris)}))
	obj.data.shade_smooth()
	return obj


def bark_uvs(me, nodes):
	"""Per face: nearest stem segment; u around it, v along the stem's cumulative length."""
	N = len(nodes)
	vcoord = [0.0] * N
	ref = [Vector((1, 0, 0))] * N
	for i in range(1, N):
		p = nodes[i][2]
		seg = nodes[i][0] - nodes[p][0]
		d = seg.normalized()
		vcoord[i] = vcoord[p] + seg.length / BARK_TILE
		rr = ref[p] - d * ref[p].dot(d)
		ref[i] = (rr if rr.length > 1e-4 else d.orthogonal()).normalized()
	segs = [(nodes[i][2], i) for i in range(1, N)]
	A = np.array([nodes[a][0] for a, _ in segs])
	AB = np.array([nodes[b][0] for _, b in segs]) - A
	L2 = (AB ** 2).sum(1)
	C = np.array([f.center for f in me.polygons])
	fseg = np.zeros(len(C), int)
	for c0 in range(0, len(C), 256):
		AP = C[c0:c0 + 256, None, :] - A[None]
		t = np.clip((AP * AB[None]).sum(2) / L2[None], 0, 1)
		fseg[c0:c0 + 256] = ((AP - t[..., None] * AB[None]) ** 2).sum(2).argmin(1)
	D = AB / np.sqrt(L2)[:, None]
	R0 = np.array([ref[b] for _, b in segs])
	S0 = np.cross(D, R0)
	VA = np.array([vcoord[a] for a, _ in segs])
	VB = np.array([vcoord[b] for _, b in segs])
	co = np.array([v.co for v in me.vertices])
	uv = me.uv_layers.new(name="UVMap")
	for poly in me.polygons:
		si = fseg[poly.index]
		vis = [me.loops[li].vertex_index for li in poly.loop_indices]
		Pp = co[vis] - A[si]
		t = Pp @ AB[si] / L2[si]
		rad = Pp - np.outer(t, AB[si])
		ang = np.arctan2(rad @ S0[si], rad @ R0[si])
		period = 2 * np.pi * np.maximum(np.linalg.norm(rad, axis=1), 0.01) / BARK_TILE
		u = ang / (2 * np.pi) * period
		v = VA[si] + (VB[si] - VA[si]) * t
		for k in range(1, len(u)):
			if u[k] - u[0] > period[k] / 2:
				u[k] -= period[k]
			elif u[0] - u[k] > period[k] / 2:
				u[k] += period[k]
		for li, uu, vv in zip(poly.loop_indices, u, v):
			uv.data[li].uv = (uu, vv)


def build_leaves(name, tips, rng):
	"""Leaves only on the ends of the sticks: 3-5 folded cards per tip, fanned out along the
	stick's direction (a small spray, not a ball), so the sapling stays open and twiggy."""
	verts, faces, uvs, homes = [], [], [], []
	fold = math.radians(28)
	for ci, (c, sd) in enumerate(tips):
		for _ in range(rng.randint(3, 5)):
			t = (sd + elm.rand_unit(rng) * 0.55 + UP * 0.15).normalized()
			n = elm.rand_unit(rng) + UP * 0.4
			n = (n - t * n.dot(t))
			n = n.normalized() if n.length > 1e-3 else t.orthogonal()
			L = CARD_LEN * rng.uniform(0.8, 1.1)
			side = t.cross(n).normalized()
			w = L * 0.5
			b0 = c - t * L * 0.15
			tip = b0 + t * L
			lo = side * (-math.cos(fold) * w) + n * (math.sin(fold) * w)
			ro = side * (math.cos(fold) * w) + n * (math.sin(fold) * w)
			i = len(verts)
			verts.extend([b0, tip, b0 + lo, tip + lo, b0 + ro, tip + ro])
			faces.append((i + 2, i + 0, i + 1, i + 3))
			uvs.append(((0, 0), (0.5, 0), (0.5, 1), (0, 1)))
			faces.append((i + 0, i + 4, i + 5, i + 1))
			uvs.append(((0.5, 0), (1, 0), (1, 1), (0.5, 1)))
			homes += [ci, ci]
	me = bpy.data.meshes.new(name)
	me.from_pydata(verts, [], faces)
	uvl = me.uv_layers.new(name="UVMap")
	for poly, quv in zip(me.polygons, uvs):
		for li, u in zip(poly.loop_indices, quv):
			uvl.data[li].uv = u
	normals = []
	for poly in me.polygons:
		c = tips[homes[poly.index]][0] - tips[homes[poly.index]][1] * 0.1
		for li in poly.loop_indices:
			q = me.vertices[me.loops[li].vertex_index].co
			fn = poly.normal.copy()
			o = (q - c).normalized()
			if fn.dot(o) < 0:
				fn = -fn
			normals.append((fn * 0.15 + o * 0.85).normalized())
	me.normals_split_custom_set(normals)
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	return obj, len(faces) // 2


# ---------------------------------------------------------------------------- main

def remove_old_versions():
	import glob
	old = []
	for d in (MODEL_DIR, PREFAB_DIR):
		old += [f for f in glob.glob(os.path.join(d, "environment_elmsapling*")) + glob.glob(os.path.join(d, "elm_sapling_*"))
				if TAG not in os.path.basename(f)]
	old += [f for f in glob.glob(os.path.join(os.path.dirname(BLEND_OUT), "environment_elmsapling*.blend*"))
			if TAG not in os.path.basename(f)]
	old += [f for f in glob.glob(os.path.join(PREVIEW_DIR, "*.png")) if TAG not in os.path.basename(f)]
	for f in old:
		os.remove(f)
	print(f"removed {len(old)} files from older sapling versions")


def render(made):
	os.makedirs(PREVIEW_DIR, exist_ok=True)
	scene = bpy.context.scene
	scene.render.engine = "BLENDER_EEVEE"
	scene.render.resolution_x, scene.render.resolution_y = 700, 900
	world = bpy.data.worlds.new("sky")
	world.use_nodes = True
	world.node_tree.nodes["Background"].inputs[0].default_value = (0.45, 0.62, 0.9, 1)
	world.node_tree.nodes["Background"].inputs[1].default_value = 0.8
	scene.world = world
	sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN'))
	sun.data.energy = 4.0
	sun.rotation_euler = (math.radians(50), 0, math.radians(35))
	scene.collection.objects.link(sun)
	gm = bpy.data.meshes.new("ground")
	gm.from_pydata([(-40, -40, 0), (40, -40, 0), (40, 40, 0), (-40, 40, 0)], [], [(0, 1, 2, 3)])
	ground = bpy.data.objects.new("ground", gm)
	gmat = bpy.data.materials.new("grass")
	gmat.use_nodes = True
	gmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.25, 0.4, 0.12, 1)
	gm.materials.append(gmat)
	scene.collection.objects.link(ground)
	bpy.ops.mesh.primitive_cylinder_add(radius=0.2, depth=1.8, location=(0.9, 0.3, 0.9))   # 1.8 m person
	cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
	cam.data.lens = 35
	scene.collection.objects.link(cam)
	scene.camera = cam

	def look(frm, to):
		cam.location = Vector(frm)
		cam.rotation_euler = (Vector(to) - Vector(frm)).to_track_quat("-Z", "Y").to_euler()

	for name, objs in made.items():
		for other, oo in made.items():
			for o in oo:
				o.hide_render = other != name
				o.location.x = 0
		look((0.4, -4.6, 1.3), (0.2, 0, 1.0))
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + ".png")
		bpy.ops.render.render(write_still=True)
		look((0.25, -0.9, 0.9), (0, 0, 0.75))
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + "_bark.png")
		bpy.ops.render.render(write_still=True)


def main():
	for o in list(bpy.data.objects):
		bpy.data.objects.remove(o, do_unlink=True)
	remove_old_versions()
	bark_png = os.path.join(MODEL_DIR, BARK + ".png")
	leaf_png = os.path.join(MODEL_DIR, LEAVES + ".png")
	elm.write_png(bark_png, make_sapling_bark())
	leaf = elm.make_leaf_texture()
	elm.write_png(leaf_png, leaf)
	mask = np.repeat(leaf[..., 3:4], 4, axis=2)
	mask[..., 3] = 255
	elm.write_png(os.path.join(MODEL_DIR, LEAVES + "_mask.png"), mask)
	with open(os.path.join(MODEL_DIR, BARK + ".vmat"), "w", newline="\n") as f:
		f.write(elm.VMAT_BARK.replace("elm_bark", BARK))
	with open(os.path.join(MODEL_DIR, LEAVES + ".vmat"), "w", newline="\n") as f:
		f.write(elm.VMAT_LEAVES.replace("elm_leaves", LEAVES))
	bark = elm.blender_material(BARK, bark_png, False)
	leaves_mat = elm.blender_material(LEAVES, leaf_png, True)

	made = {}
	for name, seed, height, r0, shoots, lean in VARIANTS:
		rng = random.Random(seed)
		nodes, tips = build_skeleton(rng, height, r0, shoots, lean)
		wood = build_wood(name + "_wood", nodes)
		bark_uvs(wood.data, nodes)
		wood.data.materials.append(bark)
		leaves, cards = build_leaves(name + "_leaves", tips, rng)
		leaves.data.materials.append(leaves_mat)
		made[name] = (wood, leaves)
		# the height in VARIANTS is the whole sapling, leaves included: scale the finished model to it
		top = max(v.co.z for o in (wood, leaves) for v in o.data.vertices)
		k = height / top
		for o in (wood, leaves):
			for v in o.data.vertices:
				v.co *= k
		zs = [v.co.z for v in leaves.data.vertices] + [v.co.z for v in wood.data.vertices]
		base = [v.co for v in wood.data.vertices if 0.05 < v.co.z < 0.15]
		dia = (max(c.x for c in base) - min(c.x for c in base) + max(c.y for c in base) - min(c.y for c in base)) / 2
		print(f"SAPLING {name}: height {max(zs):.2f} m, stem {dia * 100:.1f} cm across at 10 cm, "
			  f"wood islands {elm.island_count(wood.data)}, wood tris {sum(len(p.vertices) - 2 for p in wood.data.polygons)}, "
			  f"cards {cards} ({cards * 4} tris), clusters {len(tips)}")
		lod_objs = elm.build_lods(name, wood, leaves, SAPLING_LODS)
		merged = elm.merge_lod_meshes(name, wood, leaves, lod_objs)
		phys = wood if SAPLING_PHYSICS_LOD == 0 else lod_objs[(SAPLING_PHYSICS_LOD - 1) * 2]
		for o in bpy.data.objects:
			o.select_set(False)
		for o in merged + [phys]:
			o.select_set(True)
		bpy.context.view_layer.objects.active = merged[0]
		bpy.ops.export_scene.fbx(filepath=os.path.join(MODEL_DIR, name + ".fbx"), use_selection=True,
								 global_scale=1.0, apply_unit_scale=True, object_types={'MESH'},
								 mesh_smooth_type='OFF', path_mode='STRIP', embed_textures=False)
		with open(os.path.join(MODEL_DIR, name + ".vmdl"), "w", newline="\n") as f:
			f.write(elm.vmdl_text(name, SAPLING_LODS, SAPLING_PHYSICS_LOD).replace("elm_bark", BARK).replace("elm_leaves", LEAVES))
		for o in lod_objs + merged:
			o.hide_render = True
			o.hide_set(True)
		fell.write_prefab(os.path.join(PREFAB_DIR, name + ".prefab"), name, f"{ASSET_DIR}/{name}.vmdl",
						  {"MaxHealth": HP, "CurrentHealth": HP, "WoodDropMin": WOOD[0], "WoodDropMax": WOOD[1]},
						  False, elm.PREFAB_TEMPLATE)
	for i, (w, l) in enumerate(made.values()):
		w.location.x = l.location.x = (i - 2) * 2.5
	bpy.ops.wm.save_as_mainfile(filepath=BLEND_OUT)
	if "render" in ARGS:
		render(made)


if __name__ == "__main__":
	main()
