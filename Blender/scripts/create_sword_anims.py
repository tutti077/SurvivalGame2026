"""Author the one-handed sword clips for the s&box human rig and export them as engine animation FBX.

The rig is the engine's citizen_human_male_REF.fbx; the base body stance (finger curl, legs, hips) comes
from the retargeted right-hand item pose. Each pose is a list of operations in WORLD metres (Blender world:
the character faces -Y, its right side is -X, Z up; engine x,y,z = -bY, bX, bZ):

  ["twist", deg]                     turn the shoulders about the spine (+ = toward the character's LEFT),
                                     spread over spine_0..2; the head is re-aimed forward afterwards
  ["lean", deg]                      bend the spine forward (+) or back (-)
  ["rot", bone, axis, deg]           rotate one bone about a world axis ("x", "y", "z") around its head
  ["grip", [fist], [blade], [elbow]] right arm: put the FIST (hold_R) at a point with the blade pointing along
                                     a direction; the elbow placement is searched so the wrist stays inside
                                     human limits (natural hammer grip, pronation and flex budgets), the
                                     elbow leaning toward the hint direction
  ["left", [hand], [pole]]           left arm: two-bone reach for the left hand, elbow toward the pole point

The wrist is never set free-hand: the hand's orientation always comes from the measured grip of the
engine's own right-hand item pose, so the sword sits in a closed fist with the blade at the grip angle.
IK target bones are synced to the hands so graph IK never pulls a hand away from its clip.

A clip is a list of (seconds, pose) keys with Bezier easing. Clips export to
Assets/models/player/animations/human@sword_*.fbx (registered by Tools/make_human_sword_assets.py).

Usage (from the repo root):
  blender -b --python Blender/scripts/create_sword_anims.py -- render  <outDir> [POSE ...] [--views a,b]
  blender -b --python Blender/scripts/create_sword_anims.py -- sheet   <outDir> <clip> [--views a,b] [--step n]
  blender -b --python Blender/scripts/create_sword_anims.py -- export  [clip ...]
"""
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

# Wrist budgets for the grip search (degrees beyond the item pose's own wrist).
PRONATION_FREE = 45.0      # forearm twist that costs nothing
SWING_FREE = 20.0          # flex / deviation that costs nothing
SHOULDER_ROLL_FREE = 45.0  # upper-arm roll about its own axis (internal / external rotation) that costs nothing


# ------------------------------------------------------------------------------------------ rig
def import_rig():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=REF, use_anim=False, automatic_bone_orientation=False)
    rig = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    bpy.context.scene.render.fps = FPS
    for pb in rig.pose.bones:
        pb.rotation_mode = "QUATERNION"
    return rig


def apply_base(rig, base):
    for pb in rig.pose.bones:
        m = base.get(pb.name)
        pb.matrix_basis = m if m is not None else Matrix.Identity(4)
    bpy.context.view_layer.update()


def import_base_pose(rig, clip, frame=2):
    """The retargeted clip FBX carries its pose as the armature REST pose (bases are identity), so the pose
    is transferred by armature-space matrices: rotations from the clip, rest lengths kept, parents first."""
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
    base, pose_mats, ordered = {}, {}, []
    stack = [b for b in rig.pose.bones if b.parent is None]
    while stack:
        b = stack.pop(0)
        ordered.append(b)
        stack.extend(b.children)
    for pb in ordered:
        d = desired.get(pb.name)
        rest = pb.bone.matrix_local
        if pb.parent is None:
            parent_pose, rest_local = Matrix.Identity(4), rest
        else:
            parent_pose = pose_mats[pb.parent.name]
            rest_local = pb.parent.bone.matrix_local.inverted() @ rest
        if d is None:
            basis = Matrix.Identity(4)
        else:
            target = d.copy()
            if pb.parent is not None:
                target.translation = (parent_pose @ rest_local).translation
            basis = (parent_pose @ rest_local).inverted() @ target
            if pb.parent is not None:
                basis = basis.to_quaternion().to_matrix().to_4x4()
        base[pb.name] = basis
        pose_mats[pb.name] = parent_pose @ rest_local @ basis
    apply_base(rig, base)
    return base


# ------------------------------------------------------------------------------------------ helpers
def mw3(rig):
    """Rotation part of the armature object's world matrix (it also carries a 0.01 scale)."""
    return rig.matrix_world.to_3x3().normalized()


def to_arm_point(rig, p):
    return rig.matrix_world.inverted() @ Vector(p)


def to_arm_dir(rig, v):
    return (mw3(rig).inverted() @ Vector(v)).normalized()


def bone_world(rig, name):
    return (rig.matrix_world @ rig.pose.bones[name].matrix).translation


def rot3(pb):
    return pb.matrix.to_3x3().normalized()


def set_rot(rig, pb, r3):
    """Set a pose bone's armature-space rotation, keeping its head where its parent puts it."""
    head = pb.matrix.translation.copy()
    m = r3.to_4x4()
    m.translation = head
    pb.matrix = m
    bpy.context.view_layer.update()


def frame_of(a, n):
    """Orthonormal frame (columns) from a primary axis and a secondary hint."""
    x = a.normalized()
    y = (n - x * n.dot(x))
    if y.length < 1e-6:
        y = x.orthogonal()
    y.normalize()
    z = x.cross(y)
    return Matrix((x, y, z)).transposed()


def rotate_about(rig, pb, axis_world, radians, pivot_world):
    inv = rig.matrix_world.inverted()
    axis = (inv.to_3x3() @ Vector(axis_world)).normalized()
    pivot = inv @ Vector(pivot_world)
    q = Quaternion(axis, radians)
    pb.matrix = Matrix.Translation(pivot) @ q.to_matrix().to_4x4() @ Matrix.Translation(-pivot) @ pb.matrix
    bpy.context.view_layer.update()


def swing_twist_deg(q, axis):
    """Decompose quaternion q into a twist about `axis` and the remaining swing; return both in degrees."""
    a = axis.normalized()
    p = Vector((q.x, q.y, q.z)).dot(a)
    twist = Quaternion((q.w, a.x * p, a.y * p, a.z * p))
    if twist.magnitude < 1e-8:
        twist = Quaternion()
    twist.normalize()
    swing = q @ twist.inverted()
    tw = math.degrees(2.0 * math.atan2(p, q.w))
    tw = (tw + 180.0) % 360.0 - 180.0
    sw = math.degrees(swing.angle)
    if sw > 180.0:
        sw = 360.0 - sw
    return tw, sw


# ------------------------------------------------------------------------------------------ grip model
class Grip:
    """Measured once from the base (right-hand item) pose: arm lengths, the hand's grip frame and the
    elbow hinge axes. Everything in armature space (centimetres)."""

    def __init__(self, rig):
        pb = rig.pose.bones
        self.up, self.lo, self.hand, self.hold = pb["arm_upper_R"], pb["arm_lower_R"], pb["hand_R"], pb["hold_R"]
        S, E, H = self.up.matrix.translation, self.lo.matrix.translation, self.hand.matrix.translation
        G = self.hold.matrix.translation
        self.l1, self.l2 = (E - S).length, (H - E).length
        Ru, Rl, Rh = rot3(self.up), rot3(self.lo), rot3(self.hand)
        F = (H - E).normalized()
        B = (rot3(self.hold) @ Vector((0, 0, 1))).normalized()
        n = (E - S).cross(H - E).normalized()
        self.F0, self.B0 = Rh.inverted() @ F, Rh.inverted() @ B
        self.off = Rh.inverted() @ (G - H)                      # fist (hold_R) offset from the wrist, hand-local
        self.grip_deg = math.degrees(self.F0.angle(self.B0))
        self.Lh = frame_of(self.B0, self.F0)                     # hand-local grip frame
        self.au, self.nu = Ru.inverted() @ (E - S).normalized(), Ru.inverted() @ n
        self.al, self.nl = Rl.inverted() @ F, Rl.inverted() @ n
        self.Q0 = (Rl.inverted() @ Rh).to_quaternion()           # forearm->hand rotation of the item pose
        self.F0_in_lo = Rl.inverted() @ F                        # forearm axis in lower-arm local
        self.clav = pb["clavicle_R"]
        self.C0 = (rot3(self.clav).inverted() @ Ru).to_quaternion()  # clavicle->upper-arm rotation of the item pose


def hand_rot_for(grip, F, B):
    """Hand rotation (armature space) whose grip frame matches blade B with the forearm toward F."""
    return frame_of(B, F) @ grip.Lh.transposed()


def solve_grip(rig, grip, fist_world, blade_world, elbow_hint_world):
    P = to_arm_point(rig, fist_world)
    B = to_arm_dir(rig, blade_world)
    hint = to_arm_dir(rig, elbow_hint_world)
    S = grip.up.matrix.translation.copy()
    Rc_inv = rot3(grip.clav).inverted()
    U = B.orthogonal().normalized()
    V = B.cross(U).normalized()
    best = None
    for dd in range(-50, 36, 5):
        th = math.radians(grip.grip_deg + dd)
        for az in range(0, 360, 4):
            a = math.radians(az)
            F = (B * math.cos(th) + (U * math.cos(a) + V * math.sin(a)) * math.sin(th)).normalized()
            Rh = hand_rot_for(grip, F, B)
            H = P - Rh @ grip.off
            E = H - F * grip.l2
            reach = abs((E - S).length - grip.l1)
            line = (H - S)
            if line.length < 1e-6:
                continue
            u = line.normalized()
            e_perp = (E - S) - u * (E - S).dot(u)
            hint_score = e_perp.normalized().dot(hint) if e_perp.length > 1e-6 else 0.0
            n = (E - S).cross(H - E)
            if n.length < 1e-6:
                continue
            Rl = frame_of(F, n) @ frame_of(grip.al, grip.nl).transposed()
            Q = (Rl.inverted() @ Rh).to_quaternion()
            D = grip.Q0.inverted() @ Q
            tw, sw = swing_twist_deg(D, grip.F0_in_lo)
            Ru = frame_of(E - S, n) @ frame_of(grip.au, grip.nu).transposed()
            Du = grip.C0.inverted() @ (Rc_inv @ Ru).to_quaternion()
            roll, _ = swing_twist_deg(Du, grip.au)
            # elbow must bend the right way: forearm folds toward the inside of the arm, never hyper-extends
            bend = (E - S).normalized().dot(F)
            score = (reach * 1.5
                     + max(0.0, abs(tw) - PRONATION_FREE) * 0.08
                     + max(0.0, abs(tw) - 80.0) * 0.3
                     + max(0.0, abs(roll) - SHOULDER_ROLL_FREE) * 0.12
                     + max(0.0, sw - SWING_FREE) * 0.08
                     + abs(dd) * 0.01
                     - hint_score * 2.5
                     + (3.0 if bend < -0.2 else 0.0))
            if best is None or score < best[0]:
                best = (score, F, Rh, E, H, n, tw, sw, reach, dd, roll)
    score, F, Rh, E, H, n, tw, sw, reach, dd, roll = best
    # Rigid lengths: put the elbow on the upper-arm sphere, the wrist one forearm further along F.
    E2 = S + (E - S).normalized() * grip.l1
    H2 = E2 + F * grip.l2
    n2 = (E2 - S).cross(H2 - E2).normalized()
    set_rot(rig, grip.up, frame_of(E2 - S, n2) @ frame_of(grip.au, grip.nu).transposed())
    set_rot(rig, grip.lo, frame_of(H2 - E2, n2) @ frame_of(grip.al, grip.nl).transposed())
    set_rot(rig, grip.hand, Rh)
    if VERBOSE:
        fist = bone_world(rig, "hold_R")
        print(f"    grip: reach err {reach:.1f}cm twist {tw:.0f} swing {sw:.0f} dd {dd} roll {roll:.0f} "
              f"fist {tuple(round(v, 2) for v in fist)} want {tuple(round(v, 2) for v in fist_world)}")


def solve_left(rig, target_world, pole_world):
    """Two-bone reach for the left hand: the hand keeps its item-pose orientation relative to the forearm."""
    pb = rig.pose.bones
    hand, lower, upper = pb["hand_L"], pb["arm_lower_L"], pb["arm_upper_L"]
    T, Pl = Vector(target_world), Vector(pole_world)
    S, E, H = bone_world(rig, upper.name), bone_world(rig, lower.name), bone_world(rig, hand.name)
    l1, l2 = (E - S).length, (H - E).length
    d = max(abs(l1 - l2) + 0.01, min((T - S).length, l1 + l2 - 0.005))
    cur = math.acos(max(-1.0, min(1.0, (S - E).normalized().dot((H - E).normalized()))))
    want = math.acos(max(-1.0, min(1.0, (l1 * l1 + l2 * l2 - d * d) / (2 * l1 * l2))))
    n = (H - E).cross(S - E)
    if n.length < 1e-6:
        n = (T - S).cross(Pl - S)
    rotate_about(rig, lower, n.normalized(), cur - want, E)
    S, H = bone_world(rig, upper.name), bone_world(rig, hand.name)
    q = (H - S).normalized().rotation_difference((T - S).normalized())
    if q.angle > 1e-6:
        rotate_about(rig, upper, q.axis, q.angle, S)
    S, E, H = bone_world(rig, upper.name), bone_world(rig, lower.name), bone_world(rig, hand.name)
    u = (H - S).normalized()
    e = (E - S) - u * (E - S).dot(u)
    p = (Pl - S) - u * (Pl - S).dot(u)
    if e.length > 1e-6 and p.length > 1e-6:
        rotate_about(rig, upper, u, math.atan2(e.cross(p).dot(u), e.dot(p)), S)


SPINE = (("spine_0", 0.30), ("spine_1", 0.35), ("spine_2", 0.35))


def keep_head(rig, base_head):
    """Re-aim neck and head to their base world orientation so the eyes stay on the target while the torso
    twists (the graph's aim layer does the rest in game)."""
    pb = rig.pose.bones
    for name in ("neck_0", "head"):
        set_rot(rig, pb[name], base_head[name])


def sync_ik_targets(rig):
    pb = rig.pose.bones
    for side in ("R", "L"):
        tgt = pb.get(f"hand_{side}_IK_target")
        if tgt is not None:
            tgt.matrix = pb[f"hand_{side}"].matrix.copy()
            bpy.context.view_layer.update()


def apply_pose(rig, base, grip, base_head, ops):
    apply_base(rig, base)
    pb = rig.pose.bones
    torso_done = False
    for op in ops:
        kind = op[0]
        if kind in ("grip", "left") and not torso_done:
            keep_head(rig, base_head)
            torso_done = True
        if kind == "twist":
            for bone, w in SPINE:
                rotate_about(rig, pb[bone], WORLD_AXES["z"], math.radians(op[1] * w), bone_world(rig, bone))
        elif kind == "lean":
            for bone, w in SPINE:
                rotate_about(rig, pb[bone], WORLD_AXES["x"], math.radians(op[1] * w), bone_world(rig, bone))
        elif kind == "rot":
            rotate_about(rig, pb[op[1]], WORLD_AXES[op[2]], math.radians(op[3]), bone_world(rig, op[1]))
        elif kind == "grip":
            solve_grip(rig, grip, op[1], op[2], op[3])
        elif kind == "left":
            solve_left(rig, op[1], op[2])
        else:
            raise ValueError("unknown op " + str(op))
    if not torso_done:
        keep_head(rig, base_head)
    sync_ik_targets(rig)


def setup():
    rig = import_rig()
    base = import_base_pose(rig, BASE_CLIP)
    grip = Grip(rig)
    base_head = {n: rot3(rig.pose.bones[n]).copy() for n in ("neck_0", "head")}
    if VERBOSE:
        print(f"grip: l1 {grip.l1:.1f} l2 {grip.l2:.1f} grip angle {grip.grip_deg:.0f} "
              f"shoulder {tuple(round(v, 3) for v in bone_world(rig, 'arm_upper_R'))}")
    return rig, base, grip, base_head


# ------------------------------------------------------------------------------------------ clips
def key_pose(rig, frame):
    for pb in rig.pose.bones:
        pb.keyframe_insert("rotation_quaternion", frame=frame)
        pb.keyframe_insert("location", frame=frame)


def build_clip(ctx, name, keys):
    rig, base, grip, base_head = ctx
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
        bpy.context.scene.frame_set(frame)          # frame_set first, it re-evaluates the action
        apply_pose(rig, base, grip, base_head, ops)
        key_pose(rig, frame)
        last = max(last, frame)
    if last == 1:
        bpy.context.scene.frame_set(2)
        key_pose(rig, 2)
        last = 2
    bpy.context.scene.frame_start = 1
    bpy.context.scene.frame_end = last
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
# World metres: facing -Y, right -X, up Z. Right shoulder joint ~(-0.19, -0.03, 1.37); arm reach to the
# fist ~0.6 m. Fist = the hold_R point inside the closed hand.
LEFT_GUARD = ["left", [0.23, -0.08, 0.97], [0.45, 0.45, 1.15]]      # relaxed at the side, slightly forward
LEFT_BALANCE = ["left", [0.20, -0.30, 1.08], [0.55, 0.05, 0.80]]    # forward for balance during a cut

REST_R = [  # sword resting back over the right shoulder
    ["lean", 3],
    ["grip", [-0.19, -0.16, 1.28], [-0.06, 0.75, 0.66], [-0.15, 0.1, -1.0]],
    LEFT_GUARD,
]
REST_L = [  # sword crossed to rest back over the left shoulder
    ["twist", 8], ["lean", 3],
    ["grip", [0.04, -0.25, 1.18], [0.28, 0.66, 0.70], [-0.35, -0.35, -1.0]],
    ["left", [0.24, -0.06, 0.97], [0.45, 0.45, 1.15]],
]
COCKED_R = [  # wound up over the right shoulder, shoulders turned right
    ["twist", -30], ["lean", 2],
    ["grip", [-0.30, 0.04, 1.44], [-0.25, 0.55, 0.80], [-1.0, 0.3, -0.3]],
    LEFT_BALANCE,
]
SWING_R_MID = [  # contact in front, travelling right -> left, edge leading
    ["twist", 2], ["lean", 8],
    ["grip", [-0.10, -0.48, 1.24], [0.45, -0.88, -0.15], [-0.4, 0.1, -1.0]],
    LEFT_BALANCE,
]
SWING_R_END = [  # follow-through low on the left
    ["twist", 30], ["lean", 12],
    ["grip", [0.22, -0.42, 1.00], [0.78, 0.15, -0.60], [-0.3, -0.6, -0.8]],
    ["left", [0.30, -0.05, 1.00], [0.70, 0.30, 0.90]],
]
SWING_R_SETTLE = [
    ["twist", 12], ["lean", 6],
    ["grip", [-0.10, -0.30, 1.14], [-0.15, 0.40, 0.90], [-0.4, 0.1, -1.0]],
    LEFT_GUARD,
]

COCKED_L = [  # backhand wind-up: forearm across the chest, sword over the left shoulder
    ["twist", 32], ["lean", 2],
    ["grip", [0.18, -0.06, 1.36], [0.45, 0.50, 0.74], [-0.55, -0.55, -0.6]],
    ["left", [0.30, 0.02, 1.02], [0.70, 0.30, 0.90]],
]
SWING_L_MID = [  # contact in front, travelling left -> right
    ["twist", -2], ["lean", 8],
    ["grip", [-0.12, -0.50, 1.22], [-0.45, -0.88, -0.12], [-0.3, 0.3, -1.0]],
    LEFT_BALANCE,
]
SWING_L_END = [  # follow-through low on the right
    ["twist", -30], ["lean", 10],
    ["grip", [-0.42, -0.24, 1.02], [-0.80, 0.30, -0.52], [-0.3, 0.6, -0.8]],
    LEFT_BALANCE,
]
SWING_L_SETTLE = [
    ["twist", -8], ["lean", 6],
    ["grip", [-0.04, -0.30, 1.14], [0.25, 0.40, 0.88], [-0.4, -0.1, -1.0]],
    LEFT_GUARD,
]

COCKED_OVER = [  # sword raised over the head, blade back
    ["twist", -12], ["lean", -6],
    ["grip", [-0.16, -0.02, 1.64], [0.0, 0.70, 0.70], [-1.0, 0.0, 0.2]],
    LEFT_BALANCE,
]
CHOP_MID = [
    ["twist", -2], ["lean", 12],
    ["grip", [-0.12, -0.50, 1.38], [0.02, -0.72, 0.70], [-0.8, 0.2, -0.6]],
    LEFT_BALANCE,
]
CHOP_END = [
    ["twist", 6], ["lean", 22],
    ["grip", [-0.12, -0.46, 0.98], [0.02, -0.55, -0.83], [-0.4, 0.4, -0.9]],
    ["left", [0.20, -0.18, 0.98], [0.60, 0.20, 0.85]],
]
CHOP_SETTLE = [
    ["twist", 0], ["lean", 8],
    ["grip", [-0.16, -0.30, 1.14], [-0.12, 0.40, 0.91], [-0.4, 0.1, -1.0]],
    LEFT_GUARD,
]

COCKED_STAB = [  # fist pulled back to the ribs, point forward
    ["twist", -26], ["lean", 4],
    ["grip", [-0.24, -0.12, 1.00], [0.05, -1.0, 0.18], [-0.3, 0.8, -0.5]],
    LEFT_BALANCE,
]
STAB_END = [  # full extension
    ["twist", 14], ["lean", 12],
    ["grip", [-0.12, -0.56, 1.22], [0.04, -1.0, 0.0], [-0.6, 0.0, -1.0]],
    ["left", [0.26, -0.02, 1.02], [0.70, 0.30, 0.90]],
]
STAB_SETTLE = [
    ["twist", 0], ["lean", 6],
    ["grip", [-0.18, -0.32, 1.14], [-0.12, 0.30, 0.95], [-0.4, 0.1, -1.0]],
    LEFT_GUARD,
]

# Strike timing (0.55 s): cocked 0 -> contact 0.07 -> follow-through 0.15 -> settle 0.30 -> rest 0.55.
# The host's active window opens at strike start and lasts ~0.1 s, so contact lands inside it.
CLIPS = {
    "Sword_Rest_R": ("human@sword_rest_r.fbx", [(0.0, REST_R)]),
    "Sword_Rest_L": ("human@sword_rest_l.fbx", [(0.0, REST_L)]),
    "Sword_Cocked_R": ("human@sword_cocked_r.fbx", [(0.0, COCKED_R)]),
    "Sword_Cocked_L": ("human@sword_cocked_l.fbx", [(0.0, COCKED_L)]),
    "Sword_Cocked_Over": ("human@sword_cocked_over.fbx", [(0.0, COCKED_OVER)]),
    "Sword_Cocked_Stab": ("human@sword_cocked_stab.fbx", [(0.0, COCKED_STAB)]),
    "Sword_Attack_R": ("human@sword_attack_r.fbx",
                       [(0.0, COCKED_R), (0.07, SWING_R_MID), (0.15, SWING_R_END), (0.30, SWING_R_SETTLE), (0.55, REST_R)]),
    "Sword_Attack_L": ("human@sword_attack_l.fbx",
                       [(0.0, COCKED_L), (0.07, SWING_L_MID), (0.15, SWING_L_END), (0.30, SWING_L_SETTLE), (0.55, REST_L)]),
    "Sword_Attack_Over": ("human@sword_attack_over.fbx",
                          [(0.0, COCKED_OVER), (0.07, CHOP_MID), (0.15, CHOP_END), (0.30, CHOP_SETTLE), (0.55, REST_R)]),
    "Sword_Attack_Stab": ("human@sword_attack_stab.fbx",
                          [(0.0, COCKED_STAB), (0.10, STAB_END), (0.28, STAB_SETTLE), (0.55, REST_R)]),
}
POSES = {k: v for k, v in dict(globals()).items()
         if k.isupper() and isinstance(v, list) and v and isinstance(v[0], list)}


# ------------------------------------------------------------------------------------------ preview
VIEWS = {
    "front": (Vector((0.0, -3.0, 1.45)), Vector((0.0, -0.1, 1.15))),
    "front_right": (Vector((-2.1, -2.2, 1.6)), Vector((0.0, -0.1, 1.15))),
    "front_left": (Vector((2.1, -2.2, 1.6)), Vector((0.0, -0.1, 1.15))),
    "right": (Vector((-3.0, 0.0, 1.45)), Vector((0.0, -0.1, 1.15))),
    "back": (Vector((0.0, 3.0, 1.6)), Vector((0.0, 0.0, 1.15))),
    "top": (Vector((0.0, -0.4, 4.3)), Vector((0.0, -0.4, 1.0))),
}


def add_sword(rig):
    """Stand-in blade on hold_R: 0.85 m blade along local +Z (engine tip axis), guard, grip through the fist.
    Bone space is centimetres (the armature object carries a 0.01 scale)."""
    for name, scale, local in (("sword_blade", (4.0, 0.8, 85.0), (0, 0, 50.0)),
                               ("sword_guard", (16.0, 2.5, 2.5), (0, 0, 7.0)),
                               ("sword_grip", (2.8, 2.8, 16.0), (0, 0, 0.0))):
        bpy.ops.mesh.primitive_cube_add(size=1.0)
        ob = bpy.context.active_object
        ob.name = name
        con = ob.constraints.new("CHILD_OF")
        con.target = rig
        con.subtarget = "hold_R"
        con.inverse_matrix = Matrix.Identity(4)
        ob.matrix_world = Matrix.Translation(Vector(local)) @ Matrix.Diagonal((*scale, 1.0))


def setup_render(out_path, view, res=(400, 500)):
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "SINGLE"
    scene.display.shading.single_color = (0.75, 0.7, 0.65)
    scene.display.shading.show_shadows = True
    scene.render.resolution_x, scene.render.resolution_y = res
    if scene.camera is None:
        cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
        cam.data.lens = 45
        scene.collection.objects.link(cam)
        scene.camera = cam
    pos, look = VIEWS[view]
    scene.camera.location = pos
    scene.camera.rotation_euler = (look - pos).to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = out_path


def parse_flag(argv, flag, default):
    if flag in argv:
        i = argv.index(flag)
        value = argv[i + 1]
        del argv[i:i + 2]
        return value
    return default


def render_poses(out_dir, names, views):
    ctx = setup()
    rig, base, grip, base_head = ctx
    add_sword(rig)
    os.makedirs(out_dir, exist_ok=True)
    for name in names:
        if VERBOSE:
            print("pose", name)
        apply_pose(rig, base, grip, base_head, POSES[name])
        for view in views:
            setup_render(os.path.join(out_dir, f"{name}__{view}.png"), view)
            bpy.ops.render.render(write_still=True)


def sheet_clip(out_dir, clip, views, step):
    ctx = setup()
    add_sword(ctx[0])
    fbx, keys = CLIPS[clip]
    build_clip(ctx, clip, keys)
    os.makedirs(out_dir, exist_ok=True)
    for f in range(1, bpy.context.scene.frame_end + 1, step):
        bpy.context.scene.frame_set(f)
        for view in views:
            setup_render(os.path.join(out_dir, f"{clip}__f{f:02d}__{view}.png"), view, res=(320, 400))
            bpy.ops.render.render(write_still=True)


def export_clips(names):
    ctx = setup()
    for name in names:
        fbx, keys = CLIPS[name]
        build_clip(ctx, name, keys)
        export_clip(ctx[0], fbx)


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if argv and argv[0] == "render":
        views = parse_flag(argv, "--views", "front,front_right").split(",")
        render_poses(argv[1], argv[2:] or list(POSES), views)
    elif argv and argv[0] == "sheet":
        views = parse_flag(argv, "--views", "front_right").split(",")
        step = int(parse_flag(argv, "--step", "1"))
        sheet_clip(argv[1], argv[2], views, step)
    elif argv and argv[0] == "export":
        export_clips(argv[1:] or list(CLIPS))
    else:
        print(__doc__)
