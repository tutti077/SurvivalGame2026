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

	public bool IsPreviewGhost { get; private set; }

	Vector3 _halfExtents = BuildModuleDimensions.FloorHalfExtents;
	readonly List<BuildSnapPoint> _snapPoints = new();

	public Vector3 HalfExtents => _halfExtents;
	public IReadOnlyList<BuildSnapPoint> SnapPoints => _snapPoints;

	public void Configure( string pieceId, bool blueprint, bool previewGhost )
	{
		PieceId = pieceId ?? string.Empty;
		IsBlueprint = blueprint;
		IsPreviewGhost = previewGhost;

		if ( BuildModuleDimensions.TryGetHalfExtents( PieceId, out var half ) )
			_halfExtents = half;
		else if ( BuildPieceCatalog.TryGet( PieceId, out var data ) )
			_halfExtents = data.PlacementHalfExtents;

		RefreshSnapPoints();

		if ( previewGhost )
		{
			SetCollidersEnabled( false );
			// Preview clones inherit prefab tags — don't steal grapple aim from ghosts.
			GameObject.Tags.Remove( PlayerMovement.GrappleSurfaceTag );
		}
		else
		{
			EnsureWalkColliders( pieceId );
			EnsureGrappleSurfaceTag();
		}

		ApplyVisualTint();
	}

	protected override void OnStart()
	{
		if ( IsPreviewGhost || string.IsNullOrWhiteSpace( PieceId ) )
			return;

		if ( BuildModuleDimensions.TryGetHalfExtents( PieceId, out var half ) )
			_halfExtents = half;

		if ( _snapPoints.Count == 0 )
			RefreshSnapPoints();

		EnsureWalkColliders( PieceId );
		EnsureGrappleSurfaceTag();
	}

	/// <summary>
	/// Placed structures use the same <c>grapple</c> tag as trees so the rope can latch.
	/// Prefabs author it; this covers already-placed pieces and any future build prefab.
	/// </summary>
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
		return new Transform( worldPos, GameObject.WorldRotation * snap.LocalRotation );
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

	void ApplyTint( Color tint )
	{
		foreach ( var renderer in Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( renderer is not null )
				renderer.Tint = tint;
		}
	}

	static void SetCollidersEnabled( GameObject go, bool enabled )
	{
		if ( !go.IsValid() )
			return;

		foreach ( var collider in go.Components.GetAll<Collider>( FindMode.EverythingInSelf ) )
		{
			if ( collider is not null )
				collider.Enabled = enabled;
		}

		foreach ( var child in go.Children )
			SetCollidersEnabled( child, enabled );
	}

	void SetCollidersEnabled( bool enabled ) => SetCollidersEnabled( GameObject, enabled );

	void EnsureWalkColliders( string pieceId )
	{
		if ( string.Equals( pieceId, "foundation", StringComparison.OrdinalIgnoreCase ) )
		{
			RestoreFoundationCollider();
			return;
		}

		if ( !string.Equals( pieceId, "45roof", StringComparison.OrdinalIgnoreCase ) )
			return;

		EnsureRoofWalkSurface();
	}

	void RestoreFoundationCollider()
	{
		var root = GameObject;
		if ( !root.IsValid() )
			return;

		RemoveWalkChild( "WalkDeck" );

		var rootBox = root.Components.Get<BoxCollider>();
		if ( rootBox is not null )
		{
			rootBox.IsTrigger = false;
			rootBox.Static = true;
			rootBox.Enabled = true;
		}
	}

	void EnsureRoofWalkSurface()
	{
		var root = GameObject;
		if ( !root.IsValid() )
			return;

		// Fat WalkRamp (50×50×160) made a tall end-cap at the eave — blocked walking
		// onto ground-placed roofs and physics-pushed the pawn on jump. Use the thin
		// plate collider that matches the pitched roof mesh instead.
		RemoveWalkChild( "WalkRamp" );
		RemoveWalkChild( "WalkDeck" );

		var rootBox = root.Components.Get<BoxCollider>();
		if ( rootBox is null )
			return;

		rootBox.Center = Vector3.Zero;
		rootBox.Scale = BuildColliderSnap.PrefabColliderSize;
		rootBox.Static = true;
		rootBox.IsTrigger = false;
		rootBox.Enabled = true;
	}

	void RemoveWalkChild( string childName )
	{
		foreach ( var child in GameObject.Children )
		{
			if ( child.IsValid() && child.Name == childName )
				child.Destroy();
		}
	}
}

/// <summary>Optional prefab child marker for snap points.</summary>
[Title( "Build Snap Point Marker" )]
public sealed class BuildSnapPointMarker : Component
{
	[Property] public BuildSnapRole Role { get; set; } = BuildSnapRole.Unknown;
}
