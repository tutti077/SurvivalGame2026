using System;
using System.Diagnostics;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>perf_suite</c>: Mark's standard fps test (2026-10-20: "when I say FPS is bad I need you to run the same tests every
/// time"). Six scenarios from the fly camera's current spot, each 1.5 s warm-up + 5 s measured, one table at the end:
/// hover 3 m above the terrain; 2 m off the terrain at 55 m/s turning; 2 m off the terrain at 5 m/s turning; 500 m up at
/// 55 m/s turning, looking 15° down; 500 m up looking straight down, still; the player pawn standing. Target: 200 fps
/// (5.0 ms) in every row on an RTX 4090. Lift the caps first (<c>fps_max 0</c>, <c>fps_max_inactive 0</c>) and keep the
/// hands off the mouse — the camera is scripted (<see cref="TerrainTestFlyCamera.BeginScript"/>). ~45 s.
/// </summary>
public sealed partial class TerrainPerfBench
{
	const float SuiteWarmupSeconds = 1.5f;
	const float SuiteMeasureSeconds = 5f;
	const float SuiteTargetMs = 5f;

	sealed record SuiteScenario( string Name, Action Begin, Action End );

	readonly List<SuiteScenario> _suite = new();
	readonly List<float> _suiteFrames = new();
	readonly List<(string Name, float AvgMs, float P50Ms, float P99Ms, float WorstMs)> _suiteResults = new();
	readonly Stopwatch _suiteClock = new();
	bool _suiteRunning;
	int _suiteIndex;
	float _suiteStarted;
	Vector3 _suiteAnchorMeters;
	float _suiteAnchorYaw;
	float _suiteGroundMeters;
	bool _suitePawnSpawned;

	int _suiteHeld; // perf_scenario: 1-based row being held, 0 = none

	[ConCmd( "perf_suite" )]
	public static void ConCmdSuite()
	{
		var bench = Find();
		if ( bench is null )
			return;
		if ( bench._suiteRunning || bench._bisectRunning || bench._suiteHeld > 0 )
		{
			Log.Warning( "[PerfBench] a run is already in progress (perf_scenario 0 releases a held row)." );
			return;
		}
		if ( !bench.PrepareSuite( out var anchor, out var ground ) )
			return;

		bench._suiteResults.Clear();
		bench._suiteIndex = 0;
		bench._suiteRunning = true;
		bench.BeginSuiteScenario();
		Log.Info( $"[PerfBench] perf_suite: {bench._suite.Count} scenarios from {anchor.x:0},{anchor.y:0} (ground {ground:0} m), ~{bench._suite.Count * (SuiteWarmupSeconds + SuiteMeasureSeconds):0} s — hands off." );
	}

	/// <summary>
	/// <c>perf_scenario N</c> parks the camera in suite row N (1–6) and HOLDS it so perf_cpu / perf_gpu / perf_bisect can
	/// dissect that one row; <c>perf_scenario 0</c> (or no argument) releases it. Row 6 spawns the pawn and removes it on release.
	/// </summary>
	[ConCmd( "perf_scenario" )]
	public static void ConCmdScenario( int row = 0 )
	{
		var bench = Find();
		if ( bench is null )
			return;
		if ( bench._suiteRunning || bench._bisectRunning )
		{
			Log.Warning( "[PerfBench] a run is already in progress." );
			return;
		}

		if ( bench._suiteHeld > 0 )
		{
			bench._suite[bench._suiteHeld - 1].End();
			Log.Info( $"[PerfBench] perf_scenario: released row {bench._suiteHeld}." );
			bench._suiteHeld = 0;
		}
		if ( row <= 0 )
			return;

		if ( !bench.PrepareSuite( out _, out _ ) )
			return;
		if ( row > bench._suite.Count )
		{
			Log.Warning( $"[PerfBench] perf_scenario: rows are 1–{bench._suite.Count}." );
			return;
		}

		bench._suiteHeld = row;
		bench._suite[row - 1].Begin();
		Log.Info( $"[PerfBench] perf_scenario: holding row {row} — {bench._suite[row - 1].Name}. perf_scenario 0 releases." );
	}

	bool PrepareSuite( out Vector3 anchorOut, out float groundOut )
	{
		anchorOut = default;
		groundOut = 0f;
		var bench = this;
		var scene = Sandbox.Game.ActiveScene;
		var fly = scene?.GetAllComponents<TerrainTestFlyCamera>().FirstOrDefault();
		var manager = scene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( fly is null || !fly.IsValid() || manager is null || !manager.IsValid() )
		{
			Log.Warning( "[PerfBench] perf_suite needs the terrainTest fly camera and a terrain world." );
			return false;
		}
		if ( scene.GetAllComponents<PlayerVitals>().Any( v => v.IsValid() && v.IsLocalInputOwnedPawn() ) )
		{
			Log.Warning( "[PerfBench] perf_suite: switch to the fly camera first (L / pawn_toggle)." );
			return false;
		}

		bench._suiteAnchorMeters = TerrainWorldUnits.EngineToMeters( fly.WorldPosition );
		bench._suiteAnchorYaw = fly.WorldRotation.Yaw();
		if ( !manager.TrySampleGroundMeters( bench._suiteAnchorMeters.x, bench._suiteAnchorMeters.y, out bench._suiteGroundMeters ) )
			bench._suiteGroundMeters = bench._suiteAnchorMeters.z;

		var anchor = bench._suiteAnchorMeters;
		var yaw = bench._suiteAnchorYaw;
		var ground = bench._suiteGroundMeters;
		var high = ground + 500f;

		var s = bench._suite;
		s.Clear();
		s.Add( new( "hover 3 m above terrain, still",
			() => { fly.SetPoseMeters( anchor.WithZ( ground + 3f ), 0f, yaw ); TerrainTestFlyCamera.BeginScript( 0f, 0f, 3f, 0f, 0f ); },
			TerrainTestFlyCamera.EndScript ) );
		s.Add( new( "2 m off terrain, 55 m/s, turning 12°/s",
			() => { fly.SetPoseMeters( anchor.WithZ( ground + 2f ), 0f, yaw ); TerrainTestFlyCamera.BeginScript( 55f, 12f, 2f, 0f, 0f ); },
			TerrainTestFlyCamera.EndScript ) );
		s.Add( new( "2 m off terrain, 5 m/s, turning 20°/s",
			() => { fly.SetPoseMeters( anchor.WithZ( ground + 2f ), 0f, yaw ); TerrainTestFlyCamera.BeginScript( 5f, 20f, 2f, 0f, 0f ); },
			TerrainTestFlyCamera.EndScript ) );
		s.Add( new( "500 m up, 55 m/s, turning 12°/s, 15° down",
			() => { fly.SetPoseMeters( anchor.WithZ( high ), 15f, yaw ); TerrainTestFlyCamera.BeginScript( 55f, 12f, -1f, high, 15f ); },
			TerrainTestFlyCamera.EndScript ) );
		s.Add( new( "500 m up, straight down, still",
			() => { fly.SetPoseMeters( anchor.WithZ( high ), 89f, yaw ); TerrainTestFlyCamera.BeginScript( 0f, 0f, -1f, high, 89f ); },
			TerrainTestFlyCamera.EndScript ) );
		s.Add( new( "player pawn standing",
			() =>
			{
				fly.SetPoseMeters( anchor.WithZ( ground + 2f ), 0f, yaw );
				TerrainTestFlyCamera.EndScript();
				TerrainTestPlayerSpawn.ConCmdPawnToggle();
				bench._suitePawnSpawned = true;
			},
			() =>
			{
				if ( bench._suitePawnSpawned )
					TerrainTestPlayerSpawn.ConCmdPawnToggle();
				bench._suitePawnSpawned = false;
				fly.SetPoseMeters( anchor, 0f, yaw );
			} ) );

		anchorOut = anchor;
		groundOut = ground;
		return true;
	}

	void BeginSuiteScenario()
	{
		_suiteFrames.Clear();
		_suiteStarted = Time.Now;
		_suiteClock.Restart();
		_suite[_suiteIndex].Begin();
	}

	void TickSuite()
	{
		var ms = (float)_suiteClock.Elapsed.TotalMilliseconds;
		_suiteClock.Restart();
		var elapsed = Time.Now - _suiteStarted;
		if ( elapsed < SuiteWarmupSeconds )
			return;
		_suiteFrames.Add( ms );
		if ( elapsed < SuiteWarmupSeconds + SuiteMeasureSeconds )
			return;

		_suite[_suiteIndex].End();
		var frames = _suiteFrames.OrderBy( f => f ).ToList();
		var avg = frames.Count > 0 ? frames.Average() : 0f;
		var p50 = frames.Count > 0 ? frames[frames.Count / 2] : 0f;
		var p99 = frames.Count > 0 ? frames[Math.Min( frames.Count - 1, (int)(frames.Count * 0.99f) )] : 0f;
		var worst = frames.Count > 0 ? frames[^1] : 0f;
		_suiteResults.Add( (_suite[_suiteIndex].Name, avg, p50, p99, worst) );

		_suiteIndex++;
		if ( _suiteIndex < _suite.Count )
		{
			BeginSuiteScenario();
			return;
		}

		_suiteRunning = false;
		Log.Info( "[PerfBench] perf_suite — avg ms (fps) | p50 | p99 | worst   target 5.0 ms = 200 fps:" );
		foreach ( var (name, a, p50v, p99v, w) in _suiteResults )
			Log.Info( $"[PerfBench]   {(a <= SuiteTargetMs ? "PASS" : "FAIL")}  {a,5:0.00} ms ({1000f / Math.Max( a, 0.01f ),4:0} fps) | {p50v,5:0.00} | {p99v,5:0.00} | {w,6:0.0}   {name}" );
	}
}
