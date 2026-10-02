@echo off
rem Import an artist pack (.blend) into the game. Usage:
rem   import_artist_pack.bat <pack.blend> <category> <name> [key=value ...]
rem   import_artist_pack.bat ..\..\Assets\models\weapons\blenderfiles\sword1pack.blend weapons sword_reforged head=-x length=1.0 texture=256
rem See import_artist_pack.py for the options. Then author the prefab / profile that uses the model.
set BLENDER="C:\Program Files\Blender Foundation\Blender 5.1\blender.exe"
%BLENDER% -b "%~1" --python "%~dp0import_artist_pack.py" -- %3 %4 %5 %6 %7 %8 %9
pause
