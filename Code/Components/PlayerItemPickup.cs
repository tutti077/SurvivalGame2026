using System;
using System.Collections.Generic;
using Sandbox;

namespace Game;

/// <summary>
/// <para><b>Player:</b> Add this component next to <see cref="PlayerController"/> (same object or a parent). Handles pickup/drop
/// and kinematic hold snap. Weapon grip tuning lives on <see cref="MeleeWeapon"/> (<c>HeldLocalOffset</c> / <c>HeldLocalAngles</c>).</para>
/// Third person: traces from the body using <see cref="PlayerController.EyeAngles"/> (not the orbit camera) so hits match the crosshair.
/// Hold visibility: raise <see cref="HoldThirdPersonWorldUp"/> / <see cref="HoldOffset"/> forward, add <see cref="HoldThirdPersonAnchorForward"/>, or <see cref="HoldThirdPersonPullTowardCamera"/> to bring the prop into the orbit-camera view.
/// First person: uses <see cref="Scene.Camera"/> for trace and hold position.
/// Default interact is <b>use</b>. Set <see cref="AlternatePickupButton"/> (e.g. <c>reload</c>) if engine use conflicts with doors.
/// </summary>
public sealed class PlayerItemPickup : Component
{
	[Property] public string PickupButton { get; set; } = "use";

	/// <summary>Optional second button (e.g. <c>reload</c>) so you can bind pickup without fighting engine use.</summary>
	[Property] public string AlternatePickupButton { get; set; } = "";

	[Property] public float PickupRange { get; set; } = 140f;

	/// <summary>Sphere radius for pickup trace — more forgiving than a thin ray.</summary>
	[Property] public float PickupTraceRadius { get; set; } = 6f;

	/// <summary>Carry / first-person hold offset in aim or camera local space (forward, right, up).</summary>
	[Property] public Vector3 HoldOffset { get; set; } = new Vector3( 28f, 2f, 8f );

	/// <summary>
	/// First person only: extra world offset along <see cref="Scene.Camera"/> view forward before <see cref="HoldOffset"/> is applied.
	/// Keeps props past the camera near plane and out of the torso (shadow visible but mesh clipped = increase this).
	/// </summary>
	[Property] public float HoldFirstPersonViewForward { get; set; } = 32f;

	/// <summary>Extra offset along view right (positive = hand side / screen-right).</summary>
	[Property] public float HoldRightBias { get; set; } = 4f;

	/// <summary>
	/// Third person only: extra world-up on the hold anchor so the prop sits chest/hand height (eye trace starts low on the body).
	/// </summary>
	[Property] public float HoldThirdPersonWorldUp { get; set; } = 18f;

	/// <summary>
	/// Third person only: extra distance along <b>body/eye forward</b> added to the hold anchor before <see cref="HoldOffset"/> is applied.
	/// Use this (or a larger <see cref="HoldOffset"/> forward component) when the prop sits inside the torso.
	/// </summary>
	[Property] public float HoldThirdPersonAnchorForward { get; set; } = 0f;

	/// <summary>
	/// Third person only: after computing the hold position, nudge it up to this many world-units toward <see cref="Scene.Camera"/> (orbit cam).
	/// Helps when the blade is technically &quot;in front&quot; of the body but still hidden behind the mesh or outside the camera frustum.
	/// </summary>
	[Property] public float HoldThirdPersonPullTowardCamera { get; set; } = 0f;

	/// <summary>If true, third person uses <see cref="HoldOffsetThirdPerson"/> and <see cref="HoldRightBiasThirdPerson"/> instead of <see cref="HoldOffset"/> / <see cref="HoldRightBias"/>.</summary>
	[Property] public bool UseThirdPersonHoldOffsets { get; set; } = false;

	/// <summary>Third-person carry offset (aim local space). Used only when <see cref="UseThirdPersonHoldOffsets"/> is true.</summary>
	[Property] public Vector3 HoldOffsetThirdPerson { get; set; } = new Vector3( 16f, 2f, 6f );

	[Property] public float HoldRightBiasThirdPerson { get; set; } = 4f;

	/// <summary>First person / camera hold: extra rotation after aim (pitch, yaw, roll).</summary>
	[Property] public Angles HoldCarryRotationOffset { get; set; }

	/// <summary>Third person carry: e.g. yaw -90 to tuck the blade off the forehead / camera line.</summary>
	[Property] public Angles HoldCarryRotationOffsetThirdPerson { get; set; } = new Angles( 0f, -90f, 0f );

	[Property] public bool MatchPickupTag { get; set; } = false;

	[Property] public float DropForwardImpulse { get; set; } = 80f;

	/// <summary>Trace starts this far in front of the body along view (reduces self-hits in 3rd person).</summary>
	[Property] public float AimTraceStartForward { get; set; } = 14f;

	/// <summary>Turn off sword/prop colliders while held so they do not fight the player or grapple swing (rope is visual only).</summary>
	[Property] public bool DisableHeldCollidersWhileCarried { get; set; } = true;

	private GameObject _held;
	private Rigidbody _heldBody;
	private PickableItem _heldPickable;
	private bool _hadMotion;
	private bool _hadGravity;

	private PlayerController _player;

	private readonly List<(Collider Col, bool WasEnabled)> _heldColliderStates = new();

	private PlayerController Player => _player ??= FindPlayerController();

	/// <summary>Physics/visual root currently held (for other systems, e.g. grapple aim traces).</summary>
	public GameObject HeldRoot => _held is not null && _held.IsValid() ? _held : null;

	protected override void OnEnabled()
	{
		_player = null;
		ClearHeld();
	}

	protected override void OnDisabled()
	{
		if ( _held is not null && _held.IsValid() )
			Drop();

		_player = null;
		ClearHeld();
	}

	protected override void OnUpdate()
	{
		if ( _held is not null && _held.IsValid() && TryGetHoldTransform( out var targetPos, out var targetRot ) )
			SnapHeldTo( targetPos, targetRot );

		var pressed =
			Input.Pressed( PickupButton )
			|| (!string.IsNullOrEmpty( AlternatePickupButton ) && Input.Pressed( AlternatePickupButton ));

		if ( !pressed )
			return;

		if ( _held is not null && _held.IsValid() )
		{
			Drop();
			return;
		}

		TryPickup();
	}

	private void SnapHeldTo( Vector3 targetPos, Rotation targetRot )
	{
		var moveRoot = _heldBody is not null && _heldBody.IsValid() ? _heldBody.GameObject : _held;
		moveRoot.WorldPosition = targetPos;
		moveRoot.WorldRotation = targetRot;

		if ( _heldBody is not null && _heldBody.IsValid() )
		{
			_heldBody.Velocity = Vector3.Zero;
			_heldBody.AngularVelocity = Vector3.Zero;
		}
	}

	private void GetCarryHoldVectors( bool thirdPerson, out Vector3 offsetLocal, out float rightBias )
	{
		if ( thirdPerson && UseThirdPersonHoldOffsets )
		{
			offsetLocal = HoldOffsetThirdPerson;
			rightBias = HoldRightBiasThirdPerson;
		}
		else
		{
			offsetLocal = HoldOffset;
			rightBias = HoldRightBias;
		}
	}

	private void NudgeHoldTowardCamera( ref Vector3 targetPos )
	{
		var pull = Math.Max( HoldThirdPersonPullTowardCamera, 0f );
		if ( pull < 0.001f )
			return;

		var cam = Scene.Camera;
		if ( cam is null )
			return;

		var toCam = cam.WorldPosition - targetPos;
		var len = toCam.Length;
		if ( len < 0.001f )
			return;

		targetPos += toCam.Normal * Math.Min( pull, len * 0.92f );
	}

	private bool TryGetHoldTransform( out Vector3 targetPos, out Rotation targetRot )
	{
		var pc = Player;

		if ( pc is not null && pc.ThirdPerson )
		{
			if ( !TryGetPlayerEyeAim( out var aimOrigin, out var aimRot ) )
			{
				targetPos = default;
				targetRot = default;
				return false;
			}

			GetCarryHoldVectors( true, out var off, out var bias );
			var lift = Math.Max( HoldThirdPersonWorldUp, 0f );
			var anchorForward = Math.Max( HoldThirdPersonAnchorForward, 0f );
			targetPos = aimOrigin + aimRot.Forward * anchorForward + Vector3.Up * lift + aimRot * off + aimRot.Right * bias;
			NudgeHoldTowardCamera( ref targetPos );
			targetRot = aimRot * Rotation.From( HoldCarryRotationOffsetThirdPerson );
			ApplyHeldMeleeGrip( aimRot, ref targetPos, ref targetRot );
			return true;
		}

		var cam = Scene.Camera;
		if ( cam is not null )
		{
			var cr = cam.WorldRotation;
			GetCarryHoldVectors( false, out var off, out var bias );
			var fpPad = Math.Max( HoldFirstPersonViewForward, 0f );
			targetPos = cam.WorldPosition + cr.Forward * fpPad + cr * off + cr.Right * bias;
			targetRot = cr * Rotation.From( HoldCarryRotationOffset );
			ApplyHeldMeleeGrip( cr, ref targetPos, ref targetRot );
			return true;
		}

		if ( TryGetPlayerEyeAim( out var origin, out var rot ) )
		{
			GetCarryHoldVectors( false, out var off, out var bias );
			var fpPad = Math.Max( HoldFirstPersonViewForward, 0f );
			targetPos = origin + rot.Forward * fpPad + rot * off + rot.Right * bias;
			targetRot = rot * Rotation.From( HoldCarryRotationOffset );
			ApplyHeldMeleeGrip( rot, ref targetPos, ref targetRot );
			return true;
		}

		targetPos = default;
		targetRot = default;
		return false;
	}

	private void ApplyHeldMeleeGrip( Rotation viewBasis, ref Vector3 targetPos, ref Rotation targetRot )
	{
		var melee = FindMeleeWeaponInHierarchy( _held );
		if ( melee is null || !melee.IsValid() )
			return;

		var lo = melee.HeldLocalOffset;
		if ( lo.LengthSquared > 1e-12f )
			targetPos += viewBasis * lo;

		var la = melee.HeldLocalAngles;
		if ( !la.IsNearlyZero( 1e-4 ) )
			targetRot *= Rotation.From( la );
	}

	private static MeleeWeapon FindMeleeWeaponInHierarchy( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			return null;

		var m = root.Components.Get<MeleeWeapon>();
		if ( m is not null )
			return m;

		foreach ( var child in root.Children )
		{
			var found = FindMeleeWeaponInHierarchy( child );
			if ( found is not null )
				return found;
		}

		return null;
	}

	private void TryPickup()
	{
		if ( !TryGetPickupTrace( out var tr ) )
			return;

		if ( !tr.Hit )
			return;

		var pickable = FindPickable( tr.GameObject );
		if ( pickable is null )
			return;

		if ( MatchPickupTag && !HasPickupTag( tr.GameObject, pickable.PickupTag ) )
			return;

		var target = pickable.GameObject;
		_held = target;
		_heldPickable = pickable;
		_heldBody = FindRigidbodyOnHierarchy( target );

		if ( _heldBody is not null )
		{
			_hadMotion = _heldBody.MotionEnabled;
			_hadGravity = _heldBody.Gravity;
			_heldBody.Gravity = false;
			_heldBody.MotionEnabled = false;
			_heldBody.Velocity = Vector3.Zero;
			_heldBody.AngularVelocity = Vector3.Zero;
		}

		if ( DisableHeldCollidersWhileCarried )
			BackupAndDisableCollidersOnHierarchy( _held );

		if ( TryGetHoldTransform( out var snapPos, out var snapRot ) )
			SnapHeldTo( snapPos, snapRot );
	}

	private bool TryGetPickupTrace( out SceneTraceResult tr )
	{
		tr = default;

		if ( !TryGetPickupAimRay( out var start, out var forward ) )
			return false;

		var end = start + forward * PickupRange;
		var ignoreRoot = Player?.GameObject ?? GameObject;
		var radius = Math.Max( PickupTraceRadius, 0.1f );

		tr = Scene.Trace
			.Sphere( radius, start, end )
			.IgnoreGameObjectHierarchy( ignoreRoot )
			.Run();

		return true;
	}

	private static Rigidbody FindRigidbodyOnHierarchy( GameObject root )
	{
		for ( var go = root; go is not null; go = go.Parent )
		{
			var rb = go.Components.Get<Rigidbody>();
			if ( rb is not null )
				return rb;
		}

		return null;
	}

	/// <summary>FP: camera ray. TP: eye-angles ray from body (same idea as grapple).</summary>
	private bool TryGetPickupAimRay( out Vector3 start, out Vector3 forward )
	{
		var pc = Player;
		var cam = Scene.Camera;

		var useThirdPersonAim = pc is not null && pc.ThirdPerson;

		if ( !useThirdPersonAim && cam is not null )
		{
			start = cam.WorldPosition;
			forward = cam.WorldRotation.Forward;
			return true;
		}

		if ( TryGetPlayerEyeAim( out start, out var rot ) )
		{
			forward = rot.Forward;
			return true;
		}

		if ( cam is not null )
		{
			start = cam.WorldPosition;
			forward = cam.WorldRotation.Forward;
			return true;
		}

		start = default;
		forward = default;
		return false;
	}

	private bool TryGetPlayerEyeAim( out Vector3 origin, out Rotation eyeRot )
	{
		var pc = Player;
		if ( pc is null || pc.Body is null )
		{
			origin = default;
			eyeRot = default;
			return false;
		}

		eyeRot = Rotation.From( pc.EyeAngles );
		var fwd = eyeRot.Forward;
		var bodyPos = pc.Body.GameObject.WorldPosition;
		var pad = Math.Max( AimTraceStartForward, 0f ) + Math.Max( pc.BodyRadius, 1f );
		origin = bodyPos + Vector3.Up * (pc.CurrentHeight * 0.45f) + fwd * pad;
		return true;
	}

	private void Drop()
	{
		if ( _held is null || !_held.IsValid() )
		{
			ClearHeld();
			return;
		}

		RestoreHeldColliders();

		if ( _heldBody is not null && _heldBody.IsValid() )
		{
			_heldBody.Gravity = _hadGravity;
			_heldBody.MotionEnabled = _hadMotion;

			if ( DropForwardImpulse > 0f && TryGetPickupAimRay( out _, out var forward ) )
				_heldBody.ApplyImpulse( forward * DropForwardImpulse );
		}

		ClearHeld();
	}

	private void ClearHeld()
	{
		RestoreHeldColliders();
		_held = null;
		_heldBody = null;
		_heldPickable = null;
	}

	private void BackupAndDisableCollidersOnHierarchy( GameObject root )
	{
		if ( !DisableHeldCollidersWhileCarried || root is null || !root.IsValid() )
			return;

		RestoreHeldColliders();

		_heldColliderStates.Clear();
		CollectCollidersRecursive( root );

		for ( var i = 0; i < _heldColliderStates.Count; i++ )
		{
			var pair = _heldColliderStates[i];
			if ( pair.Col is not null && pair.Col.IsValid() )
				pair.Col.Enabled = false;
		}
	}

	private void CollectCollidersRecursive( GameObject go )
	{
		if ( go is null || !go.IsValid() )
			return;

		foreach ( var c in go.Components.GetAll<Collider>() )
		{
			if ( c is not null && c.IsValid() )
				_heldColliderStates.Add( (c, c.Enabled) );
		}

		foreach ( var child in go.Children )
			CollectCollidersRecursive( child );
	}

	private void RestoreHeldColliders()
	{
		for ( var i = 0; i < _heldColliderStates.Count; i++ )
		{
			var (col, was) = _heldColliderStates[i];
			if ( col is not null && col.IsValid() )
				col.Enabled = was;
		}

		_heldColliderStates.Clear();
	}

	private PlayerController FindPlayerController()
	{
		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is not null )
				return pc;
		}

		return null;
	}

	private static PickableItem FindPickable( GameObject obj )
	{
		while ( obj is not null )
		{
			var p = obj.Components.Get<PickableItem>();
			if ( p is not null )
				return p;

			obj = obj.Parent;
		}

		return null;
	}

	private static bool HasPickupTag( GameObject obj, string tag )
	{
		if ( string.IsNullOrEmpty( tag ) )
			return true;

		while ( obj is not null )
		{
			if ( obj.Tags.Has( tag ) )
				return true;

			obj = obj.Parent;
		}

		return false;
	}
}
