using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>Which snap points a piece exposes.</summary>
public enum BuildSnapLayoutKind
{
	/// <summary>No structural seams (furniture, repair tool).</summary>
	None = 0,

	/// <summary>Four corner snaps on the wide face — plates: floors, walls, roofs, stairs.</summary>
	FootprintCorners,

	/// <summary>Two snaps centred on the ends of the long axis — beams and posts.</summary>
	AxisEnds,
}

/// <summary>
/// Per-piece snap layout. Snap counts are <b>not</b> uniform across the kit: beams get two end
/// points, plates get four corners, and some shapes (spiral stairs, triangle floor, roof corners)
/// want their own hand-placed set. Family gives the default; <see cref="Overrides"/> is the hook to
/// pin a specific piece without touching snap logic.
/// </summary>
public static class BuildSnapLayout
{
	/// <summary>
	/// Per-id layout overrides. Entries listed here are the pieces whose shape does not match their
	/// family default and still need a hand-authored set.
	/// </summary>
	static readonly Dictionary<string, BuildSnapLayoutKind> Overrides = new( StringComparer.OrdinalIgnoreCase )
	{
		// TODO(design): spiral stairs snap in "weird spots" — placeholder is the plate corner set
		// until the real points are specified.
		["build_wood_stairsSpiral"] = BuildSnapLayoutKind.FootprintCorners,
	};

	public static BuildSnapLayoutKind GetKind( string pieceId )
	{
		if ( string.IsNullOrWhiteSpace( pieceId ) )
			return BuildSnapLayoutKind.None;

		if ( Overrides.TryGetValue( pieceId, out var kind ) )
			return kind;

		return BuildPieceFamily.GetKind( pieceId ) switch
		{
			BuildPieceFamilyKind.Beam => BuildSnapLayoutKind.AxisEnds,
			BuildPieceFamilyKind.Floor => BuildSnapLayoutKind.FootprintCorners,
			BuildPieceFamilyKind.Wall => BuildSnapLayoutKind.FootprintCorners,
			BuildPieceFamilyKind.Roof => BuildSnapLayoutKind.FootprintCorners,
			BuildPieceFamilyKind.Stairs => BuildSnapLayoutKind.FootprintCorners,
			_ => BuildSnapLayoutKind.None,
		};
	}

	static readonly BuildSnapRole[] NoRoles = Array.Empty<BuildSnapRole>();

	static readonly BuildSnapRole[] CornerRoles =
	{
		BuildSnapRole.CornerNorthEast,
		BuildSnapRole.CornerNorthWest,
		BuildSnapRole.CornerSouthEast,
		BuildSnapRole.CornerSouthWest,
	};

	static readonly BuildSnapRole[] AxisRoles =
	{
		BuildSnapRole.AxisStart,
		BuildSnapRole.AxisEnd,
	};

	public static IReadOnlyList<BuildSnapRole> GetRoles( string pieceId ) =>
		GetKind( pieceId ) switch
		{
			BuildSnapLayoutKind.FootprintCorners => CornerRoles,
			BuildSnapLayoutKind.AxisEnds => AxisRoles,
			_ => NoRoles,
		};

	public static bool UsesAxisEnds( string pieceId ) =>
		GetKind( pieceId ) == BuildSnapLayoutKind.AxisEnds;

	public static bool IsAxisRole( BuildSnapRole role ) =>
		role is BuildSnapRole.AxisStart or BuildSnapRole.AxisEnd;

	/// <summary>
	/// HUD name for a held snap, in the piece's own local frame: +X is right and +Z is up for an
	/// upright plate, +Y is "top" (away) for a flat one.
	/// </summary>
	public static string GetHoldLabel( BuildSnapRole role ) =>
		role switch
		{
			BuildSnapRole.CornerNorthWest => "Top Left",
			BuildSnapRole.CornerNorthEast => "Top Right",
			BuildSnapRole.CornerSouthWest => "Bottom Left",
			BuildSnapRole.CornerSouthEast => "Bottom Right",
			BuildSnapRole.AxisStart => "Bottom End",
			BuildSnapRole.AxisEnd => "Top End",
			_ => "Auto",
		};

	/// <summary>
	/// Fixed Q/E order for a held snap. Aim-independent on purpose: cycling must visit the same
	/// variants in the same order no matter where the mouse is resting.
	/// </summary>
	public static int GetHoldOrder( BuildSnapRole role ) =>
		role switch
		{
			BuildSnapRole.CornerNorthEast => 0,
			BuildSnapRole.CornerNorthWest => 1,
			BuildSnapRole.CornerSouthEast => 2,
			BuildSnapRole.CornerSouthWest => 3,
			BuildSnapRole.AxisStart => 4,
			BuildSnapRole.AxisEnd => 5,
			_ => 6,
		};
}
