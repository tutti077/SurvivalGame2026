using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Victim "I was just hit" window and pose.
///
/// This lives on <see cref="PlayerAnimation"/> rather than <see cref="PlayerCombat"/> so the window and
/// the pose have a single owner that always ticks (OnUpdate <i>and</i> OnPreRender, so proxies expire it
/// too). Being hit is not an equipment-dependent state, so victim lookups resolve PlayerAnimation.
///
/// The host picks a duration, broadcasts it, and every machine runs the same local deadline. No
/// prediction, no lerping, no per-peer countdown ownership.
/// </summary>
public sealed partial class PlayerAnimation
{
	[Property, Group( "Hit reaction" ), Title( "Default hit reaction (s)" ), Description( "Used when a damage source does not specify its own duration." ), Range( 0.1f, 3f ), Step( 0.05f )]
	public float HitReactionSeconds { get; set; } = 0.9f;

	[Property, Group( "Hit reaction" ), Title( "Max hit reaction (s)" ), Description( "Ceiling for any caller — nothing can hold a pawn in the flail longer than this." ), Range( 0.5f, 5f ), Step( 0.1f )]
	public float HitReactionMaxSeconds { get; set; } = 2.5f;

	[Property, Group( "Hit reaction" ), Title( "Log hit reaction" )]
	public bool LogHitReaction { get; set; }

	/// <summary>Citizen clip for the "just got hit" flail (arms + legs thrown around).</summary>
	public const string HitReactionFlailSequence = "airborne_flail_movement";

	/// <summary>Deadline on this machine's clock. Every peer runs the same window from the broadcast.</summary>
	double _hitReactionEndsAtSandbox;

	/// <summary>Edge-trigger so the pose is restored exactly once when the window ends.</summary>
	bool _hitReactionPoseActive;

	public bool IsHitReactionActive => Time.NowDouble < _hitReactionEndsAtSandbox;

	public float HitReactionRemainingSeconds =>
		Math.Max( 0f, (float)( _hitReactionEndsAtSandbox - Time.NowDouble ) );

	float ClampHitReactionSeconds( float seconds ) =>
		Math.Clamp( seconds, 0.1f, Math.Max( 0.5f, HitReactionMaxSeconds ) );

	/// <summary>Host entry point for every "this pawn got hit" source (sword, shove, blocked heavy).</summary>
	internal void ServerBeginHitReaction( float durationSeconds )
	{
		if ( Networking.IsActive && !Networking.IsHost )
			return;

		var seconds = ClampHitReactionSeconds( durationSeconds > 1e-4f ? durationSeconds : HitReactionSeconds );

		if ( GameObject.Network is { Active: true } )
			RpcBroadcastHitReaction( seconds );
		else
			ApplyHitReactionLocally( seconds );
	}

	/// <summary>Reliable + immediate: a dropped reaction leaves one machine showing a different pawn state.</summary>
	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable | NetFlags.SendImmediate )]
	void RpcBroadcastHitReaction( float seconds ) => ApplyHitReactionLocally( seconds );

	/// <summary>Runs on every machine that knows about this pawn — owner, host, and observers alike.</summary>
	void ApplyHitReactionLocally( float seconds )
	{
		var extending = IsHitReactionActive;
		var end = Time.NowDouble + ClampHitReactionSeconds( seconds );
		// A second hit extends the window; it never cuts an existing reaction short.
		if ( end > _hitReactionEndsAtSandbox )
			_hitReactionEndsAtSandbox = end;

		// The swing clip and any recovery pose lose to the hit — kill them before the pose goes on.
		AbortMeleeAttackAnimClip( "hit reaction" );
		CancelMeleeAttackWindupHold();

		// Null on pawns that carry no combat state at all — nothing to drop then.
		ResolveCombat()?.OnHitReactionBegan();
		Components.Get<PlayerMovement>()?.OnHitReactionBegan();

		ApplyHitReactionPose();

		if ( LogHitReaction )
			Log.Info( $"[HitReaction] {GameObject.Name}: {(extending ? "extend" : "begin")} "
			          + $"{HitReactionRemainingSeconds:0.###}s (proxy={GameObject.IsProxy} host={Networking.IsHost})" );
	}

	/// <summary>
	/// Ticked from OnUpdate <b>and</b> OnPreRender: a pawn's OnUpdate is skipped on peers where it is a
	/// proxy, and an exit that only ran from a tick that peer skipped left it looping the flail forever.
	/// OnPreRender runs wherever the pawn is drawn, so the pose can always end.
	/// </summary>
	void TickHitReactionPose()
	{
		if ( IsHitReactionActive )
		{
			ApplyHitReactionPose();
			return;
		}

		if ( !_hitReactionPoseActive && !IsPlayingCombatSequence( HitReactionFlailSequence ) )
			return;

		_hitReactionEndsAtSandbox = 0;
		ClearHitReactionPose();

		if ( LogHitReaction )
			Log.Info( $"[HitReaction] {GameObject.Name}: end — restore locomotion" );
	}

	/// <summary>
	/// The flail clip as a looping <c>UseAnimGraph=false</c> sequence, sword out of hand. The sequence is
	/// what makes the reaction identical on every machine — an animgraph pose (<c>b_grounded=false</c>) is
	/// overwritten every frame by <see cref="PlayerController"/> on whichever machine simulates the pawn,
	/// so only proxies (the host's view of a client) ever showed it.
	/// Safe to call every frame: the clip only restarts on the first frame of the reaction.
	/// </summary>
	public void ApplyHitReactionPose()
	{
		EnsureAnimTargets();

		var starting = !_hitReactionPoseActive;
		_hitReactionPoseActive = true;

		if ( starting )
			ClearMeleeTwoHandHold();

		PlayCombatSequencePose( HitReactionFlailSequence, keepMeleeSwordVisible: false, forceRestart: starting );

		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
		{
			// Loop — the reaction window can outlast the clip and they should flail for all of it.
			body.Sequence.Looping = true;
			body.PlaybackRate = 1f;
		}

		DestroyMeleeDemoStick();
	}

	/// <summary>Hit reaction over: hard restore locomotion and the held sword on every peer.</summary>
	public void ClearHitReactionPose()
	{
		_hitReactionPoseActive = false;
		EnsureAnimTargets();
		ClearCombatSequencePose();
		ForceRestoreLocomotionGraph();

		var grounded = Components.Get<PlayerController>() is not { } controller
		               || !controller.IsValid()
		               || controller.IsOnGround;

		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
		{
			body.UseAnimGraph = true;
			body.PlaybackRate = 1f;
			body.Sequence.Looping = false;
			body.Set( "b_grounded", grounded );
			ResetMeleeAttackAnimGraphToIdle( body );
		}

		if ( _animHelper is not null && _animHelper.IsValid() )
		{
			_animHelper.IsGrounded = grounded;
			_animHelper.IsSwimming = false;
			_animHelper.IsClimbing = false;
			_animHelper.IsNoclipping = false;
			_animHelper.DuckLevel = 0f;
		}

		if ( ResolvePresentationHoldPose() == HoldPose.MeleeTwoHand )
			ApplyMeleeTwoHandHold();
		else
			ClearMeleeTwoHandHold();
	}
}
