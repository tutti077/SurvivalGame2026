# Melee debug arc draw — revert snapshot (v0.3.244)

Delete this file when the change is approved.

## Issues fixed

1. Yaw bank used live phase for every spoke → late-phase red on early arc geometry when rotating.
2. Per-spoke spatial color only → early arc degrees always blue/yellow behind later live phase when rotating.

## Fix (hybrid)

- **Progress reveal** (`strokeProgressOnly: true`): color = live `attackState` when the spoke first appears.
- **Yaw bank** (`strokeProgressOnly: false`): live phase color, but only arc degrees where `ArcDegreeMatchesPhase` (stroke position matches current timed phase).

Helper: `MeleeAttackPath.ArcDegreeMatchesPhase`.

## v0.3.239 — no yaw fan banks

- Removed yaw substeps that redrew full revealed fans while rotating.
- `DrawnArcStepIndices` per swing: exactly one line + sphere per arc step index.
- Spokes only added when stroke progress reveals a new step.

## v0.3.244 — rotation by abs degrees turned

- `AbsYawDegreesTurned` += |Δyaw| each frame; spokes = floor(abs / 5°), cap 72 for 360°.
- Removed per-frame `spinning` gate (was skipping spokes on slow-turn frames).
- Arc samples always on stroke progress (30 @ 150°).

## Revert

Restore `DrawAttackArcFanSpokesAtYaw` + yaw banking loop in `DrawAttackArcFanSpokesUniform`.
Remove `DrawnArcStepIndices` / `ArcDegreeToStepIndex` dedup.
