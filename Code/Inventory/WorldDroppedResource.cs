using System;
using Sandbox;

namespace Survival;

/// <summary>Physics world pickup spawned when the player drops a stack from the hotbar.</summary>
[Title( "World Dropped Resource" )]
public sealed class WorldDroppedResource : Component
{
	const float DropperSelfPickupDelaySeconds = 5f;

	[Sync, Property, Group( "Pickup" )]
	public string ResourceId { get; set; } = string.Empty;

	[Sync, Property, Group( "Pickup" )]
	public int Count { get; set; }

	public bool IsAvailable => Count > 0 && Active && GameObject.IsValid() && GameObject.Enabled;

	internal bool IsHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	internal bool IsMergeAuthority => IsHostAuthority;

	double _droppedAt = -1;
	Guid _dropperConnectionId = Guid.Empty;

	Rigidbody _body;
	double _settledSince = -1;

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

	public void Configure( string resourceId, int count )
	{
		ResourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		Count = Math.Max( 1, count );
	}

	public void SetDropper( GameObject dropper )
	{
		_droppedAt = Time.NowDouble;
		_dropperConnectionId = ResolveConnection( dropper )?.Id ?? Guid.Empty;
	}

	public int StackCount => Math.Max( 0, Count );

	protected override void OnStart()
	{
		base.OnStart();
		_body = Components.Get<Rigidbody>();
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();

		if ( !IsHostAuthority || Count <= 0 )
			return;

		if ( _body is null || !_body.IsValid() )
			_body = Components.Get<Rigidbody>();

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
		WorldDroppedResourceMerge.TryMergeCluster( this );
	}

	public static bool TryFindOnHierarchy( GameObject hitObject, out WorldDroppedResource drop )
	{
		drop = null;
		if ( hitObject is null || !hitObject.IsValid() )
			return false;

		for ( var current = hitObject; current.IsValid(); current = current.Parent )
		{
			var candidate = current.Components.Get<WorldDroppedResource>();
			if ( candidate is null || !candidate.IsAvailable )
				continue;

			drop = candidate;
			return true;
		}

		return false;
	}

	public bool CanPickupFor( GameObject picker )
	{
		if ( !IsAvailable || picker is null || !picker.IsValid() )
			return false;

		if ( _droppedAt < 0 || _dropperConnectionId == Guid.Empty )
			return true;

		if ( Time.NowDouble - _droppedAt < DropperSelfPickupDelaySeconds )
		{
			var pickerConn = ResolveConnection( picker );
			if ( pickerConn is not null && pickerConn.Id == _dropperConnectionId )
				return false;
		}

		return true;
	}

	public bool CanPickupInto( PlayerInventory inventory ) =>
		IsAvailable
		&& inventory is not null
		&& CanPickupFor( inventory.GameObject )
		&& inventory.CanAcceptResource( ResourceId, StackCount );

	public bool TryPickup( PlayerInventory inventory )
	{
		if ( !IsHostAuthority || !CanPickupInto( inventory ) )
			return false;

		if ( !inventory.HostTryAddResource( ResourceId, StackCount ) )
			return false;

		Count = 0;
		GameObject.Destroy();
		return true;
	}

	static Connection ResolveConnection( GameObject go )
	{
		if ( go is null || !go.IsValid() )
			return Connection.Local;

		if ( go.Network is { Active: true, Owner: { } owner } )
			return owner;

		return Connection.Local;
	}
}
