using System.Text.Json.Serialization;

namespace Survival;

/// <summary>
/// How one species swims inside the fishing minigame meter, from the <c>fishMotion</c> block of a
/// <c>"fish": true</c> row in <c>data/resources.json</c>.
/// </summary>
/// <remarks>
/// The fish holds position, then darts. The chance of a dart starts at <see cref="BaseMovesPerSecond"/>
/// and grows exponentially by <see cref="Urgency"/> for every second it has stayed put, so a move is
/// always coming but is telegraphed by the pause before it — far more readable than per-frame jitter.
/// Between darts the fish coasts at <see cref="DriftSpeed"/> in <see cref="DriftDirection"/>, which is
/// what makes a bolt-and-sink personality (dart hard up, slide slowly back down) fall out of the same
/// four numbers. Every field has a playable default so a JSON row can set only what makes it distinct.
/// </remarks>
public sealed class FishMotionData
{
	/// <summary>Dart chance per second with the fish freshly settled.</summary>
	[JsonPropertyName( "baseMovesPerSecond" )]
	public float BaseMovesPerSecond { get; set; } = 0.5f;

	/// <summary>Exponential growth of the dart chance per second held still. 0 = flat random.</summary>
	[JsonPropertyName( "urgency" )]
	public float Urgency { get; set; } = 1.2f;

	/// <summary>Ceiling on the dart chance so a long hold can't turn into a stutter.</summary>
	[JsonPropertyName( "maxMovesPerSecond" )]
	public float MaxMovesPerSecond { get; set; } = 3f;

	/// <summary>Probability a dart goes up rather than down (0 = always down, 1 = always up).</summary>
	[JsonPropertyName( "upBias" )]
	public float UpBias { get; set; } = 0.5f;

	/// <summary>Up-bias for the opening dart only. Negative = reuse <see cref="UpBias"/>.</summary>
	[JsonPropertyName( "openingUpBias" )]
	public float OpeningUpBias { get; set; } = -1f;

	/// <summary>
	/// Distance of the opening dart, as a fraction of the meter. Negative = use the normal
	/// jump range. Lets a species make a guaranteed entrance (break the surface exactly once)
	/// instead of hoping the random roll reaches far enough.
	/// </summary>
	[JsonPropertyName( "openingJump" )]
	public float OpeningJump { get; set; } = -1f;

	/// <summary>Shortest dart, as a fraction of the meter.</summary>
	[JsonPropertyName( "jumpMin" )]
	public float JumpMin { get; set; } = 0.12f;

	/// <summary>Longest dart, as a fraction of the meter.</summary>
	[JsonPropertyName( "jumpMax" )]
	public float JumpMax { get; set; } = 0.32f;

	/// <summary>Travel speed while darting, in meter-fractions per second.</summary>
	[JsonPropertyName( "dartSpeed" )]
	public float DartSpeed { get; set; } = 1.1f;

	/// <summary>Coast speed between darts, in meter-fractions per second.</summary>
	[JsonPropertyName( "driftSpeed" )]
	public float DriftSpeed { get; set; }

	/// <summary>Coast direction between darts: +1 up, -1 down, 0 still.</summary>
	[JsonPropertyName( "driftDirection" )]
	public float DriftDirection { get; set; } = -1f;

	/// <summary>Lowest point on the meter this species will hold.</summary>
	[JsonPropertyName( "bandMin" )]
	public float BandMin { get; set; }

	/// <summary>Highest point on the meter this species will hold.</summary>
	[JsonPropertyName( "bandMax" )]
	public float BandMax { get; set; } = 1f;

	/// <summary>
	/// Once the fish has risen past <see cref="SettleTriggerHeight"/>, its ceiling drops to this for
	/// the rest of the fight. 1 = never settles. Drives "surfaces once, then stays deep".
	/// </summary>
	[JsonPropertyName( "settleBandMax" )]
	public float SettleBandMax { get; set; } = 1f;

	/// <summary>Height that arms <see cref="SettleBandMax"/>.</summary>
	[JsonPropertyName( "settleTriggerHeight" )]
	public float SettleTriggerHeight { get; set; } = 0.88f;

	/// <summary>
	/// Size of the constant idle sway layered on top of the dart/drift path, as a fraction of the
	/// meter. Without it a fish travels in dead-straight lines and reads as a sliding block.
	/// </summary>
	[JsonPropertyName( "wobbleAmplitude" )]
	public float WobbleAmplitude { get; set; } = 0.035f;

	/// <summary>How fast the idle sway oscillates. Higher = more nervous.</summary>
	[JsonPropertyName( "wobbleSpeed" )]
	public float WobbleSpeed { get; set; } = 1f;

	/// <summary>Where on the meter this species starts the fight.</summary>
	[JsonPropertyName( "startHeight" )]
	public float StartHeight { get; set; } = 0.4f;
}
