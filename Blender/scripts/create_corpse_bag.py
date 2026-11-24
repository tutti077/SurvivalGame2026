"""
Player corpse bag (Mark 2026-11-19: "Should look like a burlap sack tied with black string. About the same width as
players, about knee high"). The model for the death loot container (Code/Inventory/DeathLootBag.cs):

    a slumped, lumpy burlap sack 0.62 m across and 0.52 m tall: flat sagging bottom, a belly that leans to one side,
    a gathered neck with pleats, a ruffle of loose cloth above the tie, and a black string wound twice round the neck
    with two loose ends hanging down the front. 12-sided rings, every ring jittered, flat shaded.

One 256 px texture: the top three quarters are the burlap weave (periodic in u, so the cylindrical UVs wrap twice round
the sack: ~270 texels / m), the bottom strip is the black string. Authored in metres, exported like the rock kit (FBX,
vmdl import_scale 0.4 -> 40 u/m), origin on the ground under the sack's middle.

Writes
    Assets/models/props/corpse_bag/corpse_bag_v1.fbx + .vmdl
    Assets/models/props/corpse_bag/corpse_bag_v1.png + .vmat   (pixel_lit, PixelFlatShading)
and renders three views to CORPSE_BAG_RENDER_DIR (default Blender/renders/corpse_bag).

Run:  "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_corpse_bag.py
"""
import bpy
import bmesh
import math
import os
import random
from mathutils import Vector

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ASSETS = os.path.join(ROOT, "Assets")
MODEL_DIR = "models/props/corpse_bag"
NAME = "corpse_bag_v1"
TEX_PX = 256
BURLAP_V = 0.75           # the burlap fills v 0 .. 0.75 of the texture, the string v 0.80 .. 1
RENDER_DIR = os.environ.get("CORPSE_BAG_RENDER_DIR", os.path.join(ROOT, "Blender", "renders", "corpse_bag"))
SEED = 1119
SIDES = 12

# (height m, radius m, pleat depth 0-1): bottom to top. Pleats pull every other side inward (the gathered neck).
PROFILE = [
	(0.000, 0.235, 0.00),
	(0.050, 0.300, 0.00),
	(0.160, 0.312, 0.03),
	(0.270, 0.280, 0.06),
	(0.350, 0.200, 0.14),
	(0.405, 0.105, 0.26),
	(0.430, 0.078, 0.30),   # the tie
	(0.455, 0.098, 0.30),
	(0.490, 0.140, 0.34),
	(0.520, 0.165, 0.40),   # ruffle rim
]
TIE_INDEX = 6


def build_bag(seed):
	rng = random.Random(seed)
	bm = bmesh.new()
	uv = bm.loops.layers.uv.new("UVMap")

	lean = Vector((rng.uniform(0.03, 0.05), rng.uniform(-0.03, 0.03), 0.0))   # the belly slumps to one side
	lump_phase = [rng.uniform(0.0, 6.28) for _ in range(3)]
	side_jitter = [rng.uniform(-0.18, 0.18) for _ in range(SIDES)]

	def centre_at(z):
		t = z / PROFILE[-1][0]
		return lean * math.sin(t * math.pi * 0.85) + Vector((0.0, 0.0, z))

	# cumulative length along the profile -> v
	lengths = [0.0]
	for i in range(1, len(PROFILE)):
		dz = PROFILE[i][0] - PROFILE[i - 1][0]
		dr = PROFILE[i][1] - PROFILE[i - 1][1]
		lengths.append(lengths[-1] + math.hypot(dz, dr))
	total = lengths[-1]

	rings = []
	for ri, (z, radius, pleat) in enumerate(PROFILE):
		ring = []
		c = centre_at(z)
		for s in range(SIDES):
			a = 2.0 * math.pi * (s + side_jitter[s]) / SIDES
			r = radius
			# lumps: what is inside pushes the cloth out unevenly, most at the belly
			belly = math.sin(min(1.0, z / 0.35) * math.pi)
			r *= 1.0 + belly * (0.07 * math.sin(a * 2.0 + lump_phase[0]) + 0.05 * math.sin(a * 3.0 + lump_phase[1] + z * 9.0))
			r *= 1.0 + rng.uniform(-0.035, 0.035)
			if s % 2 == 1:
				r *= 1.0 - pleat
			else:
				r *= 1.0 + pleat * 0.25
			zz = z + rng.uniform(-0.008, 0.008) * (0.0 if ri == 0 else 1.0)
			if ri == len(PROFILE) - 1:
				zz += 0.022 * math.sin(a * 3.0 + lump_phase[2]) + rng.uniform(-0.012, 0.012)   # ragged rim
			ring.append(bm.verts.new(Vector((c.x + math.cos(a) * r, c.y + math.sin(a) * r * 0.92, zz))))
		rings.append(ring)

	def side_faces(a, b, va, vb):
		for s in range(SIDES):
			k = (s + 1) % SIDES
			f = bm.faces.new((a[s], a[k], b[k], b[s]))
			f.smooth = False
			u0 = 2.0 * s / SIDES
			u1 = 2.0 * (s + 1) / SIDES
			for loop, (u, v) in zip(f.loops, ((u0, va), (u1, va), (u1, vb), (u0, vb))):
				loop[uv].uv = (u, v)

	for i in range(len(rings) - 1):
		side_faces(rings[i], rings[i + 1], lengths[i] / total * BURLAP_V * 0.98 + 0.005, lengths[i + 1] / total * BURLAP_V * 0.98 + 0.005)

	# bottom fan (rests on the ground)
	c = bm.verts.new(centre_at(0.0))
	for s in range(SIDES):
		k = (s + 1) % SIDES
		f = bm.faces.new((rings[0][k], rings[0][s], c))
		f.smooth = False
		for loop, (u, v) in zip(f.loops, ((0.30, 0.10), (0.10, 0.10), (0.20, 0.30))):
			loop[uv].uv = (u, v)

	# inside of the ruffle: the cloth folds down into the mouth, a dark dip
	mouth_c = centre_at(PROFILE[TIE_INDEX + 1][0] + 0.01)
	mouth = bm.verts.new(mouth_c)
	top = rings[-1]
	for s in range(SIDES):
		k = (s + 1) % SIDES
		f = bm.faces.new((top[s], top[k], mouth))
		f.smooth = False
		for loop, (u, v) in zip(f.loops, ((0.55, 0.62), (0.70, 0.62), (0.62, 0.72))):
			loop[uv].uv = (u, v)

	# ---- the black string: two turns round the neck, then two loose ends down the front
	string_v0, string_v1 = 0.84, 0.96

	def string_tube(path, radius, sides=4):
		prev_ring = None
		for i, p in enumerate(path):
			a = path[max(i - 1, 0)]
			b = path[min(i + 1, len(path) - 1)]
			t = (b - a).normalized()
			up = Vector((0.0, 0.0, 1.0)) if abs(t.z) < 0.9 else Vector((1.0, 0.0, 0.0))
			n1 = t.cross(up).normalized()
			n2 = t.cross(n1).normalized()
			ring = [bm.verts.new(p + n1 * (math.cos(2.0 * math.pi * j / sides + 0.6) * radius) + n2 * (math.sin(2.0 * math.pi * j / sides + 0.6) * radius)) for j in range(sides)]
			if prev_ring is not None:
				for j in range(sides):
					k = (j + 1) % sides
					f = bm.faces.new((prev_ring[j], prev_ring[k], ring[k], ring[j]))
					f.smooth = False
					for loop, (u, v) in zip(f.loops, ((0.1, string_v0), (0.4, string_v0), (0.4, string_v1), (0.1, string_v1))):
						loop[uv].uv = (u, v)
			prev_ring = ring
		return prev_ring

	tie_z, tie_r, _ = PROFILE[TIE_INDEX]
	tie_c = centre_at(tie_z)
	wind = []
	turns = 2.15
	steps = 26
	for i in range(steps + 1):
		t = i / steps
		a = t * turns * 2.0 * math.pi + 1.1
		r = tie_r * 1.06 + 0.012
		wind.append(Vector((tie_c.x + math.cos(a) * r, tie_c.y + math.sin(a) * r * 0.92, tie_z - 0.014 + t * 0.028)))
	end_ring = string_tube(wind, 0.0095)
	f = bm.faces.new(tuple(end_ring))
	f.smooth = False
	for loop in f.loops:
		loop[uv].uv = (0.25, 0.9)

	# loose ends: from the knot at the front, hanging down over the shoulder of the sack
	knot = wind[-1]
	out = Vector((knot.x - tie_c.x, knot.y - tie_c.y, 0.0)).normalized()
	side = Vector((-out.y, out.x, 0.0))
	for sign, length in ((1.0, 0.17), (-1.0, 0.12)):
		path = [knot.copy()]
		for i in range(1, 6):
			t = i / 5.0
			p = knot + out * (0.035 + 0.10 * t) + side * (sign * (0.02 + 0.045 * t)) + Vector((0.0, 0.0, 0.012 - length * t * t - 0.03 * t))
			path.append(p)
		tip_ring = string_tube(path, 0.008, sides=3)
		f = bm.faces.new(tuple(tip_ring))
		f.smooth = False
		for loop in f.loops:
			loop[uv].uv = (0.25, 0.9)

	bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

	# origin: on the ground under the middle of the footprint
	xs = [v.co.x for v in bm.verts]
	ys = [v.co.y for v in bm.verts]
	min_z = min(v.co.z for v in bm.verts)
	off = Vector(((max(xs) + min(xs)) * 0.5, (max(ys) + min(ys)) * 0.5, min_z))
	for v in bm.verts:
		v.co -= off

	mesh = bpy.data.meshes.new(NAME)
	bm.to_mesh(mesh)
	bm.free()
	for poly in mesh.polygons:
		poly.use_smooth = False
	obj = bpy.data.objects.new(NAME, mesh)
	bpy.context.scene.collection.objects.link(obj)
	return obj


# ------------------------------------------------------------------------------------------------------------ texture

def write_texture(path, seed):
	"""256 px: burlap in v 0 .. 0.75 (a coarse plain weave, 4 px pitch, four low-contrast tan tones, a few slubs and two
	stitched seams; darker toward the neck where the cloth bunches), black string with a faint twist in v 0.78 .. 1.
	Periodic in u. Row 0 of the image is v = 0 (Blender image origin is bottom-left)."""
	rng = random.Random(seed)
	n = TEX_PX
	tones = [(0.52, 0.42, 0.27), (0.58, 0.48, 0.32), (0.45, 0.36, 0.23), (0.63, 0.53, 0.36)]
	burlap_rows = int(n * 0.78)
	px = [[None] * n for _ in range(n)]

	col_shift = [rng.choice((0, 0, 0, 1)) for _ in range(n // 4)]
	row_tone = [rng.choice((0, 0, 1, 1, 3)) for _ in range(n // 4)]
	col_tone = [rng.choice((0, 0, 1, 2)) for _ in range(n // 4)]
	for y in range(n):
		for x in range(n):
			if y >= burlap_rows:
				# string: near-black with a diagonal twist one shade lighter
				twist = ((x + y * 2) // 3) % 4 == 0
				c = (0.065, 0.06, 0.06) if twist else (0.035, 0.032, 0.035)
				px[y][x] = list(c)
				continue
			cx, cy = x // 4, y // 4
			over = (cx + cy) % 2 == 0        # which thread is on top in this cell
			inx, iny = x % 4, (y + col_shift[cx]) % 4
			if over:
				idx = row_tone[cy]
				gap = iny == 3               # the shadow line between two weft threads
			else:
				idx = col_tone[cx]
				gap = inx == 3
			c = list(tones[idx])
			if gap:
				c = [c[0] * 0.82, c[1] * 0.80, c[2] * 0.78]   # a soft shadow: the stronger one read as a zigzag pattern
			px[y][x] = c

	def mul(x, y, k):
		if 0 <= y < burlap_rows:
			c = px[y][x % n]
			px[y][x % n] = [c[0] * k, c[1] * k, c[2] * k]

	# slubs: short thick threads a shade lighter, along the weft
	for _ in range(26):
		y = rng.randrange(0, burlap_rows // 4) * 4 + 1
		x0 = rng.randrange(0, n)
		for x in range(x0, x0 + rng.randint(6, 16)):
			mul(x, y, 1.13)
			mul(x, y + 1, 1.08)
	# two stitched side seams: a darker line with regular stitches
	for seam_x in (n // 8, n // 8 + n // 2):
		for y in range(burlap_rows):
			mul(seam_x, y, 0.62)
			if (y // 5) % 2 == 0:
				mul(seam_x - 2, y, 0.8)
				mul(seam_x + 2, y, 0.8)
	# the cloth bunches toward the neck: darker pleat shadows fading in over the top third of the burlap band
	pleats = [rng.randrange(0, n) for _ in range(14)]
	for y in range(int(burlap_rows * 0.62), burlap_rows):
		t = (y - burlap_rows * 0.62) / (burlap_rows * 0.38)
		for p in pleats:
			mul(p, y, 1.0 - 0.34 * t)
			mul(p + 1, y, 1.0 - 0.18 * t)
	# ground dirt: the bottom rows a little darker
	for y in range(0, 14):
		k = 0.80 + 0.2 * (y / 14.0)
		for x in range(n):
			mul(x, y, k)

	img = bpy.data.images.new(NAME, n, n, alpha=False)
	flat = []
	for y in range(n):
		for x in range(n):
			c = px[y][x]
			flat.extend((c[0], c[1], c[2], 1.0))
	img.pixels = flat
	img.filepath_raw = path
	img.file_format = 'PNG'
	img.save()
	return img


# ------------------------------------------------------------------------------------------------------------- export

def export_fbx(obj, rel_path):
	path = os.path.join(ASSETS, rel_path.replace("/", os.sep))
	os.makedirs(os.path.dirname(path), exist_ok=True)
	for o in bpy.data.objects:
		o.select_set(False)
	obj.select_set(True)
	bpy.context.view_layer.objects.active = obj
	bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={'MESH'}, apply_unit_scale=True,
							 global_scale=1.0, axis_forward='-Z', axis_up='Y', mesh_smooth_type='FACE',
							 add_leaf_bones=False, bake_anim=False, path_mode='STRIP', embed_textures=False)


def write_vmdl(rel_vmdl, fbx, mesh, vmat):
	text = f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
// Player corpse bag - Blender/scripts/create_corpse_bag.py
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
						global_default_material = "{vmat}"
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
						surface_prop = "default"
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
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


def write_vmat(rel_vmat, rel_png):
	text = f"""Layer0
{{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.950"
	PixelSpecular "0.150"
	PixelFlatShading "1.000"
	TextureColor "{rel_png}"
}}
"""
	path = os.path.join(ASSETS, rel_vmat.replace("/", os.sep))
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


# ------------------------------------------------------------------------------------------------------------- render

def render(obj, img):
	scene = bpy.context.scene
	scene.render.engine = 'BLENDER_EEVEE'
	scene.render.resolution_x = 800
	scene.render.resolution_y = 600
	world = bpy.data.worlds.new("w")
	world.use_nodes = True
	world.node_tree.nodes["Background"].inputs[0].default_value = (0.32, 0.36, 0.30, 1.0)
	scene.world = world

	mat = bpy.data.materials.new(NAME)
	mat.use_nodes = True
	bsdf = mat.node_tree.nodes.get("Principled BSDF")
	tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
	tex.image = img
	tex.interpolation = 'Closest'
	mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
	bsdf.inputs["Roughness"].default_value = 0.95
	obj.data.materials.append(mat)

	bpy.ops.mesh.primitive_plane_add(size=4.0, location=(0.0, 0.0, -0.001))
	ground = bpy.context.active_object
	gm = bpy.data.materials.new("ground")
	gm.use_nodes = True
	gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.27, 0.36, 0.2, 1.0)
	ground.data.materials.append(gm)

	sun = bpy.data.lights.new("sun", 'SUN')
	sun.energy = 4.0
	sun.angle = math.radians(4.0)
	so = bpy.data.objects.new("sun", sun)
	scene.collection.objects.link(so)
	so.rotation_euler = (math.radians(52.0), 0.0, math.radians(35.0))

	cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
	scene.collection.objects.link(cam)
	scene.camera = cam
	os.makedirs(RENDER_DIR, exist_ok=True)
	centre = Vector((0.0, 0.0, 0.24))
	for tag, yaw, dist, elev in (("a", 35.0, 1.5, 0.75), ("b", 200.0, 1.5, 0.45), ("top", 120.0, 0.9, 1.3)):
		a = math.radians(yaw)
		cam.location = centre + Vector((math.cos(a) * dist, math.sin(a) * dist, elev))
		cam.rotation_euler = (centre - cam.location).to_track_quat('-Z', 'Y').to_euler()
		scene.render.filepath = os.path.join(RENDER_DIR, f"{NAME}_{tag}.png")
		bpy.ops.render.render(write_still=True)


def main():
	bpy.ops.wm.read_factory_settings(use_empty=True)
	obj = build_bag(SEED)
	tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
	xs = [v.co.x for v in obj.data.vertices]
	ys = [v.co.y for v in obj.data.vertices]
	zs = [v.co.z for v in obj.data.vertices]
	print(f"[corpse_bag] {NAME}: {len(obj.data.vertices)} verts, {tris} tris, "
		  f"{max(xs) - min(xs):.3f} x {max(ys) - min(ys):.3f} x {max(zs) - min(zs):.3f} m")

	png_rel = f"{MODEL_DIR}/{NAME}.png"
	vmat_rel = f"{MODEL_DIR}/{NAME}.vmat"
	fbx_rel = f"{MODEL_DIR}/{NAME}.fbx"
	vmdl_rel = f"{MODEL_DIR}/{NAME}.vmdl"
	os.makedirs(os.path.join(ASSETS, MODEL_DIR.replace("/", os.sep)), exist_ok=True)
	img = write_texture(os.path.join(ASSETS, png_rel.replace("/", os.sep)), SEED + 7)
	write_vmat(vmat_rel, png_rel)
	export_fbx(obj, fbx_rel)
	write_vmdl(vmdl_rel, fbx_rel, NAME, vmat_rel)
	render(obj, img)
	print(f"[corpse_bag] wrote {fbx_rel}, {vmdl_rel}, {png_rel}, {vmat_rel}; renders in {RENDER_DIR}")


if __name__ == "__main__":
	main()
