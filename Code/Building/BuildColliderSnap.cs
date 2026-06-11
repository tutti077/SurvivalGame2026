using Sandbox;

namespace Survival;

/// <summary>
/// Snap positions from <see cref="BoxCollider"/> edges (Scale = corner-to-corner size).
/// </summary>
static class BuildColliderSnap
{
	/// <summary>Default BoxCollider.Scale on build prefabs.</summary>
	public static readonly Vector3 PrefabColliderSize = new( 50f, 50f, 50f );

	public static Vector3 GetColliderHalfLocal( GameObject go )
	{
		var box = go?.Components.Get<BoxCollider>();
		if ( box is not null && box.IsValid() )
			return box.Scale * 0.5f;

		return PrefabColliderSize * 0.5f;
	}

	public static Vector3 GetCornerSnapLocal( string pieceId, BuildSnapRole role, Vector3 colliderHalfLocal )
	{
		var hx = colliderHalfLocal.x;
		var hy = colliderHalfLocal.y;
		var hz = colliderHalfLocal.z;
		var thinOnY = string.Equals( pieceId, "wall", StringComparison.OrdinalIgnoreCase );

		return role switch
		{
			BuildSnapRole.CornerNorthEast => thinOnY
				? new Vector3( hx, 0f, hz )
				: new Vector3( hx, hy, 0f ),
			BuildSnapRole.CornerNorthWest => thinOnY
				? new Vector3( -hx, 0f, hz )
				: new Vector3( -hx, hy, 0f ),
			BuildSnapRole.CornerSouthEast => thinOnY
				? new Vector3( hx, 0f, -hz )
				: new Vector3( hx, -hy, 0f ),
			BuildSnapRole.CornerSouthWest => thinOnY
				? new Vector3( -hx, 0f, -hz )
				: new Vector3( -hx, -hy, 0f ),
			_ => default,
		};
	}

	public static Vector3 GetCornerSnapWorld( GameObject go, string pieceId, BuildSnapRole role )
	{
		if ( go is null || !go.IsValid() )
			return default;

		var local = GetCornerSnapLocal( pieceId, role, GetColliderHalfLocal( go ) );
		var worldTransform = new Transform( go.WorldPosition, go.WorldRotation, go.LocalScale );
		return worldTransform.PointToWorld( local );
	}

	public static Vector3 GetCornerSnapWorldOffset(
		string pieceId,
		BuildSnapRole role,
		Rotation worldRotation,
		Vector3 localScale,
		Vector3 colliderHalfLocal )
	{
		var local = GetCornerSnapLocal( pieceId, role, colliderHalfLocal );
		return new Transform( Vector3.Zero, worldRotation, localScale ).PointToWorld( local );
	}

	public static Vector3 GetBoxCornerLocal( Vector3 colliderHalfLocal, int xi, int yi, int zi ) =>
		new( xi * colliderHalfLocal.x, yi * colliderHalfLocal.y, zi * colliderHalfLocal.z );

	public static Vector3 GetBoxCornerWorldOffset(
		Rotation worldRotation,
		Vector3 localScale,
		Vector3 colliderHalfLocal,
		int xi,
		int yi,
		int zi )
	{
		var local = GetBoxCornerLocal( colliderHalfLocal, xi, yi, zi );
		return new Transform( Vector3.Zero, worldRotation, localScale ).PointToWorld( local );
	}

	/// <summary>Lowest world-Z offset from piece origin (includes prefab pitch and local scale).</summary>
	public static float GetLowestWorldZOffset( string pieceId, Rotation placementRotation )
	{
		var scale = BuildModuleDimensions.GetPieceLocalScale( pieceId );
		var colliderHalf = PrefabColliderSize * 0.5f;
		var fullRot = placementRotation * BuildModuleDimensions.GetPrefabLocalRotation( pieceId );
		var minZ = float.MaxValue;

		for ( var xi = -1; xi <= 1; xi += 2 )
		{
			for ( var yi = -1; yi <= 1; yi += 2 )
			{
				for ( var zi = -1; zi <= 1; zi += 2 )
				{
					var worldOffset = GetBoxCornerWorldOffset( fullRot, scale, colliderHalf, xi, yi, zi );
					minZ = Math.Min( minZ, worldOffset.z );
				}
			}
		}

		return minZ == float.MaxValue ? 0f : minZ;
	}
}
