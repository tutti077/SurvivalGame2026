using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Finer fps switches than <c>perf_trees</c> / <c>perf_shadows</c> (Mark 2026-10-20: "we should be at 200 fps,
/// nothing is even here"): the screen UI, every entity animgraph, the terrain chunks' own shadow casting, the
/// scatter GameObjects alone (the far ring stays), the far ring alone, the impostors alone. Each is one
/// <c>0|1</c> console command and one <c>perf_bisect</c> row. Diagnostic only; restore with 1.
/// </summary>
public sealed partial class TerrainPerfBench
{
	static TerrainWorldManager Manager()
	{
		var manager = Sandbox.Game.ActiveScene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( manager is null || !manager.IsValid() )
			Log.Warning( "[PerfBench] No terrain world in this scene." );
		return manager;
	}

	/// <summary><c>perf_ui 0|1</c>: every ScreenPanel in the scene (HUD, minimap, load screen host) off / on.</summary>
	[ConCmd( "perf_ui" )]
	public static void ConCmdUi( string on = "1" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null )
			return;
		var enabled = ParseOn( on );
		var count = 0;
		foreach ( var panel in scene.GetAllComponents<ScreenPanel>() )
		{ panel.Enabled = enabled; count++; }
		Log.Info( $"[PerfBench] Screen UI {(enabled ? "on" : "OFF")} ({count} screen panels)." );
	}

	/// <summary><c>perf_entity_anim 0|1</c>: the animgraph of every skinned body that is not the local pawn's (entities freeze in pose, keep moving).</summary>
	[ConCmd( "perf_entity_anim" )]
	public static void ConCmdEntityAnim( string on = "1" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null )
			return;
		var enabled = ParseOn( on );
		var count = 0;
		foreach ( var body in scene.GetAllComponents<SkinnedModelRenderer>() )
		{
			if ( !body.IsValid() || body.GameObject.Root.Components.Get<PlayerVitals>( FindMode.EverythingInSelfAndDescendants ) is not null )
				continue;
			body.UseAnimGraph = enabled;
			count++;
		}
		Log.Info( $"[PerfBench] Entity animgraphs {(enabled ? "on" : "OFF")} ({count} bodies)." );
	}

	/// <summary><c>perf_terrain_shadows 0|1</c>: the loaded terrain chunks' own shadow casting (trees and the sun stay).</summary>
	[ConCmd( "perf_terrain_shadows" )]
	public static void ConCmdTerrainShadows( string on = "1" )
	{
		var manager = Manager();
		if ( manager is null )
			return;
		var count = manager.SetTerrainChunkShadows( ParseOn( on ) );
		Log.Info( $"[PerfBench] Terrain chunk shadows {(ParseOn( on ) ? "on" : "OFF")} ({count} chunks)." );
	}

	/// <summary><c>perf_veg_objects 0|1</c>: the scatter GameObjects alone (trees, rocks, sticks) — the far ring and impostors keep drawing.</summary>
	[ConCmd( "perf_veg_objects" )]
	public static void ConCmdVegObjects( string on = "1" )
	{
		var manager = Manager();
		if ( manager is null )
			return;
		manager.VegetationObjectsHidden = !ParseOn( on );
		Log.Info( $"[PerfBench] Scatter objects {(manager.VegetationObjectsHidden ? "HIDDEN" : "shown")} (ring + impostors untouched)." );
	}

	/// <summary><c>perf_far_ring 0|1</c>: the instanced LOD3 far ring draw alone (its plans stay cached).</summary>
	[ConCmd( "perf_far_ring" )]
	public static void ConCmdFarRing( string on = "1" )
	{
		var manager = Manager();
		if ( manager is null )
			return;
		manager.FarRingHidden = !ParseOn( on );
		Log.Info( $"[PerfBench] Far ring {(manager.FarRingHidden ? "HIDDEN" : "shown")}." );
	}

	/// <summary><c>perf_impostors 0|1</c>: the far impostor cards alone.</summary>
	[ConCmd( "perf_impostors" )]
	public static void ConCmdImpostors( string on = "1" )
	{
		var manager = Manager();
		if ( manager is null )
			return;
		manager.FarImpostorsEnabled = ParseOn( on );
		Log.Info( $"[PerfBench] Impostors {(manager.FarImpostorsEnabled ? "shown" : "HIDDEN")}." );
	}
}
