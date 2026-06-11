using System;
using Sandbox;

namespace Survival;

public enum SnapEdgeId
{
	North,
	South,
	East,
	West,
}

/// <summary>Two corner snaps forming one thin-edge side of a piece.</summary>
public readonly struct SnapEdge
{
	public SnapEdgeId Id { get; init; }
	public BuildSnapRole CornerA { get; init; }
	public BuildSnapRole CornerB { get; init; }
}

static class BuildSnapEdge
{
	public const float EdgeAlignTolerance = 6f;

	public static readonly SnapEdge[] ThinPlaneEdges =
	{
		new() { Id = SnapEdgeId.North, CornerA = BuildSnapRole.CornerNorthWest, CornerB = BuildSnapRole.CornerNorthEast },
		new() { Id = SnapEdgeId.South, CornerA = BuildSnapRole.CornerSouthWest, CornerB = BuildSnapRole.CornerSouthEast },
		new() { Id = SnapEdgeId.East, CornerA = BuildSnapRole.CornerNorthEast, CornerB = BuildSnapRole.CornerSouthEast },
		new() { Id = SnapEdgeId.West, CornerA = BuildSnapRole.CornerNorthWest, CornerB = BuildSnapRole.CornerSouthWest },
	};

	public static SnapEdgeId GetOpposite( SnapEdgeId edge ) =>
		edge switch
		{
			SnapEdgeId.North => SnapEdgeId.South,
			SnapEdgeId.South => SnapEdgeId.North,
			SnapEdgeId.East => SnapEdgeId.West,
			SnapEdgeId.West => SnapEdgeId.East,
			_ => SnapEdgeId.North,
		};

	public static bool TryGetEdge( SnapEdgeId id, out SnapEdge edge )
	{
		for ( var i = 0; i < ThinPlaneEdges.Length; i++ )
		{
			if ( ThinPlaneEdges[i].Id == id )
			{
				edge = ThinPlaneEdges[i];
				return true;
			}
		}

		edge = default;
		return false;
	}

	/// <summary>Placing edge that mates flush against the target edge (e.g. new south → built north).</summary>
	public static SnapEdgeId GetPlacingEdgeForTarget( SnapEdgeId targetEdge ) => GetOpposite( targetEdge );

	public static float ScoreEdgeAim( Vector3 probe, Vector3 worldA, Vector3 worldB )
	{
		var segDist = DistancePointToSegment( probe, worldA, worldB );
		var score = segDist;

		if ( TryProjectOnSegment( probe, worldA, worldB, out _, out var t ) && t is >= 0.08f and <= 0.92f )
			score *= 0.25f;

		return score;
	}

	public static float DistancePointToSegment( Vector3 point, Vector3 a, Vector3 b )
	{
		if ( !TryProjectOnSegment( point, a, b, out var closest, out _ ) )
			return float.MaxValue;

		return Vector3.DistanceBetween( point, closest );
	}

	public static bool TryProjectOnSegment( Vector3 point, Vector3 a, Vector3 b, out Vector3 closest, out float t )
	{
		closest = default;
		t = 0f;
		var ab = b - a;
		var lenSq = ab.LengthSquared;
		if ( lenSq < 1e-8f )
		{
			closest = a;
			return true;
		}

		t = Math.Clamp( Vector3.Dot( point - a, ab ) / lenSq, 0f, 1f );
		closest = a + ab * t;
		return true;
	}
}
