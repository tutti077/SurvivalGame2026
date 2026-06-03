# Agent instructions — SurvivalGame2026

Read this at the start of work on **player pawn** gameplay or when touching movement, pools, or combat.

## Commandment #1 — Player feature umbrellas

All player-facing behavior in these domains is owned and **tuned through** these components. Prefer adding properties, hooks, and call paths **here** so designers and agents know where to look.

| Domain | Owner component | Scope |
|--------|-----------------|--------|
| **Movement** | `Code/Player/PlayerMovement.cs` | Anything that changes how the player **moves** (locomotion, modes, motion rules for the pawn). |
| **Vitals** | `Code/Player/PlayerVitals.cs` | **Loss/gain** of **health, stamina, oxygen** (and similar pools). Host bookkeeping may use `VitalsAuthority`; per-pawn tuning and the main “what the pawn has” contract stay on `PlayerVitals`. |
| **Combat** | `Code/Player/PlayerCombat.cs` | **Attacks and blocks** — input, phases, weapon tuning, client vs server flow. Host validation may use `CombatAuthority`; **serialized / designer-facing combat parameters for this pawn** stay on `PlayerCombat` unless there is a strong reason otherwise. |

Other scripts and whole subtrees exist (environment, AI, networking managers, etc.). For **this player prefab’s** movement, vitals, and combat, **go through these three umbrellas** for edits and for understanding what calls what.

## Primary melee — attack action (not collision damage)

Sword **colliders must not** be the source of HP damage (use them for VFX / clash / debug only). Real damage comes only from a **server-authoritative** phased attack on **`PlayerCombat`**:

**Owner → host timing:** After you **release** primary attack, the client waits **`SwingDamageWindowSeconds`** while summing mouse movement for drag-based damage tiers, then sends intent. The host runs **windup → EarlyActive / Active / LateActive → recovery** after accept.

1. **Attack selection** — directional cursor locks on press (`SouthpawSwing` inverts L/R attack **type** only). **Attack teardrop (HUD):** screen-left teardrop = **right** swing / right-side attack path; screen-right = **left** attack. **Block teardrop:** screen-left = left hold (same side as guard). Mouse evidence for attacks uses mirrored L/R cardinals vs block.
2. **Light / heavy** — hold duration vs `MeleeHeavyAttackHoldThreshold`; heavy uses `MeleeHeavyAttackDamageBonus`.
3. **Geometry** — `MeleeAttackPath` in combat-local space (+X forward, +Y up, +Z right). L/R: horizontal arc; overhead: vertical XY arc. Phases: `MeleeEarlyActiveDuration` / `MeleeActiveDuration` / `MeleeLateActiveDuration`.
4. **Hits** — `MeleeAttackSweep` sphere substeps along tip+heel motion (`MeleeHitVolumeThickness`). Per-target dedup; optional multi-hit via `MeleeAllowMultipleHitsPerAttack`.
5. **Attack path** — `MeleeAttackArcDegreeStep` (default 1°) samples the swing along `MeleeAttackPath` (~150 samples on a 150° arc, 360 on a full turn). Colored overlay lines are optional (`MeleeDebugDrawEnabled`); the path sampling is core combat readability.
6. **`CombatAuthority`** validates intent, rate limits, stamina, then calls `PlayerCombat.ServerStartMeleeAttackAction`.
7. **Block (hold)** — Attack2 locks teardrop L/R/U on press. **Block L/R (same side):** mouse toward screen-left → `SwingDirs.Left` → teardrop on the left → block guard on the defender's left (right teardrop = right side). **Not** mirrored like attacks. **Both gates required:** (1) attack ray enters the held block region before the body; (2) attacker bearing vs block look yaw must fall in that teardrop's front arc (left hold → −75°…0°, right → 0°…+75°, overhead → ±25°; behind-arc hits never block). Wide arcs can sit along the ray before the torso center — the angle gate stops “wrap-around” blocks when facing away. **Lateral holds** may block **left or right** swing types when both pass. **Overhead hold** only for overhead/forward attacks. Guard viz: green vertical/horizontal line (62u, 2u spheres) + thin ground arc (`BlockGroundArcHeightOffset`). Server computes block in `MeleeBlockResolution` + `MeleeBlockPath`; clients send block state via RPC only.

**Active combat helpers** (do not add parallel systems): `MeleeAttackPath`, `MeleeAttackSweep`, `MeleeAttackResolution`, `MeleeBlockPath`, `MeleeBlockResolution`, `PlayerCombat.ServerMeleeAttack.cs`, `PlayerCombat.Block.cs`, `AttackCombatTypes.cs`, `MeleeAttackTypes.cs`.

## Commandment #2 — End every assistant reply with “what you need to do”

When the Cursor assistant sends a **final** message in this repo, it must end with a short **“What’s needed from you”** section: concrete follow-ups (prefab/scene steps, testing to run, settings to verify, decisions only you can make). If there is genuinely nothing, say **Nothing required on your side for this change.**

This is documented for humans in **`AGENTS.md`** and enforced for the assistant via **`.cursor/rules/build-label-chat-footer.mdc`** (footer order: build label line, then this section).
