---
name: bug-test-routine
description: The BUG TEST ROUTINE — reproduce a reported defect in the live s&box editor, instrument the system with logging, fix, and re-run until the fix is proven; hammers tests autonomously, opening / closing scenes and starting / stopping play mode as needed. Triggers on "bug test routine", "bug test <feature>", "hammer tests on <feature>".
---

# Bug test routine

Read `.claude/ROUTINES.md` first — the shared contract applies.

## Goal

Turn "X is wrong" into a passing, repeatable check: state the expected behaviour as an assertion,
build a reproduction in the editor, instrument until the cause is visible, fix the cause (not the
symptom), and re-run the reproduction until the assertion holds on three consecutive runs. Mark
has granted full access to open / close / switch scenes and to start / stop play mode for this.

## Brief format

> "bug test routine on **<feature>**: **<symptom>** [expected: …] [scene: …] [repro steps: …]"

| Field | Required | Default |
|-------|----------|---------|
| feature | yes | — (stop) |
| symptom | yes | — (stop). May be a casual analogy; take it literally first (grep for the words) before forming your own theory |
| expected behaviour | no | derive from `AGENTS.md` / the owning component's doc comments; state it |
| scene | no | `testscene1` for player / build / entity features; `dungeonBoxTest`, `caveVertTest`, `lightTestScene` by subsystem |
| repro steps | no | design them from the feature's input path (console command, key, placement) |
| multiplayer | no | required when the feature involves `[Sync]`, `[Rpc.*]`, `*Authority` or host/client flow |
| cycle budget | no | 12 edit-run cycles, then report state |

## In scope

- The owning system of the feature (resolve through the CLAUDE.md layout table and umbrella
  owners) and its direct dependencies.
- Temporary logging anywhere needed to see the cause.
- Scene tabs, play mode, console commands, spawning instances, positioning the pawn, editor camera.
- The fix itself, inside the owner (umbrella rule: player behaviour is fixed on `Player*.cs`,
  host rules on `*Authority`).
- A permanent, cheap debug aid if the bug class will recur (a `ConVar`-gated log or ring overlay
  like `campRings`) — note it in the report.

## Out of scope

- Redesigning the feature. If the cause is a design flaw, fix the defect minimally and write the
  redesign as a proposal.
- Saving scenes that had Mark's unsaved changes. Check `SceneHasUnsavedChanges` before touching a
  tab; play-mode edits are discarded on `play_stop` anyway.
- Committing.

## Technologies — sbox MCP

Load the sbox tools (`ToolSearch "select:mcp__sbox__…"`) and use `call_tools` for batches.

| Need | Tool |
|------|------|
| Editor / compile state | `editor_status` (`IsCompiling`, `LastCompileSucceeded`, `LastCompileErrors`), `compile_status` |
| Scene lifecycle | `list_scenes`, `open_scene`, `switch_scene`, `close_scene` — a scene edited on disk needs close then open; `switch_scene` before any camera / screenshot tool because another tab may be fronted |
| Play | `play_start`, `play_pause`, `play_stop` |
| Drive the game | `console_command` (`ConCmd`s like `dungeon_regen`, `campRings`, `cave_tp`), `set_game_object` (position / angles), `find_game_objects`, `get_game_object`, `scene_trace` |
| Observe | `read_console` (filter on your tag), `camera_screenshot` (play mode), `editor_camera_screenshot` (edit mode), `ui_panel_dump` / `ui_screenshot` for HUD bugs, `log_info` to stamp the console between steps |
| Multiplayer | `network_start_hosting`, `network_spawn_instance`, `network_status`, `network_disconnect`, `network_migrate_to_new_instance` for host handoff bugs |
| Assets | `asset_compile`, `asset_info` when the bug is a prefab / vmdl / json problem |

Quirks: every screenshot inside one `call_tools` batch renders the same frame — screenshot in a
later call; the first play frame may lack a freshly written shader; `TerrainTestFlyCamera` overrides
the camera transform each frame (remove it at runtime to frame a shot).

## Logging convention

- Tag every temporary log `[BUGTEST:<feature>]` so it can be filtered and found for removal.
- Log state transitions and the inputs to the decision that goes wrong, not every frame. If a
  per-frame log is unavoidable, throttle it (`TimeSince` ≥ 0.25 s) or gate it on a `ConVar`.
- Log on both sides of a host/client boundary with the side in the message (`host` / `client`).
- **Remove every `[BUGTEST:…]` line before reporting** (grep for the tag — zero hits), unless it
  was promoted to a permanent `ConVar`-gated aid.

## Procedure

1. **Restate**: feature, symptom in Mark's words, the assertion ("after X, Y must be Z within N s"),
   chosen scene and repro steps, whether multiplayer is needed.
2. **Locate** the owner and read the code path from input to the observed output. Grep for the
   words in Mark's symptom first — his analogies are usually literal diagnoses.
3. **Hypotheses**: write two or three, each with the single log line that would confirm it.
4. **Reproduce unfixed**: instrument, compile (`editor_status`), reload the scene, `play_start`,
   run the steps, `read_console`. If the symptom does not reproduce, change one variable at a time
   (scene, timing, multiplayer, pawn state) until it does — a bug that cannot be reproduced cannot
   be declared fixed. Record the first reproducing setup; it is the regression test.
5. **Diagnose** from the logs; narrow until one line of code is responsible. Capture the evidence
   (console excerpt, screenshot).
6. **Fix** the cause inside the owner. Deprecate cleanly if the fix replaces a path.
7. **Re-run** the exact reproducing setup. Pass = assertion holds on **three consecutive runs**
   (fresh `play_start` each time; for multiplayer, host and spawned client both). Any failure
   resets the count and goes back to step 5.
8. **Regression sweep**: run the feature's neighbouring paths once (the other weapon class, the
   other door direction, a second entity) to check the fix did not move the bug.
9. **Cleanup**: remove `[BUGTEST:…]` logs, `play_stop`, close tabs you opened, confirm
   `LastCompileSucceeded` and zero new console errors.
10. **Report**.

Stop conditions: cycle budget spent, or three cycles in a row with no new information. Then
report the exact state (what reproduces, what is ruled out, the current best hypothesis) so Mark
can answer one question and the routine can resume.

## Quality bar

- The assertion is written down before the first run and unchanged at the end.
- The bug was seen failing before the fix and passing three times after, with console evidence.
- The fix lives on the owning component; no bandage, no second validation path, no camera /
  pawn-root rotation writes from combat.
- No temporary logs remain; compile clean; editor restored.

## Report

- Assertion, repro setup (scene, steps, multiplayer or not).
- Root cause in two sentences with file:line.
- The fix (files changed) and why it addresses the cause.
- Evidence: failing excerpt before, passing excerpts after (3), screenshots if visual.
- Regression sweep result.
- Anything promoted to a permanent debug aid.
- **Out of scope, noticed**.
- Standard footer: build label (patch bumped) + **What's needed from you** (an in-game confirm on
  the real world scene, a decision on any proposal).
