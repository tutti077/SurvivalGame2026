using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Four corner-edge snaps per piece. World positions come from
/// <see cref="BuildColliderSnap"/> (BoxCollider corner-to-corner scale / 2).
/// </summary>
static class BuildSnapDefaults
{
	public static void EnsureDefaults( BuildPieceData data )
	{
		if ( data is null || string.IsNullOrWhiteSpace( data.Id ) )
			return;

		data.SnapPoints = CreateDefaults( data.Id );
		data.AnchorSnapRole = BuildSnapRole.CornerNorthEast;
	}

	static List<BuildSnapPointData> CreateDefaults( string pieceId )
	{
		if ( string.Equals( pieceId, "wall", StringComparison.OrdinalIgnoreCase )
		     || string.Equals( pieceId, "foundation", StringComparison.OrdinalIgnoreCase )
		     || string.Equals( pieceId, "45roof", StringComparison.OrdinalIgnoreCase ) )
		{
			return new List<BuildSnapPointData>
			{
				RoleSnap( pieceId, BuildSnapRole.CornerNorthEast ),
				RoleSnap( pieceId, BuildSnapRole.CornerNorthWest ),
				RoleSnap( pieceId, BuildSnapRole.CornerSouthEast ),
				RoleSnap( pieceId, BuildSnapRole.CornerSouthWest ),
			};
		}

		return new List<BuildSnapPointData>();
	}

	static BuildSnapPointData RoleSnap( string pieceId, BuildSnapRole role )
	{
		var local = BuildColliderSnap.GetCornerSnapLocal( pieceId, role, BuildColliderSnap.PrefabColliderSize * 0.5f );
		var outward = local;
		if ( outward.LengthSquared < 1e-8f )
			outward = Vector3.Forward;

		var rot = Rotation.LookAt( outward.Normal, Vector3.Up );
		return new BuildSnapPointData
		{
			Role = role,
			LocalPosition = "0,0,0",
			LocalRotation = $"{rot.x},{rot.y},{rot.z},{rot.w}",
		};
	}
}
