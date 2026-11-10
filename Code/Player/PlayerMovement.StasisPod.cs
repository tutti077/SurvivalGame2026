using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Riding in a <see cref="StasisPod"/> (the arrival flight). Same shape as the vehicle seat: the host writes
/// <see cref="PodLockId"/>, the owner parents its root under the pod's occupant anchor, turns the body keyframed,
/// switches its colliders and input off, and the pawn flies with the pod's synced transform. The owner's view is
/// black the whole time (<see cref="PodBlackout01"/>, drawn by <see cref="PlayerScreenHud"/>), and fades in over
/// <see cref="PodBlackoutFadeSeconds"/> once the door is open and the pod has let go. Map discovery is locked while
/// the view is dark (<see cref="MapDiscoveryLocked"/>): the map page will not open and the minimap hides, so the
/// flight never gives the world away (Mark 2026-11-09).
/// </summary>
public sealed partial class PlayerMovement
{
	public const float PodBlackoutFadeSeconds = 3f;
	/// <summary>A fresh pawn stays dark this long while it waits for the host's pod lock to arrive (coma fade-in otherwise).</summary>
	const float PodSpawnGraceSeconds = 2f;

	/// <summary>Host-synced: the pod this pawn rides in (<see cref="Guid.Empty"/> = none).</summary>
	[Sync( SyncFlags.FromHost )]
	public Guid PodLockId { get; private set; }

	public bool PodLocked => PodLockId != Guid.Empty;

	/// <summary>Owner-local: 1 = fully black, 0 = clear.</summary>
	public float PodBlackout01 { get; private set; }

	/// <summary>Owner-local: the map page stays shut and the minimap hidden while this is true.</summary>
	public bool MapDiscoveryLocked => PodLocked || PodBlackout01 > 0.02f;

	bool _podAppliedLocal;
	GameObject _podDisabledColliderObject;
	readonly List<Collider> _podDisabledColliders = new();
	bool _podSavedGravity = true;
	bool _podSavedUseInput = true;
	float _podSpawnGraceUntil = -1f;
	bool _podGraceDecided;

	/// <summary>Host: lock this pawn into the pod (it rides the pod's transform until <see cref="HostReleasePod"/>).</summary>
	public void HostSetPodLocked( StasisPod pod )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;
		if ( pod is null || !pod.IsValid() )
			return;

		PodLockId = pod.GameObject.Id;
		ApplyPodLockLocal( PodLockId, default, 0f );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplyPodLock( PodLockId, default, 0f );
	}

	/// <summary>Host: let the pawn out at <paramref name="exitWorldPos"/>, facing <paramref name="exitYaw"/>.</summary>
	public void HostReleasePod( Vector3 exitWorldPos, float exitYaw )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		PodLockId = Guid.Empty;
		ApplyPodLockLocal( Guid.Empty, exitWorldPos, exitYaw );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplyPodLock( Guid.Empty, exitWorldPos, exitYaw );
	}

	[Rpc.Owner]
	void RpcOwnerApplyPodLock( Guid podId, Vector3 exitWorldPos, float exitYaw )
	{
		ApplyPodLockLocal( podId, exitWorldPos, exitYaw );
	}

	void ApplyPodLockLocal( Guid podId, Vector3 exitWorldPos, float exitYaw )
	{
		// The pawn's transform is owner-authored: only the owner parents / unparents.
		if ( !IsLocalMovementDriver() )
			return;

		if ( podId == Guid.Empty )
		{
			ExitPodLocal( exitWorldPos, exitYaw );
			return;
		}

		var pod = Scene.Directory.FindByGuid( podId )?.Components.Get<StasisPod>();
		if ( pod is null || !pod.IsValid() )
			return;

		var anchor = pod.OccupantAnchor is { IsValid: true } ? pod.OccupantAnchor : pod.GameObject;
		EnterPodLocal( anchor );
	}

	void EnterPodLocal( GameObject anchor )
	{
		if ( _podAppliedLocal )
			return;

		_podAppliedLocal = true;
		PodBlackout01 = 1f;
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();

		if ( GrappleAttached )
			DetachGrappleForHitReaction();

		// The stock controller re-enables the colliders on its ColliderObject every frame: switch the object off.
		_podDisabledColliderObject = null;
		var colliderObject = _controller?.ColliderObject;
		if ( colliderObject is { IsValid: true, Enabled: true } )
		{
			_podDisabledColliderObject = colliderObject;
			colliderObject.Enabled = false;
		}

		_podDisabledColliders.Clear();
		foreach ( var collider in Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( collider is null || !collider.IsValid() || !collider.Enabled )
				continue;
			_podDisabledColliders.Add( collider );
			collider.Enabled = false;
		}

		if ( body is not null && body.IsValid() )
		{
			_podSavedGravity = body.Gravity;
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
			body.Gravity = false;
			body.MotionEnabled = false;   // keyframed: follows the pod, no simulation
		}

		if ( _controller is not null )
		{
			_podSavedUseInput = _controller.UseInputControls;
			_controller.UseInputControls = false;
		}

		GameObject.SetParent( anchor, false );
		GameObject.LocalPosition = Vector3.Zero;
		GameObject.LocalRotation = Rotation.Identity;
		GameObject.Transform.ClearInterpolation();
		GameObject.Network?.ClearInterpolation();
	}

	void ExitPodLocal( Vector3 exitWorldPos, float exitYaw )
	{
		if ( !_podAppliedLocal )
			return;

		_podAppliedLocal = false;
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();

		GameObject.SetParent( Scene, true );
		if ( exitWorldPos != default )
			GameObject.WorldPosition = exitWorldPos;
		GameObject.WorldRotation = Rotation.FromYaw( exitYaw );

		for ( var i = 0; i < _podDisabledColliders.Count; i++ )
		{
			if ( _podDisabledColliders[i] is { IsValid: true } collider )
				collider.Enabled = true;
		}
		_podDisabledColliders.Clear();

		if ( _podDisabledColliderObject is { IsValid: true } )
			_podDisabledColliderObject.Enabled = true;
		_podDisabledColliderObject = null;

		if ( body is not null && body.IsValid() )
		{
			body.MotionEnabled = true;
			body.Gravity = _podSavedGravity;
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
			if ( body.Sleeping )
				body.Sleeping = false;
		}

		if ( _controller is not null )
			_controller.UseInputControls = _podSavedUseInput;

		GameObject.Transform.ClearInterpolation();
		GameObject.Network?.ClearInterpolation();
	}

	/// <summary>Owner, every frame: glue to the anchor while locked, run the blackout curve, cover a late-arriving lock.</summary>
	void TickPodLock()
	{
		if ( !IsLocalMovementDriver() )
			return;

		if ( !_podGraceDecided )
		{
			_podGraceDecided = true;
			// Arrivals on: a fresh pawn starts in the dark so the frames before the host's lock lands never show the world.
			if ( GameHacks.PodArrivals && StasisPodSpawner.Current is { IsValid: true } )
			{
				_podSpawnGraceUntil = Time.Now + PodSpawnGraceSeconds;
				PodBlackout01 = 1f;
			}
		}

		if ( PodLocked )
		{
			if ( !_podAppliedLocal )
				ApplyPodLockLocal( PodLockId, default, 0f );
			else if ( GameObject.LocalPosition.LengthSquared > 0.0001f )
				GameObject.LocalPosition = Vector3.Zero;
			PodBlackout01 = 1f;
			return;
		}

		if ( _podAppliedLocal )
		{
			// Host cleared the lock (pod gone) before an exit position reached us.
			ExitPodLocal( GameObject.WorldPosition, GameObject.WorldRotation.Yaw() );
		}

		if ( Time.Now < _podSpawnGraceUntil )
		{
			PodBlackout01 = 1f;
			return;
		}

		if ( PodBlackout01 > 0f )
			PodBlackout01 = MathF.Max( 0f, PodBlackout01 - Time.Delta / PodBlackoutFadeSeconds );
	}
}
