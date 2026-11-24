using System;
using Sandbox;

namespace Survival;

/// <summary>
/// World pickup: an inventory drop, harvest / chop / mining loot, animal loot, a stuck arrow.
/// Nobody can vacuum it until the host flags <see cref="PickupReady"/>: 1 s after it first touches the ground,
/// 5 s for a stack dropped out of an inventory (Mark 2026-11-16).
/// It has a collider and a body only while it falls: once it lies still (<see cref="Settled"/>) both are switched
/// off on every machine, so a loose item never blocks or bumps anything (Mark 2026-11-23). Pickup is the registry
/// magnet (<see cref="PlayerHandHarvest"/>), never a trace.
/// </summary>
[Title( "World Dropped Resource" )]
public sealed class WorldDroppedResource : Component, Component.ICollisionListener
{
	const float LandedPickupDelaySeconds = 1f;
	const float InventoryDropPickupDelaySeconds = 5f;
	const float MergeReadyDelaySeconds = 5f;

	[Sync, Property, Group( "Pickup" )]
	public string ResourceId { get; set; } = string.Empty;

	[Sync, Property, Group( "Pickup" )]
	public int Count { get; set; }

	/// <summary>Durability wear carried by a dropped tool (0 = fresh / not durable).</summary>
	[Sync, Property, Group( "Pickup" )]
	public int Wear { get; set; }

	/// <summary>Crafter display name carried by dropped equipment (empty = untracked).</summary>
	[Sync, Property, Group( "Pickup" )]
	public string CrafterName { get; set; } = string.Empty;

	/// <summary>Upgrade tier carried by dropped equipment (0 / 1 = as crafted).</summary>
	[Sync, Property, Group( "Pickup" )]
	public int Tier { get; set; }

	/// <summary>Stuck arrows / pinned drops — still magnet-pickupable, but never clump-merged.</summary>
	[Sync, Property, Group( "Pickup" )]
	public bool PreventMerge { get; set; }

	/// <summary>Host destroys this drop after this many seconds (0 = keep forever).</summary>
	[Property, Group( "Pickup" ), Title( "Despawn after (s)" )]
	public float DespawnAfterSeconds { get; set; }

	/// <summary>Host sets this once the drop has lain on the ground for its pickup delay; every pawn's magnet waits for it.</summary>
	[Sync]
	public bool PickupReady { get; set; }

	/// <summary>Host sets this when the drop has come to rest (or the magnet takes it): its colliders and body go off everywhere.</summary>
	[Sync, Change( nameof( OnSettledChanged ) )]
	public bool Settled { get; set; }

	public bool IsAvailable => Count > 0 && Active && GameObject.IsValid() && GameObject.Enabled;

	double _despawnAt = -1;

	internal bool IsHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	internal bool IsMergeAuthority => IsHostAuthority;

	/// <summary>True once this drop has sat long enough to clump with neighbors.</summary>
	internal bool IsReadyToMerge =>
		IsAvailable
		&& !PreventMerge
		&& _spawnedAt >= 0
		&& Time.NowDouble - _spawnedAt >= MergeReadyDelaySeconds;

	double _spawnedAt = -1;
	double _mergeEligibleAt = -1;
	double _landedAt = -1;
	bool _inventoryDrop;

	Rigidbody _body;
	double _settledSince = -1;
	bool _magnetBaseScaleCached;
	Vector3 _magnetBaseScale = Vector3.One;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		WorldDroppedResourceRegistry.Register( this );
	}

	protected override void OnDisabled()
	{
		WorldDroppedResourceRegistry.Unregister( this );
		base.OnDisabled();
	}

	public void Configure( string resourceId, int count, int wear = 0, string crafterName = null, int tier = 0 )
	{
		ResourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		Count = Math.Max( 1, count );
		Wear = Math.Max( 0, wear );
		CrafterName = crafterName ?? string.Empty;
		Tier = Math.Max( 0, tier );
		MarkSpawnedIfNeeded();
		RefreshDespawnDeadline();
	}

	/// <summary>Host: auto-delete after <paramref name="seconds"/> (stuck arrows, etc.).</summary>
	public void SetDespawnAfterSeconds( float seconds )
	{
		DespawnAfterSeconds = Math.Max( 0f, seconds );
		RefreshDespawnDeadline();
	}

	void RefreshDespawnDeadline()
	{
		if ( DespawnAfterSeconds <= 0f )
		{
			_despawnAt = -1;
			return;
		}

		_despawnAt = Time.NowDouble + DespawnAfterSeconds;
	}

	/// <summary>
	/// Host: a unit dropped out of an inventory waits <see cref="InventoryDropPickupDelaySeconds"/> on the ground, not 1 s,
	/// and never clumps with its neighbours (a dumped stack stays a visible spread of singles).
	/// </summary>
	public void MarkInventoryDrop()
	{
		_inventoryDrop = true;
		PreventMerge = true;
	}

	public void OnCollisionStart( Collision collision ) => MarkLanded();

	void MarkLanded()
	{
		if ( _landedAt < 0 && IsHostAuthority )
			_landedAt = Time.NowDouble;
	}

	void TickPickupReady()
	{
		// No moving body (stuck arrow, frozen pile) = already lying where it will be picked up.
		if ( _landedAt < 0 && (_body is null || !_body.IsValid() || !_body.MotionEnabled) )
			MarkLanded();

		if ( PickupReady || _landedAt < 0 )
			return;

		var delay = _inventoryDrop ? InventoryDropPickupDelaySeconds : LandedPickupDelaySeconds;
		if ( Time.NowDouble - _landedAt >= delay )
			PickupReady = true;
	}

	public int StackCount => Math.Max( 0, Count );

	protected override void OnStart()
	{
		base.OnStart();
		_body = Components.Get<Rigidbody>();
		MarkSpawnedIfNeeded();
		if ( Settled )
			DisablePhysics();   // joined after it landed
	}

	void MarkSpawnedIfNeeded()
	{
		if ( _spawnedAt >= 0 )
			return;

		_spawnedAt = Time.NowDouble;
		_mergeEligibleAt = _spawnedAt + MergeReadyDelaySeconds;
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();

		if ( !IsHostAuthority || Count <= 0 )
			return;

		if ( _despawnAt > 0 && Time.NowDouble >= _despawnAt )
		{
			Count = 0;
			if ( GameObject.IsValid() )
				GameObject.Destroy();
			return;
		}

		if ( _mergeEligibleAt > 0 && Time.NowDouble >= _mergeEligibleAt )
		{
			_mergeEligibleAt = -1;
			WorldDroppedResourceMerge.TryMergeCluster( this );
		}

		if ( _body is null || !_body.IsValid() )
			_body = Components.Get<Rigidbody>();

		TickPickupReady();

		if ( _body is null || !_body.IsValid() || !_body.MotionEnabled )
			return;

		if ( _body.Sleeping )
		{
			FreezeMotion();
			return;
		}

		var speed = _body.Velocity.Length;
		var spin = _body.AngularVelocity.Length;
		if ( speed > 10f || spin > 25f )
		{
			_settledSince = -1;
			return;
		}

		if ( _settledSince < 0 )
			_settledSince = Time.NowDouble;

		if ( Time.NowDouble - _settledSince < 0.2 )
			return;

		FreezeMotion();
	}

	void FreezeMotion()
	{
		if ( _body is null || !_body.IsValid() )
			return;

		_body.Velocity = Vector3.Zero;
		_body.AngularVelocity = Vector3.Zero;
		_body.MotionEnabled = false;
		_settledSince = -1;
		MarkLanded();
		Settle();
		WorldDroppedResourceMerge.TryMergeCluster( this );
	}

	/// <summary>Host: the fall is over - from here the drop is a model with no collider and no body.</summary>
	void Settle()
	{
		Settled = true;
		DisablePhysics();
	}

	void OnSettledChanged( bool oldValue, bool newValue )
	{
		if ( newValue )
			DisablePhysics();
	}

	void DisablePhysics()
	{
		foreach ( var collider in Components.GetAll<Collider>( FindMode.EverythingInSelf ) )
		{
			if ( collider is not null && collider.IsValid() )
				collider.Enabled = false;
		}

		var body = Components.Get<Rigidbody>( FindMode.EverythingInSelf );
		if ( body is not null && body.IsValid() )
			body.Enabled = false;
	}

	/// <summary>
	/// Host: stop the fall and move toward a magnet target (loot vacuum). Safe to call every frame.
	/// </summary>
	public void AttractToward( Vector3 worldTarget, float maxStep, float shrinkStartDistance )
	{
		if ( !IsAvailable || maxStep <= 0f )
			return;

		if ( _body is null || !_body.IsValid() )
			_body = Components.Get<Rigidbody>();

		if ( !Settled )
		{
			if ( _body is not null && _body.IsValid() )
			{
				_body.Velocity = Vector3.Zero;
				_body.AngularVelocity = Vector3.Zero;
				_body.MotionEnabled = false;
			}

			Settle();
		}

		if ( !_magnetBaseScaleCached )
		{
			_magnetBaseScale = GameObject.LocalScale;
			_magnetBaseScaleCached = true;
		}

		var pos = GameObject.WorldPosition;
		var delta = worldTarget - pos;
		var dist = delta.Length;
		if ( dist <= 1e-4f )
		{
			GameObject.WorldPosition = worldTarget;
			return;
		}

		var step = Math.Min( dist, maxStep );
		GameObject.WorldPosition = pos + (delta / dist) * step;

		if ( shrinkStartDistance > 1e-3f )
		{
			var shrink = Math.Clamp( dist / shrinkStartDistance, 0.2f, 1f );
			GameObject.LocalScale = _magnetBaseScale * shrink;
		}
	}

	public bool CanPickupInto( PlayerInventory inventory ) =>
		PickupReady
		&& IsAvailable
		&& inventory is not null
		&& inventory.CanAcceptResource( ResourceId, StackCount );

	public bool TryPickup( PlayerInventory inventory )
	{
		if ( !IsHostAuthority || !CanPickupInto( inventory ) )
			return false;

		if ( !inventory.HostTryAddResource( ResourceId, StackCount, Wear, CrafterName, Tier ) )
			return false;

		Count = 0;
		GameObject.Destroy();
		return true;
	}
}
