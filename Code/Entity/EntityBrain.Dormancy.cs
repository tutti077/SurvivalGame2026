using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Far-entity dormancy (fps work, Mark 2026-10-20: "200 fps, nothing is even here"). An entity with no viewer within
/// <see cref="SleepRangeMeters"/> — every player pawn, or the scene camera when there is none (the terrainTest fly
/// camera) — sleeps: its locomotion, nav agent and skinned body are switched off, so it neither traces the ground
/// each frame nor animates. perf_types measured 19 idle scavs at 0.6 ms locomotion + 0.6 ms skinned bodies per
/// frame with nobody near them. The brain itself keeps ticking (this check only, 2 Hz, staggered), wakes the entity
/// the moment a viewer comes within range, and raiders / stunned / dead entities are never put to sleep. Host only:
/// proxies on clients already skip their locomotion.
/// </summary>
public sealed partial class EntityBrain
{
	[Property, Group( "Performance" ), Title( "Sleep Range (m)" ), Range( 0f, 2000f ), Step( 10f ), Description( "No player pawn (or the scene camera, when there is no pawn) within this many metres: locomotion, nav agent and skinned body switch off until one comes back. 0 = never sleep." )]
	public float SleepRangeMeters { get; set; } = 350f;

	/// <summary>True while asleep: locomotion / agent / body are off (the brain's own update returns early).</summary>
	public bool IsDormant { get; private set; }

	const float DormancyCheckSeconds = 0.5f;
	float _nextDormancyCheck;
	bool _dormancyStaggered;

	/// <summary>Called at the top of OnUpdate on the host; returns true when the entity is (now) asleep and nothing else should tick.</summary>
	bool TickDormancy()
	{
		if ( SleepRangeMeters <= 0f )
		{
			if ( IsDormant )
				SetDormant( false );
			return false;
		}

		if ( !_dormancyStaggered )
		{
			_dormancyStaggered = true;
			_nextDormancyCheck = Time.Now + (DormancyCheckSeconds * ((GameObject.Id.GetHashCode() & 0xFF) / 255f));
		}
		if ( Time.Now < _nextDormancyCheck )
			return IsDormant;
		_nextDormancyCheck = Time.Now + DormancyCheckSeconds;

		var wantSleep = !IsRaider && !IsStunned && !(Vitals is not null && Vitals.IsDead) && !AnyViewerWithin( SleepRangeMeters );
		if ( wantSleep != IsDormant )
			SetDormant( wantSleep );
		return IsDormant;
	}

	bool AnyViewerWithin( float meters )
	{
		var range = TerrainWorldUnits.MetersToEngine( meters );
		var rangeSq = range * range;
		var here = WorldPosition;
		var anyPawn = false;
		foreach ( var vitals in Scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is null || !vitals.IsValid() )
				continue;
			anyPawn = true;
			if ( (vitals.WorldPosition - here).LengthSquared <= rangeSq )
				return true;
		}
		if ( anyPawn )
			return false;

		var cam = Scene.Camera;
		return cam is not null && cam.IsValid() && (cam.WorldPosition - here).LengthSquared <= rangeSq;
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
