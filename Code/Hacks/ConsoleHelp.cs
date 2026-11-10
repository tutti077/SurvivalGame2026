using Sandbox;

namespace Survival;

/// <summary>
/// <c>help [filter]</c> (alias <c>commands</c>): prints every console command this project adds, with its
/// arguments, what it does and an example. The table is maintained by hand — add a row whenever you add a
/// <c>[ConCmd]</c> (CLAUDE.md rule) — because the engine's reflection over ConCmd methods is not reachable
/// from game code and a hand-written line can carry an example, which a signature cannot.
/// </summary>
public static class ConsoleHelp
{
	readonly record struct Row( string Group, string Command, string Args, string What, string Example );

	static readonly Row[] Rows =
	[
		// ---- wind / grass / sky
		new( "World", "windtest", "[1-5]", "Hold a forced gust blowing toward the camera's right (face north, it blows east); 1 breeze … 5 hurricane (the max). Plain windtest switches it off.", "windtest 3" ),
		new( "World", "wind_base", "[0-1]", "Host: the steady wind = the weather (0 calm, 0.25 default, 1 storm); the sea swell grows taller and longer with it. No argument prints it.", "wind_base 1" ),
		new( "World", "wind_toggle", "", "Scene wind on / off live (trees + grass stand still when off).", "wind_toggle" ),
		new( "World", "grass_toggle", "", "World grass on / off live (applies as chunks refresh) — the quickest fps bisect.", "grass_toggle" ),
		new( "World", "cycle_toggle", "", "Day / night cycle group on / off (terrainTest ships it ON since 2026-10-21: cycle + sky dome + disks + moon).", "cycle_toggle" ),
		new( "World", "cycle_day", "", "Jump the cycle to noon.", "cycle_day" ),
		new( "World", "cycle_pause", "", "Freeze / resume the day-night cycle where it is (sun and sky stay lit).", "cycle_pause" ),
		new( "World", "cycle_night", "", "Jump the cycle to midnight.", "cycle_night" ),
		new( "World", "pawn_toggle", "", "terrainTest: the same as L — fly camera → playable pawn, pawn → fly camera.", "pawn_toggle" ),
		new( "World", "nameplates_reload", "", "Re-read data/nameplates.json (overhead nameplate sizes, scale curve, fades, targeting range).", "nameplates_reload" ),
		new( "UI", "ui_theme", "[id|reload]", "UI theme from data/ui_themes.json: no argument lists them, an id switches (saved per machine; menus restyle on their next build, the main menu live), reload re-reads the JSON.", "ui_theme frost" ),

		// ---- hacks
		new( "Hacks", "hacks", "", "Print every hack flag and its value.", "hacks" ),
		new( "Hacks", "give", "<resourceId> [amount]", "Host puts an item straight into your hotbar / inventory.", "give resource_woodBasic 20" ),
		new( "Hacks", "equip", "<resourceId>", "Give one of the item into hotbar slot 1 (slot 2 for a shield), select and equip it.", "equip shield_wood" ),
		new( "Hacks", "hoe_mode", "<till|grass|bare|grade>", "Set the equipped hoe's ground mode without opening its menu (Attack2 in play).", "hoe_mode grade" ),
		new( "Hacks", "hoe_menu", "", "Open / close the equipped hoe's mode menu (dev screenshots; Attack2 does it in play).", "hoe_menu" ),
		new( "Hacks", "swing_dir", "L|R|U|D", "Set the attack the next swing uses (Left, Right, the overhead or the down jab) as mouse movement would; the sword rests on that side.", "swing_dir D" ),
		new( "Hacks", "wingsuit_test", "[meters]", "Lift your pawn this high (default 80 m) and open the wingsuit through the normal deploy path - no wingsuit gear or mid-air jump needed.", "wingsuit_test 120" ),
		new( "Hacks", "stamina_infinite", "[0|1]", "Host: infinite stamina for every player on / off (Settings > Dev tools > Stamina drain does the same; on by default). No argument prints it.", "stamina_infinite 0" ),
		new( "Hacks", "day_lock", "[0|1]", "Host: jump the clock to noon and hold it there on every peer (Settings > Dev tools > Lock daytime). No argument prints it.", "day_lock 1" ),
		new( "Hacks", "wasd_facing", "[0|1]", "Body turns with WASD on / off (Settings > Dev tools does the same): on, the body turns toward the travel direction and S walks toward the camera; off, it faces the camera and strafes / back-pedals. No argument prints it.", "wasd_facing 0" ),
		new( "Hacks", "melee_combo_static", "[0|1]", "Static 3-hit melee combo on / off (Settings > Dev tools does the same): quick clicks go right, left, overhead, a hold is a heavy, Q on the ground stabs. No argument prints it.", "melee_combo_static 1" ),
		new( "Hacks", "special_attack", "", "Press the melee special (Q) once as the key would - static combo mode, on the ground (sword / club stab).", "special_attack" ),
		new( "Hacks", "swing", "[holdSeconds]", "Click the primary attack and hold it this long; held past the windup (e.g. 0.7) it turns heavy. A click mid-swing queues one follow-up.", "swing 0.7" ),
		new( "Hacks", "block", "[holdSeconds]", "Hold the block action (Attack2) as a mouse would — shield raise / guard checks.", "block 2" ),
		new( "Hacks", "sneak", "[holdSeconds]", "Hold the crouch / sneak action as a key would — taming offers (crouch + food in hand), sneak footsteps.", "sneak 20" ),
		new( "Hacks", "status", "<effectId> <seconds>", "Apply a buff / debuff from status_effects.json to your pawn; 0 removes it; 'status clear' drops all. (Engine owns a 'status' command — if nothing happens, this one lost the name.)", "status poisoned 10" ),
		new( "Hacks", "spawnPatrolBot", "", "Host drops a patrol bot 3 m in front of your pawn (Medusa Eye / Hackd testing).", "spawnPatrolBot" ),
		new( "Hacks", "thermalView", "true|false", "Force the Thermal Eye post-process on / off.", "thermalView true" ),
		new( "Hacks", "freeAugments", "true|false", "Enhance / Augment cost nothing. No argument prints the state.", "freeAugments true" ),
		new( "Hacks", "devSupply", "true|false", "Unlock the dev supply: a full, never-emptying stack of every resource and crafting ingredient. No argument prints the state.", "devSupply true" ),
		new( "Hacks", "devSupplyOpen", "", "Open / close the dev supply next to your bag (key B does the same; X is an augment key now). Needs devSupply true.", "devSupplyOpen" ),
		new( "Hacks", "allCrafting", "true|false", "Every recipe craftable anywhere and free, and the hoe's Raise costs no dirt and Cobblestone no stone (the prefab flag Free Materials on PlayerTerraform does the same before runtime). No argument prints the state.", "allCrafting true" ),

		// ---- terrain
		new( "Terrain", "terrain_cam", "<x> <y> <z> [pitch] [yaw]", "Park the fly camera at world metres looking along pitch / yaw (pitch + = down).", "terrain_cam 0 0 300 10 0" ),
		new( "Terrain", "world_find", "<kind> [radius m] [park]", "Nearest generated wheat / spire / boulder / rock / any forage kind, or camp sites, from the camera (the scatter's own planner, read-only); park = fly camera to the nearest.", "world_find spire 1500 park" ),
		new( "Terrain", "terrain_fly", "[m/s] [seconds]", "Fly the fly camera level along its heading at a fixed speed (default 55 m/s ≈ 120 mph for 20 s) — perf runs; terrain_fly 0 stops.", "terrain_fly 55 30" ),
		new( "Nav", "nav_stream_request", "0|1", "Streamed terrain chunk nav bakes: 1 = incremental RequestTilesGeneration (default, no hitch), 0 = synchronous GenerateTiles from physics (A/B).", "nav_stream_request 0" ),
		new( "Terrain", "terrain_horizon", "0|1", "Hide / show the horizon tier (the whole-world coarse terrain past Far View Distance).", "terrain_horizon 0" ),
		new( "Terrain", "terrain_far", "0|1", "Toggle the distant terrain tier (rebuilds it).", "terrain_far 0" ),
		new( "Terrain", "flash_probe", "[seconds] [ignoreSeconds]", "Per-frame image-change readout of the main camera (abrupt change %, change-and-return %); writes the worst frame pair and the frames around the worst vanish-and-return as PPM to the data folder; spikes in the first ignoreSeconds are not recorded. 0 stops.", "flash_probe 26 6" ),
		new( "Terrain", "terrain_stream_offset", "[m/s] [seconds] [startMeters]", "Move the STREAM origin along the camera heading while the camera stays parked (start negative = behind, so the load edge sweeps through the view) — chunk hand-overs happen in front of a still camera for the flash probe; 0 resets.", "terrain_stream_offset 55 20 -700" ),
		new( "Terrain", "terrain_stream_radius", "[chunks] [margin]", "Set the live stream radius / unload margin (flash probe rigs: 5 = hand-over at 320 m, past the elm's LOD3 switch).", "terrain_stream_radius 5 1" ),
		new( "Terrain", "far_trees_mode", "[0|1|2]", "Dev: far-tree draw path — 0 instanced far model, 1 one DrawModel per tree (first 400), 2 instanced dev box.", "far_trees_mode 0" ),
		new( "Terrain", "far_card_calibrate", "[apply]", "Measure the far impostor cards against the LOD3 ring on screen (mean colour + cover per band); apply ambient (by night, cycle paused, 25 s after the jump) sets Far Impostor Ambient Light, apply direct (by day) sets Far Impostor Direct Light, apply tint recolours — run each 3-5 times, alternate night / day twice.", "far_card_calibrate apply direct" ),
		new( "Terrain", "far_trees", "0|1", "Far-tree ring (each tree's lowest LOD past the loaded chunks) on / off, live — the fps bisect for the far forest.", "far_trees 0" ),
		new( "Terrain", "terrain_tp_river", "[index] [mid|source|mouth]", "Park the fly camera over river N (middle, source or mouth) looking downstream; no argument lists the rivers.", "terrain_tp_river 2 source" ),
		new( "Terrain", "sea_tune", "<swell m> [chop m]", "Set the live sea Swell / Chop Amplitude (component values, not saved).", "sea_tune 3 0.5" ),
		new( "Terrain", "terrain_tp_lake", "[nth] [height m] [back m]", "Park the fly camera over the nth-nearest lake shore, looking across the water.", "terrain_tp_lake 2 40 60" ),
		new( "Terrain", "river_mouth", "[n] [back m] [height m] [side m]", "List river mouths (lake / ocean); with n, park the fly camera upstream of mouth n looking across it.", "river_mouth 3 70 25" ),
		new( "Terrain", "river_join_probe", "", "Every river confluence: which river flows into which, where it ends, and the water strip's confluence fade at its last segment ends.", "river_join_probe" ),
		new( "Terrain", "river_surface_probe", "<x> <y> [radius m]", "River segments near a point: river, levels, half widths, and whether the water surface has each one (chunk-owned, far set, open-water cut).", "river_surface_probe 18580 1080 80" ),
		new( "Terrain", "terrain_coast_scan", "[step m] [band m] [min step m]", "Walk every coast (ocean rim + all lakes) on a grid and list height cuts — a rise much steeper than the ground either side, grouped into sites, worst 25 and how many drop to water level. Finds square cuts near water.", "terrain_coast_scan 4 300 1" ),
		new( "Terrain", "terrain_paint_line", "<x0> <y0> <x1> <y1> [points]", "Along a line: height, bed or not, resolved biome, land weights, beach and wet per point (the terrain splat inputs) — finds what makes a band or step in the ground colour.", "terrain_paint_line -1100 -1000 -900 -1000 12" ),
		new( "Terrain", "terrain_bank_probe", "<x> <y>", "Shore inputs at a point: lake mask, open-water threshold, metres to the shore, height before rivers and the river hit (why the ground near water sits where it does).", "terrain_bank_probe -6214 8360" ),
		new( "Terrain", "terrain_paint_probe","[radius m]", "Group lake-bed points round the camera by resolved biome and print their average splat weights, wet, beach and bed height.", "terrain_paint_probe 1500" ),
		new( "Terrain", "sea_probe", "", "The sea / lake under the camera: depth, openness, wind, swell + chop settings and the wave height there right now.", "sea_probe" ),
		new( "Terrain", "terrain_river_check", "[index]", "Measure river N (all when omitted): water level vs ground along the centre line and banks, deepest bed, rim height above the water, carve gaps, water under the ground, and the steepest stretch (where the rapids are).", "terrain_river_check" ),
		new( "Terrain", "terrain_river_trees", "[index]", "Re-plan the large trees on every chunk river N crosses and count the ones standing in its channel (first five with their sample now).", "terrain_river_trees 5" ),
		new( "Terrain", "terrain_probe", "<x> <y> [halfSpan] [step]", "Print pipeline heights on a cross through the point (default ±6 m every 0.5 m).", "terrain_probe 120 -40" ),
		new( "Terrain", "terrain_seams", "[minGapM]", "Compare every loaded chunk's meshed border with its neighbours' and log the steps of at least minGapM (default 0.5) with each side's LOD, water flag and fine-border sides — for walls where a river crosses a chunk border.", "terrain_seams 0.5" ),
		new( "Terrain", "terrain_tp_biome", "[biome] [mode] [minStep]", "Fly to the nearest patch of a biome (redwood, clover, dunes, mountain …).", "terrain_tp_biome redwood" ),
		new( "Voxel", "voxel_dig", "[r] [x y z]", "voxelTest / terrainTest: dig a flat round puck of radius r m (default 1.5) at world metres x y z, or where the camera looks (terrainTest swaps the touched 64 m chunk for voxel ground).", "voxel_dig 2" ),
		new( "Voxel", "voxel_fill", "[r] [x y z]", "voxelTest / terrainTest: fill a puck of ground of radius r m at x y z, or where the camera looks.", "voxel_fill 1" ),
		new( "Voxel", "voxel_flatten", "[r] [x y z]", "voxelTest / terrainTest: the rake, flat — level the ground within r m (default 2.5) to the surrounding surface height: a hole fills, a lump is cut (Reload / R in play; F swaps flat / slope).", "voxel_flatten 3" ),
		new( "Voxel", "voxel_slope", "[r] [x y z]", "voxelTest / terrainTest: the rake, slanted — lay the ground within r m on the surrounding slope, 45 degrees at most.", "voxel_slope 3" ),
		new( "Voxel", "voxel_chip", "[r] [depth]", "voxelTest / terrainTest: the pickaxe's stroke where the camera looks - a disc of radius r m (default 0.6) lying on the surface, cut depth m (default 0.3) into it. In play the equipped pickaxe does this on Attack1.", "voxel_chip 0.6 0.3" ),
		new( "Voxel", "voxel_paint", "<grass|bare|tilled|cobble> [r] [circle|square] [x y z]", "voxelTest / terrainTest: the hoe's paint - the ground inside r m (default 1.5; a square of side 2 r with square) becomes the biome's grass, bare soil, dark tilled soil (seeds sow on it) or cobblestone (the rock tile); no clutter on any of the three, at world metres x y z or where the camera looks. In play the hoe menu (Attack2) picks it, F swaps circle / square; Cobblestone costs 1 stone there.", "voxel_paint cobble 2 square" ),
		new( "Voxel", "voxel_raise", "[r] [h] [circle|square]", "voxelTest / terrainTest: the hoe's raise where the camera looks - the ground inside r m (default 1.5) comes up by one layer h m thick (default 0.25), following the slope. In play the hoe menu's Raise costs dirt.", "voxel_raise 1.5 0.25 square" ),
		new( "Voxel", "voxel_brush", "<r>", "voxelTest / terrainTest: set the digger's brush radius (m) for Attack1 dig / Attack2 fill.", "voxel_brush 2.5" ),
		new( "Voxel", "voxel_info", "", "voxelTest: the field; terrainTest: the voxel columns — chunks drawn, vertices, saved edits, build queue and the last build times.", "voxel_info" ),
		new( "Voxel", "voxel_probe", "<x> <y> [axis]", "Trace straight down either side of a line through world metres x y (default along y = const; axis x: along x = const) and log the hit object, ground z and normal — for checking a seam between voxel columns.", "voxel_probe 38 28" ),
		new( "Voxel", "voxel_reset", "", "voxelTest: forget every saved edit and reseed the field; terrainTest: drop every voxel column and put the heightfield chunks back.", "voxel_reset" ),
		new( "Terrain", "terrain_dump_chunk", "[x] [y]", "Write the chunk under the point (or the camera) as an OBJ-style text file to the data folder.", "terrain_dump_chunk 0 0" ),
		new( "Terrain", "terrain_ledge_diagonals", "0|1", "Toggle the ledge-aware triangulation for A/B dumps.", "terrain_ledge_diagonals 0" ),

		// ---- perf
		new( "Perf", "perf_bench", "[label]", "Start a timed benchmark run; perf_bench_stop prints the report.", "perf_bench grass300" ),
		new( "Perf", "perf_bench_stop", "", "Stop the benchmark and print / save its report.", "perf_bench_stop" ),
		new( "Perf", "perf_bench_baseline", "", "Copy the latest report to the baseline later runs compare against.", "perf_bench_baseline" ),
		new( "Perf", "perf_watch", "1|0", "One log line per second: frame time, chunk loads / remeshes / scatter flips, sun state, camera travel.", "perf_watch 1" ),
		new( "Perf", "perf_trees", "0|1", "Hide / show all terrain scatter (trees, saplings, rocks, sticks).", "perf_trees 0" ),
		new( "Perf", "perf_shadows", "0|1", "Sun / moon shadows off / on — the fastest way to see whether shadow passes are the cost.", "perf_shadows 0" ),
		new( "Perf", "perf_aimassist", "0|1", "Grapple aim assist on the local pawn (up to ~50 traces a frame while a grapple is equipped).", "perf_aimassist 0" ),
		new( "Perf", "perf_player", "hud|body|anim 0|1", "Switch one part of the local pawn off / on to bisect what the pawn costs.", "perf_player hud 0" ),
		new( "Perf", "perf_ai", "0|1", "Every enemy / animal brain and locomotion off / on (bodies freeze).", "perf_ai 0" ),
		new( "Perf", "perf_fov", "<degrees|0>", "Force the pawn camera's field of view (0 = back to the preference).", "perf_fov 60" ),
		new( "Perf", "perf_components", "", "Measure the local pawn's gameplay components one at a time (~1 s each) and print a table sorted by saving.", "perf_components" ),
		new( "Perf", "perf_ui", "0|1", "Every ScreenPanel (HUD, minimap, load screen host) off / on.", "perf_ui 0" ),
		new( "Perf", "perf_entity_anim", "0|1", "Every non-pawn skinned body's animgraph off / on (entities freeze in pose).", "perf_entity_anim 0" ),
		new( "Perf", "perf_terrain_shadows", "0|1", "The loaded terrain chunks' own shadow casting off / on.", "perf_terrain_shadows 0" ),
		new( "Perf", "perf_veg_objects", "0|1", "Scatter GameObjects alone hidden / shown — far ring and impostors keep drawing.", "perf_veg_objects 0" ),
		new( "Perf", "perf_far_ring", "0|1", "The instanced LOD3 far ring draw alone hidden / shown.", "perf_far_ring 0" ),
		new( "Perf", "perf_impostors", "0|1", "The far impostor cards alone hidden / shown.", "perf_impostors 0" ),
		new( "Perf", "perf_types", "[minCount]", "Disable every component TYPE in the scene in turn (1 s each, types with at least minCount instances, default 1) and print a table sorted by saving — where Update / Animation go.", "perf_types 5" ),
		new( "Perf", "hud_profile", "0|1", "Per-frame cost of every pawn HUD sub-tick (pickups, wheel, build, minimap, passive readouts…), one line a second.", "hud_profile 1" ),
		new( "Perf", "perf_suite", "", "Mark's standard fps test (~45 s, hands off, fly camera, caps lifted): hover 3 m, 2 m at 55 m/s, 2 m at 5 m/s, 500 m at 55 m/s, 500 m straight down, pawn standing — PASS/FAIL table against 5.0 ms (200 fps).", "perf_suite" ),
		new( "Perf", "perf_scenario", "[1-6]", "Park the fly camera in that perf_suite row and HOLD it (row 6 spawns the pawn) so perf_cpu / perf_gpu / perf_bisect can dissect one row; perf_scenario 0 releases.", "perf_scenario 5" ),
		new( "Perf", "perf_bisect", "", "The fps routine: 3 s baseline, then FOV 60 / HUD / body / anim / crosshair / aim assist / AI / shadows / scatter / objects-only / far ring / impostors / chunk shadows / entity anim / UI / grass / wind off one at a time, 3 s each, table sorted by saving. Run as pawn and as fly cam, stand still (~40 s).", "perf_bisect" ),

		// ---- nav / AI
		new( "Nav", "nav_trace", "1|0", "Per-entity movement / breach / raid diagnostics in the console (off by default).", "nav_trace 1" ),
		new( "Nav", "nav_draw", "1|0", "Draw entity nav paths.", "nav_draw 1" ),
		new( "Nav", "nav_regen", "[extraHeight]", "Full nav regenerate with the ceiling raised (default +1500 u).", "nav_regen" ),
		new( "Nav", "nav_regen_local", "[halfExtent]", "Rebake only the tiles around the player (default 512 u).", "nav_regen_local" ),
		new( "Nav", "nav_radius", "[radius]", "Set the nav agent radius, then nav_regen.", "nav_radius 10" ),
		new( "Nav", "nav_show", "[radius]", "Draw walkable nav around the player for a few seconds (default 600 u).", "nav_show" ),
		new( "Nav", "nav_mode", "full|tiles|request", "How a structure change rebuilds nav.", "nav_mode tiles" ),
		new( "Nav", "nav_probe", "", "Probe nav at the player's position and print what it finds.", "nav_probe" ),
		new( "Nav", "campRings", "[1|0]", "Draw the enemy camp wander / max-travel rings.", "campRings 1" ),
		new( "Nav", "entity_memory", "", "Dump the host's chunk memory: entities that despawned far from every player (position, health, slot), restored when a player comes back.", "entity_memory" ),

		// ---- dungeon / cave
		new( "Dungeon", "dungeon_regen", "[seed]", "Rebuild the box dungeon (random seed when omitted).", "dungeon_regen 42" ),
		new( "Dungeon", "dungeon_floors", "[maxFloor|all]", "Limit how many floors are built.", "dungeon_floors 2" ),
		new( "Dungeon", "dungeon_info", "", "Print the current dungeon layout stats.", "dungeon_info" ),
		new( "Dungeon", "cave_regen", "[seed]", "Rebuild the cave chain (random seed when omitted).", "cave_regen 7" ),
		new( "Dungeon", "cave_info", "", "Print the cave layout stats.", "cave_info" ),
		new( "Dungeon", "cave_tp", "[top|bottom|junction|chamber|ledge] [index]", "Teleport to a cave landmark.", "cave_tp ledge 3" ),

		// ---- quests / map
		new( "Quests", "quests_list", "", "Print quest states.", "quests_list" ),
		new( "Quests", "quests_reset", "", "Reset all quest progress.", "quests_reset" ),
		new( "Quests", "quests_report", "<eventId> <match> <amount>", "Report a quest event by hand (\"\" for match when unused).", "quests_report entity_killed fox 5" ),
		new( "Map", "map_markup_clear", "", "Wipe your local map pins and pen strokes for this world.", "map_markup_clear" ),
		new( "Map", "map_ping_test", "", "Ping your own position without the map or the middle mouse.", "map_ping_test" ),

		// ---- vehicles
		new( "Vehicles", "vehicle_spawn", "[pieceId] [metersAhead]", "Host spawns a vehicle ahead of you (vehicle_dune_buggy, vehicle_hover_platform).", "vehicle_spawn vehicle_hover_platform 6" ),
		new( "Vehicles", "vehicle_enter", "", "Enter the nearest vehicle.", "vehicle_enter" ),
		new( "Vehicles", "vehicle_exit", "", "Leave the vehicle.", "vehicle_exit" ),
		new( "Vehicles", "vehicle_fuel", "[units]", "Put fuel in the nearest vehicle (default 5).", "vehicle_fuel 10" ),
		new( "Vehicles", "vehicle_drive", "[throttle] [steer] [seconds]", "Scripted drive input on the driven vehicle.", "vehicle_drive 1 0.5 3" ),
		new( "Vehicles", "vehicle_look", "<pitch> <yaw>", "Point the pawn's eyes (seat head-tracking check).", "vehicle_look 10 45" ),
		new( "Vehicles", "vehicle_hover", "[meters]", "Hover platform: set the target height, or print the hover readout.", "vehicle_hover 12" ),
		new( "Vehicles", "vehicle_info", "", "Print the nearest vehicle's state.", "vehicle_info" ),
		new( "Vehicles", "vehicle_damage", "[amount]", "Hit the nearest vehicle through its DamageReceiver.", "vehicle_damage 50" ),
		new( "Vehicles", "vehicle_stash", "[resourceId] [count]", "Drop a stack into the nearest vehicle's storage (wreck-drop test).", "vehicle_stash resource_woodBasic 5" ),
		new( "Vehicles", "vehicle_storage", "", "Open the nearest vehicle's storage box.", "vehicle_storage" ),
		new( "Vehicles", "vehicle_tip", "[side|roof]", "Lay the nearest vehicle on its side or roof.", "vehicle_tip roof" ),
		new( "Vehicles", "vehicle_flip", "[roof|wheels|front|back]", "Press Flip on the nearest vehicle (optionally stand on a side first).", "vehicle_flip" ),
		new( "Vehicles", "vehicle_test", "", "Timed throttle / brake / reverse / turn / fuel script with logged numbers.", "vehicle_test" ),
		new( "Vehicles", "vehicle_demo", "", "Scripted showcase run with a chase camera (vehicle_spawn + vehicle_fuel first).", "vehicle_demo" ),
		new( "Vehicles", "vehicle_rolltest", "[mph] [turnSeconds] [steer]", "Full throttle to a speed, then a held steer; logs whether it rolled.", "vehicle_rolltest 60 2 1" ),

		// ---- props
		new( "Props", "pod_spawn", "[metersAhead]", "Host stands a stasis pod on the ground ahead of you (default 4 m), door facing you - no flight.", "pod_spawn 3" ),
		new( "Props", "pod_door", "[open|close|toggle]", "Host swings the nearest stasis pod's door (default toggle).", "pod_door open" ),
		new( "Props", "pod_test", "[count] [here]", "Host crash-lands this many empty stasis pods (default 10) around the spawn centre - or around you with here. Needs a StasisPodSpawner in the scene (terrainTest has one).", "pod_test 10 here" ),
		new( "Props", "pod_arrivals", "[0|1]", "Players arrive by crash-landing pod on their first spawn of the session (default 1). No argument prints it.", "pod_arrivals 0" ),
		new( "Tests", "combat_test", "", "Automated melee regression suite (play mode, testscene1): scripted fights, PASS/FAIL per case.", "combat_test" ),
		new( "Tests", "anim_test", "", "Sword animation anatomy suite (play mode): rests, shoulder swaps, look sweep, every attack; PASS/FAIL per scenario.", "anim_test" ),

		// ---- playtest logger
		new( "Playtest", "playtest_log", "[0|1]", "Host: the playtest logger on (a new <steam name>_<date_time>_v<build>.jsonl under data/2Tgames/playtest/) or off. Hits, kills with time-to-kill and the killer's pools, player damage and deaths, stamina empties, 2 s vitals samples, blocks, grapple attach / detach with speed and distance, crafts, builds, item gains, pawn positions, joins / leaves, each client's fps and menu time. No argument prints the state. Tools/playtest_report.py reads the file.", "playtest_log 1" ),
		new( "Playtest", "playtest_mark", "<text>", "Drop a marker line in the running playtest log; quote more than one word.", "playtest_mark \"toys unlocked\"" ),
	];

	[ConCmd( "help" )]
	public static void ConCmdHelp( string filter = "" ) => Print( filter );

	/// <summary>Alias — the engine may already own 'help'.</summary>
	[ConCmd( "commands" )]
	public static void ConCmdCommands( string filter = "" ) => Print( filter );

	static void Print( string filter )
	{
		filter = (filter ?? "").Trim();
		var any = false;
		string group = null;
		foreach ( var row in Rows )
		{
			if ( filter.Length > 0
			     && !row.Command.Contains( filter, StringComparison.OrdinalIgnoreCase )
			     && !row.Group.Contains( filter, StringComparison.OrdinalIgnoreCase )
			     && !row.What.Contains( filter, StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( row.Group != group )
			{
				group = row.Group;
				Log.Info( $"── {group} ──" );
			}

			var args = string.IsNullOrEmpty( row.Args ) ? "" : " " + row.Args;
			Log.Info( $"{row.Command}{args}\n    {row.What}\n    e.g. {row.Example}" );
			any = true;
		}

		if ( !any )
			Log.Info( $"[help] no command matches '{filter}'. Try: help wind, help perf, help vehicle." );
		else
			Log.Info( $"[help] {Rows.Length} project commands; 'help <word>' filters by name, group or description." );
	}
}
