using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Press: start attack clip and freeze on a windup pose while held.
/// Tap (release before freeze): play through windup→swing continuously.
/// Release after hold: unfreeze and finish the clip (slower when heavy).
/// </summary>
public sealed partial class PlayerAnimation
{
	[Property, Group( "Animation" ), Title( "Windup pose settle (s)" ), Description( "How long the attack clip advances before freezing while Attack1 is held." ), Range( 0.02f, 0.25f ), Step( 0.01f )]
	public float MeleeWindupPoseSettleSeconds { get; set; } = 0.08f;

	[Property, Group( "Animation" ), Title( "Heavy swing playback rate" ), Range( 0.35f, 1f ), Step( 0.05f )]
	public float MeleeHeavySwingPlaybackRate { get; set; } = 0.55f;

	[Property, Group( "Animation" ), Title( "Swing presentation duration (s)" ), Description( "Wall-clock window from swing start covering the full attack clip including return. Chain-busy waits for this remainder so the next attack cannot cut the anim short." ), Range( 0.35f, 2f ), Step( 0.05f )]
	public float MeleeSwingPresentationSeconds { get; set; } = 0.85f;

	[Property, Group( "Animation" ), Title( "Heavy swing presentation (s)" ), Description( "Wall-clock window for heavy attack clip including return frames. Chain-busy waits for this remainder." ), Range( 0.5f, 2.5f ), Step( 0.05f )]
	public float MeleeHeavySwingPresentationSeconds { get; set; } = 1.25f;

	bool _windupHoldActive;
	bool _windupHoldFrozen;
	bool _windupHoldWantsFreeze;
	byte _windupHoldAttackType;
	double _windupHoldStartedAt;
	bool _ownerSkipNextRemoteSwingApply;
	/// <summary>Attack clip already started on press — host Play must not re-pulse <c>b_attack</c>.</summary>
	bool _meleeSwingClipFromPress;

	public bool IsMeleeWindupHoldActive => _windupHoldActive;

	/// <summary>
	/// Single gate for "a committed swing clip is playing": the presentation window opened at release,
	/// covering the swing plus its return frames. Purely time-bounded so it can never latch busy forever.
	/// Both the next attack and the arc overlay are bound to this.
	/// The press windup hold is deliberately excluded — the attack being *aimed* must still be allowed to fire.
	/// </summary>
	public bool IsMeleeSwingAnimBusy => GetMeleeAttackAnimBusyRemainingSeconds() > 0.01f;

	/// <summary>True while a press-started or committed attack clip owns the pawn (windup hold included).</summary>
	public bool HasActiveMeleeSwingPresentation => _windupHoldActive || IsMeleeSwingAnimBusy;

	float GetSwingPresentationSeconds( bool isHeavy ) => isHeavy
		? Math.Max( 0.5f, MeleeHeavySwingPresentationSeconds )
		: Math.Max( 0.35f, MeleeSwingPresentationSeconds );

	/// <summary>Owner: start the attack clip on press; freeze into a windup pose if they keep holding.</summary>
	public void BeginMeleeAttackWindupHold( byte attackType )
	{
		if ( !PlayMeleeSwingAnimation || !GameObject.IsValid() )
			return;

		// Already in the press windup — do not re-pulse b_attack (causes a visible restart stutter).
		if ( _windupHoldActive )
		{
			_windupHoldAttackType = attackType;
			return;
		}

		if ( _combatSequenceActive )
			ClearCombatSequencePose();

		EnsureAnimTargets();
		ApplyHoldPose( HoldPose.MeleeTwoHand );

		var body = ResolveBody();
		if ( body is null )
			return;

		RestoreLateralSwingPlaybackRate();
		body.UseAnimGraph = true;
		body.Set( "holdtype_attack", 0f );
		body.Set( "b_attack", true );

		_windupHoldActive = true;
		_windupHoldFrozen = false;
		_windupHoldWantsFreeze = true;
		_windupHoldAttackType = attackType;
		_windupHoldStartedAt = Time.NowDouble;
		_meleeAttackAnimBusyUntilSandbox = 0;
		_meleeSwingClipFromPress = true;
		_ownerSkipNextRemoteSwingApply = false;

		Components.Get<PlayerCombat>()?.LogMeleeAnimStart(
			"animgraph b_attack windup (hold-ready)",
			$"type={MeleeAttackTypes.Label( attackType )} settle={MeleeWindupPoseSettleSeconds:0.###}s" );
	}

	/// <summary>
	/// While Attack1 is held, freeze after settle. If released before settle (tap), leave playing.
	/// </summary>
	public void TickMeleeAttackWindupHold( bool attackButtonHeld )
	{
		if ( !_windupHoldActive || _combatSequenceActive )
			return;

		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
			return;

		if ( !attackButtonHeld )
		{
			// Tap path: never freeze — clip keeps playing through.
			_windupHoldWantsFreeze = false;
			return;
		}

		if ( !_windupHoldWantsFreeze )
			return;

		var settle = Math.Max( 0.02f, MeleeWindupPoseSettleSeconds );
		if ( !_windupHoldFrozen && Time.NowDouble - _windupHoldStartedAt >= settle )
		{
			if ( !_lateralSwingPlaybackSlowed )
			{
				_playbackRateSaved = body.PlaybackRate;
				_lateralSwingPlaybackSlowed = true;
			}

			body.PlaybackRate = 0f;
			_windupHoldFrozen = true;
			Components.Get<PlayerCombat>()?.LogMeleeAnimStart(
				"animgraph windup pose FROZEN",
				$"type={MeleeAttackTypes.Label( _windupHoldAttackType )}" );
		}
		else if ( _windupHoldFrozen )
		{
			body.PlaybackRate = 0f;
		}
	}

	/// <summary>Release / host accept: unfreeze and finish the swing (heavy plays slower).</summary>
	public void ReleaseMeleeAttackWindupHold( byte attackType, bool isHeavy )
	{
		if ( !PlayMeleeSwingAnimation || !GameObject.IsValid() )
			return;

		EnsureAnimTargets();
		ApplyHoldPose( HoldPose.MeleeTwoHand );

		var body = ResolveBody();
		if ( body is null )
			return;

		var wasHoldingPose = _windupHoldActive && _windupHoldFrozen;
		// The press already put this clip on screen (playing, or frozen mid-windup). Restoring the
		// playback rate below resumes that same clip — re-pulsing b_attack restarted it from frame 0,
		// which read as a stutter right as the swing began.
		var pressClipOwnsSwing = _windupHoldActive;

		_windupHoldActive = false;
		_windupHoldFrozen = false;
		_windupHoldWantsFreeze = false;
		_meleeSwingClipFromPress = true;
		_ownerSkipNextRemoteSwingApply = true;

		body.UseAnimGraph = true;
		body.Set( "holdtype_attack", 0f );

		// Only pulse when nothing is on screen — committing with no press windup at all used to draw
		// arcs with no swing animation.
		if ( !pressClipOwnsSwing && GetMeleeAttackAnimBusyRemainingSeconds() <= 0.01f )
			body.Set( "b_attack", true );

		ApplySwingPlaybackRate( body, attackType, isHeavy );

		Components.Get<PlayerCombat>()?.LogMeleeAnimStart(
			wasHoldingPose ? "animgraph swing RESUME from windup freeze (no re-pulse)" : "animgraph b_attack play-through (tap)",
			$"type={MeleeAttackTypes.Label( attackType )} heavy={isHeavy} rate={body.PlaybackRate:0.##}" );
	}

	public void CancelMeleeAttackWindupHold()
	{
		if ( !_windupHoldActive && !_meleeSwingClipFromPress )
			return;

		_windupHoldActive = false;
		_windupHoldFrozen = false;
		_windupHoldWantsFreeze = false;
		_meleeSwingClipFromPress = false;
		AbortMeleeAttackAnimClip( "cancel windup hold" );
	}

	/// <summary>
	/// Host/local swing commit. If a press windup hold is active, resume it; otherwise start a fresh clip
	/// (unless the owner already released the press-started clip).
	/// </summary>
	public void PlayMeleeSwingAttack( byte attackType, bool broadcastFromHost = false, bool isHeavy = false )
	{
		if ( !PlayMeleeSwingAnimation || !GameObject.IsValid() )
			return;

		if ( _windupHoldActive )
		{
			ReleaseMeleeAttackWindupHold( attackType, isHeavy );
		}
		else if ( GetMeleeAttackAnimBusyRemainingSeconds() > 0.01f )
		{
			// Owner already started/resumed on press/release — only refresh heavy rate + busy window.
			var body = ResolveBody();
			if ( body is not null && body.IsValid() )
				ApplySwingPlaybackRate( body, attackType, isHeavy );
			_ownerSkipNextRemoteSwingApply = true;
		}
		else
		{
			ApplyMeleeSwingAttackLocal( attackType, isHeavy );
		}

		if ( !broadcastFromHost )
			return;

		if ( GameObject.Network is not { Active: true } || !Networking.IsHost )
			return;

		NetworkedHoldPose = (byte)HoldPose.MeleeTwoHand;
		NetworkedSwingAttackType = attackType;
		NetworkedSwingCounter++;

		_deferredSwingAnimType = attackType;
		_deferSwingAnimBroadcast = true;
	}

	void ApplySwingPlaybackRate( SkinnedModelRenderer body, byte attackType, bool isHeavy )
	{
		var rate = isHeavy
			? Math.Clamp( MeleeHeavySwingPlaybackRate, 0.35f, 1f )
			: Math.Clamp( MeleeLateralSwingPlaybackRate, 0.5f, 1f );

		if ( !_lateralSwingPlaybackSlowed )
		{
			_playbackRateSaved = body.PlaybackRate > 1e-3f ? body.PlaybackRate : 1f;
			_lateralSwingPlaybackSlowed = true;
		}

		body.PlaybackRate = rate;
		// Match combat chart length — do NOT stretch by 1/rate (that delayed arcs by 2–3s).
		var present = GetSwingPresentationSeconds( isHeavy );
		_playbackRateRestoreAt = Time.NowDouble + present;
		_meleeAttackAnimBusyUntilSandbox = Time.NowDouble + present;
		_ = attackType;
	}

	internal void ClearMeleeSwingClipFromPressFlag()
	{
		_meleeSwingClipFromPress = false;
		_windupHoldActive = false;
		_windupHoldFrozen = false;
		_windupHoldWantsFreeze = false;
	}

	/// <summary>Drop the press-clip flag once the presentation window ends so it cannot latch busy forever.</summary>
	void TickMeleeSwingPresentationExpiry()
	{
		if ( !_meleeSwingClipFromPress || _windupHoldActive )
			return;

		if ( GetMeleeAttackAnimBusyRemainingSeconds() > 0.01f )
			return;

		_meleeSwingClipFromPress = false;
	}
}
