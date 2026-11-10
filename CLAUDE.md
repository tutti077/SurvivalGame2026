# CLAUDE.md — SurvivalGame2026

Engine: **s&box** (C#, `Component` / `GameObject` / prefab model, Source-2 units).
Assembly: `survivalgamebasics` (`Code/`), editor tools in `Editor/`.
Namespace: `Survival` (a few `Game` / `Sandbox` roots).

This file holds the rules, Mark's guidelines and the paths Claude needs to build things inside the
project's boundaries and art style. How a system works lives in **`AGENTS.md`** (read it before
touching player pawn gameplay, movement, pools or combat) and in subsystem docs:
`Code/Environment/docs/ENVIRONMENT.md` (wind, grass, sky, lighting, terrain look),
`Code/World/TerrainPreview/docs/TERRAIN_PREVIEW.md`, `Code/World/Voxel/docs/VOXEL.md`,
`Code/UI/docs/UI_THEME.md` (the UI theme: every colour / font / radius comes from it).

---

## The six commandments

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
- Update `AGENTS.md` and subsystem docs.

Old preview JSON may still carry removed keys — keep those as **plain fields** (no `[Property]`)
in `Code/World/TerrainPreview/TerrainPreviewSettings.Legacy.cs`. Never keep a designer toggle
that no longer runs code.

Before closing: can you grep the old type name and get zero runtime callers? Does every remaining
setting change something visible in `Sample()` or the editor preview?

### 4. No `Ensure*` bandage components

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

### 5. Meters are the design language; convert once, at the read site

**Designer-facing values are written in meters.** A property named `…Meters` holds exactly the
number the user said: 100 m → **100**. Never pre-scale that number in the property, in JSON, or in
a scene transform you type by hand.

**The world is not 1:1.** s&box inherits Source units, and this project has two factors:

| Space | Factor | Converter |
|-------|--------|-----------|
| Terrain, environment, sky, fly-cam, harvest, containers, grapple | 40 u/m | `TerrainWorldUnits.MetersToEngine` |
| Pawn distances (shove, bow range, wingsuit) | ≈40 u/m | `BodyHeight / 1.8` (Citizen `BodyHeight` 72 ≈ 1.8 m) |
| Build pieces — snap math, piece colliders, camp layouts | 40 u/m | `BuildModuleDimensions.UnitsPerMeter` (kit vmdls import at 0.4) |

Convert **once**, inside the system that owns the value. A number that has already been converted —
a mesh AABB, a `BuildPieceModelCache` extent, a `BoxCollider.Scale` — is in final world units;
**do not scale it a second time.** That double-scale is what put the build snap points in the wrong
place.

The build kit's old 50 u/m was retired 2026-11-08 (Valheim scale: a 2 m wall is 80 units, just over the
72-unit pawn). A floor meets other pieces at its **face**, never its mid-plane
(`BuildSnapPlacement.ApplyFloorFaceOffset`), so a 0.25 m deck does not eat the room's headroom.

### 6. One prompt at a time on the editor — never steal the MCP

The s&box editor serves **one prompt at a time**. Before any editor call (play start / stop, open
scene, `run_csharp`, console commands, screenshots), check that no other session is using it
(`list_sessions`: nothing running; a play session you did not start belongs to someone else). If it
is busy, do the work that needs no editor and **wait** for it to go idle. Never stop, restart or
re-scene another prompt's play session, never open a second editor, and never retry a denied
`play_start` more than once. Mark 2026-11-09: "One prompt can't stop on another and steal the MCP."

---

## Working rules

### Ask before assuming

Ask when requirements, naming, data shape or ownership are not explicitly stated. Do not guess at
item stats, slot layouts, component hierarchy or migration strategy. Summarise your understanding
and list open questions when a request spans multiple systems. Exception: follow patterns already
established in the same subsystem. When Mark reports something "doesn't work", fix the exact thing
he named; when he proposes a mechanism ("it's like X"), test that against the code first.

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

### Live editor (sbox MCP) and finishing work

- After any C# edit, confirm the editor compiled it (`LastCompileSucceeded`) before measuring or
  claiming a result.
- Inspect every case Mark named in the editor before calling visual work done; list what still
  looks wrong.
- **Always use the sbox MCP — autonomously** (Mark 2026-10-27: "DO ATTEMPT to use it, that's what
  makes it awesome"). Compile-check, open scenes, play and screenshot without asking. If it is not
  answering, try to get it back yourself: the `sbox-cli --call` bridge, and launching `sbox-dev.exe`
  on the project if no editor is running (check `sbox-cli --list` first so you never open a second one).
- **Only if that still fails, pause and tell Mark what is blocked.** Never branch the work off or land
  it untested. Work that needs no editor (docs, JSON, Blender) can continue meanwhile.
- Autonomous never means grabbing a busy editor: commandment 6 — wait until the other prompt is done.

### Shared main, several sessions

Mark runs several sessions on `main` at once (2026-10-27). Keep the work on `main` — no extra
branches or worktrees unless he asks. Stage and commit **only the files your session changed**;
never sweep another session's edits into your commit.

### Commit messages

Every commit subject starts with one of the lowercase bracket tags below, then a short lowercase
summary: `[terrain] river trough carve, flowing clear water …`. No untagged subjects, merges included.

- **`[environment]`** for ALL things pertaining to the environment (Mark, 2026-10-21): trees, rocks,
  ground foliage, grass, wind, sky, day/night, environment asset moves — anything under
  `Code/Environment/`, `Code/World/Environment/` or `Assets/*/environment/`.
- Other tags in use: `[terrain]` `[building]` `[vehicles]` `[weapons]` `[items]` `[ui]` `[player]`
  `[ai]` `[lighting]` `[assets]` `[hacks]` `[extras]` (small misc). Reuse one of these before
  inventing a new tag.

### Patch bump

Every user prompt bumps the semver **patch** of `GameBuildLabel.Display` (`Code/GameBuildLabel.cs`)
**once**, at the end of the reply — code, asset / Blender work and chat-only answers alike (Mark,
2026-09-28: the label counts prompts). **Never commit a label bump on its own** (Mark 2026-10-27):
the bumped file rides along in the next real commit.

### Human verification log

**`VERIFY.md`** at the repo root holds only what Mark explicitly asks to have added ("add this to the
verify list"). Claude never adds V-rows on its own initiative — things it could not check by eye go in
that reply's **What's needed from you** as plain text. When Mark asks for a row: ID, date, build,
area, what to check, how to reproduce in under a minute, status; cite it in the footer
(`VERIFY V-003`). Only Mark closes or clears rows. The *Fable review queue* section is separate:
work by Opus gets an `F-NNN` row there for Fable to re-check, cited in the footer.

### Cool ideas list

When Mark says "remember this idea for later" (or similar), append it to **`IDEAS.md`** at the repo
root in his own words: sequential `I-NNN`, date, the idea, one optional context line. Record, don't
design: no plan, estimate or counter-proposal. Only Mark reorders, merges or removes ideas; when one
is picked up he moves it to *Picked up*. Mention the new ID in **What's needed from you** (`IDEAS I-003`).

### Automated tests

A **test** is a regression guard that runs by itself whenever its system is touched — unlike a
routine (below), nobody asks for it. Each suite is a play-mode console command that fights / drives
real prefabs through the host path, reads its expected numbers from the live tuning (so retuning
never fails it, broken behaviour does), and logs `[Tag] PASS|FAIL <case>` then one `RESULT` line.

| Suite | Command | Code | Gate scope (edits that make it owed) |
|-------|---------|------|--------------------------------------|
| Combat | `combat_test` | `Code/Combat/Tests/CombatTest.cs` | `Code/Combat/**`, `PlayerCombat*.cs`, `Melee*.cs`, combat types, `PlayerAnimation.HitReaction.cs`, `VitalsAuthority` / `ArmorMitigation`, `melee_weapon_classes.json`, `melee_block_stagger.json`, `combat_timings.csv` |
| Sword animation | `anim_test` + offline `create_sword_anims.py -- check` | `Code/Player/Tests/AnimTest.cs`, `Blender/scripts/create_sword_anims.py` | `PlayerAnimation*.cs`, `Code/Player/Tests/AnimTest.cs`, `Assets/models/player/{animations,animgraphs}/**`, `human_male_sword.vmdl`, `create_sword_anims.py`, `sword_pose_tuning.json`, `make_human_sword_assets.py` (gate `.claude/hooks/anim_test_gate.py`) |

The gate is `.claude/hooks/combat_test_gate.py`, wired in `.claude/settings.json`: an edit in scope
marks the test owed for that session (and runs the offline `combat_timings.csv` ↔ JSON mirror check);
the Stop hook blocks the end of the reply (once — a second stop goes through so a dead editor can't
trap the session; then say so in the footer) until the suite printed **ALL PASS** and the marker was
cleared (`python .claude/hooks/combat_test_gate.py clear <session id>` — the hook message carries the id).
Run it in whatever scene is playing — the suite brings a temporary `CombatAuthority` if the scene has
none and fights 300 m up — so never stop a play session someone else is using. A FAIL is either a real
regression (fix it) or a rule Mark deliberately changed (update the case, say so in the reply). Quote the
`RESULT` line in the reply. A new suite gets a row here, its own `*_test_gate.py` scope list, and a
`ConsoleHelp` row under *Tests*.

### Console commands

Every new `[ConCmd]` gets a row in `Code/Hacks/ConsoleHelp.cs` (`help [filter]`) in the same change.
Dev hacks are console toggles on the static `GameHacks` class.

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

## Mark's guidelines

### Art style

- **Lighting is the Valheim recipe** (2026-10-02): the pixel look is asset-side — low-res textures
  (256×256 for routine work; terrain ground tiles 512 px over 4 m = 128 texels / m, Mark 2026-11-03), point filtering, faceted normals baked in the mesh
  or `PixelFlatShading` on `pixel_lit.shader` — then the engine's normal sun, shadows, SSAO, fog,
  bloom and tonemap. **Never** a screen-space pixelation, dither or cel / toon filter (all tried and
  rejected). Local lights (fires, torches, lanterns) are steppy (`PixelLightSteps`); the sun stays
  smooth. Detail: `Code/Environment/docs/ENVIRONMENT.md`.
- **Low-poly models:** varied face sizes, no primitive-readable silhouettes, foliage = layered alpha
  leaf cards. Elm bark that was accepted: a sparse connected crack net, varied line weight, 3–4
  low-contrast grey-brown tones — hue shifts, moss, dither and speckle read as creepy.
- **Scatter / foliage** (plants, mushrooms, ferns, weeds, rocks …) is always **one standalone object
  per model**, never a baked cluster; clusters come from several instances with varied height / tilt /
  yaw. Small plants are 2.5–5× life size so they read at distance.
- **Water:** no invented water colour, no pattern lines, no mirrored objects — the bed shows through,
  the sky comes in by Fresnel, silhouettes mask the sky.
- **Generated models:** LODs switch at 100 / 200 / 300 m (vmdl `switch_threshold` 400 / 800 / 1200).
  Each iteration gets new versioned names (`…_v2`) and the previous version's files are deleted.
  Inspect every variant's render yourself before sending, then send one individual render per model.
- **Trees:** type 1 (elm) fells into a log that splits; type 2 (Rumple's clover tree) drops its crown
  as one body that splits on landing — never an explosion. Both on `ChopableTree`.

### Gameplay and UI

- Combat code never writes the pawn root rotation, `EyeAngles` or the camera — model facing goes
  through the Body renderer child only.
- Melee damage never comes from weapon colliders — only from the phased server attack on
  `PlayerCombat` (windup → charge → active → recovery, `AGENTS.md`). Timings live in
  `Assets/data/melee_weapon_classes.json`; every timing edit also updates `combat_timings.csv` at the
  repo root.
- Hit reaction lives on `PlayerAnimation` and ticks from both `OnUpdate` and `OnPreRender`.
- Tools and weapons lose durability only when the action actually did something.
- Traps and similar placeables are build-hammer pieces in `build_pieces.json`, never inventory items.
- World-event systems (time trials, arena, future stations) are wired entirely onto their
  interactable button / sign object.
- Player data (crews, quests, map, character state) is player-scoped and scene-independent, saved
  under `2Tgames/players/<steamid>/` in `FileSystem.Data` and written through on change.
- Host authority: `CombatAuthority` (melee), `VitalsAuthority` (pools), `BuildAuthority` (placement).
  Clients send intent; the host validates and broadcasts.
- **Menus:** clicks are screen-rect hit tests through the soft-cursor overlay (no real mouse
  events), one click never hold-to-confirm, nothing scrolled out of view is hittable, panels never
  reflow when content changes, body text ≈ 17 px, headers ≈ 20 px, icon buttons ≈ 52 px.

### Performance

The target is **5.0 ms (200 fps) on Mark's RTX 4090** in every `perf_suite` row. When Mark says
"FPS is bad", run the **fps routine** below — the same tests every time.

---

## Routines (hand-off jobs)

A routine is a job Claude runs and works inside — playing, rendering, measuring — to see how the game
has to change to meet Mark's specs (Mark 2026-10-27). Mark starts one and walks away; it is not a
pass/fail check (those are *Automated tests* above). They live in `.claude/skills/*/SKILL.md` and are
indexed, with the shared contract and a template, in `.claude/ROUTINES.md`:

- **art routine** (`/art-routine`) — redesign one model / texture / material from real-life source
  material; 256×256 textures; verified in lightingTest.
- **code qc routine** (`/code-qc-routine`) — audit a code scope for bloat and per-frame hazards.
- **bug test routine** (`/bug-test-routine`) — reproduce, instrument, fix, pass three runs in the live editor.
- **fps routine** — caps lifted (`fps_max 0`, `fps_max_inactive 0`), run `perf_suite` (six fly-camera
  rows: hover 3 m, 2 m at 55 m/s turning, 2 m at 5 m/s, 500 m up at 55 m/s, 500 m straight down, pawn
  standing), then `perf_bisect` (`Code/World/Terrain/TerrainPerfBench.Bisect.cs`) as the pawn and as
  the fly camera; the two should land at about the same frame time. `perf_scenario N` holds one row,
  `hud_profile 1` splits the pawn HUD.

---

## Paths

```
Code/
  Player/          the five umbrella owners + partials, equipment (+ .Armor), tools (ToolBuildHammer, ToolHoe + HoeMode menu,
                   ToolPickaxe, ToolWireStripper), PlayerTerraform (pickaxe chip / hoe grade + paint intent to the host), PlayerInventoryInteraction (+ .Bed / .Vehicle / .DevSupply / .TimeTrial), PlayerFarming
  Combat/          CombatAuthority, ArrowProjectile, StuckArrow, Tests/CombatTest (combat_test)
  Vitals/          VitalsAuthority, regen gates, ArmorMitigation, StatusEffectCatalog
  Entity/          EntityBrain (+ .Breach / .Raid / .Dormancy), EntityLocomotion, EntityCombat, EntityEnemySetup, Animals/
  Building/        catalog, snap layout / placement (BuildSnapPlacement, BuildColliderSnap, BuildModuleDimensions),
                   BuildAuthority, piece health, BuildStructuralIntegrity, ShelterProbe, BuildBed, BuildDoor, nav bake
  Inventory/       PlayerInventory, PlayerHotbar, containers, resource + equipment catalogs
  Crafting/ Food/ Quests/ Skills/ Augments/   JSON-backed catalogs
  Crew/ Arena/ Camps/ Boss/ Raids/ Farming/ Circuits/ Map/ TimeTrial/   one system each (see AGENTS.md)
  Dungeon/         BoxDungeonGenerator + DungeonLayout / Geometry / MapModel; CaveDungeonGenerator + CaveLayout / CaveGeometry
  Vehicles/        Vehicle (+ .Physics / .Seats / .Fuel / .Health / .Hover), VehicleSeat, VehicleDevCommands
  Props/           StasisPod (arrival flight, landing sequence, lamps / screens / light, serial plates), StasisPodSpawner (first-spawn arrivals within 25 m of the spawn centre, pod_test),
                   StasisPodSerials (per-world counter from 885922), StasisPodPuff, StasisPodDevCommands (pod_spawn / pod_door / pod_test); the pawn's side is PlayerMovement.StasisPod.cs
  Environment/     WindSystem, grass, ChopableTree, MineableRock, FireLightFlicker, BearTrap, traps   (docs/ENVIRONMENT.md)
  World/Environment/  EnvironmentDayNightCycle, SkyDome, AerialPerspective (distance desaturation + haze pass), CalderaRing (the far mountain wall), SkyPresetCatalog, SunShadowDistance
  World/Terrain/   streaming, chunk mesh, biome population, sea, rivers, grass, world save IO, TerrainPerfBench
  World/TerrainPreview/  offline world generation pipeline + settings   (docs/TERRAIN_PREVIEW.md)
  World/Voxel/     voxel digging: field, mesher, chunk set, edit log; prototype scene + live-world columns (TerrainWorldManager.Voxel.cs)   (docs/VOXEL.md)
  Hacks/           GameHacks, ConsoleHelp
  Playtest/        PlaytestLog (playtest_log / playtest_mark host telemetry → data/2Tgames/playtest/*.jsonl; Tools/playtest_report.py reads it)
  UI/              PlayerScreenHud, HUDs, Menu/ sections, Circuits/, Theme/ (UiTheme + UiStyle + UiThemeStyleSheet; themes in data/ui_themes.json; docs/UI_THEME.md)
  Networking/      host spawn, synced UI content, connection identity
  GameBuildLabel.cs
Editor/            Terrain Noise Preview tool, world manager widget
Assets/
  data/            catalog JSON (*Catalog.EnsureLoaded) — adding content is usually a JSON edit
  prefabs/         player/basicplayer · entity/scavT1 · build/* (incl. vehicle_*) · environment/* · props/stasis_pod
  models/player/   human_male_sword.vmdl (pawn body: engine human + sword clips + animgraphs/human_sword.vanmgrph),
                   animations/human@sword_*.fbx (Blender/scripts/create_sword_anims.py)
  {models,materials,prefabs}/environment/<biome>/<category>/
                   biomes cloverHills · redwoodForest · amberDunes · mountain;
                   categories trees · rocks · groundFoliage · resources (+ tests, blenderFiles for models);
                   shared materials in materials/environment/shared/, sky / sun / moon in materials/environment/sky/;
                   a Tripo FBX keeps its textures/ folder beside the model
  materials/terrain/   terrain_ground.vmat (+ _far), ground_*.png tiles
  shaders/         pixel_lit (F_WIND, F_TERRAIN_SPLAT), grass_blade, sea_waves.hlsl, riverwater, environment/sky_dome
  scenes/          terrainTest · testscene1 · lightingTest (look playground) · dungeonBoxTest · caveVertTest · voxelTest
  terrain/preview/ Generate Preview bundles + .latest_preview.json
Tools/             make_human_sword_assets.py (regenerates the sword model + graph from the engine's citizen_human sources)
                   item_models/ (generate_item_models.py: the 51 inventory item models - resources, tools, weapons, armor, food, seeds, trophies -
                   built from faceted primitives in plain Python, OBJ + vmdl + 256 px texture per item under Assets/models/<category>/<name>/; README there)
Blender/scripts/   create_* / split_* generators; create_stasis_pod.py (spawn pod: models, textures, vmdls, prefab, previews); create_sword_anims.py (human-rig sword clips); create_mineable_rocks.py (stone drops, breakable rocks, sectioned boulders + spires: models, LODs, prefabs); import_artist_pack.py for Rumple's Tripo packs
                   (Blender X → engine Y, weapons origin at the butt, build pieces bbox-centred + SizesMeters row)
                   Blender 5.1: "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python <script>
combat_timings.csv · VERIFY.md · IDEAS.md · AGENTS.md · .claude/ROUTINES.md · .claude/skills/
.claude/settings.json + .claude/hooks/   automated-test gates (combat_test_gate.py)
```

---

## Removed from this file (2026-10-27 slim-down)

- **Layout mechanics for wind, grass, tree wind, chopping stages, sky / day-night, lighting shader
  knobs, terrain sea / load screen / fly camera / ground splat** → moved word for word to
  `Code/Environment/docs/ENVIRONMENT.md`.
- **Layout mechanics for dungeon, cave, vehicles, hover platform, map, circuits, camps, bosses, raids,
  farming, armor, doors, beds** → dropped here; `AGENTS.md` already has a section for each.
- **Voxel prototype detail** → already in `Code/World/Voxel/docs/VOXEL.md`.
- **Dated design quotes** inside the layout (e.g. "Mark 2026-10-13: …") → kept only where they are
  still a rule, now under *Mark's guidelines*.
- **Long fps routine paragraph** → shortened; the measured history stays in `AGENTS.md`.
- **The Cursor setup intro and the csproj `<Version>` note** → Cursor rules deleted, project is Claude-only.
- **`[extras]` as the tag for build label bump commits** → label bumps are no longer committed alone.
- **"Working notes" section** → its rules moved under *Mark's guidelines* and *Paths*.
