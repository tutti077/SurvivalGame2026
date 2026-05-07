"""
In-Blender preview only: build mesh + one-bone rig + SwordAtk_Swing_Left keyframes.
Does NOT write FBX (use export_left_attack_for_sandbox.py for that).

HOW TO RUN
----------
Blender -> Scripting -> Open this file -> Run Script.
Animation lives in sword_export_core.py (shared with the exporter).
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


def _bootstrap_core():
	here = os.path.abspath(_script_dir())
	if here not in sys.path:
		sys.path.insert(0, here)
	import sword_export_core as core

	return core


def main():
	core = _bootstrap_core()
	sword, rig, action = core.build_sword_scene()
	print(f"Scene ready: mesh={sword.name}, rig={rig.name}, action={action.name}")
	print(f"Frames {core.ATTACK_CLIP_FRAME_START}-{core.ATTACK_CLIP_FRAME_END}. Export? Run export_left_attack_for_sandbox.py")


if __name__ == "__main__":
	main()
