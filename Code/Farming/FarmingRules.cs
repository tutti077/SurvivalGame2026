using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Farming constants shared by the client preview (<see cref="ToolHoe"/>, <see cref="PlayerFarming"/>)
/// and the host commit path (<see cref="FarmingAuthority"/>). Meters here; converted once where read.
/// </summary>
public static class FarmingRules
{
	/// <summary>Surfaces that can be tilled carry this tag (the testscene1 ground plane today; terrain materials later).</summary>
	public const string DirtTag = "dirt";

	public const string TilledSoilPrefab = "prefabs/farming/tilled_soil.prefab";
	public const string FarmPlantPrefab = "prefabs/farming/farm_plant.prefab";

	/// <summary>Edge length of one tilled square.</summary>
	public const float TileSizeMeters = 1f;

	/// <summary>Plants may not be sown closer than this to another plant (centre to centre).</summary>
	public const float MinPlantSpacingMeters = 0.3f;

	/// <summary>Seeds handed back when the hoe uproots a plant (any stage).</summary>
	public const int UprootSeedAmount = 1;

	/// <summary>Host slack on top of a tool's reach so a lagging client is not rejected for a valid aim.</summary>
	public const float ReachSlackMeters = 1.5f;

	/// <summary>Tilling needs ground that is close to flat.</summary>
	public const float MinTillSurfaceNormalZ = 0.85f;

	public static readonly Color TilledSoilTint = new( 0.62f, 0.45f, 0.28f );
	public static readonly Color ValidGhostTint = new( 0.62f, 0.45f, 0.28f, 0.5f );
	public static readonly Color InvalidGhostTint = new( 0.92f, 0.18f, 0.14f, 0.5f );

	/// <summary>Standard placement trace: solid world only, never the pawn, never preview ghosts.</summary>
	public static SceneTraceResult TraceView( Scene scene, GameObject pawn, Vector3 origin, Vector3 direction, float reachUnits )
	{
		return scene.Trace.Ray( origin, origin + direction * reachUnits )
			.IgnoreGameObjectHierarchy( pawn.Root )
			.WithoutTags( "buildpreview" )
			.WithoutTags( "farmpreview" )
			.WithoutTags( "player" )
			.WithoutTags( "trigger" )
			.Run();
	}

	/// <summary>Is this hit a flat, dirt-tagged surface with no tilled soil in its grid cell yet?</summary>
	public static bool CanTillAt( in SceneTraceResult tr, out Vector3 tileCenter, out string reason )
	{
		tileCenter = default;
		reason = "no surface";
		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
			return false;

		if ( tr.Normal.z < MinTillSurfaceNormalZ )
		{
			reason = "too steep";
			return false;
		}

		if ( !HasDirtTag( tr.GameObject ) )
		{
			reason = "not dirt";
			return false;
		}

		tileCenter = TilledSoil.SnapToGrid( tr.HitPosition );
		if ( TilledSoil.TryFindAt( tileCenter, out _ ) )
		{
			reason = "already tilled";
			return false;
		}

		return true;
	}

	public static bool HasDirtTag( GameObject go )
	{
		for ( var g = go; g.IsValid(); g = g.Parent )
		{
			if ( g.Tags.Has( DirtTag ) )
				return true;
		}

		return false;
	}

	/// <summary>Sow rule shared by the ghost and the host: inside a tile and clear of every other plant.</summary>
	public static bool CanSowAt( Vector3 point, out TilledSoil tile, out string reason )
	{
		reason = "not tilled soil";
		if ( !TilledSoil.TryFindAt( point, out tile ) )
			return false;

		var spacing = TerrainWorldUnits.MetersToEngine( MinPlantSpacingMeters );
		if ( FarmPlant.AnyWithin( point, spacing ) )
		{
			reason = "too close to another plant";
			return false;
		}

		return true;
	}

	public static Color ParseColor( string value, Color fallback )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return fallback;

		var parsed = ResourceDefinitionCatalog.ParseFallbackColor( value );
		return parsed;
	}
}
