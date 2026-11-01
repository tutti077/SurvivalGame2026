"""Animation test gate — makes the sword animation checks run whenever animation files are touched.

Wired in .claude/settings.json next to combat_test_gate.py:
  post   PostToolUse (Edit / Write / MultiEdit): a file in the animation scope changed -> remember it for this
         session and tell Claude the animation checks are now owed.
  stop   Stop: if this session still owes them, block the stop once with the instructions. (A second stop in a
         row is let through so a dead editor can't trap the session; say so in the reply.)
  clear  `python .claude/hooks/anim_test_gate.py clear <session_id>` after BOTH checks passed.
Markers live in .claude/state/anim-test-pending/<session_id>.
"""
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
STATE_DIR = os.path.join(ROOT, ".claude", "state", "anim-test-pending")

ANIM_SCOPE = [
    re.compile(r"^code/player/playeranimation[^/]*\.cs$"),
    re.compile(r"^code/player/tests/animtest\.cs$"),
    re.compile(r"^assets/models/player/(animations|animgraphs)/"),
    re.compile(r"^assets/models/player/human_male_sword\.vmdl$"),
    re.compile(r"^blender/scripts/create_sword_anims\.py$"),
    re.compile(r"^blender/scripts/sword_pose_tuning\.json$"),
    re.compile(r"^tools/make_human_sword_assets\.py$"),
]

RUN_STEPS = (
    "Animation checks (CLAUDE.md *Automated tests*): 1) offline: \"C:/Program Files/Blender Foundation/Blender 5.1/"
    "blender.exe\" -b --python Blender/scripts/create_sword_anims.py -- check  must print RESULT 0 failing items "
    "(if poses changed: -- tune <POSE>, then -- export, python Tools/make_human_sword_assets.py, asset_compile the "
    "graph + models/player/human_male_sword.vmdl); 2) confirm LastCompileSucceeded; 3) stop and restart play (a "
    "regenerated graph does not hot-swap); 4) console `anim_test`, read_console filter `[AnimTest]` until RESULT; "
    "5) film any case Mark named with Tools/sbox_film.py and look at it; 6) on ALL PASS run "
    "`python .claude/hooks/anim_test_gate.py clear {sid}` and quote both RESULT lines in the reply."
)


def rel(path):
    try:
        r = os.path.relpath(os.path.abspath(path), ROOT)
    except ValueError:
        return None
    r = r.replace("\\", "/")
    return None if r.startswith("..") else r


def in_scope(path):
    r = rel(path)
    return r is not None and any(p.search(r.lower()) for p in ANIM_SCOPE)


def marker(sid):
    return os.path.join(STATE_DIR, re.sub(r"[^A-Za-z0-9_-]", "_", sid or "unknown"))


def emit(event, context):
    print(json.dumps({"hookSpecificOutput": {"hookEventName": event, "additionalContext": context}}))


def on_post(data):
    tool_input = data.get("tool_input") or {}
    path = tool_input.get("file_path") or tool_input.get("notebook_path")
    if not path or not in_scope(path):
        return
    sid = data.get("session_id", "unknown")
    os.makedirs(STATE_DIR, exist_ok=True)
    with open(marker(sid), "a", encoding="utf-8") as f:
        f.write(rel(path) + "\n")
    emit("PostToolUse", f"Animation file changed ({rel(path)}). The animation checks are now owed before this reply "
                        "ends. " + RUN_STEPS.format(sid=sid))


def on_stop(data):
    sid = data.get("session_id", "unknown")
    m = marker(sid)
    if not os.path.exists(m) or data.get("stop_hook_active"):
        return
    with open(m, encoding="utf-8") as f:
        files = sorted({line.strip() for line in f if line.strip()})
    reason = ("Animation files changed this session without passing animation checks: " + ", ".join(files) + ". "
              + RUN_STEPS.format(sid=sid)
              + " If the sbox editor cannot be reached or restored, say so in the reply under What's needed from you.")
    print(json.dumps({"decision": "block", "reason": reason}))


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "clear":
        sid = sys.argv[2] if len(sys.argv) > 2 else ""
        m = marker(sid)
        if os.path.exists(m):
            os.remove(m)
            print(f"animation checks cleared for session {sid}")
        else:
            print(f"nothing pending for session {sid}")
        return
    try:
        data = json.load(sys.stdin)
    except Exception:  # noqa: BLE001
        return
    if mode == "post":
        on_post(data)
    elif mode == "stop":
        on_stop(data)


if __name__ == "__main__":
    main()
