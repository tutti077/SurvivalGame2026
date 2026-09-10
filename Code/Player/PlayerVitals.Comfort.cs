#nullable disable
using System;
using Sandbox;

namespace Survival;

/// <summary>Which shelter sources are currently feeding comfort (synced for the HUD tooltip).</summary>
[Flags]
public enum ComfortSourceFlags
{
	None = 0,
	/// <summary>A placed roof / upper floor is directly above the pawn.</summary>
	Roof = 1,
	/// <summary>A lit campfire is within its warmth range.</summary>
	Campfire = 2,
	/// <summary>Walls close the room (only counted under a roof).</summary>
	Enclosed = 4,
}

/// <summary>
/// Comfort → rested. Host probes the pawn's shelter on an interval (never per frame) and turns
/// comfort ticks into the <c>rested</c> status effect. Ticks add up:
/// <list type="bullet">
/// <item>+1 — under a player-built roof.</item>
/// <item>+1 — a lit campfire within its warmth range (with or without a roof).</item>
/// <item>+1 — enclosed by walls (every horizontal probe meets one), <b>only under a roof</b>. Walls
/// without a roof give nothing — you are still exposed to the elements.</item>
/// </list>
/// So: fire alone 1, roof + fire 2 (open-concept house), roof + walls 2, roof + walls + fire 3.
/// Every tick banks <see cref="RestedMinutesPerComfortTick"/> minutes. While comfort is active the
/// timer sits at full; when comfort drops (to any lower level) the banked time counts down from
/// where it was — 10 ticks → 4 ticks starts at 19:59, not 8:00 — and a lower level only holds the
/// timer once it has fallen to that level's value.
/// </summary>
public sealed partial class PlayerVitals
{
	[Property, Group( "Comfort" ), Title( "Comfort check interval (s)" ), Range( 0.25f, 5f )]
	public float ComfortCheckIntervalSeconds { get; set; } = 1f;

	[Property, Group( "Comfort" ), Title( "Rested minutes per comfort tick" ), Range( 0.5f, 10f )]
	public float RestedMinutesPerComfortTick { get; set; } = 2f;

	[Property, Group( "Comfort" ), Title( "Roof probe height (m)" ), Range( 2f, 30f )]
	public float ComfortRoofProbeMeters { get; set; } = 12f;

	[Property, Group( "Comfort" ), Title( "Wall probe range (m)" ), Range( 1f, 20f )]
	public float ComfortWallProbeMeters { get; set; } = 8f;

	[Property, Group( "Comfort" ), Title( "Wall probe height above feet (m)" ), Range( 0.2f, 2f )]
	public float ComfortWallProbeHeightMeters { get; set; } = 1.2f;

	/// <summary>Of the 8 horizontal probes, how many must meet a wall for the room to count as enclosed.</summary>
	[Property, Group( "Comfort" ), Title( "Wall hits for enclosure (of 8)" ), Range( 1, 8 )]
	public int ComfortWallHitsForEnclosure { get; set; } = ShelterProbe.EnclosureDirections;

	/// <summary>Host re-times the rested expiry only when the held value has drifted this far ahead (sync writes stay rare).</summary>
	[Property, Group( "Comfort" ), Title( "Rested top-up slack (s)" ), Range( 1f, 30f )]
	public float RestedTopUpSlackSeconds { get; set; } = 5f;

	[Property, Group( "Comfort" ), Title( "Log comfort" )]
	public bool LogComfort { get; set; }

	/// <summary>Current comfort ticks (host-probed, synced for the HUD).</summary>
	[Sync] public int ComfortLevel { get; private set; }

	/// <summary>What is feeding comfort right now (synced for the HUD tooltip).</summary>
	[Sync] public ComfortSourceFlags ComfortSources { get; private set; }

	double _nextComfortCheckAt;

	/// <summary>Seconds one comfort tick is worth.</summary>
	public float RestedSecondsPerComfortTick => Math.Max( 1f, RestedMinutesPerComfortTick * 60f );

	/// <summary>Rested time the current comfort level holds the timer at (0 when not comfortable). Valid on every machine.</summary>
	public float ComfortHeldRestedSeconds() => Math.Max( 0, ComfortLevel ) * RestedSecondsPerComfortTick;

	/// <summary>Host / offline: interval probe of the pawn's shelter → comfort level → rested expiry.</summary>
	void TickComfort()
	{
		if ( !IsHostOrOffline || !GameObject.IsValid() )
			return;

		if ( Time.NowDouble < _nextComfortCheckAt )
			return;

		_nextComfortCheckAt = Time.NowDouble + Math.Max( 0.25f, ComfortCheckIntervalSeconds );

		var level = ComputeComfortLevel( out var sources );
		if ( level != ComfortLevel || sources != ComfortSources )
		{
			if ( LogComfort )
				Log.Info( $"{VitalsLogPrefix()} {GameObject.Name}: comfort {ComfortLevel}→{level} ({sources})" );

			ComfortLevel = level;
			ComfortSources = sources;
			StatusEffectsChanged?.Invoke();
		}

		if ( level <= 0 )
			return;

		// Bank the best value; never shorten. Slack keeps this from rewriting the mirror every check.
		var held = Time.NowDouble + level * RestedSecondsPerComfortTick;
		var current = GetStatusEffectExpiry( StatusEffectCatalog.RestedId );
		if ( current is { } existing && held - existing < RestedTopUpSlackSeconds )
			return;

		HostSetStatusEffectExpiry( StatusEffectCatalog.RestedId, held, extendOnly: true );
	}

	double? GetStatusEffectExpiry( string id )
	{
		RefreshStatusEffectsFromSync();
		var index = IndexOfStatusEffect( id );
		return index >= 0 ? _statusEffects[index].ExpiresAt : null;
	}

	int ComputeComfortLevel( out ComfortSourceFlags sources )
	{
		sources = ComfortSourceFlags.None;
		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return 0;

		var feet = GameObject.WorldPosition;
		var probeOrigin = feet + Vector3.Up * TerrainWorldUnits.MetersToEngine( Math.Max( 0.2f, ComfortWallProbeHeightMeters ) );

		var roof = ShelterProbe.HasPlayerBuiltRoofAbove( scene, probeOrigin, ComfortRoofProbeMeters, GameObject, out _ );
		var campfire = Campfire.IsLitCampfireWithinWarmth( feet );

		if ( roof )
			sources |= ComfortSourceFlags.Roof;
		if ( campfire )
			sources |= ComfortSourceFlags.Campfire;

		var level = (roof ? 1 : 0) + (campfire ? 1 : 0);

		// Walls only count under a roof: walls alone leave you exposed, so skip the probes entirely.
		if ( !roof )
			return level;

		var wallHits = ShelterProbe.CountEnclosingWalls( scene, probeOrigin, ComfortWallProbeMeters, GameObject );
		var needed = Math.Clamp( ComfortWallHitsForEnclosure, 1, ShelterProbe.EnclosureDirections );
		if ( wallHits < needed )
			return level;

		sources |= ComfortSourceFlags.Enclosed;
		return level + 1;
	}
}
