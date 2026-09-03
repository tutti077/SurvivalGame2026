using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>
/// Strict build piece sizes in literal meters (X, Y, Z). Z-up.
/// Prefab <see cref="BuildColliderSnap.PrefabColliderSize"/> is 50 per meter on each axis;
/// piece <see cref="GetColliderScale"/> bakes meters into <see cref="BoxCollider.Scale"/> while
/// the root stays at scale 1. Snap/overlap world sizes read that collider half-extent directly.
/// </summary>
public static class BuildModuleDimensions
{
	/// <summary>World scale outside build snap: 40 engine units per meter (see <see cref="TerrainWorldUnits"/>). Build piece colliders use 50/m — see <see cref="BuildColliderSnap.PrefabColliderSize"/>.</summary>
	public const float UnitsPerMeter = 40f;
	/// <summary>Floor / wall module edge length (m). Floors are Module×Module; walls Module tall × Module wide.</summary>
	public const float ModuleMeters = 2f;
	public const float HalfModuleMeters = 1f;
	public const float ThinMeters = 0.06f;
	/// <summary>Square section of every post / beam (m).</summary>
	public const float BeamMeters = 0.2f;
	/// <summary>
	/// 45° roof slope length (m): √(Module²+Module²) so one roof covers the run+rise of a module
	/// and two roofs meet in the middle across a 2-module span. Width stays <see cref="ModuleMeters"/>.
	/// Rounded to 2.82 this landed the pitched corner snaps at ±0.997 m instead of ±1 m, which is a
	/// 3 mm seam per module — the Blender kit already builds the hypotenuse at the exact value.
	/// </summary>
	public const float RoofSlopeMeters = 2.8284271f;

	/// <summary>Dev box at local scale 1 = 1 m edge.</summary>
	public const float DevBoxEdgeMeters = 1f;

	/// <summary>Half module in snap/collider world units (50×ModuleMeters/2).</summary>
	public static float SnapModuleHalfUnits => HalfUnitsFor( ModuleMeters );

	/// <summary>Half thin axis in snap/collider world units.</summary>
	public static float SnapThinHalfUnits => HalfUnitsFor( ThinMeters );


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
		// Folded corners fill the 2×2×2 module cube; mesh carries the hip / valley shape.
		["build_wood_45roofInsideCorner"] = new( ModuleMeters, ModuleMeters, ModuleMeters ),
		["build_wood_45roofOutsideCorner"] = new( ModuleMeters, ModuleMeters, ModuleMeters ),

		// Stairs — module footprint, half-module rise: 1 m climb per piece, so two stack into a
		// storey and a flight can turn part way up. The two spirals are quarter turns in the same
		// box, one each way.
		["build_wood_stairs"] = new( ModuleMeters, ModuleMeters, HalfModuleMeters ),
		["build_wood_stairsSpiralLeft"] = new( ModuleMeters, ModuleMeters, HalfModuleMeters ),
		["build_wood_stairsSpiralRight"] = new( ModuleMeters, ModuleMeters, HalfModuleMeters ),

		// Beams / posts — 0.2 m square section.
		["build_wood_horizontalBeam_1m"] = new( HalfModuleMeters, BeamMeters, BeamMeters ),
		["build_wood_horizontalBeam_2m"] = new( ModuleMeters, BeamMeters, BeamMeters ),
		["build_wood_verticalBeam_1m"] = new( BeamMeters, BeamMeters, HalfModuleMeters ),
		["build_wood_verticalBeam_2m"] = new( BeamMeters, BeamMeters, ModuleMeters ),
		["build_wood_45Beam_1m"] = new( HalfModuleMeters, BeamMeters, BeamMeters ),
		["build_wood_45Beam_2m"] = new( ModuleMeters, BeamMeters, BeamMeters ),

		// Furniture / stations (unchanged by the wood rename).
		// Chest: 1 m long, 0.5 m wide, 0.5 m tall.
		["chest"] = new( 1f, 0.5f, 0.5f ),
		["augment_station"] = new( 1f, 0.6f, 0.75f ),
		["furniture_campfire"] = new( 0.35f, 0.35f, 0.35f ),
		// Workbench: 2 m wide, 1 m deep, 1 m tall (tool repair + workbench recipes).
		["workbench"] = new( 2f, 1f, 1f ),
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
	/// <para>
	/// Measured from the extents the snap system actually places corners with — the authored mesh,
	/// or the size table for baked-pitch pieces — so it can never disagree with where those corners
	/// land. It used to look the piece up in the size table by id, which returned -1 for any id the
	/// table missed and silently dropped that piece onto the <b>floor</b> corner layout: harmless for
	/// a floor, which is what that layout describes, and wrong for every wall.
	/// </para>
	/// </summary>
	public static int GetThinAxis( string pieceId ) =>
		ResolveThinAxis( BuildColliderSnap.GetColliderHalfForPiece( pieceId ) );

	/// <summary>
	/// Flattest axis of a half-extent vector (0=X, 1=Y, 2=Z). Always names one — unlike
	/// <see cref="ResolveThinAxis"/> this asks no "is it flat enough" question, because the corners
	/// of a plate are on its two widest axes whatever the ratio happens to be.
	/// </summary>
	public static int ResolveFlattestAxis( Vector3 half )
	{
		if ( half.x <= half.y && half.x <= half.z )
			return 0;

		return half.y <= half.z ? 1 : 2;
	}

	/// <summary>Flat axis of a half-extent vector, or -1 when no side is thin enough to be a plate.</summary>
	public static int ResolveThinAxis( Vector3 half )
	{
		var thin = 0;
		if ( half.y < half.x ) thin = 1;
		if ( half.z < (thin == 1 ? half.y : half.x) ) thin = 2;

		var thinValue = thin switch { 0 => half.x, 1 => half.y, _ => half.z };
		var otherMin = thin switch
		{
			0 => Math.Min( half.y, half.z ),
			1 => Math.Min( half.x, half.z ),
			_ => Math.Min( half.x, half.y ),
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
	public static int GetLongAxis( string pieceId ) =>
		ResolveLongAxis( BuildColliderSnap.GetColliderHalfForPiece( pieceId ) );

	/// <summary>Longest axis of a half-extent vector — the run of a beam or post.</summary>
	public static int ResolveLongAxis( Vector3 half )
	{
		if ( half.z >= half.x && half.z >= half.y )
			return 2;

		return half.x >= half.y ? 0 : 1;
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
		// Hip / valley corners are already folded in the mesh — pitching them again would double-rotate.
		if ( BuildPieceFamily.IsCorner( pieceId ) )
			return Rotation.Identity;

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

	/// <summary>Corners within this many units of the lowest count as ground contact (covers a plate's thickness pair).</summary>
	const float GroundContactCornerTolerance = 6f;

	/// <summary>
	/// XY offset from the piece origin to the centroid of its ground-contact corners — the lowest
	/// corners of the solid in the snap frame (yaw × kit quarter-turn × baked pitch). Zero for
	/// axis-aligned pieces, whose contact sits under the origin; for pitched roofs and 45° beams
	/// it names the low edge / low tip, i.e. the part that actually rests on the ground. Free
	/// placement holds the ghost by this point so the contact spot rides the crosshair instead of
	/// the piece hanging a meter to one side of it.
	/// </summary>
	public static Vector3 GetGroundContactOffsetXY( string pieceId, Rotation placementRotation )
	{
		var half = BuildColliderSnap.GetColliderHalfForPiece( pieceId );

		// Hip / valley corners fill a full module cube in the size table, so box corners see no
		// tilt — their real resting points are the authored fold vertices (a valley rests on its
		// single low gutter vertex, a hip on its low ring). Same source and composition
		// GetLowestWorldZOffset uses, so the hold point and the ground-sit height always agree.
		if ( BuildSnapLayout.GetKind( pieceId ) == BuildSnapLayoutKind.FoldedRoofCorners )
		{
			var roles = BuildSnapLayout.GetRoles( pieceId );
			var fullRot = BuildColliderSnap.GetSnapFrame( pieceId, placementRotation );
			var foldOffsets = new Vector3[roles.Count];
			for ( var i = 0; i < roles.Count; i++ )
				foldOffsets[i] = BuildColliderSnap.GetCornerSnapWorldOffset( pieceId, roles[i], fullRot, Vector3.One, half );

			return ContactCentroidXY( foldOffsets );
		}

		var rotation = BuildColliderSnap.GetSnapFrame( pieceId, placementRotation );
		var offsets = new Vector3[8];
		var index = 0;
		for ( var xi = -1; xi <= 1; xi += 2 )
		for ( var yi = -1; yi <= 1; yi += 2 )
		for ( var zi = -1; zi <= 1; zi += 2 )
			offsets[index++] = rotation * new Vector3( xi * half.x, yi * half.y, zi * half.z );

		return ContactCentroidXY( offsets );
	}

	/// <summary>XY centroid of the lowest offsets (within the contact tolerance of the minimum).</summary>
	static Vector3 ContactCentroidXY( Vector3[] offsets )
	{
		var minZ = float.MaxValue;
		foreach ( var offset in offsets )
			minZ = Math.Min( minZ, offset.z );

		var sum = Vector3.Zero;
		var count = 0;
		foreach ( var offset in offsets )
		{
			if ( offset.z > minZ + GroundContactCornerTolerance )
				continue;

			sum += offset;
			count++;
		}

		if ( count == 0 )
			return Vector3.Zero;

		var centroid = sum / count;
		return new Vector3( centroid.x, centroid.y, 0f );
	}

	public static Vector3 RotateLocalOffset( Rotation rotation, Vector3 local ) => rotation * local;

	/// <summary>Root transform scale — always 1; sizing lives on the box collider.</summary>
	public static Vector3 GetPieceLocalScale( string pieceId ) => Vector3.One;

	/// <summary>BoxCollider.Scale per axis: 50 world units per meter on that axis.</summary>
	public static Vector3 GetColliderScale( string pieceId ) =>
		DevBoxScale( GetSizeMeters( pieceId ) ) * BuildColliderSnap.PrefabColliderSize;

	/// <summary>Collider-local half extents (same space as snap corner math).</summary>
	public static Vector3 GetColliderHalfLocal( string pieceId ) =>
		GetColliderScale( pieceId ) * 0.5f;

	/// <summary>Meters per axis — multiplied by <see cref="BuildColliderSnap.PrefabColliderSize"/> for the box.</summary>
	public static Vector3 DevBoxScale( Vector3 sizeMeters ) =>
		new(
			sizeMeters.x / DevBoxEdgeMeters,
			sizeMeters.y / DevBoxEdgeMeters,
			sizeMeters.z / DevBoxEdgeMeters );
}
