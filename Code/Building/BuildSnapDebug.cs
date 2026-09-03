using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Snap marker positions (drawn from a Component via single-frame overlay).</summary>
static class BuildSnapDebug
{
	public static float SphereRadius => BuildModuleDimensions.SnapThinHalfUnits;

	/// <summary>Mirrors ToolBuildHammer.ShowSnapDebug so the static snap code can log alongside the markers.</summary>
	public static bool LogEdgeRejects { get; set; }

	static double _nextRejectLog;
	static string _lastRejectLine = string.Empty;

	/// <summary>
	/// Why a seam did not take. Throttled to once a second and deduplicated, so holding a piece
	/// against a structure prints one readable line per situation instead of a wall of text.
	/// </summary>
	public static void LogEdgeReject( string placingPieceId, string targetPieceId, SnapEdgeId targetEdge, string reason )
	{
		if ( !LogEdgeRejects )
			return;

		var line = $"[snap] {placingPieceId} vs {targetPieceId} {targetEdge} edge — {reason}";
		if ( line == _lastRejectLine && Time.Now < _nextRejectLog )
			return;

		_lastRejectLine = line;
		_nextRejectLog = Time.Now + 1.0;
		Log.Info( line );
	}

	public static readonly Color DefaultColor = new( 0.2f, 0.85f, 1f, 0.9f );
	public static readonly Color ActiveColor = new( 1f, 0.92f, 0.15f, 1f );
	public static readonly Color InvalidColor = new( 1f, 0.25f, 0.2f, 0.85f );
	public static readonly Color PreviewColor = new( 0.45f, 1f, 0.55f, 0.95f );
	public static readonly Color RayColor = new( 0.25f, 0.95f, 1f, 0.9f );
	public static readonly Color RayHitColor = new( 1f, 0.92f, 0.15f, 1f );
	public static readonly Color ProbeColor = new( 1f, 0.25f, 0.95f, 1f );
	public static readonly Color AimDropColor = new( 1f, 0.55f, 0.12f, 0.95f );

	/// <summary>
	/// One-shot report: what the snap system believes about every catalog piece, next to what the
	/// authored mesh says. Runs from a designer toggle, never from a tick — this is the "validate on
	/// demand" path, not a per-frame check.
	/// </summary>
	public static void LogPieceReport()
	{
		BuildPieceCatalog.EnsureLoaded();
		Log.Info( "[snap-report] pieceId | layout | thinAxis | longAxis  (axis: 0=X 1=Y 2=Z, -1=none)" );

		foreach ( var data in BuildPieceCatalog.All )
		{
			if ( data is null || string.IsNullOrWhiteSpace( data.Id ) )
				continue;

			var id = data.Id;
			var half = BuildColliderSnap.GetColliderHalfForPiece( id );
			var pitch = BuildModuleDimensions.GetPrefabLocalRotation( id );

			Log.Info( $"[snap-report] {id} | {BuildSnapLayout.GetKind( id )} | thin={BuildModuleDimensions.GetThinAxis( id )} | long={BuildModuleDimensions.GetLongAxis( id )}" );
			Log.Info( $"[snap-report]   mesh size={BuildPieceModelCache.GetSize( id )} center={BuildPieceModelCache.GetCenter( id )}" );
			Log.Info( $"[snap-report]   table size={BuildModuleDimensions.GetColliderScale( id )} | half used={half} | bakedPitch={BuildPieceVisual.UsesBakedMeshRotation( id )} pitch={pitch.Angles()}" );

			var roles = BuildSnapLayout.GetRoles( id );
			for ( var i = 0; i < roles.Count; i++ )
			{
				var role = roles[i];
				var local = BuildColliderSnap.GetCornerSnapLocal( id, role, half );
				var world = BuildColliderSnap.GetCornerSnapWorldOffset( id, role, Rotation.Identity, Vector3.One, half );
				Log.Info( $"[snap-report]   {role} ({BuildSnapLayout.GetHoldLabel( id, role )}) local={local} worldOffset@yaw0={world}" );
			}
		}
	}

	public static void DrawPieceSnapPoints(
		BuildPiece piece,
		Color color,
		BuildSnapCandidate? activeCandidate,
		bool isPreview,
		Action<Vector3, Color, float> drawSphere )
	{
		if ( piece is null || !piece.IsValid() || drawSphere is null )
			return;

		var snaps = piece.SnapPoints;
		for ( var i = 0; i < snaps.Count; i++ )
		{
			var world = piece.GetSnapWorldTransform( snaps[i] );
			var tint = color;

			if ( activeCandidate is { } active
			     && !isPreview
			     && active.TargetPiece == piece )
			{
				if ( active.IsEdgeSnap && BuildSnapEdge.TryGetEdge( active.TargetEdgeId, out var edge )
				     && ( snaps[i].Role == edge.CornerA || snaps[i].Role == edge.CornerB ) )
					tint = active.IsValid ? ActiveColor : InvalidColor;
				else if ( !active.IsEdgeSnap && active.TargetSnapIndex == i )
					tint = active.IsValid ? ActiveColor : InvalidColor;
			}

			drawSphere( world.Position, tint, SphereRadius );
		}
	}

	public static void DrawPlacingSnapPoints(
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Transform placement,
		string pieceId,
		Action<Vector3, Color, float> drawSphere )
	{
		if ( placingSnaps is null || placingSnaps.Count == 0 || drawSphere is null )
			return;

		var orientedRot = placement.Rotation;
		for ( var i = 0; i < placingSnaps.Count; i++ )
		{
			var snap = placingSnaps[i];
			var scale = BuildModuleDimensions.GetPieceLocalScale( pieceId );
			var colliderHalf = BuildColliderSnap.GetColliderHalfForPiece( pieceId );
			var worldPos = placement.Position + BuildColliderSnap.GetCornerSnapWorldOffset(
				pieceId,
				snap.Role,
				orientedRot,
				scale,
				colliderHalf );
			drawSphere( worldPos, PreviewColor, SphereRadius * 0.85f );
		}
	}

	/// <summary>Draw the camera build ray, trace hit, and snap probe point.</summary>
	public static void DrawPlacementRay(
		in BuildPlacementResult placement,
		Action<Vector3, Vector3, Color> drawLine,
		Action<Vector3, Color, float> drawSphere )
	{
		if ( !placement.HasRayDebug || drawLine is null || drawSphere is null )
			return;

		drawLine( placement.RayOrigin, placement.RayEnd, RayColor );
		drawSphere( placement.RayOrigin, Color.White, 2f );

		if ( placement.HasRayHit )
		{
			drawSphere( placement.RayHitPosition, RayHitColor, 3.5f );
			var hitToOrigin = ( placement.RayOrigin - placement.RayHitPosition ).Normal;
			if ( hitToOrigin.LengthSquared > 1e-8f )
			{
				drawLine(
					placement.RayHitPosition,
					placement.RayHitPosition + hitToOrigin * 24f,
					RayHitColor.WithAlpha( 0.65f ) );
			}
		}
		else
		{
			drawSphere( placement.RayEnd, RayColor.WithAlpha( 0.45f ), 3f );
		}

		drawSphere( placement.ProbePosition, ProbeColor, 4.5f );

		if ( !placement.HasRayHit )
			drawSphere( placement.AimDropPosition, AimDropColor, 3f );
	}
}
