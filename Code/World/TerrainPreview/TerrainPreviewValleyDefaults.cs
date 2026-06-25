namespace Survival;

/// <summary>Designer baseline layer knobs — auto tuning resets here each generate.</summary>
public static class TerrainPreviewValleyDefaults
{
	public const float Frequency = 20f;
	public const float Weight = 0.2f;
	public const float HillWeight = 0.25f;
	public const float ContinentalWeight = 0.55f;
	public const float InteriorWaterWeight = 0f;

	public static void ResetAutoBaselines( TerrainPreviewSettings settings )
	{
		settings.ValleyFrequency = Frequency;
		settings.ValleyWeight = Weight;
		settings.HillWeight = HillWeight;
		settings.ContinentalWeight = ContinentalWeight;
		settings.InteriorWaterWeight = InteriorWaterWeight;
	}
}
