"""
One-click pipeline for s&box:
1) Build sword + armature
2) Generate SwordAtk_* actions
3) Push actions to NLA strips
4) Export FBX with animation bake settings

Usage (Blender Scripting):
  - Save this file next to build_basic_sword.py and sword_attack_animations.py
  - Run script

Usage (CLI):
  blender --background --python export_sword_for_sandbox.py
"""

from __future__ import annotations

import importlib.util
import os
from typing import Any

import bpy


def _script_dir() -> str:
    try:
        return os.path.dirname(os.path.realpath(__file__))
    except NameError:
        txt = getattr(getattr(bpy.context, "space_data", None), "text", None)
        if txt and getattr(txt, "filepath", ""):
            return os.path.dirname(os.path.abspath(txt.filepath))
        if bpy.data.filepath:
            return os.path.dirname(os.path.abspath(bpy.data.filepath))
        return os.getcwd()


def _candidate_script_dirs() -> list[str]:
    dirs: list[str] = []

    def add_dir(p: str | None):
        if not p:
            return
        ap = os.path.abspath(p)
        if ap not in dirs:
            dirs.append(ap)

    add_dir(_script_dir())

    # Any saved text blocks in Blender can provide the true script folder.
    for t in bpy.data.texts:
        fp = getattr(t, "filepath", "")
        if fp:
            add_dir(os.path.dirname(fp))

    # Common case: running from project root while scripts live under ./Blender
    cwd = os.getcwd()
    add_dir(cwd)
    add_dir(os.path.join(cwd, "Blender"))

    return dirs


def _resolve_pipeline_files() -> tuple[str, str, str]:
    for d in _candidate_script_dirs():
        build_path = os.path.join(d, "build_basic_sword.py")
        anim_path = os.path.join(d, "sword_attack_animations.py")
        if os.path.isfile(build_path) and os.path.isfile(anim_path):
            return d, build_path, anim_path

    tried = "\n".join(f"  - {d}" for d in _candidate_script_dirs())
    raise RuntimeError(
        "Could not find build_basic_sword.py + sword_attack_animations.py.\n"
        "Save/open this exporter from your project's Blender folder, then run again.\n"
        f"Tried directories:\n{tried}"
    )


def _load_py(path: str, mod_name: str) -> Any:
    spec = importlib.util.spec_from_file_location(mod_name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load module from {path}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _ensure_nla_track(rig: bpy.types.Object, track_name: str = "SwordAttacks"):
    if rig.animation_data is None:
        rig.animation_data_create()

    for t in rig.animation_data.nla_tracks:
        if t.name == track_name:
            return t

    t = rig.animation_data.nla_tracks.new()
    t.name = track_name
    return t


def _clear_old_sword_nla(rig: bpy.types.Object):
    if rig.animation_data is None:
        return
    tracks = list(rig.animation_data.nla_tracks)
    for t in tracks:
        if t.name.startswith("Sword"):
            rig.animation_data.nla_tracks.remove(t)


def _push_actions_to_nla(rig: bpy.types.Object):
    _clear_old_sword_nla(rig)
    track = _ensure_nla_track(rig, "SwordAttacks")

    actions = sorted(
        [a for a in bpy.data.actions if a.name.startswith("SwordAtk_")],
        key=lambda a: a.name,
    )
    if not actions:
        raise RuntimeError("No SwordAtk_* actions found to push to NLA.")

    cursor = 1.0
    for action in actions:
        start, end = action.frame_range
        length = max(1.0, end - start)
        strip = track.strips.new(action.name, int(cursor), action)
        strip.action_frame_start = start
        strip.action_frame_end = end
        strip.frame_start = cursor
        strip.frame_end = cursor + length
        strip.blend_type = "REPLACE"
        cursor = strip.frame_end + 2.0

    # Keep first action active in Action Editor preview.
    if rig.animation_data:
        rig.animation_data.action = actions[0]

    bpy.context.scene.frame_start = 1
    bpy.context.scene.frame_end = int(cursor + 2.0)


def _select_for_export(mesh_obj: bpy.types.Object, rig_obj: bpy.types.Object):
    bpy.ops.object.select_all(action="DESELECT")
    rig_obj.select_set(True)
    mesh_obj.select_set(True)
    bpy.context.view_layer.objects.active = rig_obj


def _export_fbx(
    filepath: str,
    *,
    use_nla_strips: bool,
    use_all_actions: bool,
):
    bpy.ops.export_scene.fbx(
        filepath=filepath,
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        use_mesh_modifiers=True,
        add_leaf_bones=False,
        use_armature_deform_only=True,
        bake_anim=True,
        bake_anim_use_nla_strips=use_nla_strips,
        bake_anim_use_all_actions=use_all_actions,
        bake_anim_force_startend_keying=True,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
        axis_forward="-Z",
        axis_up="Y",
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL",
    )


def main():
    root, build_path, anim_path = _resolve_pipeline_files()

    build_mod = _load_py(build_path, "build_basic_sword")
    anim_mod = _load_py(anim_path, "sword_attack_animations")

    built = build_mod.build_basic_sword()
    if isinstance(built, tuple):
        sword, rig = built
    else:
        sword = built
        rig = bpy.data.objects.get("BasicSwordRig")
        if rig is None:
            raise RuntimeError("Rig not found after build. Expected BasicSwordRig.")

    actions = anim_mod.add_attack_actions(sword)
    if not actions:
        raise RuntimeError("No actions generated by add_attack_actions().")

    project_root = os.path.dirname(root)
    assets_models = os.path.join(project_root, "Assets", "models")
    if os.path.isdir(assets_models):
        out_dir = assets_models
    else:
        out_dir = root
    os.makedirs(out_dir, exist_ok=True)

    _select_for_export(sword, rig)

    # 1) Combined export (all clips baked from NLA/all-actions)
    _push_actions_to_nla(rig)
    combined_fbx = os.path.join(out_dir, "basic_sword_sandbox.fbx")
    _export_fbx(
        combined_fbx,
        use_nla_strips=True,
        use_all_actions=True,
    )

    # 2) Per-clip exports (most reliable for picky importers)
    clips_dir = os.path.join(out_dir, "sword_clips")
    os.makedirs(clips_dir, exist_ok=True)
    if rig.animation_data is None:
        rig.animation_data_create()
    for action in actions:
        rig.animation_data.action = action
        bpy.context.scene.frame_start = int(action.frame_range[0])
        bpy.context.scene.frame_end = int(action.frame_range[1])
        clip_fbx = os.path.join(clips_dir, f"{action.name}.fbx")
        _export_fbx(
            clip_fbx,
            use_nla_strips=False,
            use_all_actions=False,
        )

    print(f"Exported combined FBX: {combined_fbx}")
    print(f"Exported clips folder: {clips_dir}")
    print(f"Actions: {', '.join(a.name for a in actions)}")


if __name__ == "__main__":
    main()

