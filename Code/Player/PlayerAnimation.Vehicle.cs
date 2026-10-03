using Sandbox;

namespace Survival;

/// <summary>
/// Seated pose: while the pawn sits in a <see cref="Vehicle"/> the body plays the citizen
/// <c>sitpose_default</c> sequence with the animgraph off — the same UseAnimGraph=false path the hit
/// reaction uses, because a graph parameter is rewritten every frame by whoever simulates the pawn.
/// Ticks from both OnUpdate and OnPreRender like every other pose here, so proxies get it too.
/// </summary>
public sealed partial class PlayerAnimation
{
	const string SeatedSequence = "sitpose_default";

	PlayerMovement _seatMovement;
	bool _seatedPoseApplied;
	int _seatedTicks;

	/// <summary>Dev readout for <c>vehicle_info</c>.</summary>
	public string SeatedPoseDebug
	{
		get
		{
			var body = ResolveBody();
			var bodyState = body is null || !body.IsValid() ? "body=null" : $"body={body.GameObject.Name} graph={body.UseAnimGraph} seq={body.Sequence.Name ?? "-"} loop={body.Sequence.Looping} t={body.Sequence.Time:0.00}";
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

			_seatedTicks++;
			MaintainCombatSequencePose( SeatedSequence, keepMeleeSwordVisible: false );
			var body = ResolveBody();
			if ( body is not null && body.IsValid() )
				body.Sequence.Looping = true;
			_seatedPoseApplied = true;
			return;
		}

		if ( !_seatedPoseApplied )
			return;

		_seatedPoseApplied = false;
		if ( IsPlayingCombatSequence( SeatedSequence ) )
		{
			ClearCombatSequencePose();
			ForceRestoreLocomotionGraph();
		}
	}
}
