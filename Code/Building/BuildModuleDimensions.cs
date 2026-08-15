namespace Survival;

/// <summary>
/// Strict build piece sizes in literal meters (X, Y, Z). Z-up.
/// Prefab <see cref="BuildColliderSnap.PrefabColliderSize"/> is 50³ at scale 1; piece
/// <see cref="DevBoxScale"/> is meters on each axis. Snap/overlap world sizes must use
/// collider×scale (not <see cref="UnitsPerMeter"/>), or interior seams falsely collide /
/// fall outside snap reach.
/// </summary>
public static class BuildModuleDimensions
{
	public const float UnitsPerMeter = 40f;
	/// <summary>Floor / wall module edge length (m). Floors are Module×Module; walls Module tall × Module wide.</summary>
	public const float ModuleMeters = 2f;
	public const float ThinMeters = 0.06f;
	/// <summary>
	/// 45° roof slope length (m): √(Module²+Module²) so one roof covers the run+rise of a module
	/// and two roofs meet in the middle across a 2-module span. Width stays <see cref="ModuleMeters"/>.
	/// </summary>
	public const float RoofSlopeMeters = 2.82f;

	/// <summary>Dev box at local scale 1 = 1 m edge.</summary>
	public const float DevBoxEdgeMeters = 1f;

	public const float ModuleUnits = ModuleMeters * UnitsPerMeter;
	public const float ThinUnits = ThinMeters * UnitsPerMeter;
	public const float ModuleHalfUnits = ModuleUnits * 0.5f;
	public const float ThinHalfUnits = ThinUnits * 0.5f;

	/// <summary>Half module in snap/collider world units (50×ModuleMeters/2).</summary>
	public static float SnapModuleHalfUnits =>
		BuildColliderSnap.PrefabColliderSize.x * 0.5f * ModuleMeters;

	/// <summary>Half thin axis in snap/collider world units.</summary>
	public static float SnapThinHalfUnits =>
		BuildColliderSnap.PrefabColliderSize.x * 0.5f * ThinMeters;

	/// <summary>Half roof slope-axis in snap/collider world units.</summary>
	public static float SnapRoofSlopeHalfUnits =>
		BuildColliderSnap.PrefabColliderSize.x * 0.5f * RoofSlopeMeters;

	/// <summary>Tiny lift so pieces sit on the surface instead of clipping through.</summary>
	public const float SurfaceContactBias = 0.25f;

	public static readonly Rotation RoofPrefabLocalRotation = new( -0.3826834f, 0f, 0f, 0.9238795f );

	public static Vector3 FloorSizeMeters => new( ModuleMeters, ModuleMeters, ThinMeters );

	public static Vector3 FloorHalfExtents =>
		new( SnapModuleHalfUnits, SnapModuleHalfUnits, SnapThinHalfUnits );

	public static Vector3 WallSizeMeters => new( ModuleMeters, ThinMeters, ModuleMeters );

	public static Vector3 WallHalfExtents =>
		new( SnapModuleHalfUnits, SnapThinHalfUnits, SnapModuleHalfUnits );

	/// <summary>X = wall width (module), Y = slope hypotenuse, Z = thickness.</summary>
	public static Vector3 RoofSizeMeters => new( ModuleMeters, RoofSlopeMeters, ThinMeters );

	public static Vector3 RoofHalfExtents =>
		new( SnapModuleHalfUnits, SnapRoofSlopeHalfUnits, SnapThinHalfUnits );

	/// <summary>Storage chest: 1 m wide, 0.6 m deep, 0.75 m tall.</summary>
	public static Vector3 ChestSizeMeters => new( 1f, 0.6f, 0.75f );

	public static Vector3 ChestHalfExtents =>
		new(
			BuildColliderSnap.PrefabColliderSize.x * 0.5f * ChestSizeMeters.x,
			BuildColliderSnap.PrefabColliderSize.y * 0.5f * ChestSizeMeters.y,
			BuildColliderSnap.PrefabColliderSize.z * 0.5f * ChestSizeMeters.z );

	/// <summary>Campfire: small uniform sphere (meters on each axis).</summary>
	public static Vector3 CampfireSizeMeters => new( 0.35f, 0.35f, 0.35f );

	public static Vector3 CampfireHalfExtents =>
		new(
			BuildColliderSnap.PrefabColliderSize.x * 0.5f * CampfireSizeMeters.x,
			BuildColliderSnap.PrefabColliderSize.y * 0.5f * CampfireSizeMeters.y,
			BuildColliderSnap.PrefabColliderSize.z * 0.5f * CampfireSizeMeters.z );

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

		if ( string.Equals( pieceId, "chest", StringComparison.OrdinalIgnoreCase ) )
		{
			halfExtents = ChestHalfExtents;
			return true;
		}

		if ( string.Equals( pieceId, "furniture_campfire", StringComparison.OrdinalIgnoreCase ) )
		{
			halfExtents = CampfireHalfExtents;
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
		if ( string.Equals( pieceId, "chest", StringComparison.OrdinalIgnoreCase ) )
			return ChestSizeMeters;
		if ( string.Equals( pieceId, "furniture_campfire", StringComparison.OrdinalIgnoreCase ) )
			return CampfireSizeMeters;
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
