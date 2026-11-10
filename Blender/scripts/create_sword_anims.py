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
  blender -b --python Blender/scripts/create_sword_anims.py -- check   [report.txt]   anatomy checks: every pose,
                                     every strike-clip frame and every graph blend; exit 1 on any failure
  blender -b --python Blender/scripts/create_sword_anims.py -- tune    [POSE ...]     search small fist / blade
                                     corrections that clear the limits; writes sword_pose_tuning.json
  blender -b --python Blender/scripts/create_sword_anims.py -- path    tip vs debug-sphere distance per path frame and
                                     the lateral reach that would put the tip on every sphere

The left / right strikes (and their cocked poses) are not hand-placed: PATH_* poses are solved from the host's hit
path (MeleeAttackPath.cs, mirrored under "debug attack path") so the blade tip sits on the line pivot -> sphere.
"""
import json
import math
import os
import random
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector

ROOT = r"M:/s&box/Projects/SurvivalGame2026"
SBOX = r"M:/SteamLibrary/steamapps/common/sbox/addons/citizen/Assets/models/citizen_human"
REF = SBOX + "/citizen_human_male_REF.fbx"
BASE_CLIP = SBOX + "/animations/retargeted/weapons/human@holditem_rh_pose_standing_01.fbx"
FIST_CLIP = SBOX + "/animations/retargeted/weapons/human@melee_punch_pose_standing_01.fbx"
# Sword grip inside the closed right fist (hand_R-local centimetres / directions, measured from the punch pose):
# the tunnel between palm and curled fingers, the handle running along the knuckle line (pinky -> index), the
# cutting edge toward the knuckles. hold_R is posed onto this frame in every clip, so the engine (which hangs the
# sword on hold_R: blade = local +Z, edge = local +X) puts the handle through the fist.
FIST_CURL = 0.85                       # punch-pose finger curl blended over the item pose (1 = tight punch fist)
GRIP_ORIGIN = (10.0, 0.0, -1.9)
GRIP_AXIS = (0.17, 0.92, 0.35)
OUT_DIR = ROOT + "/Assets/models/player/animations"
FPS = 30
WORLD_AXES = {"x": Vector((1, 0, 0)), "y": Vector((0, 1, 0)), "z": Vector((0, 0, 1))}
VERBOSE = os.environ.get("SWORD_VERBOSE") == "1"
LAST_GRIP = {}

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
    LAST_GRIP.update(reach_cm=reach, pronation=tw, wrist=sw, roll=roll)
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


def make_fist(rig, base):
    """Close the right hand around the sword: finger bones from the punch pose (blended FIST_CURL over the item
    pose) and hold_R moved into the fist tunnel. Edits `base` in place and re-applies it."""
    fist = import_base_pose(rig, FIST_CLIP)
    for name, m in fist.items():
        if name.startswith("finger_") and name.endswith("_R"):
            qa, qb = base[name].to_quaternion(), m.to_quaternion()
            base[name] = qa.slerp(qb, FIST_CURL).to_matrix().to_4x4()
    apply_base(rig, base)
    pb = rig.pose.bones
    Rh = pb["hand_R"].matrix
    R3 = Rh.to_3x3().normalized()
    blade = (R3 @ Vector(GRIP_AXIS)).normalized()
    knuckles = R3 @ Vector((1.0, 0.0, 0.0))
    edge = (knuckles - blade * knuckles.dot(blade)).normalized()
    frame = Matrix((edge, blade.cross(edge), blade)).transposed().to_4x4()   # columns X = edge, Y, Z = blade
    frame.translation = Rh @ Vector(GRIP_ORIGIN)
    pb["hold_R"].matrix = frame
    bpy.context.view_layer.update()
    base["hold_R"] = pb["hold_R"].matrix_basis.copy()
    apply_base(rig, base)


def setup():
    rig = import_rig()
    base = import_base_pose(rig, BASE_CLIP)
    make_fist(rig, base)
    grip = Grip(rig)
    base_head = {n: rot3(rig.pose.bones[n]).copy() for n in ("neck_0", "head")}
    if VERBOSE:
        print(f"grip: l1 {grip.l1:.1f} l2 {grip.l2:.1f} grip angle {grip.grip_deg:.0f} "
              f"shoulder {tuple(round(v, 3) for v in bone_world(rig, 'arm_upper_R'))}")
    ctx = (rig, base, grip, base_head)
    solve_strike_paths(ctx)
    return ctx


# ------------------------------------------------------------------------------------------ clips
def key_pose(rig, frame):
    for pb in rig.pose.bones:
        pb.keyframe_insert("rotation_quaternion", frame=frame)
        pb.keyframe_insert("location", frame=frame)


def segment_ease(index, count, t):
    """Timing inside one key-to-key segment. Strikes: accelerate into contact (segment 0), decelerate through the
    follow-through (segment 1), smooth after. Two-key clips: smooth. The path is always the per-bone slerp the
    checker validates; only the timing along it changes."""
    if count >= 3 and index == 0:
        return t ** 1.5
    if count >= 3 and index == 1:
        return 1.0 - (1.0 - t) ** 2
    return t * t * (3.0 - 2.0 * t)


EASES = {"linear": lambda t: t, "out": lambda t: 1.0 - (1.0 - t) ** 2, "smooth": lambda t: t * t * (3.0 - 2.0 * t)}


def clip_keys(entry):
    """CLIPS entry -> (fbx, keys, fps). Keys are (seconds, pose) or (seconds, pose, ease into this key)."""
    fbx, keys = entry[0], entry[1]
    return fbx, keys, (entry[2] if len(entry) > 2 else FPS)


def build_clip(ctx, name, keys, fps=FPS):
    """Bake the clip frame by frame: each key pose is solved once, frames in between are per-bone slerps between
    the neighbouring keys (exactly what -- check and the tuner validate), keyed with linear interpolation."""
    rig, base, grip, base_head = ctx
    for a in list(bpy.data.actions):
        bpy.data.actions.remove(a)
    rig.animation_data_create()
    rig.animation_data.action = None
    bpy.context.scene.render.fps = fps
    poses, eases = [], []
    for key in keys:
        seconds, pose_name = key[0], key[1]
        LAST_GRIP.clear()
        apply_pose(rig, base, grip, base_head, tuned_ops(pose_name))
        poses.append((1 + int(round(seconds * fps)), capture_basis(rig)))
        eases.append(EASES[key[2]] if len(key) > 2 else None)
    action = bpy.data.actions.new(name)
    rig.animation_data.action = action
    slot = action.slots.new(id_type="OBJECT", name=rig.name)
    rig.animation_data.action_slot = slot
    last = poses[-1][0] if len(poses) > 1 else 2
    nseg = len(poses) - 1
    for f in range(1, last + 1):
        bpy.context.scene.frame_set(f)
        if nseg <= 0:
            apply_base(rig, poses[0][1])
        else:
            i = max(j for j in range(nseg) if poses[j][0] <= f) if f >= poses[0][0] else 0
            i = min(i, nseg - 1)
            f0, a = poses[i]
            f1, b = poses[i + 1]
            t = 0.0 if f1 == f0 else min(1.0, max(0.0, (f - f0) / (f1 - f0)))
            ease = eases[i + 1]
            blend_poses(rig, a, b, ease(t) if ease else segment_ease(i, nseg, t))
        key_pose(rig, f)
    bpy.context.scene.frame_start = 1
    bpy.context.scene.frame_end = last
    cb = action.layers[0].strips[0].channelbag(slot)
    for fc in cb.fcurves:
        for kp in fc.keyframe_points:
            kp.interpolation = "LINEAR"
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
LEFT_GUARD = ["left", [0.22, -0.10, 0.90], [0.40, 0.40, 1.20]]      # relaxed, hanging a little forward
LEFT_BALANCE = ["left", [0.20, -0.30, 1.08], [0.55, 0.05, 0.80]]    # forward for balance during a cut

WINGSUIT_A = [  # wings out: arms spread wide at shoulder height a little behind the body (a dihedral once flat), chest
    # arched, head tipped back so the face looks along the flight, legs a little apart, feet pointed
    ["lean", -8],
    ["grip", [-0.70, 0.10, 1.42], [0.0, -1.0, 0.0], [0.0, 0.6, 1.0]],
    ["left", [0.70, 0.10, 1.42], [0.30, 0.60, 1.80]],
    ["rot", "neck_0", "x", -20], ["rot", "head", "x", -35],
    ["rot", "leg_upper_L", "y", -10], ["rot", "leg_upper_R", "y", 10],
    ["rot", "ankle_L", "x", 35], ["rot", "ankle_R", "x", 35],
]
WINGSUIT_B = [  # the slow wing flex of the glide loop: arms a little higher and further back
    ["lean", -10],
    ["grip", [-0.69, 0.14, 1.47], [0.0, -1.0, 0.0], [0.0, 0.6, 1.0]],
    ["left", [0.69, 0.14, 1.47], [0.30, 0.60, 1.80]],
    ["rot", "neck_0", "x", -20], ["rot", "head", "x", -35],
    ["rot", "leg_upper_L", "y", -12], ["rot", "leg_upper_R", "y", 12],
    ["rot", "ankle_L", "x", 35], ["rot", "ankle_R", "x", 35],
]

REST_R = [  # shoulder carry: fist in front of the right shoulder, blade back over it
    ["lean", 3],
    ["grip", [-0.18, -0.30, 1.22], [-0.10, 0.60, 0.79], [-0.3, 0.1, -1.0]],
    LEFT_GUARD,
]
REST_UP = [  # aiming up: arm lifted above the shoulder, blade tipped back over the head, ready to slam down
    ["twist", -8], ["lean", -3],
    ["grip", [-0.20, -0.10, 1.58], [-0.05, 0.80, 0.60], [-1.0, 0.1, 0.0]],
    LEFT_BALANCE,
]
SWAP_MID = [  # changing shoulders: sword brought up in front, point up, well clear of the face
    ["twist", 4], ["lean", 4],
    ["grip", [-0.04, -0.46, 1.24], [0.10, -0.25, 0.96], [-0.6, 0.1, -0.8]],
    LEFT_GUARD,
]
REST_L = [  # sword crossed to rest back over the left shoulder
    ["twist", 8], ["lean", 3],
    ["grip", [0.06, -0.26, 1.18], [0.38, 0.62, 0.69], [-0.35, -0.35, -1.0]],
    ["left", [0.24, -0.06, 0.97], [0.45, 0.45, 1.15]],
]
# COCKED_R / COCKED_L and the strike frames inside the host active window are not authored here: they are solved
# from the debug attack path (see "debug attack path" below) so the blade tip follows the hit spheres.
SWING_R_END = [  # follow-through out on the left, the mirror of SWING_L_END: arm across past the left side, blade drooping
    # (twist 23: 28 held 0.42 s while looking 70 degrees aside reached anim_test's 65-degree chest limit)
    ["twist", 23], ["lean", 8],
    ["grip", [0.30, -0.38, 1.04], [0.76, 0.22, -0.61], [-0.3, -0.6, -0.8]],
    ["left", [0.32, 0.02, 1.02], [0.70, 0.30, 0.90]],
]
SWING_R_SETTLE = [
    ["twist", 12], ["lean", 6],
    ["grip", [-0.10, -0.30, 1.14], [-0.26, 0.40, 0.88], [-0.4, 0.1, -1.0]],
    LEFT_GUARD,
]

SWING_L_END = [  # follow-through out on the right: arm reaching out past the right side, blade drooping down-right
    ["twist", -32], ["lean", 8],
    ["grip", [-0.52, -0.26, 1.10], [-0.78, 0.10, -0.62], [-0.3, 0.6, -0.8]],
    LEFT_BALANCE,
]
SWING_L_SETTLE = [  # recovery comes up in front (like the shoulder swap) before laying onto the left shoulder
    ["twist", -2], ["lean", 5],
    ["grip", [-0.06, -0.45, 1.22], [0.08, -0.25, 0.96], [-0.6, 0.1, -0.8]],
    LEFT_GUARD,
]

# Wind-ups (Mark 2026-11-05, the Last Oasis reference video): the cocked poses are WOUND UP behind the head / shoulder
# with a bent elbow, reached by a drawn motion (Sword_Cocked_* clips: WIND_* -> COCKED_* over WIND_DRAW_S), never a
# straight arm held out on the hit path.
WIND_R = [  # forehand draw, halfway: fist up at the right shoulder, blade standing up
    ["twist", -10], ["lean", 0],
    ["grip", [-0.36, -0.14, 1.40], [-0.10, 0.15, 1.0], [-1.0, 0.1, -0.5]],
    LEFT_GUARD,
]
COCKED_R = [  # forehand cocked, the mirror of COCKED_L: fist up in front of the right shoulder, club back over it.
    # Twist 14: held through the 0.6 s windup while looking 70 degrees aside, 18 sat on anim_test's 65-degree chest limit
    ["twist", -14], ["lean", -2],
    ["grip", [-0.30, -0.22, 1.42], [-0.50, 0.62, 0.60], [-1.0, 0.2, -0.3]],
    LEFT_BALANCE,
]
WIND_L = [  # backhand draw, halfway: fist crossing in front of the left chest, blade up and out to the left
    ["twist", 10], ["lean", 2],
    ["grip", [0.14, -0.33, 1.28], [0.82, 0.30, 0.48], [-0.2, -0.8, -0.6]],
    LEFT_GUARD,
]
COCKED_L = [  # backhand cocked: fist up in front of the left shoulder (below the chin, never across the face), club back
    # over the left shoulder - the mirror of COCKED_R
    ["twist", 18], ["lean", -2],
    ["grip", [0.18, -0.30, 1.38], [0.50, 0.62, 0.60], [-0.2, -0.8, -0.6]],
    LEFT_BALANCE,
]
RELEASE_R = [  # forehand release (2 frames in), the mirror of RELEASE_L: fist driving forward, club swinging out right
    ["twist", -14], ["lean", 2],
    ["grip", [-0.30, -0.34, 1.38], [-0.82, 0.48, 0.30], [-1.0, 0.0, -0.3]],
    LEFT_BALANCE,
]
ENTRY_L = [  # backhand entry (3 frames in): fist out in front of the left shoulder, level blade pointing out left
    ["twist", 12], ["lean", 6],
    ["grip", [0.36, -0.30, 1.36], [0.92, 0.38, -0.05], [0.0, -1.0, -0.4]],
    LEFT_BALANCE,
]
RELEASE_L = [  # backhand release (2 frames in): fist driving forward-left, club swinging out left behind it
    ["twist", 16], ["lean", 2],
    ["grip", [0.26, -0.36, 1.36], [0.82, 0.48, 0.30], [-0.2, -0.8, -0.6]],
    LEFT_BALANCE,
]
WIND_OVER = [  # overhead draw, halfway: fist rising in front of the right shoulder, blade up
    ["twist", -6], ["lean", -2],
    ["grip", [-0.18, -0.24, 1.55], [0.0, 0.15, 1.0], [-1.0, -0.2, -0.3]],
    LEFT_BALANCE,
]
COCKED_OVER = [  # overhead cocked: fist above the head, elbow high, blade hanging down behind the head
    ["twist", -10], ["lean", -8],
    ["grip", [-0.12, 0.02, 1.80], [0.40, 0.62, -0.68], [-0.7, -0.3, 0.6]],
    LEFT_BALANCE,
]
CHOP_MID = [  # coming over the top: fist out in front of the face, blade still pointing up
    ["twist", -2], ["lean", 10],
    ["grip", [-0.12, -0.48, 1.48], [0.02, -0.55, 0.83], [-0.8, 0.2, -0.6]],
    LEFT_BALANCE,
]
CHOP_END = [  # chopped through: bent forward, fist at the waist in front, blade pointing down at the ground ahead
    ["twist", 6], ["lean", 28],
    ["grip", [-0.16, -0.50, 0.96], [-0.05, -0.62, -0.78], [-0.6, 0.3, -0.8]],
    ["left", [0.22, -0.18, 0.98], [0.60, 0.20, 0.85]],
]
CHOP_SETTLE = [
    ["twist", 0], ["lean", 8],
    ["grip", [-0.25, -0.42, 1.18], [-0.12, 0.30, 0.95], [-0.5, 0.1, -1.0]],
    LEFT_GUARD,
]

WIND_STAB = [  # jab draw, halfway: fist coming down off the shoulder in front of the chest, club tipping forward
    ["twist", -12], ["lean", 2],
    ["grip", [-0.24, -0.20, 1.20], [0.0, -0.70, 0.71], [-0.3, 1.0, -0.6]],
    ["left", [0.14, -0.38, 1.16], [0.55, -0.05, 0.85]],
]
COCKED_STAB = [  # jab cocked - the telegraph (Mark 2026-11-05: "clear that a stab is coming"): a hip chamber - fist
    # pulled back beside the right hip with the elbow behind the body and bent ~90 degrees (a fist by the shoulder folded
    # the elbow to 38 degrees and flared it up: "elbow is wonky"), club level and aimed at the target, body turned away,
    # free hand reaching out at the target. Twist 22: looking 70 degrees aside, 30 put the chest 66 degrees off the hips.
    ["twist", -22], ["lean", -2],
    ["grip", [-0.24, 0.12, 1.08], [0.04, -0.98, 0.18], [-0.3, 1.0, -0.4]],
    ["left", [0.10, -0.52, 1.34], [0.55, -0.10, 0.90]],
]
STAB_END = [  # full extension: arm nearly straight at chest height, club in line with the forearm, elbow underneath
    ["twist", 14], ["lean", 10],
    ["grip", [-0.12, -0.58, 1.28], [0.03, -1.0, 0.03], [-0.6, 0.3, -1.0]],
    ["left", [0.26, -0.02, 1.02], [0.70, 0.30, 0.90]],
]
STAB_SETTLE = [
    ["twist", 0], ["lean", 6],
    ["grip", [-0.18, -0.32, 1.14], [-0.12, 0.30, 0.95], [-0.4, 0.1, -1.0]],
    LEFT_GUARD,
]

BLOCK = [  # guard up (Mark 2026-11-04): blade held level in front of the head, fist out on the right, point to the left
    ["twist", 4], ["lean", 2],
    ["grip", [-0.42, -0.34, 1.56], [1.0, -0.10, 0.05], [-1.0, 0.2, -0.5]],
    LEFT_GUARD,
]

# ------------------------------------------------------------------------------------------ debug attack path
# The left / right strikes follow the host's hit path, Code/Player/MeleeAttackPath.cs (ComputeLateralTip +
# TransformLocalToWorld), the same path the debug sphere overlay draws. Engine pawn-local units, x forward, y up,
# z right; the numbers mirror the one-handed class (melee_weapon_classes.json "oneHanded") and basicplayer.prefab.
PATH_RANGE_U = 61.6          # reachLateralMeters 1.54 x (BodyHeight 72 / 1.8)
PATH_ARC_DEG = 150.0         # lateralArcDegrees
PATH_PIVOT_Y_U = 54.0        # ServerEyeHeight 64 + MeleeAttackZaxisStart -10 (the pitch pivot, shoulder height)
PATH_TILT_DEG = {"R": 25.0, "L": 25.0}   # MeleeAttackTiltDegreesRight / Left: mirrored, a slight downward swoop (Mark 2026-11-05)
PATH_PUSH_U = 8.0            # MeleeSwingForwardOffsetMeters 0.2 x 40
PATH_ACTIVE_S = 0.20         # activeSeconds (Mark 2026-11-04: 200 ms damage swoop): the strike clip starts when the host's active window opens
U_PER_M = 39.37              # human model units per Blender metre
SWORD_TIP_M = 40.0 / U_PER_M  # sword_reforged.vmdl is 40 u butt -> tip and its butt sits on hold_R (grip along 0)
STRIKE_FPS = 60              # lateral strikes are baked at 60 fps: 13 path samples inside the 0.2 s active window
PATH_STEPS = int(round(PATH_ACTIVE_S * STRIKE_FPS))
PATH_MARGIN = 0.5            # path poses stay this far inside LIMITS (see violations) so the bake between them passes
PATH_KINDS = {"R": (PATH_ARC_DEG * 0.5, -PATH_ARC_DEG * 0.5),   # Right: right -> left (MeleeAttackPath.GetArcDegreeSpan)
              "L": (-PATH_ARC_DEG * 0.5, PATH_ARC_DEG * 0.5)}   # Left: the mirror
PATH_ELBOW = {"R": [-0.3, 0.2, -1.0], "L": [-0.3, 0.3, -1.0]}   # forehand / backhand elbow hint
PATH_END_ARM_CLEAR = 0.05    # the last path frame keeps the arm this far off the torso (m): a guard cut from it (0.05 s blend
                             # to BLOCK) swept the arm 1.4 cm into the chest at the 1.54 m reach (2026-11-05)
PATH_INFO = {}               # per path pose: angle, sphere, blade turn, distance along the line (solve_strike_paths)


def engine_to_blender(x, y, z):
    """Engine pawn-local (x forward, y up, z right; units) -> Blender world metres (forward -Y, right -X, up Z)."""
    return Vector((-z / U_PER_M, -x / U_PER_M, y / U_PER_M))


def path_pivot(range_u=PATH_RANGE_U):
    """MeleeAttackPath.GetSwingPivotWorld for a lateral strike: (0.14 R + push, pivot height, 0)."""
    return engine_to_blender(range_u * 0.14 + PATH_PUSH_U, PATH_PIVOT_Y_U, 0.0)


def path_sphere(kind, t, range_u=PATH_RANGE_U):
    """MeleeAttackPath.ComputeLateralTip at stroke progress t (0..1): the debug sphere, Blender world metres."""
    start, end = PATH_KINDS[kind]
    a = math.radians(start + (end - start) * t)
    drop = min(range_u * math.tan(math.radians(PATH_TILT_DEG[kind])) * 0.28, range_u * 0.42)
    x = range_u * 0.14 + math.cos(a) * range_u + PATH_PUSH_U
    return engine_to_blender(x, PATH_PIVOT_Y_U - drop * t, math.sin(a) * range_u), math.degrees(a)


def path_body(a_deg):
    """Torso along the sweep: the shoulders follow the blade (offset to bring the right shoulder forward for reach),
    the spine leans into the cut most at the front. The turn toward the left stops at 20 degrees: held across the
    back half of the sweep with the aim layer looking 70 degrees aside, 28 put the chest past anim_test's 65 degrees
    from the hips. With the 0.2 s damage window (13 path samples) the peak is sampled, so both ends are 4 degrees
    tighter (-26 / 16): 20 read 66-67 degrees looking aside and crouched."""
    twist = max(-26.0, min(16.0, -0.4 * a_deg + 10.0))
    lean = 4.0 + 12.0 * (1.0 - abs(a_deg) / 75.0)
    return twist, lean


def path_ops(kind, a_deg, tip, blade):
    twist, lean = path_body(a_deg)
    fist = tip - blade * SWORD_TIP_M
    return [["twist", twist], ["lean", lean], ["grip", list(fist), list(blade), PATH_ELBOW[kind]], LEFT_BALANCE]


def path_pose_ok(ctx, ops, margin=PATH_MARGIN, min_arm=0.0):
    rig, base, grip, base_head = ctx
    LAST_GRIP.clear()
    apply_pose(rig, base, grip, base_head, ops)
    m = pose_metrics(rig)
    return not violations(m, margin) and m["arm_torso"] >= min_arm


def solve_path_sample(ctx, kind, t, prev_phi=None, range_u=PATH_RANGE_U, at_sphere_only=False):
    """Blade tip on the line pivot -> sphere: at the sphere when the arm can reach it inside LIMITS, else as far out
    along that line as it can. The blade lies in the cut plane, turned `phi` degrees off the line (searched; a small
    cost on changing it from the previous sample keeps the blade from flicking). Returns (distance along the line,
    phi, ops) or None."""
    pivot = path_pivot(range_u)
    sphere, a_deg = path_sphere(kind, t, range_u)
    min_arm = PATH_END_ARM_CLEAR if t >= 0.999 else 0.0
    ok = lambda ops: path_pose_ok(ctx, ops, min_arm=min_arm)
    u = (sphere - pivot).normalized()
    ds = (sphere - pivot).length
    best = None

    def reach_along(phi):
        """Furthest distance along the line that passes, scanning in from the sphere (the passing set is not always
        one interval, so a plain bisection from the body undershoots), then refined to 5 mm."""
        blade = Quaternion(Vector((0, 0, 1)), math.radians(phi)) @ u
        ops_at = lambda d: path_ops(kind, a_deg, pivot + u * d, blade)
        if ok(ops_at(ds)):
            return ds, ops_at(ds)
        if at_sphere_only:
            return None
        d = ds - 0.08
        while d >= 0.6:
            if ok(ops_at(d)):
                lo, hi = d, d + 0.08
                for _ in range(4):
                    mid = 0.5 * (lo + hi)
                    if ok(ops_at(mid)):
                        lo = mid
                    else:
                        hi = mid
                return lo, ops_at(lo)
            d -= 0.08
        return None

    def consider(phi):
        nonlocal best
        r = reach_along(phi)
        if r is None:
            return
        score = r[0] - (0.002 * abs(phi - prev_phi) if prev_phi is not None else 0.0)
        if best is None or score > best[0]:
            best = (score, r[0], phi, r[1])

    for phi in range(-72, 73, 8):
        consider(phi)
        if at_sphere_only and best is not None:
            return best[1:]
    if best is not None:
        for phi in (best[2] - 4, best[2] + 4):
            consider(phi)
    return None if best is None else best[1:]


def solve_strike_paths(ctx):
    """PATH_<kind>_<nn>: one pose per 60 fps frame of the active window, generated from the debug path."""
    for kind in PATH_KINDS:
        prev = None
        for i in range(PATH_STEPS + 1):
            t = i / PATH_STEPS
            d, phi, ops = solve_path_sample(ctx, kind, t, prev)
            prev = phi
            name = f"PATH_{kind}_{i:02d}"
            POSES[name] = ops
            sphere, a_deg = path_sphere(kind, t)
            PATH_INFO[name] = dict(kind=kind, t=t, a=a_deg, phi=phi, d=d, sphere=sphere)


def blade_tip(rig):
    hold_m = rig.matrix_world @ rig.pose.bones["hold_R"].matrix
    return hold_m.translation + (hold_m.to_3x3().normalized() @ Vector((0, 0, 1))).normalized() * SWORD_TIP_M


def path_report(ctx):
    """Measured tip (posed rig, real sword length) against each debug sphere."""
    rig, base, grip, base_head = ctx
    pivot = path_pivot()
    lines = []
    for name, info in PATH_INFO.items():
        LAST_GRIP.clear()
        apply_pose(rig, base, grip, base_head, POSES[name])
        tip, sphere = blade_tip(rig), info["sphere"]
        u = (sphere - pivot).normalized()
        off = ((tip - pivot) - u * (tip - pivot).dot(u)).length
        lines.append(f"info path {name} t={info['t']:.2f} arc={info['a']:+.0f} blade turn {info['phi']:+d}: sphere "
                     f"{(sphere - pivot).length:.3f} m from the pivot, tip {(tip - pivot).dot(u):.3f} m along the line "
                     f"({off * 100:.1f} cm off it), tip->sphere {(sphere - tip).length:.3f} m, "
                     f"shoulder->tip {(tip - bone_world(rig, 'arm_upper_R')).length:.3f} m")
    return lines


def reach_for_spheres(ctx, kind):
    """Per path sample: the largest lateral reach (engine units) at which the tip can sit ON that debug sphere."""
    out = []
    for i in range(PATH_STEPS + 1):
        t = i / PATH_STEPS
        lo, hi = 20.0, PATH_RANGE_U
        for _ in range(9):
            mid = 0.5 * (lo + hi)
            if solve_path_sample(ctx, kind, t, range_u=mid, at_sphere_only=True):
                lo = mid
            else:
                hi = mid
        out.append(lo)
    return out


# Strike timing (Mark 2026-11-05, Last Oasis reference): fast cut, then a deep follow-through that HOLDS before the
# sword settles back to rest. A follow-up click cuts the hold short (b_sword_interrupt) and draws the next wind-up
# straight from the follow-through, so a chain of swings flows R -> L -> R like the reference.
STRIKE_ENTRY_FRAMES = {"R": 4, "L": 6}   # cocked (behind the shoulder) -> release -> onto the hit path (first frames of the window)
# Entry keys (60 fps frame, pose) between the cocked frame and PATH_<kind>_04. The forehand drives forward over the
# shoulder; the backhand swings its level blade round behind the head to the left shoulder and then round the left side
# onto the path in small steps (one big step slerped the blade up over the head).
STRIKE_ENTRY = {"R": [(2, "RELEASE_R")],
                "L": [(2, "RELEASE_L"), (3, "ENTRY_L"), (4, "PATH_L_02"), (5, "PATH_L_04")]}
FOLLOW_AT, FOLLOW_HOLD, SETTLE_AT, STRIKE_END = 0.28, 0.42, 0.54, 0.64   # PlayerAnimation.MeleeStrikeClipSeconds
WIND_DRAW_S = 0.26           # Sword_Cocked_* draw (after the 0.12 s pose cross-fade): wound up ~0.38 s in (one-handed windup 0.6 s, scav 0.35 s - the draw stays short enough for both)


def lateral_strike(kind, follow, settle, rest):
    """Cocked pose -> onto the path in STRIKE_ENTRY_FRAMES, the path frames for the rest of the active window (linear:
    the host sweeps the arc at constant speed), then the follow-through decelerating, held, the settle and rest."""
    keys = [(0.0, f"COCKED_{kind}")] + [(f / STRIKE_FPS, pose, "linear") for f, pose in STRIKE_ENTRY[kind]]
    keys += [(i / STRIKE_FPS, f"PATH_{kind}_{i:02d}", "linear") for i in range(STRIKE_ENTRY_FRAMES[kind], PATH_STEPS + 1)]
    return keys + [(FOLLOW_AT, follow, "out"), (FOLLOW_HOLD, follow, "linear"), (SETTLE_AT, settle, "smooth"),
                   (STRIKE_END, rest, "smooth")]


# Strike timing (0.64 s, PlayerAnimation.MeleeStrikeClipSeconds). Left / right: cocked -> the debug path over the 0.2 s
# active window (the clip starts when it opens), follow-through 0.28 held to 0.42, settle 0.54, rest 0.64. Overhead:
# cocked -> over the top 0.08 -> chopped through 0.20 -> held -> settle -> rest. A follow-up click or the guard cuts the
# hold short in the graph (b_sword_interrupt), so the next wind-up draws from the follow-through.
CLIPS = {
    "Sword_Rest_R": ("human@sword_rest_r.fbx", [(0.0, "REST_R")]),
    "Sword_Rest_L": ("human@sword_rest_l.fbx", [(0.0, "REST_L")]),
    "Sword_Swap_Mid": ("human@sword_swap_mid.fbx", [(0.0, "SWAP_MID")]),
    "Sword_Rest_Up": ("human@sword_rest_up.fbx", [(0.0, "REST_UP")]),
    "Sword_Block": ("human@sword_block.fbx", [(0.0, "BLOCK")]),
    "Sword_Cocked_R": ("human@sword_cocked_r.fbx", [(0.0, "WIND_R"), (WIND_DRAW_S, "COCKED_R", "out")], STRIKE_FPS),
    "Sword_Cocked_L": ("human@sword_cocked_l.fbx", [(0.0, "WIND_L"), (WIND_DRAW_S, "COCKED_L", "out")], STRIKE_FPS),
    "Sword_Cocked_Over": ("human@sword_cocked_over.fbx", [(0.0, "WIND_OVER"), (WIND_DRAW_S, "COCKED_OVER", "out")], STRIKE_FPS),
    "Sword_Cocked_Stab": ("human@sword_cocked_stab.fbx", [(0.0, "WIND_STAB"), (WIND_DRAW_S, "COCKED_STAB", "out")], STRIKE_FPS),
    "Sword_Attack_R": ("human@sword_attack_r.fbx", lateral_strike("R", "SWING_R_END", "SWING_R_SETTLE", "REST_R"), STRIKE_FPS),
    "Sword_Attack_L": ("human@sword_attack_l.fbx", lateral_strike("L", "SWING_L_END", "SWING_L_SETTLE", "REST_L"), STRIKE_FPS),
    "Sword_Attack_Over": ("human@sword_attack_over.fbx",
                          [(0.0, "COCKED_OVER"), (0.08, "CHOP_MID"), (0.20, "CHOP_END", "linear"),
                           (FOLLOW_HOLD, "CHOP_END", "linear"), (SETTLE_AT, "CHOP_SETTLE", "smooth"),
                           (STRIKE_END, "REST_R", "smooth")]),
    "Sword_Attack_Stab": ("human@sword_attack_stab.fbx",
                          [(0.0, "COCKED_STAB"), (0.20, "STAB_END", "out"), (FOLLOW_HOLD - 0.08, "STAB_END", "linear"),
                           (SETTLE_AT, "STAB_SETTLE", "smooth"), (STRIKE_END, "REST_R", "smooth")], STRIKE_FPS),
    "Wingsuit_Glide": ("human@wingsuit_glide.fbx",
                       [(0.0, "WINGSUIT_A"), (0.8, "WINGSUIT_B", "smooth"), (1.6, "WINGSUIT_A", "smooth")]),
}
POSES = {k: v for k, v in dict(globals()).items()
         if k.isupper() and isinstance(v, list) and v and isinstance(v[0], list) and isinstance(v[0][0], str)}

# Blends the engine graph performs between static poses (selector cross-fades): every one is checked
# frame by frame like a clip. The shoulder swap is two short blends through SWAP_MID, never R <-> L direct.
GRAPH_BLENDS = [
    ("REST_R", "SWAP_MID"), ("SWAP_MID", "REST_L"),
    ("REST_R", "WIND_R"), ("REST_L", "WIND_L"),
    ("REST_R", "WIND_OVER"), ("REST_L", "WIND_OVER"),
    ("REST_R", "WIND_STAB"), ("REST_L", "WIND_STAB"),
    # a press during a swap / right after an aim flip cocks from wherever the pose is
    ("SWAP_MID", "WIND_R"), ("SWAP_MID", "WIND_L"), ("SWAP_MID", "WIND_OVER"), ("SWAP_MID", "WIND_STAB"),
    ("REST_R", "WIND_L"), ("REST_L", "WIND_R"),
    # aiming up: raised stance <-> right shoulder directly, <-> left shoulder through SWAP_MID; any press from it
    ("REST_R", "REST_UP"), ("REST_UP", "SWAP_MID"),
    ("REST_UP", "WIND_OVER"), ("REST_UP", "WIND_R"), ("REST_UP", "WIND_L"), ("REST_UP", "WIND_STAB"),
    # guard up / down from any stance (a held heavy hangs cocked; right click raises the guard from it), and a click
    # straight out of the guard
    ("REST_R", "BLOCK"), ("REST_L", "BLOCK"), ("REST_UP", "BLOCK"), ("SWAP_MID", "BLOCK"),
    ("COCKED_R", "BLOCK"), ("COCKED_L", "BLOCK"), ("COCKED_OVER", "BLOCK"), ("COCKED_STAB", "BLOCK"),
    ("BLOCK", "WIND_R"), ("BLOCK", "WIND_L"), ("BLOCK", "WIND_OVER"), ("BLOCK", "WIND_STAB"),
]
# b_sword_interrupt cuts a strike at the end of its damage window into the next cocked pose or the guard
# (PATH_* end frames exist once solve_strike_paths ran; check_all adds them).
INTERRUPT_FROM = ["CHOP_END", "STAB_END", "SWING_R_END", "SWING_L_END"]
INTERRUPT_TO = ["WIND_R", "WIND_L", "WIND_OVER", "WIND_STAB", "BLOCK"]


def graph_blends():
    """GRAPH_BLENDS plus the b_sword_interrupt cuts out of every strike's last damage frame."""
    ends = INTERRUPT_FROM + [f"PATH_{k}_{PATH_STEPS:02d}" for k in PATH_KINDS]
    return GRAPH_BLENDS + [(a, b) for a in ends for b in INTERRUPT_TO]

TUNE_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sword_pose_tuning.json")


def load_tuning():
    if os.path.exists(TUNE_PATH):
        with open(TUNE_PATH, encoding="utf-8") as f:
            return json.load(f)
    return {}


TUNING = load_tuning()


def rotate_dir(v, ax_deg, az_deg):
    """Tilt a direction by two small angles: about world X (forward/back) and world Z (around the body)."""
    q = Quaternion(Vector((1, 0, 0)), math.radians(ax_deg)) @ Quaternion(Vector((0, 0, 1)), math.radians(az_deg))
    return list(q @ Vector(v).normalized())


def is_path_pose(name):
    """Poses solved from the debug attack path are exact by construction: never tuned."""
    return name.startswith("PATH_")


def tuned_ops(name, override=None):
    """The pose's ops with its tuning (fist offset metres, blade tilt degrees) applied to the grip op."""
    t = override if override is not None else (None if is_path_pose(name) else TUNING.get(name))
    ops = []
    for op in POSES[name]:
        if op[0] == "grip" and t:
            d = t.get("fist", [0, 0, 0])
            fist = [op[1][i] + d[i] for i in range(3)]
            blade = rotate_dir(op[2], *t.get("blade", [0, 0]))
            ops.append(["grip", fist, blade, op[3]])
        else:
            ops.append(op)
    return ops



# ------------------------------------------------------------------------------------------ anatomy checks
# Thresholds (metres / degrees). The same numbers are mirrored in the engine anim_test.
LIMITS = {
    "blade_head": 0.04,      # blade (guard..tip) clear of the head sphere
    "blade_neck": 0.03,
    "blade_torso": 0.02,     # blade clear of the torso capsule (resting ON the shoulder is outside it)
    "blade_pelvis": 0.02,
    "arm_torso": 0.0,        # right forearm / upper arm / left hand not inside the body
    "elbow_min": 35.0,       # interior elbow angle: never folded tighter than this ...
    "elbow_max": 178.0,      # ... nor bent backwards
    "reach_cm": 1.5,         # grip solver: fist actually reaches the asked point
    "pronation": 95.0,       # forearm twist beyond the item pose
    "wrist": 50.0,           # wrist flex / deviation
    "roll": 95.0,            # upper-arm roll
}
TORSO_R, HEAD_R, NECK_R, PELVIS_R, ARM_R, HAND_R = 0.12, 0.11, 0.06, 0.15, 0.035, 0.04


def seg_seg(p1, q1, p2, q2):
    d1, d2, r = q1 - p1, q2 - p2, p1 - p2
    a, e, f = d1.dot(d1), d2.dot(d2), d2.dot(r)
    if a <= 1e-9 and e <= 1e-9:
        return (p1 - p2).length
    if a <= 1e-9:
        s_, t_ = 0.0, max(0.0, min(1.0, f / e))
    else:
        c = d1.dot(r)
        if e <= 1e-9:
            t_, s_ = 0.0, max(0.0, min(1.0, -c / a))
        else:
            b = d1.dot(d2)
            den = a * e - b * b
            s_ = max(0.0, min(1.0, (b * f - c * e) / den)) if den > 1e-9 else 0.0
            t_ = (b * s_ + f) / e
            if t_ < 0.0:
                t_, s_ = 0.0, max(0.0, min(1.0, -c / a))
            elif t_ > 1.0:
                t_, s_ = 1.0, max(0.0, min(1.0, (b - c) / a))
    return ((p1 + d1 * s_) - (p2 + d2 * t_)).length


def body_proxies(rig):
    W = lambda n: bone_world(rig, n)
    neck, head = W("neck_0"), W("head")
    up = (head - neck).normalized()
    hold_m = rig.matrix_world @ rig.pose.bones["hold_R"].matrix
    blade_dir = (hold_m.to_3x3().normalized() @ Vector((0, 0, 1))).normalized()
    g = hold_m.translation
    return {
        "head": head + up * 0.09, "neck": (neck, head),
        "torso": (W("spine_0"), neck - up * 0.06), "pelvis": W("pelvis"),
        "blade": (g + blade_dir * 0.07, g + blade_dir * 0.92),
        "armR": (W("arm_upper_R"), W("arm_lower_R"), W("hand_R")),
        "armL": (W("arm_upper_L"), W("arm_lower_L"), W("hand_L")),
    }


def elbow_angle(S, E, H):
    return math.degrees((S - E).angle(H - E))


def pose_metrics(rig, with_grip=True):
    b = body_proxies(rig)
    bl0, bl1 = b["blade"]
    t0, t1 = b["torso"]
    n0, n1 = b["neck"]
    S, E, H = b["armR"]
    SL, EL, HL = b["armL"]
    m = {
        "blade_head": seg_seg(bl0, bl1, b["head"], b["head"]) - HEAD_R,
        "blade_neck": seg_seg(bl0, bl1, n0, n1) - NECK_R,
        "blade_torso": seg_seg(bl0, bl1, t0, t1) - TORSO_R,
        "blade_pelvis": seg_seg(bl0, bl1, b["pelvis"], b["pelvis"]) - PELVIS_R,
        "arm_torso": min(seg_seg(E, H, t0, t1) - TORSO_R - ARM_R,
                         seg_seg(HL, HL, t0, t1) - TORSO_R - HAND_R,
                         seg_seg(HL, HL, b["pelvis"], b["pelvis"]) - PELVIS_R - HAND_R),
        "elbowR": elbow_angle(S, E, H),
        "elbowL": elbow_angle(SL, EL, HL),
    }
    if with_grip and LAST_GRIP:
        m.update(reach_cm=LAST_GRIP["reach_cm"], pronation=abs(LAST_GRIP["pronation"]),
                 wrist=LAST_GRIP["wrist"], roll=abs(LAST_GRIP["roll"]))
    return m


def violations(m, margin=0.0):
    """List of (name, amount) where the metric is outside its limit; amount in the limit's units. `margin` (0..1)
    tightens every limit (1 cm clearance, 3 degrees, 0.3 cm reach per unit) — the tuner aims inside the limits
    so the checked result passes with room to spare."""
    out = []
    for k in ("blade_head", "blade_neck", "blade_torso", "blade_pelvis", "arm_torso"):
        lim = LIMITS[k] + 0.01 * margin
        if m[k] < lim:
            out.append((k, lim - m[k]))
    for k in ("elbowR", "elbowL"):
        if m[k] < LIMITS["elbow_min"] + 3 * margin:
            out.append((k + "_fold", LIMITS["elbow_min"] + 3 * margin - m[k]))
        if m[k] > LIMITS["elbow_max"] - 3 * margin:
            out.append((k + "_hyper", m[k] - LIMITS["elbow_max"] + 3 * margin))
    for k, step in (("reach_cm", 0.3), ("pronation", 3.0), ("wrist", 3.0), ("roll", 3.0)):
        lim = LIMITS[k] - step * margin
        if k in m and m[k] > lim:
            out.append((k, m[k] - lim))
    return out


def penalty(m, margin=1.0):
    w = {"blade_head": 400, "blade_neck": 400, "blade_torso": 300, "blade_pelvis": 200, "arm_torso": 300,
         "elbowR_fold": 2, "elbowR_hyper": 2, "elbowL_fold": 2, "elbowL_hyper": 2,
         "reach_cm": 3, "pronation": 0.4, "wrist": 0.4, "roll": 0.3}
    return sum(w[k] * a for k, a in violations(m, margin))


def blend_poses(rig, basis_a, basis_b, t):
    """Engine-style cross-fade: per-bone local rotation slerp, location lerp."""
    for pb in rig.pose.bones:
        a, b = basis_a[pb.name], basis_b[pb.name]
        qa, qb = a.to_quaternion(), b.to_quaternion()
        m = qa.slerp(qb, t).to_matrix().to_4x4()
        m.translation = a.translation.lerp(b.translation, t)
        pb.matrix_basis = m
    bpy.context.view_layer.update()


def capture_basis(rig):
    return {pb.name: pb.matrix_basis.copy() for pb in rig.pose.bones}


def fmt(m):
    keys = ("blade_head", "blade_neck", "blade_torso", "arm_torso", "elbowR", "elbowL", "reach_cm", "pronation", "wrist", "roll")
    return " ".join(f"{k}={m[k]:.2f}" if isinstance(m.get(k), float) and k.startswith(("blade", "arm", "reach"))
                    else f"{k}={m[k]:.0f}" for k in keys if k in m)


def check_all(ctx, report_path=None):
    """Every static pose, every strike-clip frame and every graph blend must pass. Returns failure count."""
    rig, base, grip, base_head = ctx
    lines, failures = [], 0
    bases = {}
    for name in POSES:
        LAST_GRIP.clear()
        apply_pose(rig, base, grip, base_head, tuned_ops(name))
        m = pose_metrics(rig)
        bases[name] = capture_basis(rig)
        v = violations(m)
        failures += bool(v)
        lines.append(f"{'FAIL' if v else 'PASS'} pose {name}: {fmt(m)}" + (f"  !! {v}" if v else ""))
    blends = graph_blends()
    for a, b in transition_pairs():
        if (a, b) in blends:
            continue
        bad = []
        for i in range(1, 10):
            blend_poses(rig, bases[a], bases[b], i / 10)
            v = violations(pose_metrics(rig, with_grip=False))
            if v:
                bad.append((i / 10, v))
        if bad:
            lines.append(f"info key-pair {a}->{b}: {bad[:2]}")
    for a, b in blends:
        worst = None
        for i in range(11):
            blend_poses(rig, bases[a], bases[b], i / 10)
            v = violations(pose_metrics(rig, with_grip=False))
            if v and worst is None:
                worst = (i / 10, v)
        failures += worst is not None
        lines.append(f"{'FAIL' if worst else 'PASS'} blend {a}->{b}" + (f"  !! t={worst[0]} {worst[1]}" if worst else ""))
    for clip, entry in CLIPS.items():
        fbx, keys, fps = clip_keys(entry)
        if len(keys) < 2:
            continue
        build_clip(ctx, clip, keys, fps)
        bad = []
        for f in range(1, bpy.context.scene.frame_end + 1):
            bpy.context.scene.frame_set(f)
            v = violations(pose_metrics(rig, with_grip=False))
            if v:
                bad.append((f, v))
        rig.animation_data.action = None
        failures += bool(bad)
        lines.append(f"{'FAIL' if bad else 'PASS'} clip {clip}" + (f"  !! {bad[:3]}" if bad else ""))
    lines.extend(path_report(ctx))
    lines.append(f"RESULT {failures} failing items")
    text = "\n".join(lines)
    print(text)
    if report_path:
        with open(report_path, "w", encoding="utf-8") as f:
            f.write(text + "\n")
    return failures


def transition_pairs():
    """Every pose pair the character ever moves between: graph blends plus consecutive clip keys."""
    pairs = set(graph_blends())
    for entry in CLIPS.values():
        keys = clip_keys(entry)[1]
        for ka, kb in zip(keys, keys[1:]):
            pairs.add((ka[1], kb[1]))
    return sorted(pairs)


def tune_pose(ctx, name, bases, rounds=70, seed=1):
    """Search a small fist offset (<= 10 cm) and blade tilt (<= 20 deg) that clears every anatomy limit, in the
    pose itself AND along every transition into or out of it (sampled cross-fades against the neighbours' current
    poses), while staying as close as possible to the authored intent."""
    rig, base, grip, base_head = ctx
    rnd = random.Random(seed)
    partners = [b if a == name else a for a, b in transition_pairs() if name in (a, b) and a != b]

    def evaluate(t):
        LAST_GRIP.clear()
        apply_pose(rig, base, grip, base_head, tuned_ops(name, t))
        m = pose_metrics(rig)
        mine = capture_basis(rig)
        trans = 0.0
        for other in partners:
            for k in (0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9):
                blend_poses(rig, mine, bases[other], k)
                trans += penalty(pose_metrics(rig, with_grip=False))
        reg = sum(x * x for x in t["fist"]) / 0.05 ** 2 + sum(x * x for x in t["blade"]) / 12.0 ** 2
        return penalty(m) + trans + reg * 0.5, m

    best_t = TUNING.get(name, {"fist": [0.0, 0.0, 0.0], "blade": [0.0, 0.0]})
    best_s, best_m = evaluate(best_t)
    zero = {"fist": [0.0, 0.0, 0.0], "blade": [0.0, 0.0]}
    s0, m0 = evaluate(zero)
    if s0 < best_s:
        best_t, best_s, best_m = zero, s0, m0
    step_f, step_b = 0.04, 8.0
    for i in range(rounds):
        cand = {"fist": [max(-0.10, min(0.10, x + rnd.gauss(0, step_f))) for x in best_t["fist"]],
                "blade": [max(-20.0, min(20.0, x + rnd.gauss(0, step_b))) for x in best_t["blade"]]}
        sc, m = evaluate(cand)
        if sc < best_s:
            best_t, best_s, best_m = cand, sc, m
        if i % 20 == 19:
            step_f, step_b = step_f * 0.6, step_b * 0.6
    print(f"tuned {name}: score {best_s:.2f} fist {[round(x, 3) for x in best_t['fist']]} "
          f"blade {[round(x, 1) for x in best_t['blade']]}  {fmt(best_m)}  viol {violations(best_m)}")
    return best_t


def all_bases(ctx):
    rig, base, grip, base_head = ctx
    out = {}
    for name in POSES:
        apply_pose(rig, base, grip, base_head, tuned_ops(name))
        out[name] = capture_basis(rig)
    return out


def tune_all(ctx, names, passes=int(os.environ.get("SWORD_TUNE_PASSES", "2"))):
    """Coordinate passes: each pose is re-tuned against its neighbours' latest poses."""
    for p in range(passes):
        bases = all_bases(ctx)
        for name in names:
            if not is_path_pose(name) and any(op[0] == "grip" for op in POSES[name]):
                TUNING[name] = tune_pose(ctx, name, bases, rounds=int(os.environ.get("SWORD_TUNE_ROUNDS", "70")), seed=1 + p)
                LAST_GRIP.clear()
                apply_pose(ctx[0], ctx[1], ctx[2], ctx[3], tuned_ops(name))
                bases[name] = capture_basis(ctx[0])
                with open(TUNE_PATH, "w", encoding="utf-8") as f:
                    json.dump(TUNING, f, indent=1, sort_keys=True)

# ------------------------------------------------------------------------------------------ preview
VIEWS = {
    "front": (Vector((0.0, -3.0, 1.45)), Vector((0.0, -0.1, 1.15))),
    "front_right": (Vector((-2.1, -2.2, 1.6)), Vector((0.0, -0.1, 1.15))),
    "front_left": (Vector((2.1, -2.2, 1.6)), Vector((0.0, -0.1, 1.15))),
    "right": (Vector((-3.0, 0.0, 1.45)), Vector((0.0, -0.1, 1.15))),
    "back": (Vector((0.0, 3.0, 1.6)), Vector((0.0, 0.0, 1.15))),
    "chase": (Vector((-0.45, 2.7, 2.0)), Vector((0.0, -0.4, 1.25))),
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


def look_at_hold(rig, dist=0.38, yaw_deg=-60.0, up=0.10):
    scene = bpy.context.scene
    g = bone_world(rig, "hold_R")
    d = Vector((math.sin(math.radians(yaw_deg)), -math.cos(math.radians(yaw_deg)), 0.0))
    scene.camera.location = g + d * dist + Vector((0, 0, up))
    scene.camera.rotation_euler = (g - scene.camera.location).to_track_quat("-Z", "Y").to_euler()


def render_poses(out_dir, names, views):
    ctx = setup()
    rig, base, grip, base_head = ctx
    add_sword(rig)
    os.makedirs(out_dir, exist_ok=True)
    for name in names or list(POSES):
        if VERBOSE:
            print("pose", name)
        apply_pose(rig, base, grip, base_head, tuned_ops(name))
        for view in views:
            if view.startswith("hand"):
                setup_render(os.path.join(out_dir, f"{name}__{view}.png"), "front")
                look_at_hold(rig, yaw_deg=float(view[4:] or -60))
            else:
                setup_render(os.path.join(out_dir, f"{name}__{view}.png"), view)
            bpy.ops.render.render(write_still=True)


def sheet_clip(out_dir, clip, views, step):
    ctx = setup()
    add_sword(ctx[0])
    fbx, keys, fps = clip_keys(CLIPS[clip])
    build_clip(ctx, clip, keys, fps)
    os.makedirs(out_dir, exist_ok=True)
    for f in range(1, bpy.context.scene.frame_end + 1, step):
        bpy.context.scene.frame_set(f)
        for view in views:
            setup_render(os.path.join(out_dir, f"{clip}__f{f:02d}__{view}.png"), view, res=(320, 400))
            bpy.ops.render.render(write_still=True)


def export_clips(names):
    ctx = setup()
    for name in names:
        fbx, keys, fps = clip_keys(CLIPS[name])
        build_clip(ctx, name, keys, fps)
        export_clip(ctx[0], fbx)


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if argv and argv[0] == "render":
        views = parse_flag(argv, "--views", "front,front_right").split(",")
        render_poses(argv[1], argv[2:], views)
    elif argv and argv[0] == "sheet":
        views = parse_flag(argv, "--views", "front_right").split(",")
        step = int(parse_flag(argv, "--step", "1"))
        sheet_clip(argv[1], argv[2], views, step)
    elif argv and argv[0] == "export":
        export_clips(argv[1:] or list(CLIPS))
    elif argv and argv[0] == "check":
        sys.exit(1 if check_all(setup(), argv[1] if len(argv) > 1 else None) else 0)
    elif argv and argv[0] == "tune":
        tune_all(setup(), argv[1:] or list(POSES))
    elif argv and argv[0] == "path":
        ctx = setup()
        for line in path_report(ctx):
            print(line)
        for kind in PATH_KINDS:
            reach = reach_for_spheres(ctx, kind)
            print(f"info reach {kind}: per sample " + " ".join(f"{r / 40.0:.2f}" for r in reach)
                  + f" m -> reachLateralMeters <= {min(reach) / 40.0:.2f} puts the tip on every sphere")
    else:
        print(__doc__)
