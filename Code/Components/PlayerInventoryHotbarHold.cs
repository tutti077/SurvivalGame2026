using System;
using Sandbox;

namespace Game;

/// <summary>
/// Local-only: shows the selected hotbar item&apos;s world prefab in the same carry pose as <see cref="PlayerItemPickup"/>.
/// Hidden while the player is carrying a real world object. Strip <see cref="PickableItem.InventoryItemId"/> on the clone so it never collides into inventory.
/// </summary>
public sealed class PlayerInventoryHotbarHold : Component
{
	[Property] public PlayerInventory Inventory { get; set; }

	[Property] public PlayerItemPickup ItemPickup { get; set; }

	private GameObject _proxy;
	private string _lastKey = "";

	protected override void OnEnabled()
	{
		if ( Inventory is not null )
			Inventory.OnInventoryChanged += OnInventoryChanged;
	}

	protected override void OnDisabled()
	{
		if ( Inventory is not null )
			Inventory.OnInventoryChanged -= OnInventoryChanged;

		DestroyProxy();
	}

	private void OnInventoryChanged()
	{
		_lastKey = "";
	}

	private static bool IsLocalOwner( GameObject go )
	{
		var n = go.Network;
		if ( n is null || !n.Active )
			return true;

		return n.IsOwner;
	}

	protected override void OnUpdate()
	{
		if ( !IsLocalOwner( GameObject ) || Inventory is null || ItemPickup is null )
			return;

		if ( ItemPickup.HeldRoot is not null )
		{
			if ( _proxy is not null )
				DestroyProxy();

			return;
		}

		var hot = Math.Clamp( Inventory.HotbarSelectedIndex, 0, Math.Max( 0, Inventory.HotbarSlotCount - 1 ) );
		var slot = Inventory.GetSlot( hot );
		var key = $"{Inventory.SlotBlob}|{hot}";
		if ( string.Equals( _lastKey, key, StringComparison.Ordinal ) && _proxy is not null && _proxy.IsValid() )
		{
			ItemPickup.TrySnapCarriedPreview( _proxy );
			return;
		}

		_lastKey = key;
		DestroyProxy();

		if ( slot.IsEmpty || string.IsNullOrWhiteSpace( slot.ItemId ) )
			return;

		if ( !Inventory.TryGetDefinition( slot.ItemId, out var def ) )
			return;

		GameObject inst = null;
		var editorPrefab = ItemCatalog.ResolveEditorDropPrefab( def );
		if ( editorPrefab is not null && editorPrefab.IsValid() )
			inst = editorPrefab.Clone();
		else if ( ItemCatalog.TryLoadPrefabFile( def.WorldDropPrefabPath ) is { } pf )
			inst = GameObject.Clone( pf );

		if ( inst is null || !inst.IsValid() )
			return;

		inst.Parent = Inventory.GameObject;

		StripPickableForPreview( inst );
		FreezeProxyPhysics( inst );
		_proxy = inst;
		ItemPickup.TrySnapCarriedPreview( _proxy );
	}

	private static void StripPickableForPreview( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			return;

		var p = root.Components.Get<PickableItem>();
		if ( p is not null && p.IsValid() )
		{
			p.InventoryItemId = "";
			p.WorldPickupCount = 0;
		}

		foreach ( var child in root.Children )
			StripPickableForPreview( child );
	}

	private static void FreezeProxyPhysics( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			return;

		var rb = root.Components.Get<Rigidbody>();
		if ( rb is not null && rb.IsValid() )
		{
			rb.Gravity = false;
			rb.MotionEnabled = false;
			rb.Velocity = Vector3.Zero;
			rb.AngularVelocity = Vector3.Zero;
		}

		foreach ( var child in root.Children )
			FreezeProxyPhysics( child );
	}

	private void DestroyProxy()
	{
		if ( _proxy is not null && _proxy.IsValid() )
			_proxy.Destroy();

		_proxy = null;
	}
}
