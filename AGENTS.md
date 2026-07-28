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
2. **Light / heavy** — hold duration vs `MeleeHeavyAttackHoldThreshold`; heavy adds `MeleeHeavyAttackDamageBonus` (+0.3) to the combat multiplier. **Damage** = `round(MeleeWeaponBaseDamage × multiplier)` (base **8** @ mult **1.0**). Additive mult: Active drag good **+0.15**, bad **−0.15**; EarlyActive **−0.15**; LateActive **+0.15**.
3. **Geometry** — `MeleeAttackPath` in combat-local space (+X forward, +Y up, +Z right). L/R: horizontal arc; overhead: vertical XY arc. Phases: `MeleeEarlyActiveDuration` / `MeleeActiveDuration` / `MeleeLateActiveDuration`.
4. **Hits** — `MeleeAttackSweep` sphere substeps along tip+heel motion (`MeleeHitVolumeThickness`). Per-target dedup; optional multi-hit via `MeleeAllowMultipleHitsPerAttack`.
5. **Attack path** — `MeleeAttackArcDegreeStep` (default 1°) samples the swing along `MeleeAttackPath` (~150 samples on a 150° arc, 360 on a full turn). Colored overlay lines are optional (`MeleeDebugDrawEnabled`); the path sampling is core combat readability.
6. **`CombatAuthority`** validates intent, rate limits, stamina, then calls `PlayerCombat.ServerStartMeleeAttackAction`.
7. **Block (hold)** — Attack2 locks teardrop L/R/U on press for HUD / quick attack transition only (**does not** gate block success). While blocking, cover a **front facing arc** (default **270°** = ±135° from look/combat yaw). **Both gates required:** (1) attacker bearing is inside that facing arc; (2) attack ray enters the **body-radius block shell** before the defender body. Outcomes are **duration-based** from `Assets/data/melee_block_stagger.json` (`lightBlock` / `lightParry` / `heavyBlock` / `heavyParry`): `durationSeconds`, `healthDamage`, `staminaCost`, and `tier` (`Light` now; `Heavy` reserved). Perfect parry = block started within `MeleeBlockParryWindowSeconds` (default **0.2 s**; block viz lines are white during that window). On success the defender takes the JSON HP/stamina costs, enters light stagger for `durationSeconds` (falling pose, **cannot attack**), then post-block recovery. Tune values in the JSON (seconds + points). Server: `MeleeBlockResolution` + `MeleeBlockPath` + `MeleeBlockStaggerCatalog`; clients send block state via RPC only.

**Active combat helpers** (do not add parallel systems): `MeleeAttackPath`, `MeleeAttackSweep`, `MeleeAttackResolution`, `MeleeBlockPath`, `MeleeBlockResolution`, `PlayerCombat.ServerMeleeAttack.cs`, `PlayerCombat.Block.cs`, `AttackCombatTypes.cs`, `MeleeAttackTypes.cs`.

### Entity AI perception + biome population

- **Perception / AI states**: alert → chase live player + attack until geometric LOS lost for **30s** (`ChaseLosLostAbandonSeconds`). No flank/break/last-known hunt paths. Nav uses live **PhysicsWorld** via `GenerateTiles` when structures change.
- **Population** (`Assets/data/biome_population.json`, `BiomePopulationCatalog` / `BiomePopulationScatter`): density by world-grid `spacingMeters` + `spawnWeight` per biome (not per-chunk); `respawn: false` = mini-boss (permanent death). Hooked from `TerrainWorldManager` on chunk load **inside collision range**. Clover Hills default: ~1 `scavT1` per **250 m**. Optional `near` anchor reserved for later.
- **Streamed terrain**: default is a **uniform square** around the camera (`StreamRadiusChunks`, e.g. 8 ≈ 512 m) — same distance in all directions, no look pop-in. Unload uses `StreamUnloadMarginChunks` hysteresis (keep until outside radius + margin) so load/unload don’t thrash at the same edge. Optional forward-cone mode remains for later. Entity population only inside `CollisionRangeMeters`.

## Commandment #2 — End every assistant reply with “what you need to do”

When the Cursor assistant sends a **final** message in this repo, it must end with a short **“What’s needed from you”** section: concrete follow-ups (prefab/scene steps, testing to run, settings to verify, decisions only you can make). If there is genuinely nothing, say **Nothing required on your side for this change.**

This is documented for humans in **`AGENTS.md`** and enforced for the assistant via **`.cursor/rules/build-label-chat-footer.mdc`** (footer order: build label line, then this section).

## Commandment #3 — Deprecate cleanly

When you **replace or remove** behavior, **delete the old path in the same change**: no orphaned facades, dead toggles, or duplicate post-process hooks. Settings that no longer affect output come out of the editor; keys needed only for old JSON live as plain fields in `TerrainPreviewSettings.Legacy.cs` (no `[Property]`).

Enforced via **`.cursor/rules/deprecate-cleanly.mdc`**. Subsystem docs (e.g. **`Code/World/TerrainPreview/docs/TERRAIN_PREVIEW.md`**) must match the code you ship.

## Commandment #4 — Meters are engine units (1:1)

**1 world unit = 1 meter** for design, scenes, and designer-facing properties. When the user says **10m** or **100m**, use **10** or **100** in transforms, scales, radii, and `[Property]` values — **not** a value scaled by `UnitsPerMeter` (40) or `TerrainWorldUnits.MetersToEngine`.

| User says | Use in engine |
|-----------|----------------|
| 10 m sun/moon disk | scale / `DiskScale` = **10** |
| 100×100 m ground | `Scale` **100,100,…** on a 1 m base mesh |
| 1500 m star shell | `ShellRadiusMeters` = **1500** |

**Do not** divide or multiply user meter numbers “for engine units” on environment, scene, fly-cam, sky, or new world features — that produced wrong sizes (e.g. **40×40** when the user asked for **100×100**).

**Legacy:** `TerrainWorldUnits` / `BuildModuleDimensions.UnitsPerMeter` may still apply only at **terrain chunk mesh** boundaries. Do not spread that conversion into unrelated systems.

Enforced via **`.cursor/rules/meters-are-engine-units.mdc`**.
