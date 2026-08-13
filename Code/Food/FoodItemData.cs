using System.Text.Json.Serialization;

namespace Survival;

/// <summary>One food / raw-mat row from <c>data/food_items.json</c>.</summary>
public sealed class FoodItemData
{
	[JsonPropertyName( "resourceId" )]
	public string ResourceId { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	/// <summary>When false, item is a raw cooking ingredient only (meats / mushroom).</summary>
	public bool Edible { get; set; }

	public float MaxHealth { get; set; }
	public float MaxStamina { get; set; }
	public float HealthRegenPerSecond { get; set; }
	public float DurationSeconds { get; set; }
	public float RestoreHealth { get; set; }
	public float RestoreStamina { get; set; }
	public string FallbackColor { get; set; } = "0.7,0.7,0.7,1";
}
