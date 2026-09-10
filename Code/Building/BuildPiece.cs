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

	/// <summary>
	/// Hit points left (host-owned, synced for the hammer hover readout). Seeded from the material's
	/// <see cref="BuildMaterialData.Health"/> when the piece is placed; furniture without a material
	/// never takes damage. Only entities damage structures — see <see cref="HostApplyDamage"/>.
	/// </summary>
	[Sync, Change] public float Health { get; set; }

	/// <summary>Colour a piece drifts toward as it loses hit points (blended by missing fraction).</summary>
	static readonly Color DamageTint = new( 0.32f, 0.1f, 0.08f, 1f );

	public bool IsPreviewGhost { get; private set; }

	/// <summary>
	/// Bumps whenever a placed piece enters or leaves the world on this machine (host and clients
	/// alike — pieces network-spawn everywhere). Shelter caches (<see cref="Workbench.IsSheltered"/>)
	/// re-probe only when this moves.
	/// </summary>
	public static int WorldVersion { get; private set; }

	/// <summary>Has a structural material, so it can be attacked and can collapse.</summary>
	public bool IsDestructible => BuildPieceCatalog.GetMaterialForPiece( PieceId ) is not null;

	/// <summary>Material hit points, or 0 for furniture.</summary>
	public float MaxHealth => BuildPieceCatalog.GetMaterialForPiece( PieceId )?.Health ?? 0f;

	/// <summary>Host: health has been seeded and drained to nothing — the piece is being removed.</summary>
	public bool IsBroken => _hostHealthSeeded && Health <= 0.001f;

	bool _supportTintApplied;
	bool _hostHealthSeeded;

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
		{
			EnsureGrappleSurfaceTag();
			HostSeedHealth();
		}

		ApplyVisualTint();
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !IsPreviewGhost )
			WorldVersion++;
	}

	protected override void OnDisabled()
	{
		if ( !IsPreviewGhost )
			WorldVersion++;
		base.OnDisabled();
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
		HostSeedHealth();
	}

	bool IsHostAuthority =>
		GameObject.IsValid() && !GameObject.IsProxy
		&& (GameObject.Network is not { Active: true } || Networking.IsHost);

	/// <summary>Host: give a fresh placed piece its material hit points (scene-authored pieces get theirs on start).</summary>
	void HostSeedHealth()
	{
		if ( _hostHealthSeeded || !IsHostAuthority )
			return;

		var max = MaxHealth;
		if ( max <= 0f )
			return;

		if ( Health <= 0f || Health > max )
			Health = max;

		_hostHealthSeeded = true;
	}

	/// <summary>
	/// Host: melee damage from an entity swing (routed through <see cref="DamageReceiver"/>). Returns
	/// what was actually taken. At zero the piece is removed through <see cref="BuildAuthority.HostRemovePiece"/>,
	/// so nav rebakes and structural integrity cascades exactly as a hammer demolish would.
	/// </summary>
	public float HostApplyDamage( float amount, Component attacker )
	{
		if ( IsPreviewGhost || amount <= 0f || !IsHostAuthority )
			return 0f;

		HostSeedHealth();
		if ( !_hostHealthSeeded || Health <= 0.001f )
			return 0f;

		var before = Health;
		Health = Math.Max( 0f, Health - amount );
		var dealt = before - Health;
		Log.Info( $"[BuildPiece] {GameObject.Name} -{dealt:0.#} HP → {Health:0.#}/{MaxHealth:0}" );

		if ( Health <= 0.001f )
			BuildAuthority.HostRemovePiece( this );

		return dealt;
	}

	/// <summary>[Change] callback for <see cref="Health"/> — host and clients darken the piece as it wears down.</summary>
	void OnHealthChanged( float oldValue, float newValue ) => ApplyDamageTint();

	/// <summary>Placed-piece resting colour: catalog fallback, pulled toward <see cref="DamageTint"/> by missing health.</summary>
	Color RestingColor()
	{
		var restore = BuildPieceCatalog.TryGet( PieceId, out var data )
			? BuildPieceCatalog.ParseFallbackColor( data.FallbackColor )
			: Color.White;

		var max = MaxHealth;
		if ( max <= 0f || Health <= 0f || Health >= max - 0.5f )
			return restore;

		var missing = 1f - Math.Clamp( Health / max, 0f, 1f );
		return Color.Lerp( restore, DamageTint, missing * 0.85f );
	}

	void ApplyDamageTint()
	{
		if ( IsPreviewGhost || _supportTintApplied || !GameObject.IsValid() )
			return;

		ApplyTint( RestingColor() );
	}

	void EnsureGrappleSurfaceTag()
	{
		if ( !GameObject.IsValid() )
			return;

		GameObject.Tags.Add( PlayerMovement.GrappleSurfaceTag );
		// Shelter probes trace on this tag; prefabs carry it, placeholder spawns get it here.
		GameObject.Tags.Add( ShelterProbe.BuildPieceTag );
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
		ApplyTint( RestingColor() );
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
