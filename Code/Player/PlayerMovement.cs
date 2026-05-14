using System;

namespace Survival;

/// <summary>
/// Attach next to <see cref="PlayerVitals"/> on the pawn root. Handles <see cref="PlayerController.IEvents"/> (jump input / jump–land) and sprint stamina:
/// stamina drains visually every frame while sprint is held (local preview); <b>one</b> <see cref="PlayerVitals.RequestVitalsDelta"/> on release syncs the total to authority (no per-frame host/RPC spam).
/// </summary>
[Title( "Player Movement" )]
public sealed class PlayerMovement : Component, PlayerController.IEvents
{
	[Property, Group( "Stamina - Jump" )] public float JumpStaminaCost { get; set; } = 5f;

	/// <summary>
	/// Jump-height fraction used when stamina is too low to afford <see cref="JumpStaminaCost"/>. 1 = full jump, 0 = block jump.
	/// </summary>
	[Property, Group( "Stamina - Jump" )] public float ExhaustedJumpHeightFraction { get; set; } = 0.333f;

	/// <summary>Input cleared in <see cref="PreInput"/> when <see cref="JumpStaminaCost"/> is positive and stamina cannot pay the full cost.</summary>
	[Property, Group( "Stamina - Jump" )] public string JumpInputAction { get; set; } = "jump";

	/// <summary>Usually matches <see cref="PlayerController.AltMoveButton"/> when <c>RunByDefault</c> is off.</summary>
	[Property, Group( "Stamina - Sprint" )] public string SprintInputAction { get; set; } = "run";

	[Property, Group( "Stamina - Sprint" )] public float SprintStaminaPerSecond { get; set; } = 2f;

	/// <summary>
	/// Stamina at or below this counts as "exhausted" (sprint blocked). Keep this above tiny per-frame regen to avoid flicker around zero.
	/// </summary>
	[Property, Group( "Stamina - Sprint" )] public float ExhaustedStaminaEpsilon { get; set; } = 0.25f;

	/// <summary>
	/// Optional per-player stamina regen delay override in seconds. Use a value >= 0 to override
	/// <see cref="VitalsAuthority.StaminaRegenDelaySeconds"/> for this pawn; negative values use authority default.
	/// </summary>
	[Property, Group( "Stamina - Regen" )] public float StaminaRegenDelayOverrideSeconds { get; set; } = -1f;

	PlayerVitals _vitals;
	bool _sprintWasDown;
	float _sprintDebtPending;

	/// <summary>Host copy of the owning client’s sprint button, for <see cref="ShouldBlockStaminaRegenForAuthority"/> (local driver uses <see cref="Sandbox.Input"/> directly).</summary>
	bool _sprintHeldReportedOnHost;

	bool _sprintHeldReportedToHostLast;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		if ( _vitals is null )
			Log.Warning( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: add PlayerVitals on this pawn — movement stamina hooks disabled." );
	}

	bool IsLocalMovementDriver()
	{
		if ( GameObject.IsProxy )
			return false;

		if ( GameObject.Network is { Active: true } n && !n.IsOwner )
			return false;

		return true;
	}

	/// <summary>Pulls accumulated sprint preview debt and clears it. Merged into negative stamina on <see cref="PlayerVitals.RequestVitalsDelta"/> / <see cref="VitalsAuthority.TryApplyDeltas"/>.</summary>
	public float TakePendingSprintStaminaDebt()
	{
		var d = _sprintDebtPending;
		_sprintDebtPending = 0f;
		return d;
	}

	/// <summary>Unsynced sprint preview total (authority pool estimate ≈ <see cref="PlayerVitals.CurrentStamina"/> + this).</summary>
	public float PeekPendingSprintStaminaDebt() => _sprintDebtPending;

	/// <summary>Stamina regen on the host must not run while this pawn is sprinting here — authority stamina can lag behind preview until sprint flush / merged spends.</summary>
	public bool ShouldBlockStaminaRegenForAuthority()
	{
		if ( string.IsNullOrWhiteSpace( SprintInputAction ) || SprintStaminaPerSecond <= 0f || _vitals is null )
			return false;
		if ( IsLocalMovementDriver() )
			return Input.Down( SprintInputAction ) && !_vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon );
		return _sprintHeldReportedOnHost;
	}

	public void PreInput()
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		if ( JumpStaminaCost > 0f
		     && !_vitals.CanAffordStamina( JumpStaminaCost )
		     && ExhaustedJumpHeightFraction <= 0f )
			PlayerVitals.ClearJumpInputIfPressed( JumpInputAction );

		if ( !string.IsNullOrWhiteSpace( SprintInputAction )
		     && SprintStaminaPerSecond > 0f
		     && _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) )
			ClearActionIfPressed( SprintInputAction );
	}

	public void OnJumped()
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		if ( _vitals.OnControllerJumpedForStaminaFromMovement( JumpStaminaCost, ExhaustedJumpHeightFraction ) )
			ApplyExhaustedJumpVelocityScale();
	}

	public void OnLanded( float distance, Vector3 impactVelocity )
	{
		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		_vitals.OnControllerLandedForJumpStaminaFromMovement( distance, impactVelocity );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( !IsLocalMovementDriver() || _vitals is null )
			return;

		UpdateSprintStaminaHoldAndFlushOnRelease();
	}

	void UpdateSprintStaminaHoldAndFlushOnRelease()
	{
		if ( string.IsNullOrWhiteSpace( SprintInputAction ) || SprintStaminaPerSecond <= 0f )
		{
			if ( GameObject.Network is { Active: true } && !Networking.IsHost && _sprintHeldReportedToHostLast )
			{
				_sprintHeldReportedToHostLast = false;
				RpcSprintHeldForRegen( false );
			}

			if ( _sprintWasDown )
				FlushSprintStaminaDebt( "sprint action cleared" );
			_sprintWasDown = false;
			return;
		}

		var wantsSprint = Input.Down( SprintInputAction );
		var sprintAllowed = wantsSprint && !_vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon );
		var reportHeld = sprintAllowed;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost && reportHeld != _sprintHeldReportedToHostLast )
		{
			_sprintHeldReportedToHostLast = reportHeld;
			RpcSprintHeldForRegen( reportHeld );
		}

		if ( !sprintAllowed )
		{
			if ( _sprintWasDown )
				FlushSprintStaminaDebt( wantsSprint && _vitals.IsStaminaExhausted( ExhaustedStaminaEpsilon ) ? "stamina exhausted" : "sprint released" );
			_sprintWasDown = false;
			return;
		}

		if ( !_sprintWasDown && _vitals.LogVitalsNetworking )
			Log.Info( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: sprint held ({SprintInputAction}) — local drain {SprintStaminaPerSecond:0.#}/s, authority sync on release only" );

		var d = SprintStaminaPerSecond * Time.Delta;
		var applied = Math.Min( d, Math.Max( 0f, _vitals.CurrentStamina ) );
		if ( applied > 1e-6f )
		{
			_sprintDebtPending += applied;
			_vitals.ApplyLocalStaminaSprintPreviewSpend( applied );
		}

		_sprintWasDown = true;
	}

	static void ClearActionIfPressed( string action )
	{
		if ( string.IsNullOrWhiteSpace( action ) )
			return;

		if ( !Input.Pressed( action ) && !Input.Down( action ) )
			return;

		Input.SetAction( action, false );
		Input.ReleaseAction( action );
	}

	void ApplyExhaustedJumpVelocityScale()
	{
		var body = Components.Get<Rigidbody>();
		if ( body is null )
			return;

		var scale = Math.Clamp( ExhaustedJumpHeightFraction, 0f, 1f );
		if ( scale >= 0.999f )
			return;

		var up = Vector3.Up;
		var upwardSpeed = Vector3.Dot( body.Velocity, up );
		if ( upwardSpeed <= 1e-4f )
			return;

		var reducedUpwardSpeed = upwardSpeed * scale;
		body.Velocity += up * (reducedUpwardSpeed - upwardSpeed);
	}

	[Rpc.Host]
	void RpcSprintHeldForRegen( bool sprintHeld )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		_sprintHeldReportedOnHost = sprintHeld;
	}

	void FlushSprintStaminaDebt( string reason )
	{
		if ( _sprintDebtPending <= 1e-6f )
		{
			_sprintDebtPending = 0f;
			return;
		}

		var debt = _sprintDebtPending;

		if ( !_vitals.MayIssueVitalsDelta() )
		{
			_sprintDebtPending = 0f;
			return;
		}

		_sprintDebtPending = 0f;

		// Do not use TrySpendStamina: preview already reduced CurrentStamina each frame; host authority still had full pool until this delta.
		if ( !_vitals.RequestVitalsDelta( 0f, -debt, mergePendingSprintDebt: false ) )
		{
			_vitals.RestoreLocalStaminaAfterFailedSprintSpend( debt );
			if ( _vitals.LogVitalsNetworking )
				Log.Warning( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: {reason} — sprint stamina −{debt:0.###} rejected by authority (restored preview)" );
			return;
		}

		if ( _vitals.LogVitalsNetworking )
			Log.Info( $"[PlayerMovement|{PlayerVitals.GetVitalsProcessRoleTag( GameObject )}] {GameObject.Name}: {reason} — synced sprint stamina −{debt:0.###} → ST={_vitals.CurrentStamina:0.#}/{_vitals.CurrentStaminaMax:0.#}" );
	}

	protected override void OnDestroy()
	{
		if ( _vitals is not null && ( _sprintWasDown || _sprintDebtPending > 1e-6f ) )
			FlushSprintStaminaDebt( "destroyed" );
		base.OnDestroy();
	}
}
