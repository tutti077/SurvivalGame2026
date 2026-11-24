"""
Bird nests for Clover Hills (Mark 2026-11-10, prompt 28: "bird nests ... on ground and up in trees ... press e to
gather ... up to 2 eggs and 4 feathers"), plus the egg the player gets out of them.

    nest_ground   a woven twig cup (0.36 m) on the ground: a ring of bent twig tubes, an inner bowl, two eggs showing
    nest_tree     the same cup sitting in a short branch fork, origin at the fork's base so the scatter can set it on
                  a Clover limb the way it hangs the beehives
    egg           the inventory drop: a faceted ovoid, 0.07 m

Authored in ENGINE metres (rotated -90 deg about Z on export, like create_camp_props.py); vmdls import at 0.4.
Twigs reuse twig_v2's bark (materials/environment/cloverHills/resources/twig_v2.vmat); the bowl, eggs and branch
get small textures of their own under materials/environment/cloverHills/resources/.

Writes models under models/environment/cloverHills/resources/ (nests) and models/food/egg/ (egg), prefabs under
prefabs/environment/cloverHills/resources/, and a render per model to NEST_RENDER_DIR (default Blender/renders/nests).

Run:  "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_bird_nests.py
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
MODEL_DIR = "models/environment/cloverHills/resources"
EGG_MODEL_DIR = "models/food/egg"
MAT_DIR = "materials/environment/cloverHills/resources"
PREFAB_DIR = "prefabs/environment/cloverHills/resources"
TWIG_VMAT = f"{MAT_DIR}/twig_v2.vmat"
RENDER_DIR = os.environ.get("NEST_RENDER_DIR", os.path.join(ROOT, "Blender", "renders", "nests"))
EXPORT_ROT = Matrix.Rotation(math.radians(-90.0), 4, 'Z')
TWIG_TILE = 0.5     # twig_v2.png covers 0.5 m
SMALL_TILE = 0.25   # the 128 px textures below cover 0.25 m
TEX_PX = 128        # no environment texture under 128 px (Mark 2026-11-23); they were 64


# ----------------------------------------------------------------------------------------------------------- textures

def save_png(name, px):
	n = len(px)
	img = bpy.data.images.new(name, n, n, alpha=False)
	flat = []
	for y in range(n):
		for x in range(n):
			c = px[y][x]
			flat.extend((c[0], c[1], c[2], 1.0))
	img.pixels = flat
	path = os.path.join(ASSETS, MAT_DIR.replace("/", os.sep), name + ".png")
	img.filepath_raw = path
	img.file_format = 'PNG'
	img.save()
	return img


def tex_nest_bowl(rng):
	"""Dry grass and down pressed into the cup: pale straw tones in short curved strokes."""
	n = TEX_PX
	tones = [(0.62, 0.54, 0.36), (0.70, 0.62, 0.44), (0.54, 0.46, 0.31), (0.47, 0.40, 0.27)]
	px = [[list(tones[0]) for _ in range(n)] for _ in range(n)]
	for i in range(260 * (n // 64) ** 2):
		x, y = rng.uniform(0, n), rng.uniform(0, n)
		t = tones[rng.choice((0, 1, 1, 2, 3))]
		a = rng.uniform(0, 6.28)
		k = rng.uniform(-0.15, 0.15)
		for s in range(rng.randint(5, 14) * (n // 64)):
			px[int(y) % n][int(x) % n] = list(t)
			a += k
			x += math.cos(a)
			y += math.sin(a)
	return px


def tex_egg(rng):
	n = TEX_PX
	tones = [(0.92, 0.88, 0.78), (0.88, 0.83, 0.72), (0.95, 0.92, 0.84)]
	px = [[list(tones[0]) for _ in range(n)] for _ in range(n)]
	for y in range(n):
		for x in range(n):
			s = math.sin(x / n * 6.28 * 2.0) * math.sin(y / n * 6.28 * 1.0)
			px[y][x] = list(tones[2] if s > 0.5 else tones[1] if s < -0.5 else tones[0])
	# fine speckle, like a real shell (and no flat areas)
	for i in range(n * n // 14):
		x, y = rng.randrange(n), rng.randrange(n)
		k = rng.uniform(0.9, 0.97)
		px[y][x] = [c * k for c in px[y][x]]
	return px


def tex_branch(rng):
	"""Grey-brown clover bark for the fork: longitudinal bands, four tones."""
	n = TEX_PX
	tones = [(0.42, 0.36, 0.30), (0.48, 0.42, 0.35), (0.36, 0.31, 0.26), (0.53, 0.47, 0.40)]
	px = [[list(tones[0]) for _ in range(n)] for _ in range(n)]
	ph = rng.uniform(0, 6.28)
	for y in range(n):
		for x in range(n):
			s = math.sin(x / n * 6.28 * 5.0 + ph) + 0.5 * math.sin(x / n * 6.28 * 13.0 + y * 0.05 * 64 / n) + rng.uniform(-0.18, 0.18)
			px[y][x] = list(tones[3] if s > 1.0 else tones[1] if s > 0.1 else tones[0] if s > -0.9 else tones[2])
	for i in range(5 * n // 64):
		x = rng.uniform(0, n)
		for y in range(n):
			x += rng.uniform(-0.3, 0.3)
			c = px[y][int(x) % n]
			px[y][int(x) % n] = [c[0] * 0.65, c[1] * 0.65, c[2] * 0.65]
	return px


# ----------------------------------------------------------------------------------------------------------- geometry

class Builder:
	def __init__(self, name, materials):
		self.name = name
		self.bm = bmesh.new()
		self.uv = self.bm.loops.layers.uv.new("UVMap")
		self.materials = materials

	def mat(self, name):
		return self.materials.index(name)

	def tube(self, path, radii, sides, mat, rng, tile, cap_start=True, cap_end=True, jitter=0.12):
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
		v0 = rng.uniform(0, 1.0)
		for i in range(len(rings) - 1):
			a, b = rings[i], rings[i + 1]
			ca = 2 * math.pi * radii[i] / tile
			cb = 2 * math.pi * radii[i + 1] / tile
			va, vb = v0 + dist[i] / tile, v0 + dist[i + 1] / tile
			for j in range(sides):
				k = (j + 1) % sides
				try:
					f = self.bm.faces.new((a[j], a[k], b[k], b[j]))
				except ValueError:
					continue
				f.material_index = mat
				f.smooth = False
				for loop, uv in zip(f.loops, ((ca * j / sides, va), (ca * (j + 1) / sides, va), (cb * (j + 1) / sides, vb), (cb * j / sides, vb))):
					loop[self.uv].uv = uv
		for cap, ring in ((cap_start, tuple(reversed(rings[0]))), (cap_end, tuple(rings[-1]))):
			if not cap:
				continue
			try:
				f = self.bm.faces.new(ring)
			except ValueError:
				continue
			f.material_index = mat
			f.smooth = False
			for loop in f.loops:
				loop[self.uv].uv = (rng.uniform(0, 0.2), rng.uniform(0, 0.2))

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
	if img is None and name == "twig_v2":
		img = bpy.data.images.load(os.path.join(ASSETS, MAT_DIR.replace("/", os.sep), "twig_v2.png"))
		img.name = "twig_v2"
	if img is not None:
		bsdf = mat.node_tree.nodes.get("Principled BSDF")
		tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
		tex.image = img
		tex.interpolation = 'Closest'
		mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
		bsdf.inputs["Roughness"].default_value = 0.95
	_materials[name] = mat
	return mat


def add_cup(b, rng, centre, radius, height):
	"""The woven cup: a dozen bent twigs round the rim and lower down, an inner bowl of pressed grass."""
	twig, bowl = b.mat("twig_v2"), b.mat("nest_bowl")
	for layer, (lr, lz) in enumerate(((radius * 0.78, 0.3), (radius * 0.94, 0.65), (radius, 1.0))):
		count = 4 + layer
		for i in range(count):
			a0 = 2 * math.pi * i / count + rng.uniform(-0.3, 0.3)
			span = 2 * math.pi / count * rng.uniform(1.15, 1.6)
			pts = []
			for k in range(4):
				a = a0 + span * k / 3
				r = lr * rng.uniform(0.92, 1.08)
				z = lz * height + rng.uniform(-0.012, 0.012)
				pts.append(centre + Vector((math.cos(a) * r, math.sin(a) * r, z)))
			b.tube(pts, [0.01, 0.013, 0.012, 0.008], 4, twig, rng, TWIG_TILE, jitter=0.15)
	# a few loose ends poking out
	for i in range(4):
		a = rng.uniform(0, 2 * math.pi)
		p0 = centre + Vector((math.cos(a) * radius * 0.9, math.sin(a) * radius * 0.9, height * rng.uniform(0.4, 0.9)))
		d = Vector((math.cos(a + rng.uniform(-0.6, 0.6)), math.sin(a + rng.uniform(-0.6, 0.6)), rng.uniform(-0.2, 0.5))).normalized()
		b.tube([p0, p0 + d * 0.05, p0 + d * 0.11], [0.008, 0.006, 0.003], 4, twig, rng, TWIG_TILE)
	# bowl: a shallow fan of facets from the rim's inside down to a flat floor
	rim = []
	floor = []
	n = 10
	for i in range(n):
		a = 2 * math.pi * i / n
		rim.append(b.bm.verts.new(centre + Vector((math.cos(a) * radius * 0.78, math.sin(a) * radius * 0.78, height * 0.82 + rng.uniform(-0.01, 0.01)))))
		floor.append(b.bm.verts.new(centre + Vector((math.cos(a) * radius * 0.4, math.sin(a) * radius * 0.4, height * 0.32 + rng.uniform(-0.008, 0.008)))))
	cv = b.bm.verts.new(centre + Vector((0, 0, height * 0.3)))
	for i in range(n):
		j = (i + 1) % n
		f = b.bm.faces.new((rim[i], rim[j], floor[j], floor[i]))
		f.material_index = bowl
		f.smooth = False
		for loop, v in zip(f.loops, (rim[i], rim[j], floor[j], floor[i])):
			loop[b.uv].uv = ((v.co.x - centre.x + 0.2) / SMALL_TILE, (v.co.y - centre.y + 0.2) / SMALL_TILE)
		f2 = b.bm.faces.new((floor[i], floor[j], cv))
		f2.material_index = bowl
		f2.smooth = False
		for loop, v in zip(f2.loops, (floor[i], floor[j], cv)):
			loop[b.uv].uv = ((v.co.x - centre.x + 0.2) / SMALL_TILE, (v.co.y - centre.y + 0.2) / SMALL_TILE)
	# outer underside so the cup is closed from below
	under = []
	for i in range(n):
		a = 2 * math.pi * i / n
		under.append(b.bm.verts.new(centre + Vector((math.cos(a) * radius * 0.7, math.sin(a) * radius * 0.7, height * 0.2))))
	bc = b.bm.verts.new(centre + Vector((0, 0, 0.0)))
	for i in range(n):
		j = (i + 1) % n
		f = b.bm.faces.new((under[j], under[i], bc))
		f.material_index = bowl
		f.smooth = False
		for loop in f.loops:
			loop[b.uv].uv = (rng.uniform(0, 0.3), rng.uniform(0, 0.3))
		f = b.bm.faces.new((rim[j], rim[i], under[i], under[j]))
		f.material_index = twig
		f.smooth = False
		for loop, uv in zip(f.loops, ((0, 0.3), (0.3, 0.3), (0.3, 0.6), (0, 0.6))):
			loop[b.uv].uv = uv


def add_egg(b, rng, centre, size, mat, tilt=0.0, yaw=0.0):
	"""A faceted ovoid: 7 rings, 6 sides, fatter below the middle."""
	rot = Matrix.Rotation(yaw, 4, 'Z') @ Matrix.Rotation(tilt, 4, 'Y')
	rings = []
	profile = [(0.0, 0.0), (0.38, 0.12), (0.62, 0.28), (0.74, 0.46), (0.68, 0.64), (0.48, 0.82), (0.0, 1.0)]
	for r, z in profile:
		if r == 0.0:
			rings.append([b.bm.verts.new(centre + (rot @ Vector((0, 0, z * size)))) for _ in range(1)])
			continue
		ring = []
		for j in range(6):
			a = 2 * math.pi * j / 6 + rng.uniform(-0.1, 0.1)
			rr = r * size * 0.6 * rng.uniform(0.95, 1.05)
			ring.append(b.bm.verts.new(centre + (rot @ Vector((math.cos(a) * rr, math.sin(a) * rr, z * size)))))
		rings.append(ring)
	for i in range(len(rings) - 1):
		a, c = rings[i], rings[i + 1]
		for j in range(6):
			k = (j + 1) % 6
			if len(a) == 1:
				f = b.bm.faces.new((a[0], c[j], c[k]))
			elif len(c) == 1:
				f = b.bm.faces.new((a[j], a[k], c[0]))
			else:
				f = b.bm.faces.new((a[j], a[k], c[k], c[j]))
			f.material_index = mat
			f.smooth = False
			for loop in f.loops:
				loop[b.uv].uv = (j / 6 * 0.5 + rng.uniform(0, 0.05), i / 6 * 0.5)


def build_nest(name, seed, with_branch):
	rng = random.Random(seed)
	b = Builder(name, ["twig_v2", "nest_bowl", "egg", "nest_branch"])
	radius, height = 0.18, 0.11
	base = Vector((0, 0, 0))
	if with_branch:
		# A fork: the limb comes in from behind (-X), splits into two prongs the cup sits across.
		branch = b.mat("nest_branch")
		trunk_end = Vector((-0.42, 0.0, -0.06))
		fork = Vector((-0.1, 0.0, -0.02))
		b.tube([trunk_end, fork.lerp(trunk_end, 0.5), fork], [0.045, 0.04, 0.035], 6, branch, rng, SMALL_TILE)
		for side in (-1, 1):
			tip = Vector((0.26, side * 0.16, 0.03))
			mid = fork.lerp(tip, 0.5) + Vector((0, 0, 0.0))
			b.tube([fork, mid, tip], [0.032, 0.024, 0.012], 5, branch, rng, SMALL_TILE)
		base = Vector((0.06, 0.0, 0.0))
	add_cup(b, rng, base, radius, height)
	egg = b.mat("egg")
	for (ox, oy, yaw) in ((-0.03, 0.025, 0.4), (0.035, -0.02, -1.1)):
		add_egg(b, rng, base + Vector((ox, oy, height * 0.3)), 0.055, egg, tilt=math.radians(rng.uniform(55, 80)), yaw=yaw)
	return b.finish()


def build_egg(seed):
	rng = random.Random(seed)
	b = Builder("egg", ["egg"])
	add_egg(b, rng, Vector((0, 0, 0)), 0.07, b.mat("egg"), tilt=math.radians(90.0))
	obj = b.finish()
	# lie flat on the ground: lowest point at z 0
	mz = min(v.co.z for v in obj.data.vertices)
	for v in obj.data.vertices:
		v.co.z -= mz
	return obj


# ------------------------------------------------------------------------------------------------------------- export

def export_fbx(obj, rel_path):
	path = os.path.join(ASSETS, rel_path.replace("/", os.sep))
	os.makedirs(os.path.dirname(path), exist_ok=True)
	for o in bpy.data.objects:
		o.select_set(o is obj)
	bpy.context.view_layer.objects.active = obj
	obj.data.transform(EXPORT_ROT)
	bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={'MESH'}, apply_unit_scale=True,
							 global_scale=1.0, axis_forward='-Z', axis_up='Y', mesh_smooth_type='FACE',
							 add_leaf_bones=False, bake_anim=False, path_mode='STRIP', embed_textures=False)
	obj.data.transform(EXPORT_ROT.inverted())


def vmat_path(name):
	return TWIG_VMAT if name == "twig_v2" else f"{MAT_DIR}/{name}.vmat"


def write_vmdl(rel_vmdl, fbx, mesh, materials, note):
	remaps = "".join(f"""							{{
								from = "{m}.vmat"
								to = "{vmat_path(m)}"
							}},
""" for m in materials)
	text = f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
// {note} - Blender/scripts/create_bird_nests.py
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
			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "PhysicsHullFromRender"
						parent_bone = ""
						surface_prop = "wood"
						collision_tags = "solid"
						faceMergeAngle = 10.0
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
	with open(os.path.join(ASSETS, MAT_DIR.replace("/", os.sep), name + ".vmat"), "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


def guid(*parts):
	return str(uuid.uuid5(uuid.NAMESPACE_URL, "survivalgame2026/" + "/".join(parts)))


def write_nest_prefab(name, model, tree_nest):
	"""Like the beehive prefabs: renderer + hand-harvest ResourceItemDefinition (eggs 0-2, feathers 0-4, one press),
	plus BirdNest on the tree nest so a felled tree tips the contents onto the ground."""
	rel = f"{PREFAB_DIR}/{name}.prefab"
	renderer = {"__type": "Sandbox.ModelRenderer", "__guid": guid(rel, "renderer"), "__enabled": True, "Flags": 0,
				"BodyGroups": 18446744073709551615, "CreateAttachments": False, "LodOverride": None, "MaterialGroup": None,
				"MaterialOverride": None, "Materials": None, "Model": model,
				"RenderOptions": {"GameLayer": True, "OverlayLayer": False, "BloomLayer": False, "AfterUILayer": False},
				"RenderType": "On", "Tint": "1,1,1,1"}
	resource = {"__type": "Survival.ResourceItemDefinition", "__guid": guid(rel, "resource"), "__enabled": True, "Flags": 0,
				"AutoEnsureTraceCollider": True, "BaseYieldPerTickHigh": 1, "BaseYieldPerTickLow": 1,
				"HarvestYields": [
					{"AmountHigh": 2, "AmountLow": 0, "ChancePercent": 100, "ResourceId": "food_egg"},
					{"AmountHigh": 4, "AmountLow": 0, "ChancePercent": 100, "ResourceId": "resource_feathers"},
				],
				"DisplayName": "Bird Nest", "FallbackColor": "0.6,0.5,0.32,1", "HandHarvestRange": 150, "Harvestable": True,
				"HarvestTicksUntilGone": 1, "HideModelWhenDepleted": True, "Icon": "ui/items/food_egg.png", "LogHarvest": False,
				"MaxStack": 10, "MinimumToolTier": 0, "ResourceId": "world_bird_nest", "RespawnRateSeconds": 900,
				"ToolTypeRequired": "Hand"}
	components = [renderer, resource]
	if tree_nest:
		components.append({"__type": "Survival.BirdNest", "__guid": guid(rel, "nest"), "__enabled": True, "Flags": 0,
						   "TreeReachMeters": 4.0})
	root = {"__guid": guid(rel), "__version": 2, "Flags": 0, "Name": name, "Position": "0,0,0", "Rotation": "0,0,0,1",
			"Scale": "1,1,1", "Tags": "", "Enabled": True, "NetworkMode": 1, "NetworkFlags": 0, "NetworkOrphaned": 0,
			"NetworkTransmit": True, "OwnerTransfer": 1, "Components": components, "Children": [],
			"__properties": {"NetworkInterpolation": True, "TimeScale": 1, "WantsSystemScene": True, "Metadata": {},
							 "NavMesh": {"Enabled": False, "IncludeStaticBodies": True, "IncludeKeyframedBodies": True,
										 "EditorAutoUpdate": False, "AgentHeight": 64, "AgentRadius": 16, "AgentStepSize": 18,
										 "AgentMaxSlope": 40, "ExcludedBodies": "", "IncludedBodies": "", "DeferGeneration": False,
										 "CustomBounds": False}},
			"__variables": []}
	doc = {"RootObject": root, "ResourceVersion": 2, "ShowInMenu": False, "MenuPath": None, "MenuIcon": None,
		   "DontBreakAsTemplate": False, "__references": [], "__version": 2}
	with open(os.path.join(ASSETS, rel.replace("/", os.sep)), "w", encoding="utf-8", newline="\n") as f:
		json.dump(doc, f, indent=2)


def render(obj, tag, dist, elev):
	scene = bpy.context.scene
	scene.render.engine = 'BLENDER_EEVEE'
	scene.render.resolution_x = 800
	scene.render.resolution_y = 520
	if scene.world is None:
		world = bpy.data.worlds.new("w")
		world.use_nodes = True
		world.node_tree.nodes["Background"].inputs[0].default_value = (0.33, 0.37, 0.31, 1.0)
		scene.world = world
		sun = bpy.data.lights.new("sun", 'SUN')
		sun.energy = 4.0
		so = bpy.data.objects.new("sun", sun)
		scene.collection.objects.link(so)
		so.rotation_euler = (math.radians(50.0), 0.0, math.radians(-35.0))
		cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
		scene.collection.objects.link(cam)
		scene.camera = cam
	cam = scene.camera
	for o in bpy.data.objects:
		if o.type == 'MESH':
			o.hide_render = o is not obj
	xs = [v.co for v in obj.data.vertices]
	centre = Vector((sum(v.x for v in xs) / len(xs), sum(v.y for v in xs) / len(xs), (min(v.z for v in xs) + max(v.z for v in xs)) / 2))
	a = math.radians(-40.0)
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


def main():
	bpy.ops.wm.read_factory_settings(use_empty=True)
	rng = random.Random(2811)
	for name, fn in (("nest_bowl", tex_nest_bowl), ("egg", tex_egg), ("nest_branch", tex_branch)):
		save_png(name, fn(rng))
		write_vmat(name)
	ground = build_nest("nest_ground", 11, with_branch=False)
	tree = build_nest("nest_tree", 12, with_branch=True)
	egg = build_egg(13)
	for obj, rel_dir, note in ((ground, MODEL_DIR, "bird nest on the ground"), (tree, MODEL_DIR, "bird nest in a branch fork"), (egg, EGG_MODEL_DIR, "egg (inventory drop)")):
		fbx = f"{rel_dir}/{obj.name}.fbx"
		export_fbx(obj, fbx)
		write_vmdl(f"{rel_dir}/{obj.name}.vmdl", fbx, obj.name, [m.name for m in obj.data.materials], note)
		print(f"[nest] {obj.name}: {stats(obj)}")
	write_nest_prefab("nest_ground", f"{MODEL_DIR}/nest_ground.vmdl", tree_nest=False)
	write_nest_prefab("nest_tree", f"{MODEL_DIR}/nest_tree.vmdl", tree_nest=True)
	render(ground, "nest_ground", 0.7, 0.42)
	render(tree, "nest_tree", 1.0, 0.5)
	render(egg, "egg", 0.22, 0.12)
	print(f"[nest] renders in {RENDER_DIR}")


main()
