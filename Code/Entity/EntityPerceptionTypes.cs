using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Survival;

/// <summary>One row from <c>data/entity_perception.json</c>.</summary>
public sealed class EntityPerceptionData
{
	[JsonPropertyName( "sightRangeMeters" )]
	public float SightRangeMeters { get; set; } = 20f;

	[JsonPropertyName( "sightFovDegrees" )]
	public float SightFovDegrees { get; set; } = 150f;

	[JsonPropertyName( "eyeHeightMeters" )]
	public float EyeHeightMeters { get; set; } = 1.6f;

	[JsonPropertyName( "sightCloseMeters" )]
	public float SightCloseMeters { get; set; } = 5f;

	[JsonPropertyName( "sightFillPerSecondClose" )]
	public float SightFillPerSecondClose { get; set; } = 100f;

	[JsonPropertyName( "sightFillPerSecondFar" )]
	public float SightFillPerSecondFar { get; set; } = 35f;

	[JsonPropertyName( "idleMinSeconds" )]
	public float IdleMinSeconds { get; set; } = 1f;

	[JsonPropertyName( "idleMaxSeconds" )]
	public float IdleMaxSeconds { get; set; } = 3f;

	[JsonPropertyName( "wanderDistanceMeters" )]
	public float WanderDistanceMeters { get; set; } = 15f;

	[JsonPropertyName( "chaseCommitLosSeconds" )]
	public float ChaseCommitLosSeconds { get; set; } = 1f;

	[JsonPropertyName( "chaseLostLosSeconds" )]
	public float ChaseLostLosSeconds { get; set; } = 10f;

	[JsonPropertyName( "postLostIdleSeconds" )]
	public float PostLostIdleSeconds { get; set; } = 2f;

	[JsonPropertyName( "sprintIgnoreBeyondMeters" )]
	public float SprintIgnoreBeyondMeters { get; set; } = 14f;

	[JsonPropertyName( "sprintMidMeters" )]
	public float SprintMidMeters { get; set; } = 6f;

	[JsonPropertyName( "sprintContactMeters" )]
	public float SprintContactMeters { get; set; } = 2f;

	[JsonPropertyName( "sprintFillPerSecondFar" )]
	public float SprintFillPerSecondFar { get; set; } = 14f;

	[JsonPropertyName( "sprintFillPerSecondMid" )]
	public float SprintFillPerSecondMid { get; set; } = 28f;

	[JsonPropertyName( "sprintFillPerSecondNear" )]
	public float SprintFillPerSecondNear { get; set; } = 55f;

	[JsonPropertyName( "walkAlertRangeMeters" )]
	public float WalkAlertRangeMeters { get; set; } = 5f;

	[JsonPropertyName( "walkFillPerSecond" )]
	public float WalkFillPerSecond { get; set; } = 100f;

	[JsonPropertyName( "sneakAlertRangeMeters" )]
	public float SneakAlertRangeMeters { get; set; } = 1f;

	[JsonPropertyName( "sneakFillPerSecond" )]
	public float SneakFillPerSecond { get; set; } = 20f;

	[JsonPropertyName( "toolAlertRangeMeters" )]
	public float ToolAlertRangeMeters { get; set; } = 20f;

	[JsonPropertyName( "toolFillPerHit" )]
	public float ToolFillPerHit { get; set; } = 20f;

	[JsonPropertyName( "alertThreshold" )]
	public float AlertThreshold { get; set; } = 100f;

	[JsonPropertyName( "alertDecayPerSecond" )]
	public float AlertDecayPerSecond { get; set; } = 8f;

	[JsonPropertyName( "retreatHealthFraction" )]
	public float RetreatHealthFraction { get; set; } = 0.1f;

	[JsonPropertyName( "retreatDistanceMeters" )]
	public float RetreatDistanceMeters { get; set; } = 50f;
}

sealed class EntityPerceptionFile
{
	[JsonPropertyName( "entities" )]
	public Dictionary<string, EntityPerceptionData> Entities { get; set; }
}

/// <summary>Resolved perception (engine units + seconds).</summary>
public readonly struct EntityPerceptionProfile
{
	public float SightRange { get; init; }
	public float SightFovDegrees { get; init; }
	public float EyeHeight { get; init; }
	public float SightClose { get; init; }
	public float SightFillPerSecondClose { get; init; }
	public float SightFillPerSecondFar { get; init; }
	public float IdleMinSeconds { get; init; }
	public float IdleMaxSeconds { get; init; }
	public float WanderDistance { get; init; }
	public float ChaseCommitLosSeconds { get; init; }
	public float ChaseLostLosSeconds { get; init; }
	public float PostLostIdleSeconds { get; init; }
	public float SprintIgnoreBeyond { get; init; }
	public float SprintMid { get; init; }
	public float SprintContact { get; init; }
	public float SprintFillPerSecondFar { get; init; }
	public float SprintFillPerSecondMid { get; init; }
	public float SprintFillPerSecondNear { get; init; }
	public float WalkAlertRange { get; init; }
	public float WalkFillPerSecond { get; init; }
	public float SneakAlertRange { get; init; }
	public float SneakFillPerSecond { get; init; }
	public float ToolAlertRange { get; init; }
	public float ToolFillPerHit { get; init; }
	public float AlertThreshold { get; init; }
	public float AlertDecayPerSecond { get; init; }
	public float RetreatHealthFraction { get; init; }
	public float RetreatDistance { get; init; }

	public static EntityPerceptionProfile FromData( EntityPerceptionData data )
	{
		data ??= new EntityPerceptionData();
		float M( float meters ) => TerrainWorldUnits.MetersToEngine( Math.Max( 0f, meters ) );

		return new EntityPerceptionProfile
		{
			SightRange = M( Math.Max( 0.5f, data.SightRangeMeters ) ),
			SightFovDegrees = Math.Clamp( data.SightFovDegrees, 1f, 360f ),
			EyeHeight = M( Math.Max( 0.1f, data.EyeHeightMeters ) ),
			SightClose = M( Math.Max( 0.5f, data.SightCloseMeters ) ),
			SightFillPerSecondClose = Math.Max( 0f, data.SightFillPerSecondClose ),
			SightFillPerSecondFar = Math.Max( 0f, data.SightFillPerSecondFar ),
			IdleMinSeconds = Math.Max( 0.1f, data.IdleMinSeconds ),
			IdleMaxSeconds = Math.Max( data.IdleMinSeconds, data.IdleMaxSeconds ),
			WanderDistance = M( Math.Max( 4f, data.WanderDistanceMeters ) ),
			ChaseCommitLosSeconds = Math.Max( 0.1f, data.ChaseCommitLosSeconds ),
			ChaseLostLosSeconds = Math.Max( 1f, data.ChaseLostLosSeconds ),
			PostLostIdleSeconds = Math.Max( 0.1f, data.PostLostIdleSeconds ),
			SprintIgnoreBeyond = M( data.SprintIgnoreBeyondMeters ),
			SprintMid = M( data.SprintMidMeters ),
			SprintContact = M( data.SprintContactMeters ),
			SprintFillPerSecondFar = Math.Max( 0f, data.SprintFillPerSecondFar ),
			SprintFillPerSecondMid = Math.Max( 0f, data.SprintFillPerSecondMid ),
			SprintFillPerSecondNear = Math.Max( 0f, data.SprintFillPerSecondNear ),
			WalkAlertRange = M( data.WalkAlertRangeMeters ),
			WalkFillPerSecond = Math.Max( 0f, data.WalkFillPerSecond ),
			SneakAlertRange = M( Math.Max( 0f, data.SneakAlertRangeMeters ) ),
			SneakFillPerSecond = Math.Max( 0f, data.SneakFillPerSecond ),
			ToolAlertRange = M( data.ToolAlertRangeMeters ),
			ToolFillPerHit = Math.Max( 0f, data.ToolFillPerHit ),
			AlertThreshold = Math.Max( 1f, data.AlertThreshold ),
			AlertDecayPerSecond = Math.Max( 0f, data.AlertDecayPerSecond ),
			RetreatHealthFraction = Math.Clamp( data.RetreatHealthFraction, 0.01f, 0.5f ),
			RetreatDistance = M( Math.Max( 8f, data.RetreatDistanceMeters ) ),
		};
	}

	public static EntityPerceptionProfile CreateFallback() => FromData( new EntityPerceptionData() );
}
