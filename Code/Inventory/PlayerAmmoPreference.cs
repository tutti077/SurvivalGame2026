using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Optional preferred ammo stack type (inventory hover + E). Not a paperdoll slot —
/// marks a resource id so ranged weapons consume that ammo type first when available.
/// </summary>
[Title( "Player Ammo Preference" )]
public sealed class PlayerAmmoPreference : Component
{
	/// <summary>Canonical ammo resource id preferred for fire (empty = auto TL→BR).</summary>
	[Sync( SyncFlags.FromHost )]
	public string PreferredAmmoResourceId { get; private set; } = string.Empty;

	public event Action PreferenceChanged;

	PlayerInventory _inventory;
	PlayerHotbar _hotbar;
	string _lastObservedPreferred = string.Empty;

	protected override void OnStart()
	{
		base.OnStart();
		_inventory = Components.Get<PlayerInventory>();
		_hotbar = Components.Get<PlayerHotbar>();

		if ( _inventory is not null )
			_inventory.InventoryChanged += OnStacksChanged;
		if ( _hotbar is not null )
			_hotbar.HotbarChanged += OnStacksChanged;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		// Sync can change PreferredAmmoResourceId without InventoryChanged — refresh UI highlights.
		if ( !string.Equals( PreferredAmmoResourceId ?? string.Empty, _lastObservedPreferred, StringComparison.OrdinalIgnoreCase ) )
		{
			_lastObservedPreferred = PreferredAmmoResourceId ?? string.Empty;
			PreferenceChanged?.Invoke();
		}
	}

	protected override void OnDestroy()
	{
		if ( _inventory is not null )
			_inventory.InventoryChanged -= OnStacksChanged;
		if ( _hotbar is not null )
			_hotbar.HotbarChanged -= OnStacksChanged;
		base.OnDestroy();
	}

	public bool IsPreferredAmmo( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( PreferredAmmoResourceId ) || string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		return ResourceCatalog.ResourceIdsMatch( PreferredAmmoResourceId, resourceId );
	}

	/// <summary>Local owner: mark hovered ammo stack type as preferred (or clear if already preferred).</summary>
	public void OwnerTryEquipAmmoFromSlot( string resourceId )
	{
		if ( !IsLocalManagingClient() )
			return;

		if ( string.IsNullOrWhiteSpace( resourceId ) || !AmmoCatalog.IsAmmo( resourceId ) )
			return;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );

		if ( GameObject.Network is not { Active: true } )
		{
			HostSetPreferredAmmo( resourceId );
			return;
		}

		if ( Networking.IsHost )
			HostSetPreferredAmmo( resourceId );
		else
			RpcHostSetPreferredAmmo( resourceId );
	}

	[Rpc.Host]
	void RpcHostSetPreferredAmmo( string resourceId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		HostSetPreferredAmmo( resourceId );
	}

	void HostSetPreferredAmmo( string resourceId )
	{
		if ( string.IsNullOrWhiteSpace( resourceId ) || !AmmoCatalog.IsAmmo( resourceId ) )
			return;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		if ( CountOwned( resourceId ) <= 0 )
			return;

		PreferredAmmoResourceId = resourceId;
		PreferenceChanged?.Invoke();
	}

	void OnStacksChanged()
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( string.IsNullOrWhiteSpace( PreferredAmmoResourceId ) )
			return;

		if ( CountOwned( PreferredAmmoResourceId ) <= 0 )
		{
			PreferredAmmoResourceId = string.Empty;
			PreferenceChanged?.Invoke();
		}
	}

	int CountOwned( string resourceId )
	{
		_inventory ??= Components.Get<PlayerInventory>();
		_hotbar ??= Components.Get<PlayerHotbar>();
		if ( _inventory is not null )
			return _inventory.CountResource( resourceId );
		return _hotbar?.CountResource( resourceId ) ?? 0;
	}

	bool IsLocalManagingClient()
	{
		if ( GameObject.IsProxy )
			return false;

		if ( GameObject.Network is not { Active: true } net )
			return true;

		return net.Owner is null ? Networking.IsHost : net.IsOwner;
	}
}
