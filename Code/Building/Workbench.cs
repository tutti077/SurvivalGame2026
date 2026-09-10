using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Placeable workbench: Use-key look trace opens the crafting menu filtered to workbench
/// recipes, with the free tool-repair button at the top. No fuel — but it only works under a
/// player-built roof (<see cref="IsSheltered"/>): an exposed bench neither opens nor crafts.
/// </summary>
[Title( "Workbench" )]
public sealed class Workbench : Component
{
	public const string StationId = "workbench";

	/// <summary>How high above the bench a placed roof / floor may sit and still cover it.</summary>
	[Property, Group( "Workbench" ), Title( "Roof probe height (m)" ), Range( 2f, 30f )]
	public float RoofProbeMeters { get; set; } = 12f;

	/// <summary>Host craft validation: the crafter must be within this distance of a sheltered bench.</summary>
	[Property, Group( "Workbench" ), Title( "Use range (m)" ), Range( 1f, 10f )]
	public float UseRangeMeters { get; set; } = 4f;

	int _shelterVersion = -1;
	bool _sheltered;

	/// <summary>
	/// A placed roof or upper floor covers this bench. Probed once per build-world change
	/// (<see cref="BuildPiece.WorldVersion"/>), never per frame — benches do not move.
	/// </summary>
	public bool IsSheltered
	{
		get
		{
			if ( _shelterVersion == BuildPiece.WorldVersion )
				return _sheltered;

			_shelterVersion = BuildPiece.WorldVersion;
			var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
			var origin = GameObject.WorldPosition + Vector3.Up * TerrainWorldUnits.MetersToEngine( 1f );
			_sheltered = ShelterProbe.HasPlayerBuiltRoofAbove( scene, origin, RoofProbeMeters, GameObject, out _ );
			return _sheltered;
		}
	}

	/// <summary>Host: a sheltered, built bench stands within its use range of <paramref name="player"/>.</summary>
	public static bool IsPlayerNearUsableWorkbench( GameObject player )
	{
		if ( player is null || !player.IsValid() )
			return false;

		var scene = player.Scene.IsValid() ? player.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		var origin = player.WorldPosition;
		foreach ( var bench in scene.GetAllComponents<Workbench>() )
		{
			if ( bench is null || !bench.Enabled || !bench.GameObject.IsValid() )
				continue;

			if ( !IsBuilt( bench ) )
				continue;

			var range = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, bench.UseRangeMeters ) );
			if ( (bench.GameObject.WorldPosition - origin).LengthSquared > range * range )
				continue;

			if ( bench.IsSheltered )
				return true;
		}

		return false;
	}

	public static bool TryFindOnHierarchy( GameObject hitObject, out Workbench workbench )
	{
		workbench = null;
		if ( hitObject is null || !hitObject.IsValid() )
			return false;

		for ( var current = hitObject; current.IsValid(); current = current.Parent )
		{
			var candidate = current.Components.Get<Workbench>();
			if ( candidate is null || !candidate.Enabled )
				continue;

			if ( !IsBuilt( candidate ) )
				continue;

			workbench = candidate;
			return true;
		}

		return false;
	}

	/// <summary>Not a preview ghost or blueprint.</summary>
	static bool IsBuilt( Workbench bench )
	{
		if ( bench.GameObject.Tags.Has( "buildpreview" ) )
			return false;

		var piece = bench.Components.Get<BuildPiece>();
		return piece is null || ( !piece.IsPreviewGhost && !piece.IsBlueprint );
	}
}
