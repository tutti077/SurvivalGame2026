using System;
using System.Diagnostics;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>perf_bisect</c>: the fps routine. Measures a 3 s baseline, then switches one thing off at a time for 3 s
/// each — pawn FOV to 60, AI, the pawn HUD / body / animgraph / crosshair, grapple aim assist, sun shadows,
/// terrain scatter, world grass, wind — restoring it before the next, then a trailing baseline. Prints one
/// table sorted by saving. Run it as the pawn AND as the fly camera; the two tables side by side say what the
/// pawn costs. Stand still while it runs (~40 s). Driven through the sbox MCP console (Mark 2026-10-13).
/// </summary>
public sealed partial class TerrainPerfBench
{
	float _bisectSecondsPerStep = 3f;
	float _bisectWarmupSeconds = 0.4f;

	sealed record BisectStep( string Name, Action Off, Action On );

	readonly List<BisectStep> _bisectSteps = new();
	readonly List<(string Name, double AvgMs)> _bisectResults = new();
	readonly Stopwatch _bisectClock = new();
	bool _bisectRunning;
	int _bisectIndex;          // -1 = leading baseline, Count = trailing baseline
	float _bisectStepStarted;
	double _bisectSumMs;
	int _bisectFrames;
	double _bisectBaselineMs;

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
		_bisectFrames = 0;
		_bisectClock.Restart();
		if ( _bisectIndex >= 0 && _bisectIndex < _bisectSteps.Count )
			_bisectSteps[_bisectIndex].Off();
	}

	void TickBisect()
	{
		var ms = _bisectClock.Elapsed.TotalMilliseconds;
		_bisectClock.Restart();
		var elapsed = Time.Now - _bisectStepStarted;
		if ( elapsed < _bisectWarmupSeconds )
			return;

		_bisectSumMs += ms;
		_bisectFrames++;
		if ( elapsed < _bisectWarmupSeconds + _bisectSecondsPerStep )
			return;

		var avg = _bisectFrames > 0 ? _bisectSumMs / _bisectFrames : 0;
		var count = _bisectSteps.Count;
		if ( _bisectIndex == -1 )
		{
			_bisectBaselineMs = avg;
		}
		else if ( _bisectIndex < count )
		{
			_bisectResults.Add( (_bisectSteps[_bisectIndex].Name, avg) );
			_bisectSteps[_bisectIndex].On();
		}
		else
		{
			_bisectBaselineMs = (_bisectBaselineMs + avg) * 0.5;
			_bisectRunning = false;
			Log.Info( $"[PerfBench] bisect done — baseline {_bisectBaselineMs:0.00} ms ({1000.0 / Math.Max( _bisectBaselineMs, 0.01 ):0} fps). Saving = baseline − avg with that one thing off:" );
			foreach ( var (name, a) in _bisectResults.OrderByDescending( r => _bisectBaselineMs - r.AvgMs ) )
				Log.Info( $"[PerfBench]   {_bisectBaselineMs - a,6:+0.00;-0.00} ms  {name}  (avg {a:0.00} ms = {1000.0 / Math.Max( a, 0.01 ):0} fps)" );
			return;
		}

		_bisectIndex++;
		BeginBisectStep();
	}
}
