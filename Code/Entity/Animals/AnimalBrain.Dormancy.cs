using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Animals leave the world past their own range (Mark 2026-11-04: "They should despawn at a certain range, all entities
/// should!" then "animals can be 100/90 for now"). The same rule as <see cref="EntityBrain"/>'s dormancy: no viewer — every
/// player pawn, or the scene camera when there is none — within <see cref="EntityChunkMemory.AnimalDespawnRangeMeters"/>,
/// checked at 2 Hz staggered:
///
/// - Calm (Idle / Wander / Graze, no threat), untamed, not trapped → written to <see cref="EntityChunkMemory"/> (species,
///   health, position, facing, home, calm state, population slot) and destroyed; the memory clones it back as the same
///   animal once a viewer is within <see cref="EntityChunkMemory.AnimalReturnRangeMeters"/>.
/// - Anything else (fleeing, tracking, attacking, alerted, walking up to a food offer, trapped, tamed) sleeps: locomotion,
///   nav agent and skinned body off, the brain returns early; it wakes once a viewer is within the restore range. A sleeper
///   that is not tamed or trapped loses its trail after <see cref="TrackGiveUpSeconds"/> (its own give-up rule) and leaves
///   the world calm, so the live count never builds up. Tamed animals never leave the world.
/// - A bird flying away or a body in the air is never put to sleep: it finishes and is gone (or lands) on its own.
///
/// Host only; dead animals are already past the OnUpdate gate.
/// </summary>
public sealed partial class AnimalBrain
{
	/// <summary>True while asleep: locomotion / agent / body are off (the brain's host logic returns early).</summary>
	public bool IsDormant { get; private set; }

	/// <summary>Prefab this animal was cloned from, set by <see cref="AnimalSetup.Configure"/>. Without one it only ever sleeps.</summary>
	public string SourcePrefabPath { get; set; }

	public Vector3 HomePosition => _homePosition;

	/// <summary>Idle / Wander / Graze with no threat: nothing holds the animal here.</summary>
	public bool IsCalm => !_threat.IsValid() && _state is AnimalAiState.Idle or AnimalAiState.Wander or AnimalAiState.Graze;

	const float DormancyCheckSeconds = 0.5f;
	float _nextDormancyCheck;
	bool _dormancyStaggered;
	double _dormantSince;
	// What sleep switched off, so waking restores exactly that.
	bool _dormantLocomotionWasOn;
	bool _dormantAgentWasOn;

	/// <summary>Host, after the start / death gates in OnUpdate: true when the animal is (now) asleep or gone and nothing else should tick.</summary>
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

		if ( _flyingAway || Locomotion is { IsAirborne: true } )
		{
			if ( IsDormant )
				SetDormant( false );
			return false;
		}

		// Asleep it wakes inside the restore range, awake it leaves past the despawn range: the 10 m between never thrashes.
		var range = IsDormant ? EntityChunkMemory.AnimalReturnRangeMeters : EntityChunkMemory.AnimalDespawnRangeMeters;
		if ( EntityChunkMemory.AnyViewerWithin( Scene, WorldPosition, range ) )
		{
			if ( IsDormant )
				SetDormant( false );
			return false;
		}

		var trapped = Locomotion is { IsTrapped: true };
		if ( IsDormant && !IsCalm && !IsTamed && !trapped && Time.NowDouble - _dormantSince >= TrackGiveUpSeconds )
		{
			_threat = null;
			_tameTarget = null;
			_fleeingLowHealth = false;
			EnterState( AnimalAiState.Idle );
		}

		if ( IsCalm && !IsTamed && !trapped && TryDespawnToMemory() )
			return true;

		if ( !IsDormant )
			SetDormant( true );
		return true;
	}

	bool TryDespawnToMemory()
	{
		if ( string.IsNullOrWhiteSpace( SourcePrefabPath ) )
			return false;

		var memory = EntityChunkMemory.Get( Scene );
		if ( memory is null || !memory.Remember( this ) )
			return false;

		if ( LogStateDebug )
			Log.Info( $"[AnimalBrain] {GameObject.Name} → memory at {WorldPosition} ({Vitals?.CurrentHealth:0} hp, {_state})" );

		Agent?.Stop();
		Enabled = false;
		GameObject.Destroy();
		return true;
	}

	/// <summary>Restored from <see cref="EntityChunkMemory"/>: pick up the calm state it left in (Wander is what <see cref="BeginAiNow"/> already entered).</summary>
	public void ResumeRememberedState( AnimalAiState state )
	{
		if ( state is AnimalAiState.Idle or AnimalAiState.Graze )
			EnterState( state );
	}

	void SetDormant( bool dormant )
	{
		if ( dormant == IsDormant )
			return;
		IsDormant = dormant;
		_dormantSince = Time.NowDouble;
		Locomotion ??= Components.Get<EntityLocomotion>();
		Agent ??= Components.Get<NavMeshAgent>();
		var hasLocomotion = Locomotion is not null && Locomotion.IsValid();
		var hasAgent = Agent is not null && Agent.IsValid();

		if ( dormant )
		{
			_dormantLocomotionWasOn = hasLocomotion && Locomotion.Enabled;
			_dormantAgentWasOn = hasAgent && Agent.Enabled;
			if ( _dormantAgentWasOn )
			{
				Agent.Stop();
				Agent.Enabled = false;
			}
			if ( _dormantLocomotionWasOn )
				Locomotion.Enabled = false;
		}
		else
		{
			if ( hasLocomotion && _dormantLocomotionWasOn )
				Locomotion.Enabled = true;
			if ( hasAgent && _dormantAgentWasOn )
				Agent.Enabled = true;
		}

		foreach ( var body in Components.GetAll<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( body is not null && body.IsValid() )
				body.Enabled = !dormant;
		}
	}
}
