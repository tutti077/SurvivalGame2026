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

	/// <summary>Predator only: while low-health fleeing, a threat closing inside this range → turn and fight. 0 = never (only a hit or a dead end turns it).</summary>
	[JsonPropertyName( "reengageRangeMeters" )]
	public float ReengageRangeMeters { get; set; } = 15f;

	/// <summary>Pause facing the stimulus before committing to track (harass / predator).</summary>
	[JsonPropertyName( "alertSeconds" )]
	public float AlertSeconds { get; set; } = 1f;

	/// <summary>Harass / predator: when the alert pause ends, rush straight in (Attacking) instead of stalking (badger).</summary>
	[JsonPropertyName( "chargeOnAlert" )]
	public bool ChargeOnAlert { get; set; }

	/// <summary>Predator: hit again while running from low health → turns round and fights to the death, never flees again (badger).</summary>
	[JsonPropertyName( "lastStand" )]
	public bool LastStand { get; set; }

	/// <summary>"none" | "small" | "large" — which foot traps hold this species (see <see cref="BearTrap"/>). Missing = small.</summary>
	[JsonPropertyName( "trapSize" )]
	public string TrapSize { get; set; } = "small";

	/// <summary>
	/// Prey panic run (deer): &gt; 0 = flee in legs this long, each leg turned up to
	/// <see cref="FleeZigzagTurnDegrees"/> from the last heading while still heading away from the threat,
	/// so an archer never gets a straight line. 0 = one straight leg per flee (the old behaviour).
	/// </summary>
	[JsonPropertyName( "fleeZigzagLegMeters" )]
	public float FleeZigzagLegMeters { get; set; }

	[JsonPropertyName( "fleeZigzagTurnDegrees" )]
	public float FleeZigzagTurnDegrees { get; set; } = 45f;

	/// <summary>"flyaway" birds: seconds of flight away from the threat before the bird is gone (its slot respawns later).</summary>
	[JsonPropertyName( "flyAwaySeconds" )]
	public float FlyAwaySeconds { get; set; } = 7f;

	/// <summary>"flyaway" birds: climb rate while flying off.</summary>
	[JsonPropertyName( "flyClimbMps" )]
	public float FlyClimbMps { get; set; } = 3f;

	/// <summary>"flyaway" birds: undisturbed, the bird leaves on its own after a random perch of min..max seconds (0 = never).</summary>
	[JsonPropertyName( "perchMinSeconds" )]
	public float PerchMinSeconds { get; set; }

	[JsonPropertyName( "perchMaxSeconds" )]
	public float PerchMaxSeconds { get; set; }

	/// <summary>Hand taming: the food a crouching player must hold (resource id). Empty = this species cannot be hand-fed.</summary>
	[JsonPropertyName( "tameFoodResourceId" )]
	public string TameFoodResourceId { get; set; } = string.Empty;

	/// <summary>Hand taming: the animal notices the offer inside this range.</summary>
	[JsonPropertyName( "tameOfferRangeMeters" )]
	public float TameOfferRangeMeters { get; set; } = 8f;

	/// <summary>Hand taming: it eats once this close to the player.</summary>
	[JsonPropertyName( "tameEatRangeMeters" )]
	public float TameEatRangeMeters { get; set; } = 1.4f;

	[JsonPropertyName( "tameEatSeconds" )]
	public float TameEatSeconds { get; set; } = 2f;

	/// <summary>What the body drops when it dies: every row rolls min..max of its resource (Mark 2026-11-02).</summary>
	[JsonPropertyName( "loot" )]
	public List<AnimalLootRow> Loot { get; set; }
}

/// <summary>One drop of an animal's <c>loot</c>: <see cref="Min"/>..<see cref="Max"/> (inclusive) of a resource.</summary>
public sealed class AnimalLootRow
{
	[JsonPropertyName( "resourceId" )]
	public string ResourceId { get; set; }

	[JsonPropertyName( "min" )]
	public int Min { get; set; } = 1;

	[JsonPropertyName( "max" )]
	public int Max { get; set; } = 1;
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
	/// <summary>Units; 0 = proximity never turns a low-health flee into a fight.</summary>
	public float ReengageRange { get; init; }
	public float AlertSeconds { get; init; }
	public bool ChargeOnAlert { get; init; }
	public bool LastStand { get; init; }
	public TrapSize TrapSize { get; init; }
	/// <summary>Units; 0 = straight flee legs.</summary>
	public float FleeZigzagLeg { get; init; }
	public float FleeZigzagTurnDegrees { get; init; }
	public float FlyAwaySeconds { get; init; }
	/// <summary>Units per second.</summary>
	public float FlyClimbSpeed { get; init; }
	/// <summary>Seconds; 0 = the bird only leaves when spooked.</summary>
	public float PerchMinSeconds { get; init; }
	public float PerchMaxSeconds { get; init; }
	/// <summary>Normalised resource id, empty = not hand-tameable.</summary>
	public string TameFood { get; init; }
	public float TameOfferRange { get; init; }
	public float TameEatRange { get; init; }
	public float TameEatSeconds { get; init; }
	/// <summary>Resolved death drops (normalised ids, min ≤ max); empty = drops nothing.</summary>
	public AnimalLootRow[] Loot { get; init; }

	public static AnimalBehaviorProfile FromData( AnimalBehaviorData data )
	{
		data ??= new AnimalBehaviorData();
		float M( float meters ) => TerrainWorldUnits.MetersToEngine( Math.Max( 0f, meters ) );

		var response = data.ThreatResponse?.Trim().ToLowerInvariant() switch
		{
			"harass" => AnimalThreatResponse.Harass,
			"predator" => AnimalThreatResponse.Predator,
			"flyaway" => AnimalThreatResponse.FlyAway,
			_ => AnimalThreatResponse.Flee
		};

		var trapSize = data.TrapSize?.Trim().ToLowerInvariant() switch
		{
			"none" => TrapSize.None,
			"large" => TrapSize.Large,
			_ => TrapSize.Small
		};

		return new AnimalBehaviorProfile
		{
			ThreatResponse = response,
			TrapSize = trapSize,
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
			ReengageRange = data.ReengageRangeMeters > 0f ? M( Math.Max( 1f, data.ReengageRangeMeters ) ) : 0f,
			AlertSeconds = Math.Max( 0f, data.AlertSeconds ),
			ChargeOnAlert = data.ChargeOnAlert,
			LastStand = data.LastStand,
			FleeZigzagLeg = data.FleeZigzagLegMeters > 0.5f ? M( data.FleeZigzagLegMeters ) : 0f,
			FleeZigzagTurnDegrees = Math.Clamp( data.FleeZigzagTurnDegrees, 5f, 90f ),
			FlyAwaySeconds = Math.Clamp( data.FlyAwaySeconds, 1f, 60f ),
			FlyClimbSpeed = M( Math.Max( 0.2f, data.FlyClimbMps ) ),
			PerchMinSeconds = Math.Max( 0f, data.PerchMinSeconds ),
			PerchMaxSeconds = Math.Max( Math.Max( 0f, data.PerchMinSeconds ), data.PerchMaxSeconds ),
			TameFood = string.IsNullOrWhiteSpace( data.TameFoodResourceId ) ? string.Empty : ResourceCatalog.NormalizeResourceId( data.TameFoodResourceId ),
			TameOfferRange = M( Math.Clamp( data.TameOfferRangeMeters, 1f, 40f ) ),
			TameEatRange = M( Math.Clamp( data.TameEatRangeMeters, 0.5f, 5f ) ),
			TameEatSeconds = Math.Clamp( data.TameEatSeconds, 0.5f, 20f ),
			Loot = ResolveLoot( data.Loot ),
		};
	}

	static AnimalLootRow[] ResolveLoot( List<AnimalLootRow> rows )
	{
		if ( rows is null || rows.Count == 0 )
			return [];

		var result = new List<AnimalLootRow>( rows.Count );
		foreach ( var row in rows )
		{
			if ( row is null || string.IsNullOrWhiteSpace( row.ResourceId ) )
				continue;
			var min = Math.Max( 0, row.Min );
			result.Add( new AnimalLootRow { ResourceId = ResourceCatalog.NormalizeResourceId( row.ResourceId ), Min = min, Max = Math.Max( min, row.Max ) } );
		}

		return result.ToArray();
	}

	public static AnimalBehaviorProfile CreateFallback() => FromData( new AnimalBehaviorData() );
}
