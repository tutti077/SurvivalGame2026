using Sandbox;
using Sandbox.Citizen;

namespace Survival;

/// <summary>
/// Seated pose: while the pawn sits in a <see cref="Vehicle"/> the citizen animgraph runs its chair
/// sit (<see cref="CitizenAnimationHelper.Sitting"/>) and this component feeds the graph what the
/// controller's animator pass normally would — grounded, no velocity, and the look-at from the eye
/// angles with the body weight at zero, so the torso stays square to the seat (the root and Body
/// child are pinned to it by <see cref="PlayerMovement"/>) while the head and eyes follow the
/// camera as they do on foot. Ticks from OnUpdate and OnPreRender like every other pose here, so
/// proxies get it too.
/// </summary>
public sealed partial class PlayerAnimation
{
	PlayerMovement _seatMovement;
	PlayerController _seatController;
	bool _seatedPoseApplied;
	int _seatedTicks;

	/// <summary>Dev readout for <c>vehicle_info</c>.</summary>
	public string SeatedPoseDebug
	{
		get
		{
			var body = ResolveBody();
			var bodyState = body is null || !body.IsValid() ? "body=null" : $"body={body.GameObject.Name} graph={body.UseAnimGraph} sit={body.GetInt( "sit" )} grounded={body.GetBool( "b_grounded" )} aim_head={body.GetVector( "aim_head" )} bodyLocalYaw={body.GameObject.LocalRotation.Yaw():0}";
			return $"applied={_seatedPoseApplied} ticks={_seatedTicks} combatSeq={_combatSequenceActive}:{_activeCombatSequenceName ?? "-"} hit={IsHitReactionActive} {bodyState}";
		}
	}

	void TickSeatedPose()
	{
		_seatMovement ??= Components.Get<PlayerMovement>();
		var seated = _seatMovement is { IsSeated: true };

		if ( seated )
		{
			// A hit reaction clip may be playing on top; let it finish, then sit again.
			if ( IsHitReactionActive )
				return;

			EnsureAnimTargets();
			var helper = _animHelper;
			var body = ResolveBody();
			if ( helper is null || !helper.IsValid() || body is null || !body.IsValid() )
				return;

			if ( !_seatedPoseApplied )
			{
				_seatedPoseApplied = true;
				if ( _combatSequenceActive || !body.UseAnimGraph )
				{
					ClearCombatSequencePose();
					ForceRestoreLocomotionGraph();
				}
			}

			_seatedTicks++;
			_seatController ??= Components.Get<PlayerController>();
			var look = _seatController is not null ? _seatController.EyeAngles.Forward : GameObject.WorldRotation.Forward;

			helper.Sitting = CitizenAnimationHelper.SittingStyle.Chair;
			helper.IsGrounded = true;
			helper.DuckLevel = 0f;
			helper.WithVelocity( Vector3.Zero );
			helper.WithWishVelocity( Vector3.Zero );
			helper.WithLook( look, 1f, 1f, 0f );
			return;
		}

		if ( !_seatedPoseApplied )
			return;

		_seatedPoseApplied = false;
		if ( _animHelper is { IsValid: true } )
			_animHelper.Sitting = CitizenAnimationHelper.SittingStyle.None;
	}
}
