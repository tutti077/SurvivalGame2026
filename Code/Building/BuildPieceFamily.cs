using System;

namespace Survival;

/// <summary>Snap / behaviour family a build piece belongs to.</summary>
public enum BuildPieceFamilyKind
{
	None = 0,
	Floor,
	Wall,
	Roof,
	Beam,
	Stairs,
}

/// <summary>
/// The one place that answers "is this a wall?" for the whole build kit. Families come from the
/// piece id, so a new size or corner variant (<c>build_wood_wall_1m1m</c>,
/// <c>build_wood_45roofInsideCorner</c>) inherits its family's snap rules without editing every
/// snap file. Furniture and tools (<c>chest</c>, <c>repair</c>) are deliberately
/// <see cref="BuildPieceFamilyKind.None"/> — they have no structural seams.
/// </summary>
public static class BuildPieceFamily
{
	public static BuildPieceFamilyKind GetKind( string pieceId )
	{
		if ( string.IsNullOrWhiteSpace( pieceId ) )
			return BuildPieceFamilyKind.None;

		// Roof first: "45roofInsideCorner" must not be read as anything else.
		if ( Has( pieceId, "roof" ) )
			return BuildPieceFamilyKind.Roof;

		// A door fills a wall slot, so it snaps exactly like the wall it replaces.
		if ( Has( pieceId, "wall" ) || Has( pieceId, "door" ) )
			return BuildPieceFamilyKind.Wall;

		if ( Has( pieceId, "floor" ) )
			return BuildPieceFamilyKind.Floor;

		if ( Has( pieceId, "stair" ) || Has( pieceId, "ramp" ) )
			return BuildPieceFamilyKind.Stairs;

		if ( Has( pieceId, "beam" ) || Has( pieceId, "post" ) )
			return BuildPieceFamilyKind.Beam;

		return BuildPieceFamilyKind.None;
	}

	public static bool IsFloor( string pieceId ) => GetKind( pieceId ) == BuildPieceFamilyKind.Floor;

	public static bool IsWall( string pieceId ) => GetKind( pieceId ) == BuildPieceFamilyKind.Wall;

	public static bool IsRoof( string pieceId ) => GetKind( pieceId ) == BuildPieceFamilyKind.Roof;

	public static bool IsBeam( string pieceId ) => GetKind( pieceId ) == BuildPieceFamilyKind.Beam;

	public static bool IsStairs( string pieceId ) => GetKind( pieceId ) == BuildPieceFamilyKind.Stairs;

	public static bool IsDoor( string pieceId ) => Has( pieceId, "door" );

	/// <summary>Half-module pieces (triangle floor) — the useful seam is the diagonal.</summary>
	public static bool IsTriangle( string pieceId ) => Has( pieceId, "triangle" );

	/// <summary>Hip / valley roof pieces that turn a corner instead of running straight.</summary>
	public static bool IsCorner( string pieceId ) => Has( pieceId, "corner" );

	/// <summary>Same family on both sides — lets Q/E cycle every lip on a shared seam.</summary>
	public static bool IsSameFamily( string a, string b )
	{
		var kind = GetKind( a );
		return kind != BuildPieceFamilyKind.None && kind == GetKind( b );
	}

	/// <summary>
	/// Roofs and stairs both climb, so they share edge rules: eave/ridge lips, hang-below or
	/// sit-above scoring, and mating onto wall tops. Pitch itself stays roof-only.
	/// </summary>
	public static bool IsRampLike( string pieceId ) => IsRoof( pieceId ) || IsStairs( pieceId );

	static bool Has( string pieceId, string token ) =>
		!string.IsNullOrWhiteSpace( pieceId )
		&& pieceId.Contains( token, StringComparison.OrdinalIgnoreCase );
}
