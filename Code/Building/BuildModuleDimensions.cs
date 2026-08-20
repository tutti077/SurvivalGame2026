using System;
using System.Collections.Generic;

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
	public const float HalfModuleMeters = 1f;
	public const float ThinMeters = 0.06f;
	/// <summary>Square section of every post / beam (m).</summary>
	public const float BeamMeters = 0.5f;
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
	public static float SnapModuleHalfUnits => HalfUnitsFor( ModuleMeters );

	/// <summary>Half thin axis in snap/collider world units.</summary>
	public static float SnapThinHalfUnits => HalfUnitsFor( ThinMeters );

	/// <summary>Half roof slope-axis in snap/collider world units.</summary>
	public static float SnapRoofSlopeHalfUnits => HalfUnitsFor( RoofSlopeMeters );

	/// <summary>Tiny lift so pieces sit on the surface instead of clipping through.</summary>
	public const float SurfaceContactBias = 0.25f;

	public static readonly Rotation RoofPrefabLocalRotation = new( -0.3826834f, 0f, 0f, 0.9238795f );

	/// <summary>45° about local Y so a length-on-X beam rises as it runs forward.</summary>
	public static readonly Rotation Beam45PrefabLocalRotation = new( 0f, -0.3826834f, 0f, 0.9238795f );

	public static Vector3 FloorSizeMeters => new( ModuleMeters, ModuleMeters, ThinMeters );

	public static Vector3 FloorHalfExtents => HalfExtentsFor( FloorSizeMeters );

	public static Vector3 WallSizeMeters => new( ModuleMeters, ThinMeters, ModuleMeters );

	public static Vector3 WallHalfExtents => HalfExtentsFor( WallSizeMeters );

	/// <summary>X = wall width (module), Y = slope hypotenuse, Z = thickness.</summary>
	public static Vector3 RoofSizeMeters => new( ModuleMeters, RoofSlopeMeters, ThinMeters );

	public static Vector3 RoofHalfExtents => HalfExtentsFor( RoofSizeMeters );

	/// <summary>Storage chest: 1 m wide, 0.6 m deep, 0.75 m tall.</summary>
	public static Vector3 ChestSizeMeters => new( 1f, 0.6f, 0.75f );

	public static Vector3 ChestHalfExtents => HalfExtentsFor( ChestSizeMeters );

	/// <summary>Campfire: small uniform sphere (meters on each axis).</summary>
	public static Vector3 CampfireSizeMeters => new( 0.35f, 0.35f, 0.35f );

	public static Vector3 CampfireHalfExtents => HalfExtentsFor( CampfireSizeMeters );

	/// <summary>
	/// Authoritative size table for the wood kit. Ids match <c>Assets/data/build_pieces.json</c>.
	/// Beams are <see cref="BeamMeters"/> square with their length on the axis they run along, so a
	/// vertical post is tall on Z and a horizontal beam is long on X.
	/// </summary>
	static readonly Dictionary<string, Vector3> SizesMeters = new( StringComparer.OrdinalIgnoreCase )
	{
		// Floors — thin on Z.
		["build_wood_floor"] = new( ModuleMeters, ModuleMeters, ThinMeters ),
		["build_wood_floor_2m1m"] = new( ModuleMeters, HalfModuleMeters, ThinMeters ),
		["build_wood_floor_1m1m"] = new( HalfModuleMeters, HalfModuleMeters, ThinMeters ),
		["build_wood_triangleFloor"] = new( ModuleMeters, ModuleMeters, ThinMeters ),

		// Walls — thin on Y, height on Z.
		["build_wood_wall"] = new( ModuleMeters, ThinMeters, ModuleMeters ),
		["build_wood_wall_2m1m"] = new( ModuleMeters, ThinMeters, HalfModuleMeters ),
		["build_wood_wall_1m1m"] = new( HalfModuleMeters, ThinMeters, HalfModuleMeters ),
		["build_wood_door"] = new( ModuleMeters, ThinMeters, ModuleMeters ),
		["build_wood_45wallLeft"] = new( ModuleMeters, ThinMeters, ModuleMeters ),
		["build_wood_45wallRight"] = new( ModuleMeters, ThinMeters, ModuleMeters ),

		// Roofs — width on X, slope on Y, thin on Z, pitched by RoofPrefabLocalRotation.
		["build_wood_45roof"] = new( ModuleMeters, RoofSlopeMeters, ThinMeters ),
		["build_wood_45roofInsideCorner"] = new( ModuleMeters, RoofSlopeMeters, ThinMeters ),
		["build_wood_45roofOutsideCorner"] = new( ModuleMeters, RoofSlopeMeters, ThinMeters ),

		// Stairs — full module box footprint and rise.
		["build_wood_stairs"] = new( ModuleMeters, ModuleMeters, ModuleMeters ),
		["build_wood_stairsSpiral"] = new( ModuleMeters, ModuleMeters, ModuleMeters ),

		// Beams / posts — 0.5 m square section.
		["build_wood_horizontalBeam_1m"] = new( HalfModuleMeters, BeamMeters, BeamMeters ),
		["build_wood_horizontalBeam_2m"] = new( ModuleMeters, BeamMeters, BeamMeters ),
		["build_wood_verticalBeam_1m"] = new( BeamMeters, BeamMeters, HalfModuleMeters ),
		["build_wood_verticalBeam_2m"] = new( BeamMeters, BeamMeters, ModuleMeters ),
		["build_wood_45Beam_1m"] = new( HalfModuleMeters, BeamMeters, BeamMeters ),
		["build_wood_45Beam_2m"] = new( ModuleMeters, BeamMeters, BeamMeters ),

		// Furniture / stations (unchanged by the wood rename).
		["chest"] = new( 1f, 0.6f, 0.75f ),
		["augment_station"] = new( 1f, 0.6f, 0.75f ),
		["furniture_campfire"] = new( 0.35f, 0.35f, 0.35f ),
	};

	static float HalfUnitsFor( float meters ) =>
		BuildColliderSnap.PrefabColliderSize.x * 0.5f * meters;

	/// <summary>Collider-space half extents for a size in meters (collider is 50³ at scale 1).</summary>
	public static Vector3 HalfExtentsFor( Vector3 sizeMeters ) =>
		new(
			BuildColliderSnap.PrefabColliderSize.x * 0.5f * sizeMeters.x,
			BuildColliderSnap.PrefabColliderSize.y * 0.5f * sizeMeters.y,
			BuildColliderSnap.PrefabColliderSize.z * 0.5f * sizeMeters.z );

	public static bool TryGetSizeMeters( string pieceId, out Vector3 sizeMeters )
	{
		sizeMeters = default;
		return !string.IsNullOrWhiteSpace( pieceId ) && SizesMeters.TryGetValue( pieceId, out sizeMeters );
	}

	/// <summary>
	/// Index of the axis a piece is flat on (0=X, 1=Y, 2=Z), or -1 when it is chunky on every axis.
	/// Read from the size table rather than the id, so "which way is this plate thin" is answered by
	/// the piece's real dimensions — a wall is thin on Y, a floor or roof on Z, and a beam (square
	/// section) has no thin axis at all.
	/// </summary>
	public static int GetThinAxis( string pieceId )
	{
		if ( !TryGetSizeMeters( pieceId, out var size ) )
			return -1;

		var thin = 0;
		if ( size.y < size.x ) thin = 1;
		if ( size.z < (thin == 1 ? size.y : size.x) ) thin = 2;

		var thinValue = thin switch { 0 => size.x, 1 => size.y, _ => size.z };
		var otherMin = thin switch
		{
			0 => Math.Min( size.y, size.z ),
			1 => Math.Min( size.x, size.z ),
			_ => Math.Min( size.x, size.y ),
		};

		// Only a genuine plate counts — needs to be well under half the next-smallest side.
		return thinValue < otherMin * 0.5f ? thin : -1;
	}

	/// <summary>Wall-like: flat on local Y, so corner snaps live on the X/Z face.</summary>
	public static bool IsThinOnY( string pieceId ) => GetThinAxis( pieceId ) == 1;

	/// <summary>
	/// Index of the axis a piece runs along (0=X, 1=Y, 2=Z) — the length of a beam or post.
	/// Used to place the two <see cref="BuildSnapRole.AxisStart"/> / <see cref="BuildSnapRole.AxisEnd"/>
	/// snaps, so a vertical post gets bottom/top and a horizontal beam gets its two ends.
	/// </summary>
	public static int GetLongAxis( string pieceId )
	{
		if ( !TryGetSizeMeters( pieceId, out var size ) )
			return 2;

		if ( size.z >= size.x && size.z >= size.y )
			return 2;

		return size.x >= size.y ? 0 : 1;
	}

	public static bool TryGetHalfExtents( string pieceId, out Vector3 halfExtents )
	{
		halfExtents = default;
		if ( !TryGetSizeMeters( pieceId, out var size ) )
			return false;

		halfExtents = HalfExtentsFor( size );
		return true;
	}

	public static Vector3 GetHalfExtents( string pieceId ) =>
		TryGetHalfExtents( pieceId, out var half ) ? half : FloorHalfExtents;

	public static Vector3 GetSizeMeters( string pieceId ) =>
		TryGetSizeMeters( pieceId, out var size ) ? size : FloorSizeMeters;

	public static Rotation GetPrefabLocalRotation( string pieceId )
	{
		if ( BuildPieceFamily.IsRoof( pieceId ) )
			return RoofPrefabLocalRotation;

		if ( BuildPieceFamily.IsBeam( pieceId ) && pieceId.Contains( "45", StringComparison.OrdinalIgnoreCase ) )
			return Beam45PrefabLocalRotation;

		return Rotation.Identity;
	}

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
