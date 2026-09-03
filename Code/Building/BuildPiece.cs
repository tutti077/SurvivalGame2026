using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Placed or preview build piece instance.</summary>
[Title( "Build Piece" )]
public sealed class BuildPiece : Component
{
	public static readonly Color BlueprintTint = new( 0.35f, 0.55f, 0.95f, 0.55f );
	public static readonly Color ValidPreviewTint = new( 0.45f, 0.92f, 0.5f, 0.55f );
	public static readonly Color InvalidPreviewTint = new( 0.92f, 0.18f, 0.14f, 0.55f );

	[Property] public string PieceId { get; set; } = string.Empty;
	[Property] public bool IsBlueprint { get; set; }

	/// <summary>Structural support (host-solved, see <see cref="BuildStructuralIntegrity"/>) — synced for client hover display.</summary>
	[Sync] public float Support { get; set; }

	public bool IsPreviewGhost { get; private set; }

	bool _supportTintApplied;

	Vector3 _halfExtents = BuildModuleDimensions.FloorHalfExtents;
	readonly List<BuildSnapPoint> _snapPoints = new();

	public Vector3 HalfExtents => _halfExtents;
	public IReadOnlyList<BuildSnapPoint> SnapPoints => _snapPoints;

	public void Configure( string pieceId, bool blueprint, bool previewGhost )
	{
		PieceId = pieceId ?? string.Empty;
		IsBlueprint = blueprint;
		IsPreviewGhost = previewGhost;

		_halfExtents = BuildColliderSnap.GetColliderHalfForPiece( PieceId );

		RefreshSnapPoints();

		BuildPieceCollider.Ensure( GameObject, PieceId, previewGhost );

		if ( previewGhost )
			GameObject.Tags.Remove( PlayerMovement.GrappleSurfaceTag );
		else
			EnsureGrappleSurfaceTag();

		ApplyVisualTint();
	}

	protected override void OnStart()
	{
		if ( IsPreviewGhost || string.IsNullOrWhiteSpace( PieceId ) )
			return;

		_halfExtents = BuildColliderSnap.GetColliderHalfForPiece( PieceId );

		if ( _snapPoints.Count == 0 )
			RefreshSnapPoints();

		BuildPieceCollider.Ensure( GameObject, PieceId, previewGhost: false );
		EnsureGrappleSurfaceTag();
	}

	void EnsureGrappleSurfaceTag()
	{
		if ( !GameObject.IsValid() )
			return;

		GameObject.Tags.Add( PlayerMovement.GrappleSurfaceTag );
	}

	public void RefreshSnapPoints()
	{
		_snapPoints.Clear();
		if ( string.IsNullOrWhiteSpace( PieceId ) )
			return;

		if ( !BuildPieceCatalog.TryGet( PieceId, out var data ) )
			return;

		BuildSnapDefaults.EnsureDefaults( data );
		for ( var i = 0; i < data.SnapPoints.Count; i++ )
		{
			var snap = BuildSnapParse.FromData( data.SnapPoints[i] );
			if ( snap.Role == BuildSnapRole.Unknown )
				continue;

			_snapPoints.Add( snap );
		}
	}

	public Transform GetSnapWorldTransform( BuildSnapPoint snap )
	{
		var worldPos = BuildColliderSnap.GetCornerSnapWorld( GameObject, PieceId, snap.Role );
		var worldRot = BuildColliderSnap.GetSnapWorldRotation( GameObject, PieceId );
		return new Transform( worldPos, worldRot * snap.LocalRotation );
	}

	public void ApplyVisualTint()
	{
		if ( !IsPreviewGhost )
			return;

		ApplyTint( IsBlueprint ? BlueprintTint : ValidPreviewTint );
	}

	public void SetPreviewValid( bool valid )
	{
		if ( !IsPreviewGhost )
			return;

		if ( !valid )
		{
			ApplyTint( InvalidPreviewTint );
			return;
		}

		ApplyVisualTint();
	}

	/// <summary>Hammer hover: color a placed piece by its support gradient.</summary>
	public void ApplySupportTint( Color tint )
	{
		if ( IsPreviewGhost )
			return;

		_supportTintApplied = true;
		ApplyTint( tint );
	}

	/// <summary>
	/// Restore a placed piece's renderers after hover ends — back to the catalog fallback color,
	/// which is what <see cref="BuildPieceVisual"/> tints placed pieces with.
	/// </summary>
	public void ClearSupportTint()
	{
		if ( !_supportTintApplied )
			return;

		_supportTintApplied = false;
		var restore = BuildPieceCatalog.TryGet( PieceId, out var data )
			? BuildPieceCatalog.ParseFallbackColor( data.FallbackColor )
			: Color.White;
		ApplyTint( restore );
	}

	void ApplyTint( Color tint )
	{
		foreach ( var renderer in Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( renderer is not null )
				renderer.Tint = tint;
		}
	}

	/// <summary>Preview ghosts never participate in physics — snaps are math-only until placed.</summary>
	public static void DisablePreviewPhysics( GameObject root ) =>
		BuildPieceCollider.Ensure( root, root.Components.Get<BuildPiece>()?.PieceId ?? string.Empty, previewGhost: true );
}
