using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Far-entity dormancy and despawn-to-memory. An entity with no viewer within
/// <see cref="EntityChunkMemory.EnemyDespawnRangeMeters"/> (350 m, every enemy kind; animals have their own shorter pair) — every
/// player pawn, or the scene camera when there is none (the terrainTest fly camera) — either leaves the world or sleeps:
///
/// - Not tracking a player (Idle / Wander / Returning, no target) → its location, facing, health, leash and population
///   slot are written to <see cref="EntityChunkMemory"/> (host memory, filed under its chunk) and the object is destroyed
///   (Mark 2026-11-02). The memory clones it back when a viewer comes within <see cref="EntityChunkMemory.EnemyReturnRangeMeters"/>
///   of that spot and the ground there is live.
/// - Tracking a player (searching, chasing, attacking, retreating, breaching), or an entity the memory cannot hold
///   (bosses, raiders, console spawns without a source prefab) → sleeps: locomotion, nav agent and skinned body are off,
///   so it neither traces the ground each frame nor animates (fps work, Mark 2026-10-20: perf_types measured 19 idle
///   scavs at 0.6 ms locomotion + 0.6 ms skinned bodies per frame with nobody near them). Wakes once a viewer is within
///   the restore range.
///
/// The check runs at 2 Hz, staggered. Stunned and dead entities are never put to sleep or remembered. Host only: proxies on
/// clients already skip their locomotion.
/// </summary>
public sealed partial class EntityBrain
{
	/// <summary>True while asleep: locomotion / agent / body are off (the brain's own update returns early).</summary>
	public bool IsDormant { get; private set; }

	/// <summary>
	/// Prefab this entity was cloned from, set by <see cref="EntityEnemySetup.Configure"/>. <see cref="EntityChunkMemory"/>
	/// needs it to clone the entity back; an entity without one only ever sleeps.
	/// </summary>
	public string SourcePrefabPath { get; set; }

	/// <summary>Hunting, fighting or fleeing a player in any form. Such an entity never leaves the world — it sleeps when far.</summary>
	public bool IsTrackingPlayer =>
		_target.IsValid()
		|| _state is EnemyAiState.Searching or EnemyAiState.Chasing or EnemyAiState.Attacking
			or EnemyAiState.Retreating or EnemyAiState.Breaching or EnemyAiState.Raiding;

	const float DormancyCheckSeconds = 0.5f;
	float _nextDormancyCheck;
	bool _dormancyStaggered;

	/// <summary>Called at the top of OnUpdate on the host; returns true when the entity is (now) asleep or gone and nothing else should tick.</summary>
	bool TickDormancy()
	{
		if ( !_dormancyStaggered )
		{
			_dormancyStaggered = true;
			_nextDormancyCheck = Time.Now + (DormancyCheckSeconds * ((GameObject.Id.GetHashCode() & 0xFF) / 255f));
		}
		if ( Time.Now < _nextDormancyCheck )
			return IsDormant;
		_nextDormancyCheck = Time.Now + DormancyCheckSeconds;

		var isDead = Vitals is not null && Vitals.IsDead;
		// Asleep it wakes inside the return range, awake it leaves past the despawn range: the 50 m between never thrashes.
		var range = IsDormant ? EntityChunkMemory.EnemyReturnRangeMeters : EntityChunkMemory.EnemyDespawnRangeMeters;
		var viewerNear = EntityChunkMemory.AnyViewerWithin( Scene, WorldPosition, range );

		if ( !viewerNear && !isDead && CanDespawnToMemory() && TryDespawnToMemory() )
			return true;

		var wantSleep = !IsRaider && !IsStunned && !isDead && !viewerNear;
		if ( wantSleep != IsDormant )
			SetDormant( wantSleep );
		return IsDormant;
	}

	/// <summary>Far away and nothing holds it here: not tracking a player, not stunned, not a raider (the raid session owns its stragglers), not a boss (its own abandon rule).</summary>
	bool CanDespawnToMemory()
	{
		if ( string.IsNullOrWhiteSpace( SourcePrefabPath ) || IsStunned || IsTrackingPlayer || IsRaider || _wasRaider )
			return false;

		return Components.Get<BossEntity>() is null;
	}

	bool TryDespawnToMemory()
	{
		var memory = EntityChunkMemory.Get( Scene );
		if ( memory is null || !memory.Remember( this ) )
			return false;

		if ( LogPerceptionDebug )
			Log.Info( $"[EntityBrain] {GameObject.Name} → memory at {WorldPosition} ({Vitals?.CurrentHealth:0} hp)" );

		_target = null;
		BreachClaims.Release( this );
		EntityCombat?.ResetCycle();
		Agent?.Stop();
		Enabled = false;
		GameObject.Destroy();
		return true;
	}

	void SetDormant( bool dormant )
	{
		IsDormant = dormant;
		Locomotion ??= Components.Get<EntityLocomotion>();
		Agent ??= Components.Get<NavMeshAgent>();
		if ( Locomotion is not null && Locomotion.IsValid() )
			Locomotion.Enabled = !dormant;
		if ( Agent is not null && Agent.IsValid() )
			Agent.Enabled = !dormant;
		foreach ( var body in Components.GetAll<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( body is not null && body.IsValid() )
				body.Enabled = !dormant;
		}
	}
}
