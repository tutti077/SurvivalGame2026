using Sandbox;

namespace Survival;

/// <summary>
/// Scav-vs-scav brawls (<see cref="ScavBrawl"/>, console <c>scav_brawl</c>): a brain on a brawl team ignores players and
/// hunts the nearest living scav of another team through its normal chase / attack states, so the fight runs every
/// rule a player fight does (attack tokens, duel AI, stamina, staggers). Runtime only — 0 is the normal scav.
/// </summary>
public sealed partial class EntityBrain
{
	/// <summary>Host, runtime: brawl team (0 = not brawling — hunts players as usual).</summary>
	public int BrawlTeam { get; set; }

	/// <summary>Host: someone it would fight (a player; a scav of another team in a brawl) — the duel AI watches their swings too.</summary>
	internal bool IsHostileFighter( GameObject go ) => IsValidPlayerTarget( go );

	bool IsBrawlFoe( GameObject go )
	{
		if ( go is null || !go.IsValid() || go == GameObject )
			return false;

		return go.Components.Get<EntityBrain>() is { BrawlTeam: not 0 } brain
		       && brain.BrawlTeam != BrawlTeam
		       && go.Components.Get<EntityVitals>() is { IsDead: false };
	}

	GameObject FindNearestBrawlFoe()
	{
		GameObject best = null;
		var bestDist = float.MaxValue;
		var origin = GameObject.WorldPosition;
		foreach ( var brain in Scene.GetAllComponents<EntityBrain>() )
		{
			var go = brain.GameObject;
			if ( !IsBrawlFoe( go ) )
				continue;

			var dist = Vector3.DistanceBetween( origin, go.WorldPosition );
			if ( dist >= bestDist )
				continue;

			bestDist = dist;
			best = go;
		}

		return best;
	}

	/// <summary>Keep a living foe as the target and the brain in chase / attack; true when no foe is left (stand still).</summary>
	bool TickBrawlTarget()
	{
		if ( !IsBrawlFoe( _target ) )
			_target = FindNearestBrawlFoe();

		if ( !_target.IsValid() )
		{
			EntityCombat?.ResetCycle();
			Agent?.Stop();
			return true;
		}

		if ( _state is not (EnemyAiState.Chasing or EnemyAiState.Attacking) )
			EnterState( EnemyAiState.Chasing, _target );
		return false;
	}
}
