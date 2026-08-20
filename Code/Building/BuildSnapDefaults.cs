using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Builds a piece's snap set from <see cref="BuildSnapLayout"/> — four corner-edge snaps for
/// plates, two end snaps for beams. World positions come from <see cref="BuildColliderSnap"/>
/// (BoxCollider corner-to-corner scale / 2).
/// </summary>
static class BuildSnapDefaults
{
	public static void EnsureDefaults( BuildPieceData data )
	{
		if ( data is null || string.IsNullOrWhiteSpace( data.Id ) )
			return;

		data.SnapPoints = CreateDefaults( data.Id );
		data.AnchorSnapRole = BuildSnapLayout.UsesAxisEnds( data.Id )
			? BuildSnapRole.AxisStart
			: BuildSnapRole.CornerNorthEast;
	}

	static List<BuildSnapPointData> CreateDefaults( string pieceId )
	{
		// Snap counts differ per piece — plates get four corners, beams get two end points, and
		// furniture gets none. BuildSnapLayout owns which set a piece uses.
		var roles = BuildSnapLayout.GetRoles( pieceId );
		var snaps = new List<BuildSnapPointData>( roles.Count );
		for ( var i = 0; i < roles.Count; i++ )
			snaps.Add( RoleSnap( pieceId, roles[i] ) );

		return snaps;
	}

	static BuildSnapPointData RoleSnap( string pieceId, BuildSnapRole role )
	{
		var local = BuildColliderSnap.GetCornerSnapLocal( pieceId, role, BuildColliderSnap.PrefabColliderSize * 0.5f );
		var outward = local;
		if ( outward.LengthSquared < 1e-8f )
			outward = Vector3.Forward;

		// A beam's end snaps point straight up/down, where LookAt( dir, Up ) is degenerate.
		var direction = outward.Normal;
		var up = MathF.Abs( direction.z ) > 0.9f ? Vector3.Forward : Vector3.Up;
		var rot = Rotation.LookAt( direction, up );
		return new BuildSnapPointData
		{
			Role = role,
			LocalPosition = "0,0,0",
			LocalRotation = $"{rot.x},{rot.y},{rot.z},{rot.w}",
		};
	}
}
