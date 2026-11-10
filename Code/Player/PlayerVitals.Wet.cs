using System;
using Sandbox;

namespace Survival;

/// <summary>
/// The <c>wet</c> status (Mark 2026-11-02: torches "should not work under water, or while the player is
/// wet"). Host / offline: a few times a second the body is probed at chest height
/// (<see cref="WaterProbe"/>); in water the effect is set to run <see cref="WetDrySeconds"/> past now, so
/// it keeps ticking while swimming and dries off that long after climbing out. Held lights read it on
/// every peer through the synced status mirror (<see cref="HasStatusEffect"/>).
/// </summary>
public sealed partial class PlayerVitals
{
	public const string WetStatusId = "wet";

	[Property, Group( "Status Effects" ), Title( "Wet lasts after leaving water (s)" ), Range( 1f, 180f ), Step( 1f )]
	public float WetDrySeconds { get; set; } = 30f;

	const float WetProbeInterval = 0.25f;
	/// <summary>The expiry is pushed out again only once it has drifted this far below the full dry time (fewer sync rewrites).</summary>
	const float WetRefreshSlackSeconds = 3f;

	double _nextWetProbeAt;

	void TickWetStatus()
	{
		if ( !IsHostOrOffline || Time.NowDouble < _nextWetProbeAt )
			return;

		_nextWetProbeAt = Time.NowDouble + WetProbeInterval;
		if ( !IsBodyInWater() )
			return;

		var dry = Math.Max( 1f, WetDrySeconds );
		var target = Time.NowDouble + dry;
		if ( GetStatusEffectExpiry( WetStatusId ) is { } current && target - current < WetRefreshSlackSeconds )
			return;

		HostSetStatusEffectExpiry( WetStatusId, target, extendOnly: false );
	}

	/// <summary>Swimming per the controller, or chest height under a water surface / inside a water trigger.</summary>
	bool IsBodyInWater()
	{
		var controller = Components.Get<PlayerController>();
		if ( controller is { IsValid: true, IsSwimming: true } )
			return true;

		var bodyHeight = controller is { IsValid: true } ? MathF.Max( 24f, controller.BodyHeight ) : 72f;
		var chest = WorldPosition + Vector3.Up * (bodyHeight * 0.55f);
		return WaterProbe.IsPointUnderWater( Scene, chest );
	}
}
