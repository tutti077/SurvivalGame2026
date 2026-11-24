"""
Rumple's chest, split for a working lid (Mark 2026-11-10, prompt 25: "animate rumples chest to be shut by default and
open when a player accesses it").

The artist FBX (models/building/chest.fbx) is modelled OPEN: one mesh of nine loose parts - the box, the lid, hinge
knuckles, a hasp. This script separates the loose parts, sorts them into body and lid, finds the hinge (the lid's
lowest edge), rotates the lid SHUT onto the rim, and exports one FBX with two meshes:

    chest_body  - the box and everything that stays put, at the original origin
    chest_lid   - the lid and hasp tongue, CLOSED, still in the body's frame

plus chest_body.vmdl / chest_lid.vmdl (import_scale 0.4, materials/building/chest.vmat like the original). The
prefab nests the pivot: Lid (at the printed hinge offset) > LidMesh (at minus that offset) > renderer, and ChestLid
(Code/Building/ChestLid.cs) swings Lid about the printed axis by the printed open angle - the angle Rumple
modelled it at. Engine axes on this export convention: engine X = -Blender Y, engine Y = Blender X.

Run (prints the part table, then writes):
    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/split_chest_lid.py
"""
import bpy
import math
import os
from mathutils import Vector, Matrix

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ASSETS = os.path.join(ROOT, "Assets")
FBX_IN = os.path.join(ASSETS, "models", "building", "chest.fbx")
FBX_OUT_REL = "models/building/chest_split.fbx"
VMAT = "materials/building/chest.vmat"
UNITS_PER_BLENDER_METER = 40.0   # FBX cm x vmdl import_scale 0.4
RENDER_DIR = os.environ.get("CHEST_RENDER_DIR", os.path.join(ROOT, "Blender", "renders", "chest"))
# Loose parts (by import order) that straddle the rim but belong to the box: the hinge knuckles. Set after reading
# the part table the first run prints.
FORCE_BODY = {"chest.005", "chest.007"}
FORCE_LID = set()


def world_verts(o):
	return [o.matrix_world @ v.co for v in o.data.vertices]


def bbox(o):
	vs = world_verts(o)
	return (Vector((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs))),
			Vector((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs))))


def centroid(o):
	vs = world_verts(o)
	return sum(vs, Vector()) / len(vs)


def pca_normal(vs):
	c = sum(vs, Vector()) / len(vs)
	m = Matrix(((0, 0, 0), (0, 0, 0), (0, 0, 0)))
	for v in vs:
		d = v - c
		for i in range(3):
			for j in range(3):
				m[i][j] += d[i] * d[j]
	# Smallest-variance axis by power iteration on the inverse is overkill: brute-force a sphere of candidates.
	best, best_var = None, 1e30
	for i in range(0, 180, 3):
		for j in range(0, 360, 3):
			a, b = math.radians(i), math.radians(j)
			n = Vector((math.sin(a) * math.cos(b), math.sin(a) * math.sin(b), math.cos(a)))
			var = n.dot(Vector((m[0][0] * n.x + m[0][1] * n.y + m[0][2] * n.z,
								 m[1][0] * n.x + m[1][1] * n.y + m[1][2] * n.z,
								 m[2][0] * n.x + m[2][1] * n.y + m[2][2] * n.z)))
			if var < best_var:
				best, best_var = n, var
	return best


def render_closed(body_o, lid_o, hinge, mat=None):
	"""Two views of the shut chest (lid shown at its hinge offset) so the split can be judged by eye."""
	scene = bpy.context.scene
	scene.render.engine = 'BLENDER_EEVEE'
	scene.render.resolution_x = 720
	scene.render.resolution_y = 520
	world = bpy.data.worlds.new("w")
	world.use_nodes = True
	world.node_tree.nodes["Background"].inputs[0].default_value = (0.3, 0.33, 0.3, 1.0)
	scene.world = world
	sun = bpy.data.lights.new("sun", 'SUN')
	sun.energy = 3.5
	so = bpy.data.objects.new("sun", sun)
	scene.collection.objects.link(so)
	so.rotation_euler = (math.radians(50.0), 0.0, math.radians(-40.0))
	cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
	scene.collection.objects.link(cam)
	scene.camera = cam
	os.makedirs(RENDER_DIR, exist_ok=True)
	centre = Vector((0.0, -0.1, -0.2))
	for tag, yaw, dist, elev in (("front", -60.0, 1.6, 0.9), ("back", 70.0, 1.3, 0.7)):
		a = math.radians(yaw)
		cam.location = centre + Vector((math.cos(a) * dist, math.sin(a) * dist, elev))
		cam.rotation_euler = (centre - cam.location).to_track_quat('-Z', 'Y').to_euler()
		scene.render.filepath = os.path.join(RENDER_DIR, f"chest_closed_{tag}.png")
		bpy.ops.render.render(write_still=True)


def main():
	bpy.ops.wm.read_factory_settings(use_empty=True)
	bpy.ops.import_scene.fbx(filepath=FBX_IN)
	src = [o for o in bpy.data.objects if o.type == 'MESH'][0]
	mat = src.data.materials[0] if src.data.materials else None
	for o in bpy.data.objects:
		o.select_set(o == src)
	bpy.context.view_layer.objects.active = src
	bpy.ops.object.mode_set(mode='EDIT')
	bpy.ops.mesh.select_all(action='SELECT')
	bpy.ops.mesh.separate(type='LOOSE')
	bpy.ops.object.mode_set(mode='OBJECT')
	parts = [o for o in bpy.data.objects if o.type == 'MESH']

	# The box: the part that reaches the floor with the most faces.
	floor_z = min(bbox(p)[0].z for p in parts)
	body = max((p for p in parts if bbox(p)[0].z < floor_z + 0.01), key=lambda p: len(p.data.polygons))
	rim_top = bbox(body)[1].z
	body_c = centroid(body)

	print("[chest] parts (world, metres):")
	lid_parts, body_parts = [], [body]
	for p in parts:
		lo, hi = bbox(p)
		c = centroid(p)
		tag = "BODY" if p is body else ("lid" if c.z > rim_top + 0.01 else "body")
		if p.name in FORCE_BODY:
			tag = "body"
		elif p.name in FORCE_LID:
			tag = "lid"
		print(f"   {p.name:14s} faces {len(p.data.polygons):4d}  z {lo.z:+.3f}..{hi.z:+.3f}  c ({c.x:+.3f},{c.y:+.3f},{c.z:+.3f})  -> {tag}")
		if p is body:
			continue
		(lid_parts if tag == "lid" else body_parts).append(p)

	# Hinge: the lid's lowest verts; axis = the horizontal direction they spread along.
	lid_vs = [v for p in lid_parts for v in world_verts(p)]
	lid_min_z = min(v.z for v in lid_vs)
	low = [v for v in lid_vs if v.z < lid_min_z + 0.015]
	hinge = sum(low, Vector()) / len(low)
	spread_x = max(v.x for v in low) - min(v.x for v in low)
	spread_y = max(v.y for v in low) - min(v.y for v in low)
	axis = Vector((1, 0, 0)) if spread_x >= spread_y else Vector((0, 1, 0))

	# Tilt: the main lid panel's normal against up.
	panel = max(lid_parts, key=lambda p: len(p.data.polygons))
	n = pca_normal(world_verts(panel))
	if n.z < 0:
		n = -n
	tilt = math.degrees(math.acos(max(-1.0, min(1.0, n.z))))

	# Rotate shut: the sign that brings the lid's centroid over the box.
	lid_c = sum(lid_vs, Vector()) / len(lid_vs)
	best = None
	for sign in (1.0, -1.0):
		rot = Matrix.Rotation(math.radians(sign * tilt), 4, axis)
		moved = rot @ (lid_c - hinge) + hinge
		d = (Vector((moved.x, moved.y, 0)) - Vector((body_c.x, body_c.y, 0))).length
		if best is None or d < best[0]:
			best = (d, sign, rot)
	_, sign, rot = best
	print(f"[chest] rim top {rim_top:+.3f} m, hinge ({hinge.x:+.3f},{hinge.y:+.3f},{hinge.z:+.3f}) m, axis {tuple(axis)}, "
		  f"modelled open {tilt:.1f} deg, closing sign {sign:+.0f}")

	# Build the two export meshes in world space: body at the origin, lid relative to the hinge, closed.
	for p in body_parts:
		p.data.transform(p.matrix_world)
		p.matrix_world = Matrix.Identity(4)
	# The lid stays in the BODY frame (the exporter re-centres a mesh whose origin is moved); the prefab nests the
	# pivot instead: Lid (at the hinge) > LidMesh (at -hinge) > renderer.
	for p in lid_parts:
		p.data.transform(Matrix.Translation(hinge) @ rot @ Matrix.Translation(-hinge) @ p.matrix_world)
		p.matrix_world = Matrix.Identity(4)

	def join(objs, name):
		for o in bpy.data.objects:
			o.select_set(o in objs)
		bpy.context.view_layer.objects.active = objs[0]
		bpy.ops.object.join()
		o = bpy.context.view_layer.objects.active
		o.name = name
		o.data.name = name
		if mat and not o.data.materials:
			o.data.materials.append(mat)
		return o

	for p in lid_parts:
		lo, hi = bbox(p)
		print(f"[chest]   closed {p.name:10s} world z {lo.z:+.3f}..{hi.z:+.3f}")
	body_o = join(body_parts, "chest_body")
	lid_o = join(lid_parts, "chest_lid")
	closed_lo, closed_hi = bbox(lid_o)
	blo, bhi = bbox(body_o)
	print(f"[chest] BODY bbox x {blo.x:+.3f}..{bhi.x:+.3f} y {blo.y:+.3f}..{bhi.y:+.3f} z {blo.z:+.3f}..{bhi.z:+.3f}")
	print(f"[chest] LID  bbox x {closed_lo.x:+.3f}..{closed_hi.x:+.3f} y {closed_lo.y:+.3f}..{closed_hi.y:+.3f} z {closed_lo.z:+.3f}..{closed_hi.z:+.3f} (body frame)")
	print(f"[chest] closed lid z {closed_lo.z:+.3f}..{closed_hi.z:+.3f} m; lid bottom {closed_lo.z:+.3f} vs rim {rim_top:+.3f}")

	# Export.
	out = os.path.join(ASSETS, FBX_OUT_REL.replace("/", os.sep))
	for o in bpy.data.objects:
		o.select_set(o in (body_o, lid_o))
	bpy.context.view_layer.objects.active = body_o
	bpy.ops.export_scene.fbx(filepath=out, use_selection=True, object_types={'MESH'}, apply_unit_scale=True,
							 global_scale=1.0, axis_forward='-Z', axis_up='Y', mesh_smooth_type='FACE',
							 add_leaf_bones=False, bake_anim=False, path_mode='STRIP', embed_textures=False)

	for mesh_name in ("chest_body", "chest_lid"):
		text = f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
// Rumple's chest, {mesh_name} - Blender/scripts/split_chest_lid.py
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
							{{
								from = "chest.vmat"
								to = "{VMAT}"
							}},
						]
						use_global_default = false
						global_default_material = "{VMAT}"
					}},
				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
					{{
						_class = "RenderMeshFile"
						name = "{mesh_name}"
						filename = "{FBX_OUT_REL}"
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
							exception_list = [ "{mesh_name}" ]
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
		with open(os.path.join(ASSETS, "models", "building", f"{mesh_name}.vmdl"), "w", encoding="utf-8", newline="\n") as f:
			f.write(text)

	# Read the written FBX back: the lid must sit in the hinge frame (its bbox straddles the origin).
	bpy.ops.wm.read_factory_settings(use_empty=True)
	bpy.ops.import_scene.fbx(filepath=out)
	for o in bpy.data.objects:
		if o.type == 'MESH':
			lo, hi = bbox(o)
			print(f"[chest] verify {o.name:10s} x {lo.x:+.3f}..{hi.x:+.3f} y {lo.y:+.3f}..{hi.y:+.3f} z {lo.z:+.3f}..{hi.z:+.3f}")
	body_v = [o for o in bpy.data.objects if o.name == "chest_body"][0]
	lid_v = [o for o in bpy.data.objects if o.name == "chest_lid"][0]
	render_closed(body_v, lid_v, hinge, mat)

	u = UNITS_PER_BLENDER_METER
	# Engine axes (measured 2026-11-10 on this export convention): engine X = -Blender Y, engine Y = Blender X, Z = Z.
	ex, ey, ez = -hinge.y * u, hinge.x * u, hinge.z * u
	eaxis = Vector((-axis.y, axis.x, axis.z))
	print(f"[chest] PREFAB Lid LocalPosition (engine units) = {ex:.3f},{ey:.3f},{ez:.3f}  LidMesh LocalPosition = {-ex:.3f},{-ey:.3f},{-ez:.3f}  "
		  f"HingeAxis = {eaxis.x:.0f},{eaxis.y:.0f},{eaxis.z:.0f}  OpenAngleDegrees = {-sign * tilt:.1f}")


main()
