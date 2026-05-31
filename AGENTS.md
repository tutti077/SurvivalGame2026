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

**Owner → host timing:** After you **release** primary attack, the client waits **`SwingDamageWindowSeconds`** (Melee group, inspector **“Post-release drag window (s)”**) while summing mouse movement, then sends intent. Large values feel like lag after release; **`0`** dispatches on the next frame. The host still runs **windup → EarlyActive / Active / LateActive → recovery** after accept.

1. **Attack selection** — directional cursor locks on press. Default (`SouthpawSwing` false): **right → RightAttack**, **left → LeftAttack**, **up → ForwardAttack**. `SouthpawSwing` inverts L/R only. Resolved type is locked for the attack instance; host re-resolves from `SwingDir` + attacker's `SouthpawSwing`.
2. **Light / heavy** — host decides from hold duration vs `MeleeHeavyAttackHoldThreshold`; heavy applies `MeleeHeavyAttackDamageMultiplier` to state base damage.
3. **Shared geometry** — `MeleeAttackPath` builds paths in **player-local combat space** (+X forward, +Y up, +Z right). **L/R**: horizontal arc in XZ (`MeleeAttackRangeLeftRight`, `MeleeLateralArcTotalDegrees` default **150°**); combat basis = **yaw-only** horizontal forward/right (camera pitch ignored); height from **`MeleeAttackZaxisStart`** + tilt. **Overhead**: same arc span default (**150°**) in vertical XY — `MeleeAttackForwardArcStartDegrees` + `MeleeAttackForwardArcTotalDegrees`; `MeleeAttackRangeForward` × reach multipliers. Shared **`MeleeEarlyActiveDuration` / `MeleeActiveDuration` / `MeleeLateActiveDuration`** drive EarlyActive / Active / LateActive for **all** attack types (time-based only; total active = sum of those three). Samples append over time; live transform bends new samples only.
4. **Phases (time-based)** — shared early / active / late durations split the active window into EarlyActive (blue) / Active (yellow) / LateActive (red) for every attack type. **Not** arc degrees. State applies `Melee*DamageMultiplier` × `MeleeWeaponBaseDamage`; stagger uses `MeleeBaseStagger` × `Melee*StaggerMultiplier`. Heavy multiplies damage after that.
5. **Hits** — thickened sphere substeps along tip+heel motion (`MeleeHitVolumeThickness`). Per-target dedup; `MeleeAllowMultipleHitsPerAttack` + `MeleeMaxTargetsHit` cap multi-target swings.
6. **`CombatAuthority`** validates intent, rate limits, stamina, light/heavy, then calls `PlayerCombat.ServerStartMeleeAttackAction`.
7. **Block (hold)** — hold **Attack2** with teardrop **L / R / U**: **Right** blocks **RightAttack** from a **90°** arc on your right; **Left** blocks **LeftAttack** from the left arc; **Up** blocks **ForwardAttack** only. Direction updates live while held. Stance ends after a successful block until you release block (**10** / **20** stamina for light / heavy). Tune on **`PlayerCombat`** (`MeleeBlock*`).

Helpers under `Code/Player/`: `MeleeAttackPath`, `MeleeAttackSweep`, `MeleeAttackResolution`, `MeleeBlockDefender` — **tune on `PlayerCombat`**.

## Commandment #2 — End every assistant reply with “what you need to do”

When the Cursor assistant sends a **final** message in this repo, it must end with a short **“What’s needed from you”** section: concrete follow-ups (prefab/scene steps, testing to run, settings to verify, decisions only you can make). If there is genuinely nothing, say **Nothing required on your side for this change.**

This is documented for humans in **`AGENTS.md`** and enforced for the assistant via **`.cursor/rules/build-label-chat-footer.mdc`** (footer order: build label line, then this section).
