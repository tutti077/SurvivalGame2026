using System;
using Sandbox;
using Sandbox.Citizen;

namespace Survival;

public partial class PlayerCombat
{
	[Property, Group( "Combat — Stagger" ), Title( "Play falling pose while staggered" )]
	public bool StaggerPlayFallingPose { get; set; } = true;

	float _staggerRemainingSeconds;
	MeleeStaggerTier _activeStaggerTier;
	CitizenAnimationHelper _staggerAnimHelper;
	bool _staggerAnimHelperResolved;

	public bool IsStaggered => _staggerRemainingSeconds > 1e-4f;
	public MeleeStaggerTier ActiveStaggerTier => IsStaggered ? _activeStaggerTier : MeleeStaggerTier.None;
	public float StaggerRemainingSeconds => Math.Max( 0f, _staggerRemainingSeconds );

	internal void ServerTickMeleeStagger()
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		if ( _staggerRemainingSeconds <= 0f )
			return;

		_staggerRemainingSeconds = MathF.Max( 0f, _staggerRemainingSeconds - Time.Delta );
		if ( _staggerRemainingSeconds > 1e-4f )
			return;

		_activeStaggerTier = MeleeStaggerTier.None;
		BroadcastStaggerState( 0f, MeleeStaggerTier.None );
	}

	internal void TickLocalMeleeStaggerPresentation()
	{
		if ( _staggerRemainingSeconds > 0f && !IsServerSideForMeleeAuthority() )
			_staggerRemainingSeconds = MathF.Max( 0f, _staggerRemainingSeconds - Time.Delta );

		UpdateStaggerFallingPose( IsStaggered );
	}

	/// <summary>Host: apply JSON block outcome (HP / stamina / light stagger duration) then end the block hold.</summary>
	internal void ServerApplyBlockOutcome( in MeleeBlockOutcome outcome, Component attacker )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is not null )
		{
			if ( outcome.StaminaCost > 1e-4f )
				vitals.TrySpendStamina( outcome.StaminaCost );

			if ( outcome.HealthDamage > 1e-4f )
				vitals.ApplyDamageAfterArmor( outcome.HealthDamage, attacker );
		}

		if ( outcome.HasStagger )
			ServerBeginStagger( outcome.Tier, outcome.DurationSeconds );
		else if ( IsStaggered )
			ServerBeginStagger( MeleeStaggerTier.None, 0f );

		vitals?.ApplyMeleeStagger( outcome.DurationSeconds );
		ConsumeAuthoritativeMeleeBlock( attackWasHeavy: outcome.OutcomeId.StartsWith( "heavy", StringComparison.OrdinalIgnoreCase ) );

		Log.Info(
			$"[MeleeBlock] outcome {GameObject.Name}: {outcome.OutcomeId} tier={outcome.Tier} "
			+ $"duration={outcome.DurationSeconds:0.###}s hp={outcome.HealthDamage:0.#} stam={outcome.StaminaCost:0.#} parry={outcome.WasPerfectParry}" );
	}

	internal void ServerBeginStagger( MeleeStaggerTier tier, float durationSeconds )
	{
		if ( !IsServerSideForMeleeAuthority() )
			return;

		durationSeconds = Math.Max( 0f, durationSeconds );
		if ( durationSeconds <= 1e-4f || tier == MeleeStaggerTier.None )
		{
			_staggerRemainingSeconds = 0f;
			_activeStaggerTier = MeleeStaggerTier.None;
			BroadcastStaggerState( 0f, MeleeStaggerTier.None );
			return;
		}

		// Refresh if a stronger/longer stagger lands while already staggered.
		if ( durationSeconds >= _staggerRemainingSeconds - 1e-4f )
		{
			_staggerRemainingSeconds = durationSeconds;
			_activeStaggerTier = tier;
		}

		BroadcastStaggerState( _staggerRemainingSeconds, _activeStaggerTier );

		if ( IsLocalCombatDriver() )
			CancelOwnerPrimarySwingPhaseIfActive();
	}

	void BroadcastStaggerState( float remainingSeconds, MeleeStaggerTier tier )
	{
		if ( GameObject.Network is { Active: true } )
			RpcBroadcastMeleeStagger( remainingSeconds, (byte)tier );
		else
			ApplyLocalStaggerPresentation( remainingSeconds, tier );
	}

	[Rpc.Broadcast]
	void RpcBroadcastMeleeStagger( float remainingSeconds, byte tierByte )
	{
		var tier = tierByte switch
		{
			(byte)MeleeStaggerTier.Heavy => MeleeStaggerTier.Heavy,
			(byte)MeleeStaggerTier.Light => MeleeStaggerTier.Light,
			_ => MeleeStaggerTier.None
		};
		ApplyLocalStaggerPresentation( remainingSeconds, tier );
	}

	void ApplyLocalStaggerPresentation( float remainingSeconds, MeleeStaggerTier tier )
	{
		_staggerRemainingSeconds = Math.Max( 0f, remainingSeconds );
		_activeStaggerTier = _staggerRemainingSeconds > 1e-4f ? tier : MeleeStaggerTier.None;
		if ( IsLocalCombatDriver() && IsStaggered )
			CancelOwnerPrimarySwingPhaseIfActive();
	}

	void EnsureStaggerAnimHelper()
	{
		if ( _staggerAnimHelperResolved )
			return;

		_staggerAnimHelperResolved = true;
		_staggerAnimHelper = Components.Get<CitizenAnimationHelper>( FindMode.EverythingInSelfAndDescendants );
		if ( _staggerAnimHelper is not null && _staggerAnimHelper.IsValid() )
			return;

		var body = Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		if ( body is null || !body.IsValid() )
			return;

		_staggerAnimHelper = body.Components.Get<CitizenAnimationHelper>();
	}

	void UpdateStaggerFallingPose( bool staggered )
	{
		if ( !StaggerPlayFallingPose )
			return;

		EnsureStaggerAnimHelper();
		if ( _staggerAnimHelper is not null && _staggerAnimHelper.IsValid() )
		{
			if ( staggered )
			{
				_staggerAnimHelper.IsGrounded = false;
				return;
			}

			var controller = Components.Get<PlayerController>();
			_staggerAnimHelper.IsGrounded = controller is null || !controller.IsValid() || controller.IsOnGround;
			return;
		}

		// Fallback when PlayerController owns citizen anim without a helper on the pawn.
		var renderer = Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		if ( renderer is null || !renderer.IsValid() )
			return;

		renderer.Set( "b_grounded", !staggered && IsControllerOnGround() );
	}

	bool IsControllerOnGround()
	{
		var controller = Components.Get<PlayerController>();
		return controller is null || !controller.IsValid() || controller.IsOnGround;
	}

	void CancelOwnerPrimarySwingPhaseIfActive()
	{
		if ( !_primarySwingPhaseActive )
			return;

		_primarySwingPhaseActive = false;
		_primaryPostReleaseDragAccum = default;
	}
}
