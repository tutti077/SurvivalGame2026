"""Author the one-handed sword clips for the s&box human rig and export them as engine animation FBX.

The rig is the engine's citizen_human_male_REF.fbx; the base body stance comes from the retargeted
right-hand item pose. Poses are lists of operations applied on top of that stance, in WORLD terms so they
read like direction: Blender world has the character facing -Y, its right side at -X, Z up
(engine x,y,z = -bY, bX, bZ).

  ["rot", bone, axis, degrees]      rotate a bone about a world axis ("x" = character's left/right axis,
                                    "y" = forward/back axis, "z" = up) around its own head
  ["ik", hand, [x,y,z], [px,py,pz]] two-bone IK: put the hand bone at a world point, elbow toward the pole
  ["aim", hand, [tx,ty,tz], roll]   turn the hand so the held blade (hold_R local +Z) points along the world
                                    direction, then roll it about the blade by `roll` degrees

A clip is a list of (seconds, pose) keys; poses are interpolated by Blender's Bezier keys. Clips export to
Assets/models/player/animations/human@sword_*.fbx (registered by Tools/make_human_sword_assets.py).

Usage (from the repo root):
  blender -b --python Blender/scripts/create_sword_anims.py -- render <poses.json> <outDir> [view ...]
  blender -b --python Blender/scripts/create_sword_anims.py -- preview <clip> <outDir>    one frame per key
  blender -b --python Blender/scripts/create_sword_anims.py -- export [clip ...]           write FBX clips
"""
import json
import math
import os
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector

ROOT = r"M:/s&box/Projects/SurvivalGame2026"
SBOX = r"M:/SteamLibrary/steamapps/common/sbox/addons/citizen/Assets/models/citizen_human"
REF = SBOX + "/citizen_human_male_REF.fbx"
BASE_CLIP = SBOX + "/animations/retargeted/weapons/human@holditem_rh_pose_standing_01.fbx"
OUT_DIR = ROOT + "/Assets/models/player/animations"
FPS = 30
WORLD_AXES = {"x": Vector((1, 0, 0)), "y": Vector((0, 1, 0)), "z": Vector((0, 0, 1))}
VERBOSE = os.environ.get("SWORD_VERBOSE") == "1"


# ------------------------------------------------------------------------------------------ rig
def import_rig():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=REF, use_anim=False, automatic_bone_orientation=False)
    rig = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    bpy.context.scene.render.fps = FPS
    for pb in rig.pose.bones:
        pb.rotation_mode = "QUATERNION"
    return rig


def import_base_pose(rig, clip, frame=2):
    """The retargeted clip FBX carries its animated pose as the armature REST pose (bases are identity),
    so the pose is transferred by world matrices: each REF bone gets the clip bone's armature-space
    rotation (pelvis keeps the clip's translation too), parents first."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=clip, use_anim=True, automatic_bone_orientation=False)
    new = [o for o in bpy.data.objects if o not in before]
    arm = next(o for o in new if o.type == "ARMATURE")
    bpy.context.scene.frame_set(frame)
    bpy.context.view_layer.update()
    to_ref = rig.matrix_world.inverted() @ arm.matrix_world
    desired = {pb.name: (to_ref @ pb.matrix) for pb in arm.pose.bones}
    for o in new:
        bpy.data.objects.remove(o, do_unlink=True)
    for a in list(bpy.data.actions):
        bpy.data.actions.remove(a)
    base = {}
    ordered = []
    stack = [b for b in rig.pose.bones if b.parent is None]
    while stack:
        b = stack.pop(0)
        ordered.append(b)
        stack.extend(b.children)
    pose_mats = {}
    for pb in ordered:
        d = desired.get(pb.name)
        rest = pb.bone.matrix_local
        if pb.parent is None:
            parent_pose = Matrix.Identity(4)
            rest_local = rest
        else:
            parent_pose = pose_mats[pb.parent.name]
            rest_local = pb.parent.bone.matrix_local.inverted() @ rest
        if d is None:
            basis = Matrix.Identity(4)
        else:
            # rotation from the clip; translation only for the root chain (hip height), else rest length
            target = d.copy()
            if pb.parent is not None:
                target.translation = (parent_pose @ rest_local).translation
            basis = (parent_pose @ rest_local).inverted() @ target
            basis = basis.to_quaternion().to_matrix().to_4x4() if pb.parent is not None else basis
        base[pb.name] = basis
        pose_mats[pb.name] = parent_pose @ rest_local @ basis
    apply_base(rig, base)
    return base


def apply_base(rig, base):
    for pb in rig.pose.bones:
        m = base.get(pb.name)
        pb.matrix_basis = m if m is not None else Matrix.Identity(4)
    bpy.context.view_layer.update()


def add_sword(rig):
    """Stand-in blade on hold_R: 0.95 m along the bone's local +Z (the engine's tip direction), hilt behind.
    The armature carries a 0.01 world scale, so bone-space offsets are in centimetres."""
    parts = []
    for name, scale, local in (("sword_blade", (3.5, 1.2, 95.0), (0, 0, 55.5)),
                               ("sword_guard", (18.0, 2.0, 2.5), (0, 0, 8.0)),
                               ("sword_grip", (2.6, 2.6, 16.0), (0, 0, 0.0))):
        bpy.ops.mesh.primitive_cube_add(size=1.0)
        ob = bpy.context.active_object
        ob.name = name
        con = ob.constraints.new("CHILD_OF")
        con.target = rig
        con.subtarget = "hold_R"
        con.inverse_matrix = Matrix.Identity(4)
        ob.matrix_world = Matrix.Translation(Vector(local)) @ Matrix.Diagonal((*scale, 1.0))
        parts.append(ob)
    return parts


# ------------------------------------------------------------------------------------------ posing
def world_to_arm_dir(rig, v):
    return (rig.matrix_world.inverted().to_3x3() @ Vector(v)).normalized()


def bone_world(rig, name):
    return (rig.matrix_world @ rig.pose.bones[name].matrix).translation


def rotate_bone_world(rig, bone, axis, degrees):
    pb = rig.pose.bones[bone]
    axis_arm = world_to_arm_dir(rig, WORLD_AXES[axis])
    q = Quaternion(axis_arm, math.radians(degrees))
    head = pb.matrix.translation.copy()
    pb.matrix = Matrix.Translation(head) @ q.to_matrix().to_4x4() @ Matrix.Translation(-head) @ pb.matrix
    bpy.context.view_layer.update()


def rotate_bone_about(rig, pb, axis_world, radians, pivot_world):
    """Rotate a pose bone (and its children) about an arbitrary world axis through a world point."""
    inv = rig.matrix_world.inverted()
    axis_arm = (inv.to_3x3() @ Vector(axis_world)).normalized()
    pivot_arm = inv @ Vector(pivot_world)
    q = Quaternion(axis_arm, radians)
    pb.matrix = Matrix.Translation(pivot_arm) @ q.to_matrix().to_4x4() @ Matrix.Translation(-pivot_arm) @ pb.matrix
    bpy.context.view_layer.update()


def solve_arm_ik(rig, hand, target_world, pole_world):
    """Analytic two-bone IK in world space: the hand bone head lands on the target (clamped to reach),
    the elbow bends toward the pole point. Deterministic, no constraints involved."""
    hand_pb = rig.pose.bones[hand]
    lower = hand_pb.parent
    upper = lower.parent
    T = Vector(target_world)
    P = Vector(pole_world) if pole_world is not None else None

    def pos(pb):
        return bone_world(rig, pb.name)

    S, E, H = pos(upper), pos(lower), pos(hand_pb)
    l1, l2 = (E - S).length, (H - E).length
    d = max(abs(l1 - l2) + 0.01, min((T - S).length, l1 + l2 - 0.005))
    # 1. elbow angle by the law of cosines, bending about the current elbow plane normal
    cur_cos = ((S - E).normalized()).dot((H - E).normalized())
    cur = math.acos(max(-1.0, min(1.0, cur_cos)))
    want = math.acos(max(-1.0, min(1.0, (l1 * l1 + l2 * l2 - d * d) / (2 * l1 * l2))))
    plane_n = (H - E).cross(S - E)
    if plane_n.length < 1e-6:
        ref = (P - S) if P is not None else Vector((0, 0, 1))
        plane_n = (T - S).cross(ref)
    plane_n.normalize()
    # positive rotation about (H-E)x(S-E) swings the hand toward the shoulder, i.e. closes the elbow
    rotate_bone_about(rig, lower, plane_n, cur - want, E)
    # 2. aim the whole chain so the hand sits on the shoulder->target line
    S, H = pos(upper), pos(hand_pb)
    q = (H - S).normalized().rotation_difference((T - S).normalized())
    axis, angle = q.axis, q.angle
    if angle > 1e-6:
        rotate_bone_about(rig, upper, axis, angle, S)
    # 3. swivel about the shoulder->target axis so the elbow points at the pole
    if P is not None:
        S, E, H = pos(upper), pos(lower), pos(hand_pb)
        u = (H - S).normalized()
        e = (E - S) - u * (E - S).dot(u)
        p = (P - S) - u * (P - S).dot(u)
        if e.length > 1e-6 and p.length > 1e-6:
            ang = math.atan2(e.cross(p).dot(u), e.dot(p))
            rotate_bone_about(rig, upper, u, ang, S)
    # keep the hand's world orientation as it was before the solve (aim ops set it afterwards)
    bpy.context.view_layer.update()


def aim_hold(rig, hand, tip_dir_world, roll_degrees, hold="hold_R"):
    """Rotate the hand so the hold bone's local +Z points along tip_dir (world), then roll about it."""
    pb = rig.pose.bones[hand]
    hold_pb = rig.pose.bones[hold]
    desired = world_to_arm_dir(rig, tip_dir_world)
    for _ in range(2):
        current = (hold_pb.matrix.to_3x3() @ Vector((0, 0, 1))).normalized()
        q = current.rotation_difference(desired)
        head = pb.matrix.translation.copy()
        pb.matrix = Matrix.Translation(head) @ q.to_matrix().to_4x4() @ Matrix.Translation(-head) @ pb.matrix
        bpy.context.view_layer.update()
    if abs(roll_degrees) > 1e-3:
        q = Quaternion(desired, math.radians(roll_degrees))
        head = pb.matrix.translation.copy()
        pb.matrix = Matrix.Translation(head) @ q.to_matrix().to_4x4() @ Matrix.Translation(-head) @ pb.matrix
        bpy.context.view_layer.update()


def apply_pose(rig, base, ops):
    apply_base(rig, base)
    for op in ops:
        kind = op[0]
        if kind == "rot":
            rotate_bone_world(rig, op[1], op[2], op[3])
        elif kind == "ik":
            solve_arm_ik(rig, op[1], op[2], op[3] if len(op) > 3 else None)
        elif kind == "aim":
            aim_hold(rig, op[1], op[2], op[3] if len(op) > 3 else 0.0)
        else:
            raise ValueError("unknown op " + str(op))
        if VERBOSE:
            print("   ", op[:2], "-> hand_R", tuple(round(v, 3) for v in bone_world(rig, "hand_R")))
    bpy.context.view_layer.update()


# ------------------------------------------------------------------------------------------ clips
def key_pose(rig, frame):
    for pb in rig.pose.bones:
        pb.keyframe_insert("rotation_quaternion", frame=frame)
        pb.keyframe_insert("location", frame=frame)


def build_clip(rig, base, name, keys, loop=False):
    """keys: list of (seconds, ops). Returns the action; the scene frame range is set to the clip."""
    for a in list(bpy.data.actions):
        bpy.data.actions.remove(a)
    rig.animation_data_create()
    action = bpy.data.actions.new(name)
    rig.animation_data.action = action
    slot = action.slots.new(id_type="OBJECT", name=rig.name)
    rig.animation_data.action_slot = slot
    last = 1
    for seconds, ops in keys:
        frame = 1 + int(round(seconds * FPS))
        # frame_set first: it re-evaluates the action and would overwrite a pose applied before it
        bpy.context.scene.frame_set(frame)
        apply_pose(rig, base, ops)
        key_pose(rig, frame)
        last = max(last, frame)
    if last == 1:  # static pose: give it two identical frames so the engine sees a clip
        bpy.context.scene.frame_set(2)
        key_pose(rig, 2)
        last = 2
    bpy.context.scene.frame_start = 1
    bpy.context.scene.frame_end = last
    # Bezier keys with auto-clamped handles read as natural ease in / out between poses.
    cb = action.layers[0].strips[0].channelbag(slot)
    for fc in cb.fcurves:
        for kp in fc.keyframe_points:
            kp.interpolation = "BEZIER"
            kp.handle_left_type = kp.handle_right_type = "AUTO_CLAMPED"
        fc.update()
    return action


def export_clip(rig, fbx_name):
    os.makedirs(OUT_DIR, exist_ok=True)
    path = OUT_DIR + "/" + fbx_name
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"ARMATURE"}, add_leaf_bones=False,
        bake_anim=True, bake_anim_use_all_actions=False, bake_anim_use_nla_strips=False,
        bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0, bake_anim_step=1.0,
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_NONE", bake_space_transform=False,
        axis_forward="-Z", axis_up="Y", use_armature_deform_only=False)
    print("exported", path, os.path.getsize(path))


# ------------------------------------------------------------------------------------------ the sword set
# Hand targets are world metres (facing -Y, right -X, Z up). Shoulder sits near (-0.19, -0.03, 1.37).
def P(*ops):
    return list(ops)


REST_R = P(
    ["rot", "spine_1", "z", 6], ["rot", "spine_2", "z", 6],
    ["ik", "hand_R", [-0.20, -0.14, 1.26], [-0.60, 0.24, 0.92]],
    ["aim", "hand_R", [-0.30, 0.60, 0.74], 0],
    ["rot", "arm_upper_L", "x", -4], ["rot", "arm_lower_L", "x", 6],
)
REST_L = P(
    ["rot", "spine_1", "z", -8], ["rot", "spine_2", "z", -8],
    ["ik", "hand_R", [0.10, -0.16, 1.27], [-0.42, 0.26, 0.86]],
    ["aim", "hand_R", [0.32, 0.62, 0.72], 0],
    ["rot", "arm_upper_L", "x", -4], ["rot", "arm_lower_L", "x", 6],
)
COCKED_R = P(
    ["rot", "spine_1", "z", 20], ["rot", "spine_2", "z", 16],
    ["ik", "hand_R", [-0.30, 0.08, 1.36], [-0.72, 0.36, 0.98]],
    ["aim", "hand_R", [-0.38, 0.58, 0.72], 0],
    ["rot", "arm_upper_L", "y", -18], ["rot", "arm_lower_L", "x", 28],
)
SWING_R_MID = P(
    ["rot", "spine_1", "z", -4], ["rot", "spine_2", "z", -6],
    ["ik", "hand_R", [-0.05, -0.62, 1.18], [-0.55, -0.25, 1.05]],
    ["aim", "hand_R", [0.55, -0.80, 0.05], 0],
    ["rot", "arm_upper_L", "y", -22], ["rot", "arm_lower_L", "x", 26],
)
SWING_R_END = P(
    ["rot", "spine_1", "z", -24], ["rot", "spine_2", "z", -20],
    ["ik", "hand_R", [0.34, -0.42, 1.02], [-0.25, -0.35, 1.2]],
    ["aim", "hand_R", [0.80, -0.45, -0.35], 0],
    ["rot", "arm_upper_L", "y", -18], ["rot", "arm_lower_L", "x", 22],
)
SWING_R_SETTLE = P(
    ["rot", "spine_1", "z", -8], ["rot", "spine_2", "z", -6],
    ["ik", "hand_R", [0.05, -0.38, 1.12], [-0.45, -0.1, 1.0]],
    ["aim", "hand_R", [0.3, -0.3, 0.9], 0],
    ["rot", "arm_upper_L", "x", -6], ["rot", "arm_lower_L", "x", 10],
)

COCKED_L = P(
    ["rot", "spine_1", "z", -20], ["rot", "spine_2", "z", -16],
    ["ik", "hand_R", [0.16, 0.02, 1.34], [-0.28, 0.34, 0.92]],
    ["aim", "hand_R", [0.42, 0.58, 0.70], 0],
    ["rot", "arm_upper_L", "y", -22], ["rot", "arm_lower_L", "x", 30],
)
SWING_L_MID = P(
    ["rot", "spine_1", "z", 4], ["rot", "spine_2", "z", 6],
    ["ik", "hand_R", [-0.10, -0.62, 1.16], [-0.55, -0.2, 1.0]],
    ["aim", "hand_R", [-0.60, -0.78, 0.0], 0],
    ["rot", "arm_upper_L", "y", -22], ["rot", "arm_lower_L", "x", 26],
)
SWING_L_END = P(
    ["rot", "spine_1", "z", 24], ["rot", "spine_2", "z", 20],
    ["ik", "hand_R", [-0.52, -0.28, 1.00], [-0.6, 0.2, 1.25]],
    ["aim", "hand_R", [-0.85, -0.35, -0.35], 0],
    ["rot", "arm_upper_L", "y", -14], ["rot", "arm_lower_L", "x", 18],
)
SWING_L_SETTLE = P(
    ["rot", "spine_1", "z", 8], ["rot", "spine_2", "z", 6],
    ["ik", "hand_R", [-0.30, -0.35, 1.14], [-0.6, 0.0, 1.0]],
    ["aim", "hand_R", [-0.35, -0.25, 0.9], 0],
    ["rot", "arm_upper_L", "x", -6], ["rot", "arm_lower_L", "x", 10],
)

COCKED_OVER = P(
    ["rot", "spine_1", "x", -6], ["rot", "spine_2", "x", -6],
    ["ik", "hand_R", [-0.16, 0.10, 1.72], [-0.7, 0.2, 1.4]],
    ["aim", "hand_R", [-0.05, 0.85, 0.5], 0],
    ["rot", "arm_upper_L", "y", -26], ["rot", "arm_lower_L", "x", 34],
)
CHOP_MID = P(
    ["rot", "spine_1", "x", 8], ["rot", "spine_2", "x", 10],
    ["ik", "hand_R", [-0.12, -0.55, 1.45], [-0.6, -0.1, 1.3]],
    ["aim", "hand_R", [0.0, -0.80, 0.6], 0],
    ["rot", "arm_upper_L", "y", -22], ["rot", "arm_lower_L", "x", 30],
)
CHOP_END = P(
    ["rot", "spine_1", "x", 16], ["rot", "spine_2", "x", 16],
    ["ik", "hand_R", [-0.14, -0.62, 0.92], [-0.55, -0.2, 1.1]],
    ["aim", "hand_R", [0.05, -0.75, -0.65], 0],
    ["rot", "arm_upper_L", "y", -14], ["rot", "arm_lower_L", "x", 22],
)
CHOP_SETTLE = P(
    ["rot", "spine_1", "x", 6], ["rot", "spine_2", "x", 6],
    ["ik", "hand_R", [-0.18, -0.42, 1.05], [-0.55, -0.05, 1.0]],
    ["aim", "hand_R", [0.0, -0.55, 0.83], 0],
    ["rot", "arm_upper_L", "x", -6], ["rot", "arm_lower_L", "x", 10],
)

COCKED_STAB = P(
    ["rot", "spine_1", "z", 20], ["rot", "spine_2", "z", 16],
    ["ik", "hand_R", [-0.30, 0.12, 1.08], [-0.65, 0.4, 1.0]],
    ["aim", "hand_R", [0.1, -1.0, 0.05], 0],
    ["rot", "arm_upper_L", "y", -30], ["rot", "arm_lower_L", "x", 40],
)
STAB_END = P(
    ["rot", "spine_1", "z", -12], ["rot", "spine_2", "z", -12], ["rot", "spine_1", "x", 8],
    ["ik", "hand_R", [-0.08, -0.72, 1.18], [-0.45, -0.3, 1.0]],
    ["aim", "hand_R", [0.05, -1.0, 0.0], 0],
    ["rot", "arm_upper_L", "y", -18], ["rot", "arm_lower_L", "x", 18],
)
STAB_SETTLE = P(
    ["rot", "spine_1", "z", 4], ["rot", "spine_2", "z", 4],
    ["ik", "hand_R", [-0.22, -0.40, 1.12], [-0.6, 0.0, 1.0]],
    ["aim", "hand_R", [-0.1, -0.7, 0.7], 0],
    ["rot", "arm_upper_L", "x", -6], ["rot", "arm_lower_L", "x", 10],
)

# name -> (fbx, loop, keys)
CLIPS = {
    "Sword_Rest_R": ("human@sword_rest_r.fbx", True, [(0.0, REST_R)]),
    "Sword_Rest_L": ("human@sword_rest_l.fbx", True, [(0.0, REST_L)]),
    "Sword_Cocked_R": ("human@sword_cocked_r.fbx", True, [(0.0, COCKED_R)]),
    "Sword_Cocked_L": ("human@sword_cocked_l.fbx", True, [(0.0, COCKED_L)]),
    "Sword_Cocked_Over": ("human@sword_cocked_over.fbx", True, [(0.0, COCKED_OVER)]),
    "Sword_Cocked_Stab": ("human@sword_cocked_stab.fbx", True, [(0.0, COCKED_STAB)]),
    "Sword_Attack_R": ("human@sword_attack_r.fbx", False,
                       [(0.0, COCKED_R), (0.08, SWING_R_MID), (0.17, SWING_R_END), (0.30, SWING_R_SETTLE), (0.53, REST_R)]),
    "Sword_Attack_L": ("human@sword_attack_l.fbx", False,
                       [(0.0, COCKED_L), (0.08, SWING_L_MID), (0.17, SWING_L_END), (0.30, SWING_L_SETTLE), (0.53, REST_L)]),
    "Sword_Attack_Over": ("human@sword_attack_over.fbx", False,
                          [(0.0, COCKED_OVER), (0.08, CHOP_MID), (0.17, CHOP_END), (0.30, CHOP_SETTLE), (0.53, REST_R)]),
    "Sword_Attack_Stab": ("human@sword_attack_stab.fbx", False,
                          [(0.0, COCKED_STAB), (0.12, STAB_END), (0.27, STAB_SETTLE), (0.50, REST_R)]),
}
POSES = {
    "REST_R": REST_R, "REST_L": REST_L, "COCKED_R": COCKED_R, "SWING_R_MID": SWING_R_MID, "SWING_R_END": SWING_R_END,
    "SWING_R_SETTLE": SWING_R_SETTLE, "COCKED_L": COCKED_L, "SWING_L_MID": SWING_L_MID, "SWING_L_END": SWING_L_END,
    "SWING_L_SETTLE": SWING_L_SETTLE, "COCKED_OVER": COCKED_OVER, "CHOP_MID": CHOP_MID, "CHOP_END": CHOP_END,
    "CHOP_SETTLE": CHOP_SETTLE, "COCKED_STAB": COCKED_STAB, "STAB_END": STAB_END, "STAB_SETTLE": STAB_SETTLE,
}


# ------------------------------------------------------------------------------------------ render
VIEWS = {
    "front_right": (Vector((-2.3, -2.5, 1.7)), Vector((0.0, -0.1, 1.1))),
    "front": (Vector((0.0, -3.3, 1.5)), Vector((0.0, 0.0, 1.1))),
    "right": (Vector((-3.3, 0.0, 1.5)), Vector((0.0, 0.0, 1.1))),
    "front_left": (Vector((2.3, -2.5, 1.7)), Vector((0.0, -0.1, 1.1))),
    "top": (Vector((0.0, -0.5, 4.5)), Vector((0.0, -0.5, 1.0))),
}


def setup_render(out_path, view):
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "SINGLE"
    scene.display.shading.single_color = (0.75, 0.7, 0.65)
    scene.display.shading.show_shadows = True
    scene.render.resolution_x = 640
    scene.render.resolution_y = 800
    if scene.camera is None:
        cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
        cam.data.lens = 45
        scene.collection.objects.link(cam)
        scene.camera = cam
    cam = scene.camera
    pos, look = VIEWS[view]
    cam.location = pos
    cam.rotation_euler = (look - pos).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = out_path


def render_poses(spec_path, out_dir, views):
    with open(spec_path, encoding="utf-8") as f:
        spec = json.load(f)
    rig = import_rig()
    base = import_base_pose(rig, BASE_CLIP)
    add_sword(rig)
    os.makedirs(out_dir, exist_ok=True)
    for pose in spec["poses"]:
        ops = POSES[pose["use"]] if "use" in pose else pose["ops"]
        apply_pose(rig, base, ops)
        for view in views:
            setup_render(os.path.join(out_dir, f"{pose['name']}_{view}.png"), view)
            bpy.ops.render.render(write_still=True)
        print(pose["name"], "hand_R", tuple(round(v, 2) for v in bone_world(rig, "hand_R")),
              "hold_R", tuple(round(v, 2) for v in bone_world(rig, "hold_R")))


def preview_clip(clip_name, out_dir, views=("front_right",)):
    rig = import_rig()
    base = import_base_pose(rig, BASE_CLIP)
    add_sword(rig)
    fbx, loop, keys = CLIPS[clip_name]
    build_clip(rig, base, clip_name, keys, loop)
    os.makedirs(out_dir, exist_ok=True)
    for seconds, _ in keys:
        frame = 1 + int(round(seconds * FPS))
        bpy.context.scene.frame_set(frame)
        for view in views:
            setup_render(os.path.join(out_dir, f"{clip_name}_f{frame:02d}_{view}.png"), view)
            bpy.ops.render.render(write_still=True)
    print("previewed", clip_name, "frames", [1 + int(round(s * FPS)) for s, _ in keys])


def export_clips(names):
    rig = import_rig()
    base = import_base_pose(rig, BASE_CLIP)
    for name in names:
        fbx, loop, keys = CLIPS[name]
        build_clip(rig, base, name, keys, loop)
        export_clip(rig, fbx)


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if argv and argv[0] == "render":
        render_poses(argv[1], argv[2], argv[3:] or ["front_right"])
    elif argv and argv[0] == "preview":
        preview_clip(argv[1], argv[2], tuple(argv[3:]) or ("front_right",))
    elif argv and argv[0] == "export":
        export_clips(argv[1:] or list(CLIPS))
    else:
        print(__doc__)
