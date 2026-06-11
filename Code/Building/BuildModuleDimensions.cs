namespace Survival;

/// <summary>
/// Strict build piece sizes in literal meters (X, Y, Z). Z-up.
/// <see cref="models/dev/box.vmdl"/> is 1 m per edge at scale 1 — prefab scale equals meters on each axis.
/// Placement/overlap uses <see cref="UnitsPerMeter"/> to convert into game units.
/// </summary>
public static class BuildModuleDimensions
{
	public const float UnitsPerMeter = 40f;
	public const float ModuleMeters = 1.5f;
	public const float ThinMeters = 0.06f;

	/// <summary>Dev box at local scale 1 = 1 m edge.</summary>
	public const float DevBoxEdgeMeters = 1f;

	public const float ModuleUnits = ModuleMeters * UnitsPerMeter;
	public const float ThinUnits = ThinMeters * UnitsPerMeter;
	public const float ModuleHalfUnits = ModuleUnits * 0.5f;
	public const float ThinHalfUnits = ThinUnits * 0.5f;

	/// <summary>Tiny lift so pieces sit on the surface instead of clipping through.</summary>
	public const float SurfaceContactBias = 0.25f;

	public static readonly Rotation RoofPrefabLocalRotation = new( -0.3826834f, 0f, 0f, 0.9238795f );

	public static Vector3 FloorSizeMeters => new( ModuleMeters, ModuleMeters, ThinMeters );

	public static Vector3 FloorHalfExtents => FloorSizeMeters * 0.5f * UnitsPerMeter;

	public static Vector3 WallSizeMeters => new( ModuleMeters, ThinMeters, ModuleMeters );

	public static Vector3 WallHalfExtents => WallSizeMeters * 0.5f * UnitsPerMeter;

	public static Vector3 RoofSizeMeters => new( ModuleMeters, ModuleMeters, ThinMeters );

	public static Vector3 RoofHalfExtents => RoofSizeMeters * 0.5f * UnitsPerMeter;

	public static bool TryGetHalfExtents( string pieceId, out Vector3 halfExtents )
	{
		halfExtents = default;
		if ( string.IsNullOrWhiteSpace( pieceId ) )
			return false;

		if ( string.Equals( pieceId, "foundation", StringComparison.OrdinalIgnoreCase ) )
		{
			halfExtents = FloorHalfExtents;
			return true;
		}

		if ( string.Equals( pieceId, "wall", StringComparison.OrdinalIgnoreCase ) )
		{
			halfExtents = WallHalfExtents;
			return true;
		}

		if ( string.Equals( pieceId, "45roof", StringComparison.OrdinalIgnoreCase ) )
		{
			halfExtents = RoofHalfExtents;
			return true;
		}

		return false;
	}

	public static Vector3 GetHalfExtents( string pieceId ) =>
		TryGetHalfExtents( pieceId, out var half ) ? half : FloorHalfExtents;

	public static Vector3 GetSizeMeters( string pieceId )
	{
		if ( string.Equals( pieceId, "wall", StringComparison.OrdinalIgnoreCase ) )
			return WallSizeMeters;
		if ( string.Equals( pieceId, "45roof", StringComparison.OrdinalIgnoreCase ) )
			return RoofSizeMeters;
		return FloorSizeMeters;
	}

	public static Rotation GetPrefabLocalRotation( string pieceId ) =>
		string.Equals( pieceId, "45roof", StringComparison.OrdinalIgnoreCase )
			? RoofPrefabLocalRotation
			: Rotation.Identity;

	public static float GetGroundSitHalfExtent( string pieceId ) =>
		GetGroundSitHalfExtent( pieceId, Rotation.Identity );

	/// <summary>Distance from piece center to ground contact along world up.</summary>
	public static float GetGroundSitHalfExtent( string pieceId, Rotation placementRotation )
	{
		var minZ = BuildColliderSnap.GetLowestWorldZOffset( pieceId, placementRotation );
		return Math.Max( 0f, -minZ ) + SurfaceContactBias;
	}

	public static Vector3 RotateLocalOffset( Rotation rotation, Vector3 local ) => rotation * local;

	public static Vector3 GetPieceLocalScale( string pieceId ) =>
		DevBoxScale( GetSizeMeters( pieceId ) );

	/// <summary>Local scale for dev box — one scale unit = one meter on that axis.</summary>
	public static Vector3 DevBoxScale( Vector3 sizeMeters ) =>
		new(
			sizeMeters.x / DevBoxEdgeMeters,
			sizeMeters.y / DevBoxEdgeMeters,
			sizeMeters.z / DevBoxEdgeMeters );
}
