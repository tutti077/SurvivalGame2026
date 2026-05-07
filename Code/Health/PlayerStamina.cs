using System;
using System.Collections.Generic;
using Sandbox;

namespace Game;

/// <summary>
/// Host-authoritative stamina. Sprint drain uses run input plus locomotion (wish, analog, or move keys) so it does not drop out when
/// the mouse steals analog or wish briefly dips; sprint does not drain stamina while grappling (run has no effect there). Regen only on ground (not grappling, swimming, or climbing), not while holding sprint/run,
/// after empty (delay), and until the post-use cooldown passes with no net loss. Regen targets ~<see cref="StaminaRegenFillTimeSeconds"/> from 0→max and ramps faster near full (see <see cref="StaminaRegenAccelerationExponent"/>, <see cref="StaminaRegenBaselineWeight"/>).
/// </summary>
public sealed partial class PlayerStamina : Component, PlayerController.IEvents
{
	const float StaminaEpsilon = 0.001f;

	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnMaxStaminaChanged ) )]
	public float MaxStamina { get; set; } = 100f;

	[Sync( SyncFlags.FromHost ), Change( nameof( OnCurrentStaminaChanged ) )]
	public float CurrentStamina { get; set; } = 100f;

	/// <summary>After empty, regen stays off until this many seconds have passed.</summary>
	[Property] public float StaminaRegenDelayAfterEmptySeconds { get; set; } = 2f;

	/// <summary>After any net stamina loss, regen stays off until this many seconds pass with no further net loss.</summary>
	[Property] public float StaminaRegenCooldownAfterUseSeconds { get; set; } = 1f;

	/// <summary>Target seconds to refill from empty to max when regen is allowed (calibrated for the blend curve below).</summary>
	[Property] public float StaminaRegenFillTimeSeconds { get; set; } = 3f;

	/// <summary>Surge shape: regen blend uses (current/max)^this power on top of a baseline so the bar still accelerates toward the end.</summary>
	[Property] public float StaminaRegenAccelerationExponent { get; set; } = 3f;

	/// <summary>At 0 stamina, regen runs at this fraction of the calibrated peak; the rest of the curve surges in as the bar fills.</summary>
	[Property] public float StaminaRegenBaselineWeight { get; set; } = 0.28f;

	[Property] public float SprintDrainPerSecond { get; set; } = 12f;

	/// <summary>Horizontal wish speed above this counts as locomotion for sprint drain (low so forward run does not flicker off).</summary>
	[Property] public float SprintDrainMinHorizontalWish { get; set; } = 2f;

	/// <summary>Analog move length above this counts as locomotion for sprint drain.</summary>
	[Property] public float SprintDrainMinAnalogMove { get; set; } = 0.02f;

	/// <summary>Horizontal <see cref="PlayerController.Velocity"/> above this counts as locomotion (wish/analog can dip for a frame when looking around).</summary>
	[Property] public float SprintDrainMinHorizontalSpeed { get; set; } = 55f;

	[Property] public float GrappleStartStaminaCost { get; set; } = 5f;

	/// <summary>Per second while grapple auto high-speed retract is active (move keys no longer drain swing stamina).</summary>
	[Property] public float GrappleSwingStaminaDrainPerSecond { get; set; } = 2f;

	[Property] public string AttackButton { get; set; } = "attack1";

	[Property] public float JumpStaminaCost { get; set; } = 5f;

	/// <summary>When stamina is below <see cref="JumpStaminaCost"/>, jump apex height is this fraction of the normal apex (velocity scales as the square root).</summary>
	[Property] public float OutOfStaminaJumpHeightFraction { get; set; } = 0.5f;

	[Property] public int StaminaBarTickDivisions { get; set; } = 10;

	public event Action<float, float> OnStaminaChanged;

	public float StaminaFraction => MaxStamina > 0.001f ? Math.Clamp( CurrentStamina / MaxStamina, 0f, 1f ) : 0f;

	public bool HasStaminaForActions => CurrentStamina > StaminaEpsilon;

	public bool HasStaminaForGrappleAttach => CurrentStamina >= GrappleStartStaminaCost - StaminaEpsilon;

	public bool HasStaminaForJump => CurrentStamina >= JumpStaminaCost - StaminaEpsilon;

	private void OnCurrentStaminaChanged( float oldValue, float newValue )
	{
		OnStaminaChanged?.Invoke( CurrentStamina, MaxStamina );
	}

	private void OnMaxStaminaChanged( float oldValue, float newValue )
	{
		OnStaminaChanged?.Invoke( CurrentStamina, MaxStamina );
	}

	private float _defaultRunSpeed = -1f;
	private float _savedJumpSpeed = -1f;
	private double _regenAllowedAfterEmptyTime;
	private double _regenAllowedAfterUseCooldownTime;
	private float _staminaAtAuthorityStepStart;
	private PlayerController _cachedPc;

	protected override void OnStart()
	{
		if ( IsStaminaAuthority() )
		{
			MaxStamina = Math.Max( 1f, MaxStamina );
			CurrentStamina = Math.Clamp( CurrentStamina, 0f, MaxStamina );
			_regenAllowedAfterEmptyTime = 0;
			_regenAllowedAfterUseCooldownTime = 0;
		}
	}

	protected override void OnDisabled()
	{
		var pc = FindPlayerController();
		if ( pc is not null && pc.IsValid() && !pc.IsProxy )
		{
			if ( _defaultRunSpeed >= 0f )
				pc.RunSpeed = _defaultRunSpeed;
			if ( _savedJumpSpeed >= 0f )
				pc.JumpSpeed = _savedJumpSpeed;
		}

		_cachedPc = null;
	}

	void PlayerController.IEvents.OnJumped()
	{
		if ( !IsStaminaAuthority() )
			return;

		CurrentStamina = Math.Max( 0f, CurrentStamina - Math.Max( 0f, JumpStaminaCost ) );
		NotifyStaminaConsumed();
	}

	protected override void OnUpdate()
	{
		var pc = FindPlayerController();
		if ( pc is not null && !pc.IsProxy )
		{
			ApplyRunSpeedGate( pc );
			ApplyJumpSpeedGate( pc );
		}
	}

	protected override void OnFixedUpdate()
	{
		var pc = FindPlayerController();
		if ( pc is not null && !pc.IsProxy )
		{
			ApplyRunSpeedGate( pc );
			ApplyJumpSpeedGate( pc );
		}

		if ( !IsStaminaAuthority() )
			return;

		if ( pc is null )
			return;

		_staminaAtAuthorityStepStart = CurrentStamina;
		var wasAboveEmpty = CurrentStamina > StaminaEpsilon;

		if ( IsConsumingSprintStamina( pc ) )
			CurrentStamina = Math.Max( 0f, CurrentStamina - SprintDrainPerSecond * Time.Delta );

		if ( AnyGrappleSwingStaminaDrainActive( pc.GameObject ) && GrappleSwingStaminaDrainPerSecond > 0f )
			CurrentStamina = Math.Max( 0f, CurrentStamina - GrappleSwingStaminaDrainPerSecond * Time.Delta );

		if ( wasAboveEmpty && CurrentStamina <= StaminaEpsilon )
			_regenAllowedAfterEmptyTime = Time.Now + Math.Max( 0f, StaminaRegenDelayAfterEmptySeconds );

		TryDrainAttackStamina();

		TryApplyContinuousStaminaRegen( pc );

		if ( CurrentStamina < _staminaAtAuthorityStepStart - StaminaEpsilon * 0.5f )
			NotifyStaminaConsumed();
	}

	private void TryApplyContinuousStaminaRegen( PlayerController pc )
	{
		if ( Time.Now < _regenAllowedAfterEmptyTime )
			return;

		if ( Time.Now < _regenAllowedAfterUseCooldownTime )
			return;

		if ( !CanRegenStaminaStandOrWalk( pc ) )
			return;

		var missing = MaxStamina - CurrentStamina;
		if ( missing <= StaminaEpsilon )
			return;

		// blend(u) = baseline + (1-baseline)*u^p ; rate = peak*blend ; ∫_0^M dS/rate = fill ⇒ peak = (M/fill)*∫_0^1 du/blend(u).
		var maxV = Math.Max( MaxStamina, 1f );
		var fill = Math.Max( 0.1f, StaminaRegenFillTimeSeconds );
		var t = Math.Clamp( CurrentStamina / maxV, 0f, 1f );
		var p = Math.Max( 1.02f, StaminaRegenAccelerationExponent );
		var baseline = Math.Clamp( StaminaRegenBaselineWeight, 0.08f, 0.92f );
		var invIntegral = IntegrateInverseRegenBlend( baseline, p );
		var peakPerSecond = maxV / fill * invIntegral;
		var surge = MathF.Pow( t, p );
		var blend = baseline + (1f - baseline) * surge;
		var gain = peakPerSecond * blend * Time.Delta;
		CurrentStamina = Math.Min( maxV, CurrentStamina + gain );
	}

	/// <summary>∫₀¹ du / (baseline + (1-baseline)*u^p) so 0→max refill time matches <see cref="StaminaRegenFillTimeSeconds"/>.</summary>
	private static float IntegrateInverseRegenBlend( float baseline, float p )
	{
		const int steps = 64;
		var du = 1f / steps;
		var sum = 0f;
		for ( var i = 0; i <= steps; i++ )
		{
			var u = i * du;
			var blend = baseline + (1f - baseline) * MathF.Pow( u, p );
			var f = 1f / MathF.Max( blend, 0.001f );
			var w = i is 0 or steps ? 0.5f : 1f;
			sum += w * f;
		}

		return sum * du;
	}

	/// <summary>Regen only when landed on walkable ground, not grappling / swimming / climbing, and not holding sprint.</summary>
	private bool CanRegenStaminaStandOrWalk( PlayerController pc )
	{
		if ( pc is null || !pc.IsValid() )
			return false;

		if ( !pc.IsOnGround )
			return false;

		if ( pc.IsSwimming || pc.IsClimbing )
			return false;

		if ( IsAnyGrappleActive( pc.GameObject ) )
			return false;

		if ( WantsRunSpeedInput( pc ) )
			return false;

		return true;
	}

	private void NotifyStaminaConsumed()
	{
		if ( !IsStaminaAuthority() )
			return;

		_regenAllowedAfterUseCooldownTime = Time.Now + Math.Max( 0f, StaminaRegenCooldownAfterUseSeconds );
	}

	private void ApplyJumpSpeedGate( PlayerController pc )
	{
		if ( _savedJumpSpeed < 0f )
			_savedJumpSpeed = Math.Max( pc.JumpSpeed, 0f );

		if ( HasStaminaForJump )
			pc.JumpSpeed = _savedJumpSpeed;
		else
		{
			var h = Math.Clamp( OutOfStaminaJumpHeightFraction, 0.05f, 1f );
			pc.JumpSpeed = _savedJumpSpeed * MathF.Sqrt( h );
		}
	}

	private void ApplyRunSpeedGate( PlayerController pc )
	{
		if ( _defaultRunSpeed < 0f )
			_defaultRunSpeed = Math.Max( pc.RunSpeed, 1f );

		if ( CurrentStamina <= StaminaEpsilon )
			pc.RunSpeed = pc.WalkSpeed;
		else
			pc.RunSpeed = _defaultRunSpeed;
	}

	public static bool HasStaminaToStartGrapple( GameObject playerRoot )
	{
		var s = FindForPlayerRoot( playerRoot );
		return s is null || s.HasStaminaForGrappleAttach;
	}

	public static void ApplyGrappleAttachCost( GameObject playerRoot )
	{
		var s = FindForPlayerRoot( playerRoot );
		if ( s is null || !IsStaminaAuthority() )
			return;

		s.CurrentStamina = Math.Max( 0f, s.CurrentStamina - Math.Max( 0f, s.GrappleStartStaminaCost ) );
	}

	public static PlayerStamina FindForPlayerRoot( GameObject start )
	{
		if ( start is null || !start.IsValid() )
			return null;

		for ( var go = start; go is not null; go = go.Parent )
		{
			var s = go.Components.Get<PlayerStamina>();
			if ( s is not null )
				return s;
		}

		return start.Components.Get<PlayerStamina>();
	}

	public static void StopAllGrapplesUnder( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			return;

		StopGrappleOnObject<MoveModeGrapple>( root );
		StopGrappleOnObject<MoveModeGrapple1>( root );
		StopGrappleOnObject<MoveModeGrapple2>( root );
	}

	private static void StopGrappleOnObject<T>( GameObject root ) where T : Component, IGrappleStop
	{
		foreach ( var go in EnumerateHierarchy( root ) )
		{
			var g = go.Components.Get<T>();
			if ( g is not null && g.IsGrappling )
				g.StopGrapple();
		}
	}

	private static IEnumerable<GameObject> EnumerateHierarchy( GameObject root )
	{
		yield return root;

		foreach ( var child in root.Children )
		{
			foreach ( var d in EnumerateHierarchy( child ) )
				yield return d;
		}
	}

	private bool IsAnyGrappleActive( GameObject playerRoot )
	{
		if ( playerRoot is null || !playerRoot.IsValid() )
			return false;

		foreach ( var go in EnumerateHierarchy( playerRoot ) )
		{
			if ( go.Components.Get<MoveModeGrapple>() is { IsGrappling: true } )
				return true;
			if ( go.Components.Get<MoveModeGrapple1>() is { IsGrappling: true } )
				return true;
			if ( go.Components.Get<MoveModeGrapple2>() is { IsGrappling: true } )
				return true;
		}

		return false;
	}

	private static bool AnyGrappleSwingStaminaDrainActive( GameObject playerRoot )
	{
		if ( playerRoot is null || !playerRoot.IsValid() )
			return false;

		foreach ( var go in EnumerateHierarchy( playerRoot ) )
		{
			if ( go.Components.Get<MoveModeGrapple>() is IGrappleStop { IsGrappling: true, GrappleSwingStaminaDrainActive: true } )
				return true;
			if ( go.Components.Get<MoveModeGrapple1>() is IGrappleStop { IsGrappling: true, GrappleSwingStaminaDrainActive: true } )
				return true;
			if ( go.Components.Get<MoveModeGrapple2>() is IGrappleStop { IsGrappling: true, GrappleSwingStaminaDrainActive: true } )
				return true;
		}

		return false;
	}

	private void TryDrainAttackStamina()
	{
		if ( !Input.Pressed( AttackButton ) )
			return;

		if ( !HasStaminaForActions )
			return;

		var pickup = FindPlayerItemPickup();
		var held = pickup?.HeldRoot;
		if ( held is null || !held.IsValid() )
			return;

		var melee = FindMeleeWeaponUnder( held );
		if ( melee is null || melee.AttackStaminaCost <= 0f )
			return;

		CurrentStamina = Math.Max( 0f, CurrentStamina - melee.AttackStaminaCost );
	}

	private static bool IsStaminaAuthority()
	{
		if ( !Networking.IsActive )
			return true;

		return Networking.IsHost;
	}

	private bool WantsRunSpeedInput( PlayerController pc )
	{
		if ( pc is null || !pc.IsValid() || !pc.UseInputControls )
			return false;

		// Default Input.config uses Title Case ("Run"); scenes often serialize "run".
		var alt = string.IsNullOrEmpty( pc.AltMoveButton ) ? "Run" : pc.AltMoveButton;
		var altHeld = GameMovementInput.InputDownFlexible( alt );

		if ( pc.RunByDefault )
			return !altHeld;

		return altHeld;
	}

	private bool HasSprintLocomotionForDrain( PlayerController pc )
	{
		var wish = pc.WishVelocity;
		var hz = new Vector3( wish.x, 0f, wish.z );
		if ( hz.Length >= SprintDrainMinHorizontalWish )
			return true;

		var m = Input.AnalogMove;
		if ( new Vector2( m.x, m.y ).Length >= SprintDrainMinAnalogMove )
			return true;

		if ( GameMovementInput.AnyMoveKeyDown() )
			return true;

		var vel = pc.Velocity;
		var vh = new Vector3( vel.x, 0f, vel.z );
		var minSpeed = MathF.Max( SprintDrainMinHorizontalSpeed, pc.WalkSpeed * 0.32f );
		return vh.Length >= minSpeed;
	}

	private bool IsConsumingSprintStamina( PlayerController pc )
		=> !IsAnyGrappleActive( pc.GameObject ) && WantsRunSpeedInput( pc ) && HasSprintLocomotionForDrain( pc ) && CurrentStamina > StaminaEpsilon;

	private PlayerController FindPlayerController()
	{
		if ( _cachedPc is not null && _cachedPc.IsValid() )
			return _cachedPc;

		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is not null )
				return _cachedPc = pc;
		}

		return _cachedPc = GameObject.Components.Get<PlayerController>();
	}

	private PlayerItemPickup FindPlayerItemPickup()
	{
		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var p = go.Components.Get<PlayerItemPickup>();
			if ( p is not null )
				return p;
		}

		return GameObject.Components.Get<PlayerItemPickup>();
	}

	private static MeleeWeapon FindMeleeWeaponUnder( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			return null;

		var m = root.Components.Get<MeleeWeapon>();
		if ( m is not null )
			return m;

		foreach ( var go in EnumerateDescendants( root ) )
		{
			var mm = go.Components.Get<MeleeWeapon>();
			if ( mm is not null )
				return mm;
		}

		return null;
	}

	private static IEnumerable<GameObject> EnumerateDescendants( GameObject root )
	{
		foreach ( var child in root.Children )
		{
			yield return child;
			foreach ( var d in EnumerateDescendants( child ) )
				yield return d;
		}
	}
}

public interface IGrappleStop
{
	bool IsGrappling { get; }

	void StopGrapple();

	bool GrappleSwingStaminaDrainActive { get; }
}
