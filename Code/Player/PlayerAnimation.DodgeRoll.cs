using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Dodge roll presentation: the citizen <c>Roll_N</c> clip played as a <c>UseAnimGraph=false</c>
/// sequence, playback-fitted so the whole roll completes inside the broadcast window — the graph's
/// own roll state runs the clip at its native (slow) pace and ignores <c>PlaybackRate</c>, which is
/// why the sequence approach is used instead (same contract as the ledge mantle). The starter
/// broadcasts a duration, every machine runs the same local deadline, and the pose ticks from
/// OnUpdate <b>and</b> OnPreRender so proxies expire it. The hit-reaction flail always wins.
/// </summary>
public sealed partial class PlayerAnimation
{
	/// <summary>Citizen clip for the dodge roll (forward combat roll; travel direction sells the dodge).</summary>
	public const string DodgeRollSequence = "Roll_N";

	/// <summary>Deadline on this machine's clock. Every peer runs the same window from the broadcast.</summary>
	double _dodgeRollEndsAtSandbox;

	/// <summary>Edge-trigger so the pose is restored exactly once when the window ends.</summary>
	bool _dodgeRollPoseActive;

	public bool IsDodgeRollActive => Time.NowDouble < _dodgeRollEndsAtSandbox;

	/// <summary>Movement entry point on the machine driving the roll (owner / offline host).</summary>
	internal void BeginDodgeRoll( float seconds )
	{
		if ( GameObject.Network is { Active: true } )
			RpcBroadcastDodgeRoll( seconds );
		else
			ApplyDodgeRollLocally( seconds );
	}

	/// <summary>Owner-initiated (the roll is owner motion) — cosmetic only, so no host gate.</summary>
	[Rpc.Broadcast( NetFlags.Reliable | NetFlags.SendImmediate )]
	void RpcBroadcastDodgeRoll( float seconds ) => ApplyDodgeRollLocally( seconds );

	void ApplyDodgeRollLocally( float seconds )
	{
		// The flail owns the body — a roll never replaces a hit reaction.
		if ( IsHitReactionActive )
			return;

		var end = Time.NowDouble + Math.Clamp( seconds, 0.1f, 1.5f );
		if ( end > _dodgeRollEndsAtSandbox )
			_dodgeRollEndsAtSandbox = end;

		ApplyDodgeRollPose();
	}

	/// <summary>Ticked from OnUpdate and OnPreRender, right after the mantle tick.</summary>
	void TickDodgeRollPose()
	{
		if ( IsHitReactionActive )
		{
			_dodgeRollEndsAtSandbox = 0;
			_dodgeRollPoseActive = false;
			return;
		}

		if ( IsDodgeRollActive )
		{
			ApplyDodgeRollPose();
			return;
		}

		if ( !_dodgeRollPoseActive && !IsPlayingCombatSequence( DodgeRollSequence ) )
			return;

		_dodgeRollEndsAtSandbox = 0;
		ClearDodgeRollPose();
	}

	/// <summary>
	/// Safe to call every frame: the clip only restarts (and only re-fits its rate) on the first
	/// frame of the roll. Non-looping — a finished clip holds its last frame until the window ends.
	/// </summary>
	void ApplyDodgeRollPose()
	{
		EnsureAnimTargets();

		var starting = !_dodgeRollPoseActive;
		_dodgeRollPoseActive = true;

		PlayCombatSequencePose( DodgeRollSequence, keepMeleeSwordVisible: false, forceRestart: starting );

		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
		{
			body.Sequence.Looping = false;

			if ( starting )
			{
				// Fit the clip to the dodge window — a 0.2s roll needs the whole clip inside 0.2s.
				var clip = body.Sequence.Duration;
				var window = Math.Max( 0.1f, (float)( _dodgeRollEndsAtSandbox - Time.NowDouble ) );
				body.PlaybackRate = clip > 1e-3f ? Math.Clamp( clip / window, 1f, 12f ) : 1f;
			}
		}

		// Tumbling with a floating box-sword looks wrong — TickHoldPose re-creates it after.
		DestroyMeleeDemoStick();
	}

	/// <summary>Roll over: same hard locomotion restore as the hit reaction / mantle end.</summary>
	void ClearDodgeRollPose()
	{
		_dodgeRollPoseActive = false;
		ClearHitReactionPose();
	}
}
