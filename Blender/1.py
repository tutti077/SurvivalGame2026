"""
Basic procedural sword + simple armature for Blender 3.x / 4.x.

How to use:
  1. Open Blender → Scripting workspace → Open this file → Run Script (▶).
  Or CLI:  blender --background --python build_basic_sword.py

The sword is aligned with the blade along +Z (tip upward). Origin sits near the guard.
This script also creates a one-bone armature ("BasicSwordRig", bone "SwordBone") and
binds the mesh to it for export-friendly skeletal animation.
"""

import bpy
from mathutils import Vector


def clear_default_scene():
    # Do not use bpy.ops.object.delete here — it often fails outside a 3D View context (Blender 4+).
    for ob in list(bpy.data.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    for col in list(bpy.data.collections):
        if col.name != "Collection":
            try:
                bpy.data.collections.remove(col)
            except RuntimeError:
                pass


def create_material(name: str, color_rgba):
    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    principled = nodes.get("Principled BSDF")
    if principled:
        principled.inputs["Base Color"].default_value = color_rgba
        principled.inputs["Metallic"].default_value = 0.35
        principled.inputs["Roughness"].default_value = 0.45
    return mat


def new_cube(name: str, location, scale, rotation_euler=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(location=location, scale=scale)
    ob = bpy.context.active_object
    ob.name = ob.data.name = name
    ob.rotation_euler = rotation_euler
    return ob


def new_cylinder(name: str, location, radius, depth, rotation_euler=(0, 0, 0), vertices=16):
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=vertices, radius=radius, depth=depth, location=location
    )
    ob = bpy.context.active_object
    ob.name = ob.data.name = name
    ob.rotation_euler = rotation_euler
    return ob


def new_uvsphere(name: str, location, radius, segments=16, rings=8):
    bpy.ops.mesh.primitive_uv_sphere_add(
        segments=segments, ring_count=rings, radius=radius, location=location
    )
    ob = bpy.context.active_object
    ob.name = ob.data.name = name
    return ob


def add_single_bone_rig(sword):
    arm_data = bpy.data.armatures.new("BasicSwordRigData")
    rig = bpy.data.objects.new("BasicSwordRig", arm_data)
    bpy.context.scene.collection.objects.link(rig)
    rig.location = Vector((0, 0, 0))
    rig.rotation_euler = (0, 0, 0)
    rig.scale = (1, 1, 1)

    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    bone = arm_data.edit_bones.new("SwordBone")
    bone.head = Vector((0, 0, -0.40))
    bone.tail = Vector((0, 0, 0.95))
    bpy.ops.object.mode_set(mode="OBJECT")

    vg = sword.vertex_groups.get("SwordBone")
    if vg is None:
        vg = sword.vertex_groups.new(name="SwordBone")
    vg.add([v.index for v in sword.data.vertices], 1.0, "REPLACE")

    mod = sword.modifiers.get("Armature")
    if mod is None:
        mod = sword.modifiers.new(name="Armature", type="ARMATURE")
    mod.object = rig

    sword.parent = rig
    sword.matrix_parent_inverse = rig.matrix_world.inverted()

    return rig


def build_basic_sword():
    clear_default_scene()

    steel = create_material("SwordSteel", (0.55, 0.58, 0.62, 1.0))
    leather = create_material("GripLeather", (0.18, 0.12, 0.08, 1.0))

    # Tip at +Z; grip extends toward -Z from the guard.
    blade_len = 1.1
    blade_half = blade_len / 2
    blade = new_cube(
        "Blade",
        location=(0, 0, blade_half * 0.85),
        scale=(0.04, 0.012, blade_half),
    )
    blade.data.materials.append(steel)

    guard = new_cube(
        "Guard",
        location=(0, 0, 0),
        scale=(0.18, 0.02, 0.03),
    )
    guard.data.materials.append(steel)

    handle_len = 0.28
    guard_half_z = 0.03
    # Cylinder axis is Z; top of handle meets bottom of guard.
    handle_center_z = -guard_half_z - handle_len / 2
    handle = new_cylinder(
        "Handle",
        location=(0, 0, handle_center_z),
        radius=0.035,
        depth=handle_len,
    )
    handle.data.materials.append(leather)

    pom_bottom_z = handle_center_z - handle_len / 2
    pommel = new_uvsphere(
        "Pommel", location=(0, 0, pom_bottom_z - 0.05), radius=0.045
    )
    pommel.data.materials.append(steel)

    parts = [blade, guard, handle, pommel]
    bpy.ops.object.select_all(action="DESELECT")
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = blade
    bpy.ops.object.join()

    sword = bpy.context.active_object
    sword.name = sword.data.name = "BasicSword"

    # Pivot at the guard (z=0): handy for first-person / swing roots.
    bpy.context.scene.cursor.location = Vector((0, 0, 0))
    bpy.ops.object.origin_set(type="ORIGIN_CURSOR")
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    bpy.ops.object.shade_smooth()

    # Optional bevel (light) on the blade only — skipped for minimal tris.
    rig = add_single_bone_rig(sword)

    return sword, rig


def main():
    sword, rig = build_basic_sword()
    print(
        f"Created mesh: {sword.name}, verts={len(sword.data.vertices)} | "
        f"rig: {rig.name}, bones={len(rig.data.bones)}"
    )

    # Uncomment to auto-export next to this script (change format as you like):
    # out = bpy.path.abspath("//basic_sword.fbx")
    # bpy.ops.export_scene.fbx(filepath=out, use_selection=True)
    # print("Exported:", out)


if __name__ == "__main__":
    main()
