using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Animal animation presentation, every peer. The host writes the coarse state (<see cref="AnimState"/>,
/// <see cref="BiteSerial"/>); each machine picks the clip on its <see cref="SkinnedModelRenderer"/> by
/// sequence name (no anim graph: <c>idle</c> / <c>walk</c> / <c>run</c> / <c>sneak</c> / <c>graze</c> /
/// <c>alert</c> / <c>bite</c> / <c>takeoff</c> / <c>fly</c>, authored in <c>create_deer.py</c>,
/// <c>create_fox_anims.py</c>, <c>create_bluejay.py</c>) and matches the playback rate to the measured
/// ground speed against the clip's authored speed (the json walk / run / sneak m/s), so the feet plant.
/// A model without those clips (the dev box) just ignores the names.
/// </summary>
public sealed partial class AnimalBrain
{
	/// <summary>AnimState value for a fly-away bird (the other values are the <see cref="AnimalAiState"/> ids).</summary>
	public const byte AnimStateFlyAway = 10;

	/// <summary>Host → everyone: what the body should be doing (an <see cref="AnimalAiState"/> id, or <see cref="AnimStateFlyAway"/>).</summary>
	[Sync] public byte AnimState { get; private set; }

	/// <summary>Host → everyone: bumps once per bite windup, so each peer plays the one-shot.</summary>
	[Sync] public int BiteSerial { get; private set; }

	SkinnedModelRenderer _animRenderer;
	bool _animRendererSearched;
	string _animSequence;
	int _animBiteSeen;
	byte _animLastState = 255;
	double _animOneShotUntil;
	Vector3 _animPrevPos;
	double _animPrevAt;
	float _animSpeed;

	const float AnimSpeedSampleSeconds = 0.05f;
	const float BiteClipSeconds = 0.5f;
	const float TakeoffClipSeconds = 0.4f;

	/// <summary>Called from OnUpdate on every machine, before the host-only gate.</summary>
	void TickAnimationPresentation()
	{
		if ( !_animRendererSearched )
		{
			_animRendererSearched = true;
			_animRenderer = Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );
			if ( _animRenderer is not null && _animRenderer.IsValid() )
				_animRenderer.UseAnimGraph = false;
			_animPrevPos = WorldPosition;
			_animPrevAt = Time.NowDouble;
		}

		if ( _animRenderer is null || !_animRenderer.IsValid() )
			return;

		var now = Time.NowDouble;
		var dt = now - _animPrevAt;
		if ( dt >= AnimSpeedSampleSeconds )
		{
			var planar = (WorldPosition - _animPrevPos).WithZ( 0f ).Length / (float)dt;
			_animSpeed = MathX.Lerp( _animSpeed, planar, 0.5f );
			_animPrevPos = WorldPosition;
			_animPrevAt = now;
		}

		if ( IsDead() )
		{
			PlaySequence( Species == AnimalSpecies.Jay ? "sit" : "idle", true, 1f );
			return;
		}

		// One-shots win while they run.
		if ( BiteSerial != _animBiteSeen )
		{
			_animBiteSeen = BiteSerial;
			PlaySequence( "bite", false, 1f );
			_animOneShotUntil = now + BiteClipSeconds;
			return;
		}

		if ( AnimState != _animLastState )
		{
			var wasFlying = _animLastState == AnimStateFlyAway;
			_animLastState = AnimState;
			if ( AnimState == AnimStateFlyAway && !wasFlying )
			{
				PlaySequence( "takeoff", false, 1f );
				_animOneShotUntil = now + TakeoffClipSeconds;
				return;
			}
		}

		if ( now < _animOneShotUntil )
			return;

		var moving = _animSpeed > _behavior.WalkSpeed * 0.25f;
		string name;
		var rate = 1f;
		switch ( AnimState )
		{
			case AnimStateFlyAway:
				name = "fly";
				break;
			case (byte)AnimalAiState.Graze:
			case (byte)AnimalAiState.Eating:
				name = "graze";
				break;
			case (byte)AnimalAiState.Alerted:
				// Only the deer has a frozen head-up clip; the others hold still.
				name = Species == AnimalSpecies.Whitetail ? "alert" : "idle";
				break;
			case (byte)AnimalAiState.Tracking:
				name = moving ? "sneak" : "idle";
				rate = moving ? _animSpeed / MathF.Max( 1f, _behavior.SneakSpeed ) : 1f;
				break;
			case (byte)AnimalAiState.Attacking:
			case (byte)AnimalAiState.Fleeing:
				name = moving ? "run" : "idle";
				rate = moving ? _animSpeed / MathF.Max( 1f, _behavior.RunSpeed ) : 1f;
				break;
			default:
				if ( !moving )
				{
					name = "idle";
				}
				else if ( _animSpeed > (_behavior.WalkSpeed + _behavior.RunSpeed) * 0.5f )
				{
					name = "run";
					rate = _animSpeed / MathF.Max( 1f, _behavior.RunSpeed );
				}
				else
				{
					name = "walk";
					rate = _animSpeed / MathF.Max( 1f, _behavior.WalkSpeed );
				}
				break;
		}

		// The jay has only sit / takeoff / fly: every ground state is the perched sit (an unknown name
		// leaves the bind pose up - wings spread out to the sides).
		if ( Species == AnimalSpecies.Jay && name != "fly" )
		{
			name = "sit";
			rate = 1f;
		}

		PlaySequence( name, true, rate );
	}

	void PlaySequence( string name, bool looping, float rate )
	{
		if ( !string.Equals( _animSequence, name, StringComparison.Ordinal ) )
		{
			_animRenderer.Sequence.Name = name;
			_animRenderer.Sequence.Looping = looping;
			_animSequence = name;
		}

		_animRenderer.PlaybackRate = Math.Clamp( rate, 0.6f, 1.8f );
	}

	/// <summary>Host: publish the coarse body state for the presentation on every peer.</summary>
	void PublishAnimState( byte state )
	{
		if ( AnimState != state )
			AnimState = state;
	}
}
