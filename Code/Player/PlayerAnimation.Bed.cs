using Sandbox;
using Sandbox.Citizen;

namespace Survival;

/// <summary>
/// Lying in a bed: while <see cref="PlayerMovement.IsInBed"/> the Body renderer child is laid on its back on the
/// mattress, head on the pillow (<see cref="BuildBed.GetLieBodyTransform"/>), in the graph's idle stance - grounded,
/// no velocity, looking straight ahead. Only the Body child turns; the pawn root, the eye angles and the camera are
/// left alone. Ticks from OnUpdate and OnPreRender on every machine, after the facing pass, so proxies lie down too.
/// </summary>
public sealed partial class PlayerAnimation
{
	bool _bedPoseApplied;
	Vector3 _bedSavedBodyLocalPosition;

	void TickBedPose()
	{
		_seatMovement ??= Components.Get<PlayerMovement>();
		var body = ResolveBody();
		var bed = _seatMovement is { IsInBed: true } ? _seatMovement.OccupiedBed : null;

		if ( bed is not null && bed.IsValid() && body is not null && body.IsValid() && body.GameObject != GameObject )
		{
			if ( !_bedPoseApplied )
			{
				_bedPoseApplied = true;
				_bedSavedBodyLocalPosition = body.GameObject.LocalPosition;
				if ( _combatSequenceActive || !body.UseAnimGraph )
				{
					ClearCombatSequencePose();
					ForceRestoreLocomotionGraph();
				}
			}

			_seatController ??= Components.Get<PlayerController>();
			var bodyHeight = _seatController is not null ? _seatController.BodyHeight : 72f;
			bed.GetLieBodyTransform( bodyHeight, out var feet, out var rotation );
			body.GameObject.WorldPosition = feet;
			body.GameObject.WorldRotation = rotation;

			// A hit reaction clip may be playing on top (the hit that wakes the sleeper); leave the graph to it.
			if ( IsHitReactionActive )
				return;

			var helper = _animHelper;
			if ( helper is null || !helper.IsValid() )
				return;

			helper.Sitting = CitizenAnimationHelper.SittingStyle.None;
			helper.IsGrounded = true;
			helper.DuckLevel = 0f;
			helper.WithVelocity( Vector3.Zero );
			helper.WithWishVelocity( Vector3.Zero );
			helper.WithLook( rotation.Forward, 1f, 1f, 0f );
			return;
		}

		if ( !_bedPoseApplied )
			return;

		// Up again: the Body child goes back under the root; the facing pass turns it to the look from the next tick.
		_bedPoseApplied = false;
		if ( body is not null && body.IsValid() && body.GameObject != GameObject )
		{
			body.GameObject.LocalPosition = _bedSavedBodyLocalPosition;
			body.GameObject.LocalRotation = Rotation.Identity;
		}
	}
}
