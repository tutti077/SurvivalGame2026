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
		new( "World", "wind_toggle", "", "Scene wind on / off live (trees + grass stand still when off).", "wind_toggle" ),
		new( "World", "grass_toggle", "", "World grass on / off live (applies as chunks refresh) — the quickest fps bisect.", "grass_toggle" ),
		new( "World", "cycle_toggle", "", "Day / night cycle group on / off (terrainTest ships it ON since 2026-10-21: cycle + sky dome + disks + moon).", "cycle_toggle" ),
		new( "World", "cycle_day", "", "Jump the cycle to noon (key Y does the same).", "cycle_day" ),
		new( "World", "cycle_night", "", "Jump the cycle to midnight (key U).", "cycle_night" ),
		new( "World", "pawn_toggle", "", "terrainTest: the same as L — fly camera → playable pawn, pawn → fly camera.", "pawn_toggle" ),

		// ---- hacks
		new( "Hacks", "hacks", "", "Print every hack flag and its value.", "hacks" ),
		new( "Hacks", "give", "<resourceId> [amount]", "Host puts an item straight into your hotbar / inventory.", "give resource_woodBasic 20" ),
		new( "Hacks", "equip", "<resourceId>", "Give one of the item into hotbar slot 1, select and equip it (main-hand items).", "equip club_wood" ),
		new( "Hacks", "swing", "[holdSeconds]", "Press and release the primary attack as a mouse would (0.15 = light, longer = heavy).", "swing 0.5" ),
		new( "Hacks", "status", "<effectId> <seconds>", "Apply a buff / debuff from status_effects.json to your pawn; 0 removes it; 'status clear' drops all. (Engine owns a 'status' command — if nothing happens, this one lost the name.)", "status poisoned 10" ),
		new( "Hacks", "spawnPatrolBot", "", "Host drops a patrol bot 3 m in front of your pawn (Medusa Eye / Hackd testing).", "spawnPatrolBot" ),
		new( "Hacks", "thermalView", "true|false", "Force the Thermal Eye post-process on / off.", "thermalView true" ),
		new( "Hacks", "freeAugments", "true|false", "Enhance / Augment cost nothing. No argument prints the state.", "freeAugments true" ),
		new( "Hacks", "devSupply", "true|false", "Unlock the dev supply: a full, never-emptying stack of every resource and crafting ingredient. No argument prints the state.", "devSupply true" ),
		new( "Hacks", "devSupplyOpen", "", "Open / close the dev supply next to your bag (key X does the same). Needs devSupply true.", "devSupplyOpen" ),
		new( "Hacks", "allCrafting", "true|false", "Every recipe craftable anywhere. No argument prints the state.", "allCrafting true" ),

		// ---- terrain
		new( "Terrain", "terrain_cam", "<x> <y> <z> [pitch] [yaw]", "Park the fly camera at world metres looking along pitch / yaw (pitch + = down).", "terrain_cam 0 0 300 10 0" ),
		new( "Terrain", "terrain_fly", "[m/s] [seconds]", "Fly the fly camera level along its heading at a fixed speed (default 55 m/s ≈ 120 mph for 20 s) — perf runs; terrain_fly 0 stops.", "terrain_fly 55 30" ),
		new( "Nav", "nav_stream_request", "0|1", "Streamed terrain chunk nav bakes: 1 = incremental RequestTilesGeneration (default, no hitch), 0 = synchronous GenerateTiles from physics (A/B).", "nav_stream_request 0" ),
		new( "Terrain", "terrain_far", "0|1", "Toggle the distant terrain tier (rebuilds it).", "terrain_far 0" ),
		new( "Terrain", "flash_probe", "[seconds] [ignoreSeconds]", "Per-frame image-change readout of the main camera (abrupt change %, change-and-return %); writes the worst frame pair and the frames around the worst vanish-and-return as PPM to the data folder; spikes in the first ignoreSeconds are not recorded. 0 stops.", "flash_probe 26 6" ),
		new( "Terrain", "terrain_stream_offset", "[m/s] [seconds] [startMeters]", "Move the STREAM origin along the camera heading while the camera stays parked (start negative = behind, so the load edge sweeps through the view) — chunk hand-overs happen in front of a still camera for the flash probe; 0 resets.", "terrain_stream_offset 55 20 -700" ),
		new( "Terrain", "terrain_stream_radius", "[chunks] [margin]", "Set the live stream radius / unload margin (flash probe rigs: 5 = hand-over at 320 m, past the elm's LOD3 switch).", "terrain_stream_radius 5 1" ),
		new( "Terrain", "far_trees_mode", "[0|1|2]", "Dev: far-tree draw path — 0 instanced far model, 1 one DrawModel per tree (first 400), 2 instanced dev box.", "far_trees_mode 0" ),
		new( "Terrain", "far_card_calibrate", "[apply]", "Measure the far impostor cards against the LOD3 ring on screen (mean colour + cover per band); apply ambient (by night, cycle paused, 25 s after the jump) sets Far Impostor Ambient Light, apply direct (by day) sets Far Impostor Direct Light, apply tint recolours — run each 3-5 times, alternate night / day twice.", "far_card_calibrate apply direct" ),
		new( "Terrain", "far_trees", "0|1", "Far-tree ring (each tree's lowest LOD past the loaded chunks) on / off, live — the fps bisect for the far forest.", "far_trees 0" ),
		new( "Terrain", "terrain_tp_river", "[index] [mid|source|mouth]", "Park the fly camera over river N (middle, source or mouth) looking downstream; no argument lists the rivers.", "terrain_tp_river 2 source" ),
		new( "Terrain", "terrain_river_check", "[index]", "Measure river N (all when omitted): water level vs ground along the centre line and banks, deepest bed, rim height above the water.", "terrain_river_check" ),
		new( "Terrain", "terrain_probe", "<x> <y> [halfSpan] [step]", "Print pipeline heights on a cross through the point (default ±6 m every 0.5 m).", "terrain_probe 120 -40" ),
		new( "Terrain", "terrain_tp_biome", "[biome] [mode] [minStep]", "Fly to the nearest patch of a biome (redwood, clover, dunes, mountain …).", "terrain_tp_biome redwood" ),
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
