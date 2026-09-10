using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Console diagnostics for entity navigation over build pieces.
/// <c>nav_draw 1|0</c> renders the live nav mesh in-game; <c>nav_probe</c> reports the nav settings
/// and whether there is nav under the local player's feet (the same test the breach logic uses to
/// decide whether a player on a floor / roof is reachable), plus what they are standing on.
/// </summary>
public static class NavDebugCommands
{
	/// <summary>`nav_trace 1|0` — every host entity logs a movement / decision snapshot twice a second (`[Trace]`).</summary>
	public static bool TraceEnabled;

	[ConCmd( "nav_trace" )]
	public static void ConCmdNavTrace( string enabled = "1" )
	{
		TraceEnabled = enabled is not ("0" or "false" or "off");
		Log.Info( $"[NavDebug] entity trace {(TraceEnabled ? "ON" : "OFF")}" );
	}

	[ConCmd( "nav_draw" )]
	public static void ConCmdNavDraw( string enabled = "1" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() || scene.NavMesh is null )
		{
			Log.Info( "[NavDebug] no scene / nav mesh" );
			return;
		}

		var on = enabled is not ("0" or "false" or "off");
		scene.NavMesh.DrawMesh = on;
		Log.Info( $"[NavDebug] nav mesh draw {(on ? "ON" : "OFF")}" );
	}

	/// <summary>Full nav regenerate with the ceiling raised (default +1500 u) — proves / fixes the baked-bounds ceiling on small scenes.</summary>
	[ConCmd( "nav_regen" )]
	public static void ConCmdNavRegen( string extraHeight = "1500" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() || scene.NavMesh is null )
		{
			Log.Info( "[NavDebug] no scene / nav mesh" );
			return;
		}

		if ( !float.TryParse( extraHeight, out var extra ) || extra <= 0f )
			extra = 1500f;

		BuildNavMeshSync.RegenerateWithHeadroom( scene, extra );
		Log.Info( $"[NavDebug] regenerated with +{extra:0}u headroom — run nav_probe again" );
	}

	/// <summary>
	/// Set the nav agent radius, then `nav_regen`. Voxel size is not exposed to game code, but
	/// erosion is radius-based (and Recast derives cell size from it), so a smaller radius keeps
	/// more of a lone 2 m platform above the minimum-region cull. `nav_radius 10` is a good first try.
	/// </summary>
	[ConCmd( "nav_radius" )]
	public static void ConCmdNavRadius( string radius = "10" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() || scene.NavMesh is null )
		{
			Log.Info( "[NavDebug] no scene / nav mesh" );
			return;
		}

		if ( !float.TryParse( radius, out var r ) || r <= 0f )
			r = 10f;

		var nav = scene.NavMesh;
		Log.Info( $"[NavDebug] agentRadius {nav.AgentRadius} → {r} — run nav_regen to apply" );
		nav.AgentRadius = r;
	}

	/// <summary>
	/// Draw the nav mesh around the local player for a few seconds: a grid of probes at several
	/// heights, a dot wherever walkable nav exists (green near the ground, warmer higher up).
	/// The engine's own DrawMesh only renders in the editor viewport.
	/// </summary>
	[ConCmd( "nav_show" )]
	public static void ConCmdNavShow( string radius = "600" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() || scene.NavMesh is null )
		{
			Log.Info( "[NavDebug] no scene / nav mesh" );
			return;
		}

		if ( !float.TryParse( radius, out var r ) || r <= 0f )
			r = 600f;

		var pawn = FindLocalPawn( scene );
		if ( pawn is null )
		{
			Log.Info( "[NavDebug] no local pawn" );
			return;
		}

		const float spacing = 24f;
		const float duration = 12f;
		float[] bands = { -96f, -32f, 32f, 96f, 160f, 224f, 288f, 352f };
		var origin = pawn.WorldPosition;
		var nav = scene.NavMesh;
		// Component.DebugOverlay is protected — static code reaches the scene system directly.
		var overlay = scene.GetSystem<DebugOverlaySystem>();
		if ( overlay is null )
		{
			Log.Info( "[NavDebug] no debug overlay system" );
			return;
		}

		var drawn = 0;
		for ( var x = -r; x <= r; x += spacing )
		for ( var y = -r; y <= r; y += spacing )
		{
			foreach ( var band in bands )
			{
				var center = origin + new Vector3( x, y, band );
				var box = new BBox( center - new Vector3( spacing * 0.5f, spacing * 0.5f, 32f ), center + new Vector3( spacing * 0.5f, spacing * 0.5f, 32f ) );
				var sample = nav.GetRandomPoint( box );
				if ( !sample.HasValue )
					continue;

				var height = sample.Value.z - origin.z;
				var t = Math.Clamp( (height + 96f) / 448f, 0f, 1f );
				var color = Color.Lerp( new Color( 0.2f, 0.9f, 0.3f ), new Color( 1f, 0.35f, 0.15f ), t );
				overlay.Sphere( new Sphere( sample.Value + Vector3.Up * 2f, 4f ), color, duration );
				drawn++;
			}
		}

		Log.Info( $"[NavDebug] drew {drawn} nav samples within {r:0}u for {duration:0}s" );
	}

	static GameObject FindLocalPawn( Scene scene )
	{
		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is not null && vitals.IsLocalInputOwnedPawn() )
				return vitals.GameObject;
		}

		return null;
	}

	/// <summary>A/B for the rebake strategy: incremental tiles around the player (unload + GenerateTiles). Then nav_show.</summary>
	[ConCmd( "nav_regen_local" )]
	public static void ConCmdNavRegenLocal( string halfExtent = "512" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() || scene.NavMesh is null )
		{
			Log.Info( "[NavDebug] no scene / nav mesh" );
			return;
		}

		var pawn = FindLocalPawn( scene );
		if ( pawn is null )
		{
			Log.Info( "[NavDebug] no local pawn" );
			return;
		}

		if ( !float.TryParse( halfExtent, out var half ) || half <= 0f )
			half = 512f;

		BuildNavMeshSync.RegenerateLocalTiles( scene, pawn.WorldPosition, half );
	}

	/// <summary>`nav_mode full|tiles|request` — how a structure change rebuilds nav (see BuildNavMeshSync.StructureRebakeMode). Then place a wall and nav_show.</summary>
	[ConCmd( "nav_mode" )]
	public static void ConCmdNavMode( string mode = "" )
	{
		switch ( mode.ToLowerInvariant() )
		{
			case "full": BuildNavMeshSync.RebakeMode = BuildNavMeshSync.StructureRebakeMode.Full; break;
			case "tiles": BuildNavMeshSync.RebakeMode = BuildNavMeshSync.StructureRebakeMode.Tiles; break;
			case "request": BuildNavMeshSync.RebakeMode = BuildNavMeshSync.StructureRebakeMode.Request; break;
			case "": break;
			default:
				Log.Info( "[NavDebug] nav_mode full | tiles | request" );
				return;
		}

		Log.Info( $"[NavDebug] structure rebake mode = {BuildNavMeshSync.RebakeMode}" );
	}

	[ConCmd( "nav_probe" )]
	public static void ConCmdNavProbe()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() || scene.NavMesh is null )
		{
			Log.Info( "[NavDebug] no scene / nav mesh" );
			return;
		}

		var nav = scene.NavMesh;
		Log.Info( $"[NavDebug] nav enabled={nav.IsEnabled} generating={nav.IsGenerating} agentHeight={nav.AgentHeight} agentRadius={nav.AgentRadius} step={nav.AgentStepSize} maxSlope={nav.AgentMaxSlope} includeStatic={nav.IncludeStaticBodies} customBounds={nav.CustomBounds} bounds={nav.Bounds.Mins}..{nav.Bounds.Maxs}" );

		GameObject pawn = null;
		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is not null && vitals.IsLocalInputOwnedPawn() )
			{
				pawn = vitals.GameObject;
				break;
			}
		}

		if ( pawn is null || !pawn.IsValid() )
		{
			Log.Info( "[NavDebug] no local pawn" );
			return;
		}

		var feet = pawn.WorldPosition;
		var onNav = EntityNavMeshUtility.TryFindNavAtFeet( scene, feet, out var navPoint, vertical: 48f );
		Log.Info( onNav
			? $"[NavDebug] feet {feet} — nav at feet YES ({navPoint}, dz={navPoint.z - feet.z:0.#})"
			: $"[NavDebug] feet {feet} — nav at feet NO" );

		var wide = EntityNavMeshUtility.TryProjectToNavMesh( scene, feet, out var nearest, NavProjectTier.Full, 256f );
		Log.Info( wide
			? $"[NavDebug] nearest nav within 256u: {nearest} (dist={Vector3.DistanceBetween( nearest, feet ):0})"
			: "[NavDebug] no nav within 256u" );

		var trace = scene.Trace.Ray( feet + Vector3.Up * 8f, feet - Vector3.Up * 128f )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( pawn )
			.Run();
		if ( !trace.Hit || !trace.GameObject.IsValid() )
		{
			Log.Info( "[NavDebug] standing on: nothing (no ground hit)" );
			return;
		}

		var piece = BuildPlacementUtility.FindBuildPieceOnHierarchy( trace.GameObject );
		var pieceNote = piece is not null
			? $" piece={piece.PieceId} category={BuildPieceNavPolicy.GetCategory( piece.PieceId )}"
			: string.Empty;
		Log.Info( $"[NavDebug] standing on: {trace.GameObject.Name}{pieceNote} normal.z={trace.Normal.z:0.00} static={(trace.Body?.BodyType.ToString() ?? "?")}" );
	}
}
