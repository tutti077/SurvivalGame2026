using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Oriented-solid math for a placed piece, in the same frame the structural solver and the
/// collider use: table-frame half extents swung through <see cref="BuildColliderSnap.GetSnapWorldRotation"/>
/// (kit quarter turn + baked prefab pitch). Built analytically rather than from renderer bounds so a
/// piece spawned this frame is already right, and so pitched roofs / 45° beams report their real
/// slab instead of a flat box across empty air.
/// </summary>
public static class BuildPieceGeometry
{
	/// <summary>World AABB of the piece's true solid.</summary>
	public static BBox WorldBounds( BuildPiece piece )
	{
		var rotation = BuildColliderSnap.GetSnapWorldRotation( piece.GameObject, piece.PieceId );
		var half = BuildColliderSnap.GetColliderHalfForPiece( piece.PieceId );
		var position = piece.GameObject.WorldPosition;

		var mins = new Vector3( float.MaxValue );
		var maxs = new Vector3( float.MinValue );
		for ( var xi = -1; xi <= 1; xi += 2 )
		for ( var yi = -1; yi <= 1; yi += 2 )
		for ( var zi = -1; zi <= 1; zi += 2 )
		{
			var corner = position + rotation * new Vector3( xi * half.x, yi * half.y, zi * half.z );
			mins = Vector3.Min( mins, corner );
			maxs = Vector3.Max( maxs, corner );
		}

		return new BBox( mins, maxs );
	}

	/// <summary>Closest point on the piece's oriented solid to <paramref name="point"/>.</summary>
	public static Vector3 ClosestPoint( BuildPiece piece, Vector3 point )
	{
		var rotation = BuildColliderSnap.GetSnapWorldRotation( piece.GameObject, piece.PieceId );
		var half = BuildColliderSnap.GetColliderHalfForPiece( piece.PieceId );
		var center = piece.GameObject.WorldPosition;

		var local = rotation.Inverse * (point - center);
		var clamped = new Vector3(
			Math.Clamp( local.x, -half.x, half.x ),
			Math.Clamp( local.y, -half.y, half.y ),
			Math.Clamp( local.z, -half.z, half.z ) );

		return center + rotation * clamped;
	}

	/// <summary>Distance from <paramref name="point"/> to the piece's solid (0 when inside).</summary>
	public static float DistanceToSurface( BuildPiece piece, Vector3 point ) =>
		Vector3.DistanceBetween( point, ClosestPoint( piece, point ) );
}
