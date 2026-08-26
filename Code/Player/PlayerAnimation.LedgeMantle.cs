using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Grapple ledge-grab mantle presentation: the citizen <c>ledgegrab_pullup_01</c> clip played as a
/// <c>UseAnimGraph=false</c> sequence for the pull window, playback-fitted so the hands leave the lip
/// as the pawn tops out. Same contract as the hit reaction: the starter broadcasts a duration, every
/// machine runs the same local deadline, and the pose ticks from OnUpdate <b>and</b> OnPreRender so
/// proxies expire it. The hit-reaction flail always wins over the mantle.
/// </summary>
public sealed partial class PlayerAnimation
{
	/// <summary>Citizen clip for the ledge pull-up (both hands on the lip, body over the top).</summary>
	public const string LedgeMantleSequence = "ledgegrab_pullup_01";

	/// <summary>Deadline on this machine's clock. Every peer runs the same window from the broadcast.</summary>
	double _ledgeMantleEndsAtSandbox;

	/// <summary>Edge-trigger so the pose is restored exactly once when the window ends.</summary>
	bool _ledgeMantlePoseActive;

	public bool IsLedgeMantleActive => Time.NowDouble < _ledgeMantleEndsAtSandbox;

	/// <summary>Movement entry point on the machine driving the pull (owner / offline host).</summary>
	internal void BeginLedgeMantle( float seconds )
	{
		if ( GameObject.Network is { Active: true } )
			RpcBroadcastLedgeMantle( seconds );
		else
			ApplyLedgeMantleLocally( seconds );
	}

	/// <summary>Early stop when the pull arrives before the estimated window runs out.</summary>
	internal void EndLedgeMantle()
	{
		if ( GameObject.Network is { Active: true } )
			RpcBroadcastLedgeMantleEnd();
		else
			EndLedgeMantleLocally();
	}

	/// <summary>Owner-initiated (the pull is owner motion) — cosmetic only, so no host gate.</summary>
	[Rpc.Broadcast( NetFlags.Reliable | NetFlags.SendImmediate )]
	void RpcBroadcastLedgeMantle( float seconds ) => ApplyLedgeMantleLocally( seconds );

	[Rpc.Broadcast( NetFlags.Reliable )]
	void RpcBroadcastLedgeMantleEnd() => EndLedgeMantleLocally();

	void ApplyLedgeMantleLocally( float seconds )
	{
		// The flail owns the body — a mantle never replaces a hit reaction.
		if ( IsHitReactionActive )
			return;

		var end = Time.NowDouble + Math.Clamp( seconds, 0.15f, 3f );
		if ( end > _ledgeMantleEndsAtSandbox )
			_ledgeMantleEndsAtSandbox = end;

		ApplyLedgeMantlePose();
	}

	void EndLedgeMantleLocally()
	{
		if ( _ledgeMantleEndsAtSandbox <= 0 && !_ledgeMantlePoseActive )
			return;

		_ledgeMantleEndsAtSandbox = 0;

		if ( IsHitReactionActive )
		{
			// Drop mantle state without touching the pose — the flail is presenting.
			_ledgeMantlePoseActive = false;
			return;
		}

		ClearLedgeMantlePose();
	}

	/// <summary>Ticked from OnUpdate and OnPreRender, right after the hit reaction tick.</summary>
	void TickLedgeMantlePose()
	{
		if ( IsHitReactionActive )
		{
			_ledgeMantleEndsAtSandbox = 0;
			_ledgeMantlePoseActive = false;
			return;
		}

		if ( IsLedgeMantleActive )
		{
			ApplyLedgeMantlePose();
			return;
		}

		if ( !_ledgeMantlePoseActive && !IsPlayingCombatSequence( LedgeMantleSequence ) )
			return;

		_ledgeMantleEndsAtSandbox = 0;
		ClearLedgeMantlePose();
	}

	/// <summary>
	/// Safe to call every frame: the clip only restarts (and only re-fits its rate) on the first
	/// frame of the mantle. Non-looping — a finished clip holds its top-out frame until the window ends.
	/// </summary>
	void ApplyLedgeMantlePose()
	{
		EnsureAnimTargets();

		var starting = !_ledgeMantlePoseActive;
		_ledgeMantlePoseActive = true;

		PlayCombatSequencePose( LedgeMantleSequence, keepMeleeSwordVisible: false, forceRestart: starting );

		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
		{
			body.Sequence.Looping = false;

			if ( starting )
			{
				// Fit the clip to the pull window so the pull-up tracks the actual body motion.
				var clip = body.Sequence.Duration;
				var window = Math.Max( 0.15f, (float)( _ledgeMantleEndsAtSandbox - Time.NowDouble ) );
				body.PlaybackRate = clip > 1e-3f ? Math.Clamp( clip / window, 0.5f, 3f ) : 1f;
			}
		}

		// Both hands are on the lip — no floating box-sword during the pull.
		DestroyMeleeDemoStick();
	}

	/// <summary>Mantle over: same hard locomotion restore as the hit reaction end.</summary>
	void ClearLedgeMantlePose()
	{
		_ledgeMantlePoseActive = false;
		ClearHitReactionPose();
	}
}
