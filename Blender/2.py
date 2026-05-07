"""
Four sword attack clips for Blender 3.x / 4.x (works with BasicSword from build_basic_sword.py).

Animations (each as its own Action, timeline starts at frame 1):
  SwordAtk_Swing_Left           — tip forward, rotate wind-up right, then rotate strike left
  SwordAtk_Swing_Right          — mirrored rotation-driven strike
  SwordAtk_Slash_UpToDown       — roll blade so edge sits in the chop plane, then strike down
  SwordAtk_Stab_Forward         — blade horizontal along +Y, thrust forward (+Y)

Horizontal swings are rotation-driven after the forward tip: wind-up rotation opposite the strike,
then committed strike rotation through the attack phase.

Rest pose: rotation (0,0,0), location (0,0,0); blade points +Z, origin at guard. Each clip
ends back at this pose after a short recovery.

Blade mesh is widest on local X and thin on local Y; the Z roll before the chop lines that
thin edge up with the vertical swing plane so the strike reads as a cut, not the flat.

Run in Blender Scripting (▶) or:
  blender --background --python path/to/sword_attack_animations.py

Save this file to disk next to build_basic_sword.py before running from the Text Editor — otherwise
Blender may not resolve `__file__` and cannot auto-load the sword. If BasicSword is already in the
scene, the build script is skipped.

If BasicSword is not in the scene, the script loads build_basic_sword.py from the same folder
and creates it first.

FBX: enable “All Actions” (or bake NLA) so each Action exports as its own clip.
"""

from __future__ import annotations

import importlib.util
import math
import os
import sys
from mathutils import Euler, Quaternion, Vector

import bpy


def _get_script_directory() -> str:
    """
    Directory containing this file. In Blender's Text Editor, `__file__` is often missing until
    the script is saved to disk — fall back to the active text block path or the .blend folder.
    """
    try:
        here = os.path.realpath(__file__)
    except NameError:
        here = ""
    if here:
        return os.path.dirname(here)
    try:
        sp = bpy.context.space_data
        text = getattr(sp, "text", None) if sp is not None else None
        if text is not None and getattr(text, "filepath", ""):
            return os.path.dirname(os.path.abspath(text.filepath))
    except (AttributeError, RuntimeError):
        pass
    blend = bpy.data.filepath
    if blend:
        return os.path.dirname(os.path.abspath(blend))
    cwd = os.getcwd()
    return cwd if cwd else "."


def _quat_pitch_x(pitch_rad: float) -> Quaternion:
    return Euler((pitch_rad, 0.0, 0.0), "XYZ").to_quaternion()


def _quat_world_z_after_pitch_x(pitch_rad: float, world_z_rad: float) -> Quaternion:
    """
    Apply pitch around X, then rotate around world +Z (vertical). Intrinsic euler Z after a
    large X is not world yaw — it spins around the tipped blade — so we compose quaternions.
    """
    q_pitch = _quat_pitch_x(pitch_rad)
    h = world_z_rad * 0.5
    q_wz = Quaternion((math.cos(h), 0.0, 0.0, math.sin(h)))
    return q_wz @ q_pitch


def _quat_world_y_after_pitch_x(pitch_rad: float, world_y_rad: float) -> Quaternion:
    """
    Apply pitch around X, then rotate around world +Y.
    Useful when rig/bone orientation makes world-Z yaw feel like the wrong attack axis.
    """
    q_pitch = _quat_pitch_x(pitch_rad)
    h = world_y_rad * 0.5
    q_wy = Quaternion((math.cos(h), 0.0, math.sin(h), 0.0))
    return q_wy @ q_pitch


def _load_build_module():
    script_dir = _get_script_directory()
    path = os.path.join(script_dir, "build_basic_sword.py")
    if not os.path.isfile(path):
        return None
    spec = importlib.util.spec_from_file_location("build_basic_sword", path)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def ensure_basic_sword():
    ob = bpy.data.objects.get("BasicSword")
    if ob is not None:
        return ob
    sword_build = _load_build_module()
    if sword_build is None:
        raise RuntimeError(
            f"No object named BasicSword and build_basic_sword.py was not found in "
            f'"{_get_script_directory()}". Save this script next to build_basic_sword.py '
            "or load/merge a .blend that contains BasicSword, then run again."
        )
    built = sword_build.build_basic_sword()
    if isinstance(built, tuple):
        return built[0]
    return built


def _resolve_anim_target(sword: bpy.types.Object):
    rig = bpy.data.objects.get("BasicSwordRig")
    if rig is None and sword.parent is not None and sword.parent.type == "ARMATURE":
        rig = sword.parent

    if rig is not None and rig.type == "ARMATURE" and rig.pose is not None:
        bone_name = "SwordBone"
        if rig.pose.bones.get(bone_name) is not None:
            return {"kind": "bone", "owner": rig, "bone": bone_name}

    return {"kind": "object", "owner": sword}


def _clear_sword_generated_actions(target) -> None:
    prefix = "SwordAtk_"
    owner = target["owner"]
    if owner.animation_data is not None:
        owner.animation_data_clear()
    for action in list(bpy.data.actions):
        if action.name.startswith(prefix):
            bpy.data.actions.remove(action)


def _euler_to_quat(euler_xyz: tuple[float, float, float]) -> Quaternion:
    return Euler(euler_xyz, "XYZ").to_quaternion()


def _set_pose(
    target,
    *,
    euler_xyz: tuple[float, float, float] | None = None,
    quat: Quaternion | None = None,
    location: Vector,
) -> None:
    owner = target["owner"]
    if target["kind"] == "bone":
        pb = owner.pose.bones[target["bone"]]
        pb.rotation_mode = "QUATERNION"
        if quat is not None:
            pb.rotation_quaternion = quat
        elif euler_xyz is not None:
            pb.rotation_quaternion = _euler_to_quat(euler_xyz)
        else:
            pb.rotation_quaternion = Quaternion((1, 0, 0, 0))
        pb.location = location
        return

    sword = owner
    sword.rotation_mode = "QUATERNION"
    if quat is not None:
        sword.rotation_quaternion = quat
    elif euler_xyz is not None:
        sword.rotation_quaternion = _euler_to_quat(euler_xyz)
    else:
        sword.rotation_quaternion = Quaternion((1, 0, 0, 0))
    sword.location = location


def _insert_pose_keyframes(target, frame: int) -> None:
    owner = target["owner"]
    if target["kind"] == "bone":
        pb = owner.pose.bones[target["bone"]]
        pb.keyframe_insert(data_path="location", frame=frame)
        pb.keyframe_insert(data_path="rotation_quaternion", frame=frame)
        return

    owner.keyframe_insert(data_path="location", frame=frame)
    owner.keyframe_insert(data_path="rotation_quaternion", frame=frame)


def _set_fcurve_interpolation(action: bpy.types.Action, mode: str) -> None:
    # Blender API differs across versions (legacy fcurves vs layered actions).
    fcurves = getattr(action, "fcurves", None)
    if fcurves is None:
        return
    if not fcurves:
        return
    allowed = {"CONSTANT", "LINEAR", "BEZIER"}
    use = mode if mode in allowed else "BEZIER"
    for fc in fcurves:
        for kp in fc.keyframe_points:
            kp.interpolation = use


def _make_action(
    target,
    name: str,
    poses: list[tuple[int, tuple[float, float, float] | Quaternion, Vector]],
    interpolation: str,
) -> bpy.types.Action:
    """
    poses: (frame, rotation euler XYZ or Quaternion, location)
    """
    owner = target["owner"]
    prev_action = None
    if owner.animation_data and owner.animation_data.action:
        prev_action = owner.animation_data.action

    action = bpy.data.actions.new(name=name)
    if not owner.animation_data:
        owner.animation_data_create()
    owner.animation_data.action = action

    for frame, rot, loc in poses:
        bpy.context.scene.frame_set(frame)
        if isinstance(rot, Quaternion):
            _set_pose(target, quat=rot, location=loc)
        else:
            _set_pose(target, euler_xyz=rot, location=loc)
        _insert_pose_keyframes(target, frame)

    _set_fcurve_interpolation(action, interpolation)

    owner.animation_data.action = None
    if prev_action:
        owner.animation_data.action = prev_action
    return action


def add_attack_actions(
    sword: bpy.types.Object,
    *,
    fps: int = 30,
    # Horizontal: tip to flat, then rotate via world-Z yaw (no lateral slide).
    swing_horizontal_pitch_deg: float = 90.0,
    swing_arc_deg: float = 40.0,
    swing_windup_deg: float = 24.0,
    # Chop: blade rolls around Z so local Y (thin/edge) lies in the vertical cut plane,
    # then X rotates for the overhead strike. Tune chop_roll_z_deg if the edge reads wrong.
    chop_roll_z_deg: float = 90.0,
    chop_aim_x_deg: float = 24.0,
    chop_aim_y_deg: float = 6.0,
    chop_down_x_deg: float = 74.0,
    stab_pull: float = 0.22,
    stab_reach: float = 0.52,
) -> list[bpy.types.Action]:
    """
    Add four actions; sword should be at rest (identity) before calling if you rely on
    default rest — this function temporarily assigns actions and restores nothing except
    clearing active action on the object; it resets pose to rest at the end.
    """
    target = _resolve_anim_target(sword)
    _clear_sword_generated_actions(target)

    scene = bpy.context.scene
    scene.render.fps = fps

    R = math.radians
    rest = Vector((0, 0, 0))
    rest_q = Quaternion((1, 0, 0, 0))

    px = -R(swing_horizontal_pitch_deg)
    arc = R(swing_arc_deg)
    wind = R(swing_windup_deg)

    neutral = (0.0, 0.0, 0.0)
    q_rest = Quaternion((1.0, 0.0, 0.0, 0.0))

    # Horizontal pitch only (blade flat), then world-Y rotation for wind-up + strike.
    horizontal_ready = _quat_pitch_x(px)
    # Left swing (original feel): strike +arc, wind-up opposite (−wind).
    strike_left_q = _quat_world_y_after_pitch_x(px, arc)
    wind_left_q = _quat_world_y_after_pitch_x(px, -wind)
    # Right swing: mirrored horizontal angles only on this clip (−arc strike, +wind wind-up).
    strike_right_q = _quat_world_y_after_pitch_x(px, -arc)
    wind_right_q = _quat_world_y_after_pitch_x(px, wind)

    rz = R(chop_roll_z_deg)
    chop_windup = (R(chop_aim_x_deg), R(chop_aim_y_deg), rz)
    chop_hit = (-R(chop_down_x_deg), R(chop_aim_y_deg), rz)

    # Stab: local +Z → +Y via -90° X; thrust along +Y.
    stab_q = _euler_to_quat((R(-90.0), 0.0, 0.0))
    stab_back = Vector((0.0, -stab_pull, 0.0))
    stab_fwd = Vector((0.0, stab_reach, 0.0))

    actions: list[bpy.types.Action] = []

    # Frames: rest → wind-up (opposite) → neutral horizontal → strike → rest.
    actions.append(
        _make_action(
            target,
            "SwordAtk_Swing_Left",
            [
                (1, q_rest, rest),
                (8, wind_left_q, rest),
                (13, horizontal_ready, rest),
                (26, strike_left_q, rest),
                (38, q_rest, rest),
            ],
            "BEZIER",
        )
    )
    actions.append(
        _make_action(
            target,
            "SwordAtk_Swing_Right",
            [
                (1, q_rest, rest),
                (8, wind_right_q, rest),
                (13, horizontal_ready, rest),
                (26, strike_right_q, rest),
                (38, q_rest, rest),
            ],
            "BEZIER",
        )
    )
    actions.append(
        _make_action(
            target,
            "SwordAtk_Slash_UpToDown",
            [
                (1, neutral, rest),
                (12, chop_windup, rest),
                (27, chop_hit, rest),
                (37, neutral, rest),
            ],
            "BEZIER",
        )
    )
    actions.append(
        _make_action(
            target,
            "SwordAtk_Stab_Forward",
            [
                (1, stab_q, stab_back),
                (14, stab_q, stab_fwd),
                (24, q_rest, rest),
            ],
            "BEZIER",
        )
    )

    # Make preview easier: keep first attack active on the animated owner.
    owner = target["owner"]
    if not owner.animation_data:
        owner.animation_data_create()
    if actions:
        owner.animation_data.action = actions[0]

    # Restore to rest pose at frame 1.
    bpy.context.scene.frame_set(1)
    if target["kind"] == "bone":
        pb = target["owner"].pose.bones[target["bone"]]
        pb.rotation_mode = "QUATERNION"
        pb.rotation_quaternion = rest_q
        pb.location = rest
    else:
        sword.rotation_mode = "QUATERNION"
        sword.rotation_quaternion = rest_q
        sword.location = rest

    scene.frame_start = 1
    scene.frame_end = 42

    return actions


def main() -> None:
    import traceback

    try:
        sword = ensure_basic_sword()
        actions = add_attack_actions(sword)
        target = _resolve_anim_target(sword)
        where = (
            f'rig "{target["owner"].name}" bone "{target["bone"]}"'
            if target["kind"] == "bone"
            else f'object "{target["owner"].name}"'
        )
        print(f'Animated "{sword.name}" with {len(actions)} actions on {where}:')
        for a in actions:
            r = a.frame_range
            print(f"  - {a.name}  frames {int(r[0])}-{int(r[1])}")
    except Exception:
        print("sword_attack_animations.py failed:")
        traceback.print_exc()
        raise


if __name__ == "__main__":
    main()
