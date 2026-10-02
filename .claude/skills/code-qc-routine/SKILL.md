---
name: code-qc-routine
description: The CODE QC ROUTINE — audit a named code scope (a system, folder or component set) for bloat, dead paths and per-frame / per-tick work that can break playability; apply behaviour-preserving fixes, propose the rest. Triggers on "code qc routine", "qc the code in <scope>", "check <system> for bloat".
---

# Code QC routine

Read `.claude/ROUTINES.md` first — the shared contract applies.

## Goal

Parse every file in the given scope and find two classes of problem:

1. **Bloat** — code that costs reading time and risk without changing behaviour: dead types,
   zero-caller methods, one-line facade wrappers, duplicated helpers, settings that no longer run
   code, parallel systems that duplicate an umbrella owner, leftover debug paths.
2. **Playability hazards** — work on a hot path (`OnUpdate`, `OnFixedUpdate`, `OnPreRender`,
   per-entity ticks, per-chunk loops, RPC handlers) that scales badly, allocates every frame,
   traces or scans the scene repeatedly, re-validates what a cached value already answers, or can
   throw and kill the frame.

Then fix what can be fixed without changing behaviour and write up everything else.

## Brief format

> "code qc routine on **<scope>** [focus: perf | bloat | both] [fix | report only]"

| Field | Required | Default |
|-------|----------|---------|
| scope | yes | — (stop). Accepts a folder (`Code/Entity/`), a system name (resolve through the CLAUDE.md layout table), or a component list |
| focus | no | both |
| mode | no | **fix safe items, propose the rest** |
| description of the worry | no | none — Mark may say "the entities feel laggy when 30 are up"; treat it as the first hypothesis |

## In scope

- Every `.cs` under the resolved scope, plus the direct callers / callees needed to prove a path is
  dead or a value is already cached (read them, do not edit them unless the fix requires removing a
  call site — deprecate cleanly).
- `Assets/data/*.json` only where a setting the scope reads is being removed.
- `AGENTS.md` / subsystem docs when a removal changes what they describe.

## Out of scope

- Behaviour changes: anything that alters what the player or host sees, timings, numbers, or
  authority. Those are **proposals**, never applied.
- Style-only rewrites (naming, formatting, comment polishing). Not bloat.
- Files outside the scope, except call-site removals tied to an in-scope deletion.

## Technologies

- `Grep` / `Glob` for the sweep. `Read` the full file for every hit — a pattern hit is a lead,
  not a finding.
- sbox MCP `editor_status` / `compile_status` after each batch of edits (`LastCompileSucceeded`,
  `LastCompileErrors`). `play_start` + `read_console` for a smoke run of the touched scene;
  `play_stop` afterwards.
- No new tooling, no analyzers added to the project.

## Sweep patterns

Run each over the scope, then read the surrounding method before judging.

| Hazard | Pattern leads |
|--------|---------------|
| Scene scans per frame | `Scene.GetAllComponents`, `Scene.GetAllObjects`, `Components.GetAll`, `GetInChildren`, `GetInDescendants`, `GameObject.Find`, `FindByName` inside `OnUpdate` / `OnFixedUpdate` / `OnPreRender` / `Tick` |
| Traces per frame | `Scene.Trace`, `PhysicsWorld`, `Scene.FindInPhysics` in the same methods without an interval gate |
| Allocation per frame | `new List`, `new Dictionary`, `.ToList()`, `.ToArray()`, `.Where(`, `.Select(`, `.OrderBy(`, string interpolation / `+` on strings, `string.Format` in hot paths |
| Logging per frame | `Log.Info` / `Log.Warning` without a throttle in hot paths |
| Network churn | `[Sync]` properties assigned every frame with an unchanged value; `[Rpc.*]` / `[Broadcast]` called from `OnUpdate` |
| Re-validation | the same check done on client preview and again every frame on the host (see CLAUDE.md "Infrequent validation") |
| Throw risk | `.First(`, `.Single(`, `[0]` on possibly-empty collections, `Components.Get<T>()` deref without null check, `int.Parse` on data |
| Timers | `Time.Now` arithmetic where `TimeSince` / `TimeUntil` or `RealTimeSince` fits; unbounded `while` |
| Bandages | `Ensure*`, `AutoWire*`, `Bootstrap*`, `Components.Create<T>` on other objects at start |
| Dead paths | types / methods with zero callers (`grep` the name across `Code/` and `Editor/`), `[Property]` with no read site, `CloneForGenerate` copies of removed settings |
| Umbrella violations | movement / vitals / combat / animation / augment behaviour living outside `Code/Player/Player*.cs` |
| Unit double-scale | a value named `…Meters` multiplied twice, or a mesh AABB / collider scale run through `MetersToEngine` again |

## Severity

- **Blocker** — can throw on a hot path, unbounded growth, O(entities²) per frame, RPC per frame.
- **High** — per-frame scan / trace / allocation that scales with world or entity count.
- **Medium** — redundant validation, cheap per-frame allocation, unthrottled logs.
- **Low** — dead code, wrappers, stale settings, doc drift.

## Procedure

1. **Restate** the brief; resolve the scope to a file list and print it.
2. **Map hot paths**: for each file list the methods that run per frame / tick / RPC and what
   they touch. This map is the backbone of the report.
3. **Sweep** with the pattern table; read every hit in context; record findings with
   file:line, severity, class (bloat / hazard), evidence (why it is on a hot path, how it scales).
4. **Classify each finding**: *safe fix* (behaviour-identical: delete dead code, cache a value
   that cannot change between frames, hoist an allocation, add an interval gate that preserves the
   same observable result) or *proposal* (anything else).
5. **Apply safe fixes** when mode allows. One concern per edit; keep diffs small. Deprecate
   cleanly — remove the whole old path, update docs.
6. **Compile** via `editor_status`; fix errors you introduced. Smoke-run the scene that exercises
   the scope (`play_start`, wait, `read_console` for exceptions, `play_stop`).
7. **Report**.

Stop early and report if the scope resolves to more than ~40 files — say which sub-scope you
recommend first.

## Quality bar

- Every finding has file:line and a one-sentence reason it matters for playability or readability.
- Every applied fix is behaviour-identical and compiles; the smoke run shows no new console errors.
- No finding is reported from a pattern hit alone.
- Deleted code has zero remaining callers (grep shown in the report).

## Report

- Scope file list and the hot-path map.
- Findings table: severity · class · file:line · summary · status (fixed / proposed).
- Proposals: for each, the change, the behaviour it would alter, and the cheat or boundary
  failure the current check prevents (if any).
- Compile + smoke result.
- **Out of scope, noticed**.
- Standard footer: build label (patch bumped) + **What's needed from you** (proposals to approve,
  scenes to play-test).
