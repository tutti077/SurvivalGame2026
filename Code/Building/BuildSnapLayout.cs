using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Which snap points a piece exposes.</summary>
public enum BuildSnapLayoutKind
{
	/// <summary>No structural seams (furniture, repair tool).</summary>
	None = 0,

	/// <summary>Four corner snaps on the wide face — plates: floors, walls, roofs.</summary>
	FootprintCorners,

	/// <summary>Two snaps centred on the ends of the long axis — beams and posts.</summary>
	AxisEnds,

	/// <summary>
	/// Three of the four plate corners. The fourth is cut away by the piece's own diagonal
	/// (triangle floor, 45° gable walls), so a snap there would hang in open air.
	/// </summary>
	TriangleCorners,

	/// <summary>
	/// The four corners of a climbing surface: both ends of the low entry edge and both ends of the
	/// high exit edge. Stairs use this so they seam where the flight starts and finishes, the way a
	/// pitched roof seams on its eave and ridge, instead of at the mid-height of their box.
	/// </summary>
	RampCorners,

	/// <summary>
	/// The four corners of a folded hip or valley roof piece, at the mesh's own vertices. The slope
	/// midpoints that used to ride along with them are gone: they are not corners of anything, so
	/// they read as stray snaps floating on the face of the piece.
	/// </summary>
	FoldedRoofCorners,
}

/// <summary>Faces a ramp-like piece is walked onto and off, as unit axes in the piece's own frame.</summary>
public readonly struct BuildRampFaces
{
	/// <summary>Face the flight is entered by — the low edge.</summary>
	public Vector3 Entry { get; init; }

	/// <summary>Face the flight leaves by — the high edge. Square to <see cref="Entry"/> on a winder.</summary>
	public Vector3 Exit { get; init; }
}

/// <summary>
/// Per-piece snap layout. Snap counts are <b>not</b> uniform across the kit: beams get two end
/// points, plates get four corners, triangles get three, and stairs get the four corners of the
/// surface you walk up. Family gives the default; <see cref="Overrides"/> is the hook to pin a
/// specific piece without touching snap logic.
/// </summary>
public static class BuildSnapLayout
{
	/// <summary>
	/// Per-id layout overrides — the pieces whose shape does not match their family default.
	/// </summary>
	static readonly Dictionary<string, BuildSnapLayoutKind> Overrides = new( StringComparer.OrdinalIgnoreCase )
	{
		["build_wood_triangleFloor"] = BuildSnapLayoutKind.TriangleCorners,
		["build_wood_45wallLeft"] = BuildSnapLayoutKind.TriangleCorners,
		["build_wood_45wallRight"] = BuildSnapLayoutKind.TriangleCorners,
		["build_wood_45roofInsideCorner"] = BuildSnapLayoutKind.FoldedRoofCorners,
		["build_wood_45roofOutsideCorner"] = BuildSnapLayoutKind.FoldedRoofCorners,
	};

	/// <summary>
	/// The one plate corner each triangular piece does <b>not</b> have — the corner its hypotenuse
	/// cuts off. Piece frame: +X right, +Y away, +Z up; a wall is thin on Y so its corners read as
	/// top/bottom left/right, a floor is thin on Z so they read as near/far left/right.
	/// </summary>
	static readonly Dictionary<string, BuildSnapRole> CutCorners = new( StringComparer.OrdinalIgnoreCase )
	{
		// Half of a 2×2 deck: right angle at (-X, -Y), hypotenuse (+X, -Y) → (-X, +Y).
		["build_wood_triangleFloor"] = BuildSnapRole.CornerNorthEast,
		// Gable at full wall height on +X, so the top-left corner is the one that is missing.
		["build_wood_45wallLeft"] = BuildSnapRole.CornerNorthWest,
		// Mirror of the above: full height on -X, top-right missing.
		["build_wood_45wallRight"] = BuildSnapRole.CornerNorthEast,
	};

	/// <summary>
	/// Which face each flight is walked onto and off. The straight run climbs -X → +X; both
	/// quarter-turn winders keep that entry face and leave through the side face next to it, so a
	/// straight piece feeds either hand without rotating anything.
	/// </summary>
	static readonly Dictionary<string, BuildRampFaces> RampFaces = new( StringComparer.OrdinalIgnoreCase )
	{
		["build_wood_stairs"] = new()
		{
			Entry = new Vector3( -1f, 0f, 0f ),
			Exit = new Vector3( 1f, 0f, 0f ),
		},
		["build_wood_stairsSpiralLeft"] = new()
		{
			Entry = new Vector3( -1f, 0f, 0f ),
			Exit = new Vector3( 0f, 1f, 0f ),
		},
		["build_wood_stairsSpiralRight"] = new()
		{
			Entry = new Vector3( -1f, 0f, 0f ),
			Exit = new Vector3( 0f, -1f, 0f ),
		},
	};

	/// <summary>
	/// The four corner positions in metres, piece-local, origin at the 2×2×2 module centre. Each one
	/// is a real vertex of the authored mesh — verified against the source FBX.
	/// </summary>
	static readonly Dictionary<string, Vector3[]> FoldSnapLocals = new( StringComparer.OrdinalIgnoreCase )
	{
		// Hip: high at (-X,-Y), low ring at z=-1.
		["build_wood_45roofOutsideCorner"] =
		[
			new( -1f, -1f, 1f ),   // Fold0 apex
			new( 1f, -1f, -1f ),   // Fold1 eave +X
			new( 1f, 1f, -1f ),    // Fold2 low
			new( -1f, 1f, -1f ),   // Fold3 eave +Y
		],
		// Valley: ridge ring at z=+1, gutter bottom at (+X,+Y).
		["build_wood_45roofInsideCorner"] =
		[
			new( -1f, -1f, 1f ),   // Fold0 outer ridge
			new( 1f, -1f, 1f ),    // Fold1 ridge +X
			new( 1f, 1f, -1f ),    // Fold2 valley bottom
			new( -1f, 1f, 1f ),    // Fold3 ridge +Y
		],
	};

	static readonly SnapEdge[] OutsideFoldEdges =
	[
		new() { Id = SnapEdgeId.West, CornerA = BuildSnapRole.Fold0, CornerB = BuildSnapRole.Fold3 },
		new() { Id = SnapEdgeId.South, CornerA = BuildSnapRole.Fold0, CornerB = BuildSnapRole.Fold1 },
		new() { Id = SnapEdgeId.East, CornerA = BuildSnapRole.Fold1, CornerB = BuildSnapRole.Fold2 },
		new() { Id = SnapEdgeId.North, CornerA = BuildSnapRole.Fold3, CornerB = BuildSnapRole.Fold2 },
	];

	static readonly SnapEdge[] InsideFoldEdges =
	[
		new() { Id = SnapEdgeId.West, CornerA = BuildSnapRole.Fold0, CornerB = BuildSnapRole.Fold3 },
		new() { Id = SnapEdgeId.South, CornerA = BuildSnapRole.Fold0, CornerB = BuildSnapRole.Fold1 },
		new() { Id = SnapEdgeId.East, CornerA = BuildSnapRole.Fold1, CornerB = BuildSnapRole.Fold2 },
		new() { Id = SnapEdgeId.North, CornerA = BuildSnapRole.Fold3, CornerB = BuildSnapRole.Fold2 },
	];

	static readonly Dictionary<string, SnapEdge[]> FoldEdges = new( StringComparer.OrdinalIgnoreCase )
	{
		["build_wood_45roofOutsideCorner"] = OutsideFoldEdges,
		["build_wood_45roofInsideCorner"] = InsideFoldEdges,
	};

	static readonly BuildSnapRole[] FoldRoles =
	{
		BuildSnapRole.Fold0,
		BuildSnapRole.Fold1,
		BuildSnapRole.Fold2,
		BuildSnapRole.Fold3,
	};

	// Layout is a property of the piece id, which never changes at runtime — resolve the family
	// string scan and the override lookups once each and answer from the cache after that.
	static readonly Dictionary<string, BuildSnapLayoutKind> KindCache = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, BuildSnapRole[]> RolesCache = new( StringComparer.OrdinalIgnoreCase );

	public static BuildSnapLayoutKind GetKind( string pieceId )
	{
		if ( string.IsNullOrWhiteSpace( pieceId ) )
			return BuildSnapLayoutKind.None;

		if ( KindCache.TryGetValue( pieceId, out var cached ) )
			return cached;

		var kind = ResolveKind( pieceId );
		KindCache[pieceId] = kind;
		return kind;
	}

	static BuildSnapLayoutKind ResolveKind( string pieceId )
	{
		if ( Overrides.TryGetValue( pieceId, out var kind ) )
			return kind;

		return BuildPieceFamily.GetKind( pieceId ) switch
		{
			BuildPieceFamilyKind.Beam => BuildSnapLayoutKind.AxisEnds,
			BuildPieceFamilyKind.Floor => BuildSnapLayoutKind.FootprintCorners,
			BuildPieceFamilyKind.Wall => BuildSnapLayoutKind.FootprintCorners,
			BuildPieceFamilyKind.Roof => BuildSnapLayoutKind.FootprintCorners,
			// A flight without a declared entry/exit face has no ramp to hang corners on.
			BuildPieceFamilyKind.Stairs => RampFaces.ContainsKey( pieceId )
				? BuildSnapLayoutKind.RampCorners
				: BuildSnapLayoutKind.FootprintCorners,
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

	public static IReadOnlyList<BuildSnapRole> GetRoles( string pieceId )
	{
		if ( string.IsNullOrWhiteSpace( pieceId ) )
			return NoRoles;

		if ( RolesCache.TryGetValue( pieceId, out var cached ) )
			return cached;

		var roles = ResolveRoles( pieceId );
		RolesCache[pieceId] = roles;
		return roles;
	}

	static BuildSnapRole[] ResolveRoles( string pieceId ) =>
		GetKind( pieceId ) switch
		{
			BuildSnapLayoutKind.FootprintCorners => CornerRoles,
			BuildSnapLayoutKind.RampCorners => CornerRoles,
			BuildSnapLayoutKind.FoldedRoofCorners => FoldRoles,
			BuildSnapLayoutKind.TriangleCorners => ResolveTriangleRoles( pieceId ),
			BuildSnapLayoutKind.AxisEnds => AxisRoles,
			_ => NoRoles,
		};

	/// <summary>
	/// The triangle pieces' real edges: the two straight sides the cut corner leaves intact, plus
	/// the hypotenuse (<see cref="SnapEdgeId.Diagonal"/>) joining the two corners adjacent to the
	/// cut. Only pieces listed here own a Diagonal — a full plate has all four corners, so a
	/// corner-existence test alone would wrongly grant it a phantom diagonal seam.
	/// </summary>
	static readonly Dictionary<string, SnapEdge[]> TriangleEdges = new( StringComparer.OrdinalIgnoreCase )
	{
		// Cut NE → straight South + West, hypotenuse NW–SE.
		["build_wood_triangleFloor"] =
		[
			new() { Id = SnapEdgeId.South, CornerA = BuildSnapRole.CornerSouthWest, CornerB = BuildSnapRole.CornerSouthEast },
			new() { Id = SnapEdgeId.West, CornerA = BuildSnapRole.CornerNorthWest, CornerB = BuildSnapRole.CornerSouthWest },
			new() { Id = SnapEdgeId.Diagonal, CornerA = BuildSnapRole.CornerNorthWest, CornerB = BuildSnapRole.CornerSouthEast },
		],
		// Cut NW (gable full height on +X) → straight South (bottom) + East (tall side), slope SW–NE.
		["build_wood_45wallLeft"] =
		[
			new() { Id = SnapEdgeId.South, CornerA = BuildSnapRole.CornerSouthWest, CornerB = BuildSnapRole.CornerSouthEast },
			new() { Id = SnapEdgeId.East, CornerA = BuildSnapRole.CornerNorthEast, CornerB = BuildSnapRole.CornerSouthEast },
			new() { Id = SnapEdgeId.Diagonal, CornerA = BuildSnapRole.CornerSouthWest, CornerB = BuildSnapRole.CornerNorthEast },
		],
		// Cut NE (gable full height on -X) → straight South (bottom) + West (tall side), slope NW–SE.
		["build_wood_45wallRight"] =
		[
			new() { Id = SnapEdgeId.South, CornerA = BuildSnapRole.CornerSouthWest, CornerB = BuildSnapRole.CornerSouthEast },
			new() { Id = SnapEdgeId.West, CornerA = BuildSnapRole.CornerNorthWest, CornerB = BuildSnapRole.CornerSouthWest },
			new() { Id = SnapEdgeId.Diagonal, CornerA = BuildSnapRole.CornerNorthWest, CornerB = BuildSnapRole.CornerSouthEast },
		],
	};

	/// <summary>Thin edges this piece exposes — triangles and folded corners use their own lists.</summary>
	public static IReadOnlyList<SnapEdge> GetEdges( string pieceId )
	{
		if ( TriangleEdges.TryGetValue( pieceId, out var triangleEdges ) )
			return triangleEdges;

		return FoldEdges.TryGetValue( pieceId, out var edges ) ? edges : BuildSnapEdge.ThinPlaneEdges;
	}

	/// <summary>This piece's edge with the given id, from its own edge list (the only place a Diagonal can come from).</summary>
	public static bool TryGetPieceEdge( string pieceId, SnapEdgeId edgeId, out SnapEdge edge )
	{
		var edges = GetEdges( pieceId );
		for ( var i = 0; i < edges.Count; i++ )
		{
			if ( edges[i].Id == edgeId )
			{
				edge = edges[i];
				return true;
			}
		}

		edge = default;
		return false;
	}

	public static bool TryGetFoldSnapLocal( string pieceId, BuildSnapRole role, out Vector3 local )
	{
		local = default;
		if ( role is < BuildSnapRole.Fold0 or > BuildSnapRole.Fold3 )
			return false;

		if ( !FoldSnapLocals.TryGetValue( pieceId, out var points ) )
			return false;

		var index = (int)role - (int)BuildSnapRole.Fold0;
		if ( index < 0 || index >= points.Length )
			return false;

		local = points[index];
		return true;
	}

	public static bool IsFoldRole( BuildSnapRole role ) =>
		role is >= BuildSnapRole.Fold0 and <= BuildSnapRole.Fold3;

	static BuildSnapRole[] ResolveTriangleRoles( string pieceId )
	{
		if ( !CutCorners.TryGetValue( pieceId, out var cut ) )
			return CornerRoles;

		var roles = new BuildSnapRole[CornerRoles.Length - 1];
		var next = 0;
		for ( var i = 0; i < CornerRoles.Length; i++ )
		{
			if ( CornerRoles[i] != cut )
				roles[next++] = CornerRoles[i];
		}

		return roles;
	}

	/// <summary>True when the piece really has this snap — a triangle is short one plate corner.</summary>
	public static bool HasRole( string pieceId, BuildSnapRole role )
	{
		var roles = GetRoles( pieceId );
		for ( var i = 0; i < roles.Count; i++ )
		{
			if ( roles[i] == role )
				return true;
		}

		return false;
	}

	/// <summary>
	/// Both ends of a seam have to exist. On a triangle one "edge" of the plate is only a single
	/// corner and open air, and mating to it would pin the piece against nothing.
	/// </summary>
	public static bool HasEdge( string pieceId, SnapEdge edge ) =>
		HasRole( pieceId, edge.CornerA ) && HasRole( pieceId, edge.CornerB );

	/// <summary>
	/// A piece that declares an entry and an exit face is a ramp, full stop. This deliberately does
	/// not also consult <see cref="GetKind"/>: requiring both to agree gave the flight a second way
	/// to fail, and when it did the corners fell back to the flat plate layout — the four snaps kept
	/// their shape but sat in the box's mid-plane instead of on the surface you walk up.
	/// </summary>
	public static bool TryGetRampFaces( string pieceId, out BuildRampFaces faces ) =>
		RampFaces.TryGetValue( pieceId, out faces );

	public static bool UsesAxisEnds( string pieceId ) =>
		GetKind( pieceId ) == BuildSnapLayoutKind.AxisEnds;

	public static bool IsAxisRole( BuildSnapRole role ) =>
		role is BuildSnapRole.AxisStart or BuildSnapRole.AxisEnd;

	/// <summary>
	/// HUD name for a held snap, in the piece's own local frame: +X is right and +Z is up for an
	/// upright plate, +Y is "top" (away) for a flat one. A flight has no top and bottom in that
	/// sense, so it names the edge you walk on and the edge you walk off instead.
	/// </summary>
	public static string GetHoldLabel( string pieceId, BuildSnapRole role )
	{
		if ( GetKind( pieceId ) == BuildSnapLayoutKind.RampCorners )
		{
			return role switch
			{
				BuildSnapRole.CornerNorthEast => "Bottom Right",
				BuildSnapRole.CornerNorthWest => "Bottom Left",
				BuildSnapRole.CornerSouthEast => "Top Right",
				BuildSnapRole.CornerSouthWest => "Top Left",
				_ => "Auto",
			};
		}

		if ( GetKind( pieceId ) == BuildSnapLayoutKind.FoldedRoofCorners )
		{
			return role switch
			{
				BuildSnapRole.Fold0 => "Corner 0",
				BuildSnapRole.Fold1 => "Corner 1",
				BuildSnapRole.Fold2 => "Corner 2",
				BuildSnapRole.Fold3 => "Corner 3",
				_ => "Auto",
			};
		}

		return role switch
		{
			BuildSnapRole.CornerNorthWest => "Top Left",
			BuildSnapRole.CornerNorthEast => "Top Right",
			BuildSnapRole.CornerSouthWest => "Bottom Left",
			BuildSnapRole.CornerSouthEast => "Bottom Right",
			BuildSnapRole.AxisStart => "Bottom End",
			BuildSnapRole.AxisEnd => "Top End",
			_ => "Auto",
		};
	}

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
			BuildSnapRole.Fold0 => 0,
			BuildSnapRole.Fold1 => 1,
			BuildSnapRole.Fold2 => 2,
			BuildSnapRole.Fold3 => 3,
			BuildSnapRole.AxisStart => 4,
			BuildSnapRole.AxisEnd => 5,
			_ => 6,
		};
}
