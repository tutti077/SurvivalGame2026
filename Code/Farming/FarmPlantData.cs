using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Survival;

/// <summary>Growth form of a sown plant. Order matters — stages advance in declaration order.</summary>
public enum FarmPlantStage
{
	FreshlySown = 0,
	Seedling = 1,
	Juvenile = 2,
	Matured = 3,
	/// <summary>Left unharvested too long (single-harvest plants only): the crop is gone, harvesting yields seeds.</summary>
	Bolting = 4,
}

/// <summary>How a plant behaves once matured.</summary>
public enum FarmPlantKind
{
	/// <summary>Radish / daisy: one harvest removes the plant. Can bolt.</summary>
	Single = 0,
	/// <summary>Blueberry / raspberry: harvest leaves the bush standing and it regrows. Never bolts.</summary>
	Bush = 1,
}

/// <summary>
/// The <c>"seed": { … }</c> block on a seed row in <c>data/resources.json</c>. A resource with this
/// block is sowable on tilled soil; the block describes the plant that grows from it. Seed rows
/// live in resources.json (same convention as fish) rather than in their own catalog file.
/// </summary>
public sealed class FarmPlantData
{
	public const int StageCount = 5;

	/// <summary>Name of the grown plant ("Radish"). Falls back to the seed row's display name.</summary>
	[JsonPropertyName( "plantName" )]
	public string PlantName { get; set; } = string.Empty;

	/// <summary><c>single</c> (one harvest, then gone) or <c>bush</c> (harvest repeatedly, regrows).</summary>
	[JsonPropertyName( "type" )]
	public string Type { get; set; } = "single";

	/// <summary>Seconds from sowing to <see cref="FarmPlantStage.Matured"/>. The three transitions sit at 1/3, 2/3 and 3/3.</summary>
	[JsonPropertyName( "matureSeconds" )]
	public float MatureSeconds { get; set; } = 10f;

	/// <summary>Item a matured harvest gives.</summary>
	[JsonPropertyName( "harvestResourceId" )]
	public string HarvestResourceId { get; set; } = string.Empty;

	[JsonPropertyName( "harvestAmountLow" )]
	public int HarvestAmountLow { get; set; } = 1;

	[JsonPropertyName( "harvestAmountHigh" )]
	public int HarvestAmountHigh { get; set; } = 1;

	/// <summary>Bush only: seconds after a harvest until the bush is matured again. 0 = a third of <see cref="MatureSeconds"/>.</summary>
	[JsonPropertyName( "regrowSeconds" )]
	public float RegrowSeconds { get; set; }

	/// <summary>Single only: seconds a plant may sit matured before it bolts. 0 = never bolts.</summary>
	[JsonPropertyName( "boltAfterSeconds" )]
	public float BoltAfterSeconds { get; set; }

	/// <summary>Seeds handed back when a bolted plant is harvested.</summary>
	[JsonPropertyName( "boltSeedAmount" )]
	public int BoltSeedAmount { get; set; } = 1;

	/// <summary>Tint for the placeholder plant visual and the sow ghost, "r,g,b,a".</summary>
	[JsonPropertyName( "color" )]
	public string Color { get; set; } = "0.35,0.65,0.25,1";

	/// <summary>Placeholder height of the matured plant, in meters.</summary>
	[JsonPropertyName( "matureHeightMeters" )]
	public float MatureHeightMeters { get; set; } = 0.3f;

	/// <summary>
	/// Optional model per stage (FreshlySown, Seedling, Juvenile, Matured, Bolting). Empty / missing
	/// entries render the built-in placeholder for that stage. Slot for the real low-poly plant models.
	/// </summary>
	[JsonPropertyName( "stageModels" )]
	public List<string> StageModels { get; set; } = new();

	public FarmPlantKind Kind =>
		string.Equals( Type, "bush", StringComparison.OrdinalIgnoreCase ) ? FarmPlantKind.Bush : FarmPlantKind.Single;

	public bool IsBush => Kind == FarmPlantKind.Bush;

	public bool CanBolt => !IsBush && BoltAfterSeconds > 0f;

	public float EffectiveRegrowSeconds => RegrowSeconds > 0f ? RegrowSeconds : Math.Max( 0.1f, MatureSeconds ) / 3f;

	public string GetStageModel( FarmPlantStage stage )
	{
		var index = (int)stage;
		if ( StageModels is null || index < 0 || index >= StageModels.Count )
			return string.Empty;

		return StageModels[index] ?? string.Empty;
	}

	/// <summary>Stage reached after <paramref name="elapsedSeconds"/> of growth since sowing (or since a bush regrow reset).</summary>
	public FarmPlantStage StageAt( double elapsedSeconds )
	{
		var total = Math.Max( 0.1f, MatureSeconds );
		if ( CanBolt && elapsedSeconds >= total + BoltAfterSeconds )
			return FarmPlantStage.Bolting;

		var fraction = elapsedSeconds / total;
		if ( fraction >= 1.0 )
			return FarmPlantStage.Matured;
		if ( fraction >= 2.0 / 3.0 )
			return FarmPlantStage.Juvenile;
		if ( fraction >= 1.0 / 3.0 )
			return FarmPlantStage.Seedling;
		return FarmPlantStage.FreshlySown;
	}
}
