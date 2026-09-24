# CLAUDE.md — SurvivalGame2026

Engine: **s&box** (C#, `Component` / `GameObject` / prefab model, Source-2 units).
Assembly: `survivalgamebasics` (`Code/`), editor tools in `Editor/`.
Namespace: `Survival` (a few `Game` / `Sandbox` roots).

This file is the Claude Code equivalent of the Cursor setup. It ports **`AGENTS.md`** plus the
eight `.cursor/rules/*.mdc` rules, which Claude does **not** load automatically. `AGENTS.md`
remains the long-form reference for melee/AI mechanics — read it before touching player pawn
gameplay, movement, pools, or combat.

---

## The five commandments

### 1. Player feature umbrellas

All player-facing behavior in these domains is owned and **tuned through** these components.
Add properties, hooks and call paths **here**. Do not invent parallel systems.

| Domain | Owner | Scope |
|--------|-------|-------|
| Movement | `Code/Player/PlayerMovement.cs` | Locomotion, modes, motion rules for the pawn (grapple / wingsuit / augment abilities are partials of this class) |
| Vitals | `Code/Player/PlayerVitals.cs` | Loss/gain of health, stamina, oxygen. Host bookkeeping may live on `VitalsAuthority`; per-pawn tuning stays here |
| Combat | `Code/Player/PlayerCombat.cs` | Attacks and blocks — input, phases, weapon tuning, client vs server flow. Host validation may live on `CombatAuthority`; designer-facing combat params stay here |
| Animation | `Code/Player/PlayerAnimation.cs` | Citizen hold poses, attack anim triggers, left-swing mirror, demo held props, **hit reaction** |
| Augments | `Code/Player/PlayerAugments.cs` | Crafted body augments: 6 parts × 3 sockets + bank. Enhance (augment cores) opens sockets, placing is free/pending, Augment (gold coins) commits — costs in `Code/Augments/AugmentSlot.cs` (`AugmentBodyParts`) |

Other subtrees (environment, AI, networking, building, inventory) are their own systems — this
rule is about **this player prefab's** movement, vitals, combat and animation.

### 2. End every reply with the footer

Every **final** assistant reply in this repo ends with, in this order:

1. `Build label — vX.Y.Z (Assistant)` — the current `GameBuildLabel.Display` value.
2. A blank line, then a section headed exactly **`What's needed from you`**: concrete follow-ups
   (prefab/scene edits, tests to run, settings to verify, decisions only the user can make).
   If nothing applies, use one bullet: **Nothing required on your side for this change.**

### 3. Deprecate cleanly

When you replace or remove behavior, **delete the old path in the same change**.

- Delete superseded types and one-line facade wrappers.
- Remove settings properties, editor tab entries and `CloneForGenerate` copies that no longer
  affect output.
- Remove call sites that existed only for the old path.
- Update `AGENTS.md` and subsystem docs (e.g. `Code/World/TerrainPreview/docs/TERRAIN_PREVIEW.md`).

Old preview JSON may still carry removed keys — keep those as **plain fields** (no `[Property]`)
in `Code/World/TerrainPreview/TerrainPreviewSettings.Legacy.cs`. Never keep a designer toggle
that no longer runs code.

Before closing: can you grep the old type name and get zero runtime callers? Does every remaining
setting change something visible in `Sample()` or the editor preview?

### 4. Meters are the design language; convert once, at the read site

**Designer-facing values are written in meters.** A property named `…Meters` holds exactly the
number the user said: 100 m → **100**. Never pre-scale that number in the property, in JSON, or in
a scene transform you type by hand.

**The world is not 1:1.** s&box inherits Source units, and this project has two factors:

| Space | Factor | Converter |
|-------|--------|-----------|
| Terrain, environment, sky, fly-cam, harvest, containers, grapple | 40 u/m | `TerrainWorldUnits.MetersToEngine` |
| Pawn distances (shove, bow range, wingsuit) | ≈40 u/m | `BodyHeight / 1.8` (Citizen `BodyHeight` 72 ≈ 1.8 m) |
| Build pieces only — snap math, piece colliders | **50 u/m** | `BuildColliderSnap.PrefabColliderSize` |

Convert **once**, inside the system that owns the value. A number that has already been converted —
a mesh AABB, a `BuildPieceModelCache` extent, a `BoxCollider.Scale` — is in final world units;
**do not scale it a second time.** That double-scale is what put the build snap points in the wrong
place.

Open item: build's 50 u/m is the odd one out. Unifying it to 40 moves every snap point in the kit,
so it stays until that migration is done deliberately.

### 5. No `Ensure*` bandage components

When a prefab or scene object is missing what it needs, **fix the asset**.

Forbidden: components named `Ensure*` / `AutoWire*` / `Bootstrap*` whose job is to create missing
gameplay components on other objects at start; folder scripts that walk children and
`Components.Create<T>()`; "it works in play mode" while the prefab still lacks the components.

Required instead: author the real components on the prefab (`ChopableTree` + `DamageReceiver` on
tree prefabs, etc.); for non-prefab scene instances add them in the scene; repair bad prefab
instance patches (`RemovedObjects`) rather than compensating in code.

Allowed (not this commandment): catalog `EnsureLoaded()` for JSON caches; private helpers that
build transient UI / preview / physics for an object that already owns the feature; one-time
migrations the user explicitly asked for.

---

## Also enforced (rules, not numbered commandments)

### Ask before assuming

Ask when requirements, naming, data shape or ownership are not explicitly stated. Do not guess at
item stats, slot layouts, component hierarchy or migration strategy. Summarise your understanding
and list open questions when a request spans multiple systems. Exception: follow patterns already
established in the same subsystem.

### Infrequent validation

Default to doing **less work, less often**. Checks exist to keep the game inside defined bounds and
stop cheating — not to re-prove correctness every frame.

| Moment | Validate? |
|--------|-----------|
| Client preview / UI / local feedback | Light, cached, approximate is fine |
| Player commits an action (place, craft, spend) | Yes — once, on the path that matters |
| Server / host authority | Yes — full rules; anti-cheat lives here |
| Every frame / ray / neighbour | **No** — use cached or incremental state |

Cache immutable or slow-changing data (prefab bounds, snap locals, catalogs) once per piece or
version. Never clone/spawn/trace the world in a loop to "verify" what a cached value already
answers. Invalidate only on real change. Prefer incremental updates over full-scene scans. Client
RPC sends intent; the host re-validates once.

If you cannot say what cheat or boundary failure a check stops, it does not belong in a hot path.

### Patch bump

Any edit under `Code/` (including Razor/SCSS in `Code/UI/`) or to `Code/survivalgamebasics.csproj`
bumps the semver **patch** of `GameBuildLabel.Display` **once per turn**, at the end. No bump for
chat-only replies with zero file edits.

> Note: `bump-patch-on-code-edit.mdc` also says to mirror the string into a `<Version>` element in
> `Code/survivalgamebasics.csproj`. **That element does not exist** — either add it or drop that
> half of the rule.

### Terrain preview tuning

Cite designer knobs as **`Tab - Knob Name`** using the exact Terrain Noise Preview tab and property
titles (e.g. **Lakes - Shore Detail (0–1)**, **Water - Min Speck Diameter (m)**).

Each *Generate Preview* writes `Assets/terrain/preview/<NNN>_seed<seed>/` with `biomes.png`,
`world.png`, `generation_metrics.json`, `preview_settings.json`, `water_coverage.json`.
`Assets/terrain/preview/.latest_preview.json` points at the newest bundle. When the user says
"check the latest generate", read the pointer, then the PNG + metrics — tune from metrics, not
from re-explanation.

Targets: `lakePatchCount` ≤ 24 · `medianLakeDiameterMeters` ≥ 600 · `lakeArchipelagoScore` ≤ 1.6 ·
`mountainLandFraction` ≤ 0.38.

---

## Layout

```
Code/
  Player/       PlayerMovement · PlayerVitals (+ Comfort / StatusEffects partials) · PlayerCombat · PlayerAnimation · PlayerAugments
                (+ their partials), melee path/sweep/block helpers, equipment, hand harvest,
                inventory interaction, build hammer
  Combat/       CombatAuthority (host melee validation), ArrowProjectile, StuckArrow (arrow rides its victim, drops with physics on death)
  Crew/         CrewRegistry (host static registry — player data, survives scene loads) + PlayerCrew (per-pawn sync on the player prefab)
  Arena/        ArenaSession (crew-vs-crew battles, queue + matchmaking), ArenaMenuButton
  Vitals/       VitalsAuthority (host pools), regen gates, StatusEffectCatalog (Assets/data/status_effects.json)
  Entity/       EntityBrain (+ Breach partial: entities tear down build pieces) · EntityLocomotion · EntityCombat · perception, nav
  Building/     Build piece catalog, snap layout/placement/compatibility, piece health, nav sync (+ BuildNavBakeSystem),
                ShelterProbe (global roof-above / walled-in checks; comfort + workbench gate),
                BuildBed (claimable respawn point, raid target; E claim in PlayerInventoryInteraction.Bed.cs),
                BuildDoor (swinging leaf on the door prefab: mesh-collided frame, keyframed leaf, opens away from the user)
  Inventory/    PlayerInventory · PlayerHotbar · containers · resource + equipment catalogs
                · armor: worn-piece aggregate on Player/PlayerEquipment.Armor.cs, formula in Vitals/ArmorMitigation.cs,
                  weight → speed on PlayerMovement, clothing visuals on PlayerAnimation.WornClothing.cs (see AGENTS.md "Armor")
  Crafting/ Food/ Quests/ Skills/ Augments/   JSON-backed catalogs (Assets/data/*.json)
  Farming/      FarmingRules (dirt tag, tile size, spacing) · TilledSoil · FarmPlant (host-timed growth stages) · FarmingAuthority (host till / sow)
                · seed rows live in resources.json ("seed" block); ToolHoe + PlayerFarming live in Player/
  Camps/        EnemyCampCatalog (Assets/data/enemy_camps.json — piece + entity layouts + camp-centered wander / max-travel rings, build-kit meters)
                · EnemyCampSpawner (host stamp, sets EntityBrain camp leash, creates the EnemyCamp root) · EnemyCamp (camp root; `campRings` debug rings) · EnemyCampSpawnButton (I)
  Boss/         BossCatalog (Assets/data/bosses.json — health, second-form flags, bar range) · BossSpawner (host stamp through
                EntityEnemySetup) · BossEntity ([Sync] pool mirror + second-form refill via EntityVitals.LethalHitInterceptor)
                · BossSpawnButton (H); screen-top bar is Code/UI/BossHealthBarHud.cs
  Raids/        BaseRaidCatalog (Assets/data/raids.json — counts, waves, ring + aggro meters, enemy mix) · BaseRaidSession (L: host waves,
                win/lose, ground ring; on the BaseRaid object) · raiders run EnemyAiState.Raiding (EntityBrain.Raid.cs); banner is Code/UI/BaseRaidHud.cs
  Dungeon/      BoxDungeonGenerator (scene component: host picks a seed, every peer builds the same tinted dev-box dungeon locally;
                host places dead-end chests through BuildAuthority) · DungeonLayout (pure seeded tree: 1×1…3×3-cell rect + round rooms,
                corridor-cell runs, hub rooms with 3–6 doors, stairs + landings, dead-end pruning)
                · DungeonGeometry (plates / multi-door walls / round rings / pillars / stair runs / stairwell holes, one child per floor)
                · DungeonMapModel (rooms / halls / doors / stairs in world meters) + DungeonExploration (client-local visited rooms + current floor)
                  → TerrainWorldMapFace draws only the current floor's discovered rooms (UP / DOWN / IN badges); readout line in MapMenuSection
                · DungeonSignPanel (static ENTRANCE plate); scene Assets/scenes/dungeonBoxTest.scene; console dungeon_regen / dungeon_floors / dungeon_info
                · CaveDungeonGenerator (caveVertTest scene: a 3 km-deep CHAIN of drops, steep chutes, chimneys and caverns swept as one
                  wandering tube — no central axis, mainly one path, flat ground only at rare landings / one junction / the chamber — CaveLayout (+ .Trunk, .Passages, .Route partials) is the pure seeded layout: archetype
                  trunk plan, swept tubes with parallel-transported frames, mouths cut into the trunk, dead-end rooms and loops off the
                  junctions and drops, ledge route with descend / climb steppers; CaveGeometry (+ .Dressing) builds one runtime rock model with
                  mesh collision (no grapple tag) — tubes, holes zipped to branches, platform collar, pond bowls — plus dev-box ledges tagged
                  "grapple", oases, waterfall, host-placed chests; verified by a standalone .NET harness; console cave_regen / cave_info / cave_tp
  Hacks/        GameHacks — console-toggled dev flags (allCrafting), mirrored onto pawns via [Sync] for host checks
  Map/          Player map markup: MapPinCatalog (12 pin icons, Assets/ui/map) · LocalMapMarkup (client-local pins + pen strokes in world
                meters, write-through to 2Tgames/players/<steamid>/map/<world>.json via MapMarkupSaveStore) · MapPingFeed (MMB pings,
                crew-scoped RPC on Crew/PlayerCrew.MapPing.cs); drawn by UI/TerrainWorldMapFace, driven by UI/Menu/MapMenuSection (see AGENTS.md "Map")
  Circuits/     CircuitNode (sphere + [Sync] powered/output/wires on every circuit-enabled prefab) · CircuitRegistry (host OR-solve, event-driven)
                · CircuitLever / CircuitLight devices · CircuitWireOverlay (stripper spheres + wire lines); tool is Player/ToolWireStripper.cs,
                cable menu Code/UI/Circuits/, pieces need "circuitEnabled": true in build_pieces.json (see AGENTS.md "Circuits")
  Environment/  WindSystem (scene wind: host owns heading + base strength via [Sync], every client simulates gusts; publishes
                WindDirection / WindStrength / WindGust to all shaders through Scene.RenderAttributes once per frame)
                · GrassPatch + GrassScatterer (grass through the engine Sandbox.Clutter system — GPU instanced/culled/LOD;
                shader Assets/shaders/grass_blade.shader, models grass_clump1–4 from Blender/scripts/create_grass_clump.py)
                · ChopableTree, harvest yields, teleport pads
                · SunShadowDistance (on the scene Sun: multiplies the engine's cascade shadow reach so the shadow ring is not at the player's feet)
                · BearTrap (hammer-placed trap_small / trap_large: holds players 5 s, animals + enemies 10 s by TrapSize band;
                  hold lives on PlayerMovement.TrapLocked / EntityLocomotion.IsTrapped) · DamageOverTimeTrap (scene hazard volume)
  World/Terrain/        Streaming, chunk mesh, biome population, world save IO
  World/TerrainPreview/ Offline world generation pipeline + settings
  UI/           PlayerScreenHud, HUDs, Menu/ sections
  Networking/   Host spawn, synced UI content, connection identity
Editor/         Terrain Noise Preview tool, world manager widget
Assets/data/    Catalog JSON (resources, recipes, build pieces, food, skills, quests, …)
Assets/prefabs/ player/basicplayer · entity/scavT1 · build/* · environment/*
```

## Working notes

- Data lives in `Assets/data/*.json` and is loaded by `*Catalog.EnsureLoaded()`. Adding content is
  usually a JSON edit, not a code edit.
- Host authority: `CombatAuthority` (melee), `VitalsAuthority` (pools), `BuildAuthority` (placement).
  Clients send intent; the host validates and broadcasts.
- Melee damage never comes from weapon colliders — only from the phased server attack on
  `PlayerCombat`. See `AGENTS.md` for the full windup → charge → active → recovery contract;
  per-class timings live in `Assets/data/melee_weapon_classes.json`.
- Hit reaction lives on `PlayerAnimation`, not `PlayerCombat`, and must tick from both `OnUpdate`
  and `OnPreRender` so proxies expire it.
