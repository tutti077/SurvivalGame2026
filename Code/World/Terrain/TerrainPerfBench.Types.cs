using System;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>perf_types [minCount]</c>: the same runner as <c>perf_bisect</c>, one step per component TYPE present in the
/// scene — every enabled instance of the type off for one second, then back — so the table says what each kind
/// of component costs per frame (where the Update / Animation categories go when the pawn is not the question).
/// Types the measurement itself needs stay on. ~1.3 s per type; stand still (Mark 2026-10-20: "200 fps, nothing is even here").
/// </summary>
public sealed partial class TerrainPerfBench
{
	static readonly Type[] TypesKeptOn =
	{
		typeof( TerrainPerfBench ), typeof( CameraComponent ), typeof( TerrainTestFlyCamera ), typeof( TerrainWorldManager ),
		typeof( TerrainFarCardCalibrator ), typeof( TerrainFlashProbe ),
	};

	[ConCmd( "perf_types" )]
	public static void ConCmdTypes( int minCount = 1 )
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
		if ( scene is null )
			return;

		var byType = new Dictionary<Type, List<Component>>();
		foreach ( var go in scene.GetAllObjects( true ) )
		foreach ( var c in go.Components.GetAll<Component>( FindMode.EnabledInSelf ) )
		{
			if ( c is null || !c.IsValid() || !c.Enabled )
				continue;
			var type = c.GetType();
			if ( Array.IndexOf( TypesKeptOn, type ) >= 0 )
				continue;
			if ( !byType.TryGetValue( type, out var list ) )
				byType[type] = list = new List<Component>();
			list.Add( c );
		}

		var steps = bench._bisectSteps;
		steps.Clear();
		foreach ( var pair in byType.OrderByDescending( p => p.Value.Count ) )
		{
			if ( pair.Value.Count < Math.Max( 1, minCount ) )
				continue;
			var list = pair.Value;
			steps.Add( new( $"{pair.Key.Name} x{list.Count}",
				() => { foreach ( var c in list ) if ( c.IsValid() ) c.Enabled = false; },
				() => { foreach ( var c in list ) if ( c.IsValid() ) c.Enabled = true; } ) );
		}

		bench._bisectResults.Clear();
		bench._bisectIndex = -1;
		bench._bisectRunning = true;
		bench._bisectSecondsPerStep = 1f;
		bench._bisectWarmupSeconds = 0.3f;
		bench.BeginBisectStep();
		Log.Info( $"[PerfBench] types: {steps.Count} component types, ~{(steps.Count + 2) * 1.3f:0} s — stand still." );
	}
}
