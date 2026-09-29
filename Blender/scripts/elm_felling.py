"""Felling pieces for the elm trees, imported by create_elm_tree.py.

Chop flow in game (Code/Environment/ChopableTree.cs):
    standing tree --chop--> its stump (same model below the cut, so the swap is seamless)
                            + one universal log stood on the stump top, point down, tipped over
    log --chop--> two log halves (crosswise, each half the length)
    log half --chop--> wood drops;  stump --chop--> a little wood, then gone

Everything is in true meters (fbx exported 1:1, vmdl import_scale 0.4 = 40 u/m):
    log       8 m long, 1.2 m across, the bottom 1.2 m is a cone; origin at the cone point
    log half  4 m long, 1.2 m across, flat end-grain at both ends; origin at its centre
    stump     each tree's own wood mesh cut flat at STUMP_H, end-grain cap; origin at the base
"""
import json
import math
import os
import random
import uuid

import bmesh
import bpy
import numpy as np

LOG_LEN = 8.0
LOG_R = 0.6
LOG_POINT = 1.2
HALF_LEN = LOG_LEN / 2
STUMP_H = 1.0
SIDES = 14

# Chop tuning (a stone axe hits for 4): tree 10 hits, log 4, each half 3, stump 3.
TREE_HP, LOG_HP, HALF_HP, STUMP_HP = 40, 16, 12, 12
HALF_WOOD = (8, 10)
STUMP_WOOD = (3, 4)

ENDGRAIN_RAMP = [(88, 62, 40), (118, 86, 56), (146, 110, 72), (170, 132, 88), (190, 152, 104), (208, 172, 122), (224, 192, 142)]


def make_endgrain_texture(tex):
	"""Cut face of a log: warm growth rings (7-tone ramp, one ring per ~3 px) wobbling around a
	slightly off-centre pith, a dark pith dot, one radial check crack and a grey-brown bark rim."""
	rng = np.random.default_rng(21)
	c = (np.arange(tex) + 0.5) / tex * 2 - 1
	x, y = np.meshgrid(c, c)
	x0, y0 = 0.06, -0.04
	r = np.hypot(x - x0, y - y0)
	th = np.arctan2(y - y0, x - x0)
	wob = 0.025 * np.sin(3 * th + 1.3) + 0.015 * np.sin(7 * th + 0.4)
	ring = np.mod((r + wob) * 10.5, 1.0)
	idx = np.where(ring < 0.3, 2, 4) + np.round(1.2 * (1 - r)).astype(int)      # late wood darker
	idx = idx + (rng.random((tex, tex)) < 0.06)                                 # a few flecks
	crack = (np.abs(np.mod(th - 0.9 + np.pi, 2 * np.pi) - np.pi) < 0.05 / np.maximum(r, 0.05)) & (r > 0.12) & (r < 0.78)
	idx = np.where(crack, 0, idx)
	idx = np.where(r < 0.07, 1, idx)
	idx = np.clip(idx, 0, 6)
	rgb = np.array(ENDGRAIN_RAMP, float)[idx]
	rim = np.hypot(x, y)
	rgb[rim > 0.86] = (92, 84, 74)
	rgb[rim > 0.94] = (70, 63, 55)
	return np.dstack([rgb, np.full((tex, tex), 255)]).astype(np.uint8)


# ---------------------------------------------------------------------------- meshes

def build_log(name, length, radius, point, bark_tile, bark_mat, grain_mat, seed):
	"""Low-poly log along +Z: `point` metres of cone at the bottom (0 = flat end), flat end-grain
	cap(s), bark at a fixed texel size, a little per-ring wobble so it is not a perfect cylinder.
	Origin at the bottom (cone point / cap centre)."""
	rng = random.Random(seed)
	bm = bmesh.new()
	uvl = bm.loops.layers.uv.new("UVMap")
	zs = [0.0]
	if point > 0:
		zs += [point * f for f in (0.35, 0.7, 1.0)]
	n = max(2, int(round((length - (point if point > 0 else 0)) / 0.5)))
	start = point if point > 0 else 0.0
	zs += [start + (length - start) * k / n for k in range(1, n + 1)]
	rings = []
	for z in zs:
		if point > 0 and z <= point:
			rr = radius * (z / point) ** 0.85
		else:
			rr = radius * (1 + rng.uniform(-0.04, 0.04))
		sway = (0.03 * math.sin(z * 0.9 + seed), 0.03 * math.cos(z * 0.7 + seed))
		ring = []
		for k in range(SIDES + 1):                       # last = seam duplicate for clean UVs
			a = 2 * math.pi * (k % SIDES) / SIDES
			ring.append(bm.verts.new((sway[0] + rr * math.cos(a), sway[1] + rr * math.sin(a), z)))
		rings.append((z, ring))
	circ = 2 * math.pi * radius / bark_tile
	for (z0, r0), (z1, r1) in zip(rings, rings[1:]):
		for k in range(SIDES):
			quad = (r0[k], r0[k + 1], r1[k + 1], r1[k])
			if r0[k].co == r0[k + 1].co:                 # cone point: triangle
				f = bm.faces.new((r0[k], r1[k + 1], r1[k]))
				uvs = [((k + 0.5) / SIDES * circ, z0 / bark_tile), ((k + 1) / SIDES * circ, z1 / bark_tile),
					   (k / SIDES * circ, z1 / bark_tile)]
			else:
				f = bm.faces.new(quad)
				uvs = [(k / SIDES * circ, z0 / bark_tile), ((k + 1) / SIDES * circ, z0 / bark_tile),
					   ((k + 1) / SIDES * circ, z1 / bark_tile), (k / SIDES * circ, z1 / bark_tile)]
			f.smooth = True
			f.material_index = 0
			for loop, uv in zip(f.loops, uvs):
				loop[uvl].uv = uv

	def cap(ring, up):
		pts = [v.co.copy() for v in ring[:SIDES]]
		cz = pts[0].z
		cx = sum(p.x for p in pts) / SIDES
		cy = sum(p.y for p in pts) / SIDES
		vs = [bm.verts.new(p) for p in pts]
		f = bm.faces.new(vs if up else list(reversed(vs)))
		f.material_index = 1
		for loop in f.loops:
			p = loop.vert.co
			loop[uvl].uv = (0.5 + (p.x - cx) / (2 * radius) * 0.96, 0.5 + (p.y - cy) / (2 * radius) * 0.96)
		return cz

	cap(rings[-1][1], True)
	if point <= 0:
		cap(rings[0][1], False)
	bmesh.ops.remove_doubles(bm, verts=[v for _, r in rings[:1] for v in r], dist=1e-5) if point > 0 else None
	me = bpy.data.meshes.new(name)
	bm.to_mesh(me)
	bm.free()
	me.materials.append(bark_mat)
	me.materials.append(grain_mat)
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	return obj


def build_stump(name, wood, grain_mat):
	"""The tree's own wood mesh below STUMP_H (roots, flare and all), cut flat and capped with end
	grain. Identical to the standing tree below the cut, so the in-game swap is seamless."""
	bm = bmesh.new()
	bm.from_mesh(wood.data)
	geom = list(bm.verts) + list(bm.edges) + list(bm.faces)
	bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-4, plane_co=(0, 0, STUMP_H), plane_no=(0, 0, 1),
						   clear_outer=True)
	# keep only the piece connected to the trunk base (drops any limb tip that dipped below the cut)
	base = min(bm.verts, key=lambda v: v.co.x * v.co.x + v.co.y * v.co.y + abs(v.co.z - 0.3))
	keep, stack = {base}, [base]
	while stack:
		v = stack.pop()
		for e in v.link_edges:
			o = e.other_vert(v)
			if o not in keep:
				keep.add(o)
				stack.append(o)
	bmesh.ops.delete(bm, geom=[v for v in bm.verts if v not in keep], context='VERTS')
	edges = [e for e in bm.edges if e.is_boundary]
	res = bmesh.ops.holes_fill(bm, edges=edges, sides=0)
	uvl = bm.loops.layers.uv.active
	cap = res["faces"]
	if cap:
		pts = [v.co for f in cap for v in f.verts]
		cx = sum(p.x for p in pts) / len(pts)
		cy = sum(p.y for p in pts) / len(pts)
		rad = max(math.hypot(p.x - cx, p.y - cy) for p in pts) or 1.0
		for f in cap:
			f.material_index = 1
			f.smooth = False
			for loop in f.loops:
				p = loop.vert.co
				loop[uvl].uv = (0.5 + (p.x - cx) / (2 * rad) * 0.96, 0.5 + (p.y - cy) / (2 * rad) * 0.96)
	me = bpy.data.meshes.new(name)
	bm.to_mesh(me)
	bm.free()
	for m in wood.data.materials:
		me.materials.append(m)
	me.materials.append(grain_mat)
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	return obj


# ---------------------------------------------------------------------------- vmdl / prefabs

def single_vmdl(name, asset_dir, remaps, physics):
	"""One render mesh (the whole fbx) + physics: 'mesh' (static, exact) or 'hull' (for a
	Rigidbody: convex hull)."""
	remap = "".join(f"""							{{
								from = "{src}.vmat"
								to = "{asset_dir}/{src}.vmat"
							}},
""" for src in remaps)
	if physics == "hull":
		phys = """					{
						_class = "PhysicsHullFromRender"
						parent_bone = ""
						surface_prop = "wood"
						collision_tags = "solid"
						faceMergeAngle = 10.0
						maxHullVertices = 0
					},
"""
	else:
		phys = """					{
						_class = "PhysicsMeshFromRender"
						parent_bone = ""
						surface_prop = "wood"
						collision_tags = "solid"
					},
"""
	return f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
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
{remap}						]
						use_global_default = false
						global_default_material = "{asset_dir}/{remaps[0]}.vmat"
					}},
				]
			}},
			{{
				_class = "PhysicsShapeList"
				children =
				[
{phys}				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
					{{
						_class = "RenderMeshFile"
						filename = "{asset_dir}/{name}.fbx"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 0.4
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{{
							exclude_by_default = false
							exception_list = [  ]
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


def _g():
	return str(uuid.uuid4())


def write_prefab(path, name, model, chop, dynamic, template_path):
	"""Prefab from the project's existing tree prefab (same root / __properties layout): model
	renderer + collider, DamageReceiver, ChopableTree(chop), and a Rigidbody when dynamic."""
	with open(template_path, encoding="utf-8") as f:
		d = json.load(f)
	root = d["RootObject"]
	root["__guid"] = _g()
	root["Name"] = name
	root["Tags"] = "" if dynamic else "grapple"
	root["NetworkMode"] = 1 if dynamic else 0
	comps = [
		{"__type": "Sandbox.ModelRenderer", "__guid": _g(), "__enabled": True, "Model": model,
		 "RenderType": "On", "Tint": "1,1,1,1", "BodyGroups": 18446744073709551615},
		{"__type": "Sandbox.ModelCollider", "__guid": _g(), "__enabled": True, "Model": model,
		 "IsTrigger": False, "Static": not dynamic},
	]
	if dynamic:
		comps.append({"__type": "Sandbox.Rigidbody", "__guid": _g(), "__enabled": True, "Gravity": True})
	comps.append({"__type": "Survival.DamageReceiver", "__guid": _g(), "__enabled": True, "DamageMultiplier": 1})
	c = {"__type": "Survival.ChopableTree", "__guid": _g(), "__enabled": True, "RequireAxe": True,
		 "WoodResourceId": "resource_woodBasic", "LogChop": False}
	c.update(chop)
	comps.append(c)
	root["Components"] = comps
	root["Children"] = []
	os.makedirs(os.path.dirname(path), exist_ok=True)
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		json.dump(d, f, indent=2)
