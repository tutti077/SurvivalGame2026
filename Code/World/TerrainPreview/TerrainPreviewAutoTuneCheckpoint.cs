namespace Survival;

/// <summary>Saved knob state after phase-1 spawn land is secured; restored when interior water tuning fails.</summary>
public readonly struct TerrainPreviewAutoTuneCheckpoint
{
	public float ValleyFrequency { get; init; }
	public float ValleyWeight { get; init; }
	public float HillWeight { get; init; }
	public float ContinentalWeight { get; init; }
	public float InteriorWaterWeight { get; init; }

	public static TerrainPreviewAutoTuneCheckpoint Capture( TerrainPreviewSettings settings )
		=> new()
		{
			ValleyFrequency = settings.ValleyFrequency,
			ValleyWeight = settings.ValleyWeight,
			HillWeight = settings.HillWeight,
			ContinentalWeight = settings.ContinentalWeight,
			InteriorWaterWeight = settings.InteriorWaterWeight,
		};

	public void Restore( TerrainPreviewSettings settings )
	{
		settings.ValleyFrequency = ValleyFrequency;
		settings.ValleyWeight = ValleyWeight;
		settings.HillWeight = HillWeight;
		settings.ContinentalWeight = ContinentalWeight;
		settings.InteriorWaterWeight = InteriorWaterWeight;
	}
}
