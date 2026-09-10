using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Farming interaction for the owning pawn.
/// <list type="bullet">
/// <item><b>Sow</b> — with a seed selected on the hotbar, a translucent copy of the matured plant follows
/// the aim point: plant-coloured where a seed can go (inside tilled soil and clear of other plants), red
/// anywhere else. E or Attack1 sows one seed exactly at the aim point.</item>
/// <item><b>Harvest</b> — aiming at a matured plant prompts "Harvest &lt;plant&gt;"; a bolted plant prompts for
/// its seeds. E harvests. Single plants disappear, bushes regrow.</item>
/// <item><b>Uproot</b> — the hoe aimed at any plant prompts Attack1 to hoe it out for one seed.</item>
/// </list>
/// Every request goes to the host through this component (<see cref="FarmingAuthority"/>) — RPCs must
/// live on the networked pawn, not on the locally cloned tool prefab.
/// </summary>
[Title( "Player Farming" )]
public sealed class PlayerFarming : Component
{
	[Property, Group( "Input" ), Title( "Sow / Harvest Action" )]
	public string SowAction { get; set; } = "Use";

	[Property, Group( "Input" ), Title( "Sow Click Action" )]
	public string SowClickAction { get; set; } = "Attack1";

	[Property, Group( "Sowing" ), Title( "Reach (m)" ), Range( 1f, 10f ), Step( 0.5f )]
	public float SowReachMeters { get; set; } = 4f;

	[Property, Group( "Sowing" ), Title( "Focus Scan Interval (seconds)" )]
	public float FocusScanIntervalSeconds { get; set; } = 0.1f;

	[Property, Group( "Debug" )] public bool LogFarming { get; set; }

	/// <summary>HUD prompt line ("Sow Radish", "Harvest Radish", "Equip seeds to sow"); empty = no prompt.</summary>
	public string PromptText { get; private set; } = string.Empty;

	/// <summary>Key cap shown next to <see cref="PromptText"/> ("E", or "LMB" for the hoe's uproot).</summary>
	public string PromptKey { get; private set; } = "E";

	/// <summary>True while E belongs to farming (valid sow ghost or a harvestable plant under the aim) — hand harvest yields.</summary>
	public bool OwnsUseKey => _ghostValid || (FocusedPlant is not null && FocusedPlant.IsValid() && FocusedPlant.CanHarvest);

	/// <summary>Plant under the aim point this frame (any stage), or null.</summary>
	public FarmPlant FocusedPlant { get; private set; }

	public event Action PromptChanged;

	PlayerVitals _vitals;
	PlayerHotbar _hotbar;
	PlayerInventory _inventory;
	PlayerGameMenuController _menu;
	PlayerEquipment _equipment;

	GameObject _ghost;
	string _ghostSeedId;
	bool _ghostValid;
	Vector3 _aimPoint;
	string _activeSeedId = string.Empty;
	double _nextHintScanAt;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_hotbar = Components.Get<PlayerHotbar>();
		_inventory = Components.Get<PlayerInventory>();
		_menu = Components.Get<PlayerGameMenuController>();
		_equipment = Components.Get<PlayerEquipment>();
	}

	protected override void OnDestroy()
	{
		DestroyGhost();
		base.OnDestroy();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( _vitals is null || !_vitals.IsLocalInputOwnedPawn() )
		{
			ClearPreview();
			return;
		}

		if ( _menu is { IsMenuOpen: true } || _vitals.CurrentHealth <= 0f )
		{
			ClearPreview();
			return;
		}

		// The hoe owns the aim while it is out: tilling square, or uprooting the plant under the aim.
		var hoe = _equipment?.GetActiveTool<ToolHoe>();
		if ( hoe is not null )
		{
			HideGhost();
			_ghostValid = false;
			FocusedPlant = hoe.FocusedPlant;
			if ( FocusedPlant is not null && FocusedPlant.IsValid() )
				SetPrompt( $"Uproot {FocusedPlant.PlantName} (+{FarmingRules.UprootSeedAmount} seed)", "LMB" );
			else
				SetPrompt( string.Empty );
			return;
		}

		_activeSeedId = ResolveActiveSeedId();
		if ( string.IsNullOrWhiteSpace( _activeSeedId ) )
		{
			HideGhost();
			TickFocusWithoutSeed();
		}
		else
		{
			TickSowPreview();
		}

		var usePressed = Input.Pressed( SowAction );
		var clickPressed = Input.Pressed( SowClickAction );

		// A ready plant under the aim wins E — even with a seed selected the ghost there is red anyway.
		if ( usePressed && FocusedPlant is not null && FocusedPlant.IsValid() && FocusedPlant.CanHarvest )
		{
			OwnerRequestHarvest( FocusedPlant );
			return;
		}

		if ( _ghostValid && (usePressed || clickPressed) )
			OwnerRequestSow( _activeSeedId, _aimPoint );
	}

	/// <summary>Seed resource in the active hotbar slot, or empty.</summary>
	string ResolveActiveSeedId()
	{
		if ( _hotbar is null )
			return string.Empty;

		var slot = _hotbar.GetSlot( _hotbar.ActiveSlotIndex );
		if ( slot.IsEmpty )
			return string.Empty;

		var id = ResourceCatalog.NormalizeResourceId( slot.ResourceId );
		return ResourceDefinitionCatalog.IsSeed( id ) ? id : string.Empty;
	}

	bool TryTraceAim( out SceneTraceResult tr, out Vector3 origin, out Vector3 direction, out float reach )
	{
		tr = default;
		reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, SowReachMeters ) );
		if ( !BuildViewCamera.TryGetViewRay( GameObject, out origin, out direction ) )
			return false;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		tr = FarmingRules.TraceView( scene, GameObject, origin, direction, reach );
		return true;
	}

	void TickSowPreview()
	{
		if ( !TryTraceAim( out var tr, out var origin, out var direction, out var reach ) )
		{
			HideGhost();
			FocusedPlant = null;
			return;
		}

		_aimPoint = tr.Hit ? tr.HitPosition : origin + direction * reach;

		FarmPlant hitPlant = null;
		var valid = false;
		if ( tr.Hit && FarmPlant.TryFindOnHierarchy( tr.GameObject, out hitPlant ) )
		{
			// Aiming at a plant: never a sow spot (spacing), but harvest may apply.
		}
		else if ( tr.Hit && TilledSoil.TryFindOnHierarchy( tr.GameObject, out var tile ) )
		{
			_aimPoint = new Vector3( _aimPoint.x, _aimPoint.y, tile.SurfaceZ );
			valid = FarmingRules.CanSowAt( _aimPoint, out _, out _ );
		}

		FocusedPlant = hitPlant;

		ResourceDefinitionCatalog.TryGetSeed( _activeSeedId, out var data );
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		EnsureGhost( scene, data );
		_ghost.Enabled = true;
		_ghost.WorldPosition = _aimPoint;
		_ghostValid = valid;

		var tint = valid
			? FarmingRules.ParseColor( data?.Color, new Color( 0.35f, 0.65f, 0.25f ) ).WithAlpha( 0.45f )
			: FarmingRules.InvalidGhostTint;
		foreach ( var renderer in _ghost.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
			renderer.Tint = tint;

		if ( hitPlant is not null && hitPlant.CanHarvest )
			SetPrompt( BuildHarvestPrompt( hitPlant ) );
		else if ( valid )
			SetPrompt( $"Sow {ResolvePlantName( _activeSeedId, data )}" );
		else
			SetPrompt( string.Empty );
	}

	void EnsureGhost( Scene scene, FarmPlantData data )
	{
		if ( _ghost is { IsValid: true } && string.Equals( _ghostSeedId, _activeSeedId, StringComparison.OrdinalIgnoreCase ) )
			return;

		DestroyGhost();
		_ghostSeedId = _activeSeedId;
		_ghost = new GameObject( true, "sow_ghost" );
		_ghost.Parent = scene;
		_ghost.Tags.Add( "farmpreview" );
		FarmPlant.BuildStageVisual( _ghost, data, FarmPlantStage.Matured, tintOverride: FarmingRules.InvalidGhostTint, previewGhost: true );
	}

	/// <summary>
	/// No seed selected: a harvestable plant under the aim prompts to pick it; tilled soil with seeds
	/// in the bag hints to equip them. Scanned on an interval — nothing here is per frame.
	/// </summary>
	void TickFocusWithoutSeed()
	{
		if ( Time.NowDouble < _nextHintScanAt )
			return;

		_nextHintScanAt = Time.NowDouble + Math.Max( 0.05, FocusScanIntervalSeconds );

		if ( !TryTraceAim( out var tr, out _, out _, out _ ) || !tr.Hit )
		{
			FocusedPlant = null;
			SetPrompt( string.Empty );
			return;
		}

		if ( FarmPlant.TryFindOnHierarchy( tr.GameObject, out var plant ) )
		{
			FocusedPlant = plant;
			SetPrompt( plant.CanHarvest ? BuildHarvestPrompt( plant ) : string.Empty );
			return;
		}

		FocusedPlant = null;
		if ( !TilledSoil.TryFindOnHierarchy( tr.GameObject, out _ ) )
		{
			SetPrompt( string.Empty );
			return;
		}

		SetPrompt( HasAnySeeds() ? "Equip seeds to sow" : string.Empty );
	}

	static string BuildHarvestPrompt( FarmPlant plant )
	{
		var data = plant.Data;
		if ( plant.IsBolting )
			return $"Collect {plant.PlantName} Seeds";

		if ( FarmingAuthority.TryGetHarvestYield( plant, data, out var resourceId, out var low, out var high ) )
		{
			var itemName = ResourceCatalog.Resolve( resourceId ).DisplayName;
			var range = low == high ? $"{low}" : $"{low}-{high}";
			return $"Harvest {plant.PlantName} ({range} {itemName})";
		}

		return $"Harvest {plant.PlantName}";
	}

	bool HasAnySeeds()
	{
		foreach ( var row in ResourceDefinitionCatalog.All )
		{
			if ( row?.Seed is null )
				continue;

			if ( _inventory is not null && _inventory.CountResource( row.Id ) > 0 )
				return true;

			if ( _hotbar is not null && _hotbar.CountResource( row.Id ) > 0 )
				return true;
		}

		return false;
	}

	static string ResolvePlantName( string seedId, FarmPlantData data )
	{
		if ( data is not null && !string.IsNullOrWhiteSpace( data.PlantName ) )
			return data.PlantName;

		var seedName = ResourceCatalog.Resolve( seedId ).DisplayName;
		return string.IsNullOrWhiteSpace( seedName ) ? "Seed" : seedName;
	}

	void SetPrompt( string text, string key = "E" )
	{
		text ??= string.Empty;
		key = string.IsNullOrWhiteSpace( key ) ? "E" : key;
		if ( string.Equals( PromptText, text, StringComparison.Ordinal ) && string.Equals( PromptKey, key, StringComparison.Ordinal ) )
			return;

		PromptText = text;
		PromptKey = key;
		PromptChanged?.Invoke();
	}

	void HideGhost()
	{
		_ghostValid = false;
		if ( _ghost is { IsValid: true } )
			_ghost.Enabled = false;
	}

	void ClearPreview()
	{
		HideGhost();
		FocusedPlant = null;
		SetPrompt( string.Empty );
	}

	void DestroyGhost()
	{
		if ( _ghost is { IsValid: true } )
			_ghost.Destroy();
		_ghost = null;
		_ghostSeedId = null;
		_ghostValid = false;
	}

	// ---- intent → host ------------------------------------------------------------------

	/// <summary>Owner: till the grid cell at <paramref name="tileCenter"/> (called by <see cref="ToolHoe"/>).</summary>
	public void OwnerRequestTill( Vector3 tileCenter, float reachMeters )
	{
		if ( !GameObject.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			if ( !FarmingAuthority.TryTill( GameObject, tileCenter, reachMeters, out var reason ) && LogFarming )
				Log.Info( $"[PlayerFarming] Till rejected: {reason}" );
			return;
		}

		RpcHostTill( tileCenter, reachMeters );
	}

	/// <summary>Owner: sow one <paramref name="seedId"/> at <paramref name="point"/>.</summary>
	public void OwnerRequestSow( string seedId, Vector3 point )
	{
		if ( !GameObject.IsValid() || string.IsNullOrWhiteSpace( seedId ) )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			if ( !FarmingAuthority.TrySow( GameObject, seedId, point, SowReachMeters, out var reason ) && LogFarming )
				Log.Info( $"[PlayerFarming] Sow rejected: {reason}" );
			return;
		}

		RpcHostSow( seedId, point );
	}

	/// <summary>Owner: pick a matured / bolted plant.</summary>
	public void OwnerRequestHarvest( FarmPlant plant )
	{
		if ( !GameObject.IsValid() || plant is null || !plant.IsValid() || !plant.GameObject.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			if ( !FarmingAuthority.TryHarvest( GameObject, plant, SowReachMeters, out var reason ) && LogFarming )
				Log.Info( $"[PlayerFarming] Harvest rejected: {reason}" );
			return;
		}

		RpcHostHarvest( plant.GameObject.Id );
	}

	/// <summary>Owner: hoe a plant out for its seed (called by <see cref="ToolHoe"/>).</summary>
	public void OwnerRequestUproot( FarmPlant plant, float reachMeters )
	{
		if ( !GameObject.IsValid() || plant is null || !plant.IsValid() || !plant.GameObject.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			if ( !FarmingAuthority.TryUproot( GameObject, plant, reachMeters, out var reason ) && LogFarming )
				Log.Info( $"[PlayerFarming] Uproot rejected: {reason}" );
			return;
		}

		RpcHostUproot( plant.GameObject.Id, reachMeters );
	}

	bool IsCallerTheOwner()
	{
		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return false;

		return true;
	}

	[Rpc.Host]
	void RpcHostTill( Vector3 tileCenter, float reachMeters )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerTheOwner() )
			return;

		// Reach is host-owned — clamp what the client claims.
		var reach = Math.Clamp( reachMeters, 0.5f, 10f );
		if ( !FarmingAuthority.TryTill( GameObject, tileCenter, reach, out var reason ) && LogFarming )
			Log.Info( $"[PlayerFarming] Host till rejected: {reason}" );
	}

	[Rpc.Host]
	void RpcHostSow( string seedId, Vector3 point )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerTheOwner() )
			return;

		if ( !FarmingAuthority.TrySow( GameObject, seedId, point, SowReachMeters, out var reason ) && LogFarming )
			Log.Info( $"[PlayerFarming] Host sow rejected: {reason}" );
	}

	[Rpc.Host]
	void RpcHostHarvest( Guid plantId )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerTheOwner() )
			return;

		if ( !FarmPlant.TryResolve( plantId, out var plant ) )
			return;

		if ( !FarmingAuthority.TryHarvest( GameObject, plant, SowReachMeters, out var reason ) && LogFarming )
			Log.Info( $"[PlayerFarming] Host harvest rejected: {reason}" );
	}

	[Rpc.Host]
	void RpcHostUproot( Guid plantId, float reachMeters )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerTheOwner() )
			return;

		if ( !FarmPlant.TryResolve( plantId, out var plant ) )
			return;

		var reach = Math.Clamp( reachMeters, 0.5f, 10f );
		if ( !FarmingAuthority.TryUproot( GameObject, plant, reach, out var reason ) && LogFarming )
			Log.Info( $"[PlayerFarming] Host uproot rejected: {reason}" );
	}
}
