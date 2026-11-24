"""Grapple test gate — makes the grapple suite (player tether + landing skip) run whenever grapple code is touched.

Wired in .claude/settings.json next to combat_test_gate.py / inventory_test_gate.py:
  post   PostToolUse (Edit / Write / MultiEdit): a file in the grapple scope changed -> remember it for this
         session and tell Claude the grapple test is now owed.
  stop   Stop: if this session still owes it, block the stop once with the instructions. (A second stop in a
         row is let through so a dead or busy editor can't trap the session; say so in the reply.)
  clear  `python .claude/hooks/grapple_test_gate.py clear <session_id>` after the in-editor `grapple_test`
         run printed "RESULT ... ALL PASS".
Markers live in .claude/state/grapple-test-pending/<session_id>.
"""
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
STATE_DIR = os.path.join(ROOT, ".claude", "state", "grapple-test-pending")

# The grapple (PlayerMovement.Grapple*.cs partials: rope, player tether, landing skip, tow, ride) and the suite itself.
GRAPPLE_SCOPE = [
    re.compile(r"^code/player/playermovement\.grapple[^/]*\.cs$"),
    re.compile(r"^code/player/tests/grappletest\.cs$"),
]

RUN_STEPS = (
    "Grapple test (CLAUDE.md *Automated tests*): 1) confirm LastCompileSucceeded via sbox MCP editor_status; "
    "2) in play mode (any scene; never stop or restart a play session another prompt is using) run console "
    "`grapple_test`; 3) read_console filter `[GrappleTest]` until the `RESULT` line; 4) fix any FAIL (a real "
    "regression, or a case that no longer matches an intended rule change - say which); 5) on ALL PASS run "
    "`python .claude/hooks/grapple_test_gate.py clear {sid}` and quote the RESULT line in the reply."
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
    return r is not None and any(p.search(r.lower()) for p in GRAPPLE_SCOPE)


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
    emit("PostToolUse", f"Grapple file changed ({rel(path)}). The grapple test is now owed before this reply "
                        "ends. " + RUN_STEPS.format(sid=sid))


def on_stop(data):
    sid = data.get("session_id", "unknown")
    m = marker(sid)
    if not os.path.exists(m) or data.get("stop_hook_active"):
        return
    with open(m, encoding="utf-8") as f:
        files = sorted({line.strip() for line in f if line.strip()})
    reason = ("Grapple files changed this session without a passing grapple test: " + ", ".join(files) + ". "
              + RUN_STEPS.format(sid=sid)
              + " If the sbox editor cannot be reached, restored or is busy with another prompt for the whole turn, "
                "say so in the reply under What's needed from you.")
    print(json.dumps({"decision": "block", "reason": reason}))


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "clear":
        sid = sys.argv[2] if len(sys.argv) > 2 else ""
        m = marker(sid)
        if os.path.exists(m):
            os.remove(m)
            print(f"grapple test cleared for session {sid}")
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
