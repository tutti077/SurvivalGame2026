"""Combat test gate — makes the automated combat test run whenever combat code is touched.

Wired in .claude/settings.json:
  post   PostToolUse (Edit / Write / MultiEdit): a file in the combat scope changed ->
         remember it for this session, run the offline JSON/CSV mirror check, and tell Claude
         the combat test is now owed.
  stop   Stop: if this session still owes a combat test, block the stop once with the
         instructions. (A second stop in a row is let through so a dead editor can't trap the
         session; CLAUDE.md says to pause and tell Mark in that case.)
  clear  `python .claude/hooks/combat_test_gate.py clear <session_id>` after the in-editor
         `combat_test` run printed "RESULT ... ALL PASS".
  check  `python .claude/hooks/combat_test_gate.py check` runs just the offline checks.

Markers live in .claude/state/combat-test-pending/<session_id> (one per session, so parallel
sessions on main never block each other).
"""
import csv
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
STATE_DIR = os.path.join(ROOT, ".claude", "state", "combat-test-pending")

# Everything whose change can alter melee behaviour. Repo-relative, forward slashes, lowercase.
COMBAT_SCOPE = [
    re.compile(r"^code/combat/"),
    re.compile(r"^code/player/playercombat[^/]*\.cs$"),
    re.compile(r"^code/player/melee[^/]*\.cs$"),
    re.compile(r"^code/player/(attackcombattypes|combatrecoverytypes)\.cs$"),
    re.compile(r"^code/player/playeranimation\.hitreaction\.cs$"),
    re.compile(r"^code/player/trainingdummyattacktelegraph\.cs$"),
    re.compile(r"^code/vitals/(vitalsauthority|armormitigation)\.cs$"),
    re.compile(r"^assets/data/(melee_weapon_classes|melee_block_stagger)\.json$"),
    re.compile(r"^combat_timings\.csv$"),
]

RUN_STEPS = (
    "Combat test (CLAUDE.md *Automated tests*): 1) confirm LastCompileSucceeded via sbox MCP "
    "editor_status; 2) in play mode (any scene, testscene1 is the reference) run console "
    "`combat_test`; 3) read_console filter `[CombatTest]` until the `RESULT` line; 4) fix any FAIL "
    "(a real regression, or a test that no longer matches an intended rule change - say which); "
    "5) on ALL PASS run `python .claude/hooks/combat_test_gate.py clear {sid}` and quote the RESULT "
    "line in the reply."
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
    return r is not None and any(p.search(r.lower()) for p in COMBAT_SCOPE)


def marker(sid):
    safe = re.sub(r"[^A-Za-z0-9_-]", "_", sid or "unknown")
    return os.path.join(STATE_DIR, safe)


def load_jsonc(path):
    with open(path, encoding="utf-8") as f:
        text = f.read()
    # Strip // line comments that are not inside strings.
    out = []
    for line in text.splitlines():
        in_str = False
        cut = len(line)
        i = 0
        while i < len(line):
            ch = line[i]
            if ch == "\\" and in_str:
                i += 2
                continue
            if ch == '"':
                in_str = not in_str
            elif not in_str and line.startswith("//", i):
                cut = i
                break
            i += 1
        out.append(line[:cut])
    return json.loads(re.sub(r",(\s*[}\]])", r"\1", "\n".join(out)))


def offline_checks():
    """Static rules that need no editor. Returns a list of problems."""
    problems = []
    json_path = os.path.join(ROOT, "Assets", "data", "melee_weapon_classes.json")
    csv_path = os.path.join(ROOT, "combat_timings.csv")
    try:
        classes = {c["classId"]: c for c in load_jsonc(json_path).get("classes", [])}
    except Exception as e:  # noqa: BLE001
        return [f"melee_weapon_classes.json does not parse: {e}"]
    try:
        with open(csv_path, encoding="utf-8", newline="") as f:
            rows = {r["classId"]: r for r in csv.DictReader(f)}
    except Exception as e:  # noqa: BLE001
        return [f"combat_timings.csv does not read: {e}"]

    for cid in sorted(set(classes) | set(rows)):
        if cid not in rows:
            problems.append(f"class '{cid}' is in melee_weapon_classes.json but not combat_timings.csv")
            continue
        if cid not in classes:
            problems.append(f"class '{cid}' is in combat_timings.csv but not melee_weapon_classes.json")
            continue
        for key, cell in rows[cid].items():
            if key in ("classId", "displayName") or key not in classes[cid]:
                continue
            try:
                if abs(float(cell) - float(classes[cid][key])) > 1e-6:
                    problems.append(f"{cid}.{key}: json {classes[cid][key]} vs csv {cell}")
            except (TypeError, ValueError):
                problems.append(f"{cid}.{key}: unreadable value json {classes[cid][key]!r} / csv {cell!r}")
    return problems


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

    msg = f"Combat code changed ({rel(path)}). The combat test is now owed before this reply ends. " + RUN_STEPS.format(sid=sid)
    problems = offline_checks()
    if problems:
        msg += " OFFLINE CHECK FAILED (combat_timings.csv must mirror melee_weapon_classes.json): " + "; ".join(problems[:8])
    emit("PostToolUse", msg)


def on_stop(data):
    sid = data.get("session_id", "unknown")
    m = marker(sid)
    if not os.path.exists(m) or data.get("stop_hook_active"):
        return
    with open(m, encoding="utf-8") as f:
        files = sorted({line.strip() for line in f if line.strip()})
    reason = "Combat files changed this session without a passing combat test: " + ", ".join(files) + ". " + RUN_STEPS.format(sid=sid)
    reason += " If the sbox editor cannot be reached or restored, say so in the reply under What's needed from you instead."
    print(json.dumps({"decision": "block", "reason": reason}))


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "clear":
        sid = sys.argv[2] if len(sys.argv) > 2 else ""
        m = marker(sid)
        if os.path.exists(m):
            os.remove(m)
            print(f"combat test cleared for session {sid}")
        else:
            print(f"nothing pending for session {sid}")
        return
    if mode == "check":
        problems = offline_checks()
        print("offline combat checks: " + ("OK" if not problems else "FAILED\n  " + "\n  ".join(problems)))
        sys.exit(1 if problems else 0)

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
