using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Snap marker positions (drawn from a Component via single-frame overlay).</summary>
static class BuildSnapDebug
{
	public static float SphereRadius => BuildModuleDimensions.ThinHalfUnits;

	public static readonly Color DefaultColor = new( 0.2f, 0.85f, 1f, 0.9f );
	public static readonly Color ActiveColor = new( 1f, 0.92f, 0.15f, 1f );
	public static readonly Color InvalidColor = new( 1f, 0.25f, 0.2f, 0.85f );
	public static readonly Color PreviewColor = new( 0.45f, 1f, 0.55f, 0.95f );
	public static readonly Color RayColor = new( 0.25f, 0.95f, 1f, 0.9f );
	public static readonly Color RayHitColor = new( 1f, 0.92f, 0.15f, 1f );
	public static readonly Color ProbeColor = new( 1f, 0.25f, 0.95f, 1f );
	public static readonly Color AimDropColor = new( 1f, 0.55f, 0.12f, 0.95f );

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

		var pitch = BuildModuleDimensions.GetPrefabLocalRotation( pieceId );
		var orientedRot = placement.Rotation * pitch;
		for ( var i = 0; i < placingSnaps.Count; i++ )
		{
			var snap = placingSnaps[i];
			var scale = BuildModuleDimensions.GetPieceLocalScale( pieceId );
			var colliderHalf = BuildColliderSnap.PrefabColliderSize * 0.5f;
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
		drawSphere( placement.RayOrigin, Color.White, 5f );

		if ( placement.HasRayHit )
		{
			drawSphere( placement.RayHitPosition, RayHitColor, 9f );
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
			drawSphere( placement.RayEnd, RayColor.WithAlpha( 0.45f ), 7f );
		}

		drawSphere( placement.ProbePosition, ProbeColor, 11f );

		if ( !placement.HasRayHit )
			drawSphere( placement.AimDropPosition, AimDropColor, 7f );
	}
}
