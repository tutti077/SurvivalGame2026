using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Seated-in-vehicle state (the <see cref="Vehicle"/> seats the pawn; this is the pawn's half). Host
/// writes <see cref="SeatedVehicleId"/> / <see cref="SeatedSeatIndex"/> and tells the owner to apply:
/// the owner parents its root under the seat object (the pawn then rides the vehicle's synced
/// transform with no lag on any machine), turns the physics body keyframed, disables its colliders and
/// the controller's input, and re-pins to the seat each frame. On exit the owner is put down beside the
/// vehicle where the host said. No move / jump / roll / grapple / augment while seated; the driver also
/// loses Attack1 / Attack2 (passengers keep them — <see cref="PlayerCombat"/> gates the driver too).
/// Same shape as the bear-trap hold (<see cref="TrapLocked"/>).
/// </summary>
public sealed partial class PlayerMovement
{
	/// <summary>Host-synced: the vehicle root this pawn sits in (<see cref="Guid.Empty"/> = on foot).</summary>
	[Sync( SyncFlags.FromHost )]
	public Guid SeatedVehicleId { get; private set; }

	/// <summary>Host-synced: seat index on that vehicle (0 = driver), -1 when on foot.</summary>
	[Sync( SyncFlags.FromHost )]
	public int SeatedSeatIndex { get; private set; } = -1;

	public bool IsSeated => SeatedVehicleId != Guid.Empty;
	public bool IsSeatedDriver => IsSeated && SeatedSeatIndex == 0;
	public bool IsSeatedPassenger => IsSeated && SeatedSeatIndex > 0;

	Vehicle _seatedVehicle;
	Guid _seatedVehicleCacheId;

	bool _seatAppliedLocal;
	readonly List<Collider> _seatDisabledColliders = new();
	GameObject _seatDisabledColliderObject;
	bool _seatSavedGravity = true;
	bool _seatSavedUseInput = true;
	bool _seatSavedUseAnimator = true;
	Rotation _seatSavedRootRotation = Rotation.Identity;

	public Vehicle SeatedVehicle
	{
		get
		{
			if ( !IsSeated )
				return null;

			if ( _seatedVehicle is { IsValid: true } && _seatedVehicleCacheId == SeatedVehicleId )
				return _seatedVehicle;

			_seatedVehicle = Scene.Directory.FindByGuid( SeatedVehicleId )?.Components.Get<Vehicle>();
			_seatedVehicleCacheId = SeatedVehicleId;
			return _seatedVehicle;
		}
	}

	/// <summary>Host: seat (vehicle + index) or unseat (null; <paramref name="exitWorldPos"/> is where the feet go).</summary>
	public void HostSetSeated( Vehicle vehicle, int seatIndex, Vector3 exitWorldPos )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		var id = vehicle is { IsValid: true } && seatIndex >= 0 ? vehicle.GameObject.Id : Guid.Empty;
		SeatedVehicleId = id;
		SeatedSeatIndex = id == Guid.Empty ? -1 : seatIndex;

		ApplySeatedLocal( id, SeatedSeatIndex, exitWorldPos );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplySeated( id, SeatedSeatIndex, exitWorldPos );
	}

	[Rpc.Owner]
	void RpcOwnerApplySeated( Guid vehicleId, int seatIndex, Vector3 exitWorldPos )
	{
		ApplySeatedLocal( vehicleId, seatIndex, exitWorldPos );
	}

	void ApplySeatedLocal( Guid vehicleId, int seatIndex, Vector3 exitWorldPos )
	{
		// The pawn's transform is owner-authored: only the owner parents / unparents.
		if ( !IsLocalMovementDriver() )
			return;

		if ( vehicleId == Guid.Empty )
		{
			ExitSeatLocal( exitWorldPos );
			return;
		}

		var vehicle = Scene.Directory.FindByGuid( vehicleId )?.Components.Get<Vehicle>();
		var seatGo = vehicle?.GetSeatObject( seatIndex );
		if ( seatGo is null || !seatGo.IsValid() )
			return;

		EnterSeatLocal( seatGo );
	}

	void EnterSeatLocal( GameObject seatGo )
	{
		if ( _seatAppliedLocal )
			return;

		_seatAppliedLocal = true;
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();

		if ( GrappleAttached )
			DetachGrappleForHitReaction();

		_seatSavedRootRotation = GameObject.WorldRotation;

		// The stock controller owns the colliders on its ColliderObject and re-enables them every frame,
		// so disabling the components is not enough: switch the whole object off (the controller leaves
		// that alone). Any collider outside that object is disabled individually.
		_seatDisabledColliderObject = null;
		var colliderObject = _controller?.ColliderObject;
		if ( colliderObject is { IsValid: true, Enabled: true } )
		{
			_seatDisabledColliderObject = colliderObject;
			colliderObject.Enabled = false;
		}

		_seatDisabledColliders.Clear();
		foreach ( var collider in Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( collider is null || !collider.IsValid() || !collider.Enabled )
				continue;
			_seatDisabledColliders.Add( collider );
			collider.Enabled = false;
		}

		if ( body is not null && body.IsValid() )
		{
			_seatSavedGravity = body.Gravity;
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
			body.Gravity = false;
			body.MotionEnabled = false;   // keyframed: follows the seat, no simulation
		}

		if ( _controller is not null )
		{
			_seatSavedUseInput = _controller.UseInputControls;
			_controller.UseInputControls = false;
			// The controller's animator pass rewrites the graph every frame on the simulating machine and
			// would stand the pawn back up; PlayerAnimation owns the pose while seated.
			_seatSavedUseAnimator = _controller.UseAnimatorControls;
			_controller.UseAnimatorControls = false;
		}

		GameObject.SetParent( seatGo, false );
		GameObject.LocalPosition = Vector3.Zero;
		GameObject.LocalRotation = Rotation.Identity;
		GameObject.Transform.ClearInterpolation();
		GameObject.Network?.ClearInterpolation();
	}

	void ExitSeatLocal( Vector3 exitWorldPos )
	{
		if ( !_seatAppliedLocal )
			return;

		_seatAppliedLocal = false;
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();

		GameObject.SetParent( Scene, true );
		if ( exitWorldPos != default )
			GameObject.WorldPosition = exitWorldPos;
		GameObject.WorldRotation = _seatSavedRootRotation;

		for ( var i = 0; i < _seatDisabledColliders.Count; i++ )
		{
			if ( _seatDisabledColliders[i] is { IsValid: true } collider )
				collider.Enabled = true;
		}
		_seatDisabledColliders.Clear();

		if ( _seatDisabledColliderObject is { IsValid: true } )
			_seatDisabledColliderObject.Enabled = true;
		_seatDisabledColliderObject = null;

		if ( body is not null && body.IsValid() )
		{
			body.MotionEnabled = true;
			body.Gravity = _seatSavedGravity;
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
			if ( body.Sleeping )
				body.Sleeping = false;
		}

		if ( _controller is not null )
		{
			_controller.UseInputControls = _seatSavedUseInput;
			_controller.UseAnimatorControls = _seatSavedUseAnimator;
		}

		GameObject.Transform.ClearInterpolation();
		GameObject.Network?.ClearInterpolation();
	}

	/// <summary>Owner, every frame while seated: stay glued to the seat; the driver cannot swing or shoot.</summary>
	void TickSeatedPin()
	{
		if ( !_seatAppliedLocal )
			return;

		if ( !IsSeated )
		{
			// Host cleared the seat (vehicle wrecked / despawned) before any exit position reached us.
			ExitSeatLocal( GameObject.WorldPosition );
			return;
		}

		var vehicle = SeatedVehicle;
		if ( vehicle is null || !vehicle.IsValid() )
		{
			ExitSeatLocal( GameObject.WorldPosition );
			return;
		}

		if ( GameObject.LocalPosition.LengthSquared > 0.0001f )
			GameObject.LocalPosition = Vector3.Zero;
		if ( GameObject.LocalRotation != Rotation.Identity )
			GameObject.LocalRotation = Rotation.Identity;

		// Belt and braces: nothing of the pawn may collide with the chassis while it rides.
		if ( _seatDisabledColliderObject is { IsValid: true, Enabled: true } )
			_seatDisabledColliderObject.Enabled = false;
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() && body.MotionEnabled )
			body.MotionEnabled = false;
	}

	void PreInputSeated()
	{
		ClearActionIfPressed( JumpInputAction );
		if ( !string.IsNullOrWhiteSpace( SprintInputAction ) )
			ClearActionIfPressed( SprintInputAction );
		ClearActionIfPressed( SneakInputAction );

		if ( IsSeatedDriver )
		{
			// Hands on the wheel: no swing, block, tool or bow for the driver (passengers keep theirs).
			ClearActionIfPressed( "Attack1" );
			ClearActionIfPressed( "Attack2" );
		}

		TickSeatedPin();
	}
}
