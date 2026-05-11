using System;
using System.Collections.Generic;
using Sandbox;

namespace Game;

[Title( "Entity Stamina Feature" )]
[Category( "Entity" )]
public class EntityStaminaFeature : Component, PlayerController.IEvents
{
	private const float StaminaEpsilon = 0.001f;

	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnMaxStaminaChanged ) )]
	public float MaxStamina { get; set; } = 100f;

	[Sync( SyncFlags.FromHost ), Change( nameof( OnCurrentStaminaChanged ) )]
	public float CurrentStamina { get; set; } = 100f;

	[Property] public bool EnabledForThisEntity { get; set; } = true;

	[Property] public float StaminaRegenDelayAfterEmptySeconds { get; set; } = 2f;
	[Property] public float StaminaRegenCooldownAfterUseSeconds { get; set; } = 1f;
	[Property] public float StaminaRegenFillTimeSeconds { get; set; } = 3f;
	[Property] public float StaminaRegenAccelerationExponent { get; set; } = 3f;
	[Property] public float StaminaRegenBaselineWeight { get; set; } = 0.28f;
	[Property] public float SprintDrainPerSecond { get; set; } = 12f;
	[Property] public float SprintDrainMinHorizontalWish { get; set; } = 2f;
	[Property] public float SprintDrainMinAnalogMove { get; set; } = 0.02f;
	[Property] public float SprintDrainMinHorizontalSpeed { get; set; } = 55f;
	[Property] public float GrappleStartStaminaCost { get; set; } = 5f;
	[Property] public float GrappleSwingStaminaDrainPerSecond { get; set; } = 2f;
	[Property] public string AttackButton { get; set; } = "attack1";
	[Property] public float JumpStaminaCost { get; set; } = 5f;
	[Property] public float OutOfStaminaJumpHeightFraction { get; set; } = 0.5f;
	[Property] public int StaminaBarTickDivisions { get; set; } = 10;

	public event Action<float, float> OnStaminaChanged;

	public float StaminaFraction => MaxStamina > 0.001f ? Math.Clamp( CurrentStamina / MaxStamina, 0f, 1f ) : 0f;
	public bool HasStaminaForActions => !EnabledForThisEntity || CurrentStamina > StaminaEpsilon;
	public bool HasStaminaForGrappleAttach => !EnabledForThisEntity || CurrentStamina >= GrappleStartStaminaCost - StaminaEpsilon;
	public bool HasStaminaForJump => !EnabledForThisEntity || CurrentStamina >= JumpStaminaCost - StaminaEpsilon;

	private float _defaultRunSpeed = -1f;
	private float _savedJumpSpeed = -1f;
	private double _regenAllowedAfterEmptyTime;
	private double _regenAllowedAfterUseCooldownTime;
	private float _staminaAtAuthorityStepStart;
	private PlayerController _cachedPc;

	protected override void OnStart()
	{
		var core = EntityCore.FindOnHierarchy( GameObject );
		if ( core is not null )
			EnabledForThisEntity = core.EnableStamina;

		if ( IsAuthority() )
		{
			MaxStamina = Math.Max( 1f, MaxStamina );
			CurrentStamina = Math.Clamp( CurrentStamina, 0f, MaxStamina );
		}
	}

	protected override void OnDisabled()
	{
		var pc = FindPlayerController();
		if ( pc is not null && pc.IsValid() && !pc.IsProxy )
		{
			if ( _defaultRunSpeed >= 0f ) pc.RunSpeed = _defaultRunSpeed;
			if ( _savedJumpSpeed >= 0f ) pc.JumpSpeed = _savedJumpSpeed;
		}
		_cachedPc = null;
	}

	void PlayerController.IEvents.OnJumped()
	{
		if ( !EnabledForThisEntity || !IsAuthority() )
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

		if ( !EnabledForThisEntity || !IsAuthority() || pc is null )
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

	private void OnCurrentStaminaChanged( float oldValue, float newValue ) => OnStaminaChanged?.Invoke( CurrentStamina, MaxStamina );
	private void OnMaxStaminaChanged( float oldValue, float newValue ) => OnStaminaChanged?.Invoke( CurrentStamina, MaxStamina );

	private static bool IsAuthority()
	{
		if ( !Networking.IsActive ) return true;
		return Networking.IsHost;
	}

	public static EntityStaminaFeature FindForEntityRoot( GameObject start )
	{
		if ( start is null || !start.IsValid() ) return null;
		for ( var go = start; go is not null; go = go.Parent )
		{
			var s = go.Components.Get<EntityStaminaFeature>();
			if ( s is not null ) return s;
		}
		return start.Components.Get<EntityStaminaFeature>();
	}

	public static bool HasStaminaToStartGrapple( GameObject entityRoot )
	{
		var s = FindForEntityRoot( entityRoot );
		return s is null || s.HasStaminaForGrappleAttach;
	}

	public static void ApplyGrappleAttachCost( GameObject entityRoot )
	{
		var s = FindForEntityRoot( entityRoot );
		if ( s is null || !IsAuthority() || !s.EnabledForThisEntity ) return;
		s.CurrentStamina = Math.Max( 0f, s.CurrentStamina - Math.Max( 0f, s.GrappleStartStaminaCost ) );
	}

	private void TryDrainAttackStamina()
	{
		if ( !Input.Pressed( AttackButton ) || !HasStaminaForActions )
			return;
		var pickup = FindPlayerItemPickup();
		var held = pickup?.HeldRoot;
		if ( held is null || !held.IsValid() ) return;
		var melee = FindMeleeWeaponUnder( held );
		if ( melee is null || melee.AttackStaminaCost <= 0f ) return;
		CurrentStamina = Math.Max( 0f, CurrentStamina - melee.AttackStaminaCost );
	}

	private void TryApplyContinuousStaminaRegen( PlayerController pc )
	{
		if ( Time.Now < _regenAllowedAfterEmptyTime || Time.Now < _regenAllowedAfterUseCooldownTime ) return;
		if ( !CanRegenStaminaStandOrWalk( pc ) ) return;
		var missing = MaxStamina - CurrentStamina;
		if ( missing <= StaminaEpsilon ) return;

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

	private bool CanRegenStaminaStandOrWalk( PlayerController pc )
	{
		if ( pc is null || !pc.IsValid() ) return false;
		if ( !pc.IsOnGround || pc.IsSwimming || pc.IsClimbing ) return false;
		if ( IsAnyGrappleActive( pc.GameObject ) ) return false;
		if ( WantsRunSpeedInput( pc ) ) return false;
		return true;
	}

	private void NotifyStaminaConsumed() => _regenAllowedAfterUseCooldownTime = Time.Now + Math.Max( 0f, StaminaRegenCooldownAfterUseSeconds );

	private void ApplyJumpSpeedGate( PlayerController pc )
	{
		if ( _savedJumpSpeed < 0f ) _savedJumpSpeed = Math.Max( pc.JumpSpeed, 0f );
		if ( !EnabledForThisEntity || HasStaminaForJump )
			pc.JumpSpeed = _savedJumpSpeed;
		else
			pc.JumpSpeed = _savedJumpSpeed * MathF.Sqrt( Math.Clamp( OutOfStaminaJumpHeightFraction, 0.05f, 1f ) );
	}

	private void ApplyRunSpeedGate( PlayerController pc )
	{
		if ( _defaultRunSpeed < 0f ) _defaultRunSpeed = Math.Max( pc.RunSpeed, 1f );
		if ( EnabledForThisEntity && CurrentStamina <= StaminaEpsilon )
			pc.RunSpeed = pc.WalkSpeed;
		else
			pc.RunSpeed = _defaultRunSpeed;
	}

	private bool WantsRunSpeedInput( PlayerController pc )
	{
		if ( pc is null || !pc.IsValid() || !pc.UseInputControls ) return false;
		var alt = string.IsNullOrEmpty( pc.AltMoveButton ) ? "Run" : pc.AltMoveButton;
		var altHeld = GameMovementInput.InputDownFlexible( alt );
		return pc.RunByDefault ? !altHeld : altHeld;
	}

	private bool HasSprintLocomotionForDrain( PlayerController pc )
	{
		var wish = pc.WishVelocity;
		var hz = new Vector3( wish.x, 0f, wish.z );
		if ( hz.Length >= SprintDrainMinHorizontalWish ) return true;
		var m = Input.AnalogMove;
		if ( new Vector2( m.x, m.y ).Length >= SprintDrainMinAnalogMove ) return true;
		if ( GameMovementInput.AnyMoveKeyDown() ) return true;
		var vh = new Vector3( pc.Velocity.x, 0f, pc.Velocity.z );
		var minSpeed = MathF.Max( SprintDrainMinHorizontalSpeed, pc.WalkSpeed * 0.32f );
		return vh.Length >= minSpeed;
	}

	private bool IsConsumingSprintStamina( PlayerController pc )
		=> EnabledForThisEntity && !IsAnyGrappleActive( pc.GameObject ) && WantsRunSpeedInput( pc ) && HasSprintLocomotionForDrain( pc ) && CurrentStamina > StaminaEpsilon;

	private static bool IsAnyGrappleActive( GameObject entityRoot )
	{
		if ( entityRoot is null || !entityRoot.IsValid() ) return false;
		foreach ( var go in EnumerateHierarchy( entityRoot ) )
		{
			if ( go.Components.Get<MoveModeGrapple>() is { IsGrappling: true } ) return true;
			if ( go.Components.Get<MoveModeGrapple1>() is { IsGrappling: true } ) return true;
			if ( go.Components.Get<MoveModeGrapple2>() is { IsGrappling: true } ) return true;
		}
		return false;
	}

	private static bool AnyGrappleSwingStaminaDrainActive( GameObject entityRoot )
	{
		if ( entityRoot is null || !entityRoot.IsValid() ) return false;
		foreach ( var go in EnumerateHierarchy( entityRoot ) )
		{
			if ( go.Components.Get<MoveModeGrapple>() is IGrappleStop { IsGrappling: true, GrappleSwingStaminaDrainActive: true } ) return true;
			if ( go.Components.Get<MoveModeGrapple1>() is IGrappleStop { IsGrappling: true, GrappleSwingStaminaDrainActive: true } ) return true;
			if ( go.Components.Get<MoveModeGrapple2>() is IGrappleStop { IsGrappling: true, GrappleSwingStaminaDrainActive: true } ) return true;
		}
		return false;
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

	private PlayerController FindPlayerController()
	{
		if ( _cachedPc is not null && _cachedPc.IsValid() ) return _cachedPc;
		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is not null ) return _cachedPc = pc;
		}
		return _cachedPc = GameObject.Components.Get<PlayerController>();
	}

	private PlayerItemPickup FindPlayerItemPickup()
	{
		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var p = go.Components.Get<PlayerItemPickup>();
			if ( p is not null ) return p;
		}
		return GameObject.Components.Get<PlayerItemPickup>();
	}

	private static MeleeWeapon FindMeleeWeaponUnder( GameObject root )
	{
		if ( root is null || !root.IsValid() ) return null;
		var m = root.Components.Get<MeleeWeapon>();
		if ( m is not null ) return m;
		foreach ( var go in EnumerateDescendants( root ) )
		{
			var mm = go.Components.Get<MeleeWeapon>();
			if ( mm is not null ) return mm;
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
