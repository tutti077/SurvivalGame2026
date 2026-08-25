using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Builds a piece's snap set from <see cref="BuildSnapLayout"/> — four corner-edge snaps for
/// plates, three for triangles, four ramp-surface corners for stairs, six fold vertices for hip /
/// valley roof corners, two end snaps for beams.
/// World positions come from <see cref="BuildColliderSnap"/> (BoxCollider corner-to-corner
/// scale / 2), so the JSON carries only the role list, never coordinates.
/// </summary>
static class BuildSnapDefaults
{
	public static void EnsureDefaults( BuildPieceData data )
	{
		if ( data is null || string.IsNullOrWhiteSpace( data.Id ) )
			return;

		data.SnapPoints = CreateDefaults( data.Id );

		// Take the anchor from the piece's own set — hardcoding a corner named a snap the triangle
		// pieces do not have.
		var roles = BuildSnapLayout.GetRoles( data.Id );
		data.AnchorSnapRole = roles.Count > 0 ? roles[0] : BuildSnapRole.Unknown;
	}

	static List<BuildSnapPointData> CreateDefaults( string pieceId )
	{
		// Snap counts differ per piece — plates get four corners, triangles three, beams two end
		// points, furniture none. BuildSnapLayout owns which set a piece uses.
		var roles = BuildSnapLayout.GetRoles( pieceId );
		var snaps = new List<BuildSnapPointData>( roles.Count );
		for ( var i = 0; i < roles.Count; i++ )
			snaps.Add( RoleSnap( pieceId, roles[i] ) );

		return snaps;
	}

	static BuildSnapPointData RoleSnap( string pieceId, BuildSnapRole role )
	{
		var local = BuildColliderSnap.GetCornerSnapLocal( pieceId, role, BuildColliderSnap.GetColliderHalfForPiece( pieceId ) );
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
