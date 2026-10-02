using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// Repeatable terrain performance benchmark for terrain-test scenes (console: <c>perf_bench [label]</c>).
/// <para>
/// Drives the fly camera over one fixed route so every run sees the same world (same seed = same
/// trees): three ground-level stations where the camera stands at eye height and turns a full circle
/// (render cost of the scatter around a player), then a straight low flight that crosses many chunks
/// (streaming / scatter-spawn hitches). Each phase waits for streaming to settle before it measures.
/// </para>
/// <para>
/// Per phase: frame-time average, p50 / p95 / p99, 1 % low FPS, worst frame and hitch counts, plus the
/// loaded chunk / tree / sapling / prop counts. The report is logged and written as JSON to
/// <c>FileSystem.Data</c> <c>perf/</c> (Windows: <c>sbox/data/local/survivalgamebasics#local/perf/</c>).
/// <c>perf_bench_baseline</c> promotes the latest report to <c>perf/terrain_baseline.json</c>; every
/// later run prints its deltas against that baseline.
/// </para>
/// Measure with the same window size, graphics settings and FPS cap each time — vsync / a frame cap
/// hide real headroom.
/// </summary>
public sealed class TerrainPerfBench : GameObjectSystem
{
	const string Folder = "perf";
	const string BaselinePath = "perf/terrain_baseline.json";
	const string LatestPath = "perf/terrain_latest.json";

	/// <summary>A frame longer than this is a visible hitch.</summary>
	const double HitchMs = 33.3;
	const double BigHitchMs = 50.0;

	/// <summary>Streaming must settle this long (queue empty) before a phase starts measuring; capped.</summary>
	const float SettleSeconds = 2f;
	const float SettleTimeoutSeconds = 20f;

	const float EyeHeightMeters = 1.8f;

	enum PhaseKind { Spin, Flight }

	sealed record Phase( string Name, PhaseKind Kind, Vector2 StartMeters, Vector2 EndMeters, float Seconds, float HeightMeters );

	/// <summary>
	/// The route, in world meters from the world center. Keep it fixed so reports stay comparable;
	/// changing it makes the old baseline meaningless (re-run <c>perf_bench_baseline</c>).
	/// </summary>
	static readonly Phase[] Route =
	[
		new( "ground_center", PhaseKind.Spin, new Vector2( 0f, 0f ), default, 12f, EyeHeightMeters ),
		new( "ground_east", PhaseKind.Spin, new Vector2( 900f, 350f ), default, 12f, EyeHeightMeters ),
		new( "ground_southwest", PhaseKind.Spin, new Vector2( -700f, -600f ), default, 12f, EyeHeightMeters ),
		// ~55 m/s (default fly speed) at treetop height: streams a new chunk ring every ~1.2 s.
		new( "flight", PhaseKind.Flight, new Vector2( -1400f, 200f ), new Vector2( 1400f, 200f ), 50f, 30f ),
	];

	public sealed class PhaseResult
	{
		public string Name { get; set; }
		public int Frames { get; set; }
		public double AvgFps { get; set; }
		public double AvgMs { get; set; }
		public double P50Ms { get; set; }
		public double P95Ms { get; set; }
		public double P99Ms { get; set; }
		public double Low1PercentFps { get; set; }
		public double MaxMs { get; set; }
		public int Hitches33Ms { get; set; }
		public int Hitches50Ms { get; set; }
		public int Chunks { get; set; }
		public int FullDetailChunks { get; set; }
		public int Trees { get; set; }
		public int Saplings { get; set; }
		public int Props { get; set; }
		public int ModelRenderers { get; set; }
	}

	public sealed class Report
	{
		public string Label { get; set; }
		public string BuildLabel { get; set; }
		public string TimestampUtc { get; set; }
		public string Scene { get; set; }
		public int WorldSeed { get; set; }
		public int ScreenWidth { get; set; }
		public int ScreenHeight { get; set; }
		public Dictionary<string, string> Settings { get; set; } = new();
		public List<PhaseResult> Phases { get; set; } = new();
	}

	static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	bool _running;
	int _phaseIndex;
	bool _settling;
	float _phaseTime;
	float _settleTime;
	float _settleQuiet;
	readonly List<double> _frameMs = new();
	readonly Stopwatch _frameClock = new();
	Report _report;
	TerrainWorldManager _manager;
	TerrainTestFlyCamera _fly;
	bool _flyWasLocked;

	public TerrainPerfBench( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Tick, "TerrainPerfBench" );
	}

	static TerrainPerfBench Find() => Sandbox.Game.ActiveScene?.GetSystem<TerrainPerfBench>();

	[ConCmd( "perf_bench" )]
	public static void ConCmdStart( string label = "" )
	{
		var bench = Find();
		if ( bench is null )
		{
			Log.Warning( "[PerfBench] No active scene." );
			return;
		}

		bench.Start( label );
	}

	[ConCmd( "perf_bench_stop" )]
	public static void ConCmdStop()
	{
		var bench = Find();
		if ( bench is { _running: true } )
			bench.Finish( aborted: true );
	}

	/// <summary>
	/// Perf A/B switch for the sun / moon shadows (<c>perf_shadows 0|1</c>). A static flag because
	/// <see cref="EnvironmentDayNightCycle"/> re-applies light settings every frame and reads it there.
	/// </summary>
	public static bool SunShadowsOff { get; private set; }

	static bool ParseOn( string value ) =>
		value?.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

	/// <summary><c>perf_trees 0</c> hides all terrain scatter (trees, saplings, rocks, sticks); <c>perf_trees 1</c> brings it back.</summary>
	[ConCmd( "perf_trees" )]
	public static void ConCmdTrees( string on = "1" )
	{
		var manager = Sandbox.Game.ActiveScene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( manager is null || !manager.IsValid() )
		{
			Log.Warning( "[PerfBench] No terrain world in this scene." );
			return;
		}

		manager.VegetationHidden = !ParseOn( on );
		Log.Info( $"[PerfBench] Terrain scatter {(manager.VegetationHidden ? "HIDDEN" : "shown")}." );
	}

	/// <summary><c>perf_shadows 0</c> turns sun / moon shadows off; <c>perf_shadows 1</c> back on.</summary>
	[ConCmd( "perf_shadows" )]
	public static void ConCmdShadows( string on = "1" )
	{
		SunShadowsOff = !ParseOn( on );
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is not null )
		{
			foreach ( var light in scene.GetAllComponents<DirectionalLight>() )
				light.Shadows = !SunShadowsOff;
		}

		Log.Info( $"[PerfBench] Sun shadows {(SunShadowsOff ? "OFF" : "on")}." );
	}

	/// <summary>
	/// <c>perf_aimassist 0|1</c>: grapple aim assist on the local pawn (up to ~50 traces a frame into
	/// nearby trees while a grapple is equipped). Runtime only; respawning resets it to the prefab value.
	/// </summary>
	[ConCmd( "perf_aimassist" )]
	public static void ConCmdAimAssist( string on = "1" )
	{
		var enabled = ParseOn( on );
		var count = 0;
		foreach ( var movement in Sandbox.Game.ActiveScene?.GetAllComponents<PlayerMovement>() ?? [] )
		{
			if ( movement.IsProxy )
				continue;

			movement.AimAssistEnabled = enabled;
			count++;
		}

		Log.Info( count == 0 ? "[PerfBench] No local pawn (press L first)." : $"[PerfBench] Grapple aim assist {(enabled ? "on" : "OFF")}." );
	}

	/// <summary>
	/// <c>perf_player hud|body|anim 0|1</c>: switch one part of the local pawn off / on to bisect what the
	/// pawn costs. hud = the screen HUD (PlayerScreenHud + its ScreenPanel); body = every model renderer on
	/// the pawn (citizen, clothing, held props); anim = the citizen animgraph (PlayerAnimation +
	/// CitizenAnimationHelper; the body freezes). Diagnostic only — gameplay may misbehave while a part
	/// is off; flip it back with 1 or respawn.
	/// </summary>
	[ConCmd( "perf_player" )]
	public static void ConCmdPlayer( string part = "", string on = "1", string except = "" )
	{
		var scene = Sandbox.Game.ActiveScene;
		var pawn = scene?.GetAllComponents<PlayerVitals>().FirstOrDefault( v => v.IsValid() && v.IsLocalInputOwnedPawn() )?.GameObject;
		if ( pawn is null )
		{
			Log.Warning( "[PerfBench] No local pawn (press L first)." );
			return;
		}

		var enabled = ParseOn( on );
		var count = 0;
		switch ( part?.Trim().ToLowerInvariant() )
		{
			case "hud":
				foreach ( var c in pawn.Components.GetAll<PlayerScreenHud>( FindMode.EverythingInSelfAndDescendants ) )
				{ c.Enabled = enabled; count++; }
				foreach ( var c in pawn.Components.GetAll<ScreenPanel>( FindMode.EverythingInSelfAndDescendants ) )
				{ c.Enabled = enabled; count++; }
				break;
			case "body":
				foreach ( var c in pawn.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
				{ c.Enabled = enabled; count++; }
				break;
			case "anim":
				foreach ( var c in pawn.Components.GetAll<PlayerAnimation>( FindMode.EverythingInSelfAndDescendants ) )
				{ c.Enabled = enabled; count++; }
				foreach ( var c in pawn.Components.GetAll<Sandbox.Citizen.CitizenAnimationHelper>( FindMode.EverythingInSelfAndDescendants ) )
				{ c.Enabled = enabled; count++; }
				break;
			case "move":
				// Engine controller + our movement: the pawn stops responding and simply stands.
				foreach ( var c in pawn.Components.GetAll<PlayerController>( FindMode.EverythingInSelfAndDescendants ) )
				{ c.Enabled = enabled; count++; }
				foreach ( var c in pawn.Components.GetAll<PlayerMovement>( FindMode.EverythingInSelfAndDescendants ) )
				{ c.Enabled = enabled; count++; }
				break;
			case "list":
				// Every component on the pawn hierarchy with its object and state — what the other switches see.
				foreach ( var c in pawn.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ) )
				{
					Log.Info( $"[PerfBench]   {(c.Enabled ? "on " : "OFF")} {c.GetType().Name} @ {c.GameObject.Name}" );
					count++;
				}
				break;
			case "logic":
				// Every Survival.* gameplay component on the pawn except vitals, HUD, animation, menu and
				// movement (each has its own switch). Interaction, harvest, combat, equipment, tools, crew…
				// `except` (comma-separated type names) keeps those enabled, for a binary split when the
				// cost is not additive: single components moved the average ~1 fps each, the group ~80.
				// Logs each type it touches so a component nobody thought of shows up by name.
				var keep = (except ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
				foreach ( var c in pawn.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ) )
				{
					// Type.Namespace is not whitelisted; the full name is.
					if ( c.GetType().FullName?.StartsWith( "Survival.", StringComparison.Ordinal ) != true )
						continue;
					if ( c is PlayerVitals or PlayerScreenHud or PlayerAnimation or PlayerGameMenuController or PlayerMovement )
						continue;

					var name = c.GetType().Name;
					var kept = false;
					foreach ( var k in keep )
						kept |= string.Equals( k, name, StringComparison.OrdinalIgnoreCase );
					if ( kept )
						continue;

					c.Enabled = enabled;
					count++;
					Log.Info( $"[PerfBench]   logic {(enabled ? "on " : "OFF")} {name} @ {c.GameObject.Name}" );
				}
				break;
			default:
				// Any Survival component by type name (case-insensitive), e.g. perf_player PlayerInventoryInteraction 0.
				foreach ( var c in pawn.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ) )
				{
					if ( !string.Equals( c.GetType().Name, part?.Trim(), StringComparison.OrdinalIgnoreCase ) )
						continue;
					c.Enabled = enabled;
					count++;
				}

				if ( count == 0 )
				{
					Log.Warning( "[PerfBench] perf_player hud|body|anim|move|logic|list|<ComponentTypeName> 0|1 [logic: except=A,B,C]" );
					return;
				}
				break;
		}

		Log.Info( $"[PerfBench] Pawn {part} {(enabled ? "on" : "OFF")} ({count} components)." );
	}

	/// <summary>
	/// <c>perf_ai 0|1</c>: every enemy / animal brain and locomotion in the scene (bodies freeze where they
	/// stand). With no pawn present the brains only wander; with one they scan pawns and trace sight
	/// lines each frame, so this is a pawn-only cost.
	/// </summary>
	[ConCmd( "perf_ai" )]
	public static void ConCmdAi( string on = "1" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null )
			return;

		var enabled = ParseOn( on );
		var count = 0;
		foreach ( var c in scene.GetAllComponents<EntityBrain>() )
		{ c.Enabled = enabled; count++; }
		foreach ( var c in scene.GetAllComponents<AnimalBrain>() )
		{ c.Enabled = enabled; count++; }
		foreach ( var c in scene.GetAllComponents<EntityLocomotion>() )
		{ c.Enabled = enabled; count++; }
		foreach ( var c in scene.GetAllComponents<NavMeshAgent>() )
		{ c.Enabled = enabled; count++; }

		Log.Info( $"[PerfBench] Entity AI {(enabled ? "on" : "OFF")} ({count} components)." );
	}

	/// <summary>
	/// <c>perf_fov &lt;degrees|0&gt;</c>: force the pawn camera's field of view (0 = back to the user's
	/// preference). The fly camera is 60°; the pawn takes Preferences.FieldOfView, usually 90+ — at
	/// ground level that is a much larger slice of the tree scatter on screen.
	/// </summary>
	[ConCmd( "perf_fov" )]
	public static void ConCmdFov( string degrees = "0" )
	{
		PlayerMovement.PerfFovOverride = float.TryParse( degrees, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f ) ? f : 0f;
		Log.Info( PlayerMovement.PerfFovOverride > 0f
			? $"[PerfBench] Pawn FOV forced to {PlayerMovement.PerfFovOverride:0}°."
			: "[PerfBench] Pawn FOV back to preference." );
	}

	/// <summary>Copies the latest report to the baseline that later runs compare against.</summary>
	[ConCmd( "perf_bench_baseline" )]
	public static void ConCmdBaseline()
	{
		if ( !FileSystem.Data.FileExists( LatestPath ) )
		{
			Log.Warning( "[PerfBench] No report yet — run perf_bench first." );
			return;
		}

		FileSystem.Data.WriteAllText( BaselinePath, FileSystem.Data.ReadAllText( LatestPath ) );
		Log.Info( $"[PerfBench] Latest report is now the baseline ({BaselinePath})." );
	}

	void Start( string label )
	{
		if ( _running )
		{
			Log.Warning( "[PerfBench] Already running (perf_bench_stop to abort)." );
			return;
		}

		_manager = Scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( _manager is null || !_manager.IsValid() || _manager.IsWorldLoading )
		{
			Log.Warning( "[PerfBench] Needs a loaded terrain world (terrainTest)." );
			return;
		}

		// Fly cam only: a spawned playable pawn owns the camera and must not be driven from here.
		_fly = Scene.GetAllComponents<TerrainTestFlyCamera>().FirstOrDefault( f => f.IsValid() && f.Enabled );
		if ( _fly is null )
		{
			Log.Warning( "[PerfBench] Needs the terrain-test fly camera active (not a spawned playable pawn)." );
			return;
		}

		_flyWasLocked = _fly.InputLocked;
		_fly.SetInputLocked( true );

		_report = new Report
		{
			Label = string.IsNullOrWhiteSpace( label ) ? "run" : label.Trim(),
			BuildLabel = global::Game.GameBuildLabel.Display,
			TimestampUtc = DateTime.UtcNow.ToString( "yyyy-MM-dd HH:mm:ss" ),
			Scene = Scene.Name,
			WorldSeed = _manager.WorldSeed,
			ScreenWidth = (int)Screen.Width,
			ScreenHeight = (int)Screen.Height,
			Settings = CaptureSettings( _manager ),
		};

		_running = true;
		_phaseIndex = 0;
		BeginPhase();
		Log.Info( $"[PerfBench] Started '{_report.Label}' — {Route.Length} phases, ~{Route.Sum( p => p.Seconds ):0} s + streaming settle. Hands off the mouse." );
	}

	static Dictionary<string, string> CaptureSettings( TerrainWorldManager m ) => new()
	{
		["ScatterHidden"] = m.VegetationHidden.ToString(),
		["SunShadowsOff"] = SunShadowsOff.ToString(),
		["StreamRadiusChunks"] = m.StreamRadiusChunks.ToString(),
		["ChunkSizeMeters"] = m.ChunkSizeMeters.ToString( "0" ),
		["CollisionRangeMeters"] = m.CollisionRangeMeters.ToString( "0" ),
		["VegetationScatterEnabled"] = m.VegetationScatterEnabled.ToString(),
		["VegetationSkipFarLodChunks"] = m.VegetationSkipFarLodChunks.ToString(),
		["VegetationCellSpacingMeters"] = m.VegetationCellSpacingMeters.ToString( "0.##" ),
		["VegetationSpawnChance01"] = m.VegetationSpawnChance01.ToString( "0.##" ),
		["VegetationMaxTreesPerChunk"] = m.VegetationMaxTreesPerChunk.ToString(),
		["VegetationCloverSaplingSpacingMeters"] = m.VegetationCloverSaplingSpacingMeters.ToString( "0.##" ),
		["VegetationCloverSaplingChance01"] = m.VegetationCloverSaplingChance01.ToString( "0.##" ),
		["VegetationCloverSaplingMaxPerChunk"] = m.VegetationCloverSaplingMaxPerChunk.ToString(),
		["CloverTreePrefab0"] = m.VegetationCloverTreePrefabs?.FirstOrDefault() ?? "",
	};

	void BeginPhase()
	{
		var phase = Route[_phaseIndex];
		_settling = true;
		_settleTime = 0f;
		_settleQuiet = 0f;
		_phaseTime = 0f;
		_frameMs.Clear();
		PlaceCamera( phase, 0f );
	}

	// ---------------------------------------------------------------- perf_watch

	bool _watching;
	readonly Stopwatch _watchClock = new();
	RealTimeSince _watchWindow;
	int _watchFrames;
	double _watchSumMs;
	double _watchMaxMs;
	int _watchSlowFrames;
	TerrainWorldManager.StreamStats _watchStats;
	string _watchLight = "";
	CameraComponent _watchCamera;
	Vector3 _watchCamPos;
	// Per-frame jitter (largest single-frame change this second): a camera that twitches a fraction of
	// a unit every frame makes shadows swim, which reads as the whole scene's lighting flickering.
	Vector3 _jitterLastCamPos;
	Rotation _jitterLastCamRot;
	Vector3 _jitterLastPawnPos;
	float _jitterCamMaxUnits;
	float _jitterCamMaxDeg;
	float _jitterPawnMaxUnits;
	int _jitterCamMovingFrames;

	/// <summary>
	/// <c>perf_watch 1|0</c>: one log line per second with what can make the whole view flicker or hitch —
	/// frame time (avg / worst / frames over 20 ms), chunk loads / unloads / LOD remeshes / scatter
	/// near-flips (collider + shadow toggles), the sun state (logged with CHANGED when it differs from the
	/// previous second), a main-camera switch, and how far the camera moved. Stand still while it strobes
	/// and read which column moves in step with it.
	/// </summary>
	[ConCmd( "perf_watch" )]
	public static void ConCmdWatch( string on = "1" )
	{
		var bench = Find();
		if ( bench is null )
			return;

		bench._watching = ParseOn( on );
		bench._watchClock.Restart();
		bench._watchWindow = 0f;
		bench._watchFrames = 0;
		bench._watchSumMs = 0;
		bench._watchMaxMs = 0;
		bench._watchSlowFrames = 0;
		bench._watchStats = bench.Scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault()?.Stats ?? default;
		bench._watchLight = "";
		bench._watchCamera = bench.Scene.Camera;
		bench._watchCamPos = bench._watchCamera?.WorldPosition ?? default;
		Log.Info( $"[PerfWatch] {(bench._watching ? "on — one line per second" : "off")}." );
	}

	void TickWatch()
	{
		var ms = _watchClock.Elapsed.TotalMilliseconds;
		_watchClock.Restart();
		_watchFrames++;
		_watchSumMs += ms;
		_watchMaxMs = Math.Max( _watchMaxMs, ms );
		if ( ms > 20d )
			_watchSlowFrames++;

		var frameCam = Scene.Camera;
		if ( frameCam is not null )
		{
			var dp = (frameCam.WorldPosition - _jitterLastCamPos).Length;
			var dr = Rotation.Difference( _jitterLastCamRot, frameCam.WorldRotation ).Angle();
			if ( _watchFrames > 1 )
			{
				_jitterCamMaxUnits = MathF.Max( _jitterCamMaxUnits, dp );
				_jitterCamMaxDeg = MathF.Max( _jitterCamMaxDeg, dr );
				if ( dp > 0.001f || dr > 0.001f )
					_jitterCamMovingFrames++;
			}

			_jitterLastCamPos = frameCam.WorldPosition;
			_jitterLastCamRot = frameCam.WorldRotation;
		}

		var pawn = Scene.GetAllComponents<PlayerVitals>().FirstOrDefault( v => v.IsValid() && v.IsLocalInputOwnedPawn() )?.GameObject;
		if ( pawn is not null )
		{
			if ( _watchFrames > 1 )
				_jitterPawnMaxUnits = MathF.Max( _jitterPawnMaxUnits, (pawn.WorldPosition - _jitterLastPawnPos).Length );
			_jitterLastPawnPos = pawn.WorldPosition;
		}

		if ( _watchWindow < 1f )
			return;

		var manager = Scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		var stats = manager?.Stats ?? default;
		var light = Scene.GetAllComponents<DirectionalLight>().FirstOrDefault( l => l.IsValid() );
		var lightState = light is null
			? "none"
			: $"shadows={light.Shadows} color={light.LightColor} sky={light.SkyColor} rot={light.WorldRotation.Angles()}";
		var lightChanged = _watchLight.Length > 0 && lightState != _watchLight;
		_watchLight = lightState;

		var cam = Scene.Camera;
		var camSwitched = cam != _watchCamera;
		var camMoved = cam is null ? 0f : TerrainWorldUnits.EngineToMeters( (cam.WorldPosition - _watchCamPos).Length );
		_watchCamera = cam;
		_watchCamPos = cam?.WorldPosition ?? default;

		Log.Info( $"[PerfWatch] avg {_watchSumMs / Math.Max( 1, _watchFrames ):0.0} ms, worst {_watchMaxMs:0.0} ms, >20ms {_watchSlowFrames} | "
			+ $"chunks +{stats.Loads - _watchStats.Loads} -{stats.Unloads - _watchStats.Unloads} remesh {stats.Remeshes - _watchStats.Remeshes} nearFlips {stats.NearFlips - _watchStats.NearFlips} | "
			+ $"cam {(camSwitched ? "SWITCHED " : "")}moved {camMoved:0.0} m, per-frame max {_jitterCamMaxUnits:0.000} u / {_jitterCamMaxDeg:0.000} deg, moving frames {_jitterCamMovingFrames}/{_watchFrames} | "
			+ $"pawn per-frame max {_jitterPawnMaxUnits:0.000} u | sun {(lightChanged ? "CHANGED " : "")}{lightState}" );
		_jitterCamMaxUnits = 0f;
		_jitterCamMaxDeg = 0f;
		_jitterPawnMaxUnits = 0f;
		_jitterCamMovingFrames = 0;

		_watchStats = stats;
		_watchWindow = 0f;
		_watchFrames = 0;
		_watchSumMs = 0;
		_watchMaxMs = 0;
		_watchSlowFrames = 0;
	}

	// ── perf_components: automated per-component cost table ─────────────────────────────────

	/// <summary>Frames measured per step (baseline, then each component off in turn, then baseline again).</summary>
	const int AutoFramesPerStep = 120;
	/// <summary>Frames skipped after each toggle so OnDisabled / OnEnabled work does not pollute the window.</summary>
	const int AutoWarmupFrames = 10;

	bool _autoRunning;
	readonly List<Component> _autoComponents = new();
	readonly List<(string Name, double AvgMs)> _autoResults = new();
	int _autoIndex = -1;          // -1 = leading baseline, Count = trailing baseline
	int _autoFrame;
	double _autoSumMs;
	double _autoBaselineMs;
	readonly Stopwatch _autoClock = new();

	/// <summary>
	/// <c>perf_components</c>: measures the local pawn's gameplay components one at a time — the same set
	/// as <c>perf_player logic</c> — by disabling each for <see cref="AutoFramesPerStep"/> frames and
	/// comparing the average frame time against an all-on baseline taken before and after. Prints a
	/// table sorted by saving. Stand still while it runs (~1 s per component). Hand bisecting by eye
	/// could not tell 0.1 ms steps apart; this can.
	/// </summary>
	[ConCmd( "perf_components" )]
	public static void ConCmdComponents()
	{
		var bench = Find();
		if ( bench is null )
			return;

		var scene = Sandbox.Game.ActiveScene;
		var pawn = scene?.GetAllComponents<PlayerVitals>().FirstOrDefault( v => v.IsValid() && v.IsLocalInputOwnedPawn() )?.GameObject;
		if ( pawn is null )
		{
			Log.Warning( "[PerfBench] No local pawn (press L first)." );
			return;
		}

		bench._autoComponents.Clear();
		foreach ( var c in pawn.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( c.GetType().FullName?.StartsWith( "Survival.", StringComparison.Ordinal ) != true )
				continue;
			if ( c is PlayerVitals or PlayerScreenHud or PlayerAnimation or PlayerGameMenuController or PlayerMovement )
				continue;
			if ( !c.Enabled )
				continue;
			bench._autoComponents.Add( c );
		}

		bench._autoResults.Clear();
		bench._autoIndex = -1;
		bench._autoFrame = 0;
		bench._autoSumMs = 0;
		bench._autoBaselineMs = 0;
		bench._autoRunning = true;
		bench._autoClock.Restart();
		Log.Info( $"[PerfBench] Measuring {bench._autoComponents.Count} pawn components, ~{(bench._autoComponents.Count + 2) * (AutoFramesPerStep + AutoWarmupFrames) / 120f:0} s — stand still." );
	}

	void TickAutoComponents()
	{
		var ms = _autoClock.Elapsed.TotalMilliseconds;
		_autoClock.Restart();
		_autoFrame++;
		if ( _autoFrame <= AutoWarmupFrames )
			return;

		_autoSumMs += ms;
		if ( _autoFrame < AutoWarmupFrames + AutoFramesPerStep )
			return;

		var avg = _autoSumMs / AutoFramesPerStep;
		var count = _autoComponents.Count;
		if ( _autoIndex == -1 )
			_autoBaselineMs = avg;
		else if ( _autoIndex < count )
		{
			var c = _autoComponents[_autoIndex];
			_autoResults.Add( (c.GetType().Name, avg) );
			if ( c.IsValid() )
				c.Enabled = true;
		}
		else
		{
			// Trailing baseline: report against the mean of both so drift over the run is halved.
			_autoBaselineMs = (_autoBaselineMs + avg) * 0.5;
			_autoRunning = false;
			// One Log.Info per row: the console shows only the first line of a multi-line entry.
			Log.Info( $"[PerfBench] Component cost (all-on baseline {_autoBaselineMs:0.00} ms; saving = baseline − avg with that one component off):" );
			foreach ( var (name, a) in _autoResults.OrderByDescending( r => _autoBaselineMs - r.AvgMs ) )
				Log.Info( $"[PerfBench]   {_autoBaselineMs - a,6:+0.00;-0.00} ms  {name}  (avg {a:0.00} ms)" );
			return;
		}

		_autoIndex++;
		_autoFrame = 0;
		_autoSumMs = 0;
		if ( _autoIndex < count && _autoComponents[_autoIndex].IsValid() )
			_autoComponents[_autoIndex].Enabled = false;
	}

	void Tick()
	{
		if ( _watching )
			TickWatch();

		if ( _autoRunning )
			TickAutoComponents();

		if ( !_running )
			return;

		var frameMs = _frameClock.IsRunning ? _frameClock.Elapsed.TotalMilliseconds : 0d;
		_frameClock.Restart();

		if ( _manager is null || !_manager.IsValid() || _fly is null || !_fly.IsValid() )
		{
			Log.Warning( "[PerfBench] Terrain or fly camera went away — aborting." );
			Finish( aborted: true );
			return;
		}

		var phase = Route[_phaseIndex];
		var dt = (float)(frameMs / 1000d);

		if ( _settling )
		{
			// Hold the start pose until streaming is idle for SettleSeconds, so load hitches from the
			// teleport are not counted as this phase's steady-state frames.
			_settleTime += dt;
			_settleQuiet = _manager.PendingStreamChunkCount == 0 ? _settleQuiet + dt : 0f;
			PlaceCamera( phase, 0f );
			if ( _settleQuiet < SettleSeconds && _settleTime < SettleTimeoutSeconds )
				return;

			_settling = false;
			_frameClock.Restart();
			return;
		}

		_frameMs.Add( frameMs );
		_phaseTime += dt;
		PlaceCamera( phase, Math.Clamp( _phaseTime / phase.Seconds, 0f, 1f ) );
		if ( _phaseTime < phase.Seconds )
			return;

		_report.Phases.Add( Summarize( phase.Name ) );
		_phaseIndex++;
		if ( _phaseIndex >= Route.Length )
		{
			Finish( aborted: false );
			return;
		}

		BeginPhase();
	}

	/// <summary>Spin: stand at the station and turn 360°. Flight: straight line, level above the ground.</summary>
	void PlaceCamera( Phase phase, float t )
	{
		var xy = phase.Kind == PhaseKind.Flight
			? Vector2.Lerp( phase.StartMeters, phase.EndMeters, t )
			: phase.StartMeters;
		if ( !_manager.TrySampleGroundMeters( xy.x, xy.y, out var groundZ ) )
			groundZ = 0f;

		var posMeters = new Vector3( xy.x, xy.y, Math.Max( groundZ, 0f ) + phase.HeightMeters );
		_fly.WorldPosition = TerrainWorldUnits.MetersToEngine( posMeters );

		var yaw = phase.Kind == PhaseKind.Flight
			? MathF.Atan2( phase.EndMeters.y - phase.StartMeters.y, phase.EndMeters.x - phase.StartMeters.x ) * (180f / MathF.PI)
			: t * 360f;
		var pitch = phase.Kind == PhaseKind.Flight ? 8f : 0f;
		_fly.WorldRotation = new Angles( pitch, yaw, 0f ).ToRotation();
	}

	PhaseResult Summarize( string name )
	{
		var sorted = _frameMs.OrderBy( x => x ).ToList();
		var count = Math.Max( 1, sorted.Count );
		double Pct( double p ) => sorted.Count == 0 ? 0d : sorted[Math.Clamp( (int)Math.Ceiling( p * count ) - 1, 0, count - 1 )];

		var worstN = Math.Max( 1, count / 100 );
		var worstAvg = sorted.Count == 0 ? 0d : sorted.Skip( sorted.Count - worstN ).Average();
		var avg = sorted.Count == 0 ? 0d : sorted.Average();
		var scatter = _manager.CountScatter();

		return new PhaseResult
		{
			Name = name,
			Frames = sorted.Count,
			AvgMs = Math.Round( avg, 2 ),
			AvgFps = avg > 0 ? Math.Round( 1000d / avg, 1 ) : 0,
			P50Ms = Math.Round( Pct( 0.50 ), 2 ),
			P95Ms = Math.Round( Pct( 0.95 ), 2 ),
			P99Ms = Math.Round( Pct( 0.99 ), 2 ),
			Low1PercentFps = worstAvg > 0 ? Math.Round( 1000d / worstAvg, 1 ) : 0,
			MaxMs = Math.Round( sorted.Count == 0 ? 0 : sorted[^1], 1 ),
			Hitches33Ms = sorted.Count( x => x > HitchMs ),
			Hitches50Ms = sorted.Count( x => x > BigHitchMs ),
			Chunks = scatter.Chunks,
			FullDetailChunks = scatter.FullDetailChunks,
			Trees = scatter.Trees,
			Saplings = scatter.Saplings,
			Props = scatter.Props,
			ModelRenderers = Scene.GetAllComponents<ModelRenderer>().Count(),
		};
	}

	void Finish( bool aborted )
	{
		_running = false;
		_frameClock.Reset();
		if ( _fly is not null && _fly.IsValid() )
			_fly.SetInputLocked( _flyWasLocked );

		if ( aborted || _report is null || _report.Phases.Count == 0 )
		{
			Log.Info( "[PerfBench] Stopped — no report written." );
			return;
		}

		var baseline = LoadBaseline();
		Log.Info( FormatReport( _report, baseline ) );

		if ( !FileSystem.Data.DirectoryExists( Folder ) )
			FileSystem.Data.CreateDirectory( Folder );

		var json = JsonSerializer.Serialize( _report, JsonOptions );
		var safeLabel = new string( _report.Label.Select( c => char.IsLetterOrDigit( c ) || c == '-' || c == '_' ? c : '_' ).ToArray() );
		var path = $"{Folder}/terrain_{DateTime.Now:yyyyMMdd_HHmmss}_{safeLabel}.json";
		FileSystem.Data.WriteAllText( path, json );
		FileSystem.Data.WriteAllText( LatestPath, json );
		Log.Info( $"[PerfBench] Saved {path} (and {LatestPath}).{(baseline is null ? " No baseline yet — run perf_bench_baseline to keep this one." : "")}" );
	}

	static Report LoadBaseline()
	{
		try
		{
			return FileSystem.Data.FileExists( BaselinePath )
				? JsonSerializer.Deserialize<Report>( FileSystem.Data.ReadAllText( BaselinePath ), JsonOptions )
				: null;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[PerfBench] Baseline unreadable: {e.Message}" );
			return null;
		}
	}

	static string FormatReport( Report r, Report baseline )
	{
		var sb = new StringBuilder();
		r.Settings.TryGetValue( "ScatterHidden", out var hidden );
		r.Settings.TryGetValue( "SunShadowsOff", out var shadowsOff );
		sb.AppendLine( $"[PerfBench] '{r.Label}' build {r.BuildLabel}, {r.ScreenWidth}x{r.ScreenHeight}, seed {r.WorldSeed}, trees {(hidden == "True" ? "OFF" : "on")}, shadows {(shadowsOff == "True" ? "OFF" : "on")}"
			+ (baseline is null ? "" : $" — vs baseline '{baseline.Label}' (build {baseline.BuildLabel})") );
		sb.AppendLine( "  phase             avgFPS  1%low   p50ms  p95ms  p99ms  maxms  hitch>33/>50  chunks(full)  trees  saplings  props" );
		foreach ( var p in r.Phases )
		{
			var b = baseline?.Phases.FirstOrDefault( x => x.Name == p.Name );
			sb.AppendLine( $"  {p.Name,-16} {p.AvgFps,6:0.0}  {p.Low1PercentFps,5:0.0}  {p.P50Ms,6:0.00} {p.P95Ms,6:0.00} {p.P99Ms,6:0.00} {p.MaxMs,6:0.0}  {p.Hitches33Ms,5}/{p.Hitches50Ms,-5}  {p.Chunks,5}({p.FullDetailChunks,3})  {p.Trees,6}  {p.Saplings,8}  {p.Props,5}" );
			if ( b is not null )
				sb.AppendLine( $"    vs baseline     {Delta( p.AvgFps, b.AvgFps )}  {Delta( p.Low1PercentFps, b.Low1PercentFps )}  p95 {p.P95Ms - b.P95Ms:+0.00;-0.00} ms  hitches {p.Hitches33Ms - b.Hitches33Ms:+0;-0}  trees {p.Trees - b.Trees:+0;-0}  saplings {p.Saplings - b.Saplings:+0;-0}" );
		}

		return sb.ToString();
	}

	static string Delta( double now, double then )
		=> then <= 0 ? "   n/a" : $"{(now - then) / then * 100d:+0.0;-0.0}%";
}
