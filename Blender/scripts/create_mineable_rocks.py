"""Mineable stone set for Clover Hills (Mark 2026-11: "We should also start adding some stone models to the game ... a. Stone
model when we throw stone on the ground ... c. Stone model that when we hit it busts into raw stone ... d. Stone model that
when we hit it busts into individual chunks, like the stone spires" + "big boulders like 3m in diameter that are like the
spires too, multiple sections"; kind b, the pickup pebbles, already exists).

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_mineable_rocks.py
    (optional after `--`: a family name to build only that one: drop, break, spire, boulder, bit)

Families (all at real size, Blender metres -> FBX -> vmdl import_scale 0.4 = the project's 40 u/m; all wear
materials/environment/cloverHills/rocks/rock_gunmetal.vmat, the 256 px gunmetal texture at tiling 1):

| family        | files (Assets/models/...)                                   | size          | what it is                                  |
|---------------|-------------------------------------------------------------|---------------|---------------------------------------------|
| stone_drop    | resources/stone/stone_drop{1-4}_v1                          | 0.12-0.18 m   | (a) the resource_stone world drop           |
| rock_break    | environment/cloverHills/rocks/rock_break{1-4}_v1 (+ _p0..)  | 0.9-1.5 m     | (c) bursts into raw stone; 7 Voronoi pieces |
| rock_spire    | environment/cloverHills/rocks/rock_spire{1-3}_v1_s0..s5     | ~33 m tall    | (d) six stacked sections                    |
| rock_boulder  | environment/cloverHills/rocks/rock_boulder{1-3}_v1_s0..     | ~3 m across   | (e) 4-6 interlocking Voronoi sections       |
| rock_bit      | environment/cloverHills/rocks/rock_bit{1-5}_v1              | 0.3-0.7 m     | the stone bits a section bursts into        |

Look (Mark's low-poly rules): a displaced, domain-warped sphere (or lofted rings for the spire), collapse-decimated, then
tris-to-quads + planar dissolve, so facets vary in size and nothing reads as a primitive. UVs: every face is projected flat
on its own plane at UV_METERS per tile, so the 256 px texture lands at 128 texels / m on every face at scale 1 (the
projection is a pure function of position + normal, so a piece's outer faces keep exactly the whole rock's UVs).

Sections and pieces share their rock's origin (assembled at one transform). Every sectioned family prints its support
graph (which sections touch the ground, which touch each other) and writes it into the prefab's MineableRock
(Sections - Grounded Sections / Section Links), with the family's tuning (FAMILY_TUNING). Prefab and component guids are
uuid5 of the path, so a re-run rewrites the same prefab instead of breaking scene references.

LODs: whole rocks and sections get lod0-3 switching at 100 / 200 / 300 m (switch_threshold 400 / 800 / 1200). Debris
(break pieces, bits) and the drop stones are single-mesh: they live a few seconds or are a fist-sized pickup.

Renders of every variant go to RENDER_DIR.
"""
import bpy
import bmesh
import json
import math
import os
import random
import sys
import uuid
from mathutils import Vector, Matrix, noise

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ASSETS = os.path.join(ROOT, "Assets")
TEXTURE = os.path.join(ASSETS, "materials", "environment", "cloverHills", "rocks", "rock_gunmetal.png")
VMAT = "materials/environment/cloverHills/rocks/rock_gunmetal.vmat"
UV_METERS = 2.0  # one 256 px tile per 2 m = 128 texels / m
RENDER_DIR = os.environ.get("ROCK_RENDER_DIR", os.path.join(ROOT, "Blender", "renders", "mineable_rocks"))
PREFAB_DIR = "prefabs/environment/cloverHills/rocks"
VERSION = "v1"

DROP_DIR = ("models/resources/stone", "stone_drop")
ROCK_DIR = "models/environment/cloverHills/rocks"

LOD_DISSOLVE_DEG = (0.0, 9.0, 18.0, 32.0)
LOD_THRESHOLDS = (0.0, 400.0, 800.0, 1200.0)


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


def blob(name, seed, size, subdiv=3, warp=0.9, rough=0.22, taper=0.15, shear=0.12, bottom=None, faces=80, cleave=2, dissolve=5.0):
	"""Displaced, domain-warped sphere scaled to `size` (full extents, m). bottom = (flatten_z, sink) for ground rocks."""
	rnd = random.Random(seed)
	off = Vector((rnd.uniform(-50, 50), rnd.uniform(-50, 50), rnd.uniform(-50, 50)))
	off2 = Vector((rnd.uniform(-50, 50), rnd.uniform(-50, 50), rnd.uniform(-50, 50)))
	sx, sy = rnd.uniform(-shear, shear), rnd.uniform(-shear, shear)
	bm = bmesh.new()
	bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=1.0)
	for v in bm.verts:
		p = v.co.copy()
		w = noise.noise_vector(p * 0.45 + off) * warp
		q = (p + w * 0.45).normalized()
		r = 1.0 + rough * noise.noise(q * 1.4 + off2) + rough * 0.4 * noise.noise(q * 3.1 + off)
		p = q * r
		# egg taper along z, shear / lean
		p.x *= 1.0 - taper * p.z
		p.y *= 1.0 - taper * p.z
		p.x += sx * p.z
		p.y += sy * p.z
		v.co = Vector((p.x * size[0] * 0.5, p.y * size[1] * 0.5, p.z * size[2] * 0.5))
	if bottom is not None:
		# a flat-ish bed: sit the rock on z = 0, flatten what is under the bed line, then sink it a little
		zmin = min(v.co.z for v in bm.verts)
		for v in bm.verts:
			v.co.z -= zmin
		flat, sink = bottom
		for v in bm.verts:
			if v.co.z < flat:
				v.co.z = flat * 0.35 + v.co.z * 0.65 * 0.3
			v.co.z -= sink
	# a few cleavage planes: big flat breaks beside the small facets, so no two faces read alike
	for k in range(cleave):
		n = Vector((rnd.uniform(-1, 1), rnd.uniform(-1, 1), rnd.uniform(-0.2, 1.0))).normalized()
		reach = max(abs(v.co.dot(n)) for v in bm.verts)
		co = n * reach * rnd.uniform(0.62, 0.8)
		if bottom is not None and n.z < -0.3:
			continue
		res = bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=co, plane_no=n, clear_outer=True, dist=1e-5)
		cut = [e for e in res["geom_cut"] if isinstance(e, bmesh.types.BMEdge)]
		if cut:
			bmesh.ops.holes_fill(bm, edges=cut, sides=0)
	obj = bm_to_object(bm, name)
	decimate(obj, faces)
	facet(obj, dissolve)
	return obj


def hull(name, seed, size, points=12, faces=40):
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


def spire(name, seed, height=33.0):
	"""A leaning, tapering stone needle lofted from irregular rings, sunk 2.5 m below its origin."""
	rnd = random.Random(seed)
	off = Vector((rnd.uniform(-50, 50), rnd.uniform(-50, 50), rnd.uniform(-50, 50)))
	rings = 22
	sides = 9
	base = rnd.uniform(3.9, 4.6)
	top = rnd.uniform(1.4, 2.0)
	squash = rnd.uniform(0.7, 0.85)
	lean = Vector((rnd.uniform(-1, 1), rnd.uniform(-1, 1), 0.0)).normalized() * rnd.uniform(2.5, 4.5)
	z0 = -2.5
	bm = bmesh.new()
	ring_verts = []
	for i in range(rings):
		t = i / (rings - 1)
		z = z0 + (height - z0) * t + (rnd.uniform(-0.35, 0.35) if 0 < i < rings - 1 else 0.0)
		taper = base + (top - base) * (t ** 0.85)
		flare = 1.0 + 0.32 * max(0.0, 1.0 - t * 6.0) ** 2
		bulge = 1.0 + 0.3 * noise.noise(Vector((0.0, 0.0, t * 2.4)) + off) + 0.14 * noise.noise(Vector((0.0, t * 6.5, 0.0)) + off)
		centre = lean * (t ** 1.6) + Vector((noise.noise(Vector((t * 2.0, 0.0, 0.0)) + off), noise.noise(Vector((0.0, t * 2.0, 0.0)) + off), 0.0)) * 0.9
		twist = t * 0.9 + rnd.uniform(-0.06, 0.06)
		ring = []
		for k in range(sides):
			a = twist + (k / sides) * math.tau + rnd.uniform(-0.18, 0.18)
			dir = Vector((math.cos(a), math.sin(a), 0.0))
			r = taper * flare * bulge * (1.0 + 0.24 * noise.noise(dir * 1.3 + Vector((0.0, 0.0, t * 5.0)) + off))
			r *= rnd.uniform(0.88, 1.12)
			dir = Vector((dir.x, dir.y * squash, 0.0))  # flattened section: a blade, not a cone
			ring.append(bm.verts.new(Vector((centre.x + dir.x * r, centre.y + dir.y * r, z))))
		ring_verts.append(ring)
	for i in range(rings - 1):
		a, b = ring_verts[i], ring_verts[i + 1]
		for k in range(sides):
			k2 = (k + 1) % sides
			bm.faces.new((a[k], a[k2], b[k2], b[k]))
	bm.faces.new(list(reversed(ring_verts[0])))
	# a jagged peak instead of a flat cap
	last = ring_verts[-1]
	c = sum((v.co for v in last), Vector()) / sides
	peak = bm.verts.new(c + Vector((rnd.uniform(-0.4, 0.4), rnd.uniform(-0.4, 0.4), rnd.uniform(0.4, 1.3))))
	for k in range(sides):
		bm.faces.new((last[k], last[(k + 1) % sides], peak))
	bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
	bmesh.ops.triangulate(bm, faces=bm.faces[:])
	obj = bm_to_object(bm, name)
	facet(obj, 6.0)
	return obj


# ------------------------------------------------------------------------------------------------------------ fracture

def convex_cell(planes, extent=200.0):
	"""Big cube clipped by half-spaces (co, no): keeps the side (p - co) . no <= 0."""
	bm = bmesh.new()
	bmesh.ops.create_cube(bm, size=extent * 2.0)
	for co, no in planes:
		geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
		res = bmesh.ops.bisect_plane(bm, geom=geom, plane_co=co, plane_no=no, clear_outer=True, dist=1e-5)
		cut = [e for e in res["geom_cut"] if isinstance(e, bmesh.types.BMEdge)]
		if cut:
			bmesh.ops.holes_fill(bm, edges=cut, sides=0)
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


def voronoi_pieces(source, seeds, name):
	pieces = []
	for i, s in enumerate(seeds):
		planes = []
		for j, o in enumerate(seeds):
			if i != j:
				n = (o - s).normalized()
				planes.append(((s + o) * 0.5, n))
		p = intersect(source, convex_cell(planes), f"{name}{i}")
		pieces.append((p, i))
	return pieces


def face_area_on_plane(obj, co, no):
	area = 0.0
	for f in obj.data.polygons:
		if f.normal.dot(no) > 0.999 and abs((f.center - co).dot(no)) < 2e-3:
			area += f.area
	return area


# ------------------------------------------------------------------------------------------------------------------ uv

def planar_uvs(obj):
	"""Every face flat on its own plane at UV_METERS per tile (position + normal only, so pieces match their rock)."""
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


def finish(obj, material):
	bm = obj_to_bm(obj)
	bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method='BEAUTY', ngon_method='BEAUTY')
	bm.to_mesh(obj.data)
	bm.free()
	planar_uvs(obj)
	obj.data.materials.clear()
	obj.data.materials.append(material)
	for p in obj.data.polygons:
		p.use_smooth = False


def tris(obj):
	return len(obj.data.polygons)


def bounds(obj):
	xs = [v.co.x for v in obj.data.vertices]
	ys = [v.co.y for v in obj.data.vertices]
	zs = [v.co.z for v in obj.data.vertices]
	return (min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs))


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
		return f"""			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "PhysicsHullFromRender"
						parent_bone = ""
						surface_prop = "rock"
						collision_tags = "solid"
						faceMergeAngle = 10.0
						maxHullVertices = 32
						hull_mode = "SingleHull"
					}},
				]
			}},
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
	"""meshes = [lod0, lod1, ...] mesh names in `fbx` (one = no LOD groups)."""
	renders = "".join(render_mesh_block(fbx, m) for m in meshes)
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
// {note} - Blender/scripts/create_mineable_rocks.py
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
						remaps = [  ]
						use_global_default = true
						global_default_material = "{VMAT}"
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


def write_far_vmdl(rel_vmdl, fbx, meshes, note):
	"""The whole rock as one render model for the terrain's far ring (TerrainWorldManager.FarTrees draws it instanced past
	Tree Objects Range, like an elm's _far.vmdl): every section's lowest LOD together, no physics, no LOD groups."""
	renders = "".join(render_mesh_block(fbx, m) for m in meshes)
	text = f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
// {note} - Blender/scripts/create_mineable_rocks.py
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
						remaps = [  ]
						use_global_default = true
						global_default_material = "{VMAT}"
					}},
				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
{renders}				]
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
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


# -------------------------------------------------------------------------------------------------------------- render

def setup_render():
	scene = bpy.context.scene
	scene.render.engine = 'BLENDER_EEVEE'
	scene.render.resolution_x = 900
	scene.render.resolution_y = 900
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
	gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.20, 0.27, 0.14, 1)
	g.data.materials.append(gm)
	scene.collection.objects.link(g)
	return cam, g


def render(objs, path, cam, ground, ground_z=0.0, elev=22.0, yaw=35.0):
	scene = bpy.context.scene
	bpy.context.view_layer.update()  # moved objects: refresh matrix_world before framing
	shown = set(o.name for o in objs)
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
	centre = (lo + hi) * 0.5
	radius = max((hi - lo).length * 0.5, 0.05)
	ground.location = (0, 0, ground_z)
	d = radius / math.tan(math.radians(18)) * 1.05
	dir = Vector((math.cos(math.radians(yaw)) * math.cos(math.radians(elev)), math.sin(math.radians(yaw)) * math.cos(math.radians(elev)), math.sin(math.radians(elev))))
	cam.location = centre + dir * d
	cam.rotation_euler = (-dir).to_track_quat('-Z', 'Y').to_euler()
	cam.data.clip_start = d * 0.01
	cam.data.clip_end = d * 10
	scene.render.filepath = path
	bpy.ops.render.render(write_still=True)



# -------------------------------------------------------------------------------------------------------------- prefab

BITS_MODELS = [f"{ROCK_DIR}/rock_bit{i}_{VERSION}.vmdl" for i in range(1, 6)]

# Per family: hits per section, stone from a mined section, stone a collapsing section rains, bits per section,
# debris lifetime (s), debris scatter (m/s). Designer knobs on MineableRock; these are the authored defaults.
FAMILY_TUNING = {
	"break": dict(HitsPerSection=4, StoneMin=3, StoneMax=5, CollapseStoneMin=0, CollapseStoneMax=0, BitsPerSection=0, BitScale=1.0,
				  DebrisLifetimeSeconds=2.0, DebrisScatterMeters=2.5, tags=""),
	"spire": dict(HitsPerSection=8, StoneMin=6, StoneMax=9, CollapseStoneMin=4, CollapseStoneMax=6, BitsPerSection=18, BitScale=2.0,
				  DebrisLifetimeSeconds=5.0, DebrisScatterMeters=2.0, tags="grapple"),
	"boulder": dict(HitsPerSection=5, StoneMin=3, StoneMax=5, CollapseStoneMin=2, CollapseStoneMax=4, BitsPerSection=7, BitScale=0.9,
					DebrisLifetimeSeconds=3.5, DebrisScatterMeters=1.5, tags=""),
}


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


def write_prefab(name, family, section_models, grounded, links, piece_models):
	"""Sections are children section0..N (break: one, the whole rock) with renderer + static collider, tagged rock_section."""
	t = FAMILY_TUNING[family]
	rel = f"{PREFAB_DIR}/{name}.prefab"
	mine = {
		"__type": "Survival.MineableRock", "__guid": guid(rel, "mineable"), "__enabled": True, "Flags": 0,
		"HitsPerSection": t["HitsPerSection"], "RequireMineTool": True,
		"GroundedSections": grounded, "SectionLinks": links,
		"StoneResourceId": "resource_stone", "StoneMin": t["StoneMin"], "StoneMax": t["StoneMax"],
		"CollapseStoneMin": t["CollapseStoneMin"], "CollapseStoneMax": t["CollapseStoneMax"],
		"PieceModels": piece_models, "BitModels": [] if family == "break" else BITS_MODELS,
		"ChipModels": [f"{DROP_DIR[0]}/{DROP_DIR[1]}{i}_{VERSION}.vmdl" for i in range(1, len(DROPS) + 1)],
		"BitsPerSection": t["BitsPerSection"], "BitScale": t["BitScale"], "DebrisLifetimeSeconds": t["DebrisLifetimeSeconds"],
		"DebrisScatterMeters": t["DebrisScatterMeters"], "LogMining": False,
	}
	components = [mine]
	children = []
	if family == "break":
		section_models = [f"{ROCK_DIR}/{name}.vmdl"]
	section_tags = "rock_section" + ("," + t["tags"] if t["tags"] else "")
	for k, model in enumerate(section_models):
		key = f"{rel}/section{k}"
		children.append({
			"__guid": guid(key), "__version": 2, "Flags": 0, "Name": f"section{k}", "Position": "0,0,0",
			"Rotation": "0,0,0,1", "Scale": "1,1,1", "Tags": section_tags, "Enabled": True, "NetworkMode": 2,
			"NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": True, "OwnerTransfer": 1,
			"Components": [renderer_json(key, model), collider_json(key, model)], "Children": [],
		})
	root = {
		"__guid": guid(rel), "__version": 2, "Flags": 0, "Name": name, "Position": "0,0,0", "Rotation": "0,0,0,1",
		"Scale": "1,1,1", "Tags": t["tags"], "Enabled": True, "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0,
		"NetworkTransmit": True, "OwnerTransfer": 1, "Components": components, "Children": children,
		"__properties": {"NetworkInterpolation": True, "TimeScale": 1, "WantsSystemScene": True, "Metadata": {}},
		"__variables": [],
	}
	doc = {"RootObject": root, "ResourceVersion": 2, "ShowInMenu": False, "MenuPath": None, "MenuIcon": None,
		   "DontBreakAsTemplate": False, "__references": [], "__version": 2}
	path = os.path.join(ASSETS, rel.replace("/", os.sep))
	os.makedirs(os.path.dirname(path), exist_ok=True)
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		json.dump(doc, f, indent=2)


# --------------------------------------------------------------------------------------------------------------- build

def make_material():
	mat = bpy.data.materials.new("rock_gunmetal")
	mat.use_nodes = True
	bsdf = mat.node_tree.nodes.get("Principled BSDF")
	img = bpy.data.images.load(TEXTURE)
	tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
	tex.image = img
	tex.interpolation = 'Closest'
	mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
	bsdf.inputs["Roughness"].default_value = 0.9
	return mat


DROPS = [  # (a) world-drop stones, full extents in metres
	(101, (0.16, 0.12, 0.09)),
	(102, (0.13, 0.115, 0.10)),
	(103, (0.18, 0.10, 0.08)),
	(104, (0.125, 0.12, 0.07)),
]

BREAKS = [  # (c) breakable rocks
	(201, (1.3, 1.0, 0.8)),
	(202, (1.0, 0.85, 0.9)),
	(203, (1.5, 1.1, 0.65)),
	(204, (0.95, 0.8, 0.75)),
]

SPIRES = [301, 302, 303]  # (d) 33 m, six sections

BOULDERS = [  # (e) ~3 m, sectioned
	(401, (3.2, 2.8, 2.3), 5),
	(402, (2.9, 3.0, 2.0), 4),
	(403, (3.4, 2.6, 2.6), 6),
]

BITS = [  # the bits a section bursts into
	(501, (0.8, 0.6, 0.5)),
	(502, (0.6, 0.5, 0.45)),
	(503, (1.0, 0.65, 0.55)),
	(504, (0.5, 0.45, 0.35)),
	(505, (0.75, 0.7, 0.45)),
]


def build_drops(mat, cam, ground, report):
	folder, stem = DROP_DIR
	for i, (seed, size) in enumerate(DROPS, 1):
		name = f"{stem}{i}_{VERSION}"
		obj = blob(name, seed, size, subdiv=3, warp=1.0, rough=0.3, taper=0.12, faces=70, cleave=3, dissolve=9.0)
		lo, hi = bounds(obj)
		c = Vector(((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2))
		obj.data.transform(Matrix.Translation(-c))  # centred: the drop's sphere collider sits at its origin
		finish(obj, mat)
		fbx = f"{folder}/{name}.fbx"
		export_fbx([obj], fbx)
		write_vmdl(f"{folder}/{name}.vmdl", fbx, [name], True, "(a) resource_stone world drop")
		lo, hi = bounds(obj)
		report.append(f"{name}: {tris(obj)} tris, {hi[0]-lo[0]:.2f} x {hi[1]-lo[1]:.2f} x {hi[2]-lo[2]:.2f} m")
		render([obj], os.path.join(RENDER_DIR, f"{name}.png"), cam, ground, ground_z=lo[2])


def build_breaks(mat, cam, ground, report):
	for i, (seed, size) in enumerate(BREAKS, 1):
		name = f"rock_break{i}_{VERSION}"
		whole = blob(name, seed, size, subdiv=3, warp=1.0, rough=0.28, taper=0.18, bottom=(0.06, 0.1), faces=110, cleave=3, dissolve=7.0)
		rnd = random.Random(seed + 7)
		lo, hi = bounds(whole)
		seeds = []
		while len(seeds) < 7:
			p = Vector((rnd.uniform(lo[0], hi[0]) * 0.8, rnd.uniform(lo[1], hi[1]) * 0.8, rnd.uniform(lo[2] + 0.1, hi[2] * 0.9)))
			if all((p - s).length > min(size) * 0.28 for s in seeds):
				seeds.append(p)
		pieces = [p for p, _ in voronoi_pieces(whole, seeds, f"{name}_p") if p is not None]
		for k, p in enumerate(pieces):
			p.name = f"{name}_p{k}"
			p.data.name = p.name
			finish(p, mat)
		levels = lods(whole)
		for o in levels:
			finish(o, mat)
		fbx = f"{ROCK_DIR}/{name}.fbx"
		export_fbx(levels + pieces, fbx)
		write_vmdl(f"{ROCK_DIR}/{name}.vmdl", fbx, [o.name for o in levels], False, "(c) breakable rock, bursts into raw stone")
		for p in pieces:
			write_vmdl(f"{ROCK_DIR}/{p.name}.vmdl", fbx, [p.name], True, f"(c) {name} burst piece")
		lo, hi = bounds(levels[0])
		report.append(f"{name}: lod tris {[tris(o) for o in levels]}, {hi[0]-lo[0]:.2f} x {hi[1]-lo[1]:.2f} x {hi[2]-lo[2]:.2f} m, {len(pieces)} pieces {[tris(p) for p in pieces]}")
		write_prefab(name, "break", [], [0], [], [f"{ROCK_DIR}/{p.name}.vmdl" for p in pieces])
		render([levels[0]], os.path.join(RENDER_DIR, f"{name}.png"), cam, ground)
		explode(pieces, Vector((0, 0, (lo[2] + hi[2]) * 0.5)), 0.35)
		render(pieces, os.path.join(RENDER_DIR, f"{name}_pieces.png"), cam, ground)
		explode(pieces, None, 0)
		for o in levels[1:]:
			o.hide_render = True


def explode(objs, centre, amount):
	for o in objs:
		if centre is None:
			o.location = (0, 0, 0)
			continue
		lo, hi = bounds(o)
		c = Vector(((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2))
		d = c - centre
		o.location = d.normalized() * amount * max(1.0, d.length) if d.length > 1e-4 else Vector()


def section_graph(sections, cuts):
	"""Grounded = reaches below 0.15 m. "a-b" = a RESTS ON b: they share at least 0.05 m^2 of a cut plane whose normal
	from a toward b points down (b is under a; side-by-side contact is not support - Mark: nothing under it). A section
	left with no support at all (only side contacts) keeps its side contacts as support, so nothing falls on the first hit."""
	n = len(sections)
	grounded = [k for k, s in enumerate(sections) if s is not None and bounds(s)[0][2] < 0.15]
	rests = {k: [] for k in range(n)}
	sides = {k: [] for k in range(n)}
	for (a, b), (co, no) in cuts.items():
		if a >= n or b >= n or sections[a] is None or sections[b] is None:
			continue
		if face_area_on_plane(sections[a], co, no) <= 0.05:
			continue
		(rests if no.z < -0.2 else sides)[a].append(b)
	for k in range(n):
		if k not in grounded and not rests[k]:
			print(f"[mineable] note: section {k} only touches its neighbours side-on {sides[k]}; they count as support")
			rests[k] = sides[k]
	links = [f"{a}-{b}" for a in range(n) for b in sorted(rests[a])]
	return grounded, links


def build_spires(mat, cam, ground, report):
	for i, seed in enumerate(SPIRES, 1):
		name = f"rock_spire{i}_{VERSION}"
		whole = spire(name + "_whole", seed)
		rnd = random.Random(seed + 11)
		# five cut planes between six sections: ~5.5 m apart, tilted up to ~9 degrees
		heights = []
		z = 0.0
		for k in range(5):
			z += rnd.uniform(4.6, 6.2) if k else rnd.uniform(4.8, 6.0)
			heights.append(z)
		planes = []
		for h in heights:
			tilt = Vector((rnd.uniform(-0.15, 0.15), rnd.uniform(-0.15, 0.15), 1.0)).normalized()
			planes.append((Vector((0, 0, h)), tilt))
		sections = []
		cuts = {}
		for k in range(6):
			clip = []
			if k > 0:
				co, no = planes[k - 1]
				clip.append((co, -no))
				cuts[(k, k - 1)] = (co, -no)
			if k < 5:
				co, no = planes[k]
				clip.append((co, no))
				cuts[(k, k + 1)] = (co, no)
			s = intersect(whole, convex_cell(clip), f"{name}_s{k}")
			sections.append(s)
		grounded, links = section_graph(sections, cuts)
		write_sections(name, whole, sections, grounded, links, mat, cam, ground, report, 1.6)


def build_boulders(mat, cam, ground, report):
	for i, (seed, size, count) in enumerate(BOULDERS, 1):
		name = f"rock_boulder{i}_{VERSION}"
		whole = blob(name + "_whole", seed, size, subdiv=4, warp=1.0, rough=0.26, taper=0.2, bottom=(0.15, 0.25), faces=170, cleave=3, dissolve=6.0)
		rnd = random.Random(seed + 13)
		lo, hi = bounds(whole)
		h = hi[2]
		low = max(2, count - 2) if count > 4 else 3
		seeds = []
		for k in range(low):  # a ring of footing chunks
			a = (k / low) * math.tau + rnd.uniform(-0.3, 0.3)
			seeds.append(Vector((math.cos(a) * size[0] * 0.26, math.sin(a) * size[1] * 0.26, h * rnd.uniform(0.18, 0.3))))
		for k in range(count - low):  # capstones resting across them
			a = rnd.uniform(0, math.tau)
			seeds.append(Vector((math.cos(a) * size[0] * 0.12, math.sin(a) * size[1] * 0.12, h * rnd.uniform(0.68, 0.82))))
		sections = []
		cuts = {}
		for a, s in enumerate(seeds):
			clip = []
			for b, o in enumerate(seeds):
				if a != b:
					n = (o - s).normalized()
					co = (s + o) * 0.5
					clip.append((co, n))
					cuts[(a, b)] = (co, n)
			sections.append(intersect(whole, convex_cell(clip), f"{name}_s{a}"))
		grounded, links = section_graph(sections, cuts)
		write_sections(name, whole, sections, grounded, links, mat, cam, ground, report, 0.5)


def write_sections(name, whole, sections, grounded, links, mat, cam, ground, report, explode_amount):
	bpy.data.objects.remove(whole, do_unlink=True)
	keep = [s for s in sections if s is not None]
	if len(keep) != len(sections):
		print(f"[mineable] WARNING {name}: {len(sections) - len(keep)} empty sections")
	allobjs = []
	lod0 = []
	for k, s in enumerate(keep):
		levels = lods(s)
		for o in levels:
			finish(o, mat)
		allobjs += levels
		lod0.append(levels[0])
		for o in levels[1:]:
			o.hide_render = True
	fbx = f"{ROCK_DIR}/{name}.fbx"
	export_fbx(allobjs, fbx)
	for k in range(len(keep)):
		write_vmdl(f"{ROCK_DIR}/{name}_s{k}.vmdl", fbx, [f"{name}_s{k}_lod{j}" for j in range(4)], False, f"{name} section {k}")
	if "spire" in name:
		write_far_vmdl(f"{ROCK_DIR}/{name}_far.vmdl", fbx, [f"{name}_s{k}_lod3" for k in range(len(keep))], f"{name} far model (every section's lod3)")
	lo = Vector((min(bounds(o)[0][0] for o in lod0), min(bounds(o)[0][1] for o in lod0), min(bounds(o)[0][2] for o in lod0)))
	hi = Vector((max(bounds(o)[1][0] for o in lod0), max(bounds(o)[1][1] for o in lod0), max(bounds(o)[1][2] for o in lod0)))
	report.append(f"{name}: {len(keep)} sections, lod0 tris {[tris(o) for o in lod0]}, {hi.x-lo.x:.1f} x {hi.y-lo.y:.1f} x {hi.z-lo.z:.1f} m, grounded {grounded}, links {links}")
	family = "spire" if "spire" in name else "boulder"
	write_prefab(name, family, [f"{ROCK_DIR}/{name}_s{k}.vmdl" for k in range(len(keep))], grounded, links, [])
	render(lod0, os.path.join(RENDER_DIR, f"{name}.png"), cam, ground)
	centre = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, 0.0))
	for o in lod0:  # exploded: each section pushed off the axis (spire: up the stack)
		b0, b1 = bounds(o)
		c = Vector(((b0[0] + b1[0]) / 2, (b0[1] + b1[1]) / 2, (b0[2] + b1[2]) / 2))
		if explode_amount > 1.0:
			o.location = Vector((0, 0, c.z * 0.18 * explode_amount))
		else:
			d = (c - centre)
			o.location = d.normalized() * explode_amount * 1.6 if d.length > 1e-4 else Vector()
	render(lod0, os.path.join(RENDER_DIR, f"{name}_sections.png"), cam, ground)
	for o in lod0:
		o.location = (0, 0, 0)
		o.hide_render = True


def build_bits(mat, cam, ground, report):
	objs = []
	for i, (seed, size) in enumerate(BITS, 1):
		name = f"rock_bit{i}_{VERSION}"
		obj = hull(name, seed, size, points=11 + i, faces=30)
		finish(obj, mat)
		objs.append(obj)
		lo, hi = bounds(obj)
		report.append(f"{name}: {tris(obj)} tris, {hi[0]-lo[0]:.2f} x {hi[1]-lo[1]:.2f} x {hi[2]-lo[2]:.2f} m")
	fbx = f"{ROCK_DIR}/rock_bits_{VERSION}.fbx"
	export_fbx(objs, fbx)
	for o in objs:
		write_vmdl(f"{ROCK_DIR}/{o.name}.vmdl", fbx, [o.name], True, "stone bit a mined section bursts into")
	for k, o in enumerate(objs):
		o.location = (k * 0.8 - 1.6, 0, -bounds(o)[0][2])
	render(objs, os.path.join(RENDER_DIR, f"rock_bits_{VERSION}.png"), cam, ground, elev=28, yaw=80)
	for o in objs:
		o.hide_render = True


def main():
	only = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
	bpy.ops.wm.read_factory_settings(use_empty=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0
	os.makedirs(RENDER_DIR, exist_ok=True)
	mat = make_material()
	cam, ground = setup_render()
	report = []

	def want(k):
		return not only or k in only

	if want("drop"):
		build_drops(mat, cam, ground, report)
	if want("break"):
		build_breaks(mat, cam, ground, report)
	if want("spire"):
		build_spires(mat, cam, ground, report)
	if want("boulder"):
		build_boulders(mat, cam, ground, report)
	if want("bit"):
		build_bits(mat, cam, ground, report)
	print("[mineable] ---")
	for line in report:
		print("[mineable]", line)


main()
