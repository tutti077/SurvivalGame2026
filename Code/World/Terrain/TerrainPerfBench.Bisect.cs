using System;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>perf_bisect</c>: the fps routine. Measures a 3 s baseline, then switches one thing off at a time for 3 s
/// each — pawn FOV to 60, AI, the pawn HUD / body / animgraph / crosshair, grapple aim assist, sun shadows,
/// terrain scatter, world grass, wind — restoring it before the next, then a trailing baseline. Prints one
/// table sorted by saving. Run it as the pawn AND as the fly camera; the two tables side by side say what the
/// pawn costs. Stand still while it runs (~40 s). Driven through the sbox MCP console (Mark 2026-10-13).
/// CPU is the engine's frame work without the cap wait (<see cref="FrameWorkMs"/>), GPU the latest GPU frame time —
/// both valid on a vsynced window.
/// </summary>
public sealed partial class TerrainPerfBench
{
	/// <summary>
	/// The previous frame's CPU work WITHOUT the vsync / fps_max wait (ms): wall time between two StartUpdate ticks minus
	/// the engine's <c>Idle</c> timing (the cap wait at the flip). <c>PerformanceStats.FrameTime</c> read from game code
	/// is the wall time, so it cannot be used for this. Sampled once per bench tick (<see cref="SampleFrameWork"/>).
	/// </summary>
	static double FrameWorkMs { get; set; }

	readonly System.Diagnostics.Stopwatch _workClock = new();

	void SampleFrameWork()
	{
		var wall = _workClock.IsRunning ? _workClock.Elapsed.TotalMilliseconds : 0d;
		_workClock.Restart();
		FrameWorkMs = Math.Max( 0d, wall - Sandbox.Diagnostics.PerformanceStats.Timings.Idle.AverageMs( 1 ) );
	}

	/// <summary>Latest GPU frame time the engine has (ms; arrives a few frames late).</summary>
	static double FrameGpuMs => Sandbox.Diagnostics.PerformanceStats.GpuFrametime;

	float _bisectSecondsPerStep = 3f;
	float _bisectWarmupSeconds = 0.4f;

	sealed record BisectStep( string Name, Action Off, Action On );

	readonly List<BisectStep> _bisectSteps = new();
	readonly List<(string Name, double AvgMs, double GpuMs)> _bisectResults = new();
	bool _bisectRunning;
	int _bisectIndex;          // -1 = leading baseline, Count = trailing baseline
	float _bisectStepStarted;
	double _bisectSumMs, _bisectGpuSumMs;
	int _bisectFrames;
	double _bisectBaselineMs, _bisectBaselineGpuMs;

	[ConCmd( "perf_bisect" )]
	public static void ConCmdBisect()
	{
		var bench = Find();
		if ( bench is null )
			return;
		if ( bench._bisectRunning )
		{
			Log.Warning( "[PerfBench] bisect already running." );
			return;
		}

		var scene = Sandbox.Game.ActiveScene;
		var pawn = scene?.GetAllComponents<PlayerVitals>().FirstOrDefault( v => v.IsValid() && v.IsLocalInputOwnedPawn() )?.GameObject;
		var steps = bench._bisectSteps;
		steps.Clear();

		if ( pawn is not null )
		{
			steps.Add( new( "pawn FOV 60 (fly cam's)", () => ConCmdFov( "60" ), () => ConCmdFov( "0" ) ) );
			steps.Add( new( "pawn HUD off", () => ConCmdPlayer( "hud", "0" ), () => ConCmdPlayer( "hud", "1" ) ) );
			steps.Add( new( "pawn body off", () => ConCmdPlayer( "body", "0" ), () => ConCmdPlayer( "body", "1" ) ) );
			steps.Add( new( "pawn animgraph off", () => ConCmdPlayer( "anim", "0" ), () => ConCmdPlayer( "anim", "1" ) ) );
			steps.Add( new( "grapple aim assist off", () => ConCmdAimAssist( "0" ), () => ConCmdAimAssist( "1" ) ) );
			var crosshair = pawn.Components.Get<PlayerCrosshair>( FindMode.EverythingInSelfAndDescendants );
			if ( crosshair is not null )
				steps.Add( new( "crosshair off", () => crosshair.Enabled = false, () => crosshair.Enabled = true ) );
		}

		steps.Add( new( "AI off", () => ConCmdAi( "0" ), () => ConCmdAi( "1" ) ) );
		steps.Add( new( "sun shadows off", () => ConCmdShadows( "0" ), () => ConCmdShadows( "1" ) ) );
		steps.Add( new( "terrain scatter hidden", () => ConCmdTrees( "0" ), () => ConCmdTrees( "1" ) ) );
		steps.Add( new( "scatter objects hidden (ring + impostors stay)", () => ConCmdVegObjects( "0" ), () => ConCmdVegObjects( "1" ) ) );
		steps.Add( new( "far ring hidden", () => ConCmdFarRing( "0" ), () => ConCmdFarRing( "1" ) ) );
		steps.Add( new( "impostors hidden", () => ConCmdImpostors( "0" ), () => ConCmdImpostors( "1" ) ) );
		steps.Add( new( "terrain chunk shadows off", () => ConCmdTerrainShadows( "0" ), () => ConCmdTerrainShadows( "1" ) ) );
		steps.Add( new( "entity animgraphs off", () => ConCmdEntityAnim( "0" ), () => ConCmdEntityAnim( "1" ) ) );
		steps.Add( new( "screen UI off", () => ConCmdUi( "0" ), () => ConCmdUi( "1" ) ) );

		var grassVolumes = scene?.GetAllComponents<Sandbox.Clutter.ClutterComponent>()
			.Where( c => c.IsValid() && c.GameObject.Name == "grass" ).Select( c => c.GameObject ).ToList() ?? new();
		if ( grassVolumes.Count > 0 )
			steps.Add( new( $"grass hidden ({grassVolumes.Count} volumes)", () => grassVolumes.ForEach( g => g.Enabled = false ), () => grassVolumes.ForEach( g => g.Enabled = true ) ) );

		if ( WindSystem.Current.IsValid() )
			steps.Add( new( "wind off", () => WindSystem.Current.WindEnabled = false, () => WindSystem.Current.WindEnabled = true ) );

		bench._bisectResults.Clear();
		bench._bisectIndex = -1;
		bench._bisectRunning = true;
		bench.BeginBisectStep();
		bench._bisectSecondsPerStep = 3f;
		bench._bisectWarmupSeconds = 0.4f;
		var seconds = (steps.Count + 2) * (bench._bisectSecondsPerStep + bench._bisectWarmupSeconds);
		Log.Info( $"[PerfBench] bisect: {(pawn is null ? "FLY CAMERA (no pawn)" : "PAWN")}, {steps.Count} switches, ~{seconds:0} s — stand still." );
	}

	void BeginBisectStep()
	{
		_bisectStepStarted = Time.Now;
		_bisectSumMs = 0;
		_bisectGpuSumMs = 0;
		_bisectFrames = 0;
		if ( _bisectIndex >= 0 && _bisectIndex < _bisectSteps.Count )
			_bisectSteps[_bisectIndex].Off();
	}

	void TickBisect()
	{
		var elapsed = Time.Now - _bisectStepStarted;
		if ( elapsed < _bisectWarmupSeconds )
			return;

		_bisectSumMs += FrameWorkMs;
		_bisectGpuSumMs += FrameGpuMs;
		_bisectFrames++;
		if ( elapsed < _bisectWarmupSeconds + _bisectSecondsPerStep )
			return;

		var avg = _bisectFrames > 0 ? _bisectSumMs / _bisectFrames : 0;
		var gpuAvg = _bisectFrames > 0 ? _bisectGpuSumMs / _bisectFrames : 0;
		var count = _bisectSteps.Count;
		if ( _bisectIndex == -1 )
		{
			_bisectBaselineMs = avg;
			_bisectBaselineGpuMs = gpuAvg;
		}
		else if ( _bisectIndex < count )
		{
			_bisectResults.Add( (_bisectSteps[_bisectIndex].Name, avg, gpuAvg) );
			_bisectSteps[_bisectIndex].On();
		}
		else
		{
			_bisectBaselineMs = (_bisectBaselineMs + avg) * 0.5;
			_bisectBaselineGpuMs = (_bisectBaselineGpuMs + gpuAvg) * 0.5;
			_bisectRunning = false;
			Log.Info( $"[PerfBench] bisect done — baseline cpu {_bisectBaselineMs:0.00} ms, gpu {_bisectBaselineGpuMs:0.00} ms. Saving = baseline − avg with that one thing off (cpu | gpu):" );
			foreach ( var (name, a, g) in _bisectResults.OrderByDescending( r => _bisectBaselineMs - r.AvgMs ) )
				Log.Info( $"[PerfBench]   cpu {_bisectBaselineMs - a,6:+0.00;-0.00} | gpu {_bisectBaselineGpuMs - g,6:+0.00;-0.00} ms  {name}  (cpu {a:0.00}, gpu {g:0.00})" );
			return;
		}

		_bisectIndex++;
		BeginBisectStep();
	}
}
