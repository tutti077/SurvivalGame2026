# Agent instructions — SurvivalGame2026

Read this at the start of work on **player pawn** gameplay or when touching movement, pools, or combat.

## Commandment #1 — Player feature umbrellas

All player-facing behavior in these domains is owned and **tuned through** these components. Prefer adding properties, hooks, and call paths **here** so designers and agents know where to look.

| Domain | Owner component | Scope |
|--------|-----------------|--------|
| **Movement** | `Code/Player/PlayerMovement.cs` | Anything that changes how the player **moves** (locomotion, modes, motion rules for the pawn). |
| **Vitals** | `Code/Player/PlayerVitals.cs` | **Loss/gain** of **health, stamina, oxygen** (and similar pools). Host bookkeeping may use `VitalsAuthority`; per-pawn tuning and the main “what the pawn has” contract stay on `PlayerVitals`. |
| **Combat** | `Code/Player/PlayerCombat.cs` | **Attacks and blocks** — input, phases, weapon tuning, client vs server flow. Host validation may use `CombatAuthority`; **serialized / designer-facing combat parameters for this pawn** stay on `PlayerCombat` unless there is a strong reason otherwise. |
| **Animation** | `Code/Player/PlayerAnimation.cs` | Citizen **hold poses**, attack anim triggers, left-swing mirror, demo held props. Combat/equipment request intents; this component applies animgraph + presentation. Runtime presentation props (the `melee_demo_stick` box-sword) must be `NetworkMode.Never` — a plain child of a networked pawn is `Snapshot` by default and replicates to joining clients, which showed **two swords** on the host player. |
| **Augments** | `Code/Player/PlayerAugments.cs` | Crafted body augments: 18 sockets + bank, station craft/install. Movement abilities (jump legs / lateral dash / double jump) read installed state from here via `PlayerMovement`. |

Other scripts and whole subtrees exist (environment, AI, networking managers, etc.). For **this player prefab’s** movement, vitals, combat, and animation, **go through these umbrellas** for edits and for understanding what calls what.

## Primary melee — attack action (not collision damage)

Sword **colliders must not** be the source of HP damage (use them for VFX / clash / debug only). Real damage comes only from a **server-authoritative** phased attack on **`PlayerCombat`**:

**Owner → host timing:** After you **release** primary attack, the client waits **`SwingDamageWindowSeconds`** while summing mouse movement for drag-based damage tiers, then sends intent. The host runs **windup → EarlyActive / Active / LateActive**, then **outcome combat recovery** (built-in `MeleeRecoveryDuration` stays ~0).

1. **Attack selection** — directional cursor locks on press (`SouthpawSwing` inverts L/R attack **type** only). **Attack teardrop (HUD):** screen-left teardrop = **right** swing / right-side attack path; screen-right = **left** attack. **Block teardrop:** screen-left = left hold (same side as guard). Mouse evidence for attacks uses mirrored L/R cardinals vs block.
2. **Light / heavy timing** — hold duration vs `MeleeHeavyAttackHoldThreshold` (default / prefab **0.7 s**). Chart defaults: **light** windup **0.22s** + damage window **0.10s** (`Early`/`Active`/`Late`) + soft recover **return frames + ~0.1s buffer**; **heavy** windup **0.30s** + damage **0.15s** + soft recover similarly. Heavy also adds `MeleeHeavyAttackDamageBonus` (+0.3) to the combat multiplier and uses a slower anim playback (`MeleeHeavySwingPlaybackRate`). **Damage** = `round(MeleeWeaponBaseDamage × multiplier)`. Additive mult: Active drag good **+0.15**, bad **−0.15**; EarlyActive **−0.15**; LateActive **+0.15**.
3. **Geometry** — `MeleeAttackPath` in combat-local space (+X forward, +Y up, +Z right). L/R: horizontal arc; overhead: vertical XY arc. Light/heavy phase durations via `GetPhaseDurations(..., isHeavy)`.
4. **Hits** — `MeleeAttackSweep` sphere substeps along tip+heel motion (`MeleeHitVolumeThickness`). Per-target dedup; optional multi-hit via `MeleeAllowMultipleHitsPerAttack`.
5. **Attack path** — `MeleeAttackArcDegreeStep` (default 1°) samples the swing along `MeleeAttackPath` (~150 samples on a 150° arc, 360 on a full turn). Colored overlay lines are optional (`MeleeDebugDrawEnabled`); the path sampling is core combat readability. **Arcs are bound to the swing clip:** a peer only draws Early/Active/Late lines while `PlayerAnimation.HasActiveMeleeSwingPresentation` is true on that machine — never an arc without an animation. Damage sweeps are unaffected (authority never depends on presentation).
6. **`CombatAuthority`** validates intent, rate limits, stamina, then calls `PlayerCombat.ServerStartMeleeAttackAction`.
7. **Attack commitment lock** — from **Attack1 press** (owner preview) through host windup + swing: ground move at **`MeleeAttackMoveSpeedScale`** (default **10%**); sprint suppressed; airborne velocity kept. Look uses slowed mouse + yaw/pitch cone via `OnEyeAngles`. **Windup telegraph:** black (light) / white (heavy) on press/hold and during host windup; **Early/Active/Late path overlay** (blue/yellow/red) only after release during damage phases. Dummy black telegraph flash length = light `MeleeWindupDuration` (0.22s). **Press anim:** `BeginMeleeAttackWindupHold` starts the attack clip **once**; hold freezes a windup pose (`PlaybackRate=0`); release resumes that same clip by restoring the playback rate. Release must **not** re-pulse `b_attack` when the press already started a clip — that restarted it from frame 0 and read as a stutter as the swing began.
8. **Combat recovery** — soft miss/hit: lock = **remaining swing presentation window + short buffer** (~0.1s); **no interrupting pistol sequence**. Hard recoveries: shove punch still uses a sequence; **victim/blocker fall uses stagger `IsGrounded=false` only** (never `UseAnimGraph=false` flail — that froze clients). Chain-busy = host sweep + recovery/stagger lock + **swing presentation remaining** (next attack waits for the clip to finish). `AttackCooldownAfterRelease` default **0**.
   **One swing at a time, no buffering:** pressing Attack1 while chain-busy does **nothing** — no queued intent, no chained spam swing, no auto-start from a held button. The player must press again after the clip finishes. Do not reintroduce a buffer; `PlayerAnimation.IsMeleeSwingAnimBusy` (time-bounded presentation window) is the single gate, and clearing a recovery sequence must preserve a running swing clip (`ExitCombatSequenceToLocomotion` skips the `b_attack` reset while a swing is presenting — resetting it produced arcs with no animation).
9. **Hit reaction (victim)** — `PlayerAnimation.HitReaction.cs` is the **only** "I was just hit" state, and it lives on **animation, not combat**, so the window and the pose have one owner that always ticks. **Resolve victims by `PlayerAnimation`** (shove trace, `ApplyMeleeStaggerToVictim`) — being hit is not equipment-dependent; `PlayerCombat` only exposes `IsHitReactionActive` for `IsCombatActionLocked` and `OnHitReactionBegan()` to drop its own in-flight state. Host picks a duration (`HitReactionSeconds` default **0.9 s**, or the caller's value — shove victim 1.2 s, shove-vs-block 0.5 s, heavy block 0.4 s) and `[Rpc.Broadcast]` gives every machine the same local deadline. Pose is the **looping flail sequence** `airborne_flail_movement` with `UseAnimGraph=false` (re-asserted from `PlayerAnimation.OnUpdate`, restarted only on the first frame) and the **held sword is destroyed for the whole window** (`TickHoldPose` / `TickMeleeDemoStickTransform` refuse to re-create it); the window end **hard-restores** locomotion and the hold pose once. `PlayerAnimation.TickHitReactionPose` owns **both** the pose and its exit and runs from `OnUpdate` **and `OnPreRender`** — a pawn's `OnUpdate` is skipped on peers where it is a proxy, so an exit that lived only in `PlayerCombat.TickHitReactionState` let that peer loop the flail forever. `HitReactionMaxSeconds` (2.5 s) caps any caller. Do **not** go back to an animgraph-only pose (`b_grounded=false`): `PlayerController` rewrites grounded state every frame on whichever machine simulates the pawn, so only proxies (the host's view of a client) played the reaction — a sequence is the same on every peer. During it: no attack/block/shove (`IsCombatActionLocked`), **no jump** (cleared in `PlayerMovement.PreInput`), and the **grapple detaches and cannot re-attach**. Do **not** re-add authority/proxy gates to this tick — the old stagger path had four gated tick sites and the host never expired client-owned pawns, which froze them in the fall pose and locked them out of combat. `MeleeStaggerTier` survives only as **block JSON data**, and `AirborneFlail` is gone.
10. **Shove (F)** — a **player ability, not a weapon one**: it works bare-handed and will become an unlockable augment (see Augment Station / `PlayerAugments`). `PlayerEquipment` must **never** disable `PlayerCombat` to express "no weapon" (that killed the shove, the hit reaction, and the jump/grapple locks at once) — the sword gate is the `PlayerEquippedItem.HasAction( PrimaryMelee )` check inside `PlayerCombat.OnUpdate`, which `TickOwnerShoveInput()` deliberately runs **before**, plus `RejectNoMeleeItemEquipped` on the host in `CombatAuthority`. Hand harvest / gather is **E** (`HandHarvest`); shove stays on **F** (`Shove`). Anytime **grounded**: spends **`ShoveStaminaCost`** (default **10**), dashes **`ShoveDashMeters`** (default **1 m**, converted via BodyHeight/1.8 ≈ **40** pawn units) flat-forward, then punch hit. Attacker then enters **`RecoveryShoveCombatLockSeconds`** (default **0.8 s**, extended to cover the punch clip) via `IsCombatActionLocked` (blocks sword, block, shove, and sprint; Attack1 during kick is not buffered into a swing). Owner gets lock via Rpc.Owner + local prediction. Vs block → blocker **stagger** **0.5s**; vs open → 3 damage + victim **stagger fall** **`RecoveryShoveVictimSeconds`** (default **1.2s**).
11. **Block (hold)** — Attack2 locks teardrop L/R/U on press for HUD / quick attack transition only (**does not** gate block success). While blocking, cover a **front facing arc** (default **270°** = ±135° from look/combat yaw). **Both gates required:** (1) attacker bearing is inside that facing arc; (2) attack ray enters the **body-radius block shell** before the defender body. Perfect parry = block started within `MeleeBlockParryWindowSeconds` (default **0.2 s**). HP/stamina from `Assets/data/melee_block_stagger.json`; recovery poses from combat recovery (not falling stagger).

**Active combat helpers** (do not add parallel systems): `MeleeAttackPath`, `MeleeAttackSweep`, `MeleeAttackResolution`, `MeleeBlockPath`, `MeleeBlockResolution`, `PlayerCombat.ServerMeleeAttack.cs`, `PlayerCombat.Block.cs`, `PlayerCombat.AttackCommitmentLock.cs`, `PlayerCombat.CombatRecovery.cs`, `PlayerCombat.Shove.cs`, `PlayerCombat.Durability.cs`, `AttackCombatTypes.cs`, `MeleeAttackTypes.cs`.

### Tool / weapon durability + workbench repair

- **Config** lives on equipment profiles (`Assets/data/equipment_profiles.json`): `durabilityMax` (uses before broken; 0 = never wears) and `durabilityDrainSecondsEquipped` (torch/lantern passive drain: 1 tick per N seconds while in the active hotbar slot). Rules live in `ToolDurability`; wear state rides `InventorySlot.Wear` (uses consumed, 0 = fresh) through every grid, cursor drag, container, world drop, and sync RPC. `InventorySlot.CrafterName` rides the same plumbing (set at craft time in `PlayerCrafting.HostTryCraft` for equipment-profile outputs only) — **any new per-item field must be added to every one of those RPC/copy sites together**.
- **Item tooltip** (`PlayerInventoryInteraction.Tooltip.cs`): hover any slot (bag / hotbar / paperdoll / container) for 0.5 s while the menu is open → content-sized popup follows the pointer on the drag layer. Shows name, recipe Type/Description, equipment stats (damage, swing windup from the local `PlayerCombat`, bow draw, harvest tool, durability, passive drain), food effects from `FoodCatalog`, ammo data, and "Crafted by …" for tagged equipment. `ResourceDefinitionData.Description` is an optional JSON field for raw-resource flavor text.
- **Ticks (host only)**: melee — 1 per swing that connected (hit or blocked), applied in `ServerMeleeAttackRuntime.TrySendCompletion`; bow — 1 per shot fired (`ServerTryFireBow`); build hammer — 1 per successful place/repair (`BuildAuthority`); torch/lantern — passive drain via `EquippedToolDrainAuthority.HostTick` from `CombatAuthority.OnUpdate`, plus per-hit like any melee weapon. **Free swings never cost durability.**
- **Broken (wear ≥ max)**: cannot swing (`CanStartPrimaryAttack` owner-side + `ServerCanBeginMeleeAttackAction` host-side), cannot fire, cannot place/repair structures, and **cannot be equipped** — `PlayerEquipment.TryResolveHotbarEquipResourceId` refuses broken stacks, and since wear changes raise `HotbarChanged`, a tool breaking mid-use auto-unequips (MainHand empties until repaired). Item stays in inventory.
- **Slot visuals** (`ResourceCatalog.ApplyStackVisual`): every durability item shows a thin bottom bar — full tan/yellow when untouched, shrinking with wear, entirely grey when broken — plus a semi-transparent red X overlay while broken. Renders in every grid (bag, hotbar, containers, drag ghost).
- **Repair**: free, at the **workbench** (`Workbench` station, `prefabs/build/workbench.prefab`). E opens the crafting page in workbench mode — recipe list filtered by `CraftingRecipe.AppearsAtStation` (explicit `stations` array; default: `requiredStation` when set, else `workbench`) — with a header button repairing the most-damaged tool (hotbar + bag) to full per click via `PlayerCrafting.HostTryRepairMostDamagedTool`.

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

## Commandment #4 — Meters are the design language; convert once, at the read site

**Designer-facing values are written in meters.** A property named `…Meters` holds exactly the number the user said: 100 m → **100**. Never pre-scale that number in the property, in JSON, or in a scene transform you type by hand.

**The world is not 1:1.** s&box inherits Source units, and this project has two conversion factors:

| Space | Factor | Converter |
|-------|--------|-----------|
| Terrain, environment, sky, fly-cam, harvest, containers, grapple | **40 u/m** | `TerrainWorldUnits.MetersToEngine` |
| Pawn distances (shove dash, bow range, wingsuit) | **≈40 u/m** | `BodyHeight / 1.8` — Citizen `BodyHeight` 72 ≈ 1.8 m |
| Build pieces only — snap math and piece colliders | **50 u/m** | `BuildColliderSnap.PrefabColliderSize` |

Convert **once**, inside the system that owns the value, and never carry a converted number across a system boundary. A value that has already been converted (a mesh AABB, a `BuildPieceModelCache` extent) is in final world units — do not scale it again.

| User says | Property value | What the read site produces |
|-----------|----------------|------------------------------|
| 1500 m star shell | `ShellRadiusMeters` = **1500** | 60,000 units via `MetersToEngine` |
| 1 m shove dash | `ShoveDashMeters` = **1** | ≈40 units via `BodyHeight / 1.8` |
| 2 m wall module | `ModuleMeters` = **2** | 100 units via `PrefabColliderSize` |

**Open item:** build's 50 u/m is the odd one out. Unifying it to 40 would move every snap point in the kit, so it stays until that migration is done deliberately.

Enforced via **`.cursor/rules/meters-are-engine-units.mdc`**.

## Commandment #5 — No Ensure* bandage components

When a prefab or scene object is missing what it needs, **fix the asset**. Do **not** add a runtime helper (`EnsureChopableTrees`, `AutoWire*`, folder bootstraps that `Components.Create` on children, etc.) to paper over incomplete setup.

- Prefer clarifying requirements and s&box prefab/instance behavior over guessing.
- Author `ChopableTree` + `DamageReceiver` (and similar) on the tree prefab or scene instance.
- Repair bad prefab instance patches (`RemovedObjects`) instead of compensating in code.

Enforced via **`.cursor/rules/no-ensure-bandage-components.mdc`**.
