"""
Shared build + rig + animation + FBX export for the basic sword (s&box pipeline).

Used by:
  - export_left_attack_for_sandbox.py  (build + write FBX into Assets/)
  - sword_left_attack_only.py          (build + keyframes in the open Blender scene only)

Keep animation keyframes identical in ONE place here so rerun/export stays in sync.
"""

from __future__ import annotations

import os
from math import radians

import bpy
from mathutils import Vector

# Timeline (must match baked clip after export).
ATTACK_CLIP_FRAME_START = 1
ATTACK_CLIP_FRAME_END = 40


def clear_scene() -> None:
	for obj in list(bpy.data.objects):
		bpy.data.objects.remove(obj, do_unlink=True)


def create_material(name, color_rgba, metallic=0.35, roughness=0.45):
	mat = bpy.data.materials.new(name=name)
	mat.use_nodes = True
	principled = mat.node_tree.nodes.get("Principled BSDF")
	if principled:
		principled.inputs["Base Color"].default_value = color_rgba
		principled.inputs["Metallic"].default_value = metallic
		principled.inputs["Roughness"].default_value = roughness
	return mat


def build_sword_mesh():
	"""Single merged mesh named BasicSword (matches ModelDoc import_filter)."""
	steel = create_material("SwordSteel", (0.55, 0.58, 0.62, 1.0))
	leather = create_material("GripLeather", (0.18, 0.12, 0.08, 1.0), metallic=0.05, roughness=0.7)

	blade_len = 1.1
	blade_half = blade_len / 2.0
	bpy.ops.mesh.primitive_cube_add(location=(0, 0, blade_half * 0.85), scale=(0.04, 0.012, blade_half))
	blade = bpy.context.active_object
	blade.name = blade.data.name = "BasicSword"
	blade.data.materials.append(steel)

	bpy.ops.mesh.primitive_cube_add(location=(0, 0, 0), scale=(0.18, 0.02, 0.03))
	guard = bpy.context.active_object
	guard.data.materials.append(steel)

	handle_len = 0.28
	guard_half_z = 0.03
	handle_center_z = -guard_half_z - handle_len / 2.0
	bpy.ops.mesh.primitive_cylinder_add(vertices=16, radius=0.035, depth=handle_len, location=(0, 0, handle_center_z))
	handle = bpy.context.active_object
	handle.data.materials.append(leather)

	pom_bottom_z = handle_center_z - handle_len / 2.0
	bpy.ops.mesh.primitive_uv_sphere_add(segments=16, ring_count=8, radius=0.045, location=(0, 0, pom_bottom_z - 0.05))
	pommel = bpy.context.active_object
	pommel.data.materials.append(steel)

	parts = [blade, guard, handle, pommel]
	bpy.ops.object.select_all(action="DESELECT")
	for p in parts:
		p.select_set(True)
	bpy.context.view_layer.objects.active = blade
	bpy.ops.object.join()
	sword = bpy.context.active_object
	sword.name = sword.data.name = "BasicSword"

	bpy.context.scene.cursor.location = Vector((0, 0, 0))
	bpy.ops.object.origin_set(type="ORIGIN_CURSOR")
	bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
	bpy.ops.object.shade_smooth()
	return sword


def create_rig_and_bind(sword):
	arm_data = bpy.data.armatures.new("BasicSwordRigData")
	rig = bpy.data.objects.new("BasicSwordRig", arm_data)
	bpy.context.scene.collection.objects.link(rig)

	bpy.ops.object.select_all(action="DESELECT")
	rig.select_set(True)
	bpy.context.view_layer.objects.active = rig
	bpy.ops.object.mode_set(mode="EDIT")
	bone = arm_data.edit_bones.new("SwordBone")
	bone.head = Vector((0, 0, -0.40))
	bone.tail = Vector((0, 0, 0.95))
	bpy.ops.object.mode_set(mode="OBJECT")

	vg = sword.vertex_groups.new(name="SwordBone")
	vg.add([v.index for v in sword.data.vertices], 1.0, "REPLACE")
	mod = sword.modifiers.new(name="Armature", type="ARMATURE")
	mod.object = rig
	sword.parent = rig
	sword.matrix_parent_inverse = rig.matrix_world.inverted()
	return rig


def animate_left_attack(rig):
	"""
	Keyed clip: SwordAtk_Swing_Left (internal Blender action name).
	In s&box the sequence comes from AnimFile.name = basic_sword_attack_left (modeldoc).
	+X euler on articulated frames pushes the swipe away from the camera after FBX axis conversion.
	"""
	if rig.animation_data:
		rig.animation_data_clear()

	action = bpy.data.actions.new("SwordAtk_Swing_Left")
	rig.animation_data_create()
	rig.animation_data.action = action

	pb = rig.pose.bones["SwordBone"]
	pb.rotation_mode = "XYZ"
	pb.location = Vector((0, 0, 0))

	poses = [
		(1, (0.0, 0.0, 0.0)),
		(10, (radians(90.0), 0.0, 0.0)),
		(18, (radians(90.0), radians(-30.0), 0.0)),
		(28, (radians(90.0), radians(90.0), 0.0)),
		(40, (0.0, 0.0, 0.0)),
	]

	for frame, rot in poses:
		bpy.context.scene.frame_set(frame)
		pb.rotation_euler = rot
		pb.keyframe_insert(data_path="rotation_euler", frame=frame)
		pb.keyframe_insert(data_path="location", frame=frame)

	fcurves = getattr(action, "fcurves", None)
	if fcurves is not None:
		for fc in fcurves:
			for kp in fc.keyframe_points:
				kp.interpolation = "BEZIER"

	bpy.context.scene.frame_start = ATTACK_CLIP_FRAME_START
	bpy.context.scene.frame_end = ATTACK_CLIP_FRAME_END
	bpy.context.scene.frame_set(ATTACK_CLIP_FRAME_START)
	return action


def select_export_objects(mesh_obj, rig_obj):
	bpy.ops.object.select_all(action="DESELECT")
	rig_obj.select_set(True)
	mesh_obj.select_set(True)
	bpy.context.view_layer.objects.active = rig_obj


def export_bind_pose_fbx(filepath: str) -> None:
	bpy.ops.export_scene.fbx(
		filepath=filepath,
		use_selection=True,
		object_types={"ARMATURE", "MESH"},
		use_mesh_modifiers=True,
		add_leaf_bones=False,
		use_armature_deform_only=True,
		bake_anim=False,
		axis_forward="-Z",
		axis_up="Y",
		apply_unit_scale=True,
		apply_scale_options="FBX_SCALE_ALL",
	)


def export_attack_clip_fbx(filepath: str, rig, action) -> None:
	if rig.animation_data is None:
		rig.animation_data_create()
	rig.animation_data.action = action

	start = int(action.frame_range[0])
	end = int(action.frame_range[1])
	bpy.context.scene.frame_start = start
	bpy.context.scene.frame_end = end
	bpy.context.scene.frame_set(start)

	bpy.ops.export_scene.fbx(
		filepath=filepath,
		use_selection=True,
		object_types={"ARMATURE", "MESH"},
		use_mesh_modifiers=True,
		add_leaf_bones=False,
		use_armature_deform_only=True,
		bake_anim=True,
		bake_anim_use_nla_strips=False,
		bake_anim_use_all_actions=False,
		bake_anim_force_startend_keying=True,
		bake_anim_step=1.0,
		bake_anim_simplify_factor=0.0,
		axis_forward="-Z",
		axis_up="Y",
		apply_unit_scale=True,
		apply_scale_options="FBX_SCALE_ALL",
	)


def build_sword_scene():
	"""Clears scene, builds mesh + rig + action. Returns (sword, rig, action)."""
	clear_scene()
	sword = build_sword_mesh()
	rig = create_rig_and_bind(sword)
	action = animate_left_attack(rig)
	return sword, rig, action


def write_pipeline_note(out_dir: str) -> None:
	"""Reminder for s&box after FBX regenerate."""
	os.makedirs(out_dir, exist_ok=True)
	path = os.path.join(out_dir, "AFTER_EXPORT_sbox_steps.txt")
	lines = [
		"Exported FBXs are wired to basic_sword_bind.vmdl in this repo.",
		"",
		"1) Open the game project in s&box editor.",
		"2) Compile / refresh compiled model: Assets/models/sword_left/basic_sword_bind.vmdl",
		"   - Mesh file: basic_sword_bind.fbx",
		"   - AnimFile name (sequence): basic_sword_attack_left",
		"   - Anim source file: basic_sword_attack_left.fbx",
		"3) Prefab swords (e.g. Assets/prefabs/sword1.prefab): SkinnedModelRenderer model = basic_sword_bind.vmdl",
		"   MeleeWeapon.SwingSequenceName = basic_sword_attack_left",
		"",
		f"out_dir={os.path.abspath(out_dir)}",
		"",
	]
	with open(path, "w", encoding="utf-8") as f:
		f.write("\n".join(lines))


def export_to_assets_models_sword_left(project_root: str) -> tuple[str, str, str]:
	"""
	Writes bind + clip FBX. Returns (out_dir, bind_fbx, clip_fbx).
	"""
	out_dir = os.path.join(os.path.abspath(project_root), "Assets", "models", "sword_left")
	os.makedirs(out_dir, exist_ok=True)

	sword, rig, action = build_sword_scene()
	select_export_objects(sword, rig)

	bind_fbx = os.path.join(out_dir, "basic_sword_bind.fbx")
	clip_fbx = os.path.join(out_dir, "basic_sword_attack_left.fbx")

	export_bind_pose_fbx(bind_fbx)
	export_attack_clip_fbx(clip_fbx, rig, action)

	marker = os.path.join(out_dir, "EXPORT_HERE.txt")
	with open(marker, "w", encoding="utf-8") as f:
		f.write("export_left_attack_for_sandbox.py (or sword_export_core) wrote FBXs here.\n")
		f.write(f"out_dir={out_dir}\n")

	write_pipeline_note(out_dir)
	return out_dir, bind_fbx, clip_fbx
