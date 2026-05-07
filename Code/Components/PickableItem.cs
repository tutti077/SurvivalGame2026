using System;
using System.Collections.Generic;
using Sandbox;

namespace Game;

/// <summary>
/// Attach to a prop with a <see cref="Rigidbody"/> (and collider). <see cref="PlayerItemPickup"/> on the player runs traces and grab/drop.
/// Per-weapon grip offsets live on <see cref="MeleeWeapon"/> on this object or children.
/// </summary>
public sealed class PickableItem : Component
{
	[Property] public string DisplayName { get; set; } = "Item";

	/// <summary>Optional tag so traces can filter pickup layers.</summary>
	[Property] public string PickupTag { get; set; } = "pickup";

	/// <summary>Weapon classification for UI / inventory (non-weapons stay <see cref="WeaponType.None"/>).</summary>
	[Property] public WeaponType ItemWeaponType { get; set; } = WeaponType.None;

	/// <summary>If set, <see cref="PlayerItemPickup"/> adds this many items to <see cref="PlayerInventory"/> instead of kinematic carry.</summary>
	[Property] public string InventoryItemId { get; set; } = "";

	/// <summary>How many units this world pickup grants when collected into inventory.</summary>
	[Property] public int WorldPickupCount { get; set; } = 1;

	/// <summary>
	/// If true, finds a <see cref="Rigidbody"/> on this object or children and disables motion and gravity until
	/// <see cref="PlayerItemPickup"/> picks it up (then drop restores the saved motion state).
	/// </summary>
	[Property] public bool StaticUntilPickedUp { get; set; } = true;

	protected override void OnEnabled()
	{
		BindPickupTriggers( GameObject, subscribe: true );

		if ( !StaticUntilPickedUp )
			return;

		var rb = FindRigidbodyInHierarchy( GameObject );
		if ( rb is null || !rb.IsValid() )
			return;

		rb.Gravity = false;
		rb.MotionEnabled = false;
		rb.Velocity = Vector3.Zero;
		rb.AngularVelocity = Vector3.Zero;
	}

	protected override void OnDisabled()
	{
		BindPickupTriggers( GameObject, subscribe: false );
	}

	/// <summary>Called from inventory world-drops so the item starts as a normal rigidbody and collides immediately.</summary>
	public void BeginInventoryDropSleepUntilCollision()
	{
		// Legacy name kept to avoid prefab/script breakage; ensures dropped props simulate immediately.
		foreach ( var rb in EnumerateRigidbodiesInHierarchy( GameObject ) )
		{
			if ( rb is null || !rb.IsValid() )
				continue;

			rb.CollisionEventsEnabled = true;
			rb.Gravity = true;
			rb.MotionEnabled = true;
		}
	}

	/// <summary>
	/// Whitelist blocks resolving <see cref="Collision.Other"/> to a <see cref="GameObject"/> (no reflection/dynamic).
	/// Automatic pickup uses trigger <see cref="Collider"/>s only: set <c>IsTrigger</c> on a pickup volume (often a child),
	/// or keep use-key / trace pickup via <see cref="PlayerItemPickup"/>.
	/// </summary>
	private void BindPickupTriggers( GameObject go, bool subscribe )
	{
		if ( go is null || !go.IsValid() )
			return;

		foreach ( var col in go.Components.GetAll<Collider>() )
		{
			if ( col is null || !col.IsValid() || !col.IsTrigger )
				continue;

			if ( subscribe )
				col.OnTriggerEnter += OnPickupTriggerEnter;
			else
				col.OnTriggerEnter -= OnPickupTriggerEnter;
		}

		foreach ( var child in go.Children )
			BindPickupTriggers( child, subscribe );
	}

	private void OnPickupTriggerEnter( Collider other )
	{
		if ( other is null || !other.IsValid() )
			return;

		TryPickupFromOtherHierarchy( other.GameObject );
	}

	private void TryPickupFromOtherHierarchy( GameObject otherRoot )
	{
		if ( !PlayerInventory.CanAuthoritativePickup() )
			return;

		if ( string.IsNullOrWhiteSpace( InventoryItemId ) || WorldPickupCount <= 0 )
			return;

		if ( otherRoot is null || !otherRoot.IsValid() )
			return;

		var inv = FindInventoryInHierarchy( otherRoot );
		if ( inv is null || !inv.IsValid() )
			return;

		if ( !inv.HostTryAddFromWorld( InventoryItemId, Math.Max( 1, WorldPickupCount ) ) )
			return;

		GameObject.Destroy();
	}

	private static Rigidbody FindRigidbodyInHierarchy( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			return null;

		var rb = root.Components.Get<Rigidbody>();
		if ( rb is not null )
			return rb;

		foreach ( var child in root.Children )
		{
			var found = FindRigidbodyInHierarchy( child );
			if ( found is not null )
				return found;
		}

		return null;
	}

	private static IEnumerable<Rigidbody> EnumerateRigidbodiesInHierarchy( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			yield break;

		var self = root.Components.Get<Rigidbody>();
		if ( self is not null )
			yield return self;

		foreach ( var child in root.Children )
		{
			foreach ( var rb in EnumerateRigidbodiesInHierarchy( child ) )
				yield return rb;
		}
	}

	private static PlayerInventory FindInventoryInHierarchy( GameObject start )
	{
		for ( var go = start; go is not null; go = go.Parent )
		{
			var inv = go.Components.Get<PlayerInventory>();
			if ( inv is not null )
				return inv;
		}

		return null;
	}
}
