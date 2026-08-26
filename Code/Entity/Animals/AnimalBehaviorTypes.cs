using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Survival;

/// <summary>One species row from <c>data/animal_behaviors.json</c>. Designer values are meters / seconds.</summary>
public sealed class AnimalBehaviorData
{
	/// <summary>"flee" | "harass" | "predator".</summary>
	[JsonPropertyName( "threatResponse" )]
	public string ThreatResponse { get; set; } = "flee";

	[JsonPropertyName( "maxHealth" )]
	public float MaxHealth { get; set; } = 40f;

	// --- Movement (meters / second) ---
	[JsonPropertyName( "walkSpeedMps" )]
	public float WalkSpeedMps { get; set; } = 1.2f;

	[JsonPropertyName( "runSpeedMps" )]
	public float RunSpeedMps { get; set; } = 6f;

	/// <summary>Tracking / stalking speed (harass + predator only).</summary>
	[JsonPropertyName( "sneakSpeedMps" )]
	public float SneakSpeedMps { get; set; } = 1.6f;

	// --- Ambient loop ---
	[JsonPropertyName( "idleMinSeconds" )]
	public float IdleMinSeconds { get; set; } = 1.5f;

	[JsonPropertyName( "idleMaxSeconds" )]
	public float IdleMaxSeconds { get; set; } = 4f;

	[JsonPropertyName( "grazeMinSeconds" )]
	public float GrazeMinSeconds { get; set; } = 5f;

	[JsonPropertyName( "grazeMaxSeconds" )]
	public float GrazeMaxSeconds { get; set; } = 12f;

	/// <summary>0–1 chance a finished wander leg ends in grazing instead of a short idle.</summary>
	[JsonPropertyName( "grazeChance" )]
	public float GrazeChance { get; set; } = 0.65f;

	[JsonPropertyName( "wanderRadiusMeters" )]
	public float WanderRadiusMeters { get; set; } = 15f;

	// --- Perception ---
	[JsonPropertyName( "sightRangeMeters" )]
	public float SightRangeMeters { get; set; } = 25f;

	[JsonPropertyName( "sightFovDegrees" )]
	public float SightFovDegrees { get; set; } = 220f;

	[JsonPropertyName( "eyeHeightMeters" )]
	public float EyeHeightMeters { get; set; } = 0.8f;

	/// <summary>Player noises (footsteps, running, chops, swings) within this range register as a threat.</summary>
	[JsonPropertyName( "hearRangeMeters" )]
	public float HearRangeMeters { get; set; } = 20f;

	/// <summary>Sneak footsteps only carry this fraction of <see cref="HearRangeMeters"/>.</summary>
	[JsonPropertyName( "sneakHearFraction" )]
	public float SneakHearFraction { get; set; } = 0.3f;

	// --- Fleeing ---
	/// <summary>Length of one flee leg away from the last-sensed threat position.</summary>
	[JsonPropertyName( "fleeDistanceMeters" )]
	public float FleeDistanceMeters { get; set; } = 60f;

	/// <summary>Threat farther than this while fleeing → calm down (prey: back to wander; fox: back to tracking).</summary>
	[JsonPropertyName( "calmRangeMeters" )]
	public float CalmRangeMeters { get; set; } = 45f;

	// --- Tracking / attacking (harass + predator) ---
	/// <summary>Start stalking only when the sensed threat is within this range — seeing/hearing one farther away is ignored.</summary>
	[JsonPropertyName( "trackStartRangeMeters" )]
	public float TrackStartRangeMeters { get; set; } = 30f;

	/// <summary>Keep tracking while the threat stays inside this range; beyond it the animal loses interest.</summary>
	[JsonPropertyName( "trackRangeMeters" )]
	public float TrackRangeMeters { get; set; } = 60f;

	/// <summary>Within this range the stalk turns into a full-speed rush — the attack is committed and nothing spooks it.</summary>
	[JsonPropertyName( "lungeRangeMeters" )]
	public float LungeRangeMeters { get; set; } = 12f;

	/// <summary>True: a player looking straight at this animal while it stalks (outside lunge range) scares it into fleeing.</summary>
	[JsonPropertyName( "spookedByStare" )]
	public bool SpookedByStare { get; set; }

	/// <summary>How long the player must hold the stare before the spook triggers.</summary>
	[JsonPropertyName( "stareSpookSeconds" )]
	public float StareSpookSeconds { get; set; } = 0.5f;

	/// <summary>Stare spook: length of the short retreat hop before it turns around and resumes tracking.</summary>
	[JsonPropertyName( "stareRetreatDistanceMeters" )]
	public float StareRetreatDistanceMeters { get; set; } = 10f;

	[JsonPropertyName( "attackRangeMeters" )]
	public float AttackRangeMeters { get; set; } = 1.8f;

	[JsonPropertyName( "attackDamage" )]
	public float AttackDamage { get; set; } = 8f;

	[JsonPropertyName( "attackWindupSeconds" )]
	public float AttackWindupSeconds { get; set; } = 0.35f;

	[JsonPropertyName( "attackCooldownSeconds" )]
	public float AttackCooldownSeconds { get; set; } = 1.2f;

	/// <summary>Harass only: bites landed before breaking off to flee (fox 1, coyote 2). 0 = no limit.</summary>
	[JsonPropertyName( "attacksBeforeFlee" )]
	public int AttacksBeforeFlee { get; set; }

	/// <summary>Predator only: flee once health fraction drops to this (lynx / wolf 0.25). 0 = never.</summary>
	[JsonPropertyName( "fleeHealthFraction" )]
	public float FleeHealthFraction { get; set; }

	/// <summary>Predator only: while low-health fleeing, a threat closing inside this range → turn and fight.</summary>
	[JsonPropertyName( "reengageRangeMeters" )]
	public float ReengageRangeMeters { get; set; } = 15f;

	/// <summary>Pause facing the stimulus before committing to track (harass / predator).</summary>
	[JsonPropertyName( "alertSeconds" )]
	public float AlertSeconds { get; set; } = 1f;
}

sealed class AnimalBehaviorFile
{
	[JsonPropertyName( "animals" )]
	public Dictionary<string, AnimalBehaviorData> Animals { get; set; }
}

/// <summary>Resolved behavior (engine units + seconds). Converted once from meters at load.</summary>
public readonly struct AnimalBehaviorProfile
{
	public AnimalThreatResponse ThreatResponse { get; init; }
	public float MaxHealth { get; init; }
	public float WalkSpeed { get; init; }
	public float RunSpeed { get; init; }
	public float SneakSpeed { get; init; }
	public float IdleMinSeconds { get; init; }
	public float IdleMaxSeconds { get; init; }
	public float GrazeMinSeconds { get; init; }
	public float GrazeMaxSeconds { get; init; }
	public float GrazeChance { get; init; }
	public float WanderRadius { get; init; }
	public float SightRange { get; init; }
	public float SightFovDegrees { get; init; }
	public float EyeHeight { get; init; }
	public float HearRange { get; init; }
	public float SneakHearFraction { get; init; }
	public float FleeDistance { get; init; }
	public float CalmRange { get; init; }
	public float TrackStartRange { get; init; }
	public float TrackRange { get; init; }
	public float LungeRange { get; init; }
	public bool SpookedByStare { get; init; }
	public float StareSpookSeconds { get; init; }
	public float StareRetreatDistance { get; init; }
	public float AttackRange { get; init; }
	public float AttackDamage { get; init; }
	public float AttackWindupSeconds { get; init; }
	public float AttackCooldownSeconds { get; init; }
	public int AttacksBeforeFlee { get; init; }
	public float FleeHealthFraction { get; init; }
	public float ReengageRange { get; init; }
	public float AlertSeconds { get; init; }

	public static AnimalBehaviorProfile FromData( AnimalBehaviorData data )
	{
		data ??= new AnimalBehaviorData();
		float M( float meters ) => TerrainWorldUnits.MetersToEngine( Math.Max( 0f, meters ) );

		var response = data.ThreatResponse?.Trim().ToLowerInvariant() switch
		{
			"harass" => AnimalThreatResponse.Harass,
			"predator" => AnimalThreatResponse.Predator,
			_ => AnimalThreatResponse.Flee
		};

		return new AnimalBehaviorProfile
		{
			ThreatResponse = response,
			MaxHealth = Math.Max( 1f, data.MaxHealth ),
			WalkSpeed = M( Math.Max( 0.2f, data.WalkSpeedMps ) ),
			RunSpeed = M( Math.Max( 0.5f, data.RunSpeedMps ) ),
			SneakSpeed = M( Math.Max( 0.2f, data.SneakSpeedMps ) ),
			IdleMinSeconds = Math.Max( 0.1f, data.IdleMinSeconds ),
			IdleMaxSeconds = Math.Max( data.IdleMinSeconds, data.IdleMaxSeconds ),
			GrazeMinSeconds = Math.Max( 0.5f, data.GrazeMinSeconds ),
			GrazeMaxSeconds = Math.Max( data.GrazeMinSeconds, data.GrazeMaxSeconds ),
			GrazeChance = Math.Clamp( data.GrazeChance, 0f, 1f ),
			WanderRadius = M( Math.Max( 4f, data.WanderRadiusMeters ) ),
			SightRange = M( Math.Max( 1f, data.SightRangeMeters ) ),
			SightFovDegrees = Math.Clamp( data.SightFovDegrees, 1f, 360f ),
			EyeHeight = M( Math.Max( 0.1f, data.EyeHeightMeters ) ),
			HearRange = M( Math.Max( 0f, data.HearRangeMeters ) ),
			SneakHearFraction = Math.Clamp( data.SneakHearFraction, 0f, 1f ),
			FleeDistance = M( Math.Max( 5f, data.FleeDistanceMeters ) ),
			CalmRange = M( Math.Max( 5f, data.CalmRangeMeters ) ),
			TrackStartRange = M( Math.Max( 2f, data.TrackStartRangeMeters ) ),
			TrackRange = M( Math.Max( 5f, data.TrackRangeMeters ) ),
			LungeRange = M( Math.Max( 1f, data.LungeRangeMeters ) ),
			SpookedByStare = data.SpookedByStare,
			StareSpookSeconds = Math.Max( 0.05f, data.StareSpookSeconds ),
			StareRetreatDistance = M( Math.Max( 2f, data.StareRetreatDistanceMeters ) ),
			AttackRange = M( Math.Max( 0.5f, data.AttackRangeMeters ) ),
			AttackDamage = Math.Max( 0f, data.AttackDamage ),
			AttackWindupSeconds = Math.Max( 0.05f, data.AttackWindupSeconds ),
			AttackCooldownSeconds = Math.Max( 0.2f, data.AttackCooldownSeconds ),
			AttacksBeforeFlee = Math.Max( 0, data.AttacksBeforeFlee ),
			FleeHealthFraction = Math.Clamp( data.FleeHealthFraction, 0f, 0.9f ),
			ReengageRange = M( Math.Max( 1f, data.ReengageRangeMeters ) ),
			AlertSeconds = Math.Max( 0f, data.AlertSeconds ),
		};
	}

	public static AnimalBehaviorProfile CreateFallback() => FromData( new AnimalBehaviorData() );
}
