"""Crashed stasis pod: a point of interest in Clover Hills (Mark 2026-11-19: "smoke trails that lead players to crashed
stasis pods that are like totally crumpled and exploded. Maybe some burnt grass around them, should look like a crushed
soda can really lol. They can then whack it with a pickaxe and get some resources, like scrap").

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_crashed_pod.py

The wreck IS the spawn pod: this script imports create_stasis_pod.py, builds the same hull and door, then crushes them
the way a can crushes - the middle of the hull folds into three rows of alternating diamond dents (a Yoshimura buckle),
the length drops from 2.3 m to about 1.5 m, the waist is squeezed flat, the whole thing twists and kinks - and lays the
result head-first in the ground. The door is torn off and thrown a couple of metres, the window is gone.

Writes Assets/models/props/crashed_pod/ (Blender metres, vmdl import_scale 0.4 = 40 u/m; origin = the ground under the wreck):
    crashed_pod_v1_s0 .. s3 (.fbx / .vmdl)   the four sections of the Survival.MineableRock prefab, all sharing the origin:
                                             s0 buried head half, s1 the base half that rests on it, s2 the torn door,
                                             s3 a pile of torn hull plates. lod0-3 at 100 / 200 / 300 m.
    crashed_pod_v1_scorch                    the burnt ground: a low dome with a raised crater lip whose skirt runs below
                                             the terrain, so it meets a slope anywhere (no collider).
    crashed_pod_shard1..3_v1                 torn plates: the bits a section bursts into and the chips off a hit.
    crashed_pod_smoke_v1.png / .vmat         pale mottled 128 px tile for the smoke column's blobs (Survival.SmokeColumn tints it).
    crashed_pod_scorch_v1_mask.png           the burnt ground's cut-out rim (alpha test).
    crashed_pod_{shell,trim,pad,scorch}_v1.png / .vmat   256 px: the pod's own textures under soot and heat tint.
and Assets/prefabs/props/crashed_pod_v1.prefab (MineableRock -> resource_scrap, SmokeColumn, section children, Scorch).

One render per model lands in Blender/renders/crashed_pod/.
"""
import bpy
import bmesh
import json
import math
import os
import sys
import uuid

import numpy as np
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import create_stasis_pod as sp   # noqa: E402  (the intact pod: builders + textures)

ROOT = sp.ROOT
REL = "models/props/crashed_pod"
OUT_DIR = os.path.join(ROOT, "Assets", "models", "props", "crashed_pod")
PREFAB_REL = "prefabs/props/crashed_pod_v1.prefab"
RENDER_DIR = os.path.join(ROOT, "Blender", "renders", "crashed_pod")
NAME = "crashed_pod_v1"
SHELL, TRIM, PAD, SCORCH, SMOKE = (f"crashed_pod_{n}_v1" for n in ("shell", "trim", "pad", "scorch", "smoke"))
LOD_THRESHOLDS = (0.0, 400.0, 800.0, 1200.0)
LOD_RATIOS = (1.0, 0.5, 0.25, 0.12)
UNITS_PER_M = 40.0

# crush band along the pod's height (upright metres) and how much of it is left
CRUSH_Z0, CRUSH_Z1, CRUSH_KEEP = 0.50, 1.95, 0.42
LOD0_KEEP = 0.5              # share of the crushed hull's triangles lod0 keeps
SCORCH_PX = 1024             # burnt ground texture: 9.6 m across, so ~107 texels / m like the terrain tiles
SPLIT_Z = 0.86               # crushed height where the hull is cut into its two sections
SMOKE_ORIGIN = (0.35, 0.0, 0.9)   # metres: where the column leaves the wreck


# ------------------------------------------------------------------------------------------------------------ textures
def value_noise(rng, cells, size=256):
	"""Tileable value noise 0..1: a cells x cells grid, bilinear, wrapped."""
	g = rng.random((cells, cells))
	t = np.arange(size) * cells / size
	i0 = np.floor(t).astype(int) % cells
	i1 = (i0 + 1) % cells
	f = t - np.floor(t)
	f = f * f * (3 - 2 * f)
	rows = g[i0][:, i0] * (1 - f)[None, :] + g[i0][:, i1] * f[None, :]
	rows1 = g[i1][:, i0] * (1 - f)[None, :] + g[i1][:, i1] * f[None, :]
	return rows * (1 - f)[:, None] + rows1 * f[:, None]


def scorch_metal(img, rng, soot_amount, heat):
	"""A pod texture after the fire: darkened, soot in broad patches, a brown heat tint where the soot thins."""
	rgb = img[..., :3].astype(float)
	soot = 0.6 * value_noise(rng, 4) + 0.4 * value_noise(rng, 9)
	soot = np.clip((soot - 0.45) * 2.6, 0.0, 1.0)[..., None]
	lum = rgb.mean(axis=2, keepdims=True)
	burnt = lum * np.array((0.78, 0.66, 0.56))[None, None, :] * heat + rgb * (1.0 - heat) * 0.9
	char = lum * 0.2 + np.array((22.0, 20.0, 19.0))[None, None, :]
	out = burnt * (1.0 - soot * soot_amount) + char * (soot * soot_amount)
	return sp.rgba(out)


def make_scorch(rng):
	"""Burnt ground, planar over the 9.6 m disc at the terrain's own texel size (1024 px = 107 texels / m beside the ground's 128;
	at 256 px its texels were four times the lawn's - Mark 2026-11-23: "texture is so poor in the crash site"): charcoal in the middle, burnt soil round it, earth at the rim with grass
	flecks so the edge melts into the lawn. No flat zones and no clean outlines (Mark 2026-11-23: "looks horrible ...
	solid colors are also not allowed"): the bands meet through per-texel dither and every band carries grain."""
	n = SCORCH_PX
	c = (n - 1) / 2.0
	k = n // 256          # noise cell counts were tuned at 256 px: scale the texel-sized ones with the size
	yy, xx = np.mgrid[0:n, 0:n]
	r = np.hypot(xx - c, yy - c) / c
	wob = 0.7 * value_noise(rng, 5, n) + 0.3 * value_noise(rng, 11, n)
	speck = rng.random((n, n))
	rr = r + (wob - 0.5) * 0.34 + (speck - 0.5) * 0.11 + (value_noise(rng, 37, n) - 0.5) * 0.10 + (value_noise(rng, 37 * k, n) - 0.5) * 0.06

	def band(lo, hi):
		t = np.clip((rr - lo) / (hi - lo), 0.0, 1.0)
		return (t * t * (3 - 2 * t))[..., None]

	char = np.array((30.0, 28.0, 27.0))
	burnt = np.array((58.0, 44.0, 33.0))
	soil = np.array((92.0, 70.0, 47.0))
	img = char + (burnt - char) * band(0.40, 0.62)
	img = img + (soil - img) * band(0.66, 0.86)
	# grey ash drifted over the char, thinning at its edge
	ash = np.clip((value_noise(rng, 7, n) - 0.62) * 6.0 + (speck - 0.5) * 0.9, 0.0, 1.0) * (1.0 - band(0.45, 0.62)[..., 0])
	img = img + (np.array((84.0, 80.0, 76.0)) - img) * (ash * 0.75)[..., None]
	# streaks thrown outward from the middle (ejecta rays), broken up by the dither
	ang = np.arctan2(yy - c, xx - c)
	rays = np.clip((np.sin(ang * 9.0 + wob * 5.0) - 0.8) * 5.0, 0.0, 1.0) * band(0.45, 0.6)[..., 0] * (1.0 - band(0.82, 0.98)[..., 0])
	img = img + (char * 1.15 - img) * (rays * (0.45 + 0.5 * speck))[..., None]
	# grain: clods a few texels across, then texel noise; a few pale pebbles and dark cinders
	clod = 0.45 * value_noise(rng, 53, n) + 0.3 * value_noise(rng, 53 * k, n) + 0.25 * value_noise(rng, 97 * k, n)
	img *= (0.8 + 0.4 * clod)[..., None]
	img *= (0.93 + 0.14 * rng.random((n, n)))[..., None]
	bits = rng.random((n, n))
	img[bits > 0.992] *= 1.5
	img[bits < 0.012] *= 0.55
	# The rim is cut out, not drawn: a mask that thins the soil into loose clods (3 x 3 texels) from 2.4 m out and is
	# gone by 3.6 m, so the lawn shows between them and the disc has no outline (it was a brown slab with straight
	# polygon edges lying on the grass).
	blk = 3
	cells = -(-n // blk)
	coarse = np.repeat(np.repeat(rng.random((cells, cells)), blk, axis=0), blk, axis=1)[:n, :n]
	keep = coarse > np.clip((rr - 0.50) / 0.24, 0.0, 1.0)
	mask = np.zeros((n, n, 3))
	mask[keep] = 255.0
	return sp.rgba(np.clip(img, 0, 255)), sp.rgba(mask)


def make_smoke(rng):
	"""The smoke column's blobs: a pale, softly mottled 128 px tile (SmokeColumn tints it dark at the foot)."""
	n = 128
	m = 0.6 * value_noise(rng, 6, n) + 0.4 * value_noise(rng, 17, n)
	img = np.array((196.0, 196.0, 200.0))[None, None, :] * (0.9 + 0.2 * m)[..., None]
	return sp.rgba(np.clip(img, 0, 255))


def pixel_vmat(tex, roughness, specular, mask=None):
	cutout = "" if mask is None else f"""	F_ALPHA_TEST 1
	PixelAlphaCutoff "0.500"
	TextureTranslucency "{REL}/{mask}.png"
"""
	return f"""Layer0
{{
	shader "shaders/pixel_lit.shader"
{cutout}
	PixelRoughness "{roughness:.3f}"
	PixelSpecular "{specular:.3f}"
	PixelFlatShading "1.000"
	TextureColor "{REL}/{tex}.png"
}}
"""


# ---------------------------------------------------------------------------------------------------------- the crush
def crush(p):
	"""Upright pod point (metres) -> the same point on the crushed can."""
	x, y, z = p.x, p.y, p.z
	theta = math.atan2(y, x)
	r = math.hypot(x, y)
	u = min(max((z - CRUSH_Z0) / (CRUSH_Z1 - CRUSH_Z0), 0.0), 1.0)
	if z <= CRUSH_Z0:
		zn = z
	elif z <= CRUSH_Z1:
		zn = CRUSH_Z0 + (z - CRUSH_Z0) * CRUSH_KEEP
	else:
		zn = CRUSH_Z0 + (CRUSH_Z1 - CRUSH_Z0) * CRUSH_KEEP + (z - CRUSH_Z1) * 0.9
	if 0.0 < u < 1.0:
		# Yoshimura buckle: three rows of four diamond dents, every other row turned half a dent; the row seams bulge
		row = u * 3.0
		f = row - math.floor(row)
		pleat = math.sin(math.pi * f)
		lobes = 0.5 + 0.5 * math.cos(4.0 * theta + (int(row) % 2) * math.pi)
		r *= 1.0 - 0.5 * (pleat ** 0.7) * lobes + 0.13 * (1.0 - pleat)
	# the whole can twists a little over its length
	tw = math.radians(34.0) * min(max((z - 0.3) / 2.0, 0.0), 1.0)
	theta += tw
	x, y = r * math.cos(theta), r * math.sin(theta)
	# squeezed flat across the waist, kinked forward above it
	y *= 1.0 - 0.34 * math.exp(-(((zn - 0.82) / 0.3) ** 2))
	x += 0.26 * max(0.0, zn - 0.7) ** 1.3
	# small dents everywhere (smooth, so neighbouring faces stay joined)
	x += 0.022 * math.sin(11.0 * y + 7.0 * zn)
	y += 0.022 * math.sin(9.0 * x + 13.0 * zn + 1.3)
	zn += 0.018 * math.sin(8.0 * x + 10.0 * y)
	return Vector((x, y, zn))


def obj_bm(obj):
	bm = bmesh.new()
	bm.from_mesh(obj.data)
	return bm


def bm_back(bm, obj):
	bm.to_mesh(obj.data)
	bm.free()
	obj.data.update()
	for poly in obj.data.polygons:
		poly.use_smooth = False


def subdivide_and_crush(obj, drop_material=None):
	bm = obj_bm(obj)
	if drop_material is not None:
		bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index == drop_material], context='FACES')
	# one cut through every long edge of the hull so the buckle has vertices to fold on
	long_edges = [e for e in bm.edges if e.calc_length() > 0.32]
	bmesh.ops.subdivide_edges(bm, edges=long_edges, cuts=1, use_grid_fill=True)
	bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
	for v in bm.verts:
		v.co = crush(v.co)
	bm_back(bm, obj)


def cut(obj, name, keep_above):
	"""A copy of the crushed hull on one side of SPLIT_Z (pod space)."""
	c = obj.copy()
	c.data = obj.data.copy()
	c.name = name
	c.data.name = name
	bpy.context.scene.collection.objects.link(c)
	bm = obj_bm(c)
	bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=(0, 0, SPLIT_Z), plane_no=(0.18, 0.1, 1.0),
						   clear_inner=keep_above, clear_outer=not keep_above)
	bm_back(bm, c)
	return c


def planar_uvs(obj, scale):
	uv = obj.data.uv_layers.get("UVMap") or obj.data.uv_layers.new(name="UVMap")
	for poly in obj.data.polygons:
		n = poly.normal
		ax = Vector((0, 1, 0)) if abs(n.x) >= abs(n.y) and abs(n.x) >= abs(n.z) else Vector((1, 0, 0))
		ay = n.cross(ax).normalized()
		ax = ay.cross(n).normalized()
		for li in poly.loop_indices:
			co = obj.data.vertices[obj.data.loops[li].vertex_index].co
			uv.data[li].uv = (co.dot(ax) * scale, co.dot(ay) * scale)


def plate(name, mat, rng, size, bend, thickness=0.035):
	"""A torn hull plate: an irregular 5-7 sided sheet with a fold across it."""
	bm = bmesh.new()
	n = int(rng.integers(5, 8))
	pts = []
	for k in range(n):
		a = 2 * math.pi * k / n + rng.uniform(-0.35, 0.35)
		rad = size * rng.uniform(0.55, 1.0)
		px, py = math.cos(a) * rad, math.sin(a) * rad * 0.7
		pts.append(Vector((px, py, bend * abs(px) ** 1.4 * (1 if px > 0 else 0.4))))
	top = [bm.verts.new(p) for p in pts]
	bot = [bm.verts.new(p - Vector((0, 0, thickness))) for p in pts]
	bm.faces.new(top)
	bm.faces.new(list(reversed(bot)))
	for k in range(n):
		j = (k + 1) % n
		bm.faces.new((top[j], top[k], bot[k], bot[j]))
	bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
	bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
	me = bpy.data.meshes.new(name)
	bm.to_mesh(me)
	bm.free()
	me.materials.append(mat)
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	for poly in me.polygons:
		poly.use_smooth = False
	planar_uvs(obj, 1.0)
	return obj


def build_scorch(mat, rng):
	"""Burnt ground: rings from the middle out - a shallow bowl, a raised lip of thrown earth, then a skirt that runs
	0.7 m below the origin so the disc meets the terrain on any slope (the visible rim is wherever the ground cuts it)."""
	rings = [(0.0, 0.22), (0.9, 0.20), (1.7, 0.40), (2.5, 0.24), (3.5, 0.06), (4.5, -0.7)]
	segs = 22
	bm = bmesh.new()
	uvl = bm.loops.layers.uv.new("UVMap")
	centre = bm.verts.new((0, 0, rings[0][1]))
	loops = []
	for ri, (rad, h) in enumerate(rings[1:]):
		ring = []
		for k in range(segs):
			a = 2 * math.pi * (k + 0.5 * (ri % 2)) / segs
			wob = 1.0 + 0.16 * math.sin(3 * a + 0.7) + 0.09 * math.sin(7 * a + 2.1) + rng.uniform(-0.05, 0.05)
			ring.append(bm.verts.new((math.cos(a) * rad * wob, math.sin(a) * rad * wob, h + rng.uniform(-0.035, 0.035) * (1 if ri < 4 else 0))))
		loops.append(ring)
	faces = []
	for k in range(segs):
		faces.append(bm.faces.new((centre, loops[0][k], loops[0][(k + 1) % segs])))
	for a, b in zip(loops, loops[1:]):
		for k in range(segs):
			j = (k + 1) % segs
			faces.append(bm.faces.new((a[k], b[k], b[j])))
			faces.append(bm.faces.new((a[k], b[j], a[j])))
	for f in faces:
		for loop in f.loops:
			loop[uvl].uv = (loop.vert.co.x / 9.6 + 0.5, loop.vert.co.y / 9.6 + 0.5)
	bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
	me = bpy.data.meshes.new(NAME + "_scorch")
	bm.to_mesh(me)
	bm.free()
	me.materials.append(mat)
	obj = bpy.data.objects.new(NAME + "_scorch", me)
	bpy.context.scene.collection.objects.link(obj)
	for poly in me.polygons:
		poly.use_smooth = False
	if sum(p.normal.z for p in me.polygons) < 0:
		me.flip_normals()
	return obj


def join(objs, name):
	for o in bpy.data.objects:
		o.select_set(False)
	for o in objs:
		o.select_set(True)
	bpy.context.view_layer.objects.active = objs[0]
	bpy.ops.object.join()
	out = bpy.context.view_layer.objects.active
	out.name = name
	out.data.name = name
	return out


def lods(obj):
	"""lod0 = the mesh, lod1-3 = collapse decimations (UVs survive)."""
	base = obj.name
	out = []
	for k, ratio in enumerate(LOD_RATIOS):
		c = obj.copy()
		c.data = obj.data.copy()
		c.name = f"{base}_lod{k}"
		c.data.name = c.name
		bpy.context.scene.collection.objects.link(c)
		if ratio < 1.0:
			bpy.context.view_layer.objects.active = c
			mod = c.modifiers.new("dec", 'DECIMATE')
			mod.decimate_type = 'COLLAPSE'
			mod.ratio = ratio
			bpy.ops.object.modifier_apply(modifier=mod.name)
			for poly in c.data.polygons:
				poly.use_smooth = False
		out.append(c)
	return out


def reduce(obj, ratio):
	"""Collapse a crushed section to a share of its triangles (Mark 2026-11-23: "reduce how many tris are on the crashed
	pod"). The pod's panels are separate islands, so they are welded first - unwelded, a collapse pulls every island in
	on itself and the hull opens into loose shards. Kept gentle: past about half, the buckle's folds and the UVs go."""
	bm = obj_bm(obj)
	bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=0.004)
	bm_back(bm, obj)
	have = tri_count(obj)
	for o in bpy.data.objects:
		o.select_set(False)
	obj.select_set(True)
	bpy.context.view_layer.objects.active = obj
	mod = obj.modifiers.new("budget", 'DECIMATE')
	mod.decimate_type = 'COLLAPSE'
	mod.ratio = ratio
	bpy.ops.object.modifier_apply(modifier=mod.name)
	for poly in obj.data.polygons:
		poly.use_smooth = False
	print(f"{obj.name}: {have} -> {tri_count(obj)} tris")


def tri_count(obj):
	return sum(len(p.vertices) - 2 for p in obj.data.polygons)


# -------------------------------------------------------------------------------------------------------------- export
EXPORT_YAW = Matrix.Rotation(math.radians(-90.0), 4, 'Z')


def export(objs, name):
	for o in objs:
		o.data.transform(EXPORT_YAW)
		o.data.update()
	for o in bpy.data.objects:
		o.select_set(False)
	for o in objs:
		o.select_set(True)
	bpy.context.view_layer.objects.active = objs[0]
	bpy.ops.export_scene.fbx(filepath=os.path.join(OUT_DIR, name + ".fbx"), use_selection=True, global_scale=1.0,
							 apply_unit_scale=True, object_types={'MESH'}, mesh_smooth_type='OFF', path_mode='STRIP',
							 embed_textures=False, add_leaf_bones=False, bake_anim=False)
	for o in objs:
		o.data.transform(EXPORT_YAW.inverted())
		o.data.update()


def write_vmdl(name, meshes, materials, physics, note):
	"""meshes = [lod0, lod1, ..] mesh names inside <name>.fbx. physics: None or "hull" (one convex hull of the render mesh)."""
	fbx = f"{REL}/{name}.fbx"
	remaps = "".join(f"""
							{{
								from = "{frm}"
								to = "{REL}/{m}.vmat"
							}},""" for m in materials for frm in (m + ".vmat", m))
	renders = "".join(f"""					{{
						_class = "RenderMeshFile"
						name = "{m}"
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
							exception_list = [ "{m}" ]
						}}
					}},
""" for m in meshes)
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
	phys = ""
	if physics == "hull":
		phys = """			{
				_class = "PhysicsShapeList"
				children =
				[
					{
						_class = "PhysicsHullFromRender"
						parent_bone = ""
						surface_prop = "metal"
						collision_tags = "solid"
						faceMergeAngle = 10.0
						maxHullVertices = 32
						hull_mode = "SingleHull"
					},
				]
			},
"""
	text = f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
// {note} - Blender/scripts/create_crashed_pod.py
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
						[{remaps}
						]
						use_global_default = false
						global_default_material = "{REL}/{materials[0]}.vmat"
					}},
				]
			}},
{phys}			{{
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
	with open(os.path.join(OUT_DIR, name + ".vmdl"), "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


# -------------------------------------------------------------------------------------------------------------- prefab
def guid(tag):
	return str(uuid.uuid5(uuid.NAMESPACE_URL, "survivalgame2026/" + PREFAB_REL + "/" + tag))


def renderer(tag, model):
	return {"__type": "Sandbox.ModelRenderer", "__guid": guid(tag + "/renderer"), "__enabled": True, "Flags": 0,
			"BodyGroups": 18446744073709551615, "CreateAttachments": False, "LodOverride": None, "MaterialGroup": None,
			"MaterialOverride": None, "Materials": None, "Model": model,
			"RenderOptions": {"GameLayer": True, "OverlayLayer": False, "BloomLayer": False, "AfterUILayer": False},
			"RenderType": "On", "Tint": "1,1,1,1"}


def collider(tag, model):
	return {"__type": "Sandbox.ModelCollider", "__guid": guid(tag + "/collider"), "__enabled": True, "Flags": 0,
			"ColliderFlags": 0, "Elasticity": None, "Friction": None, "IsTrigger": False, "Model": model,
			"RollingResistance": None, "Static": True, "Surface": None, "SurfaceVelocity": "0,0,0"}


def child(name, components, tags="", pos="0,0,0"):
	return {"__guid": guid(name), "__version": 2, "Flags": 0, "Name": name, "Position": pos, "Rotation": "0,0,0,1",
			"Scale": "1,1,1", "Tags": tags, "Enabled": True, "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0,
			"NetworkTransmit": True, "OwnerTransfer": 1, "Components": components, "Children": []}


def write_prefab(section_count, grounded, links):
	shards = [f"{REL}/crashed_pod_shard{k}_v1.vmdl" for k in (1, 2, 3)]
	rock = {
		"__type": "Survival.MineableRock", "__guid": guid("rock"), "__enabled": True, "Flags": 0,
		"HitsPerSection": 5, "RequireMineTool": True, "GroundedSections": grounded, "SectionLinks": links,
		# Mark 2026-11-19: 1-2 scrap per section (4-8 a wreck), mined or collapsed alike
		"StoneResourceId": "resource_scrap", "StoneMin": 1, "StoneMax": 2, "CollapseStoneMin": 1, "CollapseStoneMax": 2,
		"PieceModels": [], "BitModels": shards, "ChipModels": shards, "BitsPerSection": 6, "BitScale": 1.0,
		"DebrisLifetimeSeconds": 3.5, "DebrisScatterMeters": 2.0, "LogMining": False,
	}
	smoke = {
		"__type": "Survival.SmokeColumn", "__guid": guid("smoke"), "__enabled": True, "Flags": 0,
		"OriginMeters": "%g,%g,%g" % SMOKE_ORIGIN,
		"PuffModel": "models/props/stasis_pod/stasis_pod_puff.vmdl",
		"PuffMaterial": f"{REL}/{SMOKE}.vmat",
		"StopWhenMinedOut": True,
		# written out (not left to the component defaults): a prefab clone keeps the defaults it was first loaded with
		"HeightMeters": 100.0, "RiseSeconds": 45.0, "PuffCount": 48, "BaseDiameterMeters": 1.8, "TopDiameterMeters": 22.0,
		"LeanMeters": 22.0, "WanderMeters": 2.5, "FootTint": "0.22,0.21,0.21,1", "TopTint": "0.8,0.8,0.82,1",
	}
	children = [child(f"section{i}", [renderer(f"s{i}", f"{REL}/{NAME}_s{i}.vmdl"), collider(f"s{i}", f"{REL}/{NAME}_s{i}.vmdl")],
					  tags="rock_section") for i in range(section_count)]
	children.append(child("Scorch", [renderer("scorch", f"{REL}/{NAME}_scorch.vmdl")]))
	root = {"__guid": guid("root"), "__version": 2, "Flags": 0, "Name": NAME, "Position": "0,0,0", "Rotation": "0,0,0,1",
			"Scale": "1,1,1", "Tags": "", "Enabled": True, "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0,
			"NetworkTransmit": True, "OwnerTransfer": 1, "Components": [rock, smoke], "Children": children,
			"__properties": {"NetworkInterpolation": True, "TimeScale": 1, "WantsSystemScene": True, "Metadata": {},
							 "NavMesh": {"Enabled": False, "IncludeStaticBodies": True, "IncludeKeyframedBodies": True,
										 "EditorAutoUpdate": False, "AgentHeight": 64, "AgentRadius": 16, "AgentStepSize": 18,
										 "AgentMaxSlope": 40, "ExcludedBodies": "", "IncludedBodies": "", "DeferGeneration": False,
										 "CustomBounds": False}},
			"__variables": []}
	doc = {"RootObject": root, "ResourceVersion": 2, "ShowInMenu": False, "MenuPath": None, "MenuIcon": None,
		   "DontBreakAsTemplate": False, "__references": [], "__version": 2}
	with open(os.path.join(ROOT, "Assets", PREFAB_REL.replace("/", os.sep)), "w", encoding="utf-8", newline="\n") as f:
		json.dump(doc, f, indent=2)


# -------------------------------------------------------------------------------------------------------------- render
def render(tag, show, cam_from, look_at, lens=40):
	scene = bpy.context.scene
	scene.render.engine = 'BLENDER_WORKBENCH'
	scene.display.shading.light = 'FLAT' if tag.endswith('_top') else 'STUDIO'
	scene.display.shading.color_type = 'TEXTURE'
	scene.display.shading.show_shadows = True
	scene.display.shading.show_cavity = True
	scene.display.shading.show_backface_culling = True
	scene.render.resolution_x = 1100
	scene.render.resolution_y = 800
	scene.world = scene.world or bpy.data.worlds.new("World")
	scene.world.color = (0.2, 0.24, 0.2)
	for o in bpy.data.objects:
		if o.type == 'MESH':
			o.hide_render = o not in show
	cam = bpy.data.objects.get("PreviewCam")
	if cam is None:
		cam = bpy.data.objects.new("PreviewCam", bpy.data.cameras.new("PreviewCam"))
		scene.collection.objects.link(cam)
	cam.data.lens = lens
	cam.location = Vector(cam_from)
	cam.rotation_euler = (Vector(look_at) - cam.location).to_track_quat('-Z', 'Y').to_euler()
	scene.camera = cam
	os.makedirs(RENDER_DIR, exist_ok=True)
	scene.render.filepath = os.path.join(RENDER_DIR, tag + ".png")
	bpy.ops.render.render(write_still=True)
	print("render", scene.render.filepath)


def main():
	for o in list(bpy.data.objects):
		bpy.data.objects.remove(o, do_unlink=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0
	os.makedirs(OUT_DIR, exist_ok=True)
	rng = np.random.default_rng(1119)

	scorch_img, scorch_mask = make_scorch(rng)
	sp.write_png(os.path.join(OUT_DIR, SCORCH + "_mask.png"), scorch_mask)
	textures = {
		SHELL: (scorch_metal(sp.make_shell(), rng, 0.8, 0.45), 0.5, 0.7),
		TRIM: (scorch_metal(sp.make_trim(), rng, 0.6, 0.4), 0.7, 0.4),
		PAD: (scorch_metal(sp.make_pad(), rng, 0.9, 0.9), 0.95, 0.0),
		SCORCH: (scorch_img, 1.0, 0.0),
		SMOKE: (make_smoke(rng), 1.0, 0.0),
	}
	mats = {}
	for name, (img, rough, spec) in textures.items():
		sp.write_png(os.path.join(OUT_DIR, name + ".png"), img)
		with open(os.path.join(OUT_DIR, name + ".vmat"), "w", newline="\n") as f:
			f.write(pixel_vmat(name, rough, spec, SCORCH + "_mask" if name == SCORCH else None))
		mats[name] = sp.make_material(name, os.path.join(OUT_DIR, name + ".png"), rough)

	# the intact pod, wearing the burnt materials (the FBX material names decide the vmats)
	pod_mats = {"stasis_pod_shell": mats[SHELL], "stasis_pod_trim": mats[TRIM], "stasis_pod_pad": mats[PAD], sp.GLASS: mats[TRIM]}
	pod = sp.build_pod(pod_mats)
	door, door_phys, hinge = sp.build_door(pod_mats)
	bpy.data.objects.remove(door_phys, do_unlink=True)
	door.data.transform(Matrix.Translation(hinge))   # back into the closed doorway
	print(f"intact: pod {tri_count(pod)} tris, door {tri_count(door)} tris")

	subdivide_and_crush(pod)
	subdivide_and_crush(door, drop_material=2)   # the window glass is gone

	head = cut(pod, NAME + "_s0", keep_above=True)    # the head end: buried
	base = cut(pod, NAME + "_s1", keep_above=False)   # the base end: up in the air, rests on the head half
	bpy.data.objects.remove(pod, do_unlink=True)

	# lay the hull down head-first: roll about its own axis, tip the axis 124 deg from upright, yaw, sink
	pose = Matrix.Rotation(math.radians(28.0), 4, 'Z') @ Matrix.Rotation(math.radians(124.0), 4, 'Y') @ Matrix.Rotation(math.radians(20.0), 4, 'Z')
	for o in (head, base):
		o.data.transform(pose)
	low = min(v.co.z for o in (head, base) for v in o.data.vertices)
	cx = sum(v.co.x for v in head.data.vertices) / len(head.data.vertices)
	cy = sum(v.co.y for v in head.data.vertices) / len(head.data.vertices)
	settle = Matrix.Translation((-cx * 0.6, -cy * 0.6, -low - 0.42))
	for o in (head, base):
		o.data.transform(settle)
		o.data.update()

	# the door: curled a little more, thrown clear, lying outer face up and half in the dirt
	dc = sum((v.co for v in door.data.vertices), Vector()) / len(door.data.vertices)
	door.data.transform(Matrix.Translation(-dc))
	for v in door.data.vertices:
		v.co.x += 0.5 * v.co.y * v.co.y   # curl
	door.data.transform(Matrix.Translation((2.5, -1.7, 0.16)) @ Matrix.Rotation(math.radians(140.0), 4, 'Z')
						@ Matrix.Rotation(math.radians(-72.0), 4, 'Y'))
	door.name = NAME + "_s2"
	door.data.name = door.name

	# torn plates standing in the ground beside the wreck (one section)
	prng = np.random.default_rng(77)
	plates = []
	for k, (px, py, yaw, tilt, size) in enumerate(((-1.5, 1.5, 20, 62, 0.55), (-2.0, 1.0, 110, 38, 0.42), (-1.2, 2.1, 200, 75, 0.36))):
		p = plate(f"plate{k}", mats[SHELL], prng, size, 0.5)
		p.data.transform(Matrix.Translation((px, py, size * 0.25)) @ Matrix.Rotation(math.radians(yaw), 4, 'Z')
						 @ Matrix.Rotation(math.radians(tilt), 4, 'Y'))
		plates.append(p)
	pile = join(plates, NAME + "_s3")

	scorch = build_scorch(mats[SCORCH], np.random.default_rng(5))
	shards = [plate(f"crashed_pod_shard{k}_v1", mats[SHELL], np.random.default_rng(900 + k), s, b, 0.03)
			  for k, (s, b) in ((1, (0.24, 0.6)), (2, (0.18, 0.9)), (3, (0.3, 0.3)))]

	sections = [head, base, door, pile]
	for sec in (head, base, door):
		reduce(sec, LOD0_KEEP)
	for i, sec in enumerate(sections):
		names = [m.name for m in sec.data.materials if m is not None]
		used = sorted({p.material_index for p in sec.data.polygons})
		lod_objs = lods(sec)
		export(lod_objs, sec.name)
		write_vmdl(sec.name, [o.name for o in lod_objs], names, "hull", f"crashed stasis pod, section {i}")
		print(f"{sec.name}: " + ", ".join(f"lod{k} {tri_count(o)}" for k, o in enumerate(lod_objs)) + f" tris; materials {names} used {used}")
		for o in lod_objs:
			o.hide_render = True
	export([scorch], scorch.name)
	write_vmdl(scorch.name, [scorch.name], [SCORCH], None, "crashed stasis pod, burnt ground")
	for s in shards:
		export([s], s.name)
		write_vmdl(s.name, [s.name], [SHELL], "hull", "crashed stasis pod, torn plate (debris)")
		print(f"{s.name}: {tri_count(s)} tris")
	print(f"{scorch.name}: {tri_count(scorch)} tris")
	for o in sections:
		xs = [v.co for v in o.data.vertices]
		print(f"{o.name} bounds x {min(v.x for v in xs):.2f}..{max(v.x for v in xs):.2f} y {min(v.y for v in xs):.2f}..{max(v.y for v in xs):.2f} z {min(v.z for v in xs):.2f}..{max(v.z for v in xs):.2f}")

	# s1 rests on s0; the door and the plate pile stand on the ground themselves
	write_prefab(len(sections), [0, 2, 3], ["1-0"])

	sun = bpy.data.lights.new("Sun", 'SUN')
	so = bpy.data.objects.new("Sun", sun)
	bpy.context.scene.collection.objects.link(so)
	so.rotation_euler = (math.radians(50), math.radians(10), math.radians(35))
	whole = sections + [scorch]
	render("crashed_pod_v1_front", whole, (6.5, -6.0, 3.6), (0.3, 0, 0.3))
	render("crashed_pod_v1_back", whole, (-6.0, 5.5, 3.2), (0.3, 0, 0.3))
	render("crashed_pod_v1_hull_close", [head, base], (3.2, -2.6, 1.7), (0.2, 0, 0.5))
	render("crashed_pod_v1_top", whole, (0.0, -0.1, 13.0), (0, 0, 0))
	for k, s in enumerate(shards):
		render(s.name, [s], (0.7, -0.6, 0.5), (0, 0, 0.03))

	blend = os.path.join(ROOT, "Blender", "blenderprojects", "crashed_pod.blend")
	bpy.ops.wm.save_as_mainfile(filepath=blend)
	print("saved", blend)


if __name__ == "__main__":
	main()
