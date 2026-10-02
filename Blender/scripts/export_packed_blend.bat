@echo off
rem Export a packed single-mesh .blend into the game. Usage:
rem   export_packed_blend.bat <file.blend> <category> <model_name> <material_name> [fbx_scale] [texture_size]
rem   export_packed_blend.bat ..\..\Assets\models\weapons\blenderfiles\axepack1.blend weapons axe_reforged axe_reforged 1.0 256
rem Then hand-write / keep the .vmdl (import_scale 0.4 for weapons) and .vmat (shaders/pixel_lit.shader) beside the outputs.
set BLENDER="C:\Program Files\Blender Foundation\Blender 5.1\blender.exe"
%BLENDER% -b "%~1" --python "%~dp0export_packed_blend.py" -- %2 %3 %4 %5 %6
pause
