using Sandbox;

namespace Survival;

/// <summary>
/// Snap positions from the piece's authored model bounds (see <see cref="BuildPieceModelCache"/>).
/// Roles (which corners / ends) stay layout-driven; distances come from the mesh.
/// </summary>
static class BuildColliderSnap
{
	/// <summary>Legacy prefab box size — only used when a piece has no model and no size table hit.</summary>
	public static readonly Vector3 PrefabColliderSize = new( 50f, 50f, 50f );

	/// <summary>
	/// The kit's meshes arrive in engine space yawed a quarter turn from the frame
	/// <see cref="BuildModuleDimensions.SizesMeters"/> describes: what the size table calls the X
	/// axis is the mesh's Y. Snap offsets are built in the table's frame, so they need the same
	/// quarter turn before they mean anything in world space.
	/// <para>
	/// This is why a square floor looked correct while everything else did not — a quarter turn maps
	/// a square's four corners onto each other, so the error was invisible there and nowhere else.
	/// A 2×1 floor, a wall, a triangle and a flight of stairs all show it.
	/// </para>
	/// <para>
	/// If snaps land a half turn out rather than on the corners, this is the sign to flip.
	/// </para>
	/// </summary>
	public static readonly Rotation KitMeshYaw = Rotation.FromYaw( 90f );

	/// <summary>
	/// Half extents the snap corners are built from.
	/// <para>
	/// The size table wins whenever it knows the piece. It is a compile-time constant that has been
	/// checked against the source FBX of every mesh in the kit, so it cannot be thrown off by asset
	/// load order, by a model failing to resolve, or by <see cref="Model.Bounds"/> being render
	/// bounds rather than a vertex box. The mesh is the fallback for anything the table has not
	/// been taught yet.
	/// </para>
	/// <para>
	/// Baked-pitch roofs and braces deliberately want the <b>unpitched</b> plate here — their snaps
	/// are plate-local and get pitched afterwards by <see cref="GetSnapWorldRotation"/>.
	/// </para>
	/// </summary>
	/// <summary>
	/// Piece size in the frame the mesh actually occupies. The box collider is axis-aligned to the
	/// piece root, so it needs <see cref="KitMeshYaw"/> applied to its extents — a quarter turn
	/// swaps X and Y — or the solid sits across the mesh instead of around it.
	/// </summary>
	public static Vector3 GetColliderSizeInMeshFrame( string pieceId )
	{
		var half = GetColliderHalfForPiece( pieceId );
		return new Vector3( half.y, half.x, half.z ) * 2f;
	}

	public static Vector3 GetColliderHalfForPiece( string pieceId )
	{
		if ( BuildModuleDimensions.TryGetHalfExtents( pieceId, out var tableHalf ) )
			return tableHalf;

		return BuildPieceModelCache.GetHalfExtents( pieceId );
	}

	public static Vector3 GetColliderHalfLocal( GameObject go )
	{
		var piece = go?.Components.Get<BuildPiece>();
		if ( piece is not null && !string.IsNullOrWhiteSpace( piece.PieceId ) )
			return GetColliderHalfForPiece( piece.PieceId );

		return PrefabColliderSize * 0.5f;
	}

	public static Rotation GetSnapWorldRotation( GameObject go, string pieceId )
	{
		if ( go is null || !go.IsValid() )
			return Rotation.Identity;

		// One composition for placed pieces and ghosts alike. A baked-pitch piece sits on a yaw-only
		// root and gets its pitch from here; every other piece has an identity pitch, so the same
		// expression serves both and the two paths cannot drift apart again.
		return GetSnapFrame( pieceId, go.WorldRotation );
	}

	/// <summary>
	/// Snap-local offset → world, for a piece placed at <paramref name="yawRotation"/>.
	/// <para>
	/// Order is the whole point. A snap offset starts in the size table's frame, so the baked prefab
	/// pitch — which is authored in that same frame — has to apply <b>first</b>, then
	/// <see cref="KitMeshYaw"/> carries the result into the frame the mesh occupies, and the piece's
	/// own yaw takes it to world. Applying the quarter turn before the pitch instead tips a pitched
	/// piece about its long axis rather than across it, which is why the 45° roof and the 45° braces
	/// were the only two families left wrong: every other piece has an identity pitch, so the order
	/// could not show.
	/// </para>
	/// <para>Pass the piece's plain placement yaw — this composes the pitch itself.</para>
	/// </summary>
	public static Rotation GetSnapFrame( string pieceId, Rotation yawRotation ) =>
		yawRotation * KitMeshYaw * BuildModuleDimensions.GetPrefabLocalRotation( pieceId );

	/// <summary>
	/// Every piece in the wood kit is authored centred on its own origin — verified against the
	/// source FBX vertices for all 23 meshes, each of which is symmetric about (0,0,0). Snap corners
	/// are therefore ±half about the origin and nothing else. <see cref="Model.Bounds"/> is render
	/// bounds, not a vertex AABB, so its Center can carry padding the mesh does not have; folding
	/// that into a corner offset slides every snap on the piece off the geometry it names.
	/// </summary>
	public static Vector3 GetCornerSnapLocal( string pieceId, BuildSnapRole role, Vector3 colliderHalfLocal )
	{
		var hx = colliderHalfLocal.x;
		var hy = colliderHalfLocal.y;
		var hz = colliderHalfLocal.z;

		if ( BuildSnapLayout.IsAxisRole( role ) )
		{
			var sign = role == BuildSnapRole.AxisEnd ? 1f : -1f;
			return BuildModuleDimensions.ResolveLongAxis( colliderHalfLocal ) switch
			{
				0 => new Vector3( hx * sign, 0f, 0f ),
				1 => new Vector3( 0f, hy * sign, 0f ),
				_ => new Vector3( 0f, 0f, hz * sign ),
			};
		}

		if ( BuildSnapLayout.TryGetRampFaces( pieceId, out var ramp ) )
			return GetRampCornerLocal( role, colliderHalfLocal, ramp );

		// Fold verts are authored in meters on a ±1 m half-module cube — scale to this mesh's half.
		if ( BuildSnapLayout.TryGetFoldSnapLocal( pieceId, role, out var foldMeters ) )
		{
			var scale = new Vector3(
				hx / BuildModuleDimensions.HalfModuleMeters,
				hy / BuildModuleDimensions.HalfModuleMeters,
				hz / BuildModuleDimensions.HalfModuleMeters );
			return new Vector3( foldMeters.x * scale.x, foldMeters.y * scale.y, foldMeters.z * scale.z );
		}

		// A plate's four corners sit on its two widest axes; the flattest axis collapses to the
		// mid-plane. The flattest axis is simply the smallest of the three extents in hand, which
		// always names one — the old test asked whether the piece was "thin enough to count as a
		// plate" first and answered "no" for anything it was unsure about, then fell through to the
		// floor face. That is how a wall ended up with its four snaps on the left and right edges at
		// mid-height instead of on the corners of its face.
		var flat = BuildModuleDimensions.ResolveFlattestAxis( colliderHalfLocal );

		// Of the two surviving axes, the lower-indexed one runs East/West and the higher one
		// North/South, so a floor reads X/Y and a wall reads X/Z exactly as before.
		var east = role is BuildSnapRole.CornerNorthEast or BuildSnapRole.CornerSouthEast ? 1f : -1f;
		var north = role is BuildSnapRole.CornerNorthEast or BuildSnapRole.CornerNorthWest ? 1f : -1f;

		if ( role is not (BuildSnapRole.CornerNorthEast or BuildSnapRole.CornerNorthWest
			or BuildSnapRole.CornerSouthEast or BuildSnapRole.CornerSouthWest) )
			return default;

		return flat switch
		{
			0 => new Vector3( 0f, hy * east, hz * north ),
			1 => new Vector3( hx * east, 0f, hz * north ),
			_ => new Vector3( hx * east, hy * north, 0f ),
		};
	}

	static Vector3 GetRampCornerLocal( BuildSnapRole role, Vector3 colliderHalfLocal, BuildRampFaces ramp )
	{
		var onEntry = role is BuildSnapRole.CornerNorthEast or BuildSnapRole.CornerNorthWest;
		var isEast = role is BuildSnapRole.CornerNorthEast or BuildSnapRole.CornerSouthEast;
		if ( !onEntry && role is not (BuildSnapRole.CornerSouthEast or BuildSnapRole.CornerSouthWest) )
			return default;

		var travel = onEntry ? ramp.Entry * -1f : ramp.Exit;
		var toRight = Vector3.Cross( travel, Vector3.Up );
		var face = onEntry ? ramp.Entry : ramp.Exit;

		var local = ScaleByHalf( face, colliderHalfLocal )
		            + ScaleByHalf( toRight * ( isEast ? 1f : -1f ), colliderHalfLocal );
		return local.WithZ( onEntry ? -colliderHalfLocal.z : colliderHalfLocal.z );
	}

	static Vector3 ScaleByHalf( Vector3 axis, Vector3 colliderHalfLocal ) =>
		new( axis.x * colliderHalfLocal.x, axis.y * colliderHalfLocal.y, axis.z * colliderHalfLocal.z );

	public static Vector3 GetCornerSnapWorld( GameObject go, string pieceId, BuildSnapRole role )
	{
		if ( go is null || !go.IsValid() )
			return default;

		// Half extents come from the mesh (or the size table), so they are already final world size —
		// do NOT fold go.LocalScale in again. Pieces are authored and spawned at scale 1
		// (BuildModuleDimensions.GetPieceLocalScale); a stale instance left at the old dev-box scale
		// would otherwise push every corner out by that scale instead of reading its real bounds.
		var half = GetColliderHalfForPiece( pieceId );
		var local = GetCornerSnapLocal( pieceId, role, half );
		var snapRot = GetSnapWorldRotation( go, pieceId );
		return new Transform( go.WorldPosition, snapRot, Vector3.One ).PointToWorld( local );
	}

	public static Vector3 GetCornerSnapWorldOffset(
		string pieceId,
		BuildSnapRole role,
		Rotation worldRotation,
		Vector3 localScale,
		Vector3 colliderHalfLocal )
	{
		var local = GetCornerSnapLocal( pieceId, role, colliderHalfLocal );
		return new Transform( Vector3.Zero, GetSnapFrame( pieceId, worldRotation ), localScale ).PointToWorld( local );
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

	public static float GetLowestWorldZOffset( string pieceId, Rotation placementRotation )
	{
		var colliderHalf = GetColliderHalfForPiece( pieceId );
		var fullRot = GetSnapFrame( pieceId, placementRotation );
		var minZ = float.MaxValue;

		if ( BuildSnapLayout.GetKind( pieceId ) == BuildSnapLayoutKind.FoldedRoofCorners )
		{
			var roles = BuildSnapLayout.GetRoles( pieceId );
			for ( var i = 0; i < roles.Count; i++ )
			{
				var worldOffset = GetCornerSnapWorldOffset( pieceId, roles[i], fullRot, Vector3.One, colliderHalf );
				minZ = Math.Min( minZ, worldOffset.z );
			}

			return minZ == float.MaxValue ? 0f : minZ;
		}

		for ( var xi = -1; xi <= 1; xi += 2 )
		{
			for ( var yi = -1; yi <= 1; yi += 2 )
			{
				for ( var zi = -1; zi <= 1; zi += 2 )
				{
					var local = GetBoxCornerLocal( colliderHalf, xi, yi, zi );
					var worldOffset = new Transform( Vector3.Zero, fullRot, Vector3.One ).PointToWorld( local );
					minZ = Math.Min( minZ, worldOffset.z );
				}
			}
		}

		return minZ == float.MaxValue ? 0f : minZ;
	}
}
