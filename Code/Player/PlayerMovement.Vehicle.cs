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
/// vehicle where the host said. The owner RPC is sent at once and the synced id only goes out with the
/// next network tick, so on a remote owner the RPC lands first: the owner applies the RPC and waits for
/// the synced id to agree (<see cref="_seatAwaitingSync"/>) instead of reading the stale id as "the host
/// cleared my seat". No move / jump / roll / grapple / augment while seated; the driver also
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
	/// <summary>Owner: the vehicle this pawn is parented into right now (<see cref="Guid.Empty"/> = on foot).</summary>
	Guid _seatAppliedVehicleId;
	/// <summary>Owner: an owner RPC was applied and <see cref="SeatedVehicleId"/> has not caught up with it yet.</summary>
	bool _seatAwaitingSync;
	readonly List<Collider> _seatDisabledColliders = new();
	GameObject _seatDisabledColliderObject;
	bool _seatSavedGravity = true;
	bool _seatSavedUseInput = true;
	bool _seatSavedUseAnimator = true;
	Rotation _seatSavedRootRotation = Rotation.Identity;
	GameObject _seatBodyObject;

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
		}
		else
		{
			var vehicle = Scene.Directory.FindByGuid( vehicleId )?.Components.Get<Vehicle>();
			var seatGo = vehicle?.GetSeatObject( seatIndex );
			if ( seatGo is not null && seatGo.IsValid() )
				EnterSeatLocal( seatGo, vehicleId );
		}

		// Applied what was asked while the synced id still shows the state before it: the sync is in flight.
		// (A failed enter does not wait - TickSeatedPin retries it from the synced id once that arrives.)
		_seatAwaitingSync = _seatAppliedVehicleId == vehicleId && SeatedVehicleId != _seatAppliedVehicleId;
	}

	void EnterSeatLocal( GameObject seatGo, Guid vehicleId )
	{
		if ( _seatAppliedLocal )
			return;

		_seatAppliedLocal = true;
		_seatAppliedVehicleId = vehicleId;
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

		// The controller turns the Body child toward the look yaw, not the root; with its animator pass
		// off that child would keep whatever facing it had on entry. Square it to the seat (the head
		// still follows the camera through the animgraph look-at, see PlayerAnimation.Vehicle).
		_seatBodyObject = _controller?.Renderer?.GameObject;
		if ( _seatBodyObject is { IsValid: true } )
		{
			_seatBodyObject.LocalRotation = Rotation.Identity;
			_seatBodyObject.Transform.ClearInterpolation();
		}
	}

	void ExitSeatLocal( Vector3 exitWorldPos )
	{
		if ( !_seatAppliedLocal )
			return;

		_seatAppliedLocal = false;
		_seatAppliedVehicleId = Guid.Empty;
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();

		GameObject.SetParent( Scene, true );
		if ( exitWorldPos != default )
			GameObject.WorldPosition = exitWorldPos;
		GameObject.WorldRotation = _seatSavedRootRotation;

		if ( _seatBodyObject is { IsValid: true } && _controller is not null )
			_seatBodyObject.WorldRotation = Rotation.FromYaw( _controller.EyeAngles.yaw );
		_seatBodyObject = null;

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

	/// <summary>Owner, every frame: keep the local seat in step with the host's synced one, and stay glued to the seat while in it.</summary>
	void TickSeatedPin()
	{
		if ( SeatedVehicleId == _seatAppliedVehicleId )
		{
			_seatAwaitingSync = false;
		}
		else if ( !_seatAwaitingSync )
		{
			// The synced seat changed with no owner RPC applied here: the host cleared it (vehicle wrecked /
			// despawned) before an exit position reached us, or the enter RPC could not find the vehicle yet.
			if ( _seatAppliedLocal )
				ExitSeatLocal( GameObject.WorldPosition );
			if ( IsSeated )
				ApplySeatedLocal( SeatedVehicleId, SeatedSeatIndex, default );
		}

		if ( !_seatAppliedLocal )
			return;

		// While the sync is in flight SeatedVehicle still answers for the state before the RPC.
		if ( !_seatAwaitingSync )
		{
			var vehicle = SeatedVehicle;
			if ( vehicle is null || !vehicle.IsValid() )
			{
				ExitSeatLocal( GameObject.WorldPosition );
				return;
			}
		}

		if ( GameObject.LocalPosition.LengthSquared > 0.0001f )
			GameObject.LocalPosition = Vector3.Zero;
		if ( GameObject.LocalRotation != Rotation.Identity )
			GameObject.LocalRotation = Rotation.Identity;
		if ( _seatBodyObject is { IsValid: true } && _seatBodyObject.LocalRotation != Rotation.Identity )
			_seatBodyObject.LocalRotation = Rotation.Identity;

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
		// The hover platform's driver flies on jump / sneak (Space up, Ctrl down): the Vehicle reads those
		// actions itself, so they stay live for that one pawn. Everyone else seated loses them.
		var flying = IsSeatedDriver && SeatedVehicle is { UsesVerticalInput: true };
		if ( !flying )
		{
			ClearActionIfPressed( JumpInputAction );
			ClearActionIfPressed( SneakInputAction );
		}
		if ( !string.IsNullOrWhiteSpace( SprintInputAction ) )
			ClearActionIfPressed( SprintInputAction );

		if ( IsSeatedDriver )
		{
			// Hands on the wheel: no swing, block, tool or bow for the driver (passengers keep theirs).
			ClearActionIfPressed( "Attack1" );
			ClearActionIfPressed( "Attack2" );
		}

		TickSeatedPin();
	}
}
