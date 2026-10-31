using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Two-phase melee presentation on the Citizen attack clip, driven as a sequence so it can be scrubbed:
/// press SNAPS the body straight to the clip's cocked frame (no animated lift, no graph blend) and holds
/// it there; when the host's active window opens the STRIKE plays from that frame; the follow-through
/// hold is skipped and the return frames bring the sword back to idle, then the animgraph resumes.
/// Remote peers get the same snap + strike from the host broadcast (with its remaining windup).
/// </summary>
public sealed partial class PlayerAnimation
{
	const string MeleeSwingClipName = "Melee_Weapons_2H_Attack_01";

	[Property, Group( "Animation" ), Title( "Swing snap frame (s)" ), Description( "Clip time the sword snaps to on press — the cocked pose the strike starts from. Citizen Melee_Weapons_2H_Attack_01: lift 0–0.08, strike 0.08–0.21, hold out front to 0.54, return to 0.80." ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float MeleeSwingSnapClipSeconds { get; set; } = 0.08f;

	[Property, Group( "Animation" ), Title( "Swing strike end frame (s)" ), Description( "Clip time where the blade stops moving forward; the clip jumps from here to the return start (skipping the held-out follow-through)." ), Range( 0.05f, 1f ), Step( 0.01f )]
	public float MeleeSwingStrikeEndClipSeconds { get; set; } = 0.22f;

	[Property, Group( "Animation" ), Title( "Swing return start frame (s)" ), Range( 0.05f, 1f ), Step( 0.01f )]
	public float MeleeSwingReturnStartClipSeconds { get; set; } = 0.54f;

	[Property, Group( "Animation" ), Title( "Swing return end frame (s)" ), Description( "Clip time where the sword is back at idle; the animgraph resumes here." ), Range( 0.1f, 1f ), Step( 0.01f )]
	public float MeleeSwingReturnEndClipSeconds { get; set; } = 0.8f;

	[Property, Group( "Animation" ), Title( "Heavy swing playback rate" ), Range( 0.35f, 1f ), Step( 0.05f )]
	public float MeleeHeavySwingPlaybackRate { get; set; } = 0.55f;

	/// <summary>The attack sequence owns the body (snapped, waiting for the strike, or striking/returning).</summary>
	bool _swingClipActive;
	/// <summary>A strike has been scheduled (release / host start) — the clip is committed.</summary>
	bool _swingStrikeScheduled;
	/// <summary>The clip is advancing from the snap frame.</summary>
	bool _swingStrikeStarted;
	bool _swingFollowThroughSkipped;
	double _swingStrikeAtSandbox;
	byte _swingAttackType;
	bool _swingHeavy;
	bool _ownerSkipNextRemoteSwingApply;

	/// <summary>Owner pressed (or is charging the special): the body is snapped to the cocked frame and no strike is committed yet.</summary>
	public bool IsMeleeSwingSnapActive => _swingClipActive && !_swingStrikeScheduled;

	/// <summary>
	/// Single gate for "a committed swing clip is playing": from the strike being scheduled through the
	/// return frames. Purely time-bounded so it can never latch busy forever. Both the next attack and
	/// the arc overlay are bound to this. The press snap is deliberately excluded — the attack being
	/// aimed must still be allowed to fire.
	/// </summary>
	public bool IsMeleeSwingAnimBusy => GetMeleeAttackAnimBusyRemainingSeconds() > 0.01f;

	/// <summary>True while the attack sequence owns the pawn (press snap included).</summary>
	public bool HasActiveMeleeSwingPresentation => _swingClipActive || IsMeleeSwingAnimBusy;

	float SwingClipPlaybackRate( bool isHeavy ) => isHeavy
		? Math.Clamp( MeleeHeavySwingPlaybackRate, 0.35f, 1f )
		: Math.Clamp( MeleeLateralSwingPlaybackRate, 0.5f, 1f );

	/// <summary>Wall-clock length of strike + return at the given rate (follow-through skipped).</summary>
	float SwingClipPresentationSeconds( bool isHeavy )
	{
		var strike = Math.Max( 0.02f, MeleeSwingStrikeEndClipSeconds - MeleeSwingSnapClipSeconds );
		var back = Math.Max( 0.02f, MeleeSwingReturnEndClipSeconds - MeleeSwingReturnStartClipSeconds );
		return (strike + back) / SwingClipPlaybackRate( isHeavy );
	}

	/// <summary>Owner: press (or special charge) — snap the body to the cocked frame and hold it there.</summary>
	public void BeginMeleeSwingSnap( byte attackType )
	{
		if ( !PlayMeleeSwingAnimation || !GameObject.IsValid() )
			return;

		if ( _swingClipActive && !_swingStrikeScheduled )
		{
			_swingAttackType = attackType;
			return;
		}

		if ( _swingClipActive )
			EndMeleeSwingClip( "new press" );

		if ( _combatSequenceActive )
			ClearCombatSequencePose();

		if ( !SnapBodyToSwingFrame( attackType ) )
			return;

		Components.Get<PlayerCombat>()?.LogMeleeAnimStart(
			"sequence snap to cocked frame (held)",
			$"type={MeleeAttackTypes.Label( attackType )} frame={MeleeSwingSnapClipSeconds:0.###}s" );
	}

	/// <summary>
	/// Owner release / special fire: commit the strike. The clip stays on the cocked frame for the
	/// predicted remaining windup (class windup − hold), then plays. The host re-schedules with its
	/// authoritative remaining when it accepts the swing.
	/// </summary>
	public void ReleaseMeleeSwingSnap( byte attackType, bool isHeavy, float windupRemainingSeconds )
	{
		if ( !PlayMeleeSwingAnimation || !GameObject.IsValid() )
			return;

		_ownerSkipNextRemoteSwingApply = true;
		ScheduleMeleeStrike( attackType, isHeavy, windupRemainingSeconds, "owner release" );
	}

	/// <summary>Drop the press snap / clip (block, shove, hit reaction, cancelled special).</summary>
	public void CancelMeleeSwingSnap()
	{
		if ( !_swingClipActive )
			return;

		EndMeleeSwingClip( "cancel swing snap" );
	}

	/// <summary>
	/// Host / local swing commit with the authoritative remaining windup. Snaps first when nothing is
	/// on screen (remote peers, host pawn without a press), then schedules the strike.
	/// </summary>
	public void PlayMeleeSwingAttack( byte attackType, bool broadcastFromHost, bool isHeavy, float windupRemainingSeconds )
	{
		if ( !PlayMeleeSwingAnimation || !GameObject.IsValid() )
			return;

		ScheduleMeleeStrike( attackType, isHeavy, windupRemainingSeconds, "host swing start" );

		if ( !broadcastFromHost )
			return;

		if ( GameObject.Network is not { Active: true } || !Networking.IsHost )
			return;

		NetworkedHoldPose = (byte)HoldPose.MeleeTwoHand;
		NetworkedSwingAttackType = attackType;
		NetworkedSwingHeavy = isHeavy;
		NetworkedSwingWindupRemainingSeconds = windupRemainingSeconds;
		NetworkedSwingCounter++;

		_deferredSwingAnimType = attackType;
		_deferredSwingAnimHeavy = isHeavy;
		_deferredSwingAnimWindupRemaining = windupRemainingSeconds;
		_deferSwingAnimBroadcast = true;
	}

	void ScheduleMeleeStrike( byte attackType, bool isHeavy, float windupRemainingSeconds, string source )
	{
		EnsureAnimTargets();
		ApplyHoldPose( HoldPose.MeleeTwoHand );

		// Already striking: only refresh the heavy rate (host confirms heavy after the owner predicted it).
		if ( _swingClipActive && _swingStrikeStarted )
		{
			if ( _swingHeavy != isHeavy )
			{
				_swingHeavy = isHeavy;
				var striking = ResolveBody();
				if ( striking is not null && striking.IsValid() )
					striking.PlaybackRate = SwingClipPlaybackRate( isHeavy );
			}

			return;
		}

		if ( !_swingClipActive && !SnapBodyToSwingFrame( attackType ) )
			return;

		var remaining = Math.Max( 0f, windupRemainingSeconds );
		_swingAttackType = attackType;
		_swingHeavy = isHeavy;
		_swingStrikeScheduled = true;
		_swingStrikeAtSandbox = Time.NowDouble + remaining;
		_meleeAttackAnimBusyUntilSandbox = _swingStrikeAtSandbox + SwingClipPresentationSeconds( isHeavy );

		Components.Get<PlayerCombat>()?.LogMeleeAnimStart(
			"sequence strike scheduled",
			$"{source} type={MeleeAttackTypes.Label( attackType )} heavy={isHeavy} in={remaining:0.###}s busy={GetMeleeAttackAnimBusyRemainingSeconds():0.###}s" );
	}

	/// <summary>Put the body on the attack sequence at the cocked frame, frozen. False when there is no body.</summary>
	bool SnapBodyToSwingFrame( byte attackType )
	{
		EnsureAnimTargets();
		ApplyHoldPose( HoldPose.MeleeTwoHand );

		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
			return false;

		if ( !_lateralSwingPlaybackSlowed )
		{
			_playbackRateSaved = body.PlaybackRate > 1e-3f ? body.PlaybackRate : 1f;
			_lateralSwingPlaybackSlowed = true;
		}

		body.UseAnimGraph = false;
		body.Sequence.Name = MeleeSwingClipName;
		body.Sequence.Looping = false;
		body.Sequence.Time = MeleeSwingSnapClipSeconds;
		body.PlaybackRate = 0f;

		_swingClipActive = true;
		_swingStrikeScheduled = false;
		_swingStrikeStarted = false;
		_swingFollowThroughSkipped = false;
		_swingStrikeAtSandbox = double.MaxValue;
		_swingAttackType = attackType;
		_meleeAttackAnimBusyUntilSandbox = 0;
		return true;
	}

	/// <summary>Per frame: hold the snap frame, start the strike on time, skip the follow-through, end on the return frame.</summary>
	void TickMeleeSwingClip()
	{
		if ( !_swingClipActive )
			return;

		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
		{
			EndMeleeSwingClip( "no body" );
			return;
		}

		// Another pose took the body (hit reaction / mantle go through Cancel, but never fight a graph that is back on).
		if ( body.UseAnimGraph
		     || !string.Equals( body.Sequence.Name, MeleeSwingClipName, StringComparison.OrdinalIgnoreCase ) )
		{
			EndMeleeSwingClip( "body taken by another pose" );
			return;
		}

		var now = Time.NowDouble;
		if ( !_swingStrikeStarted )
		{
			if ( now < _swingStrikeAtSandbox )
			{
				body.Sequence.Time = MeleeSwingSnapClipSeconds;
				body.PlaybackRate = 0f;
				return;
			}

			_swingStrikeStarted = true;
			body.Sequence.Time = MeleeSwingSnapClipSeconds;
			body.PlaybackRate = SwingClipPlaybackRate( _swingHeavy );
			Components.Get<PlayerCombat>()?.LogMeleeAnimStart(
				"sequence STRIKE from cocked frame",
				$"type={MeleeAttackTypes.Label( _swingAttackType )} heavy={_swingHeavy} rate={body.PlaybackRate:0.##}" );
			return;
		}

		var t = body.Sequence.Time;
		if ( !_swingFollowThroughSkipped && t >= MeleeSwingStrikeEndClipSeconds )
		{
			_swingFollowThroughSkipped = true;
			var over = Math.Max( 0f, t - MeleeSwingStrikeEndClipSeconds );
			body.Sequence.Time = Math.Max( MeleeSwingStrikeEndClipSeconds, MeleeSwingReturnStartClipSeconds ) + over;
			return;
		}

		if ( body.Sequence.IsFinished
		     || ( _swingFollowThroughSkipped && t >= MeleeSwingReturnEndClipSeconds )
		     || now >= _meleeAttackAnimBusyUntilSandbox + 0.05 )
		{
			EndMeleeSwingClip( "return frames done" );
		}
	}

	/// <summary>Hand the body back to the locomotion graph and clear the swing window.</summary>
	void EndMeleeSwingClip( string reason )
	{
		var wasActive = _swingClipActive;
		_swingClipActive = false;
		_swingStrikeScheduled = false;
		_swingStrikeStarted = false;
		_swingFollowThroughSkipped = false;
		_swingStrikeAtSandbox = double.MaxValue;
		_meleeAttackAnimBusyUntilSandbox = 0;

		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
		{
			if ( string.Equals( body.Sequence.Name, MeleeSwingClipName, StringComparison.OrdinalIgnoreCase ) )
			{
				body.UseAnimGraph = false;
				body.Sequence.Name = string.Empty;
				body.Sequence.Time = 0f;
				body.UseAnimGraph = true;
			}

			body.Set( "b_weapon_lower", false );
		}

		RestoreLateralSwingPlaybackRate();

		if ( wasActive )
			Components.Get<PlayerCombat>()?.LogMeleeAnimStopIfAny( $"end swing clip ({reason})" );
	}
}
