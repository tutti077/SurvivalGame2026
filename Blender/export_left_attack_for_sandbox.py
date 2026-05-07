"""
One-shot: build sword + rig + attack clip, export FBX into the s&box project.

Output (under <project>/Assets/models/sword_left/):
  - basic_sword_bind.fbx
  - basic_sword_attack_left.fbx

HOW TO RUN
----------
A) Blender UI: Scripting workspace -> Text -> Open this file -> Run Script.

B) Headless (edit BLENDER_EXE if needed):
   run_sword_export.bat

C) Env override when project moves:
   SURVIVALGAME_BASICS_ROOT = absolute path to folder that contains Assets/
"""

from __future__ import annotations

import os
import sys

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


def _project_root_from_anywhere() -> str:
	env = os.environ.get("SURVIVALGAME_BASICS_ROOT", "").strip()
	if env and os.path.isdir(os.path.join(env, "Assets")):
		return os.path.abspath(env)

	candidates = []
	root = _script_dir()
	candidates.append(root)
	candidates.append(os.getcwd())
	candidates.append(os.path.join(os.getcwd(), "Blender"))
	if bpy.data.filepath:
		candidates.append(os.path.dirname(os.path.abspath(bpy.data.filepath)))

	upward = []
	for c in list(candidates):
		if not c:
			continue
		p = os.path.abspath(c)
		for _ in range(8):
			if p in upward:
				break
			upward.append(p)
			parent = os.path.dirname(p)
			if not parent or parent == p:
				break
			p = parent
	candidates.extend(upward)

	for c in candidates:
		if not c:
			continue
		c = os.path.abspath(c)
		if os.path.basename(c).lower() == "blender":
			maybe_root = os.path.dirname(c)
			if os.path.isdir(os.path.join(maybe_root, "Assets")):
				return maybe_root
		if os.path.isdir(os.path.join(c, "Assets")):
			return c
		if os.path.isdir(os.path.join(c, "Blender")) and os.path.isdir(os.path.join(c, "Assets")):
			return c

	tried = "\n".join(f"  - {os.path.abspath(c)}" for c in candidates if c)
	raise RuntimeError(
		"Could not resolve project root containing Assets/.\n"
		"Set SURVIVALGAME_BASICS_ROOT or save this script under <project>/Blender/.\n"
		f"Tried:\n{tried}"
	)


def _bootstrap_core():
	here = os.path.abspath(_script_dir())
	if here not in sys.path:
		sys.path.insert(0, here)
	import sword_export_core as core

	return core


def main():
	core = _bootstrap_core()
	project_root = _project_root_from_anywhere()
	out_dir, bind_fbx, clip_fbx = core.export_to_assets_models_sword_left(project_root)
	print(f"project_root: {project_root}")
	print(f"Exported: {bind_fbx}")
	print(f"Exported: {clip_fbx}")
	print(f"Next: see {os.path.join(out_dir, 'AFTER_EXPORT_sbox_steps.txt')}")


if __name__ == "__main__":
	main()
