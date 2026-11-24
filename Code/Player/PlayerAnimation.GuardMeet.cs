using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Guard meet (Mark 2026-11-23: "the enemy hits my right side, i block right and my sword should be pushed further out
/// to the right to meet his sword"): when a block lands the host sends how far the guarding weapon was from the point the
/// blades met (<see cref="PlayerCombat"/> <c>ServerApplyBlockOutcome</c> → <see cref="StartGuardMeet"/>), and for
/// <see cref="GuardMeetSeconds"/> the sword hand reaches out by that offset and comes back - a two-bone arm solve on top
/// of whatever the graph plays (the guard breaks on a block, so the pose under it is already leaving). The held sword
/// rides the hand bone, so it follows. Presentation only: the block was decided on the host's guard bar
/// (<see cref="MeleeGuardGeometry"/>), never from this pose.
/// </summary>
public partial class PlayerAnimation
{
	[Property, Group( "Animation" ), Title( "Guard meet length (s)" ), Range( 0.1f, 0.8f ), Step( 0.01f ), Description( "On a block the sword hand reaches out to where the blades met and returns in this time." )]
	public float GuardMeetSeconds { get; set; } = 0.3f;

	Vector3 _guardMeetPush;
	double _guardMeetStartedAt = double.NegativeInfinity;
	bool _guardMeetIkActive;

	/// <summary>Every peer, from the block broadcast: reach the guarding weapon out by <paramref name="pushWorld"/> (world offset) and back.</summary>
	public void StartGuardMeet( Vector3 pushWorld )
	{
		if ( pushWorld.LengthSquared < 1f )
			return;

		_guardMeetPush = pushWorld;
		_guardMeetStartedAt = Time.NowDouble;
	}

	/// <summary>0..1: out in the first fifth, held to the half, eased back by the end.</summary>
	float GuardMeetEnvelope()
	{
		var length = MathF.Max( 0.1f, GuardMeetSeconds );
		var t = (float)((Time.NowDouble - _guardMeetStartedAt) / length);
		if ( t < 0f || t >= 1f )
			return 0f;

		if ( t < 0.2f )
		{
			var u = t / 0.2f;
			return 1f - (1f - u) * (1f - u);
		}

		if ( t < 0.5f )
			return 1f;

		var back = (t - 0.5f) / 0.5f;
		return 1f - back * back * (3f - 2f * back);
	}

	/// <summary>OnPreRender, before the held sword is placed: the sword arm (both arms on a two-handed grip) reaches to the contact.</summary>
	void TickGuardMeet()
	{
		var body = ResolveBody();
		var envelope = GuardMeetEnvelope();
		// The first-person rest grip owns the right arm while it is shown (PlayerAnimation.FirstPerson.cs).
		if ( envelope <= 0f || body is null || !body.IsValid() || _hitReactionPoseActive || _firstPersonSwordHold is not null
		     || ResolvePresentationHoldPose() != HoldPose.MeleeTwoHand )
		{
			if ( _guardMeetIkActive )
			{
				_guardMeetIkActive = false;
				// No per-bone clear exists; every other arm solve of this frame runs after this.
				if ( body is not null && body.IsValid() )
					body.SceneModel?.ClearBoneOverrides();
			}

			return;
		}

		var offset = _guardMeetPush * envelope;
		var basis = body.GameObject.WorldRotation;
		var reached = ReachArm( body, "arm_upper_R", "arm_lower_R", "hand_R", offset, basis.Right * 0.7f + Vector3.Down * 0.7f );
		if ( !MeleeOneHandedGrip )
			reached |= ReachArm( body, "arm_upper_L", "arm_lower_L", "hand_L", offset, basis.Left * 0.7f + Vector3.Down * 0.7f );
		_guardMeetIkActive |= reached;
	}

	/// <summary>Moves one hand by <paramref name="offset"/> from where the animation has it this frame.</summary>
	bool ReachArm( SkinnedModelRenderer body, string upper, string lower, string hand, Vector3 offset, Vector3 pole )
	{
		var bone = body.Model?.Bones?.GetBone( hand );
		if ( bone is null || !body.TryGetBoneTransformAnimation( bone, out var handTx ) )
			return false;

		return SolveTwoBoneArm( body, upper, lower, hand, handTx.Position + offset, pole, null ) is not null;
	}
}
