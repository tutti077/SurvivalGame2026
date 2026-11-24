"""
Clover Hills pick-up twig (Mark 2026-11-10, prompt 24: "clean up the twig model ... the one that players can just walk up
and pick up"). Replaces the blocky Y-stick (low_poly_stick) with a proper low-poly twig:

    v3 (2026-11-18): the stem arches off the ground like a fallen branch, butt and tip down, the middle 22 cm up.
    a bent main stem with a knobby, tapering 7-sided cross-section, a broken butt end, two side twigs and a snapped
    stub, every ring jittered so no facet repeats and nothing reads as a primitive; flat shaded.

Authored in metres (0.58 m long, then SIZE_SCALE 3 -> 1.74 m: Mark 2026-11-17 "make the twig much bigger, like 3x",
scaled in the model so the prefab stays at 1.0; the UVs scale with it so the bark keeps its density), exported like the rock kit (FBX in cm, vmdl import_scale 0.4 -> 40 u/m). The bark
texture is generated here too: 128 px over 0.5 m (256 texels / m, the item-model density), 4 low-contrast grey-brown
tones, longitudinal ridges and a sparse crack net - no hue shifts, moss, dither or speckle (the elm bark rule).

Writes
    Assets/models/environment/cloverHills/resources/twig_v3.fbx + .vmdl
    Assets/materials/environment/cloverHills/resources/twig_v3.png + .vmat   (pixel_lit, PixelFlatShading)
and renders two views to TWIG_RENDER_DIR (default Blender/renders/twig).

Run:  "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_twig.py
"""
import bpy
import bmesh
import math
import os
import random
from mathutils import Vector

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ASSETS = os.path.join(ROOT, "Assets")
MODEL_DIR = "models/environment/cloverHills/resources"
MAT_DIR = "materials/environment/cloverHills/resources"
NAME = "twig_v3"
TEX_PX = 128
TEX_METERS = 0.5          # the 128 px tile covers 0.5 m -> 256 texels / m
RENDER_DIR = os.environ.get("TWIG_RENDER_DIR", os.path.join(ROOT, "Blender", "renders", "twig"))
SEED = 2411
SIZE_SCALE = 3.0          # in-world size over the authored 0.58 m shape (geometry and UVs together)


# ----------------------------------------------------------------------------------------------------------- geometry

def basis(tangent):
	t = tangent.normalized()
	up = Vector((0.0, 0.0, 1.0)) if abs(t.z) < 0.9 else Vector((1.0, 0.0, 0.0))
	n1 = t.cross(up).normalized()
	n2 = t.cross(n1).normalized()
	return t, n1, n2


def ring_points(center, tangent, radius, sides, rng, roll, jitter=0.14, angle_jitter=0.22):
	_, n1, n2 = basis(tangent)
	pts = []
	for i in range(sides):
		a = roll + 2.0 * math.pi * i / sides + rng.uniform(-angle_jitter, angle_jitter) * (2.0 * math.pi / sides)
		r = radius * rng.uniform(1.0 - jitter, 1.0 + jitter)
		pts.append(center + n1 * (math.cos(a) * r) + n2 * (math.sin(a) * r))
	return pts


def tube(bm, uv, path, radii, sides, rng, butt_break=False, tip=True):
	"""Lofted rings along `path` (metres). Faces get cylindrical UVs: u around at the ring's circumference / TEX_METERS,
	v along the length / TEX_METERS, so the bark lands at the texture's density everywhere."""
	roll = rng.uniform(0.0, math.pi)
	rings = []
	lengths = [0.0]
	for i in range(1, len(path)):
		lengths.append(lengths[-1] + (path[i] - path[i - 1]).length)
	for i, p in enumerate(path):
		prev = path[max(i - 1, 0)]
		nxt = path[min(i + 1, len(path) - 1)]
		pts = ring_points(p, nxt - prev, radii[i], sides, rng, roll)
		roll += rng.uniform(-0.15, 0.15)
		rings.append([bm.verts.new(q) for q in pts])

	v0 = rng.uniform(0.0, 1.0)
	for i in range(len(rings) - 1):
		a, b = rings[i], rings[i + 1]
		circ_a = 2.0 * math.pi * radii[i] / TEX_METERS
		circ_b = 2.0 * math.pi * radii[i + 1] / TEX_METERS
		va = v0 + lengths[i] / TEX_METERS
		vb = v0 + lengths[i + 1] / TEX_METERS
		for j in range(sides):
			k = (j + 1) % sides
			try:
				f = bm.faces.new((a[j], a[k], b[k], b[j]))
			except ValueError:
				continue
			f.smooth = False
			ua0, ua1 = circ_a * j / sides, circ_a * (j + 1) / sides
			ub0, ub1 = circ_b * j / sides, circ_b * (j + 1) / sides
			for loop, (u, v) in zip(f.loops, ((ua0, va), (ua1, va), (ub1, vb), (ub0, vb))):
				loop[uv].uv = (u, v)

	# Butt: a broken end - a sunken, ragged fan so the cut reads as snapped, not sawn.
	start = rings[0]
	t, _, _ = basis(path[1] - path[0])
	if butt_break:
		centre = bm.verts.new(path[0] + t * (radii[0] * 0.55))
		for j in range(sides):
			k = (j + 1) % sides
			f = bm.faces.new((start[k], start[j], centre))
			f.smooth = False
			for loop in f.loops:
				loop[uv].uv = (rng.uniform(0.0, 0.3), rng.uniform(0.6, 0.9))
	else:
		f = bm.faces.new(tuple(reversed(start)))
		f.smooth = False
		for loop in f.loops:
			loop[uv].uv = (rng.uniform(0.0, 0.3), rng.uniform(0.0, 0.3))

	# Tip: a short point past the last ring.
	end = rings[-1]
	t, _, _ = basis(path[-1] - path[-2])
	if tip:
		apex = bm.verts.new(path[-1] + t * (radii[-1] * 1.6))
		for j in range(sides):
			k = (j + 1) % sides
			f = bm.faces.new((end[j], end[k], apex))
			f.smooth = False
			for loop in f.loops:
				loop[uv].uv = (rng.uniform(0.0, 0.3), rng.uniform(0.3, 0.6))
	else:
		f = bm.faces.new(tuple(end))
		f.smooth = False
		for loop in f.loops:
			loop[uv].uv = (rng.uniform(0.0, 0.3), rng.uniform(0.6, 0.9))


def stem_path(rng, length, points, wobble, sag):
	"""A gently bent run along +X with uneven segment lengths."""
	segs = [rng.uniform(0.7, 1.3) for _ in range(points - 1)]
	total = sum(segs)
	xs = [0.0]
	for s in segs:
		xs.append(xs[-1] + s / total * length)
	bend_phase = rng.uniform(0.0, math.pi)
	path = []
	for x in xs:
		t = x / length
		y = wobble * math.sin(t * math.pi * 0.9 + bend_phase) + rng.uniform(-0.004, 0.004)
		z = sag * math.sin(t * math.pi) * 0.5 + rng.uniform(-0.003, 0.003)
		path.append(Vector((x, y, z)))
	return path


def branch_path(rng, origin, direction, length, points):
	d = direction.normalized()
	path = [origin.copy()]
	cur = origin.copy()
	for i in range(1, points):
		step = length / (points - 1) * rng.uniform(0.8, 1.2)
		# curl upward a little as it goes, like a twig reaching for light
		d = (d + Vector((0.0, 0.0, 0.06)) + Vector((rng.uniform(-0.08, 0.08), rng.uniform(-0.08, 0.08), 0.0))).normalized()
		cur = cur + d * step
		path.append(cur.copy())
	return path


def radii_for(rng, points, r0, r1, knob=0.16):
	phase = rng.uniform(0.0, 6.0)
	out = []
	for i in range(points):
		t = i / (points - 1)
		r = r0 + (r1 - r0) * t
		r *= 1.0 + knob * math.sin(t * 7.0 + phase) * rng.uniform(0.5, 1.0)
		out.append(r)
	return out


def build_twig(seed):
	rng = random.Random(seed)
	bm = bmesh.new()
	uv = bm.loops.layers.uv.new("UVMap")

	length = 0.58
	# A fallen branch, not a stick laid flat (Mark 2026-11-18: "tip up and back down so they stand off the ground"): the
	# stem arches 7.5 cm (22 cm at SIZE_SCALE 3) between its butt and its tip, which both rest on the ground.
	stem = stem_path(rng, length, 9, wobble=0.035, sag=0.15)
	stem_r = radii_for(rng, len(stem), 0.021, 0.0085)
	tube(bm, uv, stem, stem_r, 7, rng, butt_break=True, tip=True)

	def point_on_stem(t):
		x = t * length
		for i in range(len(stem) - 1):
			if stem[i].x <= x <= stem[i + 1].x:
				f = (x - stem[i].x) / max(1e-6, stem[i + 1].x - stem[i].x)
				p = stem[i].lerp(stem[i + 1], f)
				r = stem_r[i] + (stem_r[i + 1] - stem_r[i]) * f
				return p, (stem[i + 1] - stem[i]).normalized(), r
		return stem[-1], (stem[-1] - stem[-2]).normalized(), stem_r[-1]

	def side_dir(tangent, yaw_deg, pitch_deg):
		yaw = math.radians(yaw_deg)
		pitch = math.radians(pitch_deg)
		flat = Vector((tangent.x, tangent.y, 0.0)).normalized()
		d = Vector((flat.x * math.cos(yaw) - flat.y * math.sin(yaw), flat.x * math.sin(yaw) + flat.y * math.cos(yaw), 0.0))
		return (d * math.cos(pitch) + Vector((0.0, 0.0, math.sin(pitch)))).normalized()

	# Side twig 1: long, forks off at 42 %.
	p, tan, r = point_on_stem(0.42)
	d = side_dir(tan, 44.0, 22.0)
	path = branch_path(rng, p + d * (r * 0.3), d, 0.17, 5)
	tube(bm, uv, path, radii_for(rng, len(path), r * 0.5, 0.0035, knob=0.1), 5, rng, butt_break=False, tip=True)

	# Side twig 2: shorter, the other side, further along.
	p, tan, r = point_on_stem(0.71)
	d = side_dir(tan, -52.0, 14.0)
	path = branch_path(rng, p + d * (r * 0.3), d, 0.115, 4)
	tube(bm, uv, path, radii_for(rng, len(path), r * 0.48, 0.003, knob=0.1), 5, rng, butt_break=False, tip=True)

	# Snapped stub near the butt: a knot that lost its twig.
	p, tan, r = point_on_stem(0.19)
	d = side_dir(tan, -115.0, 35.0)
	path = branch_path(rng, p + d * (r * 0.2), d, 0.028, 3)
	tube(bm, uv, path, [r * 0.42, r * 0.4, r * 0.36], 5, rng, butt_break=False, tip=False)

	bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

	# In-world size: the authored shape times SIZE_SCALE, UVs with it (they are metric, so the texel density holds).
	for v in bm.verts:
		v.co *= SIZE_SCALE
	for f in bm.faces:
		for loop in f.loops:
			loop[uv].uv = loop[uv].uv * SIZE_SCALE

	# Lie on the ground: lowest point at z = 0, origin under the stem's middle.
	min_z = min(v.co.z for v in bm.verts)
	mid = (stem[0] + stem[-1]) * 0.5 * SIZE_SCALE
	for v in bm.verts:
		v.co -= Vector((mid.x, mid.y, min_z))

	mesh = bpy.data.meshes.new(NAME)
	bm.to_mesh(mesh)
	bm.free()
	for poly in mesh.polygons:
		poly.use_smooth = False
	obj = bpy.data.objects.new(NAME, mesh)
	bpy.context.scene.collection.objects.link(obj)
	return obj


# ------------------------------------------------------------------------------------------------------------ texture

def write_bark_texture(path, seed):
	"""128 px bark: four low-contrast grey-brown tones in longitudinal ridges (bark runs along v), a sparse connected
	crack net with varied line weight, a few darker knot shadows. Point-filtered in engine."""
	rng = random.Random(seed)
	n = TEX_PX
	tones = [(0.40, 0.33, 0.26), (0.46, 0.39, 0.31), (0.35, 0.28, 0.22), (0.51, 0.45, 0.37)]
	# Ridge field: tone index chosen by a slow function of u with a slower drift along v.
	ridge_phase = [rng.uniform(0.0, 6.28) for _ in range(4)]
	px = [[None] * n for _ in range(n)]
	for y in range(n):
		for x in range(n):
			u = x / n
			v = y / n
			drift = 0.08 * math.sin(v * 6.28 * 1.0 + ridge_phase[0]) + 0.05 * math.sin(v * 6.28 * 2.3 + ridge_phase[1])
			s = math.sin((u + drift) * 6.28 * 5.0 + ridge_phase[2]) + 0.6 * math.sin((u - drift) * 6.28 * 11.0 + ridge_phase[3])
			if s > 0.9:
				idx = 3
			elif s > 0.1:
				idx = 1
			elif s > -0.8:
				idx = 0
			else:
				idx = 2
			px[y][x] = list(tones[idx])

	def darken(x, y, k):
		x %= n
		y %= n
		c = px[y][x]
		px[y][x] = [c[0] * k, c[1] * k, c[2] * k]

	# Cracks: 7 mostly-longitudinal polylines, 1-2 px wide, some branching into a neighbour.
	crack_x = []
	for i in range(7):
		x = rng.uniform(0.0, n)
		crack_x.append(x)
		y = rng.randint(0, n - 1)
		run = rng.randint(n // 2, n + n // 3)
		width = 2 if rng.random() < 0.35 else 1
		for step in range(run):
			y = (y + 1)
			x += rng.uniform(-0.45, 0.45)
			if rng.random() < 0.02:
				x += rng.choice((-2.0, 2.0))
			for w in range(width):
				darken(int(x) + w, y, 0.58)
			if width == 2 and rng.random() < 0.3:
				darken(int(x) - 1, y, 0.78)
	# Short connecting cracks between neighbours.
	for i in range(5):
		y = rng.randint(0, n - 1)
		x0 = rng.choice(crack_x)
		x1 = x0 + rng.uniform(-14.0, 14.0)
		steps = int(abs(x1 - x0)) + 1
		for s in range(steps):
			x = x0 + (x1 - x0) * s / max(1, steps - 1)
			y += rng.choice((0, 0, 1, -1))
			darken(int(x), y, 0.66)
	# One faint knot: a small oval a shade darker, ringed by a crack (blotches read as dirt, so just one, and light).
	cx, cy = rng.uniform(0, n), rng.uniform(0, n)
	rx, ry = 2.5, 4.5
	for y in range(int(cy - ry) - 1, int(cy + ry) + 2):
		for x in range(int(cx - rx) - 1, int(cx + rx) + 2):
			d = ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2
			if 0.6 < d < 1.0:
				darken(x, y, 0.72)
			elif d <= 0.6:
				darken(x, y, 0.9)

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
// Clover Hills pick-up twig - Blender/scripts/create_twig.py
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
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)


def write_vmat(rel_vmat, rel_png):
	text = f"""Layer0
{{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.950"
	PixelSpecular "0.250"
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
	scene.render.resolution_y = 500
	scene.render.film_transparent = False
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

	# Ground slab so the twig sits on something.
	bpy.ops.mesh.primitive_plane_add(size=2.0 * SIZE_SCALE, location=(0.0, 0.0, -0.001))
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
	centre = Vector((0.0, 0.0, 0.03)) * SIZE_SCALE
	for tag, yaw, dist, elev in (("a", 35.0, 0.55, 0.28), ("b", 200.0, 0.42, 0.16)):
		dist *= SIZE_SCALE
		elev *= SIZE_SCALE
		a = math.radians(yaw)
		cam.location = centre + Vector((math.cos(a) * dist, math.sin(a) * dist, elev))
		cam.rotation_euler = (centre - cam.location).to_track_quat('-Z', 'Y').to_euler()
		scene.render.filepath = os.path.join(RENDER_DIR, f"{NAME}_{tag}.png")
		bpy.ops.render.render(write_still=True)
	# Close-up on the texture.
	cam.location = centre + Vector((0.08, -0.16, 0.09)) * SIZE_SCALE
	cam.rotation_euler = (centre - cam.location).to_track_quat('-Z', 'Y').to_euler()
	scene.render.filepath = os.path.join(RENDER_DIR, f"{NAME}_close.png")
	bpy.ops.render.render(write_still=True)


def main():
	bpy.ops.wm.read_factory_settings(use_empty=True)
	obj = build_twig(SEED)
	tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
	xs = [v.co.x for v in obj.data.vertices]
	ys = [v.co.y for v in obj.data.vertices]
	zs = [v.co.z for v in obj.data.vertices]
	print(f"[twig] {NAME}: {len(obj.data.vertices)} verts, {tris} tris, "
		  f"{max(xs) - min(xs):.3f} x {max(ys) - min(ys):.3f} x {max(zs) - min(zs):.3f} m")

	png_rel = f"{MAT_DIR}/{NAME}.png"
	vmat_rel = f"{MAT_DIR}/{NAME}.vmat"
	fbx_rel = f"{MODEL_DIR}/{NAME}.fbx"
	vmdl_rel = f"{MODEL_DIR}/{NAME}.vmdl"
	os.makedirs(os.path.join(ASSETS, MAT_DIR.replace("/", os.sep)), exist_ok=True)
	img = write_bark_texture(os.path.join(ASSETS, png_rel.replace("/", os.sep)), SEED + 7)
	write_vmat(vmat_rel, png_rel)
	export_fbx(obj, fbx_rel)
	write_vmdl(vmdl_rel, fbx_rel, NAME, vmat_rel)
	render(obj, img)
	print(f"[twig] wrote {fbx_rel}, {vmdl_rel}, {png_rel}, {vmat_rel}; renders in {RENDER_DIR}")


if __name__ == "__main__":
	main()
