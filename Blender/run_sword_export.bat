@echo off
setlocal EnableDelayedExpansion

REM Sword FBXs go to <project>\Assets\models\sword_left\
REM Blender is often NOT on PATH — we probe common installs before giving up.

set "SCRIPT_DIR=%~dp0"
set "EXPORT_SCRIPT=%SCRIPT_DIR%export_left_attack_for_sandbox.py"

REM Project root = parent folder of Blender (folder that contains Assets\)
pushd "%SCRIPT_DIR%.."
set "SURVIVALGAME_BASICS_ROOT=%CD%"
popd

REM If you already set BLENDER_EXE in this cmd window, we use it as-is.
if defined BLENDER_EXE goto run

where blender >nul 2>&1
if !ERRORLEVEL! equ 0 set "BLENDER_EXE=blender" & goto run

REM Typical Windows installs (newer named folders first helps a bit).
set "BPF=%ProgramFiles%\Blender Foundation"
for %%V in ("Blender 5.5" "Blender 5.4" "Blender 5.3" "Blender 5.2" "Blender 5.1" "Blender 5.0" "Blender 4.4" "Blender 4.3" "Blender 4.2" "Blender 4.1" "Blender 4.0" "Blender 3.6") do (
  if exist "!BPF!\%%~V\blender.exe" (
    set "BLENDER_EXE=!BPF!\%%~V\blender.exe"
    goto run
  )
)

echo/
echo ===== run_sword_export.bat: could not find blender.exe =====
echo Tried PATH, then "!BPF!\Blender *\".
echo/
echo Fix one of these:
echo   1^) Add Blender to PATH, OR
echo   2^) Set BLENDER_EXE before running ^(quoted path OK^:^)
echo        set BLENDER_EXE=C:\Program Files\Blender Foundation\Blender 5.1\blender.exe
echo      then run this batch again OR
echo   3^) Drag your blender.exe onto this bat ^(unsupported^) — use ^(2^) instead.
echo/
echo Resolved project SURVIVALGAME_BASICS_ROOT=!SURVIVALGAME_BASICS_ROOT!
echo/
pause
exit /b 1

:run
if not defined BLENDER_EXE (
  echo BLENDER_EXE is empty.
  pause
  exit /b 1
)
echo SURVIVALGAME_BASICS_ROOT=!SURVIVALGAME_BASICS_ROOT!
echo Using: "!BLENDER_EXE!"
"%BLENDER_EXE%" --background --python "%EXPORT_SCRIPT%"
set "EXITCODE=!ERRORLEVEL!"
if !EXITCODE! neq 0 (
  echo Blender exited !EXITCODE!
  pause
  exit /b !EXITCODE!
)
echo Done. Check Assets\models\sword_left\ for .fbx and AFTER_EXPORT_sbox_steps.txt
endlocal
exit /b 0
