using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// The one place that answers "is there a player-built roof over this spot?" and "is this spot
/// walled in?". Only placed <see cref="BuildPiece"/>s count — traces filter on the
/// <see cref="BuildPieceTag"/> so cave ceilings, terrain and dug-out holes never shelter anyone.
/// <para>
/// Callers cache: pawns re-probe on <see cref="PlayerVitals.ComfortCheckIntervalSeconds"/>,
/// static stations (<see cref="Workbench"/>) re-probe only when <see cref="BuildPiece.WorldVersion"/>
/// moves. Nothing here runs per frame.
/// </para>
/// </summary>
public static class ShelterProbe
{
	/// <summary>Tag every placed build piece carries (prefab tag, re-asserted by <see cref="BuildPiece"/>).</summary>
	public const string BuildPieceTag = "buildpiece";

	/// <summary>Horizontal probe count for the enclosure test — every 45°.</summary>
	public const int EnclosureDirections = 8;

	static readonly List<SceneTraceResult> HitScratch = new();

	/// <summary>
	/// A placed roof or upper floor sits somewhere above <paramref name="origin"/> within
	/// <paramref name="maxMeters"/>. Beams and walls crossing the ray are skipped, so a rafter under
	/// the roof does not hide it.
	/// </summary>
	public static bool HasPlayerBuiltRoofAbove( Scene scene, Vector3 origin, float maxMeters, GameObject ignore, out BuildPiece roof )
	{
		roof = null;
		if ( scene is null || !scene.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, maxMeters ) );
		return TryFirstQualifying( scene, origin, origin + Vector3.Up * reach, ignore, IsRoofLike, out roof );
	}

	/// <summary>
	/// How many of the <see cref="EnclosureDirections"/> horizontal rays from <paramref name="origin"/>
	/// meet a placed wall-like piece within <paramref name="rangeMeters"/>. Beams do not count as walls.
	/// </summary>
	public static int CountEnclosingWalls( Scene scene, Vector3 origin, float rangeMeters, GameObject ignore )
	{
		if ( scene is null || !scene.IsValid() )
			return 0;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, rangeMeters ) );
		var hits = 0;
		for ( var i = 0; i < EnclosureDirections; i++ )
		{
			var yaw = 360f * i / EnclosureDirections;
			var direction = Rotation.FromYaw( yaw ).Forward;
			if ( TryFirstQualifying( scene, origin, origin + direction * reach, ignore, IsWallLike, out _ ) )
				hits++;
		}

		return hits;
	}

	/// <summary>Structural roof or floor family — a second-storey floor is a ceiling for the room under it.</summary>
	static bool IsRoofLike( BuildPiece piece )
	{
		var kind = BuildPieceFamily.GetKind( piece.PieceId );
		return kind == BuildPieceFamilyKind.Roof || kind == BuildPieceFamilyKind.Floor;
	}

	/// <summary>Anything solid enough to close a room: walls, doors, roofs sloping to the ground, floors, stairs.</summary>
	static bool IsWallLike( BuildPiece piece )
	{
		var kind = BuildPieceFamily.GetKind( piece.PieceId );
		return kind != BuildPieceFamilyKind.None && kind != BuildPieceFamilyKind.Beam;
	}

	static bool TryFirstQualifying( Scene scene, Vector3 from, Vector3 to, GameObject ignore, Func<BuildPiece, bool> accept, out BuildPiece piece )
	{
		piece = null;

		var trace = scene.Trace.Ray( from, to ).WithTag( BuildPieceTag );
		if ( ignore is not null && ignore.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( ignore );

		HitScratch.Clear();
		HitScratch.AddRange( trace.RunAll() );
		HitScratch.Sort( ( a, b ) => a.Distance.CompareTo( b.Distance ) );

		for ( var i = 0; i < HitScratch.Count; i++ )
		{
			var hit = HitScratch[i];
			if ( !hit.Hit || hit.GameObject is null || !hit.GameObject.IsValid() )
				continue;

			var candidate = FindPlacedPiece( hit.GameObject );
			if ( candidate is null || !accept( candidate ) )
				continue;

			piece = candidate;
			return true;
		}

		return false;
	}

	/// <summary>Placed, built (not blueprint / ghost), structural piece on the hit hierarchy.</summary>
	static BuildPiece FindPlacedPiece( GameObject hitObject )
	{
		for ( var go = hitObject; go is not null && go.IsValid(); go = go.Parent )
		{
			var piece = go.Components.Get<BuildPiece>();
			if ( piece is null )
				continue;

			if ( piece.IsPreviewGhost || piece.IsBlueprint || !piece.IsDestructible )
				return null;

			return piece;
		}

		return null;
	}
}
