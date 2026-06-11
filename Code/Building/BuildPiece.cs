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
			SetCollidersEnabled( false );
		else
			EnsureWalkColliders( pieceId );

		ApplyVisualTint();
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
		if ( !string.Equals( pieceId, "45roof", StringComparison.OrdinalIgnoreCase ) )
			return;

		var root = GameObject;
		if ( !root.IsValid() )
			return;

		var rootBox = root.Components.Get<BoxCollider>();
		if ( rootBox is not null )
			rootBox.IsTrigger = true;

		GameObject ramp = null;
		foreach ( var child in root.Children )
		{
			if ( child.IsValid() && child.Name == "WalkRamp" )
			{
				ramp = child;
				break;
			}
		}

		if ( ramp is null || !ramp.IsValid() )
		{
			ramp = new GameObject( false, "WalkRamp" );
			ramp.Parent = root;
			ramp.LocalPosition = Vector3.Zero;
			ramp.LocalRotation = Rotation.Identity;
			ramp.LocalScale = Vector3.One;
		}

		var walkBox = ramp.Components.Get<BoxCollider>() ?? ramp.Components.Create<BoxCollider>();
		walkBox.Center = Vector3.Zero;
		walkBox.Scale = new Vector3( 50f, 50f, 160f );
		walkBox.Static = true;
		walkBox.IsTrigger = false;
		walkBox.Enabled = true;
	}
}

/// <summary>Optional prefab child marker for snap points.</summary>
[Title( "Build Snap Point Marker" )]
public sealed class BuildSnapPointMarker : Component
{
	[Property] public BuildSnapRole Role { get; set; } = BuildSnapRole.Unknown;
}
